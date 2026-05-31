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

        /// <summary>
        /// Estado del flow de vinculación táctil. Si el tractor todavía no
        /// tiene device_token propio, genera un código de 6 chars y lo anuncia
        /// al cloud (pair/init). Luego pollea pair/status; cuando el operario
        /// reclama el código, persiste token+estab_slug a orbitX.json y
        /// devuelve Paired=true (con JustClaimed=true UNA vez).
        /// La UI lo llama cada 4s mientras la pantalla OrbitX está abierta.
        /// </summary>
        Task<OrbitXPairInfo> GetPairInfoAsync();

        /// <summary>
        /// Desvincula localmente: limpia DeviceToken y EstabSlug en orbitX.json
        /// y resetea el state in-memory de pairing para que la próxima llamada
        /// a GetPairInfoAsync genere un código nuevo.
        /// </summary>
        void ResetPairing();
    }
}
