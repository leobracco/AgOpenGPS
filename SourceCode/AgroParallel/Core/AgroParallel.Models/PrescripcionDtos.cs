// ============================================================================
// PrescripcionDtos.cs
// DTOs para prescripciones variable-rate (mapa de zonas con dosis distinta
// por polígono). Origen: OrbitX cloud (módulo /api/prescripciones) → PilotX
// las descarga vía OrbitXSync a <BaseDir>/data/prescripciones/{nombre}.geojson.
//
// El consumer principal es QuantiXMotorBridge: dado un Lat/Lon del GPS,
// busca el polígono que lo contiene y devuelve la dosis del feature.
// FlowX/SectionX podrían ser consumers futuros con la misma API.
//
// Formato GeoJSON: RFC 7946 FeatureCollection con polygons (o multipolygons),
// donde cada feature.properties tiene la dosis en alguna prop común:
//   "dosis", "dose", "rate", "kgha", "lha", "tasa"
// La elección de qué propiedad consumir queda en el frontend (al hacer
// SetActive, el usuario elige cuál es la "columna activa").
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Polígono de la prescripción con su dosis. Las coordenadas se
    /// guardan en orden GeoJSON estándar: [lon, lat] (X,Y). La lista exterior
    /// es el outer ring; las restantes son holes (raro en agricultura pero
    /// soportado por completitud RFC).</summary>
    public sealed class PrescripcionFeatureDto
    {
        /// <summary>Anillos del polígono. [0] = outer ring, [1..] = holes.
        /// Cada punto es double[2] = { lon, lat } WGS84.</summary>
        [JsonPropertyName("rings")]
        public List<List<double[]>> Rings { get; set; } = new List<List<double[]>>();

        /// <summary>Dosis efectiva del polígono (kg/ha o L/ha según el
        /// insumo). La extrae el servicio leyendo la propiedad activa del
        /// GeoJSON al cargar la prescripción.</summary>
        [JsonPropertyName("dosis")]
        public double Dosis { get; set; }

        /// <summary>Etiqueta opcional de la zona (ej: "Zona A · Alto"). Solo
        /// para UI/tooltip; el bridge no la consume.</summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        // ---- Bounding box pre-calculado para fast-reject -------------------
        // Lo llena el servicio al cargar; los clients no lo setean por mano.
        [JsonPropertyName("min_lon")] public double MinLon { get; set; }
        [JsonPropertyName("min_lat")] public double MinLat { get; set; }
        [JsonPropertyName("max_lon")] public double MaxLon { get; set; }
        [JsonPropertyName("max_lat")] public double MaxLat { get; set; }
    }

    /// <summary>Prescripción cargada en memoria. La construye el servicio
    /// parseando el GeoJSON del disco. La UI la usa solo para mostrar nombre/
    /// metadata; el lookup de dosis pasa por el servicio.</summary>
    public sealed class PrescripcionDto
    {
        /// <summary>Slug derivado del filename sin extensión. "Las 33.geojson"
        /// → "las-33". Usado como ID en API y persistencia.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Nombre visible (filename sin .geojson).</summary>
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        /// <summary>Cuál property del GeoJSON estamos leyendo como "dosis"
        /// (ej: "dosis", "rate", "kgha"). Para mostrar en UI y al cambiar
        /// re-parsear sin tocar el archivo.</summary>
        [JsonPropertyName("propiedad_dosis")]
        public string PropiedadDosis { get; set; } = "";

        /// <summary>Cantidad de features del GeoJSON original.</summary>
        [JsonPropertyName("feature_count")]
        public int FeatureCount { get; set; }

        /// <summary>Bounding box global de toda la prescripción (lon/lat).</summary>
        [JsonPropertyName("min_lon")] public double MinLon { get; set; }
        [JsonPropertyName("min_lat")] public double MinLat { get; set; }
        [JsonPropertyName("max_lon")] public double MaxLon { get; set; }
        [JsonPropertyName("max_lat")] public double MaxLat { get; set; }

        /// <summary>Polígonos parseados. Vacío si el GeoJSON no tenía
        /// polígonos válidos (ej: solo puntos o LineStrings).</summary>
        [JsonPropertyName("features")]
        public List<PrescripcionFeatureDto> Features { get; set; } = new List<PrescripcionFeatureDto>();

        // ---- Resultado del lookup (no se persiste, lo arma el service) -----
        /// <summary>Cuándo se cargó (UTC ISO). Para invalidar cache si el
        /// archivo en disco cambia.</summary>
        [JsonPropertyName("loaded_utc")]
        public string LoadedUtc { get; set; } = "";
    }

    /// <summary>Item listado en GET /api/prescripciones/list — metadata sin
    /// features para que la UI cargue rápido.</summary>
    public sealed class PrescripcionListItemDto
    {
        [JsonPropertyName("id")]      public string Id { get; set; } = "";
        [JsonPropertyName("nombre")]  public string Nombre { get; set; } = "";
        [JsonPropertyName("archivo")] public string Archivo { get; set; } = "";
        [JsonPropertyName("bytes")]   public long Bytes { get; set; }
        [JsonPropertyName("fecha_mod_utc")] public string FechaModUtc { get; set; } = "";
        /// <summary>true si es la que está marcada activa en prescripciones-state.json.</summary>
        [JsonPropertyName("activo")]  public bool Activo { get; set; }
        /// <summary>Lista de properties candidatas a "dosis" encontradas en
        /// el primer feature. La UI las muestra como dropdown.</summary>
        [JsonPropertyName("propiedades_candidatas")]
        public List<string> PropiedadesCandidatas { get; set; } = new List<string>();
    }
}
