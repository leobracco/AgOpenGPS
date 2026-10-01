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

        // ── Punto de referencia contra la deriva (ref_marcar / ref_volver) ──

        /// <summary>El lote abierto tiene un punto de referencia marcado.</summary>
        public bool RefMarcada { get; set; }

        /// <summary>Cuándo se marcó (ISO 8601 UTC), "" si no hay.</summary>
        public string RefMarcadaUtc { get; set; } = "";

        /// <summary>Qué pasó con el último ref_marcar/ref_volver, en castellano
        /// para el operario (motivo del rechazo o cuánto se corrigió).</summary>
        public string RefMensaje { get; set; } = "";

        /// <summary>El último ref_marcar/ref_volver se aplicó.</summary>
        public bool RefOk { get; set; }
    }
}
