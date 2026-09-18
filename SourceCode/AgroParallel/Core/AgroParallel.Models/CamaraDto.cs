// ============================================================================
// CamaraDto / CamarasConfigDto
// POCO transfer types for the Cámaras module (HTML UI <-> WebHost <-> GPS).
// Shape espejo de Documents\AgOpenGPS\camaras.json. Los [JsonPropertyName]
// fuerzan snake_case/lowercase para que el JSON emitido coincida con lo que
// el JS lee (state.config.camaras, c.nombre, c.ip, ...). Sin esto, la UI
// veía PascalCase y mostraba "sin cámaras activas" aunque hubiera config.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class CamaraDto
    {
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "Cámara";

        [JsonPropertyName("ip")]
        public string Ip { get; set; } = "192.168.1.64";

        [JsonPropertyName("puerto")]
        public int Puerto { get; set; } = 80;

        [JsonPropertyName("canal")]
        public int Canal { get; set; } = 1;

        [JsonPropertyName("usuario")]
        public string Usuario { get; set; } = "admin";

        [JsonPropertyName("clave")]
        public string Clave { get; set; } = "";

        [JsonPropertyName("activa")]
        public bool Activa { get; set; } = true;
    }

    public sealed class CamarasConfigDto
    {
        [JsonPropertyName("camaras")]
        public List<CamaraDto> Camaras { get; set; } = new List<CamaraDto>();

        [JsonPropertyName("refrescoMs")]
        public int RefrescoMs { get; set; } = 1000;
    }
}
