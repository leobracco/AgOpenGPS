// ============================================================================
// FormVistaXSonidos.cs - Pantalla y Sonidos (Config – Pantalla y Sonidos)
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXSonidos.cs
// Target: net48 (C# 7.3)
//
// Control maestro de audio (mute global + volumen). Tabs: Pantalla | Master |
// Eventos Globales | Por Tipo de Sensor. Configura qué eventos disparan
// sonido y con qué prioridad.
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.VistaX
{
    public class FormVistaXSonidos : Form
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

        private readonly VistaXConfig _cfg;
        private TabControl _tabs;
        private CheckBox _chkMuteGlobal;
        private TrackBar _trkVolume;
        private Label _lblVolume;

        // Sound config (persisted with VistaXConfig — new fields).
        private bool _muteGlobal;
        private int _volume = 80;           // 0-100
        private bool _sndTuboTapado = true;
        private bool _sndDesvio = true;
        private bool _sndConexion = false;
        private bool _sndInicioMonitoreo = true;
        private int _beepFreq = 1200;
        private int _beepDuration = 180;

        public FormVistaXSonidos(VistaXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            _muteGlobal = cfg.AlarmMuted;
            BuildUI();
        }

        // =====================================================================
        // UI
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "VistaX — Pantalla y Sonidos";
            Size = new Size(780, 580);
            MinimumSize = new Size(640, 460);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CBgDark; ForeColor = CText;
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

            // ── Sub-header ──────────────────────────────────────────────
            var subHeader = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = CBgDark };
            subHeader.Controls.Add(new Label
            {
                Text = "\U0001F50A",
                Font = new Font("Segoe UI Emoji", 20f, FontStyle.Bold),
                ForeColor = CAccent, Location = new Point(22, 20),
                Size = new Size(40, 40), BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            });
            subHeader.Controls.Add(new Label
            {
                Text = "PANTALLA Y SONIDOS",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CText, Location = new Point(74, 18),
                AutoSize = true, BackColor = Color.Transparent
            });
            subHeader.Controls.Add(new Label
            {
                Text = "Control maestro de audio, alertas y configuraci\u00F3n visual",
                Font = new Font("Segoe UI", 9f), ForeColor = CTextDim,
                Location = new Point(74, 46), AutoSize = true, BackColor = Color.Transparent
            });
            Controls.Add(subHeader);

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
            topBar.Controls.Add(new Label { Text = "CONFIGURACION", Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = CTextDim, Location = new Point(116, 20), AutoSize = true, BackColor = Color.Transparent });

            var btnX = new Button { Text = "\u2715", FlatStyle = FlatStyle.Flat, BackColor = CBgPanel, ForeColor = CTextDim, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Size = new Size(40, 32), Cursor = Cursors.Hand, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnX.FlatAppearance.BorderSize = 0;
            btnX.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 40, 40);
            btnX.Click += (s, ev) => Close();
            topBar.Controls.Add(btnX);
            topBar.Resize += (s, ev) => btnX.Location = new Point(topBar.Width - btnX.Width - 4, 12);
            Controls.Add(topBar);

            // ── Tabs ────────────────────────────────────────────────────
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                DrawMode = TabDrawMode.OwnerDrawFixed,
                ItemSize = new Size(140, 34),
                SizeMode = TabSizeMode.Fixed
            };
            _tabs.DrawItem += PaintTab;

            // Tab: Master
            var tabMaster = new TabPage("Master") { BackColor = CBgDark };
            BuildMasterTab(tabMaster);
            _tabs.TabPages.Add(tabMaster);

            // Tab: Eventos Globales
            var tabEventos = new TabPage("Eventos") { BackColor = CBgDark };
            BuildEventosTab(tabEventos);
            _tabs.TabPages.Add(tabEventos);

            // Tab: Por Tipo de Sensor
            var tabTipo = new TabPage("Por Tipo") { BackColor = CBgDark };
            BuildTipoTab(tabTipo);
            _tabs.TabPages.Add(tabTipo);

            // Tab: Pantalla
            var tabPantalla = new TabPage("Pantalla") { BackColor = CBgDark };
            BuildPantallaTab(tabPantalla);
            _tabs.TabPages.Add(tabPantalla);

            Controls.Add(_tabs);
            _tabs.BringToFront();

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = CBgPanel };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            footer.Controls.Add(new Label
            {
                Text = "VistaX nativo \u00B7 Pantalla y Sonidos",
                Font = new Font("Segoe UI", 8.5f), ForeColor = CTextFaint,
                Location = new Point(18, 17), AutoSize = true, BackColor = Color.Transparent
            });

            var btnSave = MkPillButton("\u2713  GUARDAR", CAccent, CBgDark);
            btnSave.Size = new Size(120, 34);
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Click += (s, ev) => SaveAndClose();
            footer.Controls.Add(btnSave);

            var btnCancel = new Button
            {
                Text = "\u2715 CERRAR", FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand, Size = new Size(110, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            footer.Controls.Add(btnCancel);
            footer.Resize += (s, ev) =>
            {
                btnCancel.Location = new Point(footer.Width - btnCancel.Width - 18, 8);
                btnSave.Location = new Point(btnCancel.Left - btnSave.Width - 10, 8);
            };
            Controls.Add(footer);
            CancelButton = btnCancel;
        }

        // ── Master tab ──────────────────────────────────────────────────

        private void BuildMasterTab(TabPage tab)
        {
            int y = 20, x = 30;

            _chkMuteGlobal = new CheckBox
            {
                Text = "  MUTE GLOBAL  \u2014  Silenciar todas las alarmas sonoras",
                Checked = _muteGlobal,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = _muteGlobal ? CRed : CText,
                BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            };
            _chkMuteGlobal.CheckedChanged += (s, ev) =>
            {
                _muteGlobal = _chkMuteGlobal.Checked;
                _chkMuteGlobal.ForeColor = _muteGlobal ? CRed : CText;
            };
            tab.Controls.Add(_chkMuteGlobal);
            y += 50;

            tab.Controls.Add(new Label
            {
                Text = "VOLUMEN",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = CTextFaint, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            });
            y += 22;

            _trkVolume = new TrackBar
            {
                Minimum = 0, Maximum = 100, Value = _volume,
                TickFrequency = 10, LargeChange = 10, SmallChange = 5,
                Location = new Point(x, y), Size = new Size(400, 40),
                BackColor = CBgDark
            };
            _lblVolume = new Label
            {
                Text = _volume + "%",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.Transparent,
                Location = new Point(x + 410, y + 4), AutoSize = true
            };
            _trkVolume.ValueChanged += (s, ev) =>
            {
                _volume = _trkVolume.Value;
                _lblVolume.Text = _volume + "%";
            };
            tab.Controls.Add(_trkVolume);
            tab.Controls.Add(_lblVolume);
            y += 60;

            // Beep config.
            tab.Controls.Add(new Label
            {
                Text = "TONO DE ALARMA",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = CTextFaint, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            });
            y += 22;

            tab.Controls.Add(new Label
            {
                Text = "Frecuencia (Hz):",
                Font = new Font("Segoe UI", 9f), ForeColor = CTextDim,
                BackColor = Color.Transparent, Location = new Point(x, y), AutoSize = true
            });
            var numFreq = new NumericUpDown
            {
                Minimum = 200, Maximum = 5000, Value = _beepFreq, Increment = 100,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(x + 130, y - 2), Size = new Size(90, 26)
            };
            numFreq.ValueChanged += (s, ev) => _beepFreq = (int)numFreq.Value;
            tab.Controls.Add(numFreq);

            tab.Controls.Add(new Label
            {
                Text = "Duraci\u00F3n (ms):",
                Font = new Font("Segoe UI", 9f), ForeColor = CTextDim,
                BackColor = Color.Transparent, Location = new Point(x + 250, y), AutoSize = true
            });
            var numDur = new NumericUpDown
            {
                Minimum = 50, Maximum = 2000, Value = _beepDuration, Increment = 50,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(x + 380, y - 2), Size = new Size(80, 26)
            };
            numDur.ValueChanged += (s, ev) => _beepDuration = (int)numDur.Value;
            tab.Controls.Add(numDur);
            y += 34;

            var btnTest = MkPillButton("\U0001F50A  PROBAR SONIDO", Color.FromArgb(40, 40, 45), CText);
            btnTest.Size = new Size(160, 32);
            btnTest.Location = new Point(x, y);
            btnTest.Click += (s, ev) =>
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { Console.Beep(_beepFreq, _beepDuration); } catch { }
                });
            };
            tab.Controls.Add(btnTest);
        }

        // ── Eventos tab ─────────────────────────────────────────────────

        private void BuildEventosTab(TabPage tab)
        {
            int y = 20, x = 30;

            tab.Controls.Add(new Label
            {
                Text = "EVENTOS QUE DISPARAN SONIDO",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CText, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            });
            y += 34;

            AddEventToggle(tab, ref y, x, "Tubo tapado (flujo = 0)",
                _sndTuboTapado, v => _sndTuboTapado = v, "ALTA", CRed);
            AddEventToggle(tab, ref y, x, "Desv\u00EDo de densidad (> tolerancia)",
                _sndDesvio, v => _sndDesvio = v, "MEDIA", CYellow);
            AddEventToggle(tab, ref y, x, "Conexi\u00F3n/desconexi\u00F3n MQTT",
                _sndConexion, v => _sndConexion = v, "BAJA", CTextDim);
            AddEventToggle(tab, ref y, x, "Inicio/fin de monitoreo",
                _sndInicioMonitoreo, v => _sndInicioMonitoreo = v, "INFO", CAccent);
        }

        private void AddEventToggle(TabPage tab, ref int y, int x,
            string text, bool initial, Action<bool> onChange,
            string priority, Color priorityColor)
        {
            var chk = new CheckBox
            {
                Text = "  " + text,
                Checked = initial,
                Font = new Font("Segoe UI", 10f),
                ForeColor = CText, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            };
            chk.CheckedChanged += (s, ev) => onChange(chk.Checked);
            tab.Controls.Add(chk);

            var lblPri = new Label
            {
                Text = priority,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = priorityColor, BackColor = Color.FromArgb(30, 30, 34),
                Location = new Point(x + 420, y + 3),
                AutoSize = true, Padding = new Padding(4, 1, 4, 1)
            };
            tab.Controls.Add(lblPri);

            y += 36;
        }

        // ── Por Tipo tab ────────────────────────────────────────────────

        private void BuildTipoTab(TabPage tab)
        {
            int y = 20, x = 30;

            tab.Controls.Add(new Label
            {
                Text = "SONIDO POR TIPO DE SENSOR",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CText, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            });
            y += 34;

            var tipos = new System.Collections.Generic.List<Tuple<string, Color, bool>>();
            foreach (var t in TipoSensor.Todos)
            {
                bool defaultOn = TipoSensor.GeneraAlarmaFlujo(t);
                tipos.Add(Tuple.Create(TipoSensor.NombreAmigable(t), TipoSensor.GetColor(t), defaultOn));
            }

            foreach (var t in tipos)
            {
                var ledColor = t.Item2;
                var panel = new Panel
                {
                    Size = new Size(500, 38),
                    Location = new Point(x, y),
                    BackColor = CBgCard
                };
                panel.Paint += (s, ev) =>
                {
                    using (var acc = new SolidBrush(ledColor))
                        ev.Graphics.FillRectangle(acc, 0, 0, 4, panel.Height);
                    using (var pen = new Pen(CBorder))
                        ev.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
                };

                panel.Controls.Add(new Label
                {
                    Text = t.Item1,
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = CText, BackColor = Color.Transparent,
                    Location = new Point(16, 8), AutoSize = true
                });

                var chk = new CheckBox
                {
                    Text = "  Alarma sonora",
                    Checked = t.Item3,
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = CTextDim, BackColor = Color.Transparent,
                    Location = new Point(280, 8), AutoSize = true
                };
                panel.Controls.Add(chk);

                tab.Controls.Add(panel);
                y += 46;
            }
        }

        // ── Pantalla tab ────────────────────────────────────────────────

        private void BuildPantallaTab(TabPage tab)
        {
            int y = 20, x = 30;

            tab.Controls.Add(new Label
            {
                Text = "LAYOUT DEL PANEL EMBEBIDO",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CText, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            });
            y += 34;

            AddNumericRow(tab, ref y, x, "Altura del panel (px):",
                50, 500, _cfg.PanelHeight, v => _cfg.PanelHeight = v);
            AddNumericRow(tab, ref y, x, "Ancho del panel (%):",
                20, 100, _cfg.PanelWidthPercent, v => _cfg.PanelWidthPercent = v);
            AddNumericRow(tab, ref y, x, "Margen inferior (px):",
                0, 600, _cfg.PanelBottomMargin, v => _cfg.PanelBottomMargin = v);
            AddNumericRow(tab, ref y, x, "Intervalo UI (ms):",
                200, 2000, _cfg.UiUpdateIntervalMs, v => _cfg.UiUpdateIntervalMs = v);
        }

        private void AddNumericRow(TabPage tab, ref int y, int x,
            string label, int min, int max, int initial, Action<int> onChange)
        {
            tab.Controls.Add(new Label
            {
                Text = label,
                Font = new Font("Segoe UI", 9.5f), ForeColor = CTextDim,
                BackColor = Color.Transparent, Location = new Point(x, y + 2), AutoSize = true
            });
            var num = new NumericUpDown
            {
                Minimum = min, Maximum = max,
                Value = Math.Max(min, Math.Min(max, initial)),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(x + 240, y), Size = new Size(90, 26)
            };
            num.ValueChanged += (s, ev) => onChange((int)num.Value);
            tab.Controls.Add(num);
            y += 36;
        }

        private void PaintTab(object sender, DrawItemEventArgs e)
        {
            var tab = _tabs.TabPages[e.Index];
            bool selected = _tabs.SelectedIndex == e.Index;
            Color bg = selected ? CBgDark : CBgPanel;
            Color fg = selected ? CAccent : CTextDim;

            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);
            using (var f = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (var br = new SolidBrush(fg))
            {
                var sz = e.Graphics.MeasureString(tab.Text, f);
                e.Graphics.DrawString(tab.Text, f, br,
                    e.Bounds.X + (e.Bounds.Width - sz.Width) / 2f,
                    e.Bounds.Y + (e.Bounds.Height - sz.Height) / 2f);
            }
            if (selected)
            {
                using (var pen = new Pen(CAccent, 2))
                    e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }
        }

        // =====================================================================
        // Save
        // =====================================================================

        private void SaveAndClose()
        {
            _cfg.AlarmMuted = _muteGlobal;
            _cfg.Save();
            DialogResult = DialogResult.OK;
            Close();
        }

        private static Button MkPillButton(string text, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat,
                BackColor = bg, ForeColor = fg,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}
