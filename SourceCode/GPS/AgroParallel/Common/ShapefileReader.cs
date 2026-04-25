// ============================================================================
// ShapefileReader.cs - Lector minimo de shapefiles (.shp + .dbf + .prj)
// Ubicación: SourceCode/GPS/AgroParallel/Common/ShapefileReader.cs
// Target: net48 (C# 7.3)
//
// Paso 1 del pipeline de integracion de shapefiles en AgOpenGPS:
// - Solo parsea polygonos (Polygon/MultiPolygon). Puntos y lineas se ignoran.
// - Asume coordenadas WGS84 (X = longitud, Y = latitud). Si el .prj indica
//   otra proyeccion, se loguea como Warning y NO se reproyecta todavia.
// - Sin UI, sin render, sin dependencias a FormGPS.
//
// Proximos pasos: boton en menu -> OpenFileDialog -> llamar a ReadPolygons()
// y pasar el resultado a una capa de render sobre oglMain.
// ============================================================================

using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AgroParallel.Common
{
    public struct ShapeLatLon
    {
        public double Lat;
        public double Lon;
    }

    public class ShapePolygon
    {
        // Rings[0] = contorno exterior, Rings[1..n] = agujeros.
        public List<List<ShapeLatLon>> Rings = new List<List<ShapeLatLon>>();
        public Dictionary<string, object> Attributes = new Dictionary<string, object>();
    }

    public class ShapeLine
    {
        public List<ShapeLatLon> Points = new List<ShapeLatLon>();
        public Dictionary<string, object> Attributes = new Dictionary<string, object>();
    }

    public class ShapePoint
    {
        public ShapeLatLon Location;
        public Dictionary<string, object> Attributes = new Dictionary<string, object>();
    }

    public class ShapefileReadResult
    {
        public List<ShapePolygon> Polygons = new List<ShapePolygon>();
        public List<ShapeLine> Lines = new List<ShapeLine>();
        public List<ShapePoint> Points = new List<ShapePoint>();
        public List<string> DbfFieldNames = new List<string>();
        public string PrjText;
        public double MinLat = double.MaxValue;
        public double MinLon = double.MaxValue;
        public double MaxLat = double.MinValue;
        public double MaxLon = double.MinValue;
        public List<string> Warnings = new List<string>();
    }

    public static class ShapefileReader
    {
        // Mantenido por compatibilidad con el caller original.
        public static ShapefileReadResult ReadPolygons(string shpPath)
        {
            return ReadShapes(shpPath);
        }

        public static ShapefileReadResult ReadShapes(string shpPath)
        {
            if (string.IsNullOrWhiteSpace(shpPath))
                throw new ArgumentException("shpPath vacio", "shpPath");
            if (!File.Exists(shpPath))
                throw new FileNotFoundException("No existe el .shp", shpPath);

            var result = new ShapefileReadResult();

            string prjPath = Path.ChangeExtension(shpPath, ".prj");
            if (File.Exists(prjPath))
            {
                try
                {
                    result.PrjText = File.ReadAllText(prjPath);
                    if (!LooksLikeWgs84(result.PrjText))
                    {
                        result.Warnings.Add(
                            ".prj no parece WGS84; las coordenadas se leeran tal cual. "
                            + "Reproyectar externamente o agregar soporte en un paso siguiente.");
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("No se pudo leer .prj: " + ex.Message);
                }
            }
            else
            {
                result.Warnings.Add("No hay .prj; se asume WGS84 (lon = X, lat = Y).");
            }

            bool fieldsCaptured = false;

            foreach (var feature in Shapefile.ReadAllFeatures(shpPath))
            {
                if (!fieldsCaptured && feature.Attributes != null)
                {
                    foreach (var name in feature.Attributes.GetNames())
                        result.DbfFieldNames.Add(name);
                    fieldsCaptured = true;
                }

                var geom = feature.Geometry;
                if (geom == null) continue;

                if (geom is Polygon polygon)
                {
                    AddPolygon(result, polygon, feature.Attributes);
                }
                else if (geom is MultiPolygon multiPoly)
                {
                    for (int i = 0; i < multiPoly.NumGeometries; i++)
                    {
                        if (multiPoly.GetGeometryN(i) is Polygon p)
                            AddPolygon(result, p, feature.Attributes);
                    }
                }
                else if (geom is LineString line)
                {
                    AddLine(result, line, feature.Attributes);
                }
                else if (geom is MultiLineString multiLine)
                {
                    for (int i = 0; i < multiLine.NumGeometries; i++)
                    {
                        if (multiLine.GetGeometryN(i) is LineString ls)
                            AddLine(result, ls, feature.Attributes);
                    }
                }
                else if (geom is Point point)
                {
                    AddPoint(result, point, feature.Attributes);
                }
                else if (geom is MultiPoint multiPt)
                {
                    for (int i = 0; i < multiPt.NumGeometries; i++)
                    {
                        if (multiPt.GetGeometryN(i) is Point pt)
                            AddPoint(result, pt, feature.Attributes);
                    }
                }
            }

            if (result.Polygons.Count == 0
                && result.Lines.Count == 0
                && result.Points.Count == 0)
            {
                result.Warnings.Add("No se encontraron geometrias soportadas en el shapefile.");
            }

            return result;
        }

        private static void AddPolygon(ShapefileReadResult result, Polygon p, IAttributesTable attrs)
        {
            var poly = new ShapePolygon();
            poly.Rings.Add(ConvertRing(p.ExteriorRing, result));
            if (p.InteriorRings != null)
            {
                foreach (var hole in p.InteriorRings)
                    poly.Rings.Add(ConvertRing(hole, result));
            }

            if (attrs != null)
            {
                foreach (var name in attrs.GetNames())
                {
                    try { poly.Attributes[name] = attrs[name]; }
                    catch { /* atributo problematico: ignorar para no romper el archivo */ }
                }
            }

            result.Polygons.Add(poly);
        }

        private static void AddLine(ShapefileReadResult result, LineString line, IAttributesTable attrs)
        {
            if (line == null || line.Coordinates == null || line.Coordinates.Length < 2) return;

            var sl = new ShapeLine();
            var coords = line.Coordinates;
            for (int i = 0; i < coords.Length; i++)
            {
                double lon = coords[i].X;
                double lat = coords[i].Y;
                sl.Points.Add(new ShapeLatLon { Lat = lat, Lon = lon });
                UpdateExtent(result, lat, lon);
            }

            CopyAttributes(attrs, sl.Attributes);
            result.Lines.Add(sl);
        }

        private static void AddPoint(ShapefileReadResult result, Point point, IAttributesTable attrs)
        {
            if (point == null || double.IsNaN(point.X) || double.IsNaN(point.Y)) return;

            var sp = new ShapePoint();
            sp.Location = new ShapeLatLon { Lat = point.Y, Lon = point.X };
            UpdateExtent(result, point.Y, point.X);

            CopyAttributes(attrs, sp.Attributes);
            result.Points.Add(sp);
        }

        private static void CopyAttributes(IAttributesTable src, Dictionary<string, object> dst)
        {
            if (src == null) return;
            foreach (var name in src.GetNames())
            {
                try { dst[name] = src[name]; }
                catch { /* atributo problematico: ignorar */ }
            }
        }

        private static void UpdateExtent(ShapefileReadResult r, double lat, double lon)
        {
            if (lat < r.MinLat) r.MinLat = lat;
            if (lat > r.MaxLat) r.MaxLat = lat;
            if (lon < r.MinLon) r.MinLon = lon;
            if (lon > r.MaxLon) r.MaxLon = lon;
        }

        private static List<ShapeLatLon> ConvertRing(LineString ring, ShapefileReadResult result)
        {
            var coords = ring.Coordinates;
            var list = new List<ShapeLatLon>(coords.Length);
            for (int i = 0; i < coords.Length; i++)
            {
                double lon = coords[i].X;
                double lat = coords[i].Y;
                list.Add(new ShapeLatLon { Lat = lat, Lon = lon });

                if (lat < result.MinLat) result.MinLat = lat;
                if (lat > result.MaxLat) result.MaxLat = lat;
                if (lon < result.MinLon) result.MinLon = lon;
                if (lon > result.MaxLon) result.MaxLon = lon;
            }
            return list;
        }

        // ── Lector de GeoJSON (FeatureCollection con Polygon) ──────────────
        public static ShapefileReadResult ReadGeoJson(string geojsonPath)
        {
            if (string.IsNullOrWhiteSpace(geojsonPath))
                throw new ArgumentException("geojsonPath vacío", "geojsonPath");
            if (!File.Exists(geojsonPath))
                throw new FileNotFoundException("No existe el archivo", geojsonPath);

            var result = new ShapefileReadResult();
            string json = File.ReadAllText(geojsonPath);

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                // Soporte para FeatureCollection y formato prescripcion_dosis_variable
                JsonElement features;
                if (root.TryGetProperty("features", out features)
                    && features.ValueKind == JsonValueKind.Array)
                {
                    // Estándar GeoJSON FeatureCollection
                    foreach (var feat in features.EnumerateArray())
                        ParseGeoJsonFeature(result, feat);
                }
                else if (root.TryGetProperty("zonas", out var zonas)
                    && zonas.ValueKind == JsonValueKind.Array)
                {
                    // Formato OrbitX prescripcion_dosis_variable
                    foreach (var zona in zonas.EnumerateArray())
                        ParseOrbitXZona(result, zona);
                }
                else
                {
                    throw new InvalidOperationException(
                        "El archivo no es un GeoJSON FeatureCollection ni una prescripción OrbitX válida.");
                }
            }

            if (result.Polygons.Count == 0 && result.Points.Count == 0 && result.Lines.Count == 0)
                result.Warnings.Add("No se encontraron geometrías en el GeoJSON.");

            return result;
        }

        private static void ParseGeoJsonFeature(ShapefileReadResult result, JsonElement feat)
        {
            JsonElement geomEl;
            if (!feat.TryGetProperty("geometry", out geomEl)) return;

            // Extraer properties
            var attrs = new Dictionary<string, object>();
            bool fieldsCaptured = result.DbfFieldNames.Count > 0;
            JsonElement propsEl;
            if (feat.TryGetProperty("properties", out propsEl)
                && propsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in propsEl.EnumerateObject())
                {
                    object val = JsonValueToObject(prop.Value);
                    attrs[prop.Name] = val;
                    if (!fieldsCaptured)
                        result.DbfFieldNames.Add(prop.Name);
                }
                if (!fieldsCaptured) fieldsCaptured = true;
            }

            ParseGeoJsonGeometry(result, geomEl, attrs);
        }

        private static void ParseOrbitXZona(ShapefileReadResult result, JsonElement zona)
        {
            var attrs = new Dictionary<string, object>();
            bool fieldsCaptured = result.DbfFieldNames.Count > 0;

            // Leer atributos de la zona
            if (zona.TryGetProperty("nombre", out var n))
                attrs["Name"] = n.GetString();
            if (zona.TryGetProperty("semilla", out var sem))
                attrs["Sem"] = sem.GetDouble();
            if (zona.TryGetProperty("ferti_linea", out var fl))
                attrs["FertL"] = fl.GetDouble();
            if (zona.TryGetProperty("ferti_costado", out var fc))
                attrs["FertC"] = fc.GetDouble();

            if (!fieldsCaptured)
            {
                foreach (var k in attrs.Keys)
                    result.DbfFieldNames.Add(k);
            }

            // Geometría
            JsonElement geomEl;
            if (zona.TryGetProperty("geometry", out geomEl))
                ParseGeoJsonGeometry(result, geomEl, attrs);
        }

        private static void ParseGeoJsonGeometry(ShapefileReadResult result,
            JsonElement geomEl, Dictionary<string, object> attrs)
        {
            string type = "";
            if (geomEl.TryGetProperty("type", out var t)) type = t.GetString() ?? "";

            if (type == "Polygon")
            {
                JsonElement coords;
                if (geomEl.TryGetProperty("coordinates", out coords))
                {
                    var poly = new ShapePolygon();
                    foreach (var ring in coords.EnumerateArray())
                        poly.Rings.Add(ParseCoordRing(ring, result));
                    foreach (var kv in attrs) poly.Attributes[kv.Key] = kv.Value;
                    result.Polygons.Add(poly);
                }
            }
            else if (type == "MultiPolygon")
            {
                JsonElement coords;
                if (geomEl.TryGetProperty("coordinates", out coords))
                {
                    foreach (var polygonCoords in coords.EnumerateArray())
                    {
                        var poly = new ShapePolygon();
                        foreach (var ring in polygonCoords.EnumerateArray())
                            poly.Rings.Add(ParseCoordRing(ring, result));
                        foreach (var kv in attrs) poly.Attributes[kv.Key] = kv.Value;
                        result.Polygons.Add(poly);
                    }
                }
            }
            else if (type == "Point")
            {
                JsonElement coords;
                if (geomEl.TryGetProperty("coordinates", out coords)
                    && coords.GetArrayLength() >= 2)
                {
                    double lon = coords[0].GetDouble();
                    double lat = coords[1].GetDouble();
                    var sp = new ShapePoint();
                    sp.Location = new ShapeLatLon { Lat = lat, Lon = lon };
                    foreach (var kv in attrs) sp.Attributes[kv.Key] = kv.Value;
                    UpdateExtent(result, lat, lon);
                    result.Points.Add(sp);
                }
            }
        }

        private static List<ShapeLatLon> ParseCoordRing(JsonElement ring, ShapefileReadResult result)
        {
            var list = new List<ShapeLatLon>();
            foreach (var coord in ring.EnumerateArray())
            {
                if (coord.GetArrayLength() < 2) continue;
                double lon = coord[0].GetDouble();
                double lat = coord[1].GetDouble();
                list.Add(new ShapeLatLon { Lat = lat, Lon = lon });
                UpdateExtent(result, lat, lon);
            }
            return list;
        }

        private static object JsonValueToObject(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out long l)) return (double)l;
                    return el.GetDouble();
                case JsonValueKind.String:
                    return el.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return el.ToString();
            }
        }

        private static bool LooksLikeWgs84(string prj)
        {
            if (string.IsNullOrWhiteSpace(prj)) return false;
            string up = prj.ToUpperInvariant();
            return up.Contains("WGS_1984") || up.Contains("WGS 84") || up.Contains("EPSG\",\"4326");
        }
    }
}
