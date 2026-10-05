// ============================================================================
// IsoXmlExportResultadoDto.cs — resultado del export del lote a ISO-XML
// (TASKDATA.XML). Lo arma IsoXmlFieldExporter (AgOpenGPS.Core) y viaja tal
// cual por POST /api/lotes/exportar-isoxml. Wire snake_case.
// ============================================================================

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class IsoXmlExportResultadoDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
        /// <summary>Ruta completa del TASKDATA.XML escrito.</summary>
        [JsonPropertyName("archivo")] public string Archivo { get; set; }
        /// <summary>Carpeta TASKDATA donde quedó.</summary>
        [JsonPropertyName("carpeta")] public string Carpeta { get; set; }
        /// <summary>Si ya había una carpeta TASKDATA, adónde se movió (no se pisa).</summary>
        [JsonPropertyName("apartada")] public string Apartada { get; set; }
        /// <summary>"3" o "4".</summary>
        [JsonPropertyName("version")] public string Version { get; set; }
        /// <summary>Polígonos de lindero exportados (exterior + huecos).</summary>
        [JsonPropertyName("linderos")] public int Linderos { get; set; }
        /// <summary>Guías AB/curva exportadas.</summary>
        [JsonPropertyName("guias")] public int Guias { get; set; }
        [JsonPropertyName("cabecera")] public bool Cabecera { get; set; }
    }
}
