// ============================================================================
// ContornoDtos.cs — estado de la página "Contorno" (contorno.html), reemplazo
// de los WinForms FormBoundary (lista/borrar/KML) y FormBoundaryPlayer
// (grabar manejando). PascalCase en C#, snake_case en el cable vía AgpJson.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    public sealed class ContornoInfo
    {
        public int Index { get; set; }

        // index 0 = contorno exterior; el resto son internos (exclusiones).
        public bool IsOuter { get; set; }

        public double AreaHa { get; set; }

        // Solo aplica a internos: se puede manejar por encima (no gira el YouTurn).
        public bool IsDriveThru { get; set; }

        public int Points { get; set; }
    }

    public sealed class ContornoStateDto
    {
        public bool Ok { get; set; } = true;
        public bool JobStarted { get; set; }
        public double ToolWidth { get; set; }

        // true mientras hay una grabación manejando en curso (isBndBeingMade).
        public bool Recording { get; set; }

        public List<ContornoInfo> Boundaries { get; set; } = new List<ContornoInfo>();

        public string Error { get; set; }
    }

    public sealed class ContornoRecordDto
    {
        public bool Ok { get; set; } = true;

        public bool Active { get; set; }

        // paused = no se agregan puntos automáticos (isOkToAddPoints == false).
        public bool Paused { get; set; }

        public int Points { get; set; }
        public double AreaHa { get; set; }

        public double OffsetCm { get; set; }
        public bool RightSide { get; set; }
        public bool AtPivot { get; set; }

        // Grabar solo cuando las secciones están prendidas.
        public bool SectionRec { get; set; }

        public string Error { get; set; }
    }
}
