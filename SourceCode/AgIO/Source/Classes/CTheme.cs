using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgIO
{
    public static class CTheme
    {
        public static readonly Color BgApp = Color.FromArgb(0xF5, 0xF7, 0xF4);
        public static readonly Color BgPanel = Color.White;
        public static readonly Color BgRail = Color.FromArgb(0xE2, 0xE7, 0xE2);
        public static readonly Color Accent = Color.FromArgb(0x4A, 0xBA, 0x3E);
        public static readonly Color AccentDark = Color.FromArgb(0x2E, 0x8E, 0x25);
        public static readonly Color Text = Color.FromArgb(0x10, 0x16, 0x12);
        public static readonly Color MutedText = Color.FromArgb(0x53, 0x5E, 0x54);
        public static readonly Color Border = Color.FromArgb(0xC5, 0xCF, 0xC5);
        public static readonly Color Danger = Color.FromArgb(0xD9, 0x3A, 0x32);
        public static readonly Color Warning = Color.FromArgb(0xF5, 0xB4, 0x00);
        public static readonly Color IconTile = Color.FromArgb(0xF5, 0xF7, 0xF4);
        public static readonly Color IconTileHover = Color.FromArgb(0xEA, 0xF2, 0xE8);
        public static readonly Color IconTileDown = Color.FromArgb(0xDF, 0xF4, 0xDC);
        public static readonly Color StatusOkSurface = Color.FromArgb(0xDF, 0xF4, 0xDC);
        public static readonly Color StatusWarnSurface = Color.FromArgb(0xFF, 0xF4, 0xD8);
        public static readonly Color StatusErrorSurface = Color.FromArgb(0xFF, 0xE8, 0xE5);
        public static readonly Color StatusOffSurface = Color.FromArgb(0xE2, 0xE7, 0xE2);

        private const string TagApplied = "_pilotxAgioTheme_";

        public static void Apply(Form form)
        {
            if (form == null) return;
            if (form.Tag is string tag && tag == TagApplied) return;

            form.Tag = TagApplied;
            form.BackColor = BgApp;
            form.ForeColor = Text;
            try { form.Font = new Font("Segoe UI", 10.5F, FontStyle.Regular); } catch { }

            form.Paint -= OnFormPaint;
            form.Paint += OnFormPaint;
            form.ControlAdded -= OnControlAdded;
            form.ControlAdded += OnControlAdded;

            ApplyToControls(form.Controls);
        }

        public static void ApplyOpenForms()
        {
            foreach (Form form in Application.OpenForms)
            {
                if (!(form.Tag is string tag && tag == TagApplied))
                    Apply(form);
                else
                    ApplyToControls(form.Controls);
            }
        }

        public static void ApplyToControls(Control.ControlCollection controls)
        {
            if (controls == null) return;

            foreach (Control c in controls)
            {
                ApplyToControl(c);
                if (c.HasChildren)
                    ApplyToControls(c.Controls);
            }
        }

        private static void ApplyToControl(Control c)
        {
            if (c == null) return;

            switch (c)
            {
                case Button btn:
                    StyleButton(btn);
                    break;
                case CheckBox chk:
                    StyleCheckBox(chk);
                    break;
                case RadioButton rb:
                    rb.ForeColor = Text;
                    rb.BackColor = Color.Transparent;
                    break;
                case Label lbl:
                    StyleLabel(lbl);
                    break;
                case TextBox tb:
                    tb.BackColor = Color.White;
                    tb.ForeColor = Text;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case NumericUpDown nud:
                    nud.BackColor = Color.White;
                    nud.ForeColor = Text;
                    break;
                case ComboBox cb:
                    cb.BackColor = Color.White;
                    cb.ForeColor = Text;
                    cb.FlatStyle = FlatStyle.Flat;
                    break;
                case ListBox lb:
                    lb.BackColor = Color.White;
                    lb.ForeColor = Text;
                    lb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ListView lv:
                    lv.BackColor = Color.White;
                    lv.ForeColor = Text;
                    break;
                case RichTextBox rtb:
                    rtb.BackColor = Color.White;
                    rtb.ForeColor = Text;
                    rtb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case TabPage tp:
                    tp.BackColor = BgApp;
                    tp.ForeColor = Text;
                    break;
                case GroupBox gb:
                    gb.ForeColor = AccentDark;
                    gb.BackColor = Color.Transparent;
                    break;
                case TableLayoutPanel tlp:
                    if (IsDesignerNeutral(tlp.BackColor))
                        tlp.BackColor = Color.Transparent;
                    tlp.ForeColor = Text;
                    break;
                case Panel pn:
                    if (IsDesignerNeutral(pn.BackColor))
                        pn.BackColor = BgPanel;
                    pn.ForeColor = Text;
                    break;
                case StatusStrip ss:
                    StyleStatusStrip(ss);
                    break;
                case MenuStrip ms:
                    StyleMenuStrip(ms);
                    break;
                case ToolStrip ts:
                    StyleToolStrip(ts);
                    break;
                case PictureBox pb:
                    if (IsDesignerNeutral(pb.BackColor) || pb.BackColor == Color.Black)
                        pb.BackColor = BgPanel;
                    break;
            }
        }

        private static bool IsDesignerNeutral(Color c)
        {
            return c == Color.Gainsboro
                   || c == SystemColors.Control
                   || c == SystemColors.ControlLight
                   || c == SystemColors.ControlLightLight
                   || c == SystemColors.InactiveCaption
                   || c == Color.WhiteSmoke;
        }

        private static void StyleButton(Button b)
        {
            bool imageButton = string.IsNullOrWhiteSpace(b.Text)
                               && (b.Image != null || b.BackgroundImage != null);

            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Font = new Font("Segoe UI", b.Font.Size, b.Font.Style);

            if (imageButton)
            {
                b.ForeColor = Text;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.Transparent;
                b.FlatAppearance.MouseDownBackColor = Color.Transparent;
                b.Paint -= OnImageButtonPaint;
                b.Paint += OnImageButtonPaint;
                return;
            }

            b.Paint -= OnImageButtonPaint;
            Color border = ResolveStateBorder(b.BackColor);
            b.BackColor = ResolveStateSurface(b.BackColor, BgPanel);
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = IconTileHover;
            b.FlatAppearance.MouseDownBackColor = IconTileDown;
        }

        private static void StyleCheckBox(CheckBox chk)
        {
            bool imageButton = chk.Appearance == Appearance.Button
                               || chk.Image != null
                               || chk.BackgroundImage != null;

            chk.FlatStyle = FlatStyle.Flat;
            chk.UseVisualStyleBackColor = false;
            chk.ForeColor = Text;
            chk.BackColor = imageButton ? ResolveStateSurface(chk.BackColor, IconTile) : Color.Transparent;
            chk.FlatAppearance.BorderColor = imageButton ? Border : Accent;
            chk.FlatAppearance.BorderSize = imageButton ? 1 : 0;
            chk.FlatAppearance.CheckedBackColor = IconTileDown;
            chk.FlatAppearance.MouseOverBackColor = IconTileHover;
            chk.FlatAppearance.MouseDownBackColor = IconTileDown;
        }

        private static void StyleLabel(Label l)
        {
            l.BackColor = Color.Transparent;

            if (l.ForeColor == Color.Red || l.ForeColor == Color.OrangeRed
                || l.ForeColor == Color.DarkRed || l.ForeColor == Color.Crimson)
            {
                l.ForeColor = Danger;
                return;
            }

            if (l.ForeColor == Color.Green || l.ForeColor == Color.LimeGreen
                || l.ForeColor == Color.DarkGreen)
            {
                l.ForeColor = AccentDark;
                return;
            }

            string name = (l.Name ?? string.Empty).ToLowerInvariant();
            bool muted = name.Contains("hint") || name.Contains("info")
                         || name.Contains("status")
                         || (l.Font != null && l.Font.Size < 9);
            l.ForeColor = muted ? MutedText : Text;
        }

        private static void StyleStatusStrip(StatusStrip s)
        {
            s.BackColor = BgRail;
            s.ForeColor = Text;
            s.RenderMode = ToolStripRenderMode.Professional;
            s.Renderer = new APRenderer();
            foreach (ToolStripItem it in s.Items) StyleToolStripItem(it);
        }

        private static void StyleMenuStrip(MenuStrip m)
        {
            m.BackColor = BgRail;
            m.ForeColor = Text;
            m.RenderMode = ToolStripRenderMode.Professional;
            m.Renderer = new APRenderer();
            foreach (ToolStripItem it in m.Items) StyleToolStripItem(it);
        }

        private static void StyleToolStrip(ToolStrip t)
        {
            t.BackColor = BgRail;
            t.ForeColor = Text;
            t.RenderMode = ToolStripRenderMode.Professional;
            t.Renderer = new APRenderer();
            foreach (ToolStripItem it in t.Items) StyleToolStripItem(it);
        }

        private static void StyleToolStripItem(ToolStripItem it)
        {
            it.ForeColor = Text;
            it.BackColor = BgRail;
            if (it is ToolStripDropDownItem dd && dd.DropDown != null)
            {
                dd.DropDown.BackColor = BgRail;
                dd.DropDown.ForeColor = Text;
                foreach (ToolStripItem child in dd.DropDownItems)
                    StyleToolStripItem(child);
            }
        }

        private static void OnFormPaint(object sender, PaintEventArgs e)
        {
            if (!(sender is Form f)) return;

            // Borde fino general del form.
            using (var pen = new Pen(Border, 1f))
                e.Graphics.DrawRectangle(pen, 0, 0, f.ClientSize.Width - 1, f.ClientSize.Height - 1);

            // Ribbon de marca: barra de acento de 3px arriba con un degradé sutil
            // del acento primario al acento oscuro. Da consistencia visual a toda
            // la suite (CoreX, FormProfiles, FormTimedMessage, etc.) y deja claro
            // que el form forma parte del ecosistema Agro Parallel sin agregar
            // controles ni gastar espacio útil. Idempotente — se redibuja en cada
            // Paint sin acumular handlers.
            const int RibbonHeight = 3;
            if (f.ClientSize.Width > 4)
            {
                var ribbonRect = new Rectangle(0, 0, f.ClientSize.Width, RibbonHeight);
                using (var brush = new LinearGradientBrush(
                    ribbonRect, Accent, AccentDark, LinearGradientMode.Horizontal))
                {
                    e.Graphics.FillRectangle(brush, ribbonRect);
                }
            }
        }

        private static void OnImageButtonPaint(object sender, PaintEventArgs e)
        {
            var b = sender as Button;
            if (b == null) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var rect = new Rectangle(0, 0, b.Width - 1, b.Height - 1);
            Color surface = ResolveStateSurface(b.BackColor, IconTile);
            Color border = ResolveStateBorder(b.BackColor);

            using (var brush = new SolidBrush(surface))
                e.Graphics.FillRectangle(brush, rect);
            using (var pen = new Pen(border, 1f))
                e.Graphics.DrawRectangle(pen, rect);

            Image img = b.Image ?? b.BackgroundImage;
            if (img == null) return;

            int pad = System.Math.Max(3, System.Math.Min(b.Width, b.Height) / 10);
            float scale = System.Math.Min(
                (float)(b.Width - pad * 2) / img.Width,
                (float)(b.Height - pad * 2) / img.Height);
            int w = System.Math.Max(1, (int)(img.Width * scale));
            int h = System.Math.Max(1, (int)(img.Height * scale));
            int x = (b.Width - w) / 2;
            int y = (b.Height - h) / 2;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(img, new Rectangle(x, y, w, h));
        }

        private static Color ResolveStateSurface(Color current, Color fallback)
        {
            if (current == Color.LimeGreen || current == Color.LightGreen
                || current == Color.PaleGreen || current == Color.Green)
                return StatusOkSurface;

            if (current == Color.Red || current == Color.Salmon
                || current == Color.LightSalmon || current == Color.OrangeRed)
                return StatusErrorSurface;

            if (current == Color.Yellow || current == Color.Gold)
                return StatusWarnSurface;

            if (current == Color.Gainsboro || current == Color.Silver
                || current == SystemColors.Control)
                return StatusOffSurface;

            return fallback;
        }

        private static Color ResolveStateBorder(Color current)
        {
            if (current == Color.LimeGreen || current == Color.LightGreen
                || current == Color.PaleGreen || current == Color.Green)
                return Accent;

            if (current == Color.Red || current == Color.Salmon
                || current == Color.LightSalmon || current == Color.OrangeRed)
                return Danger;

            if (current == Color.Yellow || current == Color.Gold)
                return Warning;

            return Border;
        }

        private static void OnControlAdded(object sender, ControlEventArgs e)
        {
            ApplyToControl(e.Control);
            if (e.Control.HasChildren)
                ApplyToControls(e.Control.Controls);
        }

        private class APRenderer : ToolStripProfessionalRenderer
        {
            public APRenderer() : base(new APColors()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Text;
                base.OnRenderItemText(e);
            }
        }

        private class APColors : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => BgRail;
            public override Color ToolStripGradientMiddle => BgRail;
            public override Color ToolStripGradientEnd => BgRail;
            public override Color MenuStripGradientBegin => BgRail;
            public override Color MenuStripGradientEnd => BgRail;
            public override Color StatusStripGradientBegin => BgRail;
            public override Color StatusStripGradientEnd => BgRail;
            public override Color MenuItemSelected => IconTileHover;
            public override Color MenuItemSelectedGradientBegin => IconTileHover;
            public override Color MenuItemSelectedGradientEnd => IconTileHover;
            public override Color MenuItemPressedGradientBegin => IconTileDown;
            public override Color MenuItemPressedGradientEnd => IconTileDown;
            public override Color MenuItemBorder => Accent;
            public override Color ButtonSelectedBorder => Accent;
            public override Color ButtonSelectedHighlight => IconTileHover;
            public override Color ImageMarginGradientBegin => BgRail;
            public override Color ImageMarginGradientMiddle => BgRail;
            public override Color ImageMarginGradientEnd => BgRail;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
            public override Color ToolStripBorder => Border;
        }
    }
}
