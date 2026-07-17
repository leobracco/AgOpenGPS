// ShiftPosSnapshot — estado del corrimiento de deriva GPS (drift compensation)
// para la pantalla de "Corregir posición" de PilotX (reemplazo HTML del WinForms
// FormShiftPos). Corrimiento norte/este en cm y si el corrimiento se mantiene
// aplicado. La página lo lee para inicializar sus controles; las escrituras van
// por POST /api/aog/guidance/command (shift_north_/shift_east_/shift_zero/
// offsets_on/offsets_off).

namespace AgroParallel.Models
{
    public sealed class ShiftPosSnapshot
    {
        /// <summary>Corrimiento hacia el norte, en centímetros (±9999).</summary>
        public double NorthCm { get; set; }

        /// <summary>Corrimiento hacia el este, en centímetros (±9999).</summary>
        public double EastCm { get; set; }

        /// <summary>Si el corrimiento se mantiene aplicado entre fixes.</summary>
        public bool OffsetsOn { get; set; }
    }
}
