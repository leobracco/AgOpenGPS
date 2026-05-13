// ============================================================================
// VistaXController.cs — REST del módulo VistaX.
//
//   GET  /api/vistax/config              → VistaXConfigDto
//   PUT  /api/vistax/config              body: VistaXConfigDto
//   GET  /api/vistax/implemento          → VistaXImplementoDto
//   PUT  /api/vistax/implemento          body: VistaXImplementoDto
//   GET  /api/vistax/live                → VistaXLiveSnapshotDto
//   POST /api/vistax/reload              fuerza recarga config + implemento
// ============================================================================

using System.IO;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Swan.Formatters;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class VistaXController : WebApiController
    {
        private readonly IVistaXConfigService _cfg;
        private readonly IVistaXLiveService _live;

        public VistaXController(IVistaXConfigService cfg, IVistaXLiveService live)
        {
            _cfg = cfg;
            _live = live;
        }

        [Route(HttpVerbs.Get, "/vistax/config")]
        public object GetConfig()
        {
            if (_cfg == null) return Unavailable();
            return _cfg.GetConfig();
        }

        [Route(HttpVerbs.Put, "/vistax/config")]
        public async System.Threading.Tasks.Task<object> PutConfig()
        {
            if (_cfg == null) return Unavailable();
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            VistaXConfigDto dto;
            try { dto = Json.Deserialize<VistaXConfigDto>(body); }
            catch { return new { ok = false, error = "invalid-json" }; }
            _cfg.SaveConfig(dto);
            _live?.Reload();
            return new { ok = true };
        }

        [Route(HttpVerbs.Get, "/vistax/implemento")]
        public object GetImplemento()
        {
            if (_cfg == null) return Unavailable();
            return new
            {
                path = _cfg.GetImplementoPath(),
                implemento = _cfg.GetImplemento()
            };
        }

        [Route(HttpVerbs.Put, "/vistax/implemento")]
        public async System.Threading.Tasks.Task<object> PutImplemento()
        {
            if (_cfg == null) return Unavailable();
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            VistaXImplementoDto dto;
            try { dto = Json.Deserialize<VistaXImplementoDto>(body); }
            catch { return new { ok = false, error = "invalid-json" }; }
            _cfg.SaveImplemento(dto);
            _live?.Reload();
            return new { ok = true };
        }

        [Route(HttpVerbs.Get, "/vistax/live")]
        public object GetLive()
        {
            if (_live == null) return Unavailable();
            return _live.GetSnapshot();
        }

        [Route(HttpVerbs.Post, "/vistax/reload")]
        public object Reload()
        {
            _cfg?.GetConfig(); // ensure file touched
            _live?.Reload();
            return new { ok = true };
        }

        private object Unavailable() => new { ok = false, error = "service-unavailable" };
    }
}
