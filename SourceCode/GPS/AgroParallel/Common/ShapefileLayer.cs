// ============================================================================
// ShapefileLayerDraw.cs — el Draw GL de la capa shapefile. SOLO WinForms:
// OpenTK inmediato, que el motor headless y el mapa Avalonia no usan. La
// geometria vive en AgroParallel/Adapters/ShapefileLayer.cs (linkeada).
// ============================================================================

using AgOpenGPS.Core.Models;
using OpenTK.Graphics.OpenGL;
using System.Drawing;

namespace AgroParallel.Common
{
    public partial class ShapefileLayer
    {

        public void Draw(LocalPlane plane)
        {
            if (!IsVisible) return;
            if (plane == null) return;
            if (IsEmpty) return;

            EnsureProjected(plane);

            // Fill primero (triangulado con ear-clipping en EnsureProjected)
            // para que el outline quede por encima. Los agujeros (Rings[1..n])
            // se ignoran en el fill — quedan visibles solo como outline.
            if (ShowFill)
            {
                bool hasStyle = _polyFillColors != null;

                for (int p = 0; p < _ringsLocal.Count; p++)
                {
                    var poly = _ringsLocal[p];
                    if (poly.Count == 0) continue;

                    var outer = poly[0];
                    if (outer == null || outer.Length < 3) continue;

                    int[] tris = p < _outerTriangles.Count ? _outerTriangles[p] : null;
                    if (tris == null || tris.Length < 3) continue;

                    Color c = (hasStyle && p < _polyFillColors.Length)
                        ? _polyFillColors[p]
                        : FillColor;
                    GL.Color4(c.R, c.G, c.B, c.A);

                    GL.Begin(PrimitiveType.Triangles);
                    for (int i = 0; i < tris.Length; i++)
                        GL.Vertex2(outer[tris[i]].X, outer[tris[i]].Y);
                    GL.End();
                }
            }

            if (ShowOutline)
            {
                GL.LineWidth(LineWidth);
                GL.Color3(LineColor.R, LineColor.G, LineColor.B);

                for (int p = 0; p < _ringsLocal.Count; p++)
                {
                    var poly = _ringsLocal[p];
                    for (int r = 0; r < poly.Count; r++)
                    {
                        var ring = poly[r];
                        if (ring == null || ring.Length < 3) continue;

                        GL.Begin(PrimitiveType.LineLoop);
                        for (int i = 0; i < ring.Length; i++)
                            GL.Vertex2(ring[i].X, ring[i].Y);
                        GL.End();
                    }
                }
            }

            // Lineas del shape (tipo PolyLine) — sin cierre, LineStrip.
            if (ShowOutline && _linesLocal.Count > 0)
            {
                GL.LineWidth(LineWidth);
                GL.Color3(LineColor.R, LineColor.G, LineColor.B);

                for (int l = 0; l < _linesLocal.Count; l++)
                {
                    var pts = _linesLocal[l];
                    if (pts == null || pts.Length < 2) continue;

                    GL.Begin(PrimitiveType.LineStrip);
                    for (int i = 0; i < pts.Length; i++)
                        GL.Vertex2(pts[i].X, pts[i].Y);
                    GL.End();
                }
            }

            // Puntos — tamano fijo, color fijo.
            if (_pointsLocal.Count > 0)
            {
                GL.PointSize(PointSize);
                GL.Color4(PointColor.R, PointColor.G, PointColor.B, PointColor.A);

                GL.Begin(PrimitiveType.Points);
                for (int q = 0; q < _pointsLocal.Count; q++)
                    GL.Vertex2(_pointsLocal[q].X, _pointsLocal[q].Y);
                GL.End();
            }
        }
    }
}
