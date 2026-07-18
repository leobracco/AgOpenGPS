// ============================================================================
// TramLineDtos.cs — POCOs para el constructor de tramlines HTML (tramlines.html).
//
//   TramLineStateDto    → GET/POST respuesta del estado completo
//   TramLineTrackDto    → línea de guiado disponible para generar trams
//
// Geometría en E/N metros: cada punto es double[2] = { easting, northing }.
// Wire snake_case por AgpJson; los nombres C# van PascalCase.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    public sealed class TramLineTrackDto
    {
        public int Index { get; set; }
        public string Name { get; set; }
        // "ab" | "curve"
        public string Mode { get; set; }
        public double[][] Points { get; set; } = new double[0][];
    }

    public sealed class TramLineStateDto
    {
        public bool Ok { get; set; } = true;
        public bool HasBoundary { get; set; }

        // Info de contexto
        public string Units { get; set; } = "m";
        public double TrackWidthDisplay { get; set; }
        public double TramWidthDisplay { get; set; }
        public double ToolWidthDisplay { get; set; }

        // Líneas de guiado disponibles (AB/Curve visibles)
        public List<TramLineTrackDto> Tracks { get; set; } = new List<TramLineTrackDto>();
        public int SelIdx { get; set; } = -1;

        // Trams nuevos (preview, aún no confirmados) — lista de polilíneas
        public List<double[][]> NewTrams { get; set; } = new List<double[][]>();
        // Trams guardados
        public List<double[][]> SavedTrams { get; set; } = new List<double[][]>();

        // Contorno exterior + outer/inner tram boundary
        public List<double[][]> Fences { get; set; } = new List<double[][]>();
        public double[][] OuterBnd { get; set; } = new double[0][];
        public double[][] InnerBnd { get; set; } = new double[0][];

        // Controles
        public int Passes { get; set; } = 2;
        public int StartPass { get; set; }
        public bool IsOuter { get; set; }
        public double Alpha { get; set; } = 1.0;

        // 3-tap cut state: 0=idle, 1=ptA marcado, 2=ptA+ptB marcados (esperando lado)
        public int CutStep { get; set; }
        public double[] PtA { get; set; }
        public double[] PtB { get; set; }

        public string Error { get; set; }
    }
}
