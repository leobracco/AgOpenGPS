// ============================================================================
// OverlayRegionHelper.cs
// Recorta un Control WinForms con una Region de esquinas redondeadas para que
// la ventana host copie la forma del HTML que muestra adentro (los widgets
// WebView2 tienen cards con border-radius — sin esto las esquinas cuadradas
// del UserControl asoman en negro sobre el mapa de PilotX).
// ============================================================================

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgroParallel.Common
{
    internal static class OverlayRegionHelper
    {
        /// <summary>
        /// Aplica una Region redondeada al control. Llamar desde OnResize —
        /// la Region no se escala sola cuando cambia el tamaño.
        /// </summary>
        public static void ApplyRounded(Control c, int radius)
        {
            if (c == null || c.Width <= 0 || c.Height <= 0) return;
            int d = radius * 2;
            if (d <= 0 || c.Width <= d || c.Height <= d)
            {
                var old = c.Region;
                c.Region = null;
                old?.Dispose();
                return;
            }
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(c.Width - d - 1, 0, d, d, 270, 90);
                path.AddArc(c.Width - d - 1, c.Height - d - 1, d, d, 0, 90);
                path.AddArc(0, c.Height - d - 1, d, d, 90, 90);
                path.CloseFigure();
                var old = c.Region;
                c.Region = new Region(path);
                old?.Dispose();
            }
        }
    }
}
