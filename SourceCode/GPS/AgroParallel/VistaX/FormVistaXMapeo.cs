// ============================================================================
// FormVistaXMapeo.cs - Mapeo visual de la sembradora (Config – Mapeo Visual)
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXMapeo.cs
// Target: net48 (C# 7.3)
//
// Vista "desde atrás del tractor" de toda la sembradora. Coloreable por
// Estado/Nodo/Tipo. Solo lectura, con botón Imprimir. Muestra trenes con
// surcos numerados para verificar la configuración antes de ir a campo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.VistaX
{
    public class FormVistaXMapeo : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CAccentDim { get { return Theme.AccentDim; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CTextFaint { get { return Theme.TextFaint; } }
        private static Color CBorder { get { return Theme.Border; } }

        private enum ColorMode { Estado, Nodo, Tipo }

        private readonly VistaXConfig _cfg;
        private ImplementoConfig _implemento;
        private Panel _canvas;
        private ComboBox _cboMode;
        private ColorMode _colorMode = ColorMode.Estado;

        // Palette for node coloring (up to 10 nodes).
        private static readonly Color[] NodePalette = new[]
        {
            Color.FromArgb(0, 230, 118),   // green
            Color.FromArgb(0, 176, 255),   // blue
            Color.FromArgb(255, 160, 0),   // orange
            Color.FromArgb(156, 39, 176),  // purple
            Color.FromArgb(255, 234, 0),   // yellow
            Color.FromArgb(0, 229, 255),   // cyan
            Color.FromArgb(255, 23, 68),   // red
            Color.FromArgb(233, 30, 99),   // pink
            Color.FromArgb(121, 85, 72),   // brown
            Color.FromArgb(96, 125, 139)   // blue-grey
        };

        public FormVistaXMapeo(VistaXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadData();
            _canvas.Invalidate();
        }

        // =====================================================================
        // UI
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "VistaX — Mapeo Visual";
            Size = new Size(1100, 680);
            MinimumSize = new Size(860, 500);
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
                Text = "\U0001F5FA",
                Font = new Font("Segoe UI Emoji", 20f, FontStyle.Bold),
                ForeColor = CAccent, Location = new Point(22, 20),
                Size = new Size(40, 40), BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            });
            subHeader.Controls.Add(new Label
            {
                Text = "MAPEO VISUAL",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CText, Location = new Point(74, 18),
                AutoSize = true, BackColor = Color.Transparent
            });
            subHeader.Controls.Add(new Label
            {
                Text = "Vista desde atr\u00E1s del tractor \u2014 verificaci\u00F3n de configuraci\u00F3n antes de ir a campo",
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

            // ── Toolbar (color mode + print) ────────────────────────────
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = CBgDark };

            toolbar.Controls.Add(new Label
            {
                Text = "Colorear por:", Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim, Location = new Point(20, 10),
                AutoSize = true, BackColor = Color.Transparent
            });

            _cboMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CBgPanel, ForeColor = CText,
                Font = new Font("Segoe UI", 9.5f),
                Location = new Point(110, 6), Size = new Size(140, 26)
            };
            _cboMode.Items.AddRange(new object[] { "Estado", "Nodo", "Tipo" });
            _cboMode.SelectedIndex = 0;
            _cboMode.SelectedIndexChanged += (s, ev) =>
            {
                _colorMode = (ColorMode)_cboMode.SelectedIndex;
                _canvas.Invalidate();
            };
            toolbar.Controls.Add(_cboMode);

            var btnPrint = new Button
            {
                Text = "\U0001F5A8  IMPRIMIR", FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 45), ForeColor = CText,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Size = new Size(120, 28), Location = new Point(270, 6), Cursor = Cursors.Hand
            };
            btnPrint.FlatAppearance.BorderSize = 0;
            btnPrint.Click += (s, ev) => DoPrint();
            toolbar.Controls.Add(btnPrint);

            Controls.Add(toolbar);

            // ── Canvas ──────────────────────────────────────────────────
            _canvas = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 10, 14)
            };
            _canvas.Paint += PaintCanvas;
            _canvas.Resize += (s, ev) => _canvas.Invalidate();
            Controls.Add(_canvas);
            _canvas.BringToFront();

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = CBgPanel };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            footer.Controls.Add(new Label
            {
                Text = "VistaX nativo \u00B7 Mapeo Visual \u2014 Solo lectura",
                Font = new Font("Segoe UI", 8.5f), ForeColor = CTextFaint,
                Location = new Point(18, 14), AutoSize = true, BackColor = Color.Transparent
            });

            var btnClose = new Button
            {
                Text = "\u2715 CERRAR", FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand, Size = new Size(110, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Location = new Point(Width - btnClose.Width - 18, 7);
            footer.Controls.Add(btnClose);
            Controls.Add(footer);
            CancelButton = btnClose;
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
        // Canvas painting
        // =====================================================================

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(10, 10, 14));

            if (_implemento == null || _implemento.MapeoSensores == null || _implemento.MapeoSensores.Count == 0)
            {
                using (var f = new Font("Segoe UI", 11f))
                using (var b = new SolidBrush(CTextDim))
                    g.DrawString("No hay sensores configurados", f, b, 30, 30);
                return;
            }

            // Build node color index.
            var nodeColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            int ci = 0;
            foreach (var s in _implemento.MapeoSensores)
            {
                if (s == null || string.IsNullOrEmpty(s.Uid)) continue;
                if (!nodeColors.ContainsKey(s.Uid))
                {
                    nodeColors[s.Uid] = NodePalette[ci % NodePalette.Length];
                    ci++;
                }
            }

            // Group by tren.
            var byTren = new SortedDictionary<int, List<SensorConfig>>();
            foreach (var s in _implemento.MapeoSensores)
            {
                if (s == null) continue;
                int tren = s.Tren > 0 ? s.Tren : 1;
                List<SensorConfig> list;
                if (!byTren.TryGetValue(tren, out list))
                {
                    list = new List<SensorConfig>();
                    byTren[tren] = list;
                }
                list.Add(s);
            }

            // Tren configs for names.
            var trenNames = new Dictionary<int, string>();
            if (_implemento.Trenes != null)
                foreach (var t in _implemento.Trenes)
                    if (t != null) trenNames[t.Id] = t.Nombre ?? "Tren " + t.Id;

            int canvasW = _canvas.Width;
            int canvasH = _canvas.Height;
            int marginX = 30;
            int marginY = 20;

            // Title.
            string implName = _implemento.Nombre ?? "Implemento";
            using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
            using (var b = new SolidBrush(CText))
                g.DrawString(implName + "  \u2014  Vista desde atr\u00E1s", f, b, marginX, marginY);

            int trenCount = byTren.Count;
            if (trenCount == 0) return;

            int availH = canvasH - marginY * 2 - 40;
            int trenH = Math.Min(140, Math.Max(60, availH / trenCount - 20));
            int y = marginY + 36;

            foreach (var kv in byTren)
            {
                int trenId = kv.Key;
                var sensors = kv.Value;

                // Group by bajada — only take semilla for positioning, overlay others.
                var semilla = new SortedDictionary<int, SensorConfig>();
                var auxByBajada = new Dictionary<int, List<SensorConfig>>();
                foreach (var s in sensors)
                {
                    if (string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                        semilla[s.Bajada] = s;
                    else
                    {
                        List<SensorConfig> auxList;
                        if (!auxByBajada.TryGetValue(s.Bajada, out auxList))
                        {
                            auxList = new List<SensorConfig>();
                            auxByBajada[s.Bajada] = auxList;
                        }
                        auxList.Add(s);
                    }
                }

                int count = semilla.Count;
                if (count == 0) { y += trenH + 20; continue; }

                // Tren label.
                string tName = "TREN " + trenId;
                string extra;
                if (trenNames.TryGetValue(trenId, out extra)) tName = extra.ToUpperInvariant();

                using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (var b = new SolidBrush(CAccent))
                    g.DrawString(tName + "  (" + count + " surcos)", f, b, marginX, y);

                int barY = y + 20;
                int barW = canvasW - marginX * 2;
                float cellW = Math.Min(36f, (float)barW / count);
                float totalW = cellW * count;
                float startX = marginX + (barW - totalW) / 2f;
                int cellH = trenH - 30;

                int idx = 0;
                foreach (var skv in semilla)
                {
                    var s = skv.Value;
                    float cx = startX + idx * cellW;
                    var rect = new RectangleF(cx, barY, cellW - 2, cellH);

                    Color fill = GetCellColor(s, nodeColors);
                    using (var fb = new SolidBrush(fill))
                        g.FillRectangle(fb, rect);
                    using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 0.5f))
                        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

                    // Surco number.
                    if (cellW >= 14)
                    {
                        using (var f = new Font("Segoe UI", cellW >= 22 ? 8f : 6.5f, FontStyle.Bold))
                        using (var b = new SolidBrush(CText))
                        {
                            string num = s.Bajada.ToString(CultureInfo.InvariantCulture);
                            var sz = g.MeasureString(num, f);
                            g.DrawString(num, f, b,
                                cx + (cellW - 2 - sz.Width) / 2f,
                                barY + 2);
                        }
                    }

                    // Node UID (last 4).
                    if (cellW >= 20)
                    {
                        string uid4 = s.Uid ?? "";
                        if (uid4.Length > 4) uid4 = uid4.Substring(uid4.Length - 4);
                        using (var f = new Font("Consolas", cellW >= 26 ? 6.5f : 5.5f))
                        using (var b = new SolidBrush(Color.FromArgb(180, 255, 255, 255)))
                        {
                            var sz = g.MeasureString(uid4, f);
                            g.DrawString(uid4, f, b,
                                cx + (cellW - 2 - sz.Width) / 2f,
                                barY + cellH - sz.Height - 2);
                        }
                    }

                    // Aux LEDs (small dots below cell).
                    List<SensorConfig> auxList;
                    if (auxByBajada.TryGetValue(s.Bajada, out auxList))
                    {
                        int dotX = (int)cx + 2;
                        foreach (var aux in auxList)
                        {
                            Color dotColor = GetTipoColor(aux.Tipo);
                            using (var db = new SolidBrush(dotColor))
                                g.FillEllipse(db, dotX, barY + cellH + 2, 6, 6);
                            dotX += 8;
                        }
                    }

                    idx++;
                }

                y += trenH + 20;
            }

            // Legend.
            DrawLegend(g, nodeColors, canvasW, canvasH);
        }

        private Color GetCellColor(SensorConfig s, Dictionary<string, Color> nodeColors)
        {
            switch (_colorMode)
            {
                case ColorMode.Nodo:
                    Color nc;
                    return (s.Uid != null && nodeColors.TryGetValue(s.Uid, out nc))
                        ? Color.FromArgb(120, nc.R, nc.G, nc.B)
                        : Color.FromArgb(30, 30, 35);

                case ColorMode.Tipo:
                    return Color.FromArgb(100, GetTipoColor(s.Tipo));

                case ColorMode.Estado:
                default:
                    return s.IsActive
                        ? Color.FromArgb(12, 60, 30)
                        : Color.FromArgb(50, 25, 25);
            }
        }

        private static Color GetTipoColor(string tipo)
        {
            if (tipo == null) return CTextDim;
            if (tipo.Contains("ferti_linea")) return Color.FromArgb(0, 229, 255);
            if (tipo.Contains("ferti_costado")) return Color.FromArgb(255, 234, 0);
            if (tipo.Contains("herramienta")) return Color.FromArgb(255, 160, 0);
            return CAccent; // semilla
        }

        private void DrawLegend(Graphics g, Dictionary<string, Color> nodeColors, int w, int h)
        {
            int legendY = h - 30;
            int legendX = 30;

            using (var f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            using (var faintBr = new SolidBrush(CTextFaint))
            {
                g.DrawString("LEYENDA:", f, faintBr, legendX, legendY);
                legendX += 60;

                if (_colorMode == ColorMode.Nodo)
                {
                    foreach (var kv in nodeColors)
                    {
                        string uid4 = kv.Key.Length > 4 ? kv.Key.Substring(kv.Key.Length - 4) : kv.Key;
                        using (var b = new SolidBrush(kv.Value))
                            g.FillRectangle(b, legendX, legendY + 2, 10, 10);
                        using (var tb = new SolidBrush(CTextDim))
                            g.DrawString(uid4, f, tb, legendX + 14, legendY);
                        legendX += 60;
                        if (legendX > w - 80) break;
                    }
                }
                else if (_colorMode == ColorMode.Tipo)
                {
                    var tipos = new[] {
                        Tuple.Create("Semilla", CAccent),
                        Tuple.Create("Ferti L", Color.FromArgb(0, 229, 255)),
                        Tuple.Create("Ferti C", Color.FromArgb(255, 234, 0)),
                        Tuple.Create("Herram.", Color.FromArgb(255, 160, 0))
                    };
                    foreach (var t in tipos)
                    {
                        using (var b = new SolidBrush(t.Item2))
                            g.FillRectangle(b, legendX, legendY + 2, 10, 10);
                        using (var tb = new SolidBrush(CTextDim))
                            g.DrawString(t.Item1, f, tb, legendX + 14, legendY);
                        legendX += 70;
                    }
                }
                else
                {
                    using (var b = new SolidBrush(Color.FromArgb(12, 60, 30)))
                        g.FillRectangle(b, legendX, legendY + 2, 10, 10);
                    using (var tb = new SolidBrush(CTextDim))
                        g.DrawString("Activo", f, tb, legendX + 14, legendY);
                    legendX += 60;
                    using (var b = new SolidBrush(Color.FromArgb(50, 25, 25)))
                        g.FillRectangle(b, legendX, legendY + 2, 10, 10);
                    using (var tb = new SolidBrush(CTextDim))
                        g.DrawString("Inactivo", f, tb, legendX + 14, legendY);
                }
            }
        }

        // =====================================================================
        // Print
        // =====================================================================

        private void DoPrint()
        {
            using (var doc = new PrintDocument())
            {
                doc.DocumentName = "VistaX - Mapeo Visual";
                doc.PrintPage += (s, ev) =>
                {
                    // Render canvas to print page.
                    var bmp = new Bitmap(_canvas.Width, _canvas.Height);
                    _canvas.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));

                    float scale = Math.Min(
                        (float)ev.MarginBounds.Width / bmp.Width,
                        (float)ev.MarginBounds.Height / bmp.Height);
                    int drawW = (int)(bmp.Width * scale);
                    int drawH = (int)(bmp.Height * scale);

                    ev.Graphics.DrawImage(bmp, ev.MarginBounds.X, ev.MarginBounds.Y, drawW, drawH);
                    bmp.Dispose();
                };

                using (var dlg = new PrintPreviewDialog())
                {
                    dlg.Document = doc;
                    dlg.ShowDialog(this);
                }
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

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
