// ============================================================================
// FormVistaXConfig.cs - Configuración principal de VistaX (tema oscuro)
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXConfig.cs
// Target: net48 (C# 7.3)
//
// Expone vistaX.json + resumen/acceso directo a trenes y sensores del
// implemento activo. Estilo VistaX-Core (sin chrome, draggable, oscuro).
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

namespace AgroParallel.VistaX
{
    public class FormVistaXConfig : Form
    {
        // Paleta VistaX-Core (aliases a Theme).
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CBgField { get { return Theme.BgInput; } }
        private static Color CBgCard { get { return Theme.BgCard; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CAccentDim { get { return Theme.AccentDim; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CTextFaint { get { return Theme.TextFaint; } }
        private static Color CBorder { get { return Theme.Border; } }
        private static Color CRed { get { return Theme.Error; } }

        private readonly VistaXConfig _cfg;
        private AgOpenGPS.FormGPS _formGPS;

        // ── Controls ────────────────────────────────────────────────────
        private CheckBox _chkEnabled;
        private TextBox _txtHost;
        private NumericUpDown _numPort;
        private TextBox _txtClientId;
        private TextBox _txtTopicTele;
        private TextBox _txtTopicSections;
        private TextBox _txtTopicSpeed;
        private TextBox _txtImplemento;
        private ComboBox _cmbMetodo;
        private NumericUpDown _numUmbral;
        private NumericUpDown _numConfirm;
        private NumericUpDown _numPanelH;
        private NumericUpDown _numPanelW;
        private NumericUpDown _numPanelBottom;

        // Implemento summary labels.
        private Label _lblImplNombre;
        private Label _lblImplTrenes;
        private Label _lblImplSensores;
        private Panel _implSummaryPanel;

        public FormVistaXConfig(VistaXConfig cfg) : this(cfg, null) { }

        public FormVistaXConfig(VistaXConfig cfg, AgOpenGPS.FormGPS formGPS)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            _formGPS = formGPS;
            BuildUI();
            LoadFromConfig();
        }

        // =====================================================================
        // UI Construction
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "VistaX — Configuracion";
            Size = new Size(680, 780);
            MinimumSize = new Size(600, 680);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CBgDark;
            ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            KeyDown += (s, ev) => { if (ev.KeyCode == Keys.Escape) Close(); };
            Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // ── Top bar (draggable) ─────────────────────────────────────
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
                if (ev.Button == MouseButtons.Left) { _dragging = true; _dragStart = ev.Location; }
            };
            topBar.MouseMove += (s, ev) =>
            {
                if (_dragging) Location = new Point(Location.X + ev.X - _dragStart.X, Location.Y + ev.Y - _dragStart.Y);
            };
            topBar.MouseUp += (s, ev) => _dragging = false;

            topBar.Controls.Add(new Label
            {
                Text = "VistaX", Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                ForeColor = CText, Location = new Point(24, 14),
                AutoSize = true, BackColor = Color.Transparent
            });
            topBar.Controls.Add(new Label
            {
                Text = "CONFIGURACION", Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CTextDim, Location = new Point(116, 20),
                AutoSize = true, BackColor = Color.Transparent
            });

            var btnX = new Button
            {
                Text = "\u2715", FlatStyle = FlatStyle.Flat, BackColor = CBgPanel,
                ForeColor = CTextDim, Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Size = new Size(40, 32), Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnX.FlatAppearance.BorderSize = 0;
            btnX.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 40, 40);
            btnX.Click += (s, ev) => Close();
            topBar.Controls.Add(btnX);
            topBar.Resize += (s, ev) => btnX.Location = new Point(topBar.Width - btnX.Width - 4, 12);
            Controls.Add(topBar);

            // ── Scrollable body ─────────────────────────────────────────
            var body = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = CBgDark,
                Padding = new Padding(0, 6, 0, 6)
            };
            Controls.Add(body);
            body.BringToFront();

            int lx = 28;            // label X
            int fx = 230;           // field X
            int fw = 380;           // field width
            int y = 14;

            // ── Habilitado ──────────────────────────────────────────────
            _chkEnabled = new CheckBox
            {
                Text = "  HABILITADO",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.Transparent,
                Location = new Point(lx, y), AutoSize = true
            };
            _chkEnabled.CheckedChanged += (s, ev) =>
                _chkEnabled.ForeColor = _chkEnabled.Checked ? CAccent : CRed;
            body.Controls.Add(_chkEnabled);
            y += 42;

            // ── Implemento (resumen + accesos directos) ─────────────────
            AddSectionHeader(body, "\U0001F69C  IMPLEMENTO ACTIVO", y);
            y += 26;

            _implSummaryPanel = new Panel
            {
                Location = new Point(lx, y),
                Size = new Size(fw + fx - lx, 140),
                BackColor = CBgCard
            };
            _implSummaryPanel.Paint += (s, ev) =>
            {
                using (var acc = new SolidBrush(CAccent))
                    ev.Graphics.FillRectangle(acc, 0, 0, 4, _implSummaryPanel.Height);
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, _implSummaryPanel.Width - 1, _implSummaryPanel.Height - 1);
            };

