// ============================================================================
// ILineXLiveService.cs — agregador de telemetría LineX en vivo.
// Se suscribe (vía NodoRegistryService) a agp/linex/+/status_live y mantiene
// un snapshot consolidado por nodo (estado abierto/cerrado de cada surco,
// ángulo, pulso, rssi, uptime).
//
// La UI consume vía GET /api/linex/live — no toca MQTT directo.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ILineXLiveService
    {
        /// <summary>Arranca la suscripción.</summary>
        void Start();

        /// <summary>Detiene el agregador.</summary>
        void Stop();

        /// <summary>Snapshot consolidado para enviar al cliente HTTP.</summary>
        LineXLiveSnapshotDto GetSnapshot();

        /// <summary>True si está consumiendo telemetría activamente.</summary>
        bool IsRunning { get; }

        /// <summary>Recarga config (llamar tras un POST a lineX.json).</summary>
        void Reload();
    }
}
