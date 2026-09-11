// ============================================================================
// CabeceraLineasDtos.cs — POCOs del constructor de cabecera por líneas
// (cabecera-lineas.html, ex FormHeadAche). Un solo DTO de estado que viaja
// completo tras cada acción (no hay poll: la geometría solo cambia por acción
// del usuario). Geometría en E/N metros: punto = double[2] {easting, northing}.
// Wire snake_case vía AgpJson.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    public sealed class CabeceraLineasTrackDto
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Mode { get; set; } = "curve";   // "curve" | "ab"
        public double[][] Points { get; set; } = new double[0][];
    }

    public sealed class CabeceraLineasStateDto
    {
        public bool Ok { get; set; } = true;
        public bool JobStarted { get; set; }
        public bool HasBoundary { get; set; }
        public string Units { get; set; } = "m";

        // Ancho útil (width - overlap) en unidades display, para el combo "x ancho".
        public double ToolWidthDisplay { get; set; }

        // Todos los contornos (fenceLine) del lote. [bnd][punto][e,n].
        public List<double[][]> Fences { get; set; } = new List<double[][]>();

        // Contorno seleccionado por el primer tap (para pintarlo distinto).
        public int BndSelect { get; set; }

        // Líneas construidas + índice seleccionado (hdl.idx; -1 = ninguna).
        public List<CabeceraLineasTrackDto> Tracks { get; set; } = new List<CabeceraLineasTrackDto>();
        public int SelIdx { get; set; } = -1;

        // Cabecera armada (bnd.bndList[0].hdLine).
        public double[][] HdLine { get; set; } = new double[0][];

        // Puntos A/B tocados sobre el contorno (null = sin tocar).
        public double[] APoint { get; set; }
        public double[] BPoint { get; set; }

        public bool IsSectionControlled { get; set; }

        // Códigos: sin-lote, sin-contorno, mismo-punto, una-sola-linea, cruces.
        public string Error { get; set; }
    }
}
