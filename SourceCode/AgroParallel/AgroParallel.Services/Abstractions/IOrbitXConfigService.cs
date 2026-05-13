// IOrbitXConfigService — CRUD de orbitX.json + status snapshot.
// Independiente de IOrbitXSyncService para que la UI HTML pueda
// editar la config aunque el syncer no esté corriendo (ej. wizard inicial).

using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IOrbitXConfigService
    {
        OrbitXConfigDto Load();
        void Save(OrbitXConfigDto dto);

        /// <summary>Snapshot para el dashboard (último sync, cloud conectado, etc).</summary>
        OrbitXStatus GetStatus();

        /// <summary>Hace un ping al server con las credenciales actuales.</summary>
        Task<bool> TestConnectionAsync();
    }
}
