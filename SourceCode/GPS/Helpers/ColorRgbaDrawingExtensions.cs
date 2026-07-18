// ============================================================================
// ColorRgbaDrawingExtensions.cs — Conversiones ColorRgba ↔ System.Drawing.Color
// Vive en GPS/ (WinForms) y NO en AgOpenGPS.Core/ para mantener el Core libre
// de System.Drawing (portabilidad Android). Traspaso 2026-07-18.
// ============================================================================

using System.Drawing;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.Helpers
{
    public static class ColorRgbaDrawingExtensions
    {
        public static Color ToDrawingColor(this ColorRgba c) =>
            Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

        public static ColorRgba ToColorRgba(this Color c) =>
            new ColorRgba(c.R, c.G, c.B, c.A);
    }
}
