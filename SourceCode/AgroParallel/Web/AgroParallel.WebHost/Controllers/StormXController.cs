// ============================================================================
// StormXController.cs
// Endpoints REST del módulo StormX (estación meteorológica móvil):
//   GET  /api/stormx/config   → StormXConfigDto (umbrales + nodos)
//   POST /api/stormx/config   (body = StormXConfigDto) → { ok }
//   GET  /api/stormx/nodos    → nodos StormX detectados (vía NodoRegistry)
//   GET  /api/stormx/live     → StormXLiveSnapshotDto (telemetría meteo runtime)
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class StormXController : AgpControllerBase
    {
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
        public Task GetConfig()
        {
            if (_cfg == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_cfg.Load());
        }

        [Route(HttpVerbs.Post, "/stormx/config")]
        public async Task SaveConfig()
        {
            if (_cfg == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var dto = await ReadJsonBodyAsync<StormXConfigDto>();
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            _cfg.Save(dto);
            await WriteJsonAsync(new { ok = true });
        }

        // Filtra los nodos del NodoRegistry por type "storm". Hoy va a estar
        // vacío hasta que el firmware StormX publique `agp/storm/{uid}/announcement`.
        [Route(HttpVerbs.Get, "/stormx/nodos")]
        public Task GetNodos()
        {
            if (_nodos == null) return WriteJsonAsync(new { ok = false, nodos = new object[0] });
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
            return WriteJsonAsync(new { ok = true, nodos = storm });
        }

        [Route(HttpVerbs.Get, "/stormx/live")]
        public Task GetLive()
        {
            if (_live == null) return WriteJsonAsync(new StormXLiveSnapshotDto { MonitoreoActivo = false });
            return WriteJsonAsync(_live.GetSnapshot());
        }
    }
}
