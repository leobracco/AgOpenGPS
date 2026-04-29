// ============================================================================
// FormQuantiXWidget.cs - Widget flotante de dosis en tiempo real
// Ubicación: SourceCode/GPS/AgroParallel/QuantiX/FormQuantiXWidget.cs
// Target: net48 (C# 7.3)
//
// Muestra objetivo vs real por motor, con opción de pasar a manual y
// ajustar PWM con +/- . Se suscribe al status MQTT del nodo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXWidget : Form
    {
        private readonly QuantiXConfig _cfg;
        private readonly AgOpenGPS.FormGPS _parent;
        private MotoresConfig _motores;
        private MqttClientWrapper _mqtt;
        private Panel _canvas;
        private Timer _refreshTimer;

        // Estado live por motor: [nodoIdx][motorIdx]
        private class MotorLive
        {
            public double PpsTarget;
            public double PpsReal;
            public double Rpm;
            public int Pwm;
            public bool Enabled;
            public bool Manual;
            public int ManualPwm;
            public double ManualDosis; // Dosis manual en unidades del usuario
            public double DosisObjetivo;
            public double DosisReal;
            public string Unidad;
        }

        private readonly Dictionary<string, MotorLive[]> _live =
            new Dictionary<string, MotorLive[]>(StringComparer.OrdinalIgnoreCase);

        public FormQuantiXWidget(QuantiXConfig cfg, AgOpenGPS.FormGPS parent)
        {
            _cfg = cfg ?? QuantiXConfig.Load();
            _parent = parent;
            _motores = MotoresConfig.Load();
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            InitLiveState();
            StartMqtt();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            // Enviar stop manual a todos los motores que estén en manual.
            StopAllManual();
            if (_refreshTimer != null) { _refreshTimer.Stop(); _refreshTimer.Dispose(); }
            if (_mqtt != null)
            {
                var m = _mqtt; _mqtt = null;
                System.Threading.Tasks.Task.Run(() => { try { m.Dispose(); } catch { } });
            }
        }

        // =====================================================================
        // UI
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "QuantiX — Dosificaci\u00F3n";
            Size = new Size(420, 500);
            MinimumSize = new Size(380, 300);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = true;
            Theme.ApplyToForm(this);

            Paint += (s, ev) =>
                Theme.DrawRoundedBorder(ev.Graphics,
                    new Rectangle(0, 0, Width - 1, Height - 1), Theme.Border, 10);

            // Header draggable.
            var header = new Panel
            {
                Dock = DockStyle.Top, Height = 36,
                BackColor = Theme.BgHeader, Cursor = Cursors.SizeAll
            };
            header.Paint += (s, ev) =>
            {
                var g = ev.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var f = new Font(Theme.FontFamily, 10f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.Accent))
                    g.DrawString("\U0001F4CA  QuantiX \u2014 Dosificaci\u00F3n", f, br, 10, 9);
                using (var pen = new Pen(Theme.Border))
                    g.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            header.MouseDown += (s, ev) => { if (ev.Button == MouseButtons.Left) { _dragging = true; _dragStart = ev.Location; } };
            header.MouseMove += (s, ev) => { if (_dragging) Location = new Point(Location.X + ev.X - _dragStart.X, Location.Y + ev.Y - _dragStart.Y); };
            header.MouseUp += (s, ev) => _dragging = false;

            var btnClose = Theme.MkToolbarButton("\u2715", 30);
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 30, 30);
            btnClose.Click += (s, ev) => Close();
            header.Controls.Add(btnClose);
            header.Resize += (s, ev) => btnClose.Location = new Point(header.Width - 34, 3);
            Controls.Add(header);

            // Canvas.
            _canvas = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBlack };
            _canvas.Paint += PaintWidget;
            Controls.Add(_canvas);
            _canvas.BringToFront();
        }

        // =====================================================================
        // Live state
        // =====================================================================

        private void InitLiveState()
        {
            _live.Clear();
            foreach (var nodo in _motores.Nodos)
            {
                if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;
                var arr = new MotorLive[nodo.Motores.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    arr[i] = new MotorLive
                    {
                        Unidad = string.IsNullOrEmpty(nodo.Motores[i].CampoDosis) ? "kg/ha" : nodo.Motores[i].CampoDosis,
                        ManualPwm = nodo.Motores[i].PwmMin > 0 ? nodo.Motores[i].PwmMin : 500
                    };
                }
                _live[nodo.Uid] = arr;
            }
        }

        // =====================================================================
        // MQTT
        // =====================================================================

        private void StartMqtt()
        {
            try
            {
                var vistaXCfg = VistaXConfig.Load();
                var mqttCfg = new VistaXConfig
                {
                    Enabled = true,
                    BrokerAddress = vistaXCfg.BrokerAddress,
                    BrokerPort = vistaXCfg.BrokerPort,
                    ClientId = "QX_Widget",
                    // Suscribirse a todos los status.
                    TelemetriaTopic = "agp/quantix/+/status_live",
                    SectionsTopic = "",
                    SpeedTopic = ""
                };
                _mqtt = new MqttClientWrapper(mqttCfg);
                _mqtt.MessageReceived += OnMqttMessage;
                _ = _mqtt.ConnectAsync();

                _refreshTimer = new Timer { Interval = 250 };
                _refreshTimer.Tick += (s, ev) => _canvas.Invalidate();
                _refreshTimer.Start();
            }
            catch { }
        }

        private void OnMqttMessage(string topic, string payload)
        {
            if (!topic.Contains("status_live")) return;

            try
            {
                // Extraer UID del topic: agp/quantix/{UID}/status_live
                string[] parts = topic.Split('/');
                if (parts.Length < 4) return;
                string uid = parts[2];

                MotorLive[] arr;
                if (!_live.TryGetValue(uid, out arr)) return;

                int id = (int)ExtractNum(payload, "\"id\":");
                if (id < 0 || id >= arr.Length) return;

                arr[id].PpsReal = ExtractNum(payload, "\"pps_real\":");
                arr[id].PpsTarget = ExtractNum(payload, "\"pps_target\":");
                arr[id].Rpm = ExtractNum(payload, "\"rpm\":");
                arr[id].Pwm = (int)ExtractNum(payload, "\"pwm\":");
                arr[id].Enabled = payload.Contains("\"calibrando\":true") || arr[id].PpsTarget > 0;

                // Calcular dosis real desde PPS real.
                // Buscar el nodo para obtener MeterCal.
                foreach (var nodo in _motores.Nodos)
                {
                    if (!string.Equals(nodo.Uid, uid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (id < nodo.Motores.Length && nodo.Motores[id].MeterCal > 0)
                    {
                        double meterCal = nodo.Motores[id].MeterCal;
                        double vel = _parent != null ? _parent.avgSpeed : 0;
                        double velMs = vel / 3.6;
                        double ancho = _parent != null && _parent.tool != null ? _parent.tool.width : 1;

                        // Inversa de la fórmula del bridge:
                        // pps = (dosis * ancho * velMs) / 10000 * meterCal
                        // dosis = pps * 10000 / (ancho * velMs * meterCal)
                        if (velMs > 0.1 && ancho > 0)
                        {
                            arr[id].DosisObjetivo = arr[id].PpsTarget * 10000.0 / (ancho * velMs * meterCal);
                            arr[id].DosisReal = arr[id].PpsReal * 10000.0 / (ancho * velMs * meterCal);
                        }
                    }
                    break;
                }
            }
            catch { }
        }

        // =====================================================================
        // Paint
        // =====================================================================

        private void PaintWidget(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.BgBlack);

            int w = _canvas.Width;
            int y = 10;

            foreach (var kv in _live)
            {
                string uid = kv.Key;
                string uid4 = uid.Length > 6 ? uid.Substring(uid.Length - 6) : uid;

                // Nodo header.
                using (var f = Theme.FontSmall)
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString(uid, f, br, 10, y);
                y += 16;

                for (int mi = 0; mi < kv.Value.Length; mi++)
                {
                    var m = kv.Value[mi];
                    int cardY = y;
                    int cardH = 90;
                    var cardRect = new Rectangle(8, cardY, w - 16, cardH);
                    Theme.FillRoundedRect(g, cardRect, Theme.BgCard, 6);
                    Theme.DrawRoundedBorder(g, cardRect, Theme.Border, 6);

                    // Motor label + manual badge.
                    Color mColor = mi == 0 ? Theme.Accent : Color.FromArgb(230, 160, 30);
                    string mLabel = "M" + mi;
                    using (var f = new Font(Theme.FontFamily, 10f, FontStyle.Bold))
                    using (var br = new SolidBrush(mColor))
                        g.DrawString(mLabel, f, br, 16, cardY + 6);

                    if (m.Manual)
                    {
                        using (var f = Theme.FontSmall)
                        using (var br = new SolidBrush(Theme.Warning))
                            g.DrawString("MANUAL", f, br, 50, cardY + 9);
                    }

                    // Objetivo.
                    using (var f = Theme.FontSmall)
                    using (var br = new SolidBrush(Theme.TextFaint))
                        g.DrawString("OBJETIVO", f, br, 16, cardY + 24);
                    using (var f = new Font(Theme.FontFamily, 16f, FontStyle.Bold))
                    using (var br = new SolidBrush(Theme.TextPrimary))
                        g.DrawString(m.DosisObjetivo.ToString("F1", CultureInfo.InvariantCulture), f, br, 16, cardY + 36);

                    // Real.
                    using (var f = Theme.FontSmall)
                    using (var br = new SolidBrush(Theme.TextFaint))
                        g.DrawString("REAL", f, br, 120, cardY + 24);

                    Color realColor = Theme.Accent;
                    if (m.DosisObjetivo > 0)
                    {
                        double pct = Math.Abs(m.DosisReal - m.DosisObjetivo) / m.DosisObjetivo * 100;
                        if (pct > 20) realColor = Theme.Error;
                        else if (pct > 10) realColor = Theme.Warning;
                    }
                    using (var f = new Font(Theme.FontFamily, 16f, FontStyle.Bold))
                    using (var br = new SolidBrush(realColor))
                        g.DrawString(m.DosisReal.ToString("F1", CultureInfo.InvariantCulture), f, br, 120, cardY + 36);

                    // Unidad.
                    using (var f = Theme.FontSmall)
                    using (var br = new SolidBrush(Theme.TextFaint))
                        g.DrawString(m.Unidad, f, br, 210, cardY + 46);

                    // PPS + PWM info.
                    using (var f = Theme.FontSmall)
                    using (var br = new SolidBrush(Theme.TextFaint))
                    {
                        string info = "PPS: " + m.PpsReal.ToString("F1", CultureInfo.InvariantCulture)
                            + "/" + m.PpsTarget.ToString("F1", CultureInfo.InvariantCulture)
                            + "  PWM: " + m.Pwm
                            + "  RPM: " + m.Rpm.ToString("F0", CultureInfo.InvariantCulture);
                        g.DrawString(info, f, br, 16, cardY + 66);
                    }

                    // Barra de progreso objetivo vs real.
                    int barX = 280, barW = w - barX - 24, barY2 = cardY + 28, barH = 36;
                    using (var b = new SolidBrush(Color.FromArgb(20, 20, 24)))
                        g.FillRectangle(b, barX, barY2, barW, barH);

                    if (m.DosisObjetivo > 0)
                    {
                        double pct = Math.Min(1.0, m.DosisReal / m.DosisObjetivo);
                        int fillW = (int)(barW * pct);
                        using (var b = new SolidBrush(Color.FromArgb(60, realColor.R, realColor.G, realColor.B)))
                            g.FillRectangle(b, barX, barY2, fillW, barH);
                    }

                    using (var f = new Font(Theme.FontFamily, 9f, FontStyle.Bold))
                    using (var br = new SolidBrush(Theme.TextPrimary))
                    {
                        string pctStr = m.DosisObjetivo > 0
                            ? ((int)(m.DosisReal / m.DosisObjetivo * 100)) + "%"
                            : "--";
                        var sz = g.MeasureString(pctStr, f);
                        g.DrawString(pctStr, f, br, barX + (barW - sz.Width) / 2, barY2 + (barH - sz.Height) / 2);
                    }

                    y += cardH + 6;
                }

                y += 4;
            }

            // Botones Manual / Auto.
            // Se dibujan como controles WinForms, no como paint. Se crean en BuildButtons.
        }

        // =====================================================================
        // Botones Manual +/- (se crean una vez sobre el canvas)
        // =====================================================================

        private bool _buttonsCreated;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!_buttonsCreated && _canvas != null) CreateButtons();
        }

        private NumericUpDown _numDosis;
        private Label _lblDosisUnit;

        private void CreateButtons()
        {
            _buttonsCreated = true;

            int bx = 10, by = _canvas.Height - 44;

            var btnManual = Theme.MkButton("\U0001F91A MANUAL", Color.FromArgb(50, 40, 10), Theme.Warning, 100, 34);
            btnManual.Location = new Point(bx, by);
            btnManual.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnManual.Click += (s, ev) => ToggleManual();
            _canvas.Controls.Add(btnManual);

            // Dosis manual con incremento fino.
            _numDosis = new NumericUpDown
            {
                Minimum = 0, Maximum = 9999, DecimalPlaces = 1, Increment = 0.1m,
                Value = 0,
                Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Theme.BgInput,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(bx + 110, by + 2),
                Size = new Size(90, 30),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            _numDosis.ValueChanged += (s, ev) => OnDosisChanged();
            _canvas.Controls.Add(_numDosis);

            _lblDosisUnit = new Label
            {
                Text = "",
                Font = Theme.FontSmall, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Location = new Point(bx + 205, by + 10),
                AutoSize = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            _canvas.Controls.Add(_lblDosisUnit);

            var btnAuto = Theme.MkButton("\u25B6 AUTO", Theme.AccentDim, Theme.TextPrimary, 80, 34);
            btnAuto.Location = new Point(bx + 280, by);
            btnAuto.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnAuto.Click += (s, ev) => SetAuto();
            _canvas.Controls.Add(btnAuto);
        }

        // =====================================================================
        // Manual / Auto control
        // =====================================================================

        private void ToggleManual()
        {
            foreach (var kv in _live)
            {
                for (int mi = 0; mi < kv.Value.Length; mi++)
                {
                    var m = kv.Value[mi];
                    m.Manual = !m.Manual;
                    if (m.Manual)
                    {
                        // Inicializar con la dosis actual.
                        m.ManualDosis = m.DosisObjetivo > 0 ? m.DosisObjetivo : m.DosisReal;
                        if (_numDosis != null)
                        {
                            _numDosis.Value = (decimal)Math.Max(0, Math.Min(9999, m.ManualDosis));
                            // Ajustar increment según unidad.
                            bool isKg = m.Unidad != null &&
                                (m.Unidad.Contains("kg") || m.Unidad.Contains("l"));
                            _numDosis.Increment = isKg ? 1m : 0.1m;
                            _numDosis.DecimalPlaces = isKg ? 0 : 1;
                            if (_lblDosisUnit != null)
                                _lblDosisUnit.Text = m.Unidad ?? "";
                        }
                        SendManualTarget(kv.Key, mi, m.ManualDosis);
                    }
                    else
                    {
                        SendStop(kv.Key, mi);
                    }
                }
                break;
            }
        }

        private void OnDosisChanged()
        {
            if (_numDosis == null) return;
            double dosis = (double)_numDosis.Value;
            foreach (var kv in _live)
            {
                for (int mi = 0; mi < kv.Value.Length; mi++)
                {
                    if (!kv.Value[mi].Manual) continue;
                    kv.Value[mi].ManualDosis = dosis;
                    SendManualTarget(kv.Key, mi, dosis);
                }
                break;
            }
        }

        private void SetAuto()
        {
            foreach (var kv in _live)
            {
                for (int mi = 0; mi < kv.Value.Length; mi++)
                {
                    kv.Value[mi].Manual = false;
                    SendStop(kv.Key, mi);
                }
                break;
            }
        }

        private void SendManualTarget(string uid, int motorId, double dosis)
        {
            // Convertir dosis a PPS usando MeterCal del motor.
            double pps = 0;
            if (_motores != null)
            {
                foreach (var n in _motores.Nodos)
                {
                    if (!string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase)) continue;
                    var motors = n.Motores;
                    if (motorId < motors.Length)
                    {
                        double meterCal = motors[motorId].MeterCal;
                        if (meterCal > 0)
                            pps = dosis / meterCal; // dosis en unidad/ha → pps
                    }
                    break;
                }
            }
            // Si no hay MeterCal, usar dosis como PPS directo.
            if (pps <= 0) pps = dosis;

            SendTarget(uid, motorId, pps);
        }

        private void SendTarget(string uid, int motorId, double pps)
        {
            if (_mqtt == null) return;
            string topic = "agp/quantix/" + uid + "/target";
            string payload = "{\"id\":" + motorId
                + ",\"pps\":" + Math.Round(pps, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"seccion_on\":true}";
            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic).WithPayload(payload).Build();
                _ = _mqtt.PublishAsync(msg);
            }
            catch { }
        }

        private void SendTest(string uid, int motorId, int pwm)
        {
            if (_mqtt == null) return;
            string topic = "agp/quantix/" + uid + "/test";
            string payload = "{\"cmd\":\"start\",\"id\":" + motorId + ",\"pwm\":" + pwm + "}";
            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic).WithPayload(payload).Build();
                _ = _mqtt.PublishAsync(msg);
            }
            catch { }
        }

        private void SendStop(string uid, int motorId)
        {
            if (_mqtt == null) return;
            string topic = "agp/quantix/" + uid + "/test";
            string payload = "{\"cmd\":\"stop\",\"id\":" + motorId + "}";
            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic).WithPayload(payload).Build();
                _ = _mqtt.PublishAsync(msg);
            }
            catch { }
        }

        private void StopAllManual()
        {
            foreach (var kv in _live)
            {
                for (int mi = 0; mi < kv.Value.Length; mi++)
                {
                    if (kv.Value[mi].Manual) SendStop(kv.Key, mi);
                }
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static double ExtractNum(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += key.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-'))
                end++;
            if (end == idx) return 0;
            double val;
            double.TryParse(json.Substring(idx, end - idx), NumberStyles.Any, CultureInfo.InvariantCulture, out val);
            return val;
        }
    }
}
