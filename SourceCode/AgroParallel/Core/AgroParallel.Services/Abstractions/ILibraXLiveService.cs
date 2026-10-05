// ============================================================================
// ILibraXLiveService.cs — agregador de telemetría del monitor de rendimiento.
// Se suscribe (vía NodoRegistryService) a agp/librax/+/status_live y mantiene
// un snapshot por nodo con ratio/paletas/rpm/humedad + salud del sensor.
//
// La UI consume vía GET /api/librax/live — no toca MQTT directo.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ILibraXLiveService
    {
        void Start();
        void Stop();
        LibraXLiveSnapshotDto GetSnapshot();
        bool IsRunning { get; }

        /// <summary>Recarga config (llamar tras un POST a libraX.json).</summary>
        void Reload();
    }
}