            _lblImplNombre = new Label
            {
                Text = "—", Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = CText, BackColor = Color.Transparent,
                Location = new Point(16, 10), AutoSize = true
            };
            _implSummaryPanel.Controls.Add(_lblImplNombre);

            _lblImplTrenes = new Label
            {
                Text = "", Font = new Font("Segoe UI", 9.5f),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(16, 38), AutoSize = true
            };
            _implSummaryPanel.Controls.Add(_lblImplTrenes);

            _lblImplSensores = new Label
            {
                Text = "", Font = new Font("Segoe UI", 9.5f),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(16, 58), AutoSize = true
            };
            _implSummaryPanel.Controls.Add(_lblImplSensores);

            // Editable: surcos / secciones AOG / ancho.
            int editY = 78;
            AddImplNumeric(_implSummaryPanel, "SURCOS", 16, editY, 1, 200,
                () => LoadImplSetupInt("total_surcos"),
                v => SaveImplSetupInt("total_surcos", v));
            AddImplNumeric(_implSummaryPanel, "SECCIONES AOG", 130, editY, 1, 64,
                () => LoadImplSetupInt("secciones_aog"),
                v => SaveImplSetupInt("secciones_aog", v));
            // Ancho con decimales.
            _implSummaryPanel.Controls.Add(new Label
            {
                Text = "ANCHO (m)", Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = CTextFaint, BackColor = Color.Transparent,
                Location = new Point(280, editY - 2), AutoSize = true
            });
            var txtAncho = new TextBox
            {
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(280, editY + 12), Size = new Size(80, 24),
                TextAlign = HorizontalAlignment.Center
            };
            double anchoActual = LoadImplSetupDouble("ancho_implemento");
            txtAncho.Text = anchoActual.ToString("F2", CultureInfo.InvariantCulture);
            txtAncho.Leave += (s3, ev3) =>
            {
                double val;
                if (double.TryParse(txtAncho.Text.Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out val) && val > 0)
                {
                    SaveImplSetupDecimal("ancho_implemento", val);
                    UpdateDistLabel();
                }
            };
            _implSummaryPanel.Controls.Add(txtAncho);

            // Distancia entre surcos (calculada).
            var _lblDist = new Label
            {
                Name = "lblDist",
                Font = new Font("Segoe UI", 8f),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(370, editY + 14), AutoSize = true
            };
            _implSummaryPanel.Controls.Add(_lblDist);
            // Store reference for UpdateDistLabel.
            _implSummaryPanel.Tag = _lblDist;

            // Botón para leer geometría desde AOG automáticamente.
            int btnY = 108;
            var btnAutoAOG = MkPillButton("\u21BB  LEER DE AOG", CAccentDim, CText);
            btnAutoAOG.Size = new Size(140, 26);
            btnAutoAOG.Location = new Point(16, btnY);
            btnAutoAOG.Click += (s, ev) => ReadGeometryFromAOG();
            _implSummaryPanel.Controls.Add(btnAutoAOG);

            body.Controls.Add(_implSummaryPanel);
            y += 150;

            // ── Archivo JSON (editable) ─────────────────────────────────
            AddRow(body, "Archivo JSON:", lx, fx, y, out _txtImplemento, fw - 40);
            var btnBrowse = new Button
            {
                Text = "...", FlatStyle = FlatStyle.Flat,
                BackColor = CBgField, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Size = new Size(36, 26), Cursor = Cursors.Hand,
                Location = new Point(fx + fw - 36, y)
            };
            btnBrowse.FlatAppearance.BorderSize = 0;
            btnBrowse.Click += (s, ev) => OnBrowseImplemento();
            body.Controls.Add(btnBrowse);
            _txtImplemento.TextChanged += (s, ev) => RefreshImplementoSummary();
            y += 36;

            // ── Sensores especiales ─────────────────────────────────────
            y += 8;
            AddSectionHeader(body, "\U0001F50C  SENSORES ESPECIALES", y);
            y += 26;

