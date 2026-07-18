// ============================================================================
// HeadlandEditDtos.cs — POCOs para el editor de cabecera HTML (cabecera.html).
//
//   HeadlandEditStateDto   → GET  /api/headland/state  (estado + geometría)
//   HeadlandEditResultDto  → respuesta de build/reset/off (ok + hdLine nueva)
//
// Geometría en E/N metros: cada punto es un double[2] = { easting, northing }.
// El wire sale snake_case por AgpJson; los nombres C# van PascalCase.
// ============================================================================

namespace AgroParallel.Models
{
    public sealed class HeadlandEditStateDto
    {
        public bool HasField { get; set; }
        public bool HasBoundary { get; set; }
        public bool IsHeadlandOn { get; set; }
        public bool IsSectionControlled { get; set; }
        public string Units { get; set; } = "m";
        public double ToolWidthM { get; set; }
        public double[][] Fence { get; set; } = new double[0][];
        public double[][] Headland { get; set; } = new double[0][];

        // ── Reshape manual (fase 2, ex FormHeadLine slice) ──────────────────
        // Todos los contornos (el tap A elige el más cercano entre todos).
        public System.Collections.Generic.List<double[][]> Fences { get; set; }
            = new System.Collections.Generic.List<double[][]>();
        public int BndSelect { get; set; }
        // Línea de corte vigente (con extensiones/offset ya aplicados).
        public double[][] Slice { get; set; } = new double[0][];
        public string SliceMode { get; set; }          // "curve" | "ab" | null
        public double[] APoint { get; set; }           // toque A pendiente (E/N)
        public double[] BPoint { get; set; }           // toque B (E/N)
        public bool CanUndo { get; set; }
        // distancia-cero | sin-contorno | cruces | null
        public string Error { get; set; }
    }

    public sealed class HeadlandEditResultDto
    {
        public bool Ok { get; set; }
        public bool IsHeadlandOn { get; set; }
        public double[][] Headland { get; set; } = new double[0][];
        public string Error { get; set; }
    }
}
