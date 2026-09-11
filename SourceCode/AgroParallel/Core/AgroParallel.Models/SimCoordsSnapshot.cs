// SimCoordsSnapshot — estado para la pantalla de coordenadas del simulador de
// PilotX (reemplazo HTML del WinForms FormSimCoords). Muestra la lat/lon guardada
// del simulador y si se puede aplicar (el simulador tiene que estar encendido y
// no puede haber un lote abierto). Aplicar teletransporta el simulador a la
// coordenada vía POST /api/aog/guidance/command (sim_coords_<lat>_<lon>).

namespace AgroParallel.Models
{
    public sealed class SimCoordsSnapshot
    {
        /// <summary>Latitud guardada del simulador (grados decimales).</summary>
        public double Latitude { get; set; }

        /// <summary>Longitud guardada del simulador (grados decimales).</summary>
        public double Longitude { get; set; }

        /// <summary>true si el simulador está encendido (timerSim activo).</summary>
        public bool SimOn { get; set; }

        /// <summary>true si hay un lote abierto (no se puede reubicar el simulador).</summary>
        public bool JobStarted { get; set; }
    }
}
