// ============================================================================
// FormAgroParallelHub.cs - Hub unificado de módulos Agro Parallel
// Ubicación: SourceCode/GPS/AgroParallel/Common/FormAgroParallelHub.cs
// Target: net48 (C# 7.3)
//
// Sidebar vertical + tabs horizontales. Estilo CentriX-Spark (mockups).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AgroParallel.OrbitX;
using AgroParallel.QuantiX;
using AgroParallel.SectionX;
using AgroParallel.VistaX;

namespace AgroParallel.Common
{
    public class FormAgroParallelHub : Form
    {
        private readonly AgOpenGPS.FormGPS _parent;

        private class ModuleDef
        {
            public string Name;
            public string Icon;
            public Color Accent;
            public string[] Tabs;
            public bool Available;
        }

        private readonly List<ModuleDef> _modules = new List<ModuleDef>();
        private int _selectedModule;

        private Panel _sidebar;
        private Panel _tabStrip;
        private Panel _contentArea;
        private readonly List<Panel> _sidebarButtons = new List<Panel>();
        private readonly List<Panel> _tabButtons = new List<Panel>();
        private int _selectedTab;
        private Form _currentChild;

        public FormAgroParallelHub(AgOpenGPS.FormGPS parent)
        {
            _parent = parent;
            InitModules();
            BuildUI();
        }

        private void InitModules()
        {
            _modules.Add(new ModuleDef
            {
                Name = "VistaX", Icon = "\U0001F33F",
                Accent = Theme.Accent, Available = true,
                Tabs = new[] { "Monitor", "Config", "Perfiles", "Nodes", "Trenes", "Sensores", "Mapeo", "Sonidos", "Prueba", "Simulador" }
            });
            _modules.Add(new ModuleDef
            {
                Name = "QuantiX", Icon = "\U0001F4CA",
                Accent = Color.FromArgb(230, 160, 30), Available = true,
                Tabs = new[] { "Monitor", "Motores", "Config Avanzada", "Calibraci\u00F3n", "Mapas" }
            });
            _modules.Add(new ModuleDef
            {
                Name = "FlowX", Icon = "\U0001F4A7",
                Accent = Color.FromArgb(0, 229, 255), Available = false,
                Tabs = new[] { "Caudal\u00EDmetros", "Config" }
            });
            _modules.Add(new ModuleDef
            {
                Name = "StormX", Icon = "\u26A1",
                Accent = Theme.Warning, Available = false,
                Tabs = new[] { "Clima", "Alertas", "Config" }
            });
            _modules.Add(new ModuleDef
            {
                Name = "LineX", Icon = "\U0001F4CF",
                Accent = Color.FromArgb(156, 100, 200), Available = false,
                Tabs = new[] { "Gu\u00EDa", "Config" }
            });
            _modules.Add(new ModuleDef
            {
                Name = "SectionX", Icon = "\U0001F3AF",
                Accent = Theme.Herramienta, Available = true,
                Tabs = new[] { "Config" }
            });
            _modules.Add(new ModuleDef
            {
                Name = "OrbitX", Icon = "\u2601",
                Accent = Color.FromArgb(100, 180, 255), Available = true,
                Tabs = new[] { "Config" }
            });
        }

        // =====================================================================
        // UI
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "Agro Parallel";
            Size = new Size(1240, 800);
            MinimumSize = new Size(1000, 600);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, ev) => { if (ev.KeyCode == Keys.Escape) Close(); };
            Theme.ApplyToForm(this);

            // Borde exterior.
            Paint += (s, ev) =>
                Theme.DrawRoundedBorder(ev.Graphics,
                    new Rectangle(0, 0, Width - 1, Height - 1), Theme.Border, 10);

            // ── Header ──────────────────────────────────────────────────
            var header = new Panel
            {
                Dock = DockStyle.Top, Height = Theme.HeaderHeight,
                BackColor = Theme.BgHeader, Cursor = Cursors.SizeAll
            };
            header.Paint += (s, ev) =>
            {
                Theme.PaintHeader(ev.Graphics, header.Width, null);
            };
            header.MouseDown += (s, ev) => { if (ev.Button == MouseButtons.Left) { _dragging = true; _dragStart = ev.Location; } };
            header.MouseMove += (s, ev) => { if (_dragging) Location = new Point(Location.X + ev.X - _dragStart.X, Location.Y + ev.Y - _dragStart.Y); };
            header.MouseUp += (s, ev) => _dragging = false;

