// ============================================================================
// TramSimpleDtos.cs — estado del editor "Tramlines simples" (reemplazo del
// WinForms FormTram). Los tramlines (wheel tracks) se dibujan sobre el mapa de
// PilotX; este widget solo ajusta pasadas / modo / alpha / swap AB y reconstruye.
// PascalCase en C#, snake_case en el cable vía AgpJson.
// ============================================================================

namespace AgroParallel.Models
{
    public sealed class TramSimpleStateDto
    {
        public bool Ok { get; set; } = true;

        // Necesita una guía activa (AB o curva). Sin ella no hay nada que tramear.
        public bool HasTrack { get; set; }
        // Los modos "boundary" solo tienen sentido con contorno.
        public bool HasBoundary { get; set; }
        // true si la guía activa es curva (define curve.BuildTram vs ABLine.BuildTram).
        public bool IsCurve { get; set; }

        public int Passes { get; set; }
        public int AlphaPercent { get; set; }

        // "All" | "FillTracks" | "BoundaryTracks" | "None"
        public string Mode { get; set; } = "All";

        // Anchos en unidades display (m o ft) para mostrar en el panel.
        public double ToolWidthDisplay { get; set; }
        public double TramWidthDisplay { get; set; }
        public double TrackWidthDisplay { get; set; }

        // "m" | "ft" (sin espacio).
        public string Units { get; set; } = "m";

        public string Error { get; set; }
    }
}
