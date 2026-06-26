// ============================================================================
// ICamarasConfigService — fachada del módulo Cámaras para la UI HTML.
//   * Get/Save de CamarasConfigDto (camaras.json en Documents\AgOpenGPS).
//   * FetchSnapshotAsync proxy con auth (Basic/Digest Hikvision) para que el
//     <img> del browser no tenga que hablar credenciales con la cámara IP.
// La implementación vive en GPS (lee de CamarasConfig.cs legacy mientras
// dura la migración) — el WebHost sólo conoce esta abstracción.
// ============================================================================

using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public sealed class CameraSnapshot
    {
        public byte[] Bytes { get; set; }
        public string ContentType { get; set; }
        public string Error { get; set; }
    }

    public interface ICamarasConfigService
    {
        CamarasConfigDto GetConfig();
        void SaveConfig(CamarasConfigDto cfg);
        Task<CameraSnapshot> FetchSnapshotAsync(int index, CancellationToken ct);
    }
}
