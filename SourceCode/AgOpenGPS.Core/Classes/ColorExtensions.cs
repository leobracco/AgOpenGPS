using System.Drawing;

namespace AgOpenGPS
{
    // Extensiones de Color puras (System.Drawing.Primitives, multiplataforma).
    // Separadas de los helpers WinForms de Controls/CExtensionMethods.cs
    // (traspaso portabilidad 2026-07-17).
    public static class ColorExtensions
    {
        // Evita el 255 exacto en R/G/B: el render de coverage usa 255 como
        // valor reservado al leer píxeles (GL.ReadPixels).
        public static Color CheckColorFor255(this Color color)
        {
            var currentR = color.R;
            var currentG = color.G;
            var currentB = color.B;

            if (currentR == 255) currentR = 254;
            if (currentG == 255) currentG = 254;
            if (currentB == 255) currentB = 254;

            return Color.FromArgb(color.A, currentR, currentG, currentB);
        }
    }
}
