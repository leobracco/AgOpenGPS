using System;
using System.Collections.Generic;
using System.IO;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;

namespace AgOpenGPS.Shape
{
    /// <summary>
    /// Service responsible for loading shape files and querying features.
    /// </summary>
    public class ShapeService
    {
        private readonly GeometryFactory _geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        private ShapeDataset _dataset;
        private STRtree<ShapeFeature> _index;

        /// <summary>
        /// Loads a shape file and returns the dataset.
        /// </summary>
        public ShapeDataset LoadShapefile(string shpPath)
        {
            if (string.IsNullOrWhiteSpace(shpPath)) throw new ArgumentNullException(nameof(shpPath));
            var ds = new ShapeDataset();

            using (var reader = new ShapefileDataReader(shpPath, _geometryFactory))
            {
                int id = 0;
                while (reader.Read())
                {
                    Geometry geom = reader.Geometry;
                    var attrs = new Dictionary<string, object>();
                    for (int i = 0; i < reader.DbaseHeader.NumFields; i++)
                    {
                        attrs[reader.DbaseHeader.Fields[i].Name] = reader.GetValue(i + 1);
                    }
                    ds.Features.Add(new ShapeFeature
                    {
                        Id = id++,
                        Geometry = geom,
                        Attributes = attrs
                    });
                }
            }

            BuildSpatialIndex(ds);
            _dataset = ds;
            return ds;
        }

        /// <summary>
        /// Builds a spatial index for the loaded dataset.
        /// </summary>
        private void BuildSpatialIndex(ShapeDataset ds)
        {
            var index = new STRtree<ShapeFeature>();
            foreach (var feature in ds.Features)
            {
                index.Insert(feature.Geometry.EnvelopeInternal, feature);
            }
            index.Build();
            _index = index;
        }

        /// <summary>
        /// Queries the dataset for a feature at the specified lat/lon.
        /// </summary>
        public ShapeHit QueryAtLatLon(double lat, double lon, double searchRadiusMeters)
        {
            if (_index == null) return null;
            var pt = _geometryFactory.CreatePoint(new Coordinate(lon, lat));
            var hits = _index.Query(pt.EnvelopeInternal);
            ShapeFeature best = null;
            double bestDistance = searchRadiusMeters;
            foreach (var hit in hits)
            {
                if (hit.Geometry.Contains(pt))
                {
                    best = hit;
                    bestDistance = 0;
                    break;
                }
                double dist = hit.Geometry.Distance(pt) * 111000.0; // rough degrees to meters
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    best = hit;
                }
            }
            if (best == null) return null;
            return new ShapeHit { Feature = best, DistanceMeters = bestDistance };
        }
    }
}
