// ============================================================================
// FlowXController.cs
// Endpoints REST del módulo FlowX (control de corte + dosis para pulverizadoras):
//   GET  /api/flowx/config             → FlowXConfigDto
//   POST /api/flowx/config             (body = FlowXConfigDto) → { ok }
//   GET  /api/flowx/nodos              → lista de nodos FlowX detectados (vía NodoRegistry)
//   GET  /api/flowx/live               → FlowXLiveSnapshotDto (telemetría runtime)
//   POST /api/flowx/{uid}/cmd?verb=…   publica agp/flow/{uid}/cmd/{verb} (body=payload)
//   GET  /api/flowx/{uid}/autotune     último resultado de auto-tune (si lo hay)
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
    public sealed class FlowXController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IFlowXConfigService _cfg;
        private readonly INodoRegistryService _nodos;
        private readonly IFlowXLiveService _live;

        public FlowXController(IFlowXConfigService cfg, INodoRegistryService nodos, IFlowXLiveService live)
        {
            _cfg = cfg;
            _nodos = nodos;
            _live = live;
        }

        [Route(HttpVerbs.Get, "/flowx/config")]
        public object GetConfig()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            return _cfg.Load();
        }

        [Route(HttpVerbs.Post, "/flowx/config")]
        public async Task<object> SaveConfig()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            var dto = JsonSerializer.Deserialize<FlowXConfigDto>(body, JsonOpts);
            if (dto == null) return new { ok = false, error = "invalid-body" };
            _cfg.Save(dto);
            return new { ok = true };
        }

        // Nodos FlowX descubiertos vía agp/flow/{uid}/announcement (4-part).
        // NodoRegistryService deriva el type del topic (parts[1]), así que
        // filtramos por "flow" case-insensitive.
        [Route(HttpVerbs.Get, "/flowx/nodos")]
        public object GetNodos()
        {
            if (_nodos == null) return new { ok = false, nodos = new object[0] };
            var all = _nodos.GetAll() ?? new List<NodoStatus>();
            var flow = all
                .Where(n => n != null && !string.IsNullOrEmpty(n.Type)
                            && n.Type.IndexOf("flow", System.StringComparison.OrdinalIgnoreCase) >= 0)
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
            return new { ok = true, nodos = flow };
        }

        // Telemetría runtime de los nodos FlowX (caudal real, PWM, estado PID).
        // Si el live service no está cableado todavía, devolvemos un snapshot vacío
        // para que la UI no rompa.
        [Route(HttpVerbs.Get, "/flowx/live")]
        public object GetLive()
        {
            if (_live == null) return new FlowXLiveSnapshotDto { MonitoreoActivo = false };
            return _live.GetSnapshot();
        }

        // Publica un comando arbitrario al nodo en agp/flow/{uid}/cmd/{verb}.
        // Verbos esperados (definidos en el firmware):
        //   calibrar_start   { vol_l: 1.0 }   — empieza a contar pulsos
        //   calibrar_stop    {}               — el ESP responde con pulsos contados
        //   autotune_start   {}               — Ziegler-Nichols ~30s
        //   autotune_stop    {}
        // Body crudo (JSON) se reenvía tal cual al payload MQTT.
        [Route(HttpVerbs.Post, "/flowx/{uid}/cmd")]
        public async Task<object> SendCmd(string uid, [QueryField] string verb, [QueryField] bool retain)
        {
            if (_nodos == null) return new { ok = false, error = "mqtt-unavailable" };
            if (string.IsNullOrWhiteSpace(uid)) return new { ok = false, error = "uid-required" };
            if (string.IsNullOrWhiteSpace(verb)) return new { ok = false, error = "verb-required" };

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) body = "{}";

            string topic = "agp/flow/" + uid + "/cmd/" + verb;
            bool ok = await _nodos.PublishAsync(topic, body, retain).ConfigureAwait(false);
            return new { ok, topic };
        }

        // Último resultado de auto-tune / calibración cacheado por el live service.
        // Si el firmware todavía no responde, devolvemos hasResult=false y la UI
        // sigue polleando hasta el timeout.
        [Route(HttpVerbs.Get, "/flowx/{uid}/autotune")]
        public object GetAutoTune(string uid)
        {
            if (_live == null) return new { ok = true, hasResult = false };
            var r = _live.GetAutoTuneResult(uid);
            if (r == null) return new { ok = true, hasResult = false };
            return new { ok = true, hasResult = true, result = r };
        }

        [Route(HttpVerbs.Get, "/flowx/{uid}/calibrar")]
        public object GetCalibrar(string uid)
        {
            if (_live == null) return new { ok = true, hasResult = false };
            var r = _live.GetCalibrarResult(uid);
            if (r == null) return new { ok = true, hasResult = false };
            return new { ok = true, hasResult = true, result = r };
        }
    }
}
