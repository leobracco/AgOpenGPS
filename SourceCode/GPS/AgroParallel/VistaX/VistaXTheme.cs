// ============================================================================
// VistaXTheme.cs - Paleta y constantes visuales del sistema Agro Parallel
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/VistaXTheme.cs
// Target: net48 (C# 7.3)
//
// Fuente unica de verdad para colores, fuentes, radios y helpers de
// rendering. Basado en los mockups de CentriX-Spark (fondo negro puro,
// verde acento #7ac943, tipografia limpia tipo tablet agricola).
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
        // COLORES
        // =================================================================

        // Fondos.
        public static readonly Color BgBlack = Color.FromArgb(0, 0, 0);          // Fondo principal
        public static readonly Color BgCard = Color.FromArgb(18, 18, 20);        // Cards/paneles
        public static readonly Color BgCardHover = Color.FromArgb(24, 24, 28);   // Card hover
        public static readonly Color BgInput = Color.FromArgb(12, 12, 14);       // Inputs/fields
        public static readonly Color BgHeader = Color.FromArgb(10, 10, 12);      // Header bar
        public static readonly Color BgToolbar = Color.FromArgb(14, 14, 16);     // Toolbar/footer

        // Bordes.
        public static readonly Color Border = Color.FromArgb(32, 32, 36);        // Borde sutil
        public static readonly Color BorderLight = Color.FromArgb(44, 44, 48);   // Borde visible

        // Acento (verde Agro Parallel — del logo).
        public static readonly Color Accent = Color.FromArgb(122, 201, 67);      // #7ac943
        public static readonly Color AccentDim = Color.FromArgb(80, 140, 40);    // Verde apagado
        public static readonly Color AccentDark = Color.FromArgb(40, 80, 20);    // Verde muy oscuro (bg activo)

        // Texto.
        public static readonly Color TextPrimary = Color.FromArgb(240, 242, 245);  // Blanco suave
        public static readonly Color TextSecondary = Color.FromArgb(140, 145, 155); // Gris medio
        public static readonly Color TextFaint = Color.FromArgb(75, 78, 85);       // Gris oscuro (labels)
        public static readonly Color TextDisabled = Color.FromArgb(50, 52, 58);    // Deshabilitado

        // Estados.
        public static readonly Color Ok = Color.FromArgb(122, 201, 67);          // Verde OK
        public static readonly Color Warning = Color.FromArgb(245, 166, 35);     // Amarillo advertencia
        public static readonly Color Error = Color.FromArgb(231, 76, 76);        // Rojo alarma
        public static readonly Color Info = Color.FromArgb(0, 176, 255);         // Azul info

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

        public const int BorderRadius = 8;
        public const int CardPadding = 14;
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
                        // Sube desde SourceCode/GPS/ → repo root → AgroParallel raíz.
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
                    catch { /* sin logo: fallback a texto */ }
                }
                return _logo;
            }
        }

        // =================================================================
        // HELPERS DE RENDERING
        // =================================================================

        /// Dibuja un rectangulo con bordes redondeados.
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

        /// Rellena un rectangulo redondeado.
        public static void FillRoundedRect(Graphics g, Rectangle rect, Color color, int radius)
        {
            using (var path = RoundedRect(rect, radius))
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
            }
        }

        /// Dibuja borde redondeado.
        public static void DrawRoundedBorder(Graphics g, Rectangle rect, Color color, int radius, float width = 1f)
        {
            using (var path = RoundedRect(rect, radius))
            using (var pen = new Pen(color, width))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawPath(pen, path);
            }
        }

        /// Crea un boton estilo toolbar (icono + texto, fondo transparente).
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

        /// Crea un boton pill (texto, fondo, foreground).
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
                Math.Min(255, bg.R + 20),
                Math.Min(255, bg.G + 20),
                Math.Min(255, bg.B + 20));
            return b;
        }

        /// Crea un boton de acento verde.
        public static Button MkAccentButton(string text, int w = 120, int h = 34)
        {
            return MkButton(text, Accent, BgBlack, w, h);
        }

        /// Dibuja un header estilo mockup: "🌿 Agro Parallel  RTK |||  16:20  ≡"
        public static void PaintHeader(Graphics g, int width, string sectionName, bool showRtk = false)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Fondo header.
            using (var b = new SolidBrush(BgHeader))
                g.FillRectangle(b, 0, 0, width, HeaderHeight);

            // Linea inferior.
            using (var pen = new Pen(Border))
                g.DrawLine(pen, 0, HeaderHeight - 1, width, HeaderHeight - 1);

            // Logo "Agro Parallel".
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

            // Section name.
            if (!string.IsNullOrEmpty(sectionName))
            {
                using (var f = new Font(FontFamily, 9f, FontStyle.Bold))
                using (var br = new SolidBrush(Accent))
                {
                    var sz = g.MeasureString(sectionName, f);
                    g.DrawString(sectionName, f, br, (width - sz.Width) / 2f, 15);
                }
            }

            // Hora.
            string time = DateTime.Now.ToString("HH:mm");
            using (var f = new Font(FontFamily, 10f, FontStyle.Bold))
            using (var br = new SolidBrush(TextSecondary))
            {
                var sz = g.MeasureString(time, f);
                g.DrawString(time, f, br, width - sz.Width - 40, 14);
            }
        }

        /// Dibuja un KPI grande (numero + label) como en el mockup del Monitor.
        public static void PaintKpi(Graphics g, int x, int y, int w,
            string value, string unit, string label)
        {
            // Value (grande).
            using (var fVal = new Font(FontFamily, 20f, FontStyle.Bold))
            using (var br = new SolidBrush(TextPrimary))
            {
                g.DrawString(value, fVal, br, x, y);
                // Unit (chico, al lado).
                if (!string.IsNullOrEmpty(unit))
                {
                    var szVal = g.MeasureString(value, fVal);
                    using (var fUnit = new Font(FontFamily, 9f))
                    using (var unitBr = new SolidBrush(TextSecondary))
                        g.DrawString(unit, fUnit, unitBr, x + szVal.Width + 2, y + 10);
                }
            }

            // Label (abajo).
            using (var fLbl = new Font(FontFamily, 8f))
            using (var br = new SolidBrush(TextFaint))
                g.DrawString(label, fLbl, br, x, y + 28);
        }

        /// Aplica tema oscuro a un Form.
        public static void ApplyToForm(Form form)
        {
            form.BackColor = BgBlack;
            form.ForeColor = TextPrimary;
            form.Font = FontBody;
        }

        /// Crea una card con bordes redondeados y accent bar izquierda.
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
                // Accent bar izquierda.
                using (var b = new SolidBrush(capturedAccent))
                {
                    var accentRect = new Rectangle(0, BorderRadius, 3, card.Height - BorderRadius * 2);
                    g.FillRectangle(b, accentRect);
                }
            };
            return card;
        }
    }
}
