// ============================================================================
// VistaXTheme.cs - Paleta y constantes visuales del sistema Agro Parallel
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/VistaXTheme.cs
// Target: net48 (C# 7.3)
//
// Fuente unica de verdad para colores, fuentes, radios y helpers de
// rendering. Estilo Agro Parallel 2026: fondo azulado oscuro, verde marca
// #A4BA3E, tarjetas limpias, inputs oscuros, botones flat.
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AgroParallel.VistaX
{
    public static class Theme
    {
        // =================================================================
        // COLORES — Paleta Agro Parallel 2026
        // =================================================================

        // Fondos (negro azulado, no negro puro).
        public static readonly Color BgBlack = Color.FromArgb(9, 11, 15);        // #090B0F
        public static readonly Color BgCard = Color.FromArgb(20, 23, 31);        // #14171F
        public static readonly Color BgCard2 = Color.FromArgb(25, 29, 39);       // #191D27
        public static readonly Color BgCardHover = Color.FromArgb(30, 34, 44);   // Hover
        public static readonly Color BgInput = Color.FromArgb(11, 14, 19);       // #0B0E13
        public static readonly Color BgHeader = Color.FromArgb(12, 15, 21);      // #0C0F15
        public static readonly Color BgToolbar = Color.FromArgb(14, 17, 23);     // #0E1117

        // Bordes.
        public static readonly Color Border = Color.FromArgb(42, 48, 60);        // #2A303C
        public static readonly Color BorderSoft = Color.FromArgb(30, 35, 45);    // #1E232D
        public static readonly Color BorderLight = Color.FromArgb(55, 62, 75);   // Borde visible

        // Acento (verde marca Agro Parallel).
        public static readonly Color Accent = Color.FromArgb(164, 186, 62);      // #A4BA3E
        public static readonly Color AccentDim = Color.FromArgb(88, 115, 34);    // #587322
        public static readonly Color AccentDark = Color.FromArgb(40, 55, 15);    // Fondo activo
        public static readonly Color AccentHover = Color.FromArgb(178, 205, 70); // Hover

        // Texto.
        public static readonly Color TextPrimary = Color.FromArgb(240, 243, 238);  // #F0F3EE
        public static readonly Color TextSecondary = Color.FromArgb(139, 148, 167); // #8B94A7
        public static readonly Color TextFaint = Color.FromArgb(88, 98, 115);      // #586273
        public static readonly Color TextDisabled = Color.FromArgb(50, 55, 65);    // Deshabilitado

        // Estados.
        public static readonly Color Ok = Color.FromArgb(164, 186, 62);          // Verde marca
        public static readonly Color Warning = Color.FromArgb(245, 172, 0);      // #F5AC00
        public static readonly Color Error = Color.FromArgb(235, 75, 75);        // #EB4B4B
        public static readonly Color Info = Color.FromArgb(70, 155, 255);        // #469BFF

        // Sensores especiales.
        public static readonly Color FertiLinea = Color.FromArgb(0, 200, 230);
        public static readonly Color FertiCostado = Color.FromArgb(245, 200, 35);
        public static readonly Color Herramienta = Color.FromArgb(255, 160, 0);
        public static readonly Color Turbina = Color.FromArgb(156, 100, 200);
        public static readonly Color Tolva = Color.FromArgb(140, 100, 75);

        // =================================================================
        // TIPOGRAFIA
        // =================================================================

        public static readonly string FontFamily = "Segoe UI";

        // Tamaños predefinidos.
        public static Font FontKpiValue { get { return new Font(FontFamily, 22f, FontStyle.Bold); } }
        public static Font FontKpiLabel { get { return new Font(FontFamily, 8f, FontStyle.Regular); } }
        public static Font FontTitle { get { return new Font(FontFamily, 14f, FontStyle.Bold); } }
        public static Font FontSubtitle { get { return new Font(FontFamily, 10f, FontStyle.Bold); } }
        public static Font FontBody { get { return new Font(FontFamily, 9.5f, FontStyle.Regular); } }
        public static Font FontSmall { get { return new Font(FontFamily, 8f, FontStyle.Regular); } }
        public static Font FontMono { get { return new Font("Consolas", 10f, FontStyle.Regular); } }
        public static Font FontMonoSmall { get { return new Font("Consolas", 8.5f, FontStyle.Regular); } }
        public static Font FontHeader { get { return new Font(FontFamily, 12f, FontStyle.Bold); } }
        public static Font FontButton { get { return new Font(FontFamily, 9.5f, FontStyle.Bold); } }

        // =================================================================
        // GEOMETRIA
        // =================================================================

        public const int BorderRadius = 10;
        public const int CardPadding = 18;
        public const int HeaderHeight = 44;
        public const int FooterHeight = 52;
        public const int ToolbarIconSize = 28;

        // =================================================================
        // LOGO
        // =================================================================

        private static Image _logo;
        private static bool _logoLoaded;

        public static Image Logo
        {
            get
            {
                if (!_logoLoaded)
                {
                    _logoLoaded = true;
                    try
                    {
                        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        string[] candidates = new[]
                        {
                            Path.Combine(baseDir, @"..\..\..\..\..\..\..\..\Marketing\favicon.png"),
                            @"G:\AgroParallel\Marketing\favicon.png"
                        };
                        foreach (var c in candidates)
                        {
                            string full = Path.GetFullPath(c);
                            if (File.Exists(full))
                            {
                                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read))
                                    _logo = Image.FromStream(fs);
                                break;
                            }
                        }
                    }
                    catch { }
                }
                return _logo;
            }
        }

        // =================================================================
        // HELPERS DE RENDERING
        // =================================================================

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRoundedRect(Graphics g, Rectangle rect, Color color, int radius)
        {
            using (var path = RoundedRect(rect, radius))
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
            }
        }

        public static void DrawRoundedBorder(Graphics g, Rectangle rect, Color color, int radius, float width = 1f)
        {
            using (var path = RoundedRect(rect, radius))
            using (var pen = new Pen(color, width))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawPath(pen, path);
            }
        }

        // =================================================================
        // CONTROLES — Helpers de creación con estilo unificado
        // =================================================================

        public static void StyleInput(Control c)
        {
            c.BackColor = BgInput;
            c.ForeColor = TextPrimary;
            c.Font = new Font(FontFamily, 9f);
        }

        public static TextBox MkTextBox(int w = 160)
        {
            return new TextBox
            {
                Width = w, Height = 26,
                BackColor = BgInput, ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font(FontFamily, 9f)
            };
        }

        public static NumericUpDown MkNumeric(decimal min, decimal max, decimal value,
            int decimals = 0, decimal increment = 1m, int w = 90)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max,
                Value = Math.Max(min, Math.Min(max, value)),
                DecimalPlaces = decimals, Increment = increment,
                Width = w, Height = 26,
                BackColor = BgInput, ForeColor = Accent,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font(FontFamily, 9f, FontStyle.Bold)
            };
        }

        public static ComboBox MkCombo(int w = 180)
        {
            return new ComboBox
            {
                Width = w, Height = 28,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BgInput, ForeColor = TextPrimary,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(FontFamily, 9f)
            };
        }

        public static CheckBox MkCheck(string text, bool initial = false)
        {
            return new CheckBox
            {
                Text = "  " + text,
                Checked = initial,
                AutoSize = true,
                ForeColor = TextPrimary,
                BackColor = Color.Transparent,
                Font = FontBody
            };
        }

        // =================================================================
        // BOTONES
        // =================================================================

        public static Button MkToolbarButton(string text, int size = 40)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = TextSecondary,
                Font = new Font(FontFamily, 11f, FontStyle.Bold),
                Size = new Size(size, size),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = BgCardHover;
            return b;
        }

        public static Button MkButton(string text, Color bg, Color fg, int w = 120, int h = 34)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = fg,
                Font = FontButton,
                Size = new Size(w, h),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(
                Math.Min(255, bg.R + 18),
                Math.Min(255, bg.G + 18),
                Math.Min(255, bg.B + 18));
            return b;
        }

        public static Button MkAccentButton(string text, int w = 120, int h = 34)
        {
            var b = new Button
            {
                Text = text,
                Width = w, Height = h,
                FlatStyle = FlatStyle.Flat,
                BackColor = Accent,
                ForeColor = Color.FromArgb(5, 7, 9),
                Font = new Font(FontFamily, 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            b.FlatAppearance.MouseDownBackColor = AccentDim;
            return b;
        }

        public static Button MkSecondaryButton(string text, int w = 120, int h = 34)
        {
            var b = new Button
            {
                Text = text,
                Width = w, Height = h,
                FlatStyle = FlatStyle.Flat,
                BackColor = BgCard2,
                ForeColor = TextPrimary,
                Font = new Font(FontFamily, 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = BgCardHover;
            return b;
        }

        public static Button MkDangerButton(string text, int w = 120, int h = 34)
        {
            var b = MkSecondaryButton(text, w, h);
            b.BackColor = Color.FromArgb(45, 15, 16);
            b.ForeColor = Error;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 28, 30);
            return b;
        }

        // =================================================================
        // HEADER
        // =================================================================

        public static void PaintHeader(Graphics g, int width, string sectionName, bool showRtk = false)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var b = new SolidBrush(BgHeader))
                g.FillRectangle(b, 0, 0, width, HeaderHeight);

            using (var pen = new Pen(Border))
                g.DrawLine(pen, 0, HeaderHeight - 1, width, HeaderHeight - 1);

            int logoSize = HeaderHeight - 12;
            float textX = 12;
            if (Logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, 8, 6, logoSize, logoSize);
                textX = 8 + logoSize + 4;
            }

            using (var f = new Font(FontFamily, 11f, FontStyle.Bold))
            using (var br = new SolidBrush(TextPrimary))
                g.DrawString("Agro Parallel", f, br, textX, 13);

            if (!string.IsNullOrEmpty(sectionName))
            {
                using (var f = new Font(FontFamily, 9f, FontStyle.Bold))
                using (var br = new SolidBrush(Accent))
                {
                    var sz = g.MeasureString(sectionName, f);
                    g.DrawString(sectionName, f, br, (width - sz.Width) / 2f, 15);
                }
            }

            string time = DateTime.Now.ToString("HH:mm");
            using (var f = new Font(FontFamily, 10f, FontStyle.Bold))
            using (var br = new SolidBrush(TextSecondary))
            {
                var sz = g.MeasureString(time, f);
                g.DrawString(time, f, br, width - sz.Width - 40, 14);
            }
        }

        // =================================================================
        // KPI
        // =================================================================

        public static void PaintKpi(Graphics g, int x, int y, int w,
            string value, string unit, string label)
        {
            using (var fVal = new Font(FontFamily, 20f, FontStyle.Bold))
            using (var br = new SolidBrush(TextPrimary))
            {
                g.DrawString(value, fVal, br, x, y);
                if (!string.IsNullOrEmpty(unit))
                {
                    var szVal = g.MeasureString(value, fVal);
                    using (var fUnit = new Font(FontFamily, 9f))
                    using (var unitBr = new SolidBrush(TextSecondary))
                        g.DrawString(unit, fUnit, unitBr, x + szVal.Width + 2, y + 10);
                }
            }

            using (var fLbl = new Font(FontFamily, 8f))
            using (var br = new SolidBrush(TextFaint))
                g.DrawString(label, fLbl, br, x, y + 28);
        }

        // =================================================================
        // FORM & CARD
        // =================================================================

        public static void ApplyToForm(Form form)
        {
            form.BackColor = BgBlack;
            form.ForeColor = TextPrimary;
            form.Font = FontBody;
        }

        public static Panel MkCard(int width, int height, Color accentColor)
        {
            var card = new Panel
            {
                Size = new Size(width, height),
                BackColor = BgCard
            };
            var capturedAccent = accentColor;
            card.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                FillRoundedRect(g, rect, BgCard, BorderRadius);
                DrawRoundedBorder(g, rect, Border, BorderRadius);
                using (var b = new SolidBrush(capturedAccent))
                    g.FillRectangle(b, 0, BorderRadius, 3, card.Height - BorderRadius * 2);
            };
            return card;
        }
    }
}
