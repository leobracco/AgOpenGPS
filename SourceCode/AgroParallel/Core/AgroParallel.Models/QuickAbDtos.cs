// ============================================================================
// QuickAbDtos.cs — estado del widget "AB rápido" (ab-rapido.html), reemplazo
// del WinForm FormQuickAB. Crea guías manejando: Curva (grabar puntos),
// Línea AB (punto A + punto B) y A+ (punto A + rumbo). El preview se ve en
// el mapa de PilotX; el widget solo manda comandos.
// PascalCase en C#, snake_case en el cable vía AgpJson.
// ============================================================================

namespace AgroParallel.Models
{
    public sealed class QuickAbStateDto
    {
        public bool Ok { get; set; } = true;

        /// <summary>Hay lote abierto (sin lote no se pueden crear guías).</summary>
        public bool HasField { get; set; }

        /// <summary>Modo activo: "none" | "curve" | "ab" | "aplus".</summary>
        public string Mode { get; set; } = "none";

        /// <summary>Fase: "choose" | "capture" | "name".</summary>
        public string Phase { get; set; } = "choose";

        /// <summary>Referencia del lado derecho (true) o izquierdo (false).</summary>
        public bool RefRight { get; set; } = true;

        /// <summary>Punto A ya marcado.</summary>
        public bool AMarked { get; set; }

        /// <summary>Punto B ya marcado (solo modo ab).</summary>
        public bool BMarked { get; set; }

        /// <summary>Curva: grabando puntos (no pausada).</summary>
        public bool Recording { get; set; }

        /// <summary>Curva: puntos grabados hasta ahora.</summary>
        public int Points { get; set; }

        /// <summary>Rumbo actual de la línea en grados (ab/aplus).</summary>
        public double HeadingDeg { get; set; }

        /// <summary>Nombre sugerido de la guía (fase name).</summary>
        public string SuggestedName { get; set; } = "";

        /// <summary>Al guardar: true si se apagó el autosteer/U-turn.</summary>
        public bool GuidanceStopped { get; set; }

        public string Error { get; set; }
    }
}
