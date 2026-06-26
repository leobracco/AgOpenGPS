// ============================================================================
// StormXController.cs
// Endpoints REST del módulo StormX (estación meteorológica móvil):
//   GET  /api/stormx/config   → StormXConfigDto (umbrales + nodos)
//   POST /api/stormx/config   (body = StormXConfigDto) → { ok }
//   GET  /api/stormx/nodos    → nodos StormX detectados (vía NodoRegistry)
//   GET  /api/stormx/live     → StormXLiveSnapshotDto (telemetría meteo runtime)
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class StormXController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IStormXConfigService _cfg;
        private readonly INodoRegistryService _nodos;
        private readonly IStormXLiveService _live;

        public StormXController(IStormXConfigService cfg, INodoRegistryService nodos, IStormXLiveService live)
        {
            _cfg = cfg;
            _nodos = nodos;
            _live = live;
        }

        [Route(HttpVerbs.Get, "/stormx/config")]
        public object GetConfig()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            return _cfg.Load();
        }

        [Route(HttpVerbs.Post, "/stormx/config")]
        public async Task<object> SaveConfig()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            var dto = JsonSerializer.Deserialize<StormXConfigDto>(body, JsonOpts);
            if (dto == null) return new { ok = false, error = "invalid-body" };
            _cfg.Save(dto);
            return new { ok = true };
        }

        // Filtra los nodos del NodoRegistry por type "storm". Hoy va a estar
        // vacío hasta que el firmware StormX publique `agp/storm/{uid}/announcement`.
        [Route(HttpVerbs.Get, "/stormx/nodos")]
        public object GetNodos()
        {
            if (_nodos == null) return new { ok = false, nodos = new object[0] };
            var all = _nodos.GetAll() ?? new List<NodoStatus>();
            var storm = all
                .Where(n => n != null && !string.IsNullOrEmpty(n.Type)
                            && n.Type.IndexOf("storm", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(n => new
                {
                    uid = n.Uid,
                    nombre = n.Type,
                    ip = n.Ip,
                    firmware = n.Firmware,
                    online = n.Online,
                    last_seen_utc = n.LastSeenUtc
                })
                .ToList();
            return new { ok = true, nodos = storm };
        }

        [Route(HttpVerbs.Get, "/stormx/live")]
        public object GetLive()
        {
            if (_live == null) return new StormXLiveSnapshotDto { MonitoreoActivo = false };
            return _live.GetSnapshot();
        }
    }
}
