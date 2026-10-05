// ============================================================================
// NudgeDtos.cs — estado del widget "Mover guía" (mover-guia.html), reemplazo de
// los WinForms FormNudge (guía activa) y FormRefNudge (guía de referencia).
// El movimiento se ve sobre el mapa de PilotX; el widget solo manda comandos.
// PascalCase en C#, snake_case en el cable vía AgpJson.
// ============================================================================

namespace AgroParallel.Models
{
    public sealed class NudgeStateDto
    {
        public bool Ok { get; set; } = true;

        // Necesita una guía activa (AB o curva); sin ella el widget avisa.
        public bool HasTrack { get; set; }

        // Corrimiento acumulado de la guía activa, en unidades display (cm o in),
        // con signo: negativo = izquierda, positivo = derecha (igual que lblOffset).
        public int OffsetDisplay { get; set; }

        // Paso de movimiento en unidades display (cm entero / in con 1 decimal).
        public double StepDisplay { get; set; }
        public double StepRefDisplay { get; set; }

        // Sesión de nudge de referencia: activa = hay backup para Cancelar.
        public bool RefActive { get; set; }
        // Distancia movida en la sesión de referencia (unidades display, con signo).
        public int RefMovedDisplay { get; set; }

        // "cm" | "in" (sin espacio).
        public string Units { get; set; } = "cm";

        public string Error { get; set; }
    }
}
