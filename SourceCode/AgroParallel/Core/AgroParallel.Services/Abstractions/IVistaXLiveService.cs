// ============================================================================
// IVistaXLiveService.cs — agregador de telemetría VistaX en vivo.
// Se suscribe (vía NodoRegistryService) al topic de telemetría configurado
// y mantiene un snapshot consolidado por tren/surco a partir de las lecturas
// de los ESP32. Calcula SPM (semillas/minuto) por surco mediante derivada
// temporal de "valor" y aplica timeout para marcar sensores como "no-data".
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IVistaXLiveService
    {
        /// <summary>Arranca la suscripción y los timers de cálculo.</summary>
        void Start();

        /// <summary>Detiene el agregador.</summary>
        void Stop();

        /// <summary>Snapshot consolidado para enviar al cliente HTTP.</summary>
        VistaXLiveSnapshotDto GetSnapshot();

        /// <summary>True si está consumiendo telemetría activamente.</summary>
        bool IsRunning { get; }

        /// <summary>Recarga config + implemento (llamar tras un PUT).</summary>
        void Reload();
    }
}
