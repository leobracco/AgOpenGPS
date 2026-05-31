// LoteDto — info de un lote/campo de PilotX para listar/abrir/crear desde la UI HTML.
// Lo poblamos leyendo el directorio Fields/ (RegistrySettings.fieldsDirectory) +
// el FormGPS para saber cuál está activo. Sin acoplamiento con WinForms.

using System;

namespace AgroParallel.Models
{
    /// <summary>
    /// Item de la lista de lotes (Fields/&lt;Name&gt;/) que la UI HTML muestra
    /// para abrir/cerrar/crear.
    /// </summary>
    public sealed class FieldInfo
    {
        /// <summary>Nombre del directorio bajo Fields/ — id estable.</summary>
        public string Name { get; set; }

        /// <summary>Última modificación del directorio (UTC ISO 8601).</summary>
        public DateTime LastModifiedUtc { get; set; }

        /// <summary>True si existe Boundary.txt con al menos un punto.</summary>
        public bool HasBoundary { get; set; }

        /// <summary>True si este es el lote actualmente abierto en PilotX.</summary>
        public bool IsCurrent { get; set; }

        /// <summary>Hectáreas estimadas desde Boundary.txt. 0 si no aplica.</summary>
        public double AreaHa { get; set; }
    }
}
