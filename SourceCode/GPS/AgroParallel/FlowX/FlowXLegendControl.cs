// ============================================================================
// FlowXLegendControl.cs — Widget overlay con telemetría FlowX en vivo.
//
// Read-only. Una fila por nodo configurado con:
//   · Nombre del nodo (o UID si no tiene nombre).
//   · Caudal real (L/min) en grande + VU contra target.
//   · PWM aplicado (0..255) y estado PID (ok / saturado / sin_pulsos / off).
//   · Indicador online/offline.
//
// El setpoint manual se edita en /flowx desde el Hub, no acá. Este widget
// solo informa.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AgroParallel.FlowX
{
    public sealed class FlowXLegendControl : UserControl
    {
        public struct NodoLive
        {
            public string Uid;
            public string Nombre;
            public bool Online;
            public double CaudalLmin;
            public double TargetLmin;
            public int Pwm;
            public string PidEstado;
        }

        private List<NodoLive> _nodos = new List<NodoLive>();
        private bool _connected;

        private static readonly Color CBg = Color.FromArgb(230, 8, 8, 10);
        private static readonly Color CBorder = Color.FromArgb(40, 40, 44);
        private static readonly Color CText = Color.FromArgb(235, 235, 235);
        private static readonly Color CTextDim = Color.FromArgb(130, 135, 145);
        private static readonly Color CAccent = Color.FromArgb(122, 201, 67);
        private static readonly Color CWarn = Color.FromArgb(255, 220, 0);
        private static readonly Color CError = Color.FromArgb(220, 40, 0);

        public FlowXLegendControl()
        {
            DoubleBuffered = true;
            Size = new Size(220, 90);
            BackColor = Color.Black;
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint, true);
        }

        public bool HasData { get { return _nodos != null && _nodos.Count > 0; } }

        public void SetConnected(bool c) { _connected = c; Invalidate(); }

        public void SetNodos(IList<NodoLive> nodos)
        {
            _nodos.Clear();
            if (nodos != null) _nodos.AddRange(nodos);
            Invalidate();
        }

        private static Color PidColor(string estado, bool online)
        {
            if (!online) return Color.FromArgb(60, 60, 65);
            if (string.IsNullOrEmpty(estado)) return CTextDim;
            string e = estado.ToLowerInvariant();
            if (e == "ok") return CAccent;
            if (e == "saturado" || e == "sin_pulsos") return CWarn;
            return CError;
        }

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

            // Header
            using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var b = new SolidBrush(CText))
                g.DrawString("FlowX", f, b, 8, y);
            using (var b = new SolidBrush(_connected ? CAccent : Color.FromArgb(60, 60, 65)))
                g.FillEllipse(b, Width - 16, y + 2, 8, 8);
            y += 18;

            using (var p = new Pen(CBorder))
                g.DrawLine(p, 8, y, Width - 8, y);
            y += 4;

            if (_nodos.Count == 0)
            {
                using (var f = new Font("Segoe UI", 8f))
                using (var b = new SolidBrush(CTextDim))
                    g.DrawString("Sin nodos configurados", f, b, 8, y);
                int h0 = y + 24;
                if (Height != h0 && h0 > 50) Size = new Size(Width, h0);
                return;
            }

            foreach (var n in _nodos)
            {
                string nombre = string.IsNullOrEmpty(n.Nombre) ? (n.Uid ?? "Nodo") : n.Nombre;

                // Línea 1: nombre + dot online
                using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                using (var b = new SolidBrush(n.Online ? CText : CTextDim))
                    g.DrawString(nombre, f, b, 8, y);
                using (var b = new SolidBrush(n.Online ? CAccent : Color.FromArgb(60, 60, 65)))
                    g.FillEllipse(b, Width - 16, y + 3, 7, 7);
                y += 14;

                // Línea 2: caudal grande + "/target"
                using (var f = new Font("Segoe UI", 6.5f))
                using (var b = new SolidBrush(CTextDim))
                    g.DrawString("L/min", f, b, 8, y);
                using (var f = new Font("Segoe UI", 12f, FontStyle.Bold))
                using (var b = new SolidBrush(n.Online ? CText : CTextDim))
                    g.DrawString(n.CaudalLmin.ToString("F2", CultureInfo.InvariantCulture),
                                 f, b, 36, y - 4);
                using (var f = new Font("Segoe UI", 8f))
                using (var b = new SolidBrush(CTextDim))
                    g.DrawString("/ " + n.TargetLmin.ToString("F2", CultureInfo.InvariantCulture),
                                 f, b, 110, y - 1);
                y += 18;

                // VU caudal vs target
                int vuW = Width - 18, vuH = 6;
                var vuRect = new Rectangle(8, y, vuW, vuH);
                using (var b = new SolidBrush(Color.FromArgb(22, 22, 26)))
                    g.FillRectangle(b, vuRect);

                double pct = n.TargetLmin > 0 ? Math.Min(1.5, n.CaudalLmin / n.TargetLmin) : 0;
                int fillW = (int)(vuW * Math.Min(1.0, pct));
                Color fillColor;
                if (!n.Online || pct < 0.1) fillColor = Color.FromArgb(50, 50, 55);
                else if (pct > 1.15) fillColor = CError;
                else if (pct > 0.85) fillColor = CAccent;
                else fillColor = CWarn;
                using (var b = new SolidBrush(fillColor))
                    g.FillRectangle(b, vuRect.X, vuRect.Y, fillW, vuH);
                using (var p = new Pen(CBorder))
                    g.DrawRectangle(p, vuRect);
                y += vuH + 4;

                // Línea 4: PWM + PID estado
                using (var f = new Font("Segoe UI", 7.5f))
                using (var b = new SolidBrush(CTextDim))
                    g.DrawString("PWM " + n.Pwm, f, b, 8, y);
                string pid = string.IsNullOrEmpty(n.PidEstado) ? "—" : n.PidEstado;
                using (var f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                using (var b = new SolidBrush(PidColor(pid, n.Online)))
                {
                    var sz = g.MeasureString(pid, f);
                    g.DrawString(pid, f, b, Width - sz.Width - 10, y);
                }
                y += 14;

                using (var p = new Pen(CBorder))
                    g.DrawLine(p, 8, y, Width - 8, y);
                y += 4;
            }

            int newH = y + 4;
            if (Height != newH && newH > 50) Size = new Size(Width, newH);
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
