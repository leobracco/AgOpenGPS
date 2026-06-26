// ============================================================================
// FormVistaXNodos.cs - Inventario de VistaX Nodes (Config – Nodos)
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXNodos.cs
// Target: net48 (C# 7.3)
//
// Muestra los VistaX Nodes configurados en el implemento activo y detecta
// nodos online via telemetría MQTT. Permite reemplazar un nodo (swap de
// hardware) sin perder el mapeo de sensores.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.VistaX
{
    public class FormVistaXNodos : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CBgCard { get { return Theme.BgCard; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CTextFaint { get { return Theme.TextFaint; } }
        private static Color CBorder { get { return Theme.Border; } }
        private static Color CRed { get { return Theme.Error; } }
        private static Color CYellow { get { return Theme.Warning; } }
        private static Color COnline { get { return Theme.Accent; } }
        private static Color COffline { get { return Theme.Error; } }
        private static Color CUnassigned { get { return Theme.Warning; } }

        // Estado interno de un nodo.
        private class NodoInfo
        {
            public string Uid;
            public bool IsConfigured;       // Existe en mapeo_sensores del implemento
            public int SensoresAsignados;   // Cantidad de sensores mapeados a este nodo
            public int TrenesUsados;        // Cantidad de trenes distintos que usa
            public bool IsOnline;           // Recibió telemetría recientemente
            public DateTime LastSeen;       // Último mensaje de telemetría
            public int PulsesTotal;         // Suma de pulsos en último mensaje
            public int CanalesActivos;      // Canales con valor > 0 en último mensaje
            public int CanalesTotal;        // Total canales reportados en último mensaje
            public string PerfilNombre;     // Nombre del implemento que lo referencia
        }

        private readonly VistaXConfig _cfg;
        private readonly SeedMonitor _monitor;  // puede ser null
        private ImplementoConfig _implemento;
        private FlowLayoutPanel _list;
        private Label _lblOnlineCount;
        private Timer _refreshTimer;

        private readonly Dictionary<string, NodoInfo> _nodos = new Dictionary<string, NodoInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        // Suscripción directa a MQTT para detección de nodos.
        private MqttClientWrapper _mqtt;
        private bool _ownsMqtt; // true si creamos nuestro propio cliente

        public FormVistaXNodos(VistaXConfig cfg, SeedMonitor monitor)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            _monitor = monitor;
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadImplemento();
            BuildNodoIndex();
            StartListening();
            RebuildCards();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            // Stop timer immediately to prevent callbacks on disposed controls.
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                _refreshTimer = null;
            }
            // Dispose MQTT in background to avoid blocking UI if broker is slow.
            var mqtt = _mqtt;
            _mqtt = null;
            if (mqtt != null && _ownsMqtt)
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { mqtt.Dispose(); } catch { }
                });
            }
        }

        // =====================================================================
        // UI Construction
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "VistaX — VistaX Nodes";
            Size = new Size(1020, 660);
            MinimumSize = new Size(780, 480);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CBgDark;
            ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, ev) => { if (ev.KeyCode == Keys.Escape) Close(); };
            Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // ── Sub-header ──────────────────────────────────────────────
            var subHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 76,
                BackColor = CBgDark
            };
            var lblIcon = new Label
            {
                Text = "\U0001F4E1",  // satellite antenna
                Font = new Font("Segoe UI Emoji", 20f, FontStyle.Bold),
                ForeColor = CAccent,
                Location = new Point(22, 20),
                Size = new Size(40, 40),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };
            subHeader.Controls.Add(lblIcon);

            var lblTitle = new Label
            {
                Text = "VISTAX NODES",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(74, 18),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            subHeader.Controls.Add(lblTitle);

            var lblSub = new Label
            {
                Text = "Inventario de VistaX Nodes detectados por red y configurados en el implemento",
                Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim,
                Location = new Point(74, 46),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            subHeader.Controls.Add(lblSub);

            _lblOnlineCount = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = CAccent,
                BackColor = Color.Transparent,
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            subHeader.Controls.Add(_lblOnlineCount);
            subHeader.Resize += (s, ev) =>
            {
                _lblOnlineCount.Location = new Point(
                    subHeader.Width - _lblOnlineCount.Width - 24, 46);
            };

            Controls.Add(subHeader);

            // ── Top bar ─────────────────────────────────────────────────
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = CBgPanel,
                Cursor = Cursors.SizeAll
            };
            topBar.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };
            topBar.MouseDown += (s, ev) =>
            {
                if (ev.Button == MouseButtons.Left)
                {
                    _dragging = true;
                    _dragStart = ev.Location;
                }
            };
            topBar.MouseMove += (s, ev) =>
            {
                if (_dragging)
                    Location = new Point(
                        Location.X + ev.X - _dragStart.X,
                        Location.Y + ev.Y - _dragStart.Y);
            };
            topBar.MouseUp += (s, ev) => _dragging = false;

            var lblBrand = new Label
            {
                Text = "VistaX",
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(24, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            topBar.Controls.Add(lblBrand);
            var lblBrandSub = new Label
            {
                Text = "CONFIGURACION",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CTextDim,
                Location = new Point(116, 20),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            topBar.Controls.Add(lblBrandSub);

            // Cerrar: usar el chrome nativo del Form (FormBorderStyle.Sizable).
            Controls.Add(topBar);

            // ── Card list ───────────────────────────────────────────────
            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = CBgDark,
                Padding = new Padding(20, 14, 20, 14)
            };
            _list.Resize += (s, ev) =>
            {
                int w = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
                foreach (Control c in _list.Controls)
                    if (c.Width != w) c.Width = w;
            };
            Controls.Add(_list);
            _list.BringToFront();

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = CBgPanel
            };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            var lblVer = new Label
            {
                Text = "VistaX nativo \u00B7 VistaX Nodes",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = CTextFaint,
                Location = new Point(18, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            footer.Controls.Add(lblVer);

            var btnRefresh = MkPillButton("\u21BB  Escanear", Color.FromArgb(40, 40, 45), CText);
            btnRefresh.Size = new Size(120, 30);
            btnRefresh.Location = new Point(160, 7);
            btnRefresh.Click += (s, ev) =>
            {
                BuildNodoIndex();
                RebuildCards();
            };
            footer.Controls.Add(btnRefresh);

            var btnClose = new Button
            {
                Text = "\u2715 CERRAR",
                FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel,
                ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Size = new Size(110, 30),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                DialogResult = DialogResult.Cancel
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 30, 30);
            btnClose.Location = new Point(Width - btnClose.Width - 18, 7);
            footer.Controls.Add(btnClose);

            Controls.Add(footer);
            CancelButton = btnClose;
        }

        // =====================================================================
        // Data loading
        // =====================================================================

        private void LoadImplemento()
        {
            try
            {
                _implemento = _cfg.LoadImplemento();
            }
            catch
            {
                _implemento = new ImplementoConfig();
            }
        }

        private void BuildNodoIndex()
        {
            lock (_lock)
            {
                // Preserve online state from existing entries.
                var prevOnline = new Dictionary<string, NodoInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _nodos)
                    if (kv.Value.IsOnline) prevOnline[kv.Key] = kv.Value;

                _nodos.Clear();

                // 1) Index from implemento mapeo_sensores.
                if (_implemento != null && _implemento.MapeoSensores != null)
                {
                    foreach (var sensor in _implemento.MapeoSensores)
                    {
                        if (sensor == null || string.IsNullOrEmpty(sensor.Uid)) continue;
                        NodoInfo info;
                        if (!_nodos.TryGetValue(sensor.Uid, out info))
                        {
                            info = new NodoInfo
                            {
                                Uid = sensor.Uid,
                                IsConfigured = true,
                                PerfilNombre = _implemento.Nombre ?? ""
                            };
                            _nodos[sensor.Uid] = info;
                        }
                        info.SensoresAsignados++;
                        // Track distinct trains.
                        // Simple: just count unique tren values.
                    }

                    // Count distinct trains per node.
                    var trenSets = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sensor in _implemento.MapeoSensores)
                    {
                        if (sensor == null || string.IsNullOrEmpty(sensor.Uid)) continue;
                        HashSet<int> set;
                        if (!trenSets.TryGetValue(sensor.Uid, out set))
                        {
                            set = new HashSet<int>();
                            trenSets[sensor.Uid] = set;
                        }
                        set.Add(sensor.Tren);
                    }
                    foreach (var kv in trenSets)
                    {
                        NodoInfo info;
                        if (_nodos.TryGetValue(kv.Key, out info))
                            info.TrenesUsados = kv.Value.Count;
                    }
                }

                // 2) Restore online state from previous scan.
                foreach (var kv in prevOnline)
                {
                    NodoInfo info;
                    if (_nodos.TryGetValue(kv.Key, out info))
                    {
                        info.IsOnline = kv.Value.IsOnline;
                        info.LastSeen = kv.Value.LastSeen;
                        info.PulsesTotal = kv.Value.PulsesTotal;
                        info.CanalesActivos = kv.Value.CanalesActivos;
                        info.CanalesTotal = kv.Value.CanalesTotal;
                    }
                    else
                    {
                        // Node detected online but not in config — show as unassigned.
                        _nodos[kv.Key] = kv.Value;
                    }
                }
            }
        }

        // =====================================================================
        // MQTT Listening — detect nodes in real time
        // =====================================================================

        private void StartListening()
        {
            if (!_cfg.Enabled) return;
            if (string.IsNullOrWhiteSpace(_cfg.TelemetriaTopic)) return;

            // Use a lightweight MQTT client to listen for telemetry.
            _ownsMqtt = true;
            _mqtt = new MqttClientWrapper(_cfg);
            _mqtt.MessageReceived += OnMqttMessage;
            _mqtt.ErrorOccurred += delegate (string err)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX-Nodos] " + err);
            };
            _ = _mqtt.ConnectAsync();

            // Refresh cards periodically to update "last seen" and online/offline.
            _refreshTimer = new Timer();
            _refreshTimer.Interval = 2000;
            _refreshTimer.Tick += (s, ev) => RefreshOnlineState();
            _refreshTimer.Start();
        }

        private void StopListening()
        {
            if (_mqtt != null && _ownsMqtt)
            {
                try { _mqtt.Dispose(); } catch { }
                _mqtt = null;
            }
        }

        private void OnMqttMessage(string topic, string payload)
        {
            if (!topic.Contains("telemetria")) return;

            try
            {
                var data = JsonSerializer.Deserialize<EspTelemetriaPayload>(payload);
                if (data == null || string.IsNullOrEmpty(data.Uid)) return;

                int pulsesTotal = 0;
                int canalesActivos = 0;
                int canalesTotal = data.Sensores != null ? data.Sensores.Count : 0;
                if (data.Sensores != null)
                {
                    foreach (var s in data.Sensores)
                    {
                        pulsesTotal += s.Raw;
                        if (s.Valor > 0) canalesActivos++;
                    }
                }

                lock (_lock)
                {
                    NodoInfo info;
                    if (!_nodos.TryGetValue(data.Uid, out info))
                    {
                        info = new NodoInfo
                        {
                            Uid = data.Uid,
                            IsConfigured = false,
                            PerfilNombre = ""
                        };
                        _nodos[data.Uid] = info;
                    }
                    info.IsOnline = true;
                    info.LastSeen = DateTime.UtcNow;
                    info.PulsesTotal = pulsesTotal;
                    info.CanalesActivos = canalesActivos;
                    info.CanalesTotal = canalesTotal;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX-Nodos] Parse: " + ex.Message);
            }
        }

        private void RefreshOnlineState()
        {
            bool changed = false;
            int onlineCount = 0;
            int totalCount = 0;

            lock (_lock)
            {
                var timeout = TimeSpan.FromMilliseconds(
                    _cfg.SensorTimeoutMs > 0 ? _cfg.SensorTimeoutMs : 3000);

                foreach (var kv in _nodos)
                {
                    totalCount++;
                    bool wasOnline = kv.Value.IsOnline;
                    if (kv.Value.LastSeen != DateTime.MinValue
                        && (DateTime.UtcNow - kv.Value.LastSeen) > timeout)
                    {
                        kv.Value.IsOnline = false;
                    }
                    if (kv.Value.IsOnline) onlineCount++;
                    if (kv.Value.IsOnline != wasOnline) changed = true;
                }
            }

            // Update online counter label.
            if (_lblOnlineCount != null && !_lblOnlineCount.IsDisposed)
            {
                try
                {
                    if (InvokeRequired)
                        BeginInvoke(new Action(() => UpdateOnlineLabel(onlineCount, totalCount)));
                    else
                        UpdateOnlineLabel(onlineCount, totalCount);
                }
                catch { }
            }

            if (changed) RebuildCardsThreadSafe();
        }

        private void UpdateOnlineLabel(int online, int total)
        {
            if (_lblOnlineCount == null || _lblOnlineCount.IsDisposed) return;
            _lblOnlineCount.Text = online + "/" + total + " ONLINE";
            _lblOnlineCount.ForeColor = online == total && total > 0 ? CAccent : CYellow;
        }

        private void RebuildCardsThreadSafe()
        {
            try
            {
                if (InvokeRequired)
                    BeginInvoke(new Action(RebuildCards));
                else
                    RebuildCards();
            }
            catch { }
        }

        // =====================================================================
        // Card rendering
        // =====================================================================

        private void RebuildCards()
        {
            _list.SuspendLayout();
            _list.Controls.Clear();

            List<NodoInfo> sorted;
            lock (_lock)
            {
                sorted = _nodos.Values.ToList();
            }

            if (sorted.Count == 0)
            {
                _list.Controls.Add(MkEmptyState(
                    _cfg.Enabled
                        ? "No hay nodos configurados ni detectados.\nVerific\u00E1 la conexi\u00F3n MQTT y el implemento activo."
                        : "VistaX est\u00E1 deshabilitado. Activ\u00E1lo desde Configurar VistaX."));
                _list.ResumeLayout();
                UpdateOnlineLabel(0, 0);
                return;
            }

            // Sort: online first, then configured, then by UID.
            sorted.Sort((a, b) =>
            {
                int cmp = b.IsOnline.CompareTo(a.IsOnline);
                if (cmp != 0) return cmp;
                cmp = b.IsConfigured.CompareTo(a.IsConfigured);
                if (cmp != 0) return cmp;
                return string.Compare(a.Uid, b.Uid, StringComparison.OrdinalIgnoreCase);
            });

            int onlineCount = 0;
            foreach (var nodo in sorted)
            {
                _list.Controls.Add(MkNodoCard(nodo));
                if (nodo.IsOnline) onlineCount++;
            }

            _list.ResumeLayout();
            UpdateOnlineLabel(onlineCount, sorted.Count);
        }

        private Panel MkNodoCard(NodoInfo nodo)
        {
            int cardW = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
            var card = new Panel
            {
                Size = new Size(cardW, 100),
                Margin = new Padding(0, 0, 0, 10),
                BackColor = CBgCard,
                Cursor = Cursors.Default
            };

            // Accent bar (left edge) + border.
            Color accentColor = nodo.IsOnline ? COnline
                : nodo.IsConfigured ? COffline
                : CUnassigned;

            card.Paint += (s, ev) =>
            {
                using (var acc = new SolidBrush(accentColor))
                    ev.Graphics.FillRectangle(acc, 0, 0, 4, card.Height);
                using (var pen = new Pen(nodo.IsOnline ? Color.FromArgb(0, 80, 40) : CBorder, 1))
                    ev.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            // ── LED indicator (circle) ──────────────────────────────────
            var ledPanel = new Panel
            {
                Size = new Size(40, 40),
                Location = new Point(20, 30),
                BackColor = Color.Transparent
            };
            ledPanel.Paint += (s, ev) =>
            {
                ev.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color ledColor = nodo.IsOnline ? COnline
                    : nodo.IsConfigured ? COffline
                    : CUnassigned;
                using (var b = new SolidBrush(ledColor))
                    ev.Graphics.FillEllipse(b, 4, 4, 32, 32);

                // Glow effect for online nodes.
                if (nodo.IsOnline)
                {
                    using (var glow = new SolidBrush(Color.FromArgb(40, 0, 230, 118)))
                        ev.Graphics.FillEllipse(glow, 0, 0, 40, 40);
                }

                // Icon in center.
                string icon = nodo.IsOnline ? "\u2713" : nodo.IsConfigured ? "\u2717" : "?";
                using (var f = new Font("Segoe UI", 14f, FontStyle.Bold))
                using (var br = new SolidBrush(CBgDark))
                {
                    var sz = ev.Graphics.MeasureString(icon, f);
                    ev.Graphics.DrawString(icon, f, br,
                        (40 - sz.Width) / 2f, (40 - sz.Height) / 2f);
                }
            };
            card.Controls.Add(ledPanel);

            // ── UID ─────────────────────────────────────────────────────
            var lblUid = new Label
            {
                Text = nodo.Uid,
                Font = new Font("Consolas", 13f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(72, 12),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblUid);

            // ── Status badge ────────────────────────────────────────────
            string statusText;
            Color statusBg;
            Color statusFg;

            if (nodo.IsOnline)
            {
                statusText = " ONLINE ";
                statusBg = COnline;
                statusFg = CBgDark;
            }
            else if (nodo.IsConfigured)
            {
                statusText = " OFFLINE ";
                statusBg = CRed;
                statusFg = CText;
            }
            else
            {
                statusText = " SIN ASIGNAR ";
                statusBg = CUnassigned;
                statusFg = CBgDark;
            }

            var badge = new Label
            {
                Text = statusText,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = statusFg,
                BackColor = statusBg,
                Location = new Point(lblUid.Right + 10, 16),
                AutoSize = true,
                Padding = new Padding(4, 1, 4, 1)
            };
            card.Controls.Add(badge);

            // ── Stats row ───────────────────────────────────────────────
            int statsY = 42;
            int statsX = 72;

            AddStatPair(card, ref statsX, statsY, "SENSORES",
                nodo.SensoresAsignados.ToString(CultureInfo.InvariantCulture));
            AddStatPair(card, ref statsX, statsY, "TRENES",
                nodo.TrenesUsados.ToString(CultureInfo.InvariantCulture));

            if (nodo.IsOnline)
            {
                AddStatPair(card, ref statsX, statsY, "CANALES",
                    nodo.CanalesActivos + "/" + nodo.CanalesTotal);
                AddStatPair(card, ref statsX, statsY, "PULSOS",
                    nodo.PulsesTotal.ToString(CultureInfo.InvariantCulture));
            }

            // ── Last seen ───────────────────────────────────────────────
            string lastSeenText = "";
            if (nodo.LastSeen != DateTime.MinValue)
            {
                var elapsed = DateTime.UtcNow - nodo.LastSeen;
                if (elapsed.TotalSeconds < 5)
                    lastSeenText = "hace < 5s";
                else if (elapsed.TotalSeconds < 60)
                    lastSeenText = "hace " + (int)elapsed.TotalSeconds + "s";
                else if (elapsed.TotalMinutes < 60)
                    lastSeenText = "hace " + (int)elapsed.TotalMinutes + "m";
                else
                    lastSeenText = nodo.LastSeen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrEmpty(lastSeenText))
            {
                var lblSeen = new Label
                {
                    Text = "\U0001F551  " + lastSeenText,
                    Font = new Font("Segoe UI", 8.5f),
                    ForeColor = CTextFaint,
                    Location = new Point(72, 74),
                    AutoSize = true,
                    BackColor = Color.Transparent
                };
                card.Controls.Add(lblSeen);
            }

            // ── Perfil name (if configured) ─────────────────────────────
            if (nodo.IsConfigured && !string.IsNullOrEmpty(nodo.PerfilNombre))
            {
                var lblPerfil = new Label
                {
                    Text = nodo.PerfilNombre,
                    Font = new Font("Segoe UI", 8.5f),
                    ForeColor = CTextDim,
                    Location = new Point(220, 74),
                    AutoSize = true,
                    BackColor = Color.Transparent
                };
                card.Controls.Add(lblPerfil);
            }

            // ── Action buttons (right-aligned) ─────────────────────────
            int btnTop = (card.Height - 36) / 2;
            int btnRight = card.Width - 20;

            if (nodo.IsConfigured)
            {
                var btnReplace = MkPillButton("\U0001F504  REEMPLAZAR",
                    Color.FromArgb(50, 50, 55), CText);
                btnReplace.Size = new Size(140, 36);
                btnReplace.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                btnReplace.Location = new Point(btnRight - btnReplace.Width, btnTop);
                string capturedUid = nodo.Uid;
                btnReplace.Click += (s, ev) => ReplaceNode(capturedUid);
                card.Controls.Add(btnReplace);
            }
            else
            {
                // Unassigned node — button to add it to the implemento.
                var btnAssign = MkPillButton("\u2795  ASIGNAR AL IMPLEMENTO",
                    CAccent, CBgDark);
                btnAssign.Size = new Size(210, 36);
                btnAssign.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                btnAssign.Location = new Point(btnRight - btnAssign.Width, btnTop);
                string capturedUid = nodo.Uid;
                int capturedCanales = nodo.CanalesTotal;
                btnAssign.Click += (s, ev) => AssignNode(capturedUid, capturedCanales);
                card.Controls.Add(btnAssign);
            }

            return card;
        }

        private void AddStatPair(Control parent, ref int x, int y, string label, string value)
        {
            var lblVal = new Label
            {
                Text = value,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(x, y),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lblVal);

            var lblLbl = new Label
            {
                Text = label,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = CTextFaint,
                Location = new Point(x, y + 20),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lblLbl);

            x += Math.Max(lblVal.PreferredWidth, lblLbl.PreferredWidth) + 28;
        }

        // =====================================================================
        // Replace node (swap hardware UID preserving sensor mapping)
        // =====================================================================

        private void ReplaceNode(string oldUid)
        {
            // Collect available UIDs for replacement: online unassigned nodes,
            // or allow manual entry.
            List<string> candidates;
            lock (_lock)
            {
                candidates = _nodos.Values
                    .Where(n => n.IsOnline && !n.IsConfigured
                        && !string.Equals(n.Uid, oldUid, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Uid)
                    .OrderBy(u => u)
                    .ToList();
            }

            using (var dlg = new FormReplaceNode(oldUid, candidates))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string newUid = dlg.SelectedUid;
                if (string.IsNullOrWhiteSpace(newUid)) return;
                newUid = newUid.Trim();

                if (string.Equals(oldUid, newUid, StringComparison.OrdinalIgnoreCase))
                    return;

                // Update implemento JSON: replace all occurrences of oldUid with newUid.
                if (_implemento != null && _implemento.MapeoSensores != null)
                {
                    int replaced = 0;
                    foreach (var sensor in _implemento.MapeoSensores)
                    {
                        if (sensor != null && string.Equals(sensor.Uid, oldUid, StringComparison.OrdinalIgnoreCase))
                        {
                            sensor.Uid = newUid;
                            replaced++;
                        }
                    }

                    if (replaced > 0)
                    {
                        SaveImplemento();
                        BuildNodoIndex();
                        RebuildCards();

                        MessageBox.Show(this,
                            "Nodo reemplazado exitosamente.\n\n"
                            + oldUid + " \u2192 " + newUid + "\n"
                            + replaced + " sensor(es) actualizado(s).\n\n"
                            + "Reinici\u00E1 VistaX para que tome efecto.",
                            "Nodo Reemplazado",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }

        private void SaveImplemento()
        {
            try
            {
                string path = _cfg.ImplementoJsonPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                string json = JsonSerializer.Serialize(_implemento, opts);
                File.WriteAllText(path, json);
                System.Diagnostics.Debug.WriteLine("[VistaX-Nodos] Implemento guardado: " + path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX-Nodos] Error guardando: " + ex.Message);
                MessageBox.Show(this, "Error al guardar: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =====================================================================
        // Assign unassigned node to implemento
        // =====================================================================

        private void AssignNode(string uid, int canalesDetectados)
        {
            if (_implemento == null)
            {
                MessageBox.Show(this, "No hay implemento activo. Seleccion\u00E1 uno desde Configurar VistaX.",
                    "Nodos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int canales = canalesDetectados > 0 ? canalesDetectados : 8;

            using (var dlg = new FormAssignNode(uid, canales, _implemento))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                int trenId = dlg.SelectedTrenId;
                int canalesToAdd = dlg.SelectedCanales;

                // Find next available bajada for this tren.
                int maxBajada = 0;
                if (_implemento.MapeoSensores != null)
                {
                    foreach (var s in _implemento.MapeoSensores)
                    {
                        if (s == null) continue;
                        int st = s.Tren > 0 ? s.Tren : 1;
                        if (st == trenId && string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                        {
                            if (s.Bajada > maxBajada) maxBajada = s.Bajada;
                        }
                    }
                }

                if (_implemento.MapeoSensores == null)
                    _implemento.MapeoSensores = new List<SensorConfig>();

                for (int c = 1; c <= canalesToAdd; c++)
                {
                    int bajada = maxBajada + c;
                    _implemento.MapeoSensores.Add(new SensorConfig
                    {
                        Uid = uid,
                        Cable = c,
                        Pin = c - 1,
                        Bajada = bajada,
                        Tipo = "semilla",
                        Nombre = "Surco " + bajada,
                        Tren = trenId,
                        IsActive = true
                    });
                }

                SaveImplemento();
                LoadImplemento();
                BuildNodoIndex();
                RebuildCards();

                MessageBox.Show(this,
                    "Nodo " + uid + " asignado exitosamente.\n"
                    + canalesToAdd + " sensor(es) agregados al tren " + trenId
                    + ", surcos " + (maxBajada + 1) + " a " + (maxBajada + canalesToAdd) + ".",
                    "Nodo Asignado", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static Button MkPillButton(string text, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = fg,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private Panel MkEmptyState(string msg)
        {
            var p = new Panel
            {
                Size = new Size(_list.ClientSize.Width - 36, 140),
                BackColor = Color.FromArgb(18, 18, 20)
            };
            var lbl = new Label
            {
                Text = msg,
                Font = new Font("Segoe UI", 10f),
                ForeColor = CTextDim,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            p.Controls.Add(lbl);
            return p;
        }
    }

    // =========================================================================
    // FormReplaceNode - Mini dialog for selecting replacement UID
    // =========================================================================

    internal class FormReplaceNode : Form
    {
        private static readonly Color CBgDark = Color.FromArgb(0, 0, 0);
        private static readonly Color CBgPanel = Color.FromArgb(20, 20, 20);
        private static readonly Color CAccent = Color.FromArgb(0, 230, 118);
        private static readonly Color CText = Color.FromArgb(235, 235, 235);
        private static readonly Color CTextDim = Color.FromArgb(120, 120, 120);
        private static readonly Color CBorder = Color.FromArgb(40, 40, 40);

        public string SelectedUid { get; private set; }

        private readonly string _oldUid;
        private ComboBox _combo;
        private TextBox _txtManual;

        public FormReplaceNode(string oldUid, List<string> candidates)
        {
            _oldUid = oldUid;

            Text = "Reemplazar Nodo";
            Size = new Size(460, 300);
            MinimumSize = new Size(400, 260);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = CBgDark;
            ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            var lblTitle = new Label
            {
                Text = "\U0001F504  REEMPLAZAR NODO",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(24, 20),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            Controls.Add(lblTitle);

            var lblOld = new Label
            {
                Text = "Nodo actual:  " + _oldUid,
                Font = new Font("Consolas", 10f),
                ForeColor = CTextDim,
                Location = new Point(24, 56),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            Controls.Add(lblOld);

            var lblNew = new Label
            {
                Text = "Nuevo UID (detectado online):",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = CTextDim,
                Location = new Point(24, 92),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            Controls.Add(lblNew);

            _combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CBgPanel,
                ForeColor = CText,
                Font = new Font("Consolas", 11f),
                Location = new Point(24, 116),
                Size = new Size(Width - 48, 30)
            };
            if (candidates != null && candidates.Count > 0)
            {
                foreach (var c in candidates)
                    _combo.Items.Add(c);
                _combo.SelectedIndex = 0;
            }
            else
            {
                _combo.Items.Add("(ning\u00FAn nodo disponible)");
                _combo.SelectedIndex = 0;
                _combo.Enabled = false;
            }
            Controls.Add(_combo);

            var lblManual = new Label
            {
                Text = "O ingres\u00E1 el UID manualmente:",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = CTextDim,
                Location = new Point(24, 158),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            Controls.Add(lblManual);

            _txtManual = new TextBox
            {
                BackColor = CBgPanel,
                ForeColor = CAccent,
                Font = new Font("Consolas", 11f),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(24, 182),
                Size = new Size(Width - 48, 28)
            };
            Controls.Add(_txtManual);

            var btnOk = new Button
            {
                Text = "\u2713  REEMPLAZAR",
                FlatStyle = FlatStyle.Flat,
                BackColor = CAccent,
                ForeColor = CBgDark,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Size = new Size(150, 36),
                Location = new Point(Width - 174, 234),
                Cursor = Cursors.Hand
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) =>
            {
                // Prefer manual entry if not empty; otherwise combo.
                string uid = _txtManual.Text.Trim();
                if (string.IsNullOrEmpty(uid) && _combo.Enabled && _combo.SelectedItem != null)
                    uid = _combo.SelectedItem.ToString();

                if (string.IsNullOrEmpty(uid)
                    || uid.StartsWith("(", StringComparison.Ordinal))
                {
                    return; // nothing valid selected
                }

                SelectedUid = uid;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnOk);

            var btnCancel = new Button
            {
                Text = "CANCELAR",
                FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel,
                ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Size = new Size(110, 36),
                Location = new Point(btnOk.Left - 120, 234),
                Cursor = Cursors.Hand,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(btnCancel);

            CancelButton = btnCancel;
            AcceptButton = btnOk;
        }
    }

    // =========================================================================
    // FormAssignNode - Assign a new node: choose tren + canales
    // =========================================================================

    internal class FormAssignNode : Form
    {
        private static readonly Color CBgDark = Color.FromArgb(0, 0, 0);
        private static readonly Color CBgPanel = Color.FromArgb(20, 20, 20);
        private static readonly Color CAccent = Color.FromArgb(0, 230, 118);
        private static readonly Color CText = Color.FromArgb(235, 235, 235);
        private static readonly Color CTextDim = Color.FromArgb(120, 120, 120);
        private static readonly Color CBorder = Color.FromArgb(40, 40, 40);

        public int SelectedTrenId { get; private set; }
        public int SelectedCanales { get; private set; }

        public FormAssignNode(string uid, int canalesDetectados, ImplementoConfig implemento)
        {
            Text = "Asignar Nodo";
            Size = new Size(480, 340);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = CBgDark; ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            Controls.Add(new Label
            {
                Text = "\u2795  ASIGNAR NODO AL IMPLEMENTO",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = CText, Location = new Point(24, 16),
                AutoSize = true, BackColor = Color.Transparent
            });

            Controls.Add(new Label
            {
                Text = "Nodo:  " + uid,
                Font = new Font("Consolas", 11f),
                ForeColor = CAccent, Location = new Point(24, 52),
                AutoSize = true, BackColor = Color.Transparent
            });

            // ── Tren selector ───────────────────────────────────────────
            Controls.Add(new Label
            {
                Text = "Asignar al tren:",
                Font = new Font("Segoe UI", 10f), ForeColor = CTextDim,
                Location = new Point(24, 92), AutoSize = true, BackColor = Color.Transparent
            });

            var cboTren = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CBgPanel, ForeColor = CText,
                Font = new Font("Segoe UI", 11f),
                Location = new Point(24, 116), Size = new Size(Width - 48, 30)
            };

            // Populate with trenes from implemento.
            var trenIds = new List<int>();
            if (implemento != null && implemento.Trenes != null && implemento.Trenes.Count > 0)
            {
                foreach (var t in implemento.Trenes)
                {
                    string label = "Tren " + t.Id + " \u2014 " + (t.Nombre ?? "?")
                        + " (" + t.Surcos + " surcos)";

                    // Count existing sensors for this tren.
                    int existingSensors = 0;
                    if (implemento.MapeoSensores != null)
                    {
                        foreach (var s in implemento.MapeoSensores)
                        {
                            if (s != null && (s.Tren > 0 ? s.Tren : 1) == t.Id
                                && string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                                existingSensors++;
                        }
                    }
                    label += " [" + existingSensors + " sensores asignados]";

                    cboTren.Items.Add(label);
                    trenIds.Add(t.Id);
                }
            }
            else
            {
                cboTren.Items.Add("Tren 1 \u2014 (default)");
                trenIds.Add(1);
            }
            if (cboTren.Items.Count > 0) cboTren.SelectedIndex = 0;
            Controls.Add(cboTren);

            // ── Canales ─────────────────────────────────────────────────
            Controls.Add(new Label
            {
                Text = "Cantidad de canales (sensores) a crear:",
                Font = new Font("Segoe UI", 10f), ForeColor = CTextDim,
                Location = new Point(24, 160), AutoSize = true, BackColor = Color.Transparent
            });

            var numCanales = new NumericUpDown
            {
                Minimum = 1, Maximum = 16,
                Value = Math.Max(1, Math.Min(16, canalesDetectados > 0 ? canalesDetectados : 8)),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(24, 184), Size = new Size(100, 34),
                TextAlign = HorizontalAlignment.Center
            };
            Controls.Add(numCanales);

            Controls.Add(new Label
            {
                Text = "cables (c1 a c" + (canalesDetectados > 0 ? canalesDetectados : 8) + " detectados)",
                Font = new Font("Segoe UI", 9f), ForeColor = CTextDim,
                Location = new Point(134, 192), AutoSize = true, BackColor = Color.Transparent
            });

            // ── Preview label ───────────────────────────────────────────
            var lblPreview = new Label
            {
                Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(24, 228), Size = new Size(Width - 48, 20)
            };
            Controls.Add(lblPreview);

            Action updatePreview = () =>
            {
                int tId = cboTren.SelectedIndex >= 0 && cboTren.SelectedIndex < trenIds.Count
                    ? trenIds[cboTren.SelectedIndex] : 1;
                int n = (int)numCanales.Value;

                int maxB = 0;
                if (implemento != null && implemento.MapeoSensores != null)
                {
                    foreach (var s in implemento.MapeoSensores)
                    {
                        if (s == null) continue;
                        if ((s.Tren > 0 ? s.Tren : 1) == tId
                            && string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                        {
                            if (s.Bajada > maxB) maxB = s.Bajada;
                        }
                    }
                }

                lblPreview.Text = "Se crear\u00E1n surcos " + (maxB + 1) + " a " + (maxB + n)
                    + " en tren " + tId;
            };
            cboTren.SelectedIndexChanged += (s, ev) => updatePreview();
            numCanales.ValueChanged += (s, ev) => updatePreview();
            updatePreview();

            // ── Buttons ─────────────────────────────────────────────────
            var btnOk = new Button
            {
                Text = "\u2713  ASIGNAR", FlatStyle = FlatStyle.Flat,
                BackColor = CAccent, ForeColor = CBgDark,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Size = new Size(130, 36), Location = new Point(Width - 154, 274),
                Cursor = Cursors.Hand
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, ev) =>
            {
                SelectedTrenId = cboTren.SelectedIndex >= 0 && cboTren.SelectedIndex < trenIds.Count
                    ? trenIds[cboTren.SelectedIndex] : 1;
                SelectedCanales = (int)numCanales.Value;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnOk);

            var btnCancel = new Button
            {
                Text = "CANCELAR", FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Size = new Size(100, 36), Location = new Point(btnOk.Left - 110, 274),
                Cursor = Cursors.Hand, DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(btnCancel);

            CancelButton = btnCancel;
            AcceptButton = btnOk;
        }
    }
}
