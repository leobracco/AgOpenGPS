// ============================================================================
// MapasDtos.cs — DTOs para el preview de mapas en el Hub (página Mapas).
//
// Endpoints (servidos por MapasController):
//   GET  /api/mapas/sesiones                 → MapasSesionesDto
//   GET  /api/mapas/sesion/{ts}/heatmap      → GeoJsonFeatureCollectionDto
//   GET  /api/mapas/sesion/{ts}/puntos       → GeoJsonFeatureCollectionDto
//   GET  /api/mapas/boundary                 → GeoJsonFeatureCollectionDto
//   GET  /api/mapas/headland                 → GeoJsonFeatureCollectionDto
//
// El payload es GeoJSON RFC 7946 — Leaflet lo consume nativo con L.geoJSON().
// Coordenadas en WGS84 [lon, lat] (orden RFC 7946).
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>Una sesión de monitoreo VistaX disponible en el lote actual.</summary>
    public sealed class MapasSesionDto
    {
        /// <summary>Timestamp del archivo NDJSON original (yyyyMMdd_HHmmss).</summary>
        public string Ts { get; set; } = "";
        /// <summary>ISO 8601 derivado de Ts para mostrar en la UI.</summary>
        public string FechaIso { get; set; } = "";
        /// <summary>True si existe el _heatmap.shp asociado.</summary>
        public bool HasHeatmap { get; set; }
        /// <summary>True si existe el .shp de puntos por surco.</summary>
        public bool HasPuntos { get; set; }
        /// <summary>Cantidad de celdas del heatmap (si está); 0 si no.</summary>
        public int HeatmapCeldas { get; set; }
        /// <summary>Cantidad de puntos en la capa de puntos (si está); 0 si no.</summary>
        public int Puntos { get; set; }
    }

    /// <summary>Listado para el dropdown de la página Mapas.</summary>
    public sealed class MapasSesionesDto
    {
        /// <summary>Nombre del lote PilotX actual; "" si no hay lote abierto.</summary>
        public string Lote { get; set; } = "";
        public List<MapasSesionDto> Sesiones { get; set; } = new List<MapasSesionDto>();
    }

    // ------------------------------------------------------------------------
    // GeoJSON output (RFC 7946) — usamos POCOs en vez de objetos anónimos para
    // controlar el orden de propiedades y que sea estable entre llamadas.
    // ------------------------------------------------------------------------

    public sealed class GeoJsonFeatureCollectionDto
    {
        public string Type { get; set; } = "FeatureCollection";
        public List<GeoJsonFeatureDto> Features { get; set; } = new List<GeoJsonFeatureDto>();
        /// <summary>Bounding box [minLon, minLat, maxLon, maxLat] del conjunto. null si vacío.</summary>
        public double[] Bbox { get; set; }
    }

    public sealed class GeoJsonFeatureDto
    {
        public string Type { get; set; } = "Feature";
        public GeoJsonGeometryDto Geometry { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    public sealed class GeoJsonGeometryDto
    {
        /// <summary>"Point" | "LineString" | "Polygon" | "MultiPolygon" | …</summary>
        public string Type { get; set; } = "Point";
        /// <summary>
        /// Estructura depende de Type:
        ///   Point      → double[2] = [lon, lat]
        ///   LineString → double[][] = [[lon,lat], ...]
        ///   Polygon    → double[][][] = [ring][pt][lon,lat]; ring 0 = exterior, 1..n = holes
        /// </summary>
        public object Coordinates { get; set; }
    }
}
