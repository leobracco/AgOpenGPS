// ============================================================================
// OverlayDragger.cs
// Helper para hacer arrastrables los widgets de overlay de PilotX
// (shapefileLegend, flowXLegend, vistaXPanel). El usuario puede tomar el
// widget con el dedo o el mouse y soltarlo donde quiera; la posición se
// persiste en overlayPrefs.json (vía OverlayPrefsService).
//
// Diseño:
//  · Engancha MouseDown/MouseMove/MouseUp sobre el control raíz Y todos sus
//    hijos descendentes (así arrastra desde "cualquier parte" del widget,
//    no solo desde el bg desnudo).
//  · Filtra hijos interactivos (Button, TextBox, NumericUpDown, etc.) para
//    no robarles el click — si el hit-test cae sobre uno, NO inicia drag.
//  · Bounds-check contra el ClientSize del parent: no permite tirar el
//    widget fuera de pantalla. Margen de seguridad de 8 px.
//  · Threshold de 4 px antes de considerar drag — evita "saltos" cuando el
//    usuario solo quiso hacer click sobre el widget (header, botones, etc.).
//  · onMoved se invoca SOLO al soltar (MouseUp), no en cada Move — minimiza
//    escrituras a disco en OverlayPrefsService.
// ============================================================================

using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgroParallel.Common
{
    public static class OverlayDragger
    {
        // Distancia (px) que el cursor debe moverse desde el MouseDown para
        // considerar la acción un drag (y no un click).
        private const int DragThreshold = 4;

        // Margen de seguridad para que el widget no se pueda esconder del
        // todo fuera del área visible.
        private const int EdgeMargin = 8;

        public static void Attach(Control target, Action<Point> onMoved)
        {
            if (target == null) return;

            // Estado por target. Lo capturamos en el closure.
            var state = new DragState();

            WireRecursive(target, target, state, onMoved);
        }

        /// <summary>
        /// Variante con handle separado del control que se mueve. Útil cuando el
        /// control tiene un hijo que intercepta mouse (WebView2) y necesitamos
        /// dragear desde una barra superior. Los eventos se enganchan en
        /// <paramref name="handle"/> pero quien se mueve es <paramref name="moveTarget"/>.
        /// </summary>
        public static void Attach(Control handle, Action<Point> onMoved, Control moveTarget)
        {
            if (handle == null || moveTarget == null) return;
            var state = new DragState();
            WireRecursive(moveTarget, handle, state, onMoved);
        }

        private static void WireRecursive(Control root, Control current, DragState st, Action<Point> onMoved)
        {
            // No enganchamos sobre controles interactivos: queremos que sigan
            // recibiendo sus eventos sin interferencia.
            if (!IsPassThrough(current))
            {
                current.MouseDown += (s, e) => HandleDown(root, e, st);
                current.MouseMove += (s, e) => HandleMove(root, e, st);
                current.MouseUp   += (s, e) => HandleUp(root, e, st, onMoved);
            }

            foreach (Control child in current.Controls)
                WireRecursive(root, child, st, onMoved);
        }

        private static bool IsPassThrough(Control c)
        {
            if (c == null) return true;
            // Controles que tienen su propia interacción — no robamos su click.
            return c is Button
                || c is TextBox
                || c is NumericUpDown
                || c is ComboBox
                || c is TrackBar
                || c is CheckBox
                || c is RadioButton
                || c is ListBox
                || c is ScrollBar;
        }

        private static void HandleDown(Control root, MouseEventArgs e, DragState st)
        {
            if (e.Button != MouseButtons.Left) return;
            st.IsArmed = true;
            st.IsDragging = false;
            // Capturamos las coords en espacio del PARENT, no del control que
            // recibió el evento (porque puede ser un hijo nested).
            st.MouseDownAtParent = root.Parent != null
                ? root.Parent.PointToClient(Control.MousePosition)
                : Point.Empty;
            st.OriginAtParent = root.Location;
        }

        private static void HandleMove(Control root, MouseEventArgs e, DragState st)
        {
            if (!st.IsArmed) return;
            if (root.Parent == null) return;

            Point cur = root.Parent.PointToClient(Control.MousePosition);
            int dx = cur.X - st.MouseDownAtParent.X;
            int dy = cur.Y - st.MouseDownAtParent.Y;

            if (!st.IsDragging)
            {
                if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
                    return;
                st.IsDragging = true;
                // Mientras dura el drag fijamos el Anchor en TopLeft para que
                // los anchors originales (Left|Bottom) no peleen con nuestras
                // asignaciones de Location. El caller los restaurará si quiere.
                st.OriginalAnchor = root.Anchor;
                root.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                root.BringToFront();
            }

            int newX = st.OriginAtParent.X + dx;
            int newY = st.OriginAtParent.Y + dy;
            ClampToParent(root, ref newX, ref newY);
            root.Location = new Point(newX, newY);
        }

        private static void HandleUp(Control root, MouseEventArgs e, DragState st, Action<Point> onMoved)
        {
            if (!st.IsArmed) return;
            bool dragged = st.IsDragging;
            st.IsArmed = false;
            st.IsDragging = false;

            if (!dragged) return;
            if (onMoved != null)
            {
                try { onMoved(root.Location); } catch { }
            }
        }

        private static void ClampToParent(Control root, ref int x, ref int y)
        {
            if (root.Parent == null) return;
            int pw = root.Parent.ClientSize.Width;
            int ph = root.Parent.ClientSize.Height;
            int w = root.Width;
            int h = root.Height;

            // Permitimos que sobresalga hasta dejar EdgeMargin px visibles
            // en cada lado, así el operario no pierde el widget.
            int minX = EdgeMargin - w;
            int maxX = pw - EdgeMargin;
            int minY = EdgeMargin - h;
            int maxY = ph - EdgeMargin;

            if (x < minX) x = minX;
            if (x > maxX) x = maxX;
            if (y < minY) y = minY;
            if (y > maxY) y = maxY;

            // Para el caso normal (widget chico vs parent grande), preferimos
            // que quede 100% visible.
            if (w < pw)
            {
                if (x < EdgeMargin) x = EdgeMargin;
                if (x + w > pw - EdgeMargin) x = pw - EdgeMargin - w;
            }
            if (h < ph)
            {
                if (y < EdgeMargin) y = EdgeMargin;
                if (y + h > ph - EdgeMargin) y = ph - EdgeMargin - h;
            }
        }

        private sealed class DragState
        {
            public bool IsArmed;
            public bool IsDragging;
            public Point MouseDownAtParent;
            public Point OriginAtParent;
            public AnchorStyles OriginalAnchor = AnchorStyles.None;
        }
    }
}