            // Un numeric por cada tipo no-semilla.
            var specialTypes = new[] {
                TipoSensor.FertiLinea, TipoSensor.FertiCostado,
                TipoSensor.BajadaHerramienta, TipoSensor.Turbina, TipoSensor.Tolva
            };
            foreach (var tipo in specialTypes)
            {
                Color tc = TipoSensor.GetColor(tipo);
                // Color dot.
                var dot = new Panel
                {
                    Location = new Point(lx, y + 4), Size = new Size(10, 10),
                    BackColor = Color.Transparent
                };
                var capturedColor = tc;
                dot.Paint += (s2, ev2) =>
                {
                    ev2.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(capturedColor))
                        ev2.Graphics.FillEllipse(b, 0, 0, 9, 9);
                };
                body.Controls.Add(dot);

                body.Controls.Add(new Label
                {
                    Text = TipoSensor.NombreAmigable(tipo) + ":",
                    Font = new Font("Segoe UI", 9f), ForeColor = CTextDim,
                    BackColor = Color.Transparent,
                    Location = new Point(lx + 16, y + 2), AutoSize = true
                });

                string capturedTipo = tipo;
                var numSpec = new NumericUpDown
                {
                    Minimum = 0, Maximum = 200,
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = CAccent, BackColor = CBgField,
                    BorderStyle = BorderStyle.FixedSingle,
                    Location = new Point(fx, y), Size = new Size(80, 24)
                };
                // Load count from implemento.
                int currentCount = 0;
                try
                {
                    var impl = LoadCurrentImplemento();
                    if (impl != null && impl.MapeoSensores != null)
                    {
                        foreach (var ss in impl.MapeoSensores)
                            if (ss != null && string.Equals(ss.Tipo, capturedTipo, StringComparison.OrdinalIgnoreCase))
                                currentCount++;
                        numSpec.Value = Math.Min(numSpec.Maximum, currentCount);
                    }
                }
                catch { }

                // Guardar al cambiar: agregar o quitar sensores del tipo.
                numSpec.ValueChanged += (s2, ev2) =>
                {
                    try { UpdateSpecialSensorCount(capturedTipo, (int)numSpec.Value); }
                    catch { }
                };
                body.Controls.Add(numSpec);
                y += 30;
            }

            // ── Monitoreo ───────────────────────────────────────────────
            y += 8;
            AddSectionHeader(body, "\u26A1  MONITOREO", y);
            y += 26;

