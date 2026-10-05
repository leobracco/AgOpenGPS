using AgOpenGPS.Core.Models;
using OpenTK.Graphics.OpenGL;

namespace AgOpenGPS.Core.DrawLib
{
    // GLW is short for GL Wrapper.
    // Please use this class in stead of direct calls to functions in the GL toolkit.
    public static partial class GLW
    {
        // To optimize performance of the various DrawPrimitive functions, these functions will
        // take the number of vertices into account.
        // When the number of vertices is MinVerticesForArray or higher, a Vertex2Array is used
        // to pass the vertices as bulk.
        // For smaller numbers, the vertices are simply passed one by one.
        private const int MinVerticesForArray = 40;

        public static void BeginPointsPrimitive()
        {
            GL.Begin(PrimitiveType.Points);
        }

        // Traspaso portabilidad (bloque 6 matriz Android, 2026-07-19): triángulo
        // suelto de 3 vértices (flechas del lightbar en OpenGL.Designer.cs).
        // Un TriangleFan de 3 vértices dibuja el mismo triángulo que
        // PrimitiveType.Triangles — se reusa DrawTriangleFanPrimitive en vez de
        // agregar un modo nuevo.
        public static void DrawArrowTriangle(double cx, double cy, double size, bool isRight)
        {
            XyCoord[] vertices = isRight
                ? new[] { new XyCoord(cx - size, cy - size), new XyCoord(cx - size, cy + size), new XyCoord(cx + size, cy) }
                : new[] { new XyCoord(cx + size, cy - size), new XyCoord(cx + size, cy + size), new XyCoord(cx - size, cy) };
            DrawTriangleFanPrimitive(vertices);
        }

        public static void BeginTriangleStripPrimitive()
        {
            GL.Begin(PrimitiveType.TriangleStrip);
        }

        public static void EndPrimitive()
        {
            GL.End();
        }

    }
}

