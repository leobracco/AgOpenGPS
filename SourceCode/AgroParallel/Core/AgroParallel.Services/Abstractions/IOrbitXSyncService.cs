// IOrbitXSyncService — sincronización con OrbitX cloud
// (heartbeat, PilotX sync, tracking, prescripciones, OTA relay).

using System.Threading.Tasks;

namespace AgroParallel.Services.Abstractions
{
    public interface IOrbitXSyncService
    {
        bool IsRunning { get; }
        string LastError { get; }

        Task StartAsync();
        void Stop();

        /// <summary>Forzar un heartbeat inmediato (testeo de credenciales).</summary>
        Task<bool> HeartbeatNowAsync();
    }
}
