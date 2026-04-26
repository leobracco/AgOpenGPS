// ============================================================================
// ShapefileLegendControl.cs - Panel de dosis en vivo + control manual
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AgroParallel.Common
{
    public class ShapefileLegendControl : UserControl
    {
        private string _fieldName;
        private double _min, _max, _current;
        private bool _hasCurrent;

        public struct MotorDosis
        {
            public string Nombre;
            public double Objetivo;
            public double Real;
            public bool Activo;
            public bool Manual;
            public double ManualDosis;
        }
        public MotorDosis[] Motores = new MotorDosis[2];
        private bool _hasMotores;
        private bool _connected;
        private bool _buttonsCreated;

        // Evento: (motorIdx, manual, dosis).
        public event Action<int, bool, double> MotorManualChanged;

        private Button[] _btnMan = new Button[2];
        private Button[] _btnUp = new Button[2];
        private Button[] _btnDn = new Button[2];
        private Label[] _lblVal = new Label[2];

        private static readonly Color CBg = Color.FromArgb(230, 8, 8, 10);
        private static readonly Color CBorder = Color.FromArgb(40, 40, 44);
        private static readonly Color CText = Color.FromArgb(235, 235, 235);
        private static readonly Color CTextDim = Color.FromArgb(130, 135, 145);
        private static readonly Color CAccent = Color.FromArgb(122, 201, 67);
        private static readonly Color CWarn = Color.FromArgb(255, 220, 0);
        private static readonly Color CError = Color.FromArgb(220, 40, 0);

        public ShapefileLegendControl()
        {
            DoubleBuffered = true;
            Size = new Size(150, 200);
            BackColor = Color.Black;
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint, true);
        }

        public void SetLegend(string f, double min, double max) { _fieldName = f; _min = min; _max = max; Invalidate(); }
        public void Clear() { _fieldName = null; _hasCurrent = false; _hasMotores = false; Invalidate(); }
        public void SetCurrent(double v, bool i) { _current = v; _hasCurrent = i; Invalidate(); }
        public void SetConnected(bool c) { _connected = c; Invalidate(); }
        public void SetUdpStatus(bool a, int p, DateTime? l) { _connected = a; Invalidate(); }
        public bool HasData { get { return !string.IsNullOrEmpty(_fieldName) || _hasMotores; } }

        public void SetMotorDosis(int idx, string nombre, double objetivo, double real, bool activo)
        {
            if (idx < 0 || idx >= 2) return;
            Motores[idx].Nombre = nombre;
            Motores[idx].Objetivo = objetivo;
            Motores[idx].Real = real;
            Motores[idx].Activo = activo;
            _hasMotores = true;
            Invalidate();
        }

        private void CreateButtons()
        {
            if (_buttonsCreated) return;
            _buttonsCreated = true;

            for (int mi = 0; mi < 2; mi++)
            {
                int capturedMi = mi;
                var font = new Font("Segoe UI", 7f, FontStyle.Bold);

                _btnMan[mi] = new Button
                {
                    Text = "MAN", FlatStyle = FlatStyle.Flat, Font = font,
                    Size = new Size(34, 18), BackColor = Color.FromArgb(30, 30, 34),
                    ForeColor = CTextDim, Cursor = Cursors.Hand, Visible = false
                };
                _btnMan[mi].FlatAppearance.BorderSize = 0;
                _btnMan[mi].Click += (s, e) => ToggleManual(capturedMi);
                Controls.Add(_btnMan[mi]);

                _btnDn[mi] = new Button
                {
                    Text = "\u25BC", FlatStyle = FlatStyle.Flat, Font = font,
                    Size = new Size(22, 18), BackColor = Color.FromArgb(30, 30, 34),
                    ForeColor = CText, Cursor = Cursors.Hand, Visible = false
                };
                _btnDn[mi].FlatAppearance.BorderSize = 0;
                _btnDn[mi].Click += (s, e) => AdjustManual(capturedMi, -5);
                Controls.Add(_btnDn[mi]);

                _lblVal[mi] = new Label
                {
                    Text = "--", Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    ForeColor = CWarn, BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Size = new Size(36, 18), Visible = false
                };
                Controls.Add(_lblVal[mi]);

                _btnUp[mi] = new Button
                {
                    Text = "\u25B2", FlatStyle = FlatStyle.Flat, Font = font,
                    Size = new Size(22, 18), BackColor = Color.FromArgb(30, 30, 34),
                    ForeColor = CText, Cursor = Cursors.Hand, Visible = false
                };
                _btnUp[mi].FlatAppearance.BorderSize = 0;
                _btnUp[mi].Click += (s, e) => AdjustManual(capturedMi, 5);
                Controls.Add(_btnUp[mi]);
            }
        }

        private void ToggleManual(int mi)
        {
            Motores[mi].Manual = !Motores[mi].Manual;
            if (Motores[mi].Manual && Motores[mi].ManualDosis <= 0)
                Motores[mi].ManualDosis = Motores[mi].Objetivo > 0 ? Motores[mi].Objetivo : 100;

            _btnMan[mi].Text = Motores[mi].Manual ? "AUTO" : "MAN";
            _btnMan[mi].ForeColor = Motores[mi].Manual ? CWarn : CTextDim;
            UpdateManualUI(mi);

            var h = MotorManualChanged;
            if (h != null) h(mi, Motores[mi].Manual, Motores[mi].ManualDosis);
        }

        private void AdjustManual(int mi, double delta)
        {
            if (!Motores[mi].Manual) return;
            Motores[mi].ManualDosis = Math.Max(0, Motores[mi].ManualDosis + delta);
            UpdateManualUI(mi);

            var h = MotorManualChanged;
            if (h != null) h(mi, true, Motores[mi].ManualDosis);
        }

        private void UpdateManualUI(int mi)
        {
            if (_lblVal[mi] == null) return;
            bool m = Motores[mi].Manual;
            _lblVal[mi].Text = m ? Motores[mi].ManualDosis.ToString("F0") : "--";
            _lblVal[mi].Visible = m;
            _btnUp[mi].Visible = m;
            _btnDn[mi].Visible = m;
        }

        // Posiciones de botones por motor (se calculan en OnPaint).
        private int[] _motorBtnY = new int[2];

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            {
                using (var b = new SolidBrush(CBg)) g.FillPath(b, path);
                using (var p = new Pen(CBorder)) g.DrawPath(p, path);
            }

            int y = 8;

            using (var b = new SolidBrush(_connected ? CAccent : Color.FromArgb(60, 60, 65)))
                g.FillEllipse(b, Width - 16, y, 8, 8);

            if (_hasMotores)
            {
                for (int mi = 0; mi < 2; mi++)
                {
                    var m = Motores[mi];
                    if (string.IsNullOrEmpty(m.Nombre) && m.Objetivo <= 0 && !m.Activo) continue;

                    Color mColor = mi == 0 ? CAccent : Color.FromArgb(230, 160, 30);

                    using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                    using (var b = new SolidBrush(mColor))
                        g.DrawString(m.Nombre ?? ("M" + mi), f, b, 8, y);

                    if (m.Manual)
                    {
                        using (var f = new Font("Segoe UI", 6.5f, FontStyle.Bold))
                        using (var b = new SolidBrush(CWarn))
                            g.DrawString("MANUAL", f, b, Width - 52, y + 2);
                    }
                    y += 14;

                    using (var f = new Font("Segoe UI", 6.5f))
                    using (var b = new SolidBrush(CTextDim))
                        g.DrawString("OBJ", f, b, 8, y);
                    using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
                    using (var b = new SolidBrush(CText))
                        g.DrawString(m.Objetivo.ToString("F1", CultureInfo.InvariantCulture), f, b, 32, y - 3);
                    y += 18;

                    int vuW = Width - 18, vuH = 8;
                    var vuRect = new Rectangle(8, y, vuW, vuH);
                    using (var b = new SolidBrush(Color.FromArgb(22, 22, 26)))
                        g.FillRectangle(b, vuRect);

                    double pct = m.Objetivo > 0 ? Math.Min(1.5, m.Real / m.Objetivo) : 0;
                    int fillW = (int)(vuW * Math.Min(1.0, pct));

                    Color fillColor;
                    if (!m.Activo || pct < 0.1) fillColor = Color.FromArgb(50, 50, 55);
                    else if (pct > 1.15) fillColor = CError;
                    else if (pct > 0.85) fillColor = CAccent;
                    else fillColor = CWarn;

                    using (var b = new SolidBrush(fillColor))
                        g.FillRectangle(b, vuRect.X, vuRect.Y, fillW, vuH);
                    using (var p = new Pen(Color.FromArgb(80, 255, 255, 255)))
                        g.DrawLine(p, vuRect.Right, vuRect.Top, vuRect.Right, vuRect.Bottom);
                    using (var p = new Pen(CBorder))
                        g.DrawRectangle(p, vuRect);
                    y += vuH + 2;

                    using (var f = new Font("Segoe UI", 10f, FontStyle.Bold))
                    using (var b = new SolidBrush(fillColor))
                        g.DrawString(m.Real.ToString("F1", CultureInfo.InvariantCulture), f, b, 8, y);
                    string pctStr = m.Objetivo > 0 ? ((int)(pct * 100)) + "%" : "--";
                    using (var f = new Font("Segoe UI", 8f))
                    using (var b = new SolidBrush(CTextDim))
                    {
                        var sz = g.MeasureString(pctStr, f);
                        g.DrawString(pctStr, f, b, Width - sz.Width - 8, y + 2);
                    }
                    y += 18;

                    // Posición para botones MAN de este motor.
                    _motorBtnY[mi] = y;
                    y += 22;
                }
            }
            else if (HasData)
            {
                using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                using (var b = new SolidBrush(CText))
                    g.DrawString(_fieldName ?? "", f, b, 8, y);
                y += 18;
                using (var f = new Font("Segoe UI", 14f, FontStyle.Bold))
                using (var b = new SolidBrush(CText))
                    g.DrawString(_hasCurrent ? _current.ToString("F1", CultureInfo.InvariantCulture) : "--", f, b, 8, y);
                y += 24;
            }

            int newH = y + 4;
            if (Height != newH && newH > 50) Size = new Size(Width, newH);

            if (_hasMotores && !_buttonsCreated) CreateButtons();
            if (_buttonsCreated) LayoutMotorButtons();
        }

        private void LayoutMotorButtons()
        {
            for (int mi = 0; mi < 2; mi++)
            {
                if (_btnMan[mi] == null) continue;
                var m = Motores[mi];
                bool show = !string.IsNullOrEmpty(m.Nombre) || m.Objetivo > 0 || m.Activo;
                _btnMan[mi].Visible = show;
                if (!show) { _btnDn[mi].Visible = false; _lblVal[mi].Visible = false; _btnUp[mi].Visible = false; continue; }

                int by = _motorBtnY[mi];
                int x = 4;
                _btnMan[mi].Location = new Point(x, by); x += 36;
                _btnDn[mi].Location = new Point(x, by); x += 24;
                _lblVal[mi].Location = new Point(x, by); x += 38;
                _btnUp[mi].Location = new Point(x, by);

                _btnDn[mi].Visible = m.Manual;
                _lblVal[mi].Visible = m.Manual;
                _btnUp[mi].Visible = m.Manual;
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