            body.Controls.Add(new Label
            {
                Text = "M\u00E9todo inicio:", Font = new Font("Segoe UI", 9.5f),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(lx, y + 2), AutoSize = true
            });
            _cmbMetodo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CBgField, ForeColor = CText,
                Font = new Font("Segoe UI", 10f),
                Location = new Point(fx, y), Size = new Size(200, 26)
            };
            _cmbMetodo.Items.AddRange(new object[] { "sensores", "herramienta", "pintando", "manual" });
            body.Controls.Add(_cmbMetodo);
            y += 34;
            y = AddNumericRow(body, "Umbral sensores activos:", lx, fx, y, 0, 200, out _numUmbral);
            y = AddNumericRow(body, "Tiempo confirmaci\u00F3n (ms):", lx, fx, y, 0, 60000, out _numConfirm);

            // ── Avanzado (colapsable — Broker, Topics, Layout) ──────────
            y += 12;
            var _advancedPanel = new Panel
            {
                Location = new Point(0, y),
                Size = new Size(fw + fx, 0), // starts collapsed
                BackColor = CBgDark,
                Tag = false // collapsed state
            };
            body.Controls.Add(_advancedPanel);

            var btnAdvanced = MkPillButton("\u25B6  Avanzado (Instalador)", Color.FromArgb(30, 30, 34), CTextFaint);
            btnAdvanced.Size = new Size(220, 26);
            btnAdvanced.Location = new Point(lx, y);
            var advPanel = _advancedPanel; // capture
            btnAdvanced.Click += (s, ev) =>
            {
                bool expanded = (bool)advPanel.Tag;
                if (expanded)
                {
                    advPanel.Size = new Size(advPanel.Width, 0);
                    advPanel.Tag = false;
                    btnAdvanced.Text = "\u25B6  Avanzado (Instalador)";
                }
                else
                {
                    advPanel.Size = new Size(advPanel.Width, advPanel.PreferredSize.Height);
                    advPanel.Tag = true;
                    btnAdvanced.Text = "\u25BC  Avanzado (Instalador)";
                }
            };
            body.Controls.Add(btnAdvanced);
            y += 30;

            // Build advanced panel content.
            int ay = 6;
            advPanel.Controls.Add(new Label { Text = "BROKER MQTT", Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = CAccentDim, Location = new Point(lx, ay), AutoSize = true, BackColor = Color.Transparent });
            ay += 20;
            ay = AddRow(advPanel, "Host:", lx, fx, ay, out _txtHost, fw);
            ay = AddNumericRow(advPanel, "Puerto:", lx, fx, ay, 1, 65535, out _numPort);
            ay = AddRow(advPanel, "Client ID:", lx, fx, ay, out _txtClientId, fw);

            advPanel.Controls.Add(new Label { Text = "TOPICS MQTT", Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = CAccentDim, Location = new Point(lx, ay + 4), AutoSize = true, BackColor = Color.Transparent });
            ay += 24;
            ay = AddRow(advPanel, "Telemetr\u00EDa:", lx, fx, ay, out _txtTopicTele, fw);
            ay = AddRow(advPanel, "Secciones:", lx, fx, ay, out _txtTopicSections, fw);
            ay = AddRow(advPanel, "Velocidad:", lx, fx, ay, out _txtTopicSpeed, fw);

            advPanel.Controls.Add(new Label { Text = "LAYOUT DEL PANEL", Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = CAccentDim, Location = new Point(lx, ay + 4), AutoSize = true, BackColor = Color.Transparent });
            ay += 24;
            ay = AddNumericRow(advPanel, "Alto (px):", lx, fx, ay, 40, 800, out _numPanelH);
            ay = AddNumericRow(advPanel, "Ancho (%):", lx, fx, ay, 10, 100, out _numPanelW);
            ay = AddNumericRow(advPanel, "Margen inferior (px):", lx, fx, ay, 0, 600, out _numPanelBottom);

            advPanel.Size = new Size(fw + fx, 0); // stays collapsed
            // Recalculate y after advanced (collapsed = 0 height).
            _advancedPanel.Location = new Point(0, y);
            y += 4;

            // ── Registro / Logging ──────────────────────────────────────
            y += 8;
            AddSectionHeader(body, "\U0001F4BE  REGISTRO DE DATOS", y);
            y += 26;

            var chkLog = new CheckBox
            {
                Text = "  Grabar NDJSON durante monitoreo (auto-export a SHP)",
                Checked = _cfg.LogToFieldRecord,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = CText, BackColor = Color.Transparent,
                Location = new Point(lx, y), AutoSize = true
            };
            chkLog.CheckedChanged += (s2, ev2) => _cfg.LogToFieldRecord = chkLog.Checked;
            body.Controls.Add(chkLog);
            y += 30;

            TextBox txtLogDrive;
            y = AddRow(body, "Guardar en (vac\u00EDo = campo):", lx, fx, y, out txtLogDrive, fw - 40);
            txtLogDrive.Text = _cfg.LogOutputDrive ?? "";
            txtLogDrive.TextChanged += (s2, ev2) => _cfg.LogOutputDrive = txtLogDrive.Text.Trim();

            var btnBrowseDrive = new Button
            {
                Text = "...", FlatStyle = FlatStyle.Flat,
                BackColor = CBgField, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Size = new Size(36, 26), Cursor = Cursors.Hand,
                Location = new Point(fx + fw - 36, y - 32)
            };
            btnBrowseDrive.FlatAppearance.BorderSize = 0;
            btnBrowseDrive.Click += (s2, ev2) =>
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "Seleccionar partici\u00F3n/carpeta para logs VistaX";
                    if (!string.IsNullOrEmpty(txtLogDrive.Text) && Directory.Exists(txtLogDrive.Text))
                        fbd.SelectedPath = txtLogDrive.Text;
                    if (fbd.ShowDialog(this) == DialogResult.OK)
                        txtLogDrive.Text = fbd.SelectedPath;
                }
            };
            body.Controls.Add(btnBrowseDrive);

            body.Controls.Add(new Label
            {
                Text = "Ej: D:\\ o E:\\VistaX_Logs \u2014 dej\u00E1 vac\u00EDo para guardar en el campo actual",
                Font = new Font("Segoe UI", 8f), ForeColor = CTextFaint,
                BackColor = Color.Transparent,
                Location = new Point(lx, y), AutoSize = true
            });
            y += 24;

            // Spacer at bottom for scrolling.
            body.Controls.Add(new Panel
            {
                Location = new Point(0, y + 10),
                Size = new Size(10, 20),
                BackColor = Color.Transparent
            });

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel
            {
                Dock = DockStyle.Bottom, Height = 50, BackColor = CBgPanel
            };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            footer.Controls.Add(new Label
            {
                Text = "VistaX nativo \u00B7 Configuraci\u00F3n",
                Font = new Font("Segoe UI", 8.5f), ForeColor = CTextFaint,
                Location = new Point(18, 17), AutoSize = true, BackColor = Color.Transparent
            });

            var btnApply = MkPillButton("\u2713  APLICAR", CAccent, CBgDark);
            btnApply.Size = new Size(120, 34);
            btnApply.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnApply.Click += (s, ev) => { ApplyConfig(); DialogResult = DialogResult.OK; Close(); };
            footer.Controls.Add(btnApply);

            var btnCancel = new Button
            {
                Text = "\u2715 CANCELAR", FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand, Size = new Size(110, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 30, 30);
            footer.Controls.Add(btnCancel);

            footer.Resize += (s, ev) =>
            {
                btnCancel.Location = new Point(footer.Width - btnCancel.Width - 18, 8);
                btnApply.Location = new Point(btnCancel.Left - btnApply.Width - 10, 8);
            };

            Controls.Add(footer);
            CancelButton = btnCancel;
            AcceptButton = btnApply;
        }

        // =====================================================================
        // UI Helpers
        // =====================================================================

        private void AddSectionHeader(Control parent, string text, int y)
        {
            parent.Controls.Add(new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.Transparent,
                Location = new Point(28, y), AutoSize = true
            });
            // Separator line.
            var line = new Panel
            {
                Location = new Point(28, y + 20),
                Size = new Size(580, 1),
                BackColor = CBorder
            };
            parent.Controls.Add(line);
        }

        private int AddRow(Control parent, string label, int lx, int fx, int y,
            out TextBox tb, int fw)
        {
            parent.Controls.Add(new Label
            {
                Text = label, Font = new Font("Segoe UI", 9.5f),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(lx, y + 3), AutoSize = true
            });
            tb = new TextBox
            {
                Font = new Font("Consolas", 10f),
                ForeColor = CText, BackColor = CBgField,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(fx, y), Size = new Size(fw, 26)
            };
            parent.Controls.Add(tb);
            return y + 32;
        }

        private int AddNumericRow(Control parent, string label, int lx, int fx, int y,
            int min, int max, out NumericUpDown nud)
        {
            parent.Controls.Add(new Label
            {
                Text = label, Font = new Font("Segoe UI", 9.5f),
                ForeColor = CTextDim, BackColor = Color.Transparent,
                Location = new Point(lx, y + 2), AutoSize = true
            });
            nud = new NumericUpDown
            {
                Minimum = min, Maximum = max,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = CBgField,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(fx, y), Size = new Size(100, 26)
            };
            parent.Controls.Add(nud);
            return y + 32;
        }

        private static Button MkPillButton(string text, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat,
                BackColor = bg, ForeColor = fg,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        // =====================================================================
        // Load / Apply
        // =====================================================================

        private void LoadFromConfig()
        {
            _chkEnabled.Checked = _cfg.Enabled;
            _txtHost.Text = _cfg.BrokerAddress ?? "127.0.0.1";
            _numPort.Value = Clamp(_cfg.BrokerPort, 1, 65535);
            _txtClientId.Text = _cfg.ClientId ?? "";
            _txtTopicTele.Text = _cfg.TelemetriaTopic ?? "";
            _txtTopicSections.Text = _cfg.SectionsTopic ?? "";
            _txtTopicSpeed.Text = _cfg.SpeedTopic ?? "";
            _txtImplemento.Text = _cfg.ImplementoJsonPath ?? "";

            string metodo = (_cfg.MetodoInicio ?? "sensores").Trim().ToLowerInvariant();
            int idx = _cmbMetodo.Items.IndexOf(metodo);
            _cmbMetodo.SelectedIndex = idx >= 0 ? idx : 0;

            _numUmbral.Value = Clamp(_cfg.UmbralSensoresActivos, 0, 200);
            _numConfirm.Value = Clamp(_cfg.TiempoConfirmacionMs, 0, 60000);
            _numPanelH.Value = Clamp(_cfg.PanelHeight > 0 ? _cfg.PanelHeight : 150, 40, 800);
            _numPanelW.Value = Clamp(_cfg.PanelWidthPercent > 0 ? _cfg.PanelWidthPercent : 60, 10, 100);
            _numPanelBottom.Value = Clamp(_cfg.PanelBottomMargin >= 0 ? _cfg.PanelBottomMargin : 20, 0, 600);

            RefreshImplementoSummary();
            UpdateDistLabel();
        }

        private void ApplyConfig()
        {
            _cfg.Enabled = _chkEnabled.Checked;
            _cfg.BrokerAddress = (_txtHost.Text ?? "").Trim();
            _cfg.BrokerPort = (int)_numPort.Value;
            _cfg.ClientId = (_txtClientId.Text ?? "").Trim();
            _cfg.TelemetriaTopic = (_txtTopicTele.Text ?? "").Trim();
            _cfg.SectionsTopic = (_txtTopicSections.Text ?? "").Trim();
            _cfg.SpeedTopic = (_txtTopicSpeed.Text ?? "").Trim();
            _cfg.ImplementoJsonPath = (_txtImplemento.Text ?? "").Trim();
            _cfg.MetodoInicio = (_cmbMetodo.SelectedItem as string) ?? "sensores";
            _cfg.UmbralSensoresActivos = (int)_numUmbral.Value;
            _cfg.TiempoConfirmacionMs = (int)_numConfirm.Value;
            _cfg.PanelHeight = (int)_numPanelH.Value;
            _cfg.PanelWidthPercent = (int)_numPanelW.Value;
            _cfg.PanelBottomMargin = (int)_numPanelBottom.Value;
            _cfg.Save();
        }

        // =====================================================================
        // Implemento summary
        // =====================================================================

        private void RefreshImplementoSummary()
        {
            string path = (_txtImplemento != null ? _txtImplemento.Text : _cfg.ImplementoJsonPath) ?? "";

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _lblImplNombre.Text = "(ning\u00FAn implemento seleccionado)";
                _lblImplNombre.ForeColor = CTextFaint;
                _lblImplTrenes.Text = "";
                _lblImplSensores.Text = "";
                return;
            }

            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var json = File.ReadAllText(path);
                var impl = JsonSerializer.Deserialize<ImplementoConfig>(json, opts);
                if (impl == null) throw new Exception("null");

                _lblImplNombre.Text = impl.Nombre ?? Path.GetFileNameWithoutExtension(path);
                _lblImplNombre.ForeColor = CText;

                // Trenes info.
                if (impl.Trenes != null && impl.Trenes.Count > 0)
                {
                    var parts = new List<string>();
                    int totalSurcos = 0;
                    foreach (var t in impl.Trenes)
                    {
                        parts.Add((t.Nombre ?? "T" + t.Id) + ": " + t.Surcos + " surcos");
                        totalSurcos += t.Surcos;
                    }
                    _lblImplTrenes.Text = impl.Trenes.Count + " tren(es) \u2014 "
                        + totalSurcos + " surcos  [" + string.Join(", ", parts.ToArray()) + "]";
                }
                else
                {
                    _lblImplTrenes.Text = "Sin trenes definidos";
                }

                // Sensores + secciones info.
                int semilla = 0, otros = 0;
                var nodos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (impl.MapeoSensores != null)
                {
                    foreach (var s in impl.MapeoSensores)
                    {
                        if (s == null) continue;
                        if (string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                            semilla++;
                        else
                            otros++;
                        if (!string.IsNullOrEmpty(s.Uid)) nodos.Add(s.Uid);
                    }
                }
                int totalSurcosSetup = impl.Setup != null ? impl.Setup.TotalSurcos : 0;
                int seccionesAOG = impl.Setup != null ? impl.Setup.SeccionesAOG : 0;
                string ratio = "";
                if (totalSurcosSetup > 0 && semilla > 0 && totalSurcosSetup != semilla)
                    ratio = " (1 sensor cada " + (totalSurcosSetup / Math.Max(1, semilla)) + " surcos)";

                int surcosDeTrenes = 0;
                if (impl.Trenes != null)
                    foreach (var t2 in impl.Trenes) surcosDeTrenes += t2.Surcos;
                int surcosFinal = totalSurcosSetup > 0 ? totalSurcosSetup : surcosDeTrenes;

                _lblImplSensores.Text = surcosFinal
                    + " surcos \u00B7 " + semilla + " sensores" + ratio
                    + " \u00B7 " + (seccionesAOG > 0 ? seccionesAOG.ToString() : "?") + " secciones AOG"
                    + " \u00B7 " + nodos.Count + " nodo(s)";
            }
            catch
            {
                _lblImplNombre.Text = "Error leyendo: " + Path.GetFileName(path);
                _lblImplNombre.ForeColor = CRed;
                _lblImplTrenes.Text = "";
                _lblImplSensores.Text = "";
            }
        }

        // =====================================================================
        // Implemento numeric editors (embedded in summary card)
        // =====================================================================

        private void AddImplNumeric(Panel parent, string label, int x, int y,
            int min, int max, Func<int> loader, Action<int> saver)
        {
            parent.Controls.Add(new Label
            {
                Text = label, Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = CTextFaint, BackColor = Color.Transparent,
                Location = new Point(x, y - 2), AutoSize = true
            });
            var num = new NumericUpDown
            {
                Minimum = min, Maximum = max,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(x, y + 12), Size = new Size(80, 24),
                TextAlign = HorizontalAlignment.Center
            };
            try { num.Value = Math.Max(min, Math.Min(max, loader())); } catch { }
            num.ValueChanged += (s, ev) =>
            {
                try { saver((int)num.Value); } catch { }
            };
            parent.Controls.Add(num);
        }

        private int LoadImplSetupInt(string field)
        {
            var impl = LoadCurrentImplemento();
            if (impl == null || impl.Setup == null) return 0;
            switch (field)
            {
                case "total_surcos": return impl.Setup.TotalSurcos;
                case "secciones_aog": return impl.Setup.SeccionesAOG;
                default: return 0;
            }
        }

        private double LoadImplSetupDouble(string field)
        {
            var impl = LoadCurrentImplemento();
            if (impl == null || impl.Setup == null) return 0;
            switch (field)
            {
                case "ancho_implemento": return impl.Setup.AnchoImplemento;
                default: return 0;
            }
        }

        private void SaveImplSetupInt(string field, int value)
        {
            var impl = LoadCurrentImplemento();
            if (impl == null) return;
            if (impl.Setup == null) impl.Setup = new ImplementoSetup();
            switch (field)
            {
                case "total_surcos": impl.Setup.TotalSurcos = value; break;
                case "secciones_aog": impl.Setup.SeccionesAOG = value; break;
            }
            SaveCurrentImplemento(impl);
            RefreshImplementoSummary();
        }

        private void SaveImplSetupDecimal(string field, double value)
        {
            var impl = LoadCurrentImplemento();
            if (impl == null) return;
            if (impl.Setup == null) impl.Setup = new ImplementoSetup();
            switch (field)
            {
                case "ancho_implemento":
                    impl.Setup.AnchoImplemento = Math.Round(value, 3);
                    // Recalcular distancia entre surcos.
                    int totalSurcos = impl.Setup.TotalSurcos;
                    if (totalSurcos <= 0 && impl.Trenes != null)
                        foreach (var t in impl.Trenes) totalSurcos += t.Surcos;
                    if (totalSurcos > 0)
                        impl.Setup.DistanciaEntreSurcos = Math.Round(value / totalSurcos, 4);
                    break;
            }
            SaveCurrentImplemento(impl);
        }

        private void UpdateDistLabel()
        {
            if (_implSummaryPanel == null) return;
            var lbl = _implSummaryPanel.Tag as Label;
            if (lbl == null) return;

            var impl = LoadCurrentImplemento();
            if (impl == null || impl.Setup == null) { lbl.Text = ""; return; }

            double dist = impl.Setup.DistanciaEntreSurcos;
            if (dist > 0)
                lbl.Text = "(" + (dist * 100).ToString("F1", CultureInfo.InvariantCulture) + " cm entre surcos)";
            else
                lbl.Text = "";
        }

        private ImplementoConfig LoadCurrentImplemento()
        {
            try
            {
                string path = (_txtImplemento != null ? _txtImplemento.Text : _cfg.ImplementoJsonPath) ?? "";
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<ImplementoConfig>(File.ReadAllText(path), opts);
            }
            catch { return null; }
        }

        private void SaveCurrentImplemento(ImplementoConfig impl)
        {
            try
            {
                string path = (_txtImplemento != null ? _txtImplemento.Text : _cfg.ImplementoJsonPath) ?? "";
                if (string.IsNullOrEmpty(path)) return;
                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                File.WriteAllText(path, JsonSerializer.Serialize(impl, opts));
            }
            catch { }
        }

        private void ReadGeometryFromAOG()
        {
            try
            {
                // Buscar FormGPS: referencia directa, Owner, o recorrer parents.
                var parent = _formGPS;
                if (parent == null) parent = Owner as AgOpenGPS.FormGPS;
                if (parent == null) parent = FindFormGPS();
                if (parent == null)
                {
                    MessageBox.Show(this, "No se pudo acceder a AgOpenGPS.\nAbr\u00ED este panel desde el men\u00FA principal.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var impl = LoadCurrentImplemento();
                if (impl == null)
                {
                    MessageBox.Show(this, "No hay implemento cargado.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (impl.Setup == null) impl.Setup = new ImplementoSetup();

                // Leer geometría desde AOG.
                if (parent.tool != null)
                {
                    impl.Setup.AnchoImplemento = Math.Round(parent.tool.width, 2);
                    impl.Setup.SeccionesAOG = parent.tool.numOfSections;

                    // Calcular distancia entre surcos si hay surcos definidos.
                    int totalSurcos = impl.Setup.TotalSurcos;
                    if (totalSurcos <= 0)
                    {
                        // Estimar surcos desde trenes.
                        totalSurcos = 0;
                        if (impl.Trenes != null)
                            foreach (var t in impl.Trenes)
                                totalSurcos += t.Surcos;
                    }
                    if (totalSurcos > 0)
                    {
                        impl.Setup.TotalSurcos = totalSurcos;
                        impl.Setup.DistanciaEntreSurcos = Math.Round(parent.tool.width / totalSurcos, 4);
                    }
                }

                SaveCurrentImplemento(impl);
                RefreshImplementoSummary();
                UpdateDistLabel();

                // Update the ancho TextBox if it exists.
                foreach (Control ctrl in _implSummaryPanel.Controls)
                {
                    var tb = ctrl as TextBox;
                    if (tb != null && tb.Location.X == 280)
                    {
                        tb.Text = impl.Setup.AnchoImplemento.ToString("F2", CultureInfo.InvariantCulture);
                        break;
                    }
                }

                double distCm = impl.Setup.DistanciaEntreSurcos * 100;
                MessageBox.Show(this,
                    "Geometr\u00EDa le\u00EDda de AOG:\n\n"
                    + "Ancho: " + impl.Setup.AnchoImplemento.ToString("F2", CultureInfo.InvariantCulture) + " m\n"
                    + "Secciones: " + impl.Setup.SeccionesAOG + "\n"
                    + "Dist. entre surcos: " + distCm.ToString("F1", CultureInfo.InvariantCulture) + " cm\n"
                    + "(" + impl.Setup.DistanciaEntreSurcos.ToString("F4", CultureInfo.InvariantCulture) + " m)",
                    "Geometr\u00EDa AOG", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =====================================================================
        // Actions
        // =====================================================================

        private void OnBrowseImplemento()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Seleccionar JSON de implemento";
                ofd.Filter = "JSON (*.json)|*.json|Todos (*.*)|*.*";
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string candidate = Path.Combine(baseDir, "data", "implementos");
                if (Directory.Exists(candidate)) ofd.InitialDirectory = candidate;
                else if (Directory.Exists(baseDir)) ofd.InitialDirectory = baseDir;

                if (ofd.ShowDialog(this) == DialogResult.OK)
                    _txtImplemento.Text = ofd.FileName;
            }
        }

        private void OpenSubDialog(string which)
        {
            // Save current path first so sub-dialogs use the right implemento.
            _cfg.ImplementoJsonPath = (_txtImplemento.Text ?? "").Trim();

            if (which == "trenes")
            {
                using (var dlg = new FormVistaXTrenes(_cfg))
                {
                    dlg.ShowDialog(this);
                    RefreshImplementoSummary();
                }
            }
            else if (which == "sensores")
            {
                using (var dlg = new FormVistaXSensores(_cfg))
                {
                    dlg.ShowDialog(this);
                    RefreshImplementoSummary();
                }
            }
        }

        private void UpdateSpecialSensorCount(string tipo, int desired)
        {
            var impl = LoadCurrentImplemento();
            if (impl == null) return;
            if (impl.MapeoSensores == null)
                impl.MapeoSensores = new List<SensorConfig>();

            // Count current sensors of this type.
            var existing = new List<SensorConfig>();
            foreach (var s in impl.MapeoSensores)
                if (s != null && string.Equals(s.Tipo, tipo, StringComparison.OrdinalIgnoreCase))
                    existing.Add(s);

            int current = existing.Count;
            if (current == desired) return;

            if (desired > current)
            {
                // Add new sensors.
                // Determine default tren and UID from existing semilla sensors.
                int tren = 1;
                string uid = "UNASSIGNED";
                if (impl.MapeoSensores.Count > 0)
                {
                    var first = impl.MapeoSensores[0];
                    if (first != null)
                    {
                        tren = first.Tren > 0 ? first.Tren : 1;
                        if (!string.IsNullOrEmpty(first.Uid)) uid = first.Uid;
                    }
                }

                for (int i = current + 1; i <= desired; i++)
                {
                    impl.MapeoSensores.Add(new SensorConfig
                    {
                        Uid = uid,
                        Cable = 0,
                        Bajada = i,
                        Tipo = tipo,
                        Nombre = TipoSensor.NombreAmigable(tipo) + " " + i,
                        Tren = tren,
                        IsActive = true
                    });
                }
            }
            else
            {
                // Remove excess (from the end).
                int toRemove = current - desired;
                for (int i = 0; i < toRemove && existing.Count > 0; i++)
                {
                    var last = existing[existing.Count - 1];
                    impl.MapeoSensores.Remove(last);
                    existing.RemoveAt(existing.Count - 1);
                }
            }

            SaveCurrentImplemento(impl);
        }

        private AgOpenGPS.FormGPS FindFormGPS()
        {
            // Traverse parent chain (when embedded in Hub).
            Control c = this.Parent;
            while (c != null)
            {
                if (c is AgOpenGPS.FormGPS gps) return gps;
                var f = c as Form;
                if (f != null && f.Owner is AgOpenGPS.FormGPS gps2) return gps2;
                c = c.Parent;
            }
            // Search open forms.
            foreach (Form f in Application.OpenForms)
                if (f is AgOpenGPS.FormGPS gps3) return gps3;
            return null;
        }

        private static decimal Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
