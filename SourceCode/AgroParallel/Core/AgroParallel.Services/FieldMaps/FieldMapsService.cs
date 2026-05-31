// ============================================================================
// FieldMapsService.cs — implementación de IFieldMapsService.
//
// Lee shapefiles VistaX existentes y devuelve GeoJSON. Boundary/Headland salen
// del snapshot PilotX (en E/N locales) y se proyectan con flat-earth alrededor
// del pivote actual — buena aproximación a escala de lote (errores <10cm en
// 2 km a la redonda).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services.FieldMaps
{
    public sealed class FieldMapsService : IFieldMapsService
    {
        private const double MetersPerDegLat = 111320.0;

        private readonly IAogStateProvider _state;

        public FieldMapsService(IAogStateProvider state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        // -------------------------------------------------------------------
        // Listado de sesiones
        // -------------------------------------------------------------------

        public MapasSesionesDto ListSesiones()
        {
            var result = new MapasSesionesDto();
            string fieldDir = GetCurrentFieldDirectory();
            if (string.IsNullOrEmpty(fieldDir)) return result;

            var snap = SafeSnapshot();
            result.Lote = snap?.CurrentFieldDirectory ?? "";

            string vxDir = Path.Combine(fieldDir, "VistaX");
            if (!Directory.Exists(vxDir)) return result;

            // Agrupamos por timestamp: vistax_<ts>.shp (puntos) y vistax_<ts>_heatmap.shp.
            var perTs = new Dictionary<string, MapasSesionDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.GetFiles(vxDir, "vistax_*.shp"))
            {
                string name = Path.GetFileNameWithoutExtension(path); // vistax_20260516_143020[_heatmap]
                if (string.IsNullOrEmpty(name) || !name.StartsWith("vistax_", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isHeatmap = name.EndsWith("_heatmap", StringComparison.OrdinalIgnoreCase);
                string ts = isHeatmap
                    ? name.Substring("vistax_".Length, name.Length - "vistax_".Length - "_heatmap".Length)
                    : name.Substring("vistax_".Length);

                if (!perTs.TryGetValue(ts, out var s))
                {
                    s = new MapasSesionDto { Ts = ts, FechaIso = ParseTsToIso(ts) };
                    perTs[ts] = s;
                }

                int count = SafeCountFeatures(path);
                if (isHeatmap) { s.HasHeatmap = true; s.HeatmapCeldas = count; }
                else { s.HasPuntos = true; s.Puntos = count; }
            }

            // Más nuevas primero.
            var ordered = new List<MapasSesionDto>(perTs.Values);
            ordered.Sort((a, b) => string.Compare(b.Ts, a.Ts, StringComparison.Ordinal));
            result.Sesiones = ordered;
            return result;
        }

        // -------------------------------------------------------------------
        // Capas VistaX (heatmap / puntos) — lee .shp y arma GeoJSON
        // -------------------------------------------------------------------

        public GeoJsonFeatureCollectionDto GetHeatmap(string ts)
        {
            string shp = ResolveSessionShp(ts, heatmap: true);
            if (shp == null) return null;
            return ShapeFileToGeoJson(shp);
        }

        public GeoJsonFeatureCollectionDto GetPuntos(string ts)
        {
            string shp = ResolveSessionShp(ts, heatmap: false);
            if (shp == null) return null;
            return ShapeFileToGeoJson(shp);
        }

        private string ResolveSessionShp(string ts, bool heatmap)
        {
            if (string.IsNullOrWhiteSpace(ts)) return null;
            string fieldDir = GetCurrentFieldDirectory();
            if (string.IsNullOrEmpty(fieldDir)) return null;

            string vxDir = Path.Combine(fieldDir, "VistaX");
            string fname = "vistax_" + ts + (heatmap ? "_heatmap.shp" : ".shp");
            string path = Path.Combine(vxDir, fname);
            return File.Exists(path) ? path : null;
        }

        private static GeoJsonFeatureCollectionDto ShapeFileToGeoJson(string shp)
        {
            ShapefileReadResult r;
            try { r = ShapefileReader.ReadShapes(shp); }
            catch { return null; }

            var fc = new GeoJsonFeatureCollectionDto();

            foreach (var poly in r.Polygons)
            {
                var rings = new List<double[][]>();
                foreach (var ring in poly.Rings)
                {
                    var pts = new double[ring.Count][];
                    for (int i = 0; i < ring.Count; i++)
                        pts[i] = new[] { ring[i].Lon, ring[i].Lat };
                    rings.Add(pts);
                }
                fc.Features.Add(new GeoJsonFeatureDto
                {
                    Geometry = new GeoJsonGeometryDto
                    {
                        Type = "Polygon",
                        Coordinates = rings.ToArray()
                    },
                    Properties = CloneAttrs(poly.Attributes)
                });
            }

            foreach (var pt in r.Points)
            {
                fc.Features.Add(new GeoJsonFeatureDto
                {
                    Geometry = new GeoJsonGeometryDto
                    {
                        Type = "Point",
                        Coordinates = new[] { pt.Location.Lon, pt.Location.Lat }
                    },
                    Properties = CloneAttrs(pt.Attributes)
                });
            }

            foreach (var line in r.Lines)
            {
                var pts = new double[line.Points.Count][];
                for (int i = 0; i < line.Points.Count; i++)
                    pts[i] = new[] { line.Points[i].Lon, line.Points[i].Lat };
                fc.Features.Add(new GeoJsonFeatureDto
                {
                    Geometry = new GeoJsonGeometryDto
                    {
                        Type = "LineString",
                        Coordinates = pts
                    },
                    Properties = CloneAttrs(line.Attributes)
                });
            }

            if (r.MinLat < r.MaxLat && r.MinLon < r.MaxLon)
                fc.Bbox = new[] { r.MinLon, r.MinLat, r.MaxLon, r.MaxLat };

            return fc;
        }

        private static Dictionary<string, object> CloneAttrs(Dictionary<string, object> src)
        {
            var d = new Dictionary<string, object>(src?.Count ?? 0);
            if (src != null)
                foreach (var kv in src) d[kv.Key] = kv.Value;
            return d;
        }

        // -------------------------------------------------------------------
        // Boundary / Headland — vienen del snapshot PilotX en E/N locales
        // -------------------------------------------------------------------

        public GeoJsonFeatureCollectionDto GetBoundary()
        {
            var snap = SafeSnapshot();
            if (snap?.Boundaries == null || snap.Boundaries.Count == 0) return null;

            // En PilotX: rings[0] = contorno externo, [1..n] = islands (drive-thru).
            // GeoJSON Polygon necesita exterior + agujeros (mismo orden).
            var rings = new List<double[][]>();
            foreach (var ring in snap.Boundaries)
            {
                if (ring == null || ring.Count < 3) continue;
                var pts = ProjectRing(ring, snap.PivotEasting, snap.PivotNorthing, snap.Latitude, snap.Longitude, closeRing: true);
                if (pts != null) rings.Add(pts);
            }
            if (rings.Count == 0) return null;

            var fc = new GeoJsonFeatureCollectionDto();
            var feat = new GeoJsonFeatureDto
            {
                Geometry = new GeoJsonGeometryDto
                {
                    Type = "Polygon",
                    Coordinates = rings.ToArray()
                }
            };
            feat.Properties["tipo"] = "boundary";
            fc.Features.Add(feat);
            ComputeBbox(fc);
            return fc;
        }

        public GeoJsonFeatureCollectionDto GetHeadland()
        {
            var snap = SafeSnapshot();
            if (snap?.Headlands == null || snap.Headlands.Count == 0) return null;

            var fc = new GeoJsonFeatureCollectionDto();
            foreach (var ring in snap.Headlands)
            {
                if (ring == null || ring.Count < 2) continue;
                var pts = ProjectRing(ring, snap.PivotEasting, snap.PivotNorthing, snap.Latitude, snap.Longitude, closeRing: false);
                if (pts == null) continue;
                var feat = new GeoJsonFeatureDto
                {
                    Geometry = new GeoJsonGeometryDto
                    {
                        Type = "LineString",
                        Coordinates = pts
                    }
                };
                feat.Properties["tipo"] = "headland";
                fc.Features.Add(feat);
            }
            if (fc.Features.Count == 0) return null;
            ComputeBbox(fc);
            return fc;
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Proyecta una lista de puntos E/N a lat/lon usando flat-earth alrededor
        /// del pivote actual. Error &lt;10cm en 2 km a la redonda. Devuelve null si
        /// el lat del pivote es 0 (no hay fix GPS).
        /// </summary>
        private static double[][] ProjectRing(List<FieldPoint> ring, double pivotE, double pivotN, double pivotLat, double pivotLon, bool closeRing)
        {
            if (Math.Abs(pivotLat) < 0.001 && Math.Abs(pivotLon) < 0.001) return null;

            double metersPerDegLon = MetersPerDegLat * Math.Cos(pivotLat * Math.PI / 180.0);
            if (Math.Abs(metersPerDegLon) < 1.0) return null;

            int n = ring.Count;
            bool needClose = closeRing && (ring[0].E != ring[n - 1].E || ring[0].N != ring[n - 1].N);
            int outN = needClose ? n + 1 : n;
            var pts = new double[outN][];
            for (int i = 0; i < n; i++)
            {
                double lat = pivotLat + (ring[i].N - pivotN) / MetersPerDegLat;
                double lon = pivotLon + (ring[i].E - pivotE) / metersPerDegLon;
                pts[i] = new[] { lon, lat };
            }
            if (needClose) pts[n] = pts[0];
            return pts;
        }

        private static void ComputeBbox(GeoJsonFeatureCollectionDto fc)
        {
            double minLon = double.MaxValue, minLat = double.MaxValue;
            double maxLon = double.MinValue, maxLat = double.MinValue;
            bool any = false;
            foreach (var f in fc.Features) AccumGeom(f.Geometry, ref minLon, ref minLat, ref maxLon, ref maxLat, ref any);
            if (any) fc.Bbox = new[] { minLon, minLat, maxLon, maxLat };
        }

        private static void AccumGeom(GeoJsonGeometryDto g, ref double minLon, ref double minLat, ref double maxLon, ref double maxLat, ref bool any)
        {
            if (g == null || g.Coordinates == null) return;
            if (g.Coordinates is double[] pt && pt.Length >= 2)
            { Accum(pt[0], pt[1], ref minLon, ref minLat, ref maxLon, ref maxLat, ref any); return; }
            if (g.Coordinates is double[][] line)
            { foreach (var p in line) if (p != null && p.Length >= 2) Accum(p[0], p[1], ref minLon, ref minLat, ref maxLon, ref maxLat, ref any); return; }
            if (g.Coordinates is double[][][] poly)
            { foreach (var ring in poly) foreach (var p in ring) if (p != null && p.Length >= 2) Accum(p[0], p[1], ref minLon, ref minLat, ref maxLon, ref maxLat, ref any); }
        }

        private static void Accum(double lon, double lat, ref double minLon, ref double minLat, ref double maxLon, ref double maxLat, ref bool any)
        {
            if (lon < minLon) minLon = lon;
            if (lat < minLat) minLat = lat;
            if (lon > maxLon) maxLon = lon;
            if (lat > maxLat) maxLat = lat;
            any = true;
        }

        private AogStateSnapshot SafeSnapshot()
        {
            try { return _state.GetSnapshot(); }
            catch { return null; }
        }

        private string GetCurrentFieldDirectory()
        {
            var s = SafeSnapshot();
            if (s == null) return null;
            if (string.IsNullOrEmpty(s.CurrentFieldDirectory)) return null;
            if (string.IsNullOrEmpty(s.FieldsDirectory)) return null;
            return Path.Combine(s.FieldsDirectory, s.CurrentFieldDirectory);
        }

        private static int SafeCountFeatures(string shp)
        {
            try
            {
                var r = ShapefileReader.ReadShapes(shp);
                return r.Polygons.Count + r.Lines.Count + r.Points.Count;
            }
            catch { return 0; }
        }

        /// <summary>"20260516_143020" → "2026-05-16T14:30:20".</summary>
        private static string ParseTsToIso(string ts)
        {
            if (string.IsNullOrWhiteSpace(ts) || ts.Length < 15) return ts;
            try
            {
                // "yyyyMMdd_HHmmss"
                var dt = DateTime.ParseExact(ts, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                return dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch { return ts; }
        }
    }
}
