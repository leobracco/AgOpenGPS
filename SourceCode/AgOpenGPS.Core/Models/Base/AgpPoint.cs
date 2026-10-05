// AgpPoint — reemplazo portable de System.Drawing.Point para el Core.
// netstandard2.0 compatible — sin dependencia a System.Drawing.

namespace AgOpenGPS.Core.Models
{
    public struct AgpPoint
    {
        public int X;
        public int Y;

        public AgpPoint(int x, int y) { X = x; Y = y; }

        public override string ToString() => $"{X},{Y}";

    }

    public struct AgpSize
    {
        public int Width;
        public int Height;

        public AgpSize(int width, int height) { Width = width; Height = height; }

        public override string ToString() => $"{Width}x{Height}";
    }
}
