// ============================================================================
// FormVistaXSimulator.cs - Simulador de nodos y sensores para pruebas
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXSimulator.cs
// Target: net48 (C# 7.3)
//
// Publica mensajes MQTT simulados en el topic de telemetría, emulando
// N nodos con 8 canales cada uno. Permite ajustar: cantidad de nodos,
// canales activos, flujo base (con ruido), simular fallas en surcos
// específicos, y frecuencia de envío.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MQTTnet;
using MQTTnet.Client;
using AgroParallel.Common;

namespace AgroParallel.VistaX
{
    public class FormVistaXSimulator : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CBgCard { get { return Theme.BgCard; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CAccentDim { get { return Theme.AccentDim; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CTextFaint { get { return Theme.TextFaint; } }
        private static Color CBorder { get { return Theme.Border; } }
        private static Color CRed { get { return Theme.Error; } }

        private readonly VistaXConfig _cfg;

        // Simulator state.
        private IMqttClient _mqttClient;
        private System.Windows.Forms.Timer _timer;
        private bool _running;
        private readonly Random _rng = new Random();
        private int _msgCount;

        // Controls.
        private NumericUpDown _numNodos;
        private NumericUpDown _numCanales;
        private NumericUpDown _numFlujoBase;
        private NumericUpDown _numRuido;
        private NumericUpDown _numIntervalo;
        private CheckedListBox _chkFallas;
        private Label _lblStatus;
        private Label _lblMsgCount;
        private Button _btnStart;
        private Button _btnStop;
        private TextBox _txtTopic;
        private TextBox _txtPrefix;

        public FormVistaXSimulator(VistaXConfig cfg)
        {
            _cfg = cfg ?? new VistaXConfig();
            BuildUI();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            StopSimulator();
            if (_mqttClient != null)
            {
                var c = _mqttClient;
                _mqttClient = null;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { c.Dispose(); } catch { }
                });
            }
        }

        // =====================================================================
        // UI
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "VistaX — Simulador de Nodos";
            Size = new Size(640, 620);
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CBgDark; ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, ev) => { if (ev.KeyCode == Keys.Escape) Close(); };
            Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // ── Top bar ─────────────────────────────────────────────────
            var topBar = new Panel
            {
                Dock = DockStyle.Top, Height = 56, BackColor = CBgPanel, Cursor = Cursors.SizeAll
            };
            topBar.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };
            topBar.MouseDown += (s, ev) => { if (ev.Button == MouseButtons.Left) { _dragging = true; _dragStart = ev.Location; } };
            topBar.MouseMove += (s, ev) => { if (_dragging) Location = new Point(Location.X + ev.X - _dragStart.X, Location.Y + ev.Y - _dragStart.Y); };
            topBar.MouseUp += (s, ev) => _dragging = false;

            topBar.Controls.Add(new Label { Text = "VistaX", Font = new Font("Segoe UI", 18f, FontStyle.Bold), ForeColor = CText, Location = new Point(24, 14), AutoSize = true, BackColor = Color.Transparent });
            topBar.Controls.Add(new Label { Text = "SIMULADOR", Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = CAccent, Location = new Point(116, 20), AutoSize = true, BackColor = Color.Transparent });

            // Cerrar: chrome nativo del Form.
            Controls.Add(topBar);

