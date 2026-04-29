// ============================================================================
// FormVistaXPrueba.cs - Prueba de sensores en tiempo real
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXPrueba.cs
// Target: net48 (C# 7.3)
//
// Panel independiente (ventana aparte). Muestra todos los sensores en grilla
// de cards grandes agrupados por tren. Cada card muestra surco, nodo+canal,
// y contador de pulsos en tiempo real. Sirve para verificar cableado tirando
// semilla a mano. Indicador de conexión MQTT arriba.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace AgroParallel.VistaX
{
    public class FormVistaXPrueba : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CBgCard { get { return Theme.BgCard; } }
        private static Color CBgCardActive { get { return Theme.AccentDark; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CAccentDim { get { return Theme.AccentDim; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CTextFaint { get { return Theme.TextFaint; } }
        private static Color CBorder { get { return Theme.Border; } }
        private static Color CRed { get { return Theme.Error; } }

        private readonly VistaXConfig _cfg;
        private ImplementoConfig _implemento;

        private MqttClientWrapper _mqtt;
        private bool _isConnected;
        private Panel _connectionBar;
        private Label _lblConnection;
        private Panel _gridPanel;
        private Timer _refreshTimer;

        // Per-sensor pulse state: key = uid + "-" + cable
        private readonly Dictionary<string, PulseState> _pulses =
            new Dictionary<string, PulseState>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        private class PulseState
        {
            public int TotalPulses;
            public double LastValor;
            public DateTime LastSeen;
            public int DeltaPulses; // since last UI refresh
        }

        // Map sensor key to its card panel for efficient update.
        private readonly Dictionary<string, SensorCard> _cards =
            new Dictionary<string, SensorCard>(StringComparer.OrdinalIgnoreCase);

        private class SensorCard
        {
            public Panel Panel;
            public Label LblPulses;
            public Label LblValor;
            public Label LblSeen;
            public Panel LedPanel;
            public string Key;
            public int LastDrawnPulses;
        }

        public FormVistaXPrueba(VistaXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadData();
            BuildGrid();
            StartMqtt();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                _refreshTimer = null;
            }
            var mqtt = _mqtt;
            _mqtt = null;
            if (mqtt != null)
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { mqtt.Dispose(); } catch { }
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
            Text = "VistaX — Prueba de Sensores";
            Size = new Size(1140, 740);
            MinimumSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = CBgDark; ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = true;
            FormBorderStyle = FormBorderStyle.None;
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
            topBar.Controls.Add(new Label { Text = "PRUEBA DE SENSORES", Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = CAccent, Location = new Point(116, 20), AutoSize = true, BackColor = Color.Transparent });

            var btnX = new Button { Text = "\u2715", FlatStyle = FlatStyle.Flat, BackColor = CBgPanel, ForeColor = CTextDim, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Size = new Size(40, 32), Cursor = Cursors.Hand, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnX.FlatAppearance.BorderSize = 0;
            btnX.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 40, 40);
            btnX.Click += (s, ev) => Close();
            topBar.Controls.Add(btnX);
            topBar.Resize += (s, ev) => btnX.Location = new Point(topBar.Width - btnX.Width - 4, 12);
            Controls.Add(topBar);

            // ── Connection bar ──────────────────────────────────────────
            _connectionBar = new Panel
            {
                Dock = DockStyle.Top, Height = 32,
                BackColor = Color.FromArgb(40, 10, 10)
            };
            _lblConnection = new Label
            {
                Text = "\u26A0  Conectando a MQTT...",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = CRed, BackColor = Color.Transparent,
                Location = new Point(14, 7), AutoSize = true
            };
            _connectionBar.Controls.Add(_lblConnection);
            Controls.Add(_connectionBar);

            // ── Sub-header ──────────────────────────────────────────────
            var subHeader = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = CBgDark };
            subHeader.Controls.Add(new Label
            {
                Text = "\U0001F9EA  Tir\u00E1 semilla a mano por cada bajada para verificar el cableado. "
                    + "Cada card muestra el contador de pulsos en tiempo real.",
                Font = new Font("Segoe UI", 9.5f), ForeColor = CTextDim,
                Location = new Point(24, 10), Size = new Size(1000, 40), BackColor = Color.Transparent
            });

            var btnReset = new Button
            {
                Text = "\u21BB  RESETEAR CONTADORES", FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 45), ForeColor = CText,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Size = new Size(200, 28), Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnReset.FlatAppearance.BorderSize = 0;
            btnReset.Click += (s, ev) => ResetCounters();
            subHeader.Controls.Add(btnReset);
            subHeader.Resize += (s, ev) =>
                btnReset.Location = new Point(subHeader.Width - btnReset.Width - 24, 16);
            Controls.Add(subHeader);

            // ── Grid ────────────────────────────────────────────────────
            _gridPanel = new Panel
            {
                Dock = DockStyle.Fill, AutoScroll = true, BackColor = CBgDark,
                Padding = new Padding(14, 10, 14, 10)
            };
            Controls.Add(_gridPanel);
            _gridPanel.BringToFront();

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = CBgPanel };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            footer.Controls.Add(new Label
            {
                Text = "VistaX nativo \u00B7 Prueba de Sensores",
                Font = new Font("Segoe UI", 8.5f), ForeColor = CTextFaint,
                Location = new Point(18, 12), AutoSize = true, BackColor = Color.Transparent
            });
            Controls.Add(footer);
        }

        // =====================================================================
        // Data
        // =====================================================================

        private void LoadData()
        {
            try { _implemento = _cfg.LoadImplemento(); }
            catch { _implemento = new ImplementoConfig(); }
        }

        // =====================================================================
        // Grid
        // =====================================================================

        private void BuildGrid()
        {
            _gridPanel.SuspendLayout();
            _gridPanel.Controls.Clear();
            _cards.Clear();

            if (_implemento == null || _implemento.MapeoSensores == null || _implemento.MapeoSensores.Count == 0)
            {
                _gridPanel.Controls.Add(new Label
                {
                    Text = "No hay sensores configurados en el implemento activo",
                    Font = new Font("Segoe UI", 11f), ForeColor = CTextDim,
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
                });
                _gridPanel.ResumeLayout();
                return;
            }

            // Group by tren, filter semilla.
            var byTren = new SortedDictionary<int, List<SensorConfig>>();
            foreach (var s in _implemento.MapeoSensores)
            {
                if (s == null || !s.IsActive) continue;
                int tren = s.Tren > 0 ? s.Tren : 1;
                List<SensorConfig> list;
                if (!byTren.TryGetValue(tren, out list))
                {
                    list = new List<SensorConfig>();
                    byTren[tren] = list;
                }
                list.Add(s);
            }

            // Tren names.
            var trenNames = new Dictionary<int, string>();
            if (_implemento.Trenes != null)
                foreach (var t in _implemento.Trenes)
                    if (t != null) trenNames[t.Id] = t.Nombre ?? "Tren " + t.Id;

            int cardW = 100, cardH = 110, gap = 6;
            int y = 0;

            foreach (var kv in byTren)
            {
                int trenId = kv.Key;
                var sensors = kv.Value;
                sensors.Sort((a, b) => a.Bajada.CompareTo(b.Bajada));

                string tName;
                if (!trenNames.TryGetValue(trenId, out tName))
                    tName = trenId == 1 ? "Delantero" : trenId == 2 ? "Trasero" : "Tren " + trenId;

                var lblTren = new Label
                {
                    Text = tName.ToUpperInvariant() + "  (" + sensors.Count + " sensores)",
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = CAccent, BackColor = Color.Transparent,
                    Location = new Point(6, y), AutoSize = true
                };
                _gridPanel.Controls.Add(lblTren);
                y += 28;

                int cols = Math.Max(1, (_gridPanel.Width - 40) / (cardW + gap));

                for (int i = 0; i < sensors.Count; i++)
                {
                    var s = sensors[i];
                    int col = i % cols;
                    int row = i / cols;
                    int cx = 6 + col * (cardW + gap);
                    int cy = y + row * (cardH + gap);

                    string sKey = (s.Uid ?? "") + "-" + s.Cable;
                    var sc = MkSensorCard(s, sKey);
                    sc.Panel.Location = new Point(cx, cy);
                    _gridPanel.Controls.Add(sc.Panel);
                    _cards[sKey] = sc;
                }

                int rows = (int)Math.Ceiling((double)sensors.Count / cols);
                y += rows * (cardH + gap) + 16;
            }

            _gridPanel.ResumeLayout();
        }

        private SensorCard MkSensorCard(SensorConfig sensor, string key)
        {
            var card = new Panel
            {
                Size = new Size(100, 110),
                BackColor = CBgCard
            };
            card.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            // Surco number + type color.
            Color tipoColor = TipoSensor.GetColor(sensor.Tipo);
            var lblSurco = new Label
            {
                Text = "S" + sensor.Bajada.ToString(CultureInfo.InvariantCulture),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = tipoColor, BackColor = Color.Transparent,
                Location = new Point(6, 4), AutoSize = true
            };
            card.Controls.Add(lblSurco);

            // Type indicator (accent bar on left edge).
            card.Paint += (s2, ev2) =>
            {
                using (var b = new SolidBrush(tipoColor))
                    ev2.Graphics.FillRectangle(b, 0, 0, 3, card.Height);
            };

            // LED indicator.
            var ledPanel = new Panel
            {
                Size = new Size(14, 14),
                Location = new Point(80, 6),
                BackColor = Color.Transparent
            };
            ledPanel.Paint += (s, ev) =>
            {
                ev.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.FromArgb(50, 50, 55)))
                    ev.Graphics.FillEllipse(b, 0, 0, 13, 13);
            };
            card.Controls.Add(ledPanel);

            // Node + channel.
            string uid4 = sensor.Uid ?? "";
            if (uid4.Length > 4) uid4 = uid4.Substring(uid4.Length - 4);
            card.Controls.Add(new Label
            {
                Text = uid4 + " c" + sensor.Cable,
                Font = new Font("Consolas", 8f),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(6, 26), AutoSize = true
            });

            // Pulse counter (big).
            var lblPulses = new Label
            {
                Text = "0",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                ForeColor = CTextFaint, BackColor = Color.Transparent,
                Location = new Point(6, 44), Size = new Size(88, 32),
                TextAlign = ContentAlignment.MiddleCenter
            };
            card.Controls.Add(lblPulses);

            // Valor label.
            var lblValor = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 8f),
                ForeColor = CTextFaint, BackColor = Color.Transparent,
                Location = new Point(6, 78), AutoSize = true
            };
            card.Controls.Add(lblValor);

            // Last seen.
            var lblSeen = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 7f),
                ForeColor = Color.FromArgb(55, 55, 60), BackColor = Color.Transparent,
                Location = new Point(6, 94), AutoSize = true
            };
            card.Controls.Add(lblSeen);

            return new SensorCard
            {
                Panel = card,
                LblPulses = lblPulses,
                LblValor = lblValor,
                LblSeen = lblSeen,
                LedPanel = ledPanel,
                Key = key,
                LastDrawnPulses = -1
            };
        }

        // =====================================================================
        // MQTT
        // =====================================================================

        private void StartMqtt()
        {
            if (!_cfg.Enabled || string.IsNullOrWhiteSpace(_cfg.TelemetriaTopic))
            {
                UpdateConnectionBar(false, "VistaX deshabilitado o sin topic de telemetr\u00EDa");
                return;
            }

            _mqtt = new MqttClientWrapper(_cfg);
            _mqtt.MessageReceived += OnMessage;
            _mqtt.ConnectionStateChanged += delegate (bool c)
            {
                _isConnected = c;
                try
                {
                    if (InvokeRequired)
                        BeginInvoke(new Action<bool, string>(UpdateConnectionBar),
                            c, c ? "Conectado a MQTT \u2014 esperando pulsos..." : "Desconectado de MQTT");
                    else
                        UpdateConnectionBar(c, c ? "Conectado" : "Desconectado");
                }
                catch { }
            };
            _mqtt.ErrorOccurred += delegate (string err)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX-Prueba] " + err);
            };
            _ = _mqtt.ConnectAsync();

            _refreshTimer = new Timer { Interval = 300 };
            _refreshTimer.Tick += (s, ev) => RefreshCards();
            _refreshTimer.Start();
        }

        private void UpdateConnectionBar(bool connected, string msg)
        {
            if (_connectionBar == null || _connectionBar.IsDisposed) return;
            _connectionBar.BackColor = connected
                ? Color.FromArgb(10, 30, 16)
                : Color.FromArgb(40, 10, 10);
            _lblConnection.Text = (connected ? "\u2713  " : "\u26A0  ") + msg;
            _lblConnection.ForeColor = connected ? CAccent : CRed;
        }

        private void OnMessage(string topic, string payload)
        {
            if (!topic.Contains("telemetria")) return;

            try
            {
                var data = JsonSerializer.Deserialize<EspTelemetriaPayload>(payload);
                if (data == null || string.IsNullOrEmpty(data.Uid) || data.Sensores == null) return;

                lock (_lock)
                {
                    foreach (var s in data.Sensores)
                    {
                        string key = data.Uid + "-" + s.Cable;
                        PulseState ps;
                        if (!_pulses.TryGetValue(key, out ps))
                        {
                            ps = new PulseState();
                            _pulses[key] = ps;
                        }
                        ps.DeltaPulses = s.Raw;
                        ps.TotalPulses += s.Raw;
                        ps.LastValor = s.Valor;
                        ps.LastSeen = DateTime.UtcNow;
                    }
                }
            }
            catch { }
        }

        private void RefreshCards()
        {
            lock (_lock)
            {
                foreach (var kv in _cards)
                {
                    var sc = kv.Value;
                    PulseState ps;
                    if (!_pulses.TryGetValue(kv.Key, out ps)) continue;

                    if (ps.TotalPulses != sc.LastDrawnPulses)
                    {
                        sc.LastDrawnPulses = ps.TotalPulses;
                        sc.LblPulses.Text = ps.TotalPulses.ToString(CultureInfo.InvariantCulture);
                        sc.LblPulses.ForeColor = ps.TotalPulses > 0 ? CAccent : CTextFaint;

                        sc.LblValor.Text = "val: " + ps.LastValor.ToString("F1", CultureInfo.InvariantCulture);

                        // Flash card background.
                        if (ps.DeltaPulses > 0)
                        {
                            sc.Panel.BackColor = CBgCardActive;
                            // Repaint LED green.
                            sc.LedPanel.Paint -= PaintLedGreen;
                            sc.LedPanel.Paint += PaintLedGreen;
                            sc.LedPanel.Invalidate();
                        }
                        else
                        {
                            sc.Panel.BackColor = CBgCard;
                        }

                        var elapsed = DateTime.UtcNow - ps.LastSeen;
                        if (elapsed.TotalSeconds < 2)
                            sc.LblSeen.Text = "ahora";
                        else if (elapsed.TotalSeconds < 60)
                            sc.LblSeen.Text = (int)elapsed.TotalSeconds + "s";
                        else
                            sc.LblSeen.Text = "";
                    }
                }
            }
        }

        private static void PaintLedGreen(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.FromArgb(0, 230, 118)))
                e.Graphics.FillEllipse(b, 0, 0, 13, 13);
        }

        private void ResetCounters()
        {
            lock (_lock)
            {
                foreach (var kv in _pulses)
                    kv.Value.TotalPulses = 0;
            }
            foreach (var kv in _cards)
            {
                kv.Value.LastDrawnPulses = -1;
                kv.Value.LblPulses.Text = "0";
                kv.Value.LblPulses.ForeColor = CTextFaint;
                kv.Value.Panel.BackColor = CBgCard;
            }
        }
    }
}
