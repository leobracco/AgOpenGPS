// ============================================================================
// CoreXBridgeController.cs
// Endpoint REST del puente de estado hacia CoreX (127.0.0.1:5181):
//   GET /api/corex-bridge/status → CoreXBridgeStatusDto (proxy resumido)
//
// Consumido por la tira de estado minimalista de hub.html (pills CoreX,
// Motor/Steer, GPS, IMU, Machine). Proxy delgado: la lógica de HTTP + timeout
// + fallback vive en CoreXBridgeService.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CoreXBridgeController : AgpControllerBase
    {
        private readonly ICoreXBridgeService _svc;

        public CoreXBridgeController(ICoreXBridgeService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/corex-bridge/status")]
        public async Task GetStatus()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error_code = "AGP-SYS-009", error = "Servicio no disponible." }).ConfigureAwait(false);
                return;
            }
            var dto = await _svc.GetStatusAsync().ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }
    }
}
