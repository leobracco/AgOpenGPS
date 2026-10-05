// EventLogSnapshot — DTO read-only del visor de eventos de PilotX (reemplazo
// HTML de la vieja ventana WinForms FormEventViewer). Producido por
// IAogStateProvider.GetEventLog(): la cola del log persistido en disco +
// el buffer de eventos de la sesión actual, para que la página sea un
// renderer sin lógica.

namespace AgroParallel.Models
{
    public sealed class EventLogSnapshot
    {
        /// <summary>Ruta del archivo de log en disco (informativa para el pie).</summary>
        public string File { get; set; }

        /// <summary>Cola del log histórico persistido (últimos KB del archivo).</summary>
        public string History { get; set; }

        /// <summary>Eventos acumulados en la sesión actual (Log.sbEvents).</summary>
        public string Session { get; set; }
    }
}
