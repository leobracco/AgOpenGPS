using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AgOpenGPS.Core.Translations;
using AgOpenGPS.Helpers;

namespace AgOpenGPS
{
    public partial class FormTermsAndConditions : Form
    {
        // Links Agro Parallel — abren en el navegador por defecto del sistema.
        private const string GitHubUrl = "https://agroparallel.com";
        private const string DiscourseUrl = "https://www.instagram.com/agro.parallel/";
        private const string YouTubeUrl = "https://agroparallel.com";

        public FormTermsAndConditions()
        {
            InitializeComponent();
            // Reducir flicker del repaint custom.
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
        }

        // Paleta PilotX con dos variantes: dark (default) y gris claro.
        // El usuario alterna desde el botón ☼ del header.
        private struct Palette
        {
            public Color Bg, BgPanel, Accent, AccentSoft, Text, TextDim, Border, LineMuted, ButtonText;
        }
        private static readonly Color Accent      = Color.FromArgb(74, 186, 62);   // #4ABA3E verde PilotX (constante)
        private static readonly Color AccentSoft  = Color.FromArgb(40, 100, 38);

        private static readonly Palette DarkTheme = new Palette
        {
            Bg         = Color.FromArgb(16, 22, 18),    // #101612
            BgPanel    = Color.FromArgb(14, 20, 17),
            Accent     = Accent,
            AccentSoft = AccentSoft,
            Text       = Color.FromArgb(226, 231, 226), // #E2E7E2
            TextDim    = Color.FromArgb(150, 160, 152),
            Border     = Color.FromArgb(83, 94, 84),    // #535E54
            LineMuted  = Color.FromArgb(70, 70, 70),
            ButtonText = Color.FromArgb(14, 20, 17),
        };
        private static readonly Palette LightTheme = new Palette
        {
            Bg         = Color.FromArgb(245, 247, 244), // #F5F7F4
            BgPanel    = Color.FromArgb(226, 231, 226), // #E2E7E2
            Accent     = Accent,
            AccentSoft = AccentSoft,
            Text       = Color.FromArgb(16, 22, 18),    // #101612
            TextDim    = Color.FromArgb(83, 94, 84),    // #535E54
            Border     = Color.FromArgb(197, 207, 197), // #C5CFC5
            LineMuted  = Color.FromArgb(197, 207, 197),
            ButtonText = Color.FromArgb(245, 247, 244),
        };

        private bool _darkMode = true;
        private Palette P => _darkMode ? DarkTheme : LightTheme;
        private Button _themeToggle;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Fondo + gradiente radial verde sutil (eco del splash del Hub).
            using (var brush = new SolidBrush(P.Bg))
                g.FillRectangle(brush, ClientRectangle);

            using (var path = new GraphicsPath())
            {
                path.AddEllipse(Width / 2 - 600, Height / 2 - 700, 1200, 1100);
                using (var pb = new PathGradientBrush(path))
                {
                    int glow = _darkMode ? 35 : 22;
                    pb.CenterColor = Color.FromArgb(glow, Accent.R, Accent.G, Accent.B);
                    pb.SurroundColors = new[] { Color.FromArgb(0, P.Bg) };
                    g.FillPath(pb, path);
                }
            }

            // Banda superior fina con acento (matchea borde superior del welcome).
            using (var band = new SolidBrush(Accent))
                g.FillRectangle(band, 0, 0, Width, 4);

            // Glyph: cuatro líneas paralelas + cursor verde (replica del splash SVG).
            int glyphCx = 90;
            int glyphCy = 80;
            int glyphSize = 88;
            DrawGlyph(g, glyphCx, glyphCy, glyphSize);

            // Wordmark "AGRO/PARALLEL"
            DrawWordmark(g, glyphCx + glyphSize / 2 + 30, glyphCy - 22);

            // Tagline bajo wordmark.
            using (var f = new Font("Tahoma", 10F, FontStyle.Regular))
            using (var br = new SolidBrush(P.TextDim))
            {
                string tag = "TÉRMINOS  ·  ACEPTACIÓN";
                g.DrawString(tag, f, br, glyphCx + glyphSize / 2 + 32, glyphCy + 22);
            }

            // Línea divisoria bajo el header.
            using (var pen = new Pen(Color.FromArgb(80, P.Border), 1))
                g.DrawLine(pen, 60, glyphCy + 70, Width - 60, glyphCy + 70);
        }

