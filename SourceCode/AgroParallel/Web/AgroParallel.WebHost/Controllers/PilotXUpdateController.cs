// ============================================================================
// PilotXUpdateController.cs
// Endpoints REST del auto-update del propio PilotX (la app de PC).
//   GET  /api/pilotx/update/status     → snapshot (versión actual, disponible, fase)
//   POST /api/pilotx/update/check      → consulta el cloud
//   POST /api/pilotx/update/download   → baja ZIP y verifica SHA256
//   POST /api/pilotx/update/apply      → spawnea updater y cierra PilotX (el host
//                                         decide *cuándo* salir; el endpoint solo
//                                         dispara). El cliente debería esperar
//                                         unos segundos y reload de la app.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class PilotXUpdateController : AgpControllerBase
    {
        private readonly IPilotXUpdateService _svc;

        public PilotXUpdateController(IPilotXUpdateService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/pilotx/update/status")]
        public Task GetStatus()
        {
            var s = _svc != null ? _svc.GetStatus() : null;
            return WriteJsonAsync(new { ok = true, status = s });
        }

        [Route(HttpVerbs.Post, "/pilotx/update/check")]
        public async Task Check()
        {
            if (_svc == null) { await WriteErrorAsync(503, "service-unavailable", "Servicio no disponible"); return; }
            var s = await _svc.CheckAsync().ConfigureAwait(false);
            await WriteJsonAsync(new { ok = true, status = s });
        }

        [Route(HttpVerbs.Post, "/pilotx/update/download")]
        public async Task Download()
        {
            if (_svc == null) { await WriteErrorAsync(503, "service-unavailable", "Servicio no disponible"); return; }
            var s = await _svc.DownloadAsync().ConfigureAwait(false);
            await WriteJsonAsync(new { ok = true, status = s });
        }

        [Route(HttpVerbs.Post, "/pilotx/update/apply")]
        public async Task Apply()
        {
            if (_svc == null) { await WriteErrorAsync(503, "service-unavailable", "Servicio no disponible"); return; }
            var s = await _svc.ApplyAsync().ConfigureAwait(false);
            await WriteJsonAsync(new { ok = true, status = s });
        }
    }
}
