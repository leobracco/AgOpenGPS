// ============================================================================
// LineXController.cs
// Endpoints REST del módulo LineX (corte de siembra surco por surco):
//   GET  /api/linex/config              → LineXConfigDto
//   POST /api/linex/config              (body = LineXConfigDto) → { ok }
//   GET  /api/linex/nodos               → lista de nodos LineX detectados
//   GET  /api/linex/live                → LineXLiveSnapshotDto (estado por surco)
//   POST /api/linex/{uid}/config-push   publica agp/linex/{uid}/config (body=payload)
//   POST /api/linex/{uid}/test          publica agp/linex/{uid}/test ({ch,angle}|{ch,state})
//   POST /api/linex/{uid}/cmd           publica agp/linex/{uid}/cmd  (body con {cmd:…})
//
// Nota: a diferencia de FlowX, el firmware LineX escucha un único topic de
// comando agp/linex/{uid}/cmd y discrimina por el campo "cmd" del payload
// (ping/get_config/reboot/clear_safe_mode/ota), así que NO usamos sub-topic.
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
    public sealed class LineXController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Sin policy: cada DTO usa su [JsonPropertyName] explícito (snake_case).
        // Igual que FlowX/SectionX, evitamos que Swan emita PascalCase y rompa
        // el JS que espera snake_case.
        private static readonly JsonSerializerOptions JsonOutOpts = new JsonSerializerOptions();

        private readonly ILineXConfigService _cfg;
        private readonly INodoRegistryService _nodos;
        private readonly ILineXLiveService _live;

        public LineXController(ILineXConfigService cfg, INodoRegistryService nodos, ILineXLiveService live)
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

        [Route(HttpVerbs.Get, "/linex/config")]
        public async Task GetConfig()
        {
            if (_cfg == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            await SendJsonAsync(_cfg.Load());
        }

        [Route(HttpVerbs.Post, "/linex/config")]
        public async Task SaveConfig()
        {
            if (_cfg == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            LineXConfigDto dto;
            try { dto = JsonSerializer.Deserialize<LineXConfigDto>(body, JsonOpts); }
            catch { dto = null; }
            if (dto == null) { await SendJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            _cfg.Save(dto);
            _live?.Reload();
            await SendJsonAsync(new { ok = true });
        }

        // Nodos LineX descubiertos vía agp/linex/{uid}/announcement (4-part).
        // NodoRegistryService deriva el type del topic (parts[1]); filtramos por
        // "linex" case-insensitive.
        [Route(HttpVerbs.Get, "/linex/nodos")]
        public object GetNodos()
        {
            if (_nodos == null) return new { ok = false, nodos = new object[0] };
            var all = _nodos.GetAll() ?? new List<NodoStatus>();
            var linex = all
                .Where(n => n != null && !string.IsNullOrEmpty(n.Type)
                            && n.Type.IndexOf("linex", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(n => new
                {
                    uid = n.Uid,
                    nombre = n.Type,
                    ip = n.Ip,
                    firmware = n.Firmware,
                    online = n.Online,
                    last_seen_utc = n.LastSeenUtc,
                    uptime = n.Uptime,
                    boot_reason = n.BootReason,
                    safe_mode = n.SafeMode,
                    crash_count = n.CrashCount
                })
                .ToList();
            return new { ok = true, nodos = linex };
        }

        // Telemetría runtime (estado abierto/cerrado por surco, ángulo, pulso).
        [Route(HttpVerbs.Get, "/linex/live")]
        public async Task GetLive()
        {
            if (_live == null) { await SendJsonAsync(new LineXLiveSnapshotDto { MonitoreoActivo = false }); return; }
            await SendJsonAsync(_live.GetSnapshot());
        }

        // Publica config persistente al firmware en agp/linex/{uid}/config.
        // Body crudo se reenvía tal cual — la UI arma el objeto canónico que
        // entiende el firmware: { mdl:{section_count,pwm_freq,output_enable_pin,
        // comm_timeout_ms}, sections:[{idx,backend,channel,open_angle,close_angle,
        // min_us,max_us,travel_ms,failsafe_open,invert}] }.
        [Route(HttpVerbs.Post, "/linex/{uid}/config-push")]
        public async Task<object> PushConfig(string uid)
        {
            if (_nodos == null) return new { ok = false, error = "mqtt-unavailable" };
            if (string.IsNullOrWhiteSpace(uid)) return new { ok = false, error = "uid-required" };

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return new { ok = false, error = "empty-body" };

            string topic = "agp/linex/" + uid + "/config";
            bool ok = await _nodos.PublishAsync(topic, body, false).ConfigureAwait(false);
            return new { ok, topic };
        }

        // Prueba manual de un surco en agp/linex/{uid}/test.
        // Body canónico: { "ch": <idx>, "angle": <grados> }  (servo, ángulo directo)
        //            o:  { "ch": <idx>, "state": "open"|"close" }
        [Route(HttpVerbs.Post, "/linex/{uid}/test")]
        public async Task<object> SendTest(string uid)
        {
            if (_nodos == null) return new { ok = false, error = "mqtt-unavailable" };
            if (string.IsNullOrWhiteSpace(uid)) return new { ok = false, error = "uid-required" };

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return new { ok = false, error = "empty-body" };

            string topic = "agp/linex/" + uid + "/test";
            bool ok = await _nodos.PublishAsync(topic, body, false).ConfigureAwait(false);
            return new { ok, topic };
        }

        // Comando al nodo en agp/linex/{uid}/cmd. El firmware discrimina por el
        // campo "cmd" del payload: ping | get_config | reboot | clear_safe_mode | ota.
        // Body crudo (JSON con {cmd:…}) se reenvía tal cual.
        [Route(HttpVerbs.Post, "/linex/{uid}/cmd")]
        public async Task<object> SendCmd(string uid, [QueryField] bool retain)
        {
            if (_nodos == null) return new { ok = false, error = "mqtt-unavailable" };
            if (string.IsNullOrWhiteSpace(uid)) return new { ok = false, error = "uid-required" };

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) body = "{}";

            string topic = "agp/linex/" + uid + "/cmd";
            bool ok = await _nodos.PublishAsync(topic, body, retain).ConfigureAwait(false);
            return new { ok, topic };
        }
    }
}
