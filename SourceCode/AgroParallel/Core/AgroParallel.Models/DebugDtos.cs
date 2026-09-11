// ============================================================================
// DebugDtos.cs — POCOs para el módulo Debug global.
//
//   DebugEntryDto    → una línea de log (ts/module/level/message)
//   DebugConfigDto   → debug.json (módulos habilitados, grabación, etc.)
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>Una entrada de log unificada de cualquier módulo del sistema.</summary>
    public sealed class DebugEntryDto
    {
        /// <summary>Timestamp UTC ISO-8601.</summary>
        public string Ts { get; set; }

        /// <summary>Id incremental dentro del buffer (para "since=N").</summary>
        public long Seq { get; set; }

        /// <summary>Nombre del módulo. Convención: lowercase ("quantix", "vistax",
        /// "orbitx", "sectionx", "camaras", "sistema", "aog", "host", "ui").</summary>
        public string Module { get; set; }

        /// <summary>"debug" | "info" | "warn" | "error".</summary>
        public string Level { get; set; }

        /// <summary>Texto sin el prefijo "[Módulo]".</summary>
        public string Message { get; set; }
    }

    public sealed class DebugConfigDto
    {
        /// <summary>Por-módulo: true = se captura y se envía al UI / log.</summary>
        public Dictionary<string, bool> Modules { get; set; } = new Dictionary<string, bool>();

        /// <summary>Nivel mínimo: "debug" | "info" | "warn" | "error".</summary>
        public string MinLevel { get; set; } = "debug";

        /// <summary>Si true, los eventos se escriben también a archivo NDJSON.</summary>
        public bool RecordToFile { get; set; }

        /// <summary>Carpeta donde se escriben los .log al grabar. Si vacío,
        /// usa <c>BaseDirectory\AgroParallel\debug-logs</c>.</summary>
        public string RecordDir { get; set; } = "";

        /// <summary>Cantidad máxima de entradas en el ring buffer en RAM.</summary>
        public int MaxBufferLines { get; set; } = 5000;
    }

    public sealed class DebugSnapshotDto
    {
        public long Seq { get; set; }
        public string[] KnownModules { get; set; }
        public DebugConfigDto Config { get; set; }
        public bool Recording { get; set; }
        public string RecordingFile { get; set; }
        public List<DebugEntryDto> Entries { get; set; } = new List<DebugEntryDto>();
    }
}
