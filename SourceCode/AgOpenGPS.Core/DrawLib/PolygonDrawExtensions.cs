// Extensiones de dibujo GL para listas de vértices. Vivían en CGLM.cs (glm):
// se movieron acá para que la clase de matemática quede sin OpenTK
// (traspaso portabilidad — DrawLib es la capa GL, Windows-only hasta el
// port a GL ES/Skia).

using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;

namespace AgOpenGPS
{
    public static class PolygonDrawExtensions
    {
        public static void DrawPolygon(this List<vec3> polygon)
        {
            if (polygon.Count > 2)
            {
                GL.Begin(PrimitiveType.LineStrip);
                for (int i = 0; i < polygon.Count; i++)
                {
                    GL.Vertex2(polygon[i].easting, polygon[i].northing);
                }
                GL.End();
            }
        }

        public static void DrawPolygon(this List<vec2> polygon)
        {
            if (polygon.Count > 2)
            {
                GL.Begin(PrimitiveType.LineLoop);
                for (int i = 0; i < polygon.Count; i++)
                {
                    GL.Vertex2(polygon[i].easting, polygon[i].northing);
                }
                GL.End();
            }
        }
    }
}
