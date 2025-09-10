using System.Collections.Generic;
using NetTopologySuite.Geometries;

namespace AgOpenGPS.Shape
{
    /// <summary>
    /// Represents a set of features loaded from a shape file.
    /// </summary>
    public class ShapeDataset
    {
        public IList<ShapeFeature> Features { get; } = new List<ShapeFeature>();
    }

    /// <summary>
    /// A single feature with geometry and arbitrary attributes.
    /// </summary>
    public class ShapeFeature
    {
        public int Id { get; set; }
        public Geometry Geometry { get; set; }
        public IDictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Result of querying the spatial index.
    /// </summary>
    public class ShapeHit
    {
        public ShapeFeature Feature { get; set; }
        public double DistanceMeters { get; set; }
    }
}