            // ── Body ────────────────────────────────────────────────────
            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = CBgDark };
            Controls.Add(body);
            body.BringToFront();

            int lx = 24, fx = 260, y = 16;

            // MQTT config.
            AddLabel(body, "MQTT BROKER", lx, y, true);
            y += 24;
            AddLabel(body, "Topic telemetr\u00EDa:", lx, y + 2, false);
            _txtTopic = AddTextBox(body, fx, y, 320, _cfg.TelemetriaTopic ?? "vistax/nodos/telemetria");
            y += 32;

            AddLabel(body, "Prefijo UID nodos:", lx, y + 2, false);
            _txtPrefix = AddTextBox(body, fx, y, 180, "VX-SIM-");
            y += 36;

            // Simulation params.
            AddLabel(body, "SIMULACI\u00D3N", lx, y, true);
            y += 24;
            y = AddNumRow(body, "Cantidad de nodos:", lx, fx, y, 1, 20, 6, out _numNodos);
            y = AddNumRow(body, "Canales por nodo:", lx, fx, y, 1, 8, 8, out _numCanales);
            y = AddNumRow(body, "Flujo base (sem/s):", lx, fx, y, 0, 100, 14, out _numFlujoBase);
            y = AddNumRow(body, "Ruido (\u00B1 %):", lx, fx, y, 0, 100, 15, out _numRuido);
            y = AddNumRow(body, "Intervalo env\u00EDo (ms):", lx, fx, y, 100, 5000, 500, out _numIntervalo);

            // Fallas simuladas.
            AddLabel(body, "FALLAS SIMULADAS", lx, y, true);
            y += 24;
            AddLabel(body, "Seleccion\u00E1 surcos con falla (flujo = 0):", lx, y, false);
            y += 22;

            _chkFallas = new CheckedListBox
            {
                Location = new Point(lx, y),
                Size = new Size(Width - 48, 100),
                BackColor = CBgCard, ForeColor = CText,
                Font = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.None,
                CheckOnClick = true
            };
            body.Controls.Add(_chkFallas);
            _numNodos.ValueChanged += (s, ev) => RebuildFallasList();
            _numCanales.ValueChanged += (s, ev) => RebuildFallasList();
            RebuildFallasList();
            y += 108;

            // Status.
            _lblStatus = new Label
            {
                Text = "\u23F8  DETENIDO",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(lx, y), AutoSize = true
            };
            body.Controls.Add(_lblStatus);

            _lblMsgCount = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 9f),
                ForeColor = CTextFaint, BackColor = Color.Transparent,
                Location = new Point(lx + 200, y + 2), AutoSize = true
            };
            body.Controls.Add(_lblMsgCount);

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = CBgPanel };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            _btnStart = MkPillButton("\u25B6  INICIAR", CAccent, CBgDark);
            _btnStart.Size = new Size(130, 38);
            _btnStart.Location = new Point(24, 9);
            _btnStart.Click += (s, ev) => StartSimulator();
            footer.Controls.Add(_btnStart);

            _btnStop = MkPillButton("\u25A0  DETENER", Color.FromArgb(50, 25, 25), CRed);
            _btnStop.Size = new Size(130, 38);
            _btnStop.Location = new Point(164, 9);
            _btnStop.Enabled = false;
            _btnStop.Click += (s, ev) => StopSimulator();
            footer.Controls.Add(_btnStop);

            var btnClose = new Button
            {
                Text = "\u2715 CERRAR", FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand, Size = new Size(110, 38),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Location = new Point(Width - btnClose.Width - 18, 9);
            footer.Controls.Add(btnClose);

            Controls.Add(footer);
            CancelButton = btnClose;
        }

        private void RebuildFallasList()
        {
            _chkFallas.Items.Clear();
            int nodos = (int)_numNodos.Value;
            int canales = (int)_numCanales.Value;
            string prefix = _txtPrefix != null ? _txtPrefix.Text.Trim() : "VX-SIM-";
            if (string.IsNullOrEmpty(prefix)) prefix = "VX-SIM-";

            for (int n = 1; n <= nodos; n++)
            {
                for (int c = 1; c <= canales; c++)
                {
                    string label = prefix + n + " c" + c;
                    _chkFallas.Items.Add(label, false);
                }
            }
        }

        // =====================================================================
        // Simulator engine
        // =====================================================================

        private async void StartSimulator()
        {
            if (_running) return;

            try
            {
                var factory = new MqttFactory();
                _mqttClient = factory.CreateMqttClient();

                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(_cfg.BrokerAddress ?? "127.0.0.1", _cfg.BrokerPort > 0 ? _cfg.BrokerPort : 1883)
                    .WithClientId("VistaX_Simulator_" + Guid.NewGuid().ToString("N").Substring(0, 6))
                    .WithCleanSession(true)
                    .Build();

                await _mqttClient.ConnectAsync(opts, CancellationToken.None);

                _running = true;
                _msgCount = 0;
                _btnStart.Enabled = false;
                _btnStop.Enabled = true;
                _lblStatus.Text = "\u25B6  SIMULANDO";
                _lblStatus.ForeColor = CAccent;

                int interval = (int)_numIntervalo.Value;
                _timer = new System.Windows.Forms.Timer { Interval = interval };
                _timer.Tick += OnTimerTick;
                _timer.Start();

                System.Diagnostics.Debug.WriteLine("[VistaX-Sim] Iniciado — "
                    + (int)_numNodos.Value + " nodos, intervalo " + interval + "ms");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "\u26A0  ERROR: " + ex.Message;
                _lblStatus.ForeColor = CRed;
                _running = false;
                _btnStart.Enabled = true;
                _btnStop.Enabled = false;
            }
        }

        private void StopSimulator()
        {
            if (!_running) return;
            _running = false;

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }

            _btnStart.Enabled = true;
            _btnStop.Enabled = false;
            _lblStatus.Text = "\u23F8  DETENIDO";
            _lblStatus.ForeColor = CTextDim;
        }

        private async void OnTimerTick(object sender, EventArgs e)
        {
            if (!_running || _mqttClient == null || !_mqttClient.IsConnected) return;

            int nodos = (int)_numNodos.Value;
            int canales = (int)_numCanales.Value;
            double flujoBase = (double)_numFlujoBase.Value;
            double ruido = (double)_numRuido.Value / 100.0;
            string prefix = _txtPrefix.Text.Trim();
            if (string.IsNullOrEmpty(prefix)) prefix = "VX-SIM-";
            string topic = _txtTopic.Text.Trim();

            // Build set of failed channels.
            var failedSet = new HashSet<int>();
            for (int i = 0; i < _chkFallas.Items.Count; i++)
            {
                if (_chkFallas.GetItemChecked(i))
                    failedSet.Add(i);
            }

            int globalIdx = 0;
            for (int n = 1; n <= nodos; n++)
            {
                string uid = prefix + n;

                var sb = new StringBuilder(256);
                sb.Append("{\"uid\":\"").Append(uid).Append("\",\"sensores\":[");

                for (int c = 1; c <= canales; c++)
                {
                    if (c > 1) sb.Append(',');

                    bool isFailed = failedSet.Contains(globalIdx);
                    globalIdx++;

                    double valor;
                    int raw;
                    if (isFailed)
                    {
                        valor = 0;
                        raw = 0;
                    }
                    else
                    {
                        double noise = 1.0 + ((_rng.NextDouble() * 2 - 1) * ruido);
                        valor = Math.Max(0, Math.Round(flujoBase * noise, 2));
                        raw = (int)Math.Round(valor * 0.5); // Simulated raw pulse count.
                    }

                    sb.Append("{\"cable\":")
                      .Append(c.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"valor\":")
                      .Append(valor.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"raw\":")
                      .Append(raw.ToString(CultureInfo.InvariantCulture))
                      .Append('}');
                }

                sb.Append("]}");

                try
                {
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(sb.ToString())
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                        .Build();

                    await _mqttClient.PublishAsync(msg, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[VistaX-Sim] Publish error: " + ex.Message);
                }
            }

            _msgCount += nodos;
            _lblMsgCount.Text = _msgCount + " mensajes enviados";
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void AddLabel(Control parent, string text, int x, int y, bool isHeader)
        {
            parent.Controls.Add(new Label
            {
                Text = text,
                Font = new Font("Segoe UI", isHeader ? 9.5f : 9.5f, isHeader ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = isHeader ? CAccent : CTextDim,
                BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            });
        }

        private TextBox AddTextBox(Control parent, int x, int y, int w, string val)
        {
            var tb = new TextBox
            {
                Text = val,
                Font = new Font("Consolas", 10f),
                ForeColor = CText, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(x, y), Size = new Size(w, 26)
            };
            parent.Controls.Add(tb);
            return tb;
        }

        private int AddNumRow(Control parent, string label, int lx, int fx, int y,
            int min, int max, int initial, out NumericUpDown nud)
        {
            parent.Controls.Add(new Label
            {
                Text = label, Font = new Font("Segoe UI", 9.5f),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(lx, y + 2), AutoSize = true
            });
            nud = new NumericUpDown
            {
                Minimum = min, Maximum = max, Value = initial,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(fx, y), Size = new Size(100, 28),
                TextAlign = HorizontalAlignment.Center
            };
            parent.Controls.Add(nud);
            return y + 34;
        }

        private static Button MkPillButton(string text, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat,
                BackColor = bg, ForeColor = fg,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}
