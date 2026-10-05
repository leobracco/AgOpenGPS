//Please, if you use this, share the improvements

using AgOpenGPS.Core.Models;
using System;

namespace AgOpenGPS.Core
{
    // Modelo puro del grid del mundo: extents, zoom y estado (FieldGrid,
    // BingMap). El dibujo GL y las texturas viven en Visuals/WorldGridVisual
    // (traspaso portabilidad — este archivo queda sin OpenTK ni Bitmap).
    public class WorldGrid
    {
        private BingMap _bingMap;

        //Y
        internal double northingMax;

        internal double northingMin;

        //X
        internal double eastingMax;

        internal double eastingMin;

        private double GridSize = 6000;

        // lo ajusta WorldGridVisual según el zoom de cámara (mismo assembly)
        internal double Count = 40;

        public WorldGrid()
        {
            FieldGrid = new FieldGrid();
        }

        public FieldGrid FieldGrid { get; }

        public BingMap BingMap
        {
            internal get
            {
                return _bingMap;
            }
            set
            {
                _bingMap = value;
            }
        }

        public bool HasBingMap => _bingMap != null;

        public void checkZoomWorldGrid(GeoCoord geoCoord)
        {
            double n = Math.Round(geoCoord.Northing / (GridSize / Count * 2), MidpointRounding.AwayFromZero) * (GridSize / Count * 2);
            double e = Math.Round(geoCoord.Easting / (GridSize / Count * 2), MidpointRounding.AwayFromZero) * (GridSize / Count * 2);

            northingMax = n + GridSize;
            northingMin = n - GridSize;
            eastingMax = e + GridSize;
            eastingMin = e - GridSize;
        }
    }
}