        private void DrawGlyph(Graphics g, int cx, int cy, int size)
        {
            int half = size / 2;
            int xL = cx - half;
            int xR = cx + half;
            int[] ys = { cy - 30, cy - 10, cy + 10, cy + 30 };

            using (var penLine = new Pen(P.LineMuted, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var penActive = new Pen(Accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(penLine, xL, ys[0], xR, ys[0]);
                g.DrawLine(penActive, xL, ys[1], xR, ys[1]);
                g.DrawLine(penLine, xL, ys[2], xR, ys[2]);
                g.DrawLine(penLine, xL, ys[3], xR, ys[3]);
            }
            // Cursor del tractor sobre la línea activa.
            using (var br = new SolidBrush(Accent))
                g.FillRectangle(br, cx + 18, ys[1] - 6, 12, 12);
        }

        private void DrawWordmark(Graphics g, int x, int y)
        {
            using (var fontMark = new Font("Tahoma", 28F, FontStyle.Bold))
            using (var fontSep  = new Font("Tahoma", 28F, FontStyle.Bold))
            using (var brText   = new SolidBrush(P.Text))
            using (var brAccent = new SolidBrush(Accent))
            {
                float cx = x;
                var s1 = "AGRO";
                var s2 = "/";
                var s3 = "PARALLEL";
                var sz1 = g.MeasureString(s1, fontMark);
                var sz2 = g.MeasureString(s2, fontSep);
                g.DrawString(s1, fontMark, brText,   cx,                       y);
                g.DrawString(s2, fontSep,  brAccent, cx + sz1.Width - 4,        y);
                g.DrawString(s3, fontMark, brText,   cx + sz1.Width + sz2.Width - 8, y);
            }
        }

        private void Form_About_Load(object sender, EventArgs e)
        {
            // Header dibujado a mano → ocultamos los labels viejos del title.
            labelTermsAndConditions.Visible = false;
            pictureBoxWarning.Visible = false;

            // Cuerpo: aviso destacado (queda visible, color claro).
            labelTerms.Text =
                "ADVERTENCIA — Leer atentamente antes de continuar." + Environment.NewLine + Environment.NewLine +
                "Este software de guiado y piloto automático asistido se entrega EN SU ESTADO ACTUAL, sin garantía alguna, expresa ni implícita. El usuario asume la totalidad del riesgo derivado de su uso." + Environment.NewLine + Environment.NewLine +
                "Agro Parallel, Leonardo Bracco y todo distribuidor, revendedor o integrador NO se hacen responsables por daños materiales, lesiones personales, pérdida de cultivos, lucro cesante ni perjuicios directos, indirectos, incidentales o consecuentes derivados del uso, mal uso o falla del sistema." + Environment.NewLine + Environment.NewLine +
                "El operador es el único responsable de mantener el control del vehículo en todo momento, vigilar el entorno y desactivar el piloto automático ante cualquier situación de riesgo.";
            labelTerms.BackColor = Color.Transparent;
            labelTerms.Font = new Font("Tahoma", 11.5F, FontStyle.Regular);
            labelTerms.Location = new Point(180, 175);
            labelTerms.Size = new Size(Width - 360, 190);

            // Panel scrollable de la licencia.
            textBoxLicense.BorderStyle = BorderStyle.FixedSingle;
            textBoxLicense.Font = new Font("Consolas", 10.5F, FontStyle.Regular);
            textBoxLicense.Location = new Point(180, 380);
            textBoxLicense.Size = new Size(Width - 360, 130);

            labelAgree.Location    = new Point(Width - 220, 535);
            labelDisagree.Location = new Point(Width - 380, 535);
            buttonGitHub.Location    = new Point(30, 200);
            buttonDiscourse.Location = new Point(30, 320);
            buttonYouTube.Visible = false;

            // Botón ☼ para alternar dark / gris claro (esquina superior derecha,
            // debajo de la banda de acento y a la izquierda de la versión).
            _themeToggle = new Button
            {
                Text = "☼",
                Size = new Size(40, 40),
                Location = new Point(Width - 60, 20),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Symbol", 14F, FontStyle.Regular),
                Cursor = Cursors.Hand,
                TabStop = false,
                UseVisualStyleBackColor = false,
            };
            _themeToggle.FlatAppearance.BorderSize = 1;
            _themeToggle.Click += (s, ev) => { _darkMode = !_darkMode; ApplyTheme(); };
            Controls.Add(_themeToggle);
            _themeToggle.BringToFront();

            // Touch: sacar foco del textbox para que no auto-abra TabTip.
            textBoxLicense.TabStop = false;
            textBoxLicense.GotFocus += (s, ev) => BeginInvoke((Action)(() => labelAgree.Focus()));
            ActiveControl = labelAgree;
            TryHideTouchKeyboard();

            ApplyTheme();

            if (!ScreenHelper.IsOnScreen(Bounds))
            {
                Top = 0;
                Left = 0;
            }
        }

        private void ApplyTheme()
        {
            BackColor = P.Bg;
            labelVersion.Text = "v";
            labelVersion.ForeColor = P.TextDim;
            labelVersion.BackColor = Color.Transparent;
            labelVersionActual.ForeColor = P.TextDim;
            labelVersionActual.BackColor = Color.Transparent;
            labelVersionActual.Text = Program.SemVer;
            labelVersion.Location = new Point(Width - 200, 65);
            labelVersionActual.Location = new Point(Width - 175, 65);

            labelTerms.ForeColor = P.Text;
            labelTerms.BackColor = Color.Transparent;

            textBoxLicense.BackColor = P.BgPanel;
            textBoxLicense.ForeColor = P.TextDim;

            StylePrimaryButton(labelAgree, "Acepto");
            StyleSecondaryButton(labelDisagree, "No acepto");
            StyleLinkButton(buttonGitHub,    "agroparallel.com");
            StyleLinkButton(buttonDiscourse, "@agro.parallel");

            if (_themeToggle != null)
            {
                _themeToggle.Text = _darkMode ? "☼" : "☾";
                _themeToggle.BackColor = Color.Transparent;
                _themeToggle.ForeColor = P.TextDim;
                _themeToggle.FlatAppearance.BorderColor = P.Border;
                _themeToggle.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, Accent.R, Accent.G, Accent.B);
            }

            Invalidate();
        }

        private void StylePrimaryButton(Button b, string text)
        {
            b.Text = text;
            b.Image = null;
            b.BackColor = Accent;
            b.ForeColor = P.ButtonText;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(94, 206, 82);
            b.FlatAppearance.MouseDownBackColor = AccentSoft;
            b.Font = new Font("Tahoma", 13F, FontStyle.Bold);
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Size = new Size(150, 56);
            b.UseVisualStyleBackColor = false;
        }

        private void StyleSecondaryButton(Button b, string text)
        {
            b.Text = text;
            b.Image = null;
            b.BackColor = Color.Transparent;
            b.ForeColor = P.TextDim;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = P.Border;
            int hoverAlpha = _darkMode ? 30 : 60;
            int downAlpha  = _darkMode ? 60 : 100;
            byte tint = (byte)(_darkMode ? 255 : 0);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(hoverAlpha, tint, tint, tint);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(downAlpha, tint, tint, tint);
            b.Font = new Font("Tahoma", 12F, FontStyle.Regular);
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Size = new Size(150, 56);
            b.UseVisualStyleBackColor = false;
        }

        private void StyleLinkButton(Button b, string text)
        {
            b.Text = text;
            b.Image = null;
            b.BackColor = Color.Transparent;
            b.ForeColor = P.TextDim;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = P.Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, Accent.R, Accent.G, Accent.B);
            b.Font = new Font("Tahoma", 10F, FontStyle.Regular);
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Size = new Size(130, 44);
            b.UseVisualStyleBackColor = false;
        }

        // ---- Supresión del teclado táctil de Windows (TabTip) -------------------
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private static void TryHideTouchKeyboard()
        {
            try
            {
                IntPtr h = FindWindow("IPTip_Main_Window", null);
                if (h != IntPtr.Zero)
                    SendMessage(h, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
            }
            catch { }
        }

        private void buttonGitHub_Click(object sender, EventArgs e)    { Process.Start(GitHubUrl); }
        private void buttonDiscourse_Click(object sender, EventArgs e) { Process.Start(DiscourseUrl); }
        private void buttonYouTube_Click(object sender, EventArgs e)   { Process.Start(YouTubeUrl); }
    }
}
