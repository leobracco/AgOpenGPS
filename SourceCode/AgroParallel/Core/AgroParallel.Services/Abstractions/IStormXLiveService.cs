// ============================================================================
// IStormXLiveService.cs — agregador de telemetría meteorológica StormX.
// Se suscribe (vía NodoRegistryService) a agp/storm/+/status_live y mantiene
// un snapshot por nodo con viento/temp/humedad/presión + verdict operativo.
//
// La UI consume vía GET /api/stormx/live — no toca MQTT directo.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IStormXLiveService
    {
        void Start();
        void Stop();
        StormXLiveSnapshotDto GetSnapshot();
        bool IsRunning { get; }

        /// <summary>Recarga config (llamar tras un PUT a stormX.json).</summary>
        void Reload();
    }
}
