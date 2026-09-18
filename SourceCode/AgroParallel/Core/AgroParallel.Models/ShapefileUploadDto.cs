// ============================================================================
// ShapefileUploadDto.cs
// DTOs para subir y reportar el resultado de un shapefile multi-archivo
// (.shp + .shx + .dbf + .prj opcional) desde la UI HTML al backend.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>
    /// Un archivo individual del set de un shapefile. El controller le entrega
    /// estos al servicio que los persiste en el directorio del lote actual.
    /// </summary>
    public sealed class ShapefileUploadFile
    {
        /// <summary>Nombre del archivo tal como vino del cliente (incluye extensión).</summary>
        public string Name { get; set; }
        public byte[] Bytes { get; set; }

        public ShapefileUploadFile() { }
        public ShapefileUploadFile(string name, byte[] bytes)
        {
            Name = name;
            Bytes = bytes;
        }
    }

    /// <summary>
    /// Resultado de un upload de shapefile. Si Ok=false, Error contiene el
    /// motivo legible (faltan archivos, no hay lote abierto, error de parseo).
    /// </summary>
    public sealed class ShapefileUploadResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        /// <summary>Nombre del .shp final guardado (sin path).</summary>
        public string FileName { get; set; }
        /// <summary>Cantidad de polígonos leídos tras la carga.</summary>
        public int PolygonCount { get; set; }
        /// <summary>Columnas DBF disponibles (para feedback inmediato).</summary>
        public IList<string> Fields { get; set; }
    }
}
