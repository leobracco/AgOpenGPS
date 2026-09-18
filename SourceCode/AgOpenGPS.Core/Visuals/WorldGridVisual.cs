// Dibujo GL del WorldGrid (superficie del campo, textura de suelo, grid y
// BingMap). Vivía en Models/WorldGrid.cs: se movió acá para que el modelo
// quede sin OpenTK ni Bitmap (traspaso portabilidad — Visuals es capa GL,
// Windows-only hasta el port a GL ES/Skia).

using AgOpenGPS.Core.DrawLib;
using AgOpenGPS.Core.Models;
using OpenTK.Graphics.OpenGL;
using System.Drawing;

namespace AgOpenGPS.Core.Visuals
{
    public class WorldGridVisual
    {
        private readonly WorldGrid _worldGrid;
        private readonly FieldGridVisual _fieldGridVisual;
        private readonly Bitmap _floorBitmap;
        private GeoTexture2D _floorTexture;
        private BingMap _lastBingMap;
        private BingMapVisual _bingMapVisual;

        public WorldGridVisual(WorldGrid worldGrid, Bitmap floorBitmap)
        {
            _worldGrid = worldGrid;
            _floorBitmap = floorBitmap;
            _fieldGridVisual = new FieldGridVisual(worldGrid.FieldGrid);
        }

        private GeoTexture2D FloorTexture
        {
            get
            {
                if (null == _floorTexture) _floorTexture = new GeoTexture2D(_floorBitmap);
                return _floorTexture;
            }
        }

        // El modelo solo guarda el BingMap; acá se detecta el cambio de
        // referencia y se (re)crea el visual con su textura.
        private BingMapVisual BingMapVisual
        {
            get
            {
                BingMap bingMap = _worldGrid.BingMap;
                if (!ReferenceEquals(bingMap, _lastBingMap))
                {
                    _lastBingMap = bingMap;
                    _bingMapVisual = (bingMap != null) ? new BingMapVisual(bingMap) : null;
                }
                return _bingMapVisual;
            }
        }

        public void DrawFieldSurface(ColorRgba fieldColor, double cameraZoom, bool mustDrawFieldTexture)
        {
            // Compat: tinte blanco (textura con colores reales)
            DrawFieldSurface(fieldColor, new ColorRgba((byte)255, (byte)255, (byte)255), cameraZoom, mustDrawFieldTexture);
        }

        public void DrawFieldSurface(ColorRgba fieldColor, ColorRgba textureTint, double cameraZoom, bool mustDrawFieldTexture)
        {
            //adjust bitmap zoom based on cam zoom
            if (cameraZoom > 100) _worldGrid.Count = 4;
            else if (cameraZoom > 80) _worldGrid.Count = 8;
            else if (cameraZoom > 50) _worldGrid.Count = 16;
            else if (cameraZoom > 20) _worldGrid.Count = 32;
            else if (cameraZoom > 10) _worldGrid.Count = 64;
            else _worldGrid.Count = 80;

            double Count = _worldGrid.Count;

            GLW.SetColor(fieldColor);
            GL.Begin(PrimitiveType.TriangleStrip);
            GL.TexCoord2(0, 0);
            GL.Vertex3(_worldGrid.eastingMin, _worldGrid.northingMax, -0.10);
            GL.TexCoord2(Count, 0.0);
            GL.Vertex3(_worldGrid.eastingMax, _worldGrid.northingMax, -0.10);
            GL.TexCoord2(0.0, Count);
            GL.Vertex3(_worldGrid.eastingMin, _worldGrid.northingMin, -0.10);
            GL.TexCoord2(Count, Count);
            GL.Vertex3(_worldGrid.eastingMax, _worldGrid.northingMin, -0.10);
            GL.End();

            if (mustDrawFieldTexture)
            {
                // El color GL vigente MODULA la textura: si quedaba fieldColor
                // (gris) la imagen del suelo salía teñida de gris. Tinte
                // explícito: blanco de día (colores reales), atenuado de noche.
                GLW.SetColor(textureTint);
                GeoCoord u0v0 = new GeoCoord(_worldGrid.eastingMin, _worldGrid.northingMax);
                GeoCoord uCountvCount = new GeoCoord(_worldGrid.eastingMax, _worldGrid.northingMin);
                FloorTexture.DrawRepeatedZ(u0v0, uCountvCount, -0.10, Count);
            }
            BingMapVisual?.Draw();
        }

        public void DrawFieldGrid(bool isDay, GeoBoundingBox fieldBoundingBox)
        {
            _fieldGridVisual.Draw(isDay, fieldBoundingBox);
        }
    }
}
