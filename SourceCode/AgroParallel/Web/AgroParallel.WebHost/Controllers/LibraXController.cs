// ============================================================================
// LibraXController.cs
// Endpoints REST del módulo LibraX (monitor de rendimiento):
//   GET  /api/librax/config   → LibraXConfigDto (nodos + parámetros de UI)
//   POST /api/librax/config   (body = LibraXConfigDto) → { ok }
//   GET  /api/librax/nodos    → nodos LibraX detectados (vía NodoRegistry)
//   GET  /api/librax/live     → LibraXLiveSnapshotDto (telemetría runtime)
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
    public sealed class LibraXController : AgpControllerBase
    {
        private readonly ILibraXConfigService _cfg;
        private readonly INodoRegistryService _nodos;
        private readonly ILibraXLiveService _live;

        public LibraXController(ILibraXConfigService cfg, INodoRegistryService nodos, ILibraXLiveService live)
        {
            _cfg = cfg;
            _nodos = nodos;
            _live = live;
        }

        [Route(HttpVerbs.Get, "/librax/config")]
        public Task GetConfig()
        {
            if (_cfg == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_cfg.Load());
        }

        [Route(HttpVerbs.Post, "/librax/config")]
        public async Task SaveConfig()
        {
            if (_cfg == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var dto = await ReadJsonBodyAsync<LibraXConfigDto>();
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            // Validar ANTES de persistir — el POST es reemplazo completo del DTO.
            var val = ConfigValidation.ValidarLibraX(dto);
            if (!val.Ok)
            {
                await WriteErrorAsync(400, "AGP-CFG-001", "Config inválida", string.Join("; ", val.Errores));
                return;
            }
            _cfg.Save(dto);
            // El live service cachea el timeout y la lista de nodos: sin esto,
            // el cambio recién se vería al reiniciar PilotX.
            _live?.Reload();
            await WriteJsonAsync(new { ok = true });
        }

        // Filtra los nodos del NodoRegistry por type "librax".
        [Route(HttpVerbs.Get, "/librax/nodos")]
        public Task GetNodos()
        {
            if (_nodos == null) return WriteJsonAsync(new { ok = false, nodos = new object[0] });
            var all = _nodos.GetAll() ?? new List<NodoStatus>();
            var librax = all
                .Where(n => n != null && !string.IsNullOrEmpty(n.Type)
                            && n.Type.IndexOf("librax", System.StringComparison.OrdinalIgnoreCase) >= 0)
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
            return WriteJsonAsync(new { ok = true, nodos = librax });
        }

        [Route(HttpVerbs.Get, "/librax/live")]
        public Task GetLive()
        {
            if (_live == null) return WriteJsonAsync(new LibraXLiveSnapshotDto { MonitoreoActivo = false });
            return WriteJsonAsync(_live.GetSnapshot());
        }
    }
}
