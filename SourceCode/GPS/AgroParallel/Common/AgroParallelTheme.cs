// ============================================================================
// AgroParallelTheme.cs - Paleta y constantes visuales del ecosistema Agro Parallel
// Ubicación: SourceCode/GPS/AgroParallel/Common/AgroParallelTheme.cs
// Target: net48 (C# 7.3)
//
// Theme operativo para cabina de tractor: 3 modos seleccionables
//   - Day:           claro gris-verdoso, alto contraste sin blanco puro
//   - Night:         dark grafito/verde para uso nocturno
//   - HighContrast:  sol fuerte, contraste máximo
//
// Verde marca: #71B528 (sampleado del logo oficial Agro Parallel 2026).
// Filosofia: 70% gris/fondo, 20% blanco-negro, 10% verde marca como
// SEÑAL (acción / activo / OK), no como decoración.
//
// Default = Night. Cambiar via Theme.Current = Theme.Mode.Day; (suscribirse
// a Theme.ModeChanged para repintar forms en vivo).
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AgroParallel.Common
{
    public static class Theme
    {
        // =================================================================
        // MODO ACTIVO
        // =================================================================

        public enum Mode { Day, Night, HighContrast }

        private static Mode _current = Mode.Night;

        public static Mode Current
        {
            get { return _current; }
            set
            {
                if (_current == value) return;
                _current = value;
                var h = ModeChanged;
                if (h != null) h(null, EventArgs.Empty);
            }
        }

        /// <summary>Forms se suscriben para repintar al cambiar el modo.</summary>
        public static event EventHandler ModeChanged;

        /// <summary>Helper interno: elige color segun modo activo.</summary>
        private static Color Pick(Color day, Color night, Color hc)
        {
            switch (_current)
            {
                case Mode.Day: return day;
                case Mode.HighContrast: return hc;
                default: return night;
            }
        }

        // =================================================================
        // FONDOS
        // =================================================================

        // Fondo del form (ex BgBlack — nombre conservado para back-compat).
        public static Color BgBlack
        {
            get { return Pick(
                Color.FromArgb(0xE9, 0xEC, 0xE8),   // Day
                Color.FromArgb(0x05, 0x08, 0x06),   // Night
                Color.FromArgb(0xD8, 0xDE, 0xD6));  // HC
            }
        }

        // Tarjetas / cards principales.
        public static Color BgCard
        {
            get { return Pick(
                Color.FromArgb(0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x17, 0x22, 0x18),
                Color.FromArgb(0xFF, 0xFF, 0xFF));
            }
        }

        // Tarjeta secundaria (sub-paneles).
        public static Color BgCard2
        {
            get { return Pick(
                Color.FromArgb(0xF7, 0xF9, 0xF6),
                Color.FromArgb(0x11, 0x1A, 0x14),
                Color.FromArgb(0xF0, 0xF4, 0xF0));
            }
        }

        // Hover sobre card.
        public static Color BgCardHover
        {
            get { return Pick(
                Color.FromArgb(0xEE, 0xF2, 0xEE),
                Color.FromArgb(0x1E, 0x28, 0x20),
                Color.FromArgb(0xE0, 0xE6, 0xE0));
            }
        }

        // Fondo de input (TextBox/ComboBox/NumericUpDown).
        public static Color BgInput
        {
            get { return Pick(
                Color.FromArgb(0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x0B, 0x12, 0x0E),
                Color.FromArgb(0xFF, 0xFF, 0xFF));
            }
        }

        // Header / sub-header de form.
        public static Color BgHeader
        {
            get { return Pick(
                Color.FromArgb(0xF7, 0xF9, 0xF6),
                Color.FromArgb(0x0B, 0x12, 0x0E),
                Color.FromArgb(0xFF, 0xFF, 0xFF));
            }
        }

        // Toolbar.
        public static Color BgToolbar
        {
            get { return Pick(
                Color.FromArgb(0xEE, 0xF2, 0xEE),
                Color.FromArgb(0x0E, 0x11, 0x17),
                Color.FromArgb(0xF0, 0xF4, 0xF0));
            }
        }

        // =================================================================
        // BORDES
        // =================================================================

        public static Color Border
        {
            get { return Pick(
                Color.FromArgb(0xCB, 0xD5, 0xCC),
                Color.FromArgb(0x26, 0x32, 0x29),
                Color.FromArgb(0x8D, 0x9A, 0x8E));
            }
        }

        public static Color BorderSoft
        {
            get { return Pick(
                Color.FromArgb(0xE0, 0xE6, 0xE0),
                Color.FromArgb(0x1A, 0x22, 0x1C),
                Color.FromArgb(0xB0, 0xB8, 0xB0));
            }
        }

        public static Color BorderLight
        {
            get { return Pick(
                Color.FromArgb(0xB7, 0xC4, 0xB5),
                Color.FromArgb(0x36, 0x45, 0x39),
                Color.FromArgb(0x6B, 0x75, 0x6E));
            }
        }

        // =================================================================
        // ACENTO — verde marca (#71B528, sampleado del logo oficial)
        // =================================================================
        // En Day y Night el verde marca es el mismo. En HighContrast bajamos
        // luminancia para mantener legibilidad sobre fondo claro.

        public static Color Accent
        {
            get { return Pick(
                Color.FromArgb(113, 181, 40),   // #71B528 Day
                Color.FromArgb(113, 181, 40),   // #71B528 Night
                Color.FromArgb(20, 140, 28));   // #148C1C HC (más oscuro)
            }
        }

        public static Color AccentDim
        {
            get { return Pick(
                Color.FromArgb(75, 120, 27),
                Color.FromArgb(75, 120, 27),
                Color.FromArgb(13, 96, 16));
            }
        }

        // Fondo selección activa.
        public static Color AccentDark
        {
            get { return Pick(
                Color.FromArgb(0xDF, 0xF4, 0xDC),  // verde claro relleno (día)
                Color.FromArgb(28, 50, 12),        // verde profundo (noche)
                Color.FromArgb(0xC5, 0xE5, 0xC2));
            }
        }

        public static Color AccentHover
        {
            get { return Pick(
                Color.FromArgb(135, 205, 60),
                Color.FromArgb(135, 205, 60),
                Color.FromArgb(47, 174, 52));
            }
        }

        // Verde brillante para señales criticas (linea AB activa, RTK fijo).
        public static Color AccentBright
        {
            get { return Pick(
                Color.FromArgb(47, 174, 52),       // un poco más oscuro de día
                Color.FromArgb(124, 255, 91),      // #7CFF5B (noche)
                Color.FromArgb(0, 160, 0));
            }
        }

        // =================================================================
        // TEXTO
        // =================================================================

        public static Color TextPrimary
        {
            get { return Pick(
                Color.FromArgb(0x10, 0x14, 0x10),
                Color.FromArgb(0xEA, 0xF2, 0xE8),
                Color.FromArgb(0x00, 0x00, 0x00));
            }
        }

        public static Color TextSecondary
        {
            get { return Pick(
                Color.FromArgb(0x4E, 0x5A, 0x50),
                Color.FromArgb(0xA8, 0xB6, 0xAA),
                Color.FromArgb(0x22, 0x28, 0x22));
            }
        }

        public static Color TextFaint
        {
            get { return Pick(
                Color.FromArgb(0x8A, 0x94, 0x8B),
                Color.FromArgb(0x6E, 0x7B, 0x72),
                Color.FromArgb(0x4E, 0x5A, 0x50));
            }
        }

        public static Color TextDisabled
        {
            get { return Pick(
                Color.FromArgb(0xB0, 0xB8, 0xB0),
                Color.FromArgb(0x5E, 0x6A, 0x61),
                Color.FromArgb(0x8D, 0x9A, 0x8E));
            }
        }

        // =================================================================
        // ESTADOS
        // =================================================================

        public static Color Ok { get { return Accent; } }

        public static Color Warning
        {
            get { return Pick(
                Color.FromArgb(0xF5, 0xB4, 0x00),  // #F5B400
                Color.FromArgb(0xFF, 0xB0, 0x00),  // #FFB000
                Color.FromArgb(0xD6, 0x8A, 0x00));
            }
        }

        public static Color Error
        {
            get { return Pick(
                Color.FromArgb(0xD9, 0x3A, 0x32),  // #D93A32
                Color.FromArgb(0xFF, 0x4D, 0x3D),  // #FF4D3D
                Color.FromArgb(0xC4, 0x00, 0x00));
            }
        }

        public static Color Info
        {
            get { return Pick(
                Color.FromArgb(0x19, 0x76, 0xD2),
                Color.FromArgb(0x4C, 0xA3, 0xFF),
                Color.FromArgb(0x0A, 0x4F, 0xA0));
            }
        }

        // =================================================================
        // SENSORES — colores de identificación (constantes a través de modos)
        // =================================================================

        public static readonly Color FertiLinea = Color.FromArgb(0, 200, 230);
        public static readonly Color FertiCostado = Color.FromArgb(245, 200, 35);
        public static readonly Color Herramienta = Color.FromArgb(255, 160, 0);
        public static readonly Color Turbina = Color.FromArgb(156, 100, 200);
        public static readonly Color Tolva = Color.FromArgb(140, 100, 75);

        // =================================================================
        // TIPOGRAFIA
        // =================================================================

        public static readonly string FontFamily = "Segoe UI";

        public static Font FontKpiValue { get { return new Font(FontFamily, 22f, FontStyle.Bold); } }
        public static Font FontKpiHuge { get { return new Font(FontFamily, 42f, FontStyle.Bold); } }   // datos críticos cabina
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
                            Path.Combine(baseDir, "Branding", "favicon.png"),
                            Path.Combine(baseDir, "favicon.png"),
                            Path.Combine(baseDir, @"..\..\..\..\..\..\..\..\Marketing\Identidad\favicon.png"),
                            @"G:\AgroParallel\Marketing\Identidad\favicon.png",
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
                ForeColor = Color.White,
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
            // Fondo de peligro adaptado al modo (rojo más translucido en día).
            b.BackColor = Pick(
                Color.FromArgb(0xFC, 0xE6, 0xE5),
                Color.FromArgb(45, 15, 16),
                Color.FromArgb(0xFC, 0xD4, 0xD2));
            b.ForeColor = Error;
            b.FlatAppearance.BorderColor = Pick(
                Color.FromArgb(0xE6, 0xB6, 0xB3),
                Color.FromArgb(90, 28, 30),
                Color.FromArgb(0xC4, 0x00, 0x00));
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

        // =================================================================
        // AUTO-MODE por horario
        // =================================================================

        /// <summary>
        /// Setea Mode segun horario local. 06:30–19:30 = Day, resto = Night.
        /// HighContrast solo se activa explicitamente por el usuario.
        /// </summary>
        public static void ApplyAutoMode()
        {
            var now = DateTime.Now.TimeOfDay;
            var dayStart = new TimeSpan(6, 30, 0);
            var dayEnd = new TimeSpan(19, 30, 0);
            Current = (now >= dayStart && now < dayEnd) ? Mode.Day : Mode.Night;
        }
    }
}
