// ============================================================================
// FlowXController.cs
// Endpoints REST del módulo FlowX (control de corte + dosis para pulverizadoras):
//   GET  /api/flowx/config              → FlowXConfigDto
//   POST /api/flowx/config              (body = FlowXConfigDto) → { ok }
//   GET  /api/flowx/nodos               → lista de nodos FlowX detectados (con
//                                          uptime/boot_reason/safe_mode/crash_count)
//   GET  /api/flowx/live                → FlowXLiveSnapshotDto (telemetría runtime)
//   POST /api/flowx/{uid}/cmd?verb=…    publica agp/flow/{uid}/cmd/{verb} (body=payload)
//   POST /api/flowx/{uid}/config-push   publica agp/flow/{uid}/config (body=payload)
//   GET  /api/flowx/{uid}/autotune      último resultado de auto-tune (si lo hay)
//   GET  /api/flowx/{uid}/calibrar      último resultado de calibración (si lo hay)
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        // Sin policy: cada DTO usa su [JsonPropertyName] explícito (snake_case).
        // Swan (serializer default de EmbedIO) ignora esos atributos y emite
        // PascalCase — el JS espera snake_case, así que loadCfg() veía nodos
        // undefined y el operario percibía "habilité FlowX, salí, volví y se
        // deshabilitó". Mismo bug que ya estaba documentado en SectionXController.
        private static readonly JsonSerializerOptions JsonOutOpts = new JsonSerializerOptions();

        private readonly IFlowXConfigService _cfg;
        private readonly INodoRegistryService _nodos;
        private readonly IFlowXLiveService _live;

        public FlowXController(IFlowXConfigService cfg, INodoRegistryService nodos, IFlowXLiveService live)
        {
            _cfg = cfg;
            _nodos = nodos;
            _live = live;
        }

        private async Task SendJsonAsync(object obj)
        {
            string json = JsonSerializer.Serialize(obj, JsonOutOpts);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/flowx/config")]
        public async Task GetConfig()
        {
            if (_cfg == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            await SendJsonAsync(_cfg.Load());
        }

        [Route(HttpVerbs.Post, "/flowx/config")]
        public async Task SaveConfig()
        {
            if (_cfg == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            FlowXConfigDto dto;
            try { dto = JsonSerializer.Deserialize<FlowXConfigDto>(body, JsonOpts); }
            catch { dto = null; }
            if (dto == null) { await SendJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            _cfg.Save(dto);
            await SendJsonAsync(new { ok = true });
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
                    last_seen_utc = n.LastSeenUtc,
                    // Diagnóstico (Tanda 2) — útil en campo sin USB serial.
                    // En firmwares legacy boot_reason=null, safe_mode=false, crash_count=0.
                    uptime = n.Uptime,
                    boot_reason = n.BootReason,
                    safe_mode = n.SafeMode,
                    crash_count = n.CrashCount
                })
                .ToList();
            return new { ok = true, nodos = flow };
        }

        // Publica config persistente (electroválvulas + caudalímetro) al firmware.
        // Topic: agp/flow/{uid}/config (no es un cmd, es una rama aparte del API).
        // Body crudo se reenvía tal cual — la UI debe armar el objeto con los
        // campos canónicos que entiende el firmware: meterCal, is3Wire,
        // invertRelay, sectionIs3Wire[10]. Cualquier campo extra lo ignora el
        // nodo. El firmware guarda diferido en /config.json (LittleFS).
        [Route(HttpVerbs.Post, "/flowx/{uid}/config-push")]
        public async Task<object> PushConfig(string uid)
        {
            if (_nodos == null) return new { ok = false, error = "mqtt-unavailable" };
            if (string.IsNullOrWhiteSpace(uid)) return new { ok = false, error = "uid-required" };

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return new { ok = false, error = "empty-body" };

            string topic = "agp/flow/" + uid + "/config";
            bool ok = await _nodos.PublishAsync(topic, body, false).ConfigureAwait(false);
            return new { ok, topic };
        }

        // Telemetría runtime de los nodos FlowX (caudal real, PWM, estado PID).
        // Si el live service no está cableado todavía, devolvemos un snapshot vacío
        // para que la UI no rompa.
        [Route(HttpVerbs.Get, "/flowx/live")]
        public async Task GetLive()
        {
            if (_live == null) { await SendJsonAsync(new FlowXLiveSnapshotDto { MonitoreoActivo = false }); return; }
            await SendJsonAsync(_live.GetSnapshot());
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
        public async Task GetAutoTune(string uid)
        {
            if (_live == null) { await SendJsonAsync(new { ok = true, hasResult = false }); return; }
            var r = _live.GetAutoTuneResult(uid);
            if (r == null) { await SendJsonAsync(new { ok = true, hasResult = false }); return; }
            await SendJsonAsync(new { ok = true, hasResult = true, result = r });
        }

        [Route(HttpVerbs.Get, "/flowx/{uid}/calibrar")]
        public async Task GetCalibrar(string uid)
        {
            if (_live == null) { await SendJsonAsync(new { ok = true, hasResult = false }); return; }
            var r = _live.GetCalibrarResult(uid);
            if (r == null) { await SendJsonAsync(new { ok = true, hasResult = false }); return; }
            await SendJsonAsync(new { ok = true, hasResult = true, result = r });
        }

        // Caracterización: el firmware barre PWM 0..4095 y publica la curva +
        // pwm_min real (primer PWM con flujo) + pwm_min_estable (PID-utilizable)
        // + hz_max. La UI lo dispara con cmd verb=caracterizar_start y pollea
        // este endpoint hasta hasResult=true. El raw es el JSON tal cual lo
        // emite el firmware — devolvemos como string crudo para no tener que
        // tipar la curva.
        [Route(HttpVerbs.Get, "/flowx/{uid}/caracterizar")]
        public async Task GetCaracterizar(string uid)
        {
            if (_live == null) { await SendJsonAsync(new { ok = true, hasResult = false }); return; }
            string raw = _live.GetCaracterizarResultRaw(uid);
            if (string.IsNullOrEmpty(raw))
            {
                await SendJsonAsync(new { ok = true, hasResult = false });
                return;
            }
            // Emitimos un envelope { ok, hasResult, result:<raw inline> }. Como
            // el raw ya es JSON, lo concatenamos en vez de re-serializar.
            string json = "{\"ok\":true,\"hasResult\":true,\"result\":" + raw + "}";
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8)
                .ConfigureAwait(false);
        }

        // Limpia el último resultado cacheado — la UI lo llama antes de
        // disparar caracterizar_start para no leer una corrida vieja.
        [Route(HttpVerbs.Delete, "/flowx/{uid}/caracterizar")]
        public async Task ClearCaracterizar(string uid)
        {
            if (_live == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            _live.ClearCaracterizarResult(uid);
            await SendJsonAsync(new { ok = true });
        }
    }
}
