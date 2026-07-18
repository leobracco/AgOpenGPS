// ============================================================================
// TrackBuilderDtos.cs — POCOs para el gestor de tracks HTML (tracks.html).
// Wire snake_case por AgpJson; nombres C# PascalCase.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    public sealed class TrackBuilderStateDto
    {
        public bool Ok { get; set; } = true;
        public List<TrackItemDto> Tracks { get; set; } = new List<TrackItemDto>();
        public int SelectedIdx { get; set; } = -1;
        public int ActiveIdx { get; set; } = -1;  // trk.idx (guía activa para el guiado)

        // Geometría para el canvas (dibujo de tracks + contornos)
        public List<TrackGeomDto> TrackGeoms { get; set; } = new List<TrackGeomDto>();
        public List<double[][]> Fences { get; set; } = new List<double[][]>();
        public int BndSelect { get; set; }

        // Touch state (tap A/B para crear línea desde contorno)
        public double[] APoint { get; set; }
        public double[] BPoint { get; set; }
        public bool CanMakeLine { get; set; }    // true tras tap B
        public bool HasBoundaryCurve { get; set; } // true si ya hay un bndCurve

        public string Error { get; set; }
    }

    public sealed class TrackGeomDto
    {
        public int Index { get; set; }
        public string Mode { get; set; }  // "ab"|"curve"|"bnd_curve"|"water_pivot"
        public double[][] Points { get; set; } = new double[0][];
    }
}
