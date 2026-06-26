// ============================================================================
// FlowXLegendControl.cs — Widget overlay con telemetría FlowX en vivo.
//
// Read-only. Una fila por nodo configurado con:
//   · Nombre del nodo (o UID si no tiene nombre).
//   · Caudal real (L/ha, dosis aplicada) en grande + VU contra target.
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
            public double CaudalLha;
            public double TargetLha;
            public int Pwm;
            public long Pulsos;   // contador crudo del ISR (diagnóstico sensor/cable)
            public string PidEstado;
            public bool ModoManual;   // true = caudal L/min fijo; false = dosis L/ha por velocidad
            public double ManualLmin; // target fijo (L/min) cuando ModoManual
        }

        private List<NodoLive> _nodos = new List<NodoLive>();
        private bool _connected;

        // ── Edición táctil desde el widget (sin teclado, doctrina UI táctil) ──
        // El widget solo emite el SIGNO del ajuste; FormGPS aplica el paso
        // configurable (paso_lha / paso_lmin) según el modo y persiste.

        // (uid, sign): +1 sube, -1 baja el valor del modo activo.
        public event Action<string, int> DoseChangeRequested;
        // (uid): alterna automático ↔ manual para el nodo.
        public event Action<string> ModeToggleRequested;

        // Hit-zones por fila, recalculadas en cada OnPaint.
        private struct DoseHit
        {
            public string Uid;
            public Rectangle Minus;
            public Rectangle Plus;
            public Rectangle Mode;
        }
        private readonly List<DoseHit> _doseHits = new List<DoseHit>();

        // Estado de pulsación: distingue tap de drag y da feedback visual.
        // _pressedBtn: 0=ninguno, 1=minus, 2=plus, 3=toggle modo.
        private string _pressedUid;
        private int _pressedBtn;
        private Point _pressedDownPt;

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
            Size = new Size(280, 90);
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
            _doseHits.Clear();

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
                y += 18;

                // Línea 2: caudal grande + "/target". Según el modo:
                //   · manual → L/min (caudal fijo, indep. de la velocidad)
                //   · auto   → L/ha  (dosis aplicada, escalada por velocidad)
                string unidad = n.ModoManual ? "L/min" : "L/ha";
                double bigVal = n.ModoManual ? n.CaudalLmin : n.CaudalLha;
                double tgtVal = n.ModoManual ? n.ManualLmin : n.TargetLha;
                string tgtFmt = n.ModoManual ? "F1" : "F0";

                using (var f = new Font("Segoe UI", 6.5f))
                using (var b = new SolidBrush(CTextDim))
                    g.DrawString(unidad, f, b, 8, y);
                using (var f = new Font("Segoe UI", 12f, FontStyle.Bold))
                using (var b = new SolidBrush(n.Online ? CText : CTextDim))
                    g.DrawString(bigVal.ToString("F1", CultureInfo.InvariantCulture),
                                 f, b, 36, y - 4);
                using (var f = new Font("Segoe UI", 8f))
                using (var b = new SolidBrush(CTextDim))
                    g.DrawString("/ " + tgtVal.ToString(tgtFmt, CultureInfo.InvariantCulture),
                                 f, b, 92, y - 1);

                // Cluster de control a la derecha, en una sola fila bien separada:
                // [AUTO/MAN]   [−]   [+]. Hit-zones registradas para el tap.
                var modeRect = new Rectangle(Width - 126, y - 3, 46, 22);
                var minusRect = new Rectangle(Width - 72, y - 5, 30, 26);
                var plusRect = new Rectangle(Width - 34, y - 5, 30, 26);
                DrawModeButton(g, modeRect, n.ModoManual,
                               _pressedUid == n.Uid && _pressedBtn == 3);
                DrawDoseButton(g, minusRect, "\u2212",
                               _pressedUid == n.Uid && _pressedBtn == 1);
                DrawDoseButton(g, plusRect, "+",
                               _pressedUid == n.Uid && _pressedBtn == 2);
                _doseHits.Add(new DoseHit
                {
                    Uid = n.Uid,
                    Minus = minusRect,
                    Plus = plusRect,
                    Mode = modeRect
                });
                y += 22;

                // VU caudal vs target (ratio caudal/target, idéntico en L/ha o L/min)
                int vuW = Width - 18, vuH = 6;
                var vuRect = new Rectangle(8, y, vuW, vuH);
                using (var b = new SolidBrush(Color.FromArgb(22, 22, 26)))
                    g.FillRectangle(b, vuRect);

                double vuCaudal = n.ModoManual ? n.CaudalLmin : n.CaudalLha;
                double vuTarget = n.ModoManual ? n.ManualLmin : n.TargetLha;
                double pct = vuTarget > 0 ? Math.Min(1.5, vuCaudal / vuTarget) : 0;
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

                // Línea 5: contador de pulsos crudo (diagnóstico).
                // Si la bomba está activa (PWM > 0) y este número NO sube entre
                // refreshes, el ISR del firmware no engancha — problema en el
                // sensor, cable o nivel lógico del GPIO. Si sube pero L/min=0,
                // el bug está en GetUPM() del firmware.
                using (var f = new Font("Segoe UI", 7.5f))
                using (var b = new SolidBrush(CTextDim))
                    g.DrawString("Pulsos " + n.Pulsos.ToString(CultureInfo.InvariantCulture),
                                 f, b, 8, y);
                y += 14;

                using (var p = new Pen(CBorder))
                    g.DrawLine(p, 8, y, Width - 8, y);
                y += 4;
            }

            int newH = y + 4;
            if (Height != newH && newH > 50) Size = new Size(Width, newH);
        }

        private static void DrawDoseButton(Graphics g, Rectangle r, string glyph, bool pressed)
        {
            Color bg = pressed ? Color.FromArgb(50, 90, 30) : Color.FromArgb(30, 32, 36);
            Color border = pressed ? CAccent : Color.FromArgb(70, 72, 78);
            using (var path = RoundedRect(r, 5))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);
            }
            using (var f = new Font("Segoe UI", 12f, FontStyle.Bold))
            using (var b = new SolidBrush(CText))
            {
                var sz = g.MeasureString(glyph, f);
                g.DrawString(glyph, f, b,
                             r.X + (r.Width - sz.Width) / 2f,
                             r.Y + (r.Height - sz.Height) / 2f);
            }
        }

        // Pastilla AUTO/MAN. Verde acento = manual (caudal fijo), gris = auto.
        private static void DrawModeButton(Graphics g, Rectangle r, bool manual, bool pressed)
        {
            Color bg = manual ? Color.FromArgb(50, 90, 30) : Color.FromArgb(28, 30, 34);
            Color border = (manual || pressed) ? CAccent : Color.FromArgb(70, 72, 78);
            if (pressed) bg = Color.FromArgb(60, 100, 35);
            using (var path = RoundedRect(r, 6))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);
            }
            string txt = manual ? "MAN" : "AUTO";
            using (var f = new Font("Segoe UI", 6.5f, FontStyle.Bold))
            using (var b = new SolidBrush(manual ? CText : CTextDim))
            {
                var sz = g.MeasureString(txt, f);
                g.DrawString(txt, f, b,
                             r.X + (r.Width - sz.Width) / 2f,
                             r.Y + (r.Height - sz.Height) / 2f);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var h in _doseHits)
            {
                if (h.Minus.Contains(e.Location))
                { _pressedUid = h.Uid; _pressedBtn = 1; _pressedDownPt = e.Location; Invalidate(); return; }
                if (h.Plus.Contains(e.Location))
                { _pressedUid = h.Uid; _pressedBtn = 2; _pressedDownPt = e.Location; Invalidate(); return; }
                if (h.Mode.Contains(e.Location))
                { _pressedUid = h.Uid; _pressedBtn = 3; _pressedDownPt = e.Location; Invalidate(); return; }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string uid = _pressedUid;
            int btn = _pressedBtn;
            if (uid == null || btn == 0) return;

            _pressedUid = null;
            _pressedBtn = 0;
            Invalidate();

            // Si el puntero se movió (drag del widget), no es un tap → ignorar.
            int dx = e.Location.X - _pressedDownPt.X;
            int dy = e.Location.Y - _pressedDownPt.Y;
            if (Math.Abs(dx) > 6 || Math.Abs(dy) > 6) return;

            // Confirmar que el release sigue sobre el MISMO botón.
            foreach (var h in _doseHits)
            {
                if (!string.Equals(h.Uid, uid, StringComparison.OrdinalIgnoreCase)) continue;
                if (btn == 3)
                {
                    if (h.Mode.Contains(e.Location))
                    {
                        var mh = ModeToggleRequested;
                        if (mh != null) mh(uid);
                    }
                }
                else
                {
                    var rect = btn == 2 ? h.Plus : h.Minus;
                    if (rect.Contains(e.Location))
                    {
                        var handler = DoseChangeRequested;
                        if (handler != null) handler(uid, btn == 2 ? +1 : -1);
                    }
                }
                break;
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
