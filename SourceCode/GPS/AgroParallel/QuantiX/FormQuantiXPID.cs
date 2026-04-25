// ============================================================================
// FormQuantiXPID.cs - Tuning PID en vivo con gráfico de respuesta
// ============================================================================
//
// Widget para ajuste fino del PID de cada motor. Muestra:
// - Sliders de Kp, Ki, MinPWM, SlewRate, Deadband
// - Gráfico en vivo: Target vs Real (últimos 10 segundos)
// - Envío en caliente por MQTT al ESP32 (sin reiniciar)
// - Presets para arrancar rápido (Suave, Normal, Agresivo)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXPID : Form
    {
        private readonly QuantiXConfig _cfg;
        private MotoresConfig _motores;
        private MqttClientWrapper _mqtt;

        // Controles.
        private ComboBox _cboNodo, _cboMotor;
        private TrackBar _trkKp, _trkKi, _trkKd, _trkMinPwm, _trkSlew, _trkDeadband;
        private Label _lblKp, _lblKi, _lblKd, _lblMinPwm, _lblSlew, _lblDeadband;
        private Panel _graphPanel;
        private Timer _refreshTimer;

        // Historial para el gráfico (últimos 100 puntos = 10 seg a 10Hz).
        private readonly List<double> _histTarget = new List<double>();
        private readonly List<double> _histReal = new List<double>();
        private const int MaxHistory = 100;

        // PPS live del status MQTT.
        private double _liveTarget, _liveReal;
        private int _livePwm, _liveRpm;
        private long _livePulsos;

        // CSV log.
        private string _pidLogPath;
        private DateTime _pidLogStart;

        public FormQuantiXPID(QuantiXConfig cfg)
        {
            _cfg = cfg ?? QuantiXConfig.Load();
            _motores = MotoresConfig.Load();
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadMotorValues();
            StartMqtt();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
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

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;
            Size = new Size(580, 560);

            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.BgBlack };
            Controls.Add(body);

            int lx = 16, y = 12;

            // Selector nodo + motor.
            body.Controls.Add(MkLabel("Nodo:", lx, y));
            _cboNodo = MkCombo(lx + 40, y, 200);
            foreach (var n in _motores.Nodos) _cboNodo.Items.Add(n.Nombre + " (" + n.Uid + ")");
            if (_cboNodo.Items.Count > 0) _cboNodo.SelectedIndex = 0;
            _cboNodo.SelectedIndexChanged += (s, ev) => LoadMotorValues();
            body.Controls.Add(_cboNodo);

            body.Controls.Add(MkLabel("Motor:", lx + 260, y));
            _cboMotor = MkCombo(lx + 310, y, 120);
            _cboMotor.Items.AddRange(new object[] { "M0", "M1" });
            _cboMotor.SelectedIndex = 0;
            _cboMotor.SelectedIndexChanged += (s, ev) => LoadMotorValues();
            body.Controls.Add(_cboMotor);
            y += 30;

            // Presets.
            body.Controls.Add(MkLabel("Presets:", lx, y));
            var btnElectrico = Theme.MkButton("El\u00E9ctrico", Color.FromArgb(30, 50, 30), Theme.Accent, 80, 22);
            btnElectrico.Location = new Point(lx + 60, y - 2);
            btnElectrico.Click += (s, ev) => ApplyPreset(30, 5.0, 0, 180, 30, 3);
            body.Controls.Add(btnElectrico);

            var btnHidraulico = Theme.MkButton("Hidr\u00E1ulico", Color.FromArgb(40, 40, 20), Theme.Warning, 80, 22);
            btnHidraulico.Location = new Point(lx + 146, y - 2);
            btnHidraulico.Click += (s, ev) => ApplyPreset(20, 3.0, 0, 320, 20, 3);
            body.Controls.Add(btnHidraulico);

            var btnAgresivo = Theme.MkButton("Agresivo", Color.FromArgb(50, 25, 25), Theme.Error, 80, 22);
            btnAgresivo.Location = new Point(lx + 232, y - 2);
            btnAgresivo.Click += (s, ev) => ApplyPreset(50, 8.0, 0, 150, 50, 2);
            body.Controls.Add(btnAgresivo);
            y += 28;

            // Guía.
            body.Controls.Add(new Label
            {
                Text = "Kp = fuerza de reacci\u00F3n | Ki = corrige error acumulado | Kd = freno/suavizado",
                Font = new Font("Segoe UI", 7f), ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent, Location = new Point(lx, y), AutoSize = true
            });
            y += 14;

            // Sliders.
            // Kp: 0-200 directo. Ki: 0-50 directo. Kd: 0-50 directo.
            // Sin escala — estos valores van directo al ESP32.
            y = AddSlider(body, "Kp", lx, y, 0, 200, 30, out _trkKp, out _lblKp,
                v => { SetMotorVal("Kp", v); _lblKp.Text = v.ToString(); SendConfig(); });
            y = AddSlider(body, "Ki", lx, y, 0, 50, 5, out _trkKi, out _lblKi,
                v => { SetMotorVal("Ki", v); _lblKi.Text = v.ToString(); SendConfig(); });
            y = AddSlider(body, "Kd", lx, y, 0, 50, 0, out _trkKd, out _lblKd,
                v => { SetMotorVal("Kd", v); _lblKd.Text = v.ToString(); SendConfig(); });
            y = AddSlider(body, "PWM min", lx, y, 0, 4095, 180, out _trkMinPwm, out _lblMinPwm,
                v => { SetMotorVal("PwmMin", v); _lblMinPwm.Text = v.ToString(); SendConfig(); });
            y = AddSlider(body, "Rampa", lx, y, 5, 200, 60, out _trkSlew, out _lblSlew,
                v => { SetMotorVal("SlewRate", v); _lblSlew.Text = v.ToString(); SendConfig(); });
            y = AddSlider(body, "Deadband %", lx, y, 0, 20, 2, out _trkDeadband, out _lblDeadband,
                v => { SetMotorVal("Deadband", v); _lblDeadband.Text = v.ToString(); SendConfig(); });

            // Botón guardar.
            var btnSave = Theme.MkAccentButton("\u2713 GUARDAR", 120, 28);
            btnSave.Location = new Point(lx, y);
            btnSave.Click += (s, ev) => { _motores.Save(); SendConfig(); };
            body.Controls.Add(btnSave);

            // Live KPIs.
            var lblLive = MkLabel("", lx + 140, y + 4);
            lblLive.Name = "lblLive";
            lblLive.Size = new Size(400, 16);
            body.Controls.Add(lblLive);
            y += 36;

            // RPM display grande.
            var rpmPanel = new Panel
            {
                Location = new Point(lx, y),
                Size = new Size(540, 42),
                BackColor = Color.FromArgb(15, 20, 10)
            };
            rpmPanel.Paint += (s, ev) =>
            {
                using (var pen = new Pen(Theme.Border))
                    ev.Graphics.DrawRectangle(pen, 0, 0, rpmPanel.Width - 1, rpmPanel.Height - 1);
            };
            body.Controls.Add(rpmPanel);

            var lblRpmBig = new Label
            {
                Name = "lblRpmBig", Text = "0 RPM",
                Font = new Font(Theme.FontFamily, 18f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Color.Transparent,
                Location = new Point(12, 6), AutoSize = true
            };
            rpmPanel.Controls.Add(lblRpmBig);

            var lblRpmLabel = new Label
            {
                Text = "Velocidad del motor",
                Font = new Font("Segoe UI", 8f), ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Location = new Point(200, 14), AutoSize = true
            };
            rpmPanel.Controls.Add(lblRpmLabel);

            var lblPulsos = new Label
            {
                Name = "lblPulsos", Text = "",
                Font = new Font(Theme.FontFamily, 9f), ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                Location = new Point(380, 14), AutoSize = true
            };
            rpmPanel.Controls.Add(lblPulsos);
            y += 48;

            // Gráfico.
            _graphPanel = new Panel
            {
                Location = new Point(lx, y),
                Size = new Size(540, 160),
                BackColor = Color.FromArgb(12, 12, 14)
            };
            _graphPanel.Paint += PaintGraph;
            body.Controls.Add(_graphPanel);
        }

        // =====================================================================
        // Slider helper
        // =====================================================================

        private int AddSlider(Control parent, string label, int x, int y,
            int min, int max, int initial, out TrackBar trk, out Label lbl,
            Action<int> onChange)
        {
            parent.Controls.Add(MkLabel(label + ":", x, y + 2));

            lbl = new Label
            {
                Text = initial.ToString(), Font = new Font(Theme.FontFamily, 9f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Color.Transparent,
                Location = new Point(x + 80, y + 2), Size = new Size(50, 16)
            };
            parent.Controls.Add(lbl);

            trk = new TrackBar
            {
                Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, initial)),
                TickFrequency = Math.Max(1, (max - min) / 20),
                LargeChange = Math.Max(1, (max - min) / 10),
                Location = new Point(x + 130, y), Size = new Size(400, 28),
                BackColor = Theme.BgBlack
            };
            var capturedLbl = lbl;
            var capturedTrk = trk;
            capturedTrk.ValueChanged += (s, ev) =>
            {
                // El label se actualiza desde LoadMotorValues con el valor real.
                onChange(capturedTrk.Value);
            };
            parent.Controls.Add(capturedTrk);

            return y + 32;
        }

        // =====================================================================
        // Motor values
        // =====================================================================

        private QxMotorConfig GetSelectedMotor()
        {
            if (_cboNodo == null || _cboMotor == null) return null;
            if (_cboNodo.SelectedIndex < 0 || _cboNodo.SelectedIndex >= _motores.Nodos.Count) return null;
            var nodo = _motores.Nodos[_cboNodo.SelectedIndex];
            int mi = _cboMotor.SelectedIndex;
            if (mi < 0 || mi >= nodo.Motores.Length) return null;
            return nodo.Motores[mi];
        }

        private void LoadMotorValues()
        {
            var m = GetSelectedMotor();
            if (m == null) return;
            _trkKp.Value = Clamp(_trkKp, (int)m.Kp);
            _trkKi.Value = Clamp(_trkKi, (int)m.Ki);
            _trkKd.Value = Clamp(_trkKd, (int)m.Kd);
            _trkMinPwm.Value = Clamp(_trkMinPwm, m.PwmMin);
            _trkSlew.Value = Clamp(_trkSlew, m.SlewRate);
            _trkDeadband.Value = Clamp(_trkDeadband, m.Deadband);
            _lblKp.Text = ((int)m.Kp).ToString(); _lblKi.Text = ((int)m.Ki).ToString();
            _lblKd.Text = ((int)m.Kd).ToString();
            _lblMinPwm.Text = m.PwmMin.ToString(); _lblSlew.Text = m.SlewRate.ToString();
            _lblDeadband.Text = m.Deadband.ToString();

            _histTarget.Clear(); _histReal.Clear();
        }

        private void SetMotorVal(string field, double val)
        {
            var m = GetSelectedMotor();
            if (m == null) return;
            switch (field)
            {
                case "Kp": m.Kp = val; break;
                case "Ki": m.Ki = val; break;
                case "Kd": m.Kd = val; break;
                case "PwmMin": m.PwmMin = (int)val; break;
                case "SlewRate": m.SlewRate = (int)val; break;
                case "Deadband": m.Deadband = (int)val; break;
            }
        }

        private static int Clamp(TrackBar trk, int val)
        {
            return Math.Max(trk.Minimum, Math.Min(trk.Maximum, val));
        }

        private void ApplyPreset(double kp, double ki, double kd, int minPwm, int slew, int deadband)
        {
            _trkKp.Value = Clamp(_trkKp, (int)kp);
            _trkKi.Value = Clamp(_trkKi, (int)ki);
            _trkKd.Value = Clamp(_trkKd, (int)kd);
            _trkMinPwm.Value = Clamp(_trkMinPwm, minPwm);
            _trkSlew.Value = Clamp(_trkSlew, slew);
            _trkDeadband.Value = Clamp(_trkDeadband, deadband);
        }

        // =====================================================================
        // MQTT — enviar config en caliente + recibir status
        // =====================================================================

        private void StartMqtt()
        {
            try
            {
                var v = VistaXConfig.Load();
                var c = new VistaXConfig
                {
                    Enabled = true, BrokerAddress = v.BrokerAddress, BrokerPort = v.BrokerPort,
                    ClientId = "QX_PID", TelemetriaTopic = "agp/quantix/+/status_live",
                    SectionsTopic = "", SpeedTopic = ""
                };
                _mqtt = new MqttClientWrapper(c);
                _mqtt.MessageReceived += OnStatus;
                _ = _mqtt.ConnectAsync();

                // Log CSV.
                string logPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "qx_pid.csv");
                // Header si no existe.
                if (!System.IO.File.Exists(logPath))
                    System.IO.File.WriteAllText(logPath,
                        "time,target,real,pwm,error,error_pct,kp,ki,kd,minpwm,slew,deadband\n");
                _pidLogPath = logPath;
                _pidLogStart = DateTime.Now;

                _refreshTimer = new Timer { Interval = 100 };
                _refreshTimer.Tick += (s, ev) =>
                {
                    _histTarget.Add(_liveTarget);
                    _histReal.Add(_liveReal);
                    while (_histTarget.Count > MaxHistory) { _histTarget.RemoveAt(0); _histReal.RemoveAt(0); }

                    _graphPanel.Invalidate();

                    var lbl = Controls.Find("lblLive", true);
                    if (lbl.Length > 0)
                        lbl[0].Text = string.Format("TGT:{0:F1}  ACT:{1:F1}  PWM:{2}  RPM:{3}  ERR:{4:F1}%",
                            _liveTarget, _liveReal, _livePwm, _liveRpm,
                            _liveTarget > 0 ? ((_liveReal - _liveTarget) / _liveTarget * 100) : 0);

                    var lblRpm = Controls.Find("lblRpmBig", true);
                    if (lblRpm.Length > 0)
                        lblRpm[0].Text = _liveRpm.ToString() + " RPM";

                    var lblPulsos = Controls.Find("lblPulsos", true);
                    if (lblPulsos.Length > 0)
                        lblPulsos[0].Text = _livePulsos.ToString("N0") + " pulsos";

                    // Log cada 200ms (cada 2 ticks).
                    if (_pidLogPath != null && _histTarget.Count % 2 == 0)
                    {
                        try
                        {
                            double elapsed = (DateTime.Now - _pidLogStart).TotalSeconds;
                            double errPct = _liveTarget > 0
                                ? ((_liveReal - _liveTarget) / _liveTarget * 100) : 0;
                            var motor = GetSelectedMotor();
                            System.IO.File.AppendAllText(_pidLogPath,
                                elapsed.ToString("F1", CultureInfo.InvariantCulture) + ","
                                + _liveTarget.ToString("F2", CultureInfo.InvariantCulture) + ","
                                + _liveReal.ToString("F2", CultureInfo.InvariantCulture) + ","
                                + _livePwm + ","
                                + (_liveTarget - _liveReal).ToString("F2", CultureInfo.InvariantCulture) + ","
                                + errPct.ToString("F1", CultureInfo.InvariantCulture) + ","
                                + (motor != null ? motor.Kp.ToString("F4", CultureInfo.InvariantCulture) : "") + ","
                                + (motor != null ? motor.Ki.ToString("F5", CultureInfo.InvariantCulture) : "") + ","
                                + (motor != null ? motor.Kd.ToString("F5", CultureInfo.InvariantCulture) : "") + ","
                                + (motor != null ? motor.PwmMin.ToString() : "") + ","
                                + (motor != null ? motor.SlewRate.ToString() : "") + ","
                                + (motor != null ? motor.Deadband.ToString() : "") + "\n");
                        }
                        catch { }
                    }
                };
                _refreshTimer.Start();
            }
            catch { }
        }

        private void OnStatus(string topic, string payload)
        {
            if (!topic.Contains("status_live")) return;
            try
            {
                int id = (int)ExtractNum(payload, "\"id\":");
                if (id != _cboMotor.SelectedIndex) return;

                _liveTarget = ExtractNum(payload, "\"pps_target\":");
                _liveReal = ExtractNum(payload, "\"pps_real\":");
                _livePwm = (int)ExtractNum(payload, "\"pwm\":");
                _liveRpm = (int)ExtractNum(payload, "\"rpm\":");
                _livePulsos = (long)ExtractNum(payload, "\"pulsos\":");
            }
            catch { }
        }

        private async void SendConfig()
        {
            if (_mqtt == null || _cboNodo.SelectedIndex < 0) return;
            var nodo = _motores.Nodos[_cboNodo.SelectedIndex];
            if (string.IsNullOrEmpty(nodo.Uid)) return;
            var m = GetSelectedMotor();
            if (m == null) return;

            int mi = _cboMotor.SelectedIndex;
            string json = "{\"configs\":[{\"idx\":" + mi
                + ",\"config_pid\":{\"kp\":" + m.Kp.ToString(CultureInfo.InvariantCulture)
                + ",\"ki\":" + m.Ki.ToString(CultureInfo.InvariantCulture)
                + ",\"kd\":" + m.Kd.ToString(CultureInfo.InvariantCulture) + "}"
                + ",\"calibracion\":{\"pwm_min\":" + m.PwmMin + ",\"pwm_max\":" + m.PwmMax + "}"
                + ",\"meter_cal\":" + m.MeterCal.ToString(CultureInfo.InvariantCulture)
                + "}]}";

            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic("agp/quantix/" + nodo.Uid + "/config")
                    .WithPayload(json).WithRetainFlag(true).Build();
                await _mqtt.PublishAsync(msg);
            }
            catch { }
        }

        // =====================================================================
        // Gráfico Target vs Real
        // =====================================================================

        private void PaintGraph(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(12, 12, 14));

            int w = _graphPanel.Width, h = _graphPanel.Height;
            int pad = 4;

            // Borde.
            using (var p = new Pen(Theme.Border))
                g.DrawRectangle(p, 0, 0, w - 1, h - 1);

            if (_histTarget.Count < 2) return;

            // Escala.
            double maxVal = 1;
            foreach (var v in _histTarget) if (v > maxVal) maxVal = v;
            foreach (var v in _histReal) if (v > maxVal) maxVal = v;
            maxVal *= 1.2;

            int n = _histTarget.Count;
            float dx = (float)(w - pad * 2) / Math.Max(1, MaxHistory - 1);

            // Target (línea verde punteada).
            using (var pen = new Pen(Color.FromArgb(100, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B), 1f))
            {
                pen.DashStyle = DashStyle.Dash;
                for (int i = 1; i < n; i++)
                {
                    float x0 = pad + (MaxHistory - n + i - 1) * dx;
                    float x1 = pad + (MaxHistory - n + i) * dx;
                    float y0 = h - pad - (float)(_histTarget[i - 1] / maxVal * (h - pad * 2));
                    float y1 = h - pad - (float)(_histTarget[i] / maxVal * (h - pad * 2));
                    g.DrawLine(pen, x0, y0, x1, y1);
                }
            }

            // Real (línea sólida brillante).
            Color realColor = Theme.Accent;
            if (_liveTarget > 0)
            {
                double err = Math.Abs(_liveReal - _liveTarget) / _liveTarget;
                if (err > 0.2) realColor = Color.FromArgb(220, 40, 0);
                else if (err > 0.1) realColor = Color.FromArgb(255, 220, 0);
            }

            using (var pen = new Pen(realColor, 2f))
            {
                for (int i = 1; i < n; i++)
                {
                    float x0 = pad + (MaxHistory - n + i - 1) * dx;
                    float x1 = pad + (MaxHistory - n + i) * dx;
                    float y0 = h - pad - (float)(_histReal[i - 1] / maxVal * (h - pad * 2));
                    float y1 = h - pad - (float)(_histReal[i] / maxVal * (h - pad * 2));
                    g.DrawLine(pen, x0, y0, x1, y1);
                }
            }

            // Leyenda.
            using (var f = new Font("Segoe UI", 7f))
            {
                using (var b = new SolidBrush(Color.FromArgb(100, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B)))
                    g.DrawString("--- Target", f, b, 4, 2);
                using (var b = new SolidBrush(realColor))
                    g.DrawString("\u2501 Real", f, b, 80, 2);
                using (var b = new SolidBrush(Theme.TextFaint))
                    g.DrawString(maxVal.ToString("F1"), f, b, w - 40, 2);
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private Label MkLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text, Font = Theme.FontSmall, ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent, Location = new Point(x, y), AutoSize = true
            };
        }

        private ComboBox MkCombo(int x, int y, int w)
        {
            return new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = Theme.FontBody, ForeColor = Theme.TextPrimary, BackColor = Theme.BgInput,
                Location = new Point(x, y), Size = new Size(w, 22)
            };
        }

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
            double.TryParse(json.Substring(idx, end - idx),
                NumberStyles.Any, CultureInfo.InvariantCulture, out val);
            return val;
        }
    }
}