            var btnClose = Theme.MkToolbarButton("\u2715", 36);
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 30, 30);
            btnClose.Click += (s, ev) => Close();
            header.Controls.Add(btnClose);
            header.Resize += (s, ev) => btnClose.Location = new Point(header.Width - 40, 6);
            Controls.Add(header);

            // ── Sidebar ─────────────────────────────────────────────────
            _sidebar = new Panel
            {
                Dock = DockStyle.Left, Width = 170,
                BackColor = Color.FromArgb(6, 6, 8)
            };
            _sidebar.Paint += (s, ev) =>
            {
                using (var pen = new Pen(Theme.Border))
                    ev.Graphics.DrawLine(pen, _sidebar.Width - 1, 0,
                        _sidebar.Width - 1, _sidebar.Height);
            };

            int sy = 16;
            for (int i = 0; i < _modules.Count; i++)
            {
                int ci = i;
                var mod = _modules[i];
                var btn = new Panel
                {
                    Location = new Point(0, sy),
                    Size = new Size(_sidebar.Width - 1, 52),
                    BackColor = Color.Transparent,
                    Cursor = mod.Available ? Cursors.Hand : Cursors.Default
                };

                btn.Paint += (s, ev) =>
                {
                    var g = ev.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    bool sel = _selectedModule == ci;

                    if (sel)
                    {
                        // Selected: tinted background + accent bar.
                        Theme.FillRoundedRect(g, new Rectangle(4, 2, btn.Width - 8, btn.Height - 4),
                            Color.FromArgb(15, mod.Accent.R, mod.Accent.G, mod.Accent.B), 6);
                        using (var b = new SolidBrush(mod.Accent))
                            g.FillRectangle(b, 0, 8, 3, btn.Height - 16);
                    }

                    Color fg = sel ? Theme.TextPrimary
                        : mod.Available ? Theme.TextSecondary : Theme.TextDisabled;

                    // Icon.
                    using (var fi = new Font("Segoe UI Emoji", 14f))
                    using (var br = new SolidBrush(sel ? mod.Accent : fg))
                        g.DrawString(mod.Icon, fi, br, 16, 10);

                    // Name.
                    using (var fn = new Font(Theme.FontFamily, 10f, FontStyle.Bold))
                    using (var br = new SolidBrush(fg))
                        g.DrawString(mod.Name, fn, br, 48, 16);

                    if (!mod.Available)
                    {
                        using (var f = new Font(Theme.FontFamily, 6.5f))
                        using (var b = new SolidBrush(Theme.TextDisabled))
                            g.DrawString("PR\u00D3XIMAMENTE", f, b, 48, 34);
                    }
                };

                if (mod.Available)
                {
                    btn.Click += (s, ev) => SelectModule(ci);
                    btn.MouseEnter += (s, ev) => { btn.BackColor = Theme.BgCardHover; btn.Invalidate(); };
                    btn.MouseLeave += (s, ev) => { btn.BackColor = Color.Transparent; btn.Invalidate(); };
                }

                _sidebar.Controls.Add(btn);
                _sidebarButtons.Add(btn);
                sy += 54;
            }
            Controls.Add(_sidebar);

            // ── Right panel (tabs + content) ────────────────────────────
            var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBlack };
            Controls.Add(rightPanel);
            rightPanel.BringToFront();

            _tabStrip = new Panel
            {
                Dock = DockStyle.Top, Height = 38,
                BackColor = Theme.BgBlack
            };
            _tabStrip.Paint += (s, ev) =>
            {
                using (var pen = new Pen(Theme.Border))
                    ev.Graphics.DrawLine(pen, 0, _tabStrip.Height - 1,
                        _tabStrip.Width, _tabStrip.Height - 1);
            };
            rightPanel.Controls.Add(_tabStrip);

            _contentArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.BgBlack
            };
            rightPanel.Controls.Add(_contentArea);
            _contentArea.BringToFront();

            SelectModule(0);
        }

        // =====================================================================
        // Selection
        // =====================================================================

        private void SelectModule(int idx)
        {
            if (idx < 0 || idx >= _modules.Count) return;
            if (!_modules[idx].Available) return;
            _selectedModule = idx;
            _selectedTab = 0;
            foreach (var btn in _sidebarButtons) btn.Invalidate();
            RebuildTabStrip();
            LoadContent();
        }

        private void RebuildTabStrip()
        {
            _tabStrip.Controls.Clear();
            _tabButtons.Clear();

            var mod = _modules[_selectedModule];
            if (mod.Tabs == null) return;

            int x = 8;
            for (int i = 0; i < mod.Tabs.Length; i++)
            {
                int ti = i;
                var tab = new Panel
                {
                    Location = new Point(x, 0),
                    Size = new Size(0, _tabStrip.Height),
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand
                };

                string text = mod.Tabs[i];
                // Measure to auto-size.
                using (var f = new Font(Theme.FontFamily, 9f, FontStyle.Bold))
                {
                    var sz = TextRenderer.MeasureText(text, f);
                    tab.Size = new Size(sz.Width + 16, _tabStrip.Height);
                }

                tab.Paint += (s, ev) =>
                {
                    var g = ev.Graphics;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    bool sel = _selectedTab == ti;
                    Color fg = sel ? mod.Accent : Theme.TextSecondary;

                    using (var f = new Font(Theme.FontFamily, 9f, FontStyle.Bold))
                    using (var br = new SolidBrush(fg))
                    {
                        var sz = g.MeasureString(text, f);
                        g.DrawString(text, f, br,
                            (tab.Width - sz.Width) / 2f,
                            (tab.Height - sz.Height) / 2f);
                    }

                    // Underline for selected tab.
                    if (sel)
                    {
                        using (var pen = new Pen(mod.Accent, 2.5f))
                            g.DrawLine(pen, 4, tab.Height - 2, tab.Width - 4, tab.Height - 2);
                    }
                };

                tab.Click += (s, ev) =>
                {
                    _selectedTab = ti;
                    foreach (var tb in _tabButtons) tb.Invalidate();
                    LoadContent();
                };
                tab.MouseEnter += (s, ev) => { if (ti != _selectedTab) tab.Invalidate(); };
                tab.MouseLeave += (s, ev) => tab.Invalidate();

                _tabStrip.Controls.Add(tab);
                _tabButtons.Add(tab);
                x += tab.Width + 2;
            }
        }

        // =====================================================================
        // Content
        // =====================================================================

        private void LoadContent()
        {
            if (_currentChild != null)
            {
                _contentArea.Controls.Remove(_currentChild);
                try { _currentChild.Close(); } catch { }
                try { _currentChild.Dispose(); } catch { }
                _currentChild = null;
            }

            var mod = _modules[_selectedModule];
            Form child = null;

            if (mod.Name == "VistaX")
                child = CreateVistaXContent(_selectedTab);
            else if (mod.Name == "QuantiX")
                child = CreateQuantiXContent(_selectedTab);
            else if (mod.Name == "SectionX")
                child = CreateSectionXContent(_selectedTab);
            else if (mod.Name == "OrbitX")
                child = CreateOrbitXContent(_selectedTab);

            if (child == null)
            {
                var lbl = new Label
                {
                    Text = mod.Icon + "  " + mod.Name + "\n\n"
                        + (mod.Tabs != null && _selectedTab < mod.Tabs.Length ? mod.Tabs[_selectedTab] : "")
                        + "\n\nPr\u00F3ximamente...",
                    Font = new Font(Theme.FontFamily, 16f),
                    ForeColor = Theme.TextDisabled,
                    BackColor = Color.Transparent,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                _contentArea.Controls.Add(lbl);
                return;
            }

            child.TopLevel = false;
            child.FormBorderStyle = FormBorderStyle.None;
            child.Dock = DockStyle.Fill;
            child.BackColor = Theme.BgBlack;
            child.Visible = true;

            // Suprimir chrome propio del form (headers, footers, botones X)
            // para evitar superposición con el Hub.
            StripEmbeddedChrome(child);

            _contentArea.Controls.Add(child);
            _currentChild = child;
        }

        private Form CreateVistaXContent(int tab)
        {
            if (_parent.vistaXConfig == null)
                _parent.vistaXConfig = VistaXConfig.Load();
            var cfg = _parent.vistaXConfig;

            switch (tab)
            {
                case 0: return new FormVistaXPopup(cfg, null);
                case 1: return new FormVistaXConfig(cfg, _parent);
                case 2: return new FormVistaXPerfiles(cfg);
                case 3: return new FormVistaXNodos(cfg, null);
                case 4: return new FormVistaXTrenes(cfg);
                case 5: return new FormVistaXSensores(cfg);
                case 6: return new FormVistaXMapeo(cfg);
                case 7: return new FormVistaXSonidos(cfg);
                case 8: return new FormVistaXPrueba(cfg);
                case 9: return new FormVistaXSimulator(cfg);
                default: return null;
            }
        }

        private Form CreateOrbitXContent(int tab)
        {
            var cfg = OrbitXConfig.Load();
            // Buscar el sync en FormGPS.
            OrbitXSync sync = null;
            try
            {
                var field = _parent.GetType().GetField("orbitXSync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null) sync = field.GetValue(_parent) as OrbitXSync;
            }
            catch { }
            switch (tab)
            {
                case 0: return new FormOrbitXConfig(cfg, sync);
                default: return null;
            }
        }

        private Form CreateSectionXContent(int tab)
        {
            var cfg = SectionXConfig.Load();
            switch (tab)
            {
                case 0: return new FormSectionXConfig(cfg);
                default: return null;
            }
        }

        private Form CreateQuantiXContent(int tab)
        {
            var cfg = QuantiXConfig.Load();
            switch (tab)
            {
                case 0: return new FormQuantiXMonitor(cfg, _parent);   // Monitor
                case 1: return new FormQuantiXMotores(cfg);            // Motores (nodos + config UDP)
                case 2: return new FormQuantiXPID(cfg);                // Config Avanzada (PID, PPR, PWM, FF)
                case 3: return new FormQuantiXCalibrar(cfg);           // Calibración
                case 4: return new FormQuantiXShapefile(_parent);      // Mapas (shapefile unificado)
                default: return null;
            }
        }

        /// Oculta SOLO los paneles draggables (topBar) y sub-headers redundantes.
        /// NO oculta footers (tienen botones de acción como Guardar, Escanear, etc.)
        private static void StripEmbeddedChrome(Form child)
        {
            var toHide = new List<Control>();

            foreach (Control c in child.Controls)
            {
                if (!(c is Panel p)) continue;

                // Solo ocultar paneles Dock.Top que son draggables (topBar con SizeAll cursor).
                if (p.Dock == DockStyle.Top && p.Cursor == Cursors.SizeAll)
                {
                    toHide.Add(p);
                    continue;
                }

                // Ocultar sub-headers que dicen "VistaX CONFIGURACION" (redundante con tabs del Hub).
                if (p.Dock == DockStyle.Top && p.Height <= 80)
                {
                    bool hasVistaXLabel = false;
                    foreach (Control lbl in p.Controls)
                    {
                        if (lbl is Label l && l.Text != null
                            && l.Text.Contains("CONFIGURACION")
                            && (l.Text.Contains("VistaX") || l.Text.Contains("Quantix")))
                        {
                            hasVistaXLabel = true;
                            break;
                        }
                    }
                    if (hasVistaXLabel) toHide.Add(p);
                }
            }

            foreach (var c in toHide)
            {
                c.Visible = false;
                c.Height = 0;
            }

            // Ocultar botones X sueltos (cierre redundante — el Hub tiene el suyo).
            foreach (Control c in child.Controls)
            {
                if (c is Button btn && btn.Text == "\u2715"
                    && btn.Anchor.HasFlag(AnchorStyles.Right)
                    && btn.Parent == child) // Solo los del form raíz, no los de controles internos.
                {
                    btn.Visible = false;
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_currentChild != null)
            {
                try { _currentChild.Close(); } catch { }
                try { _currentChild.Dispose(); } catch { }
                _currentChild = null;
            }
        }
    }
}
