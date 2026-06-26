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

using System.Text;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SysJson = System.Text.Json.JsonSerializer;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class PilotXUpdateController : WebApiController
    {
        private readonly IPilotXUpdateService _svc;

        public PilotXUpdateController(IPilotXUpdateService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/pilotx/update/status")]
        public async Task GetStatus()
        {
            var s = _svc != null ? _svc.GetStatus() : null;
            await Send(s).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/pilotx/update/check")]
        public async Task Check()
        {
            if (_svc == null) { await SendErr("service-unavailable").ConfigureAwait(false); return; }
            var s = await _svc.CheckAsync().ConfigureAwait(false);
            await Send(s).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/pilotx/update/download")]
        public async Task Download()
        {
            if (_svc == null) { await SendErr("service-unavailable").ConfigureAwait(false); return; }
            var s = await _svc.DownloadAsync().ConfigureAwait(false);
            await Send(s).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/pilotx/update/apply")]
        public async Task Apply()
        {
            if (_svc == null) { await SendErr("service-unavailable").ConfigureAwait(false); return; }
            var s = await _svc.ApplyAsync().ConfigureAwait(false);
            await Send(s).ConfigureAwait(false);
        }

        private Task Send(object payload)
        {
            string json = SysJson.Serialize(new { ok = true, status = payload }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            return HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        private Task SendErr(string code)
        {
            string json = SysJson.Serialize(new { ok = false, error = code });
            return HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }
    }
}
