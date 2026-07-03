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
using System.Linq;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class LineXController : AgpControllerBase
    {
        private readonly ILineXConfigService _cfg;
        private readonly INodoRegistryService _nodos;
        private readonly ILineXLiveService _live;

        public LineXController(ILineXConfigService cfg, INodoRegistryService nodos, ILineXLiveService live)
        {
            _cfg = cfg;
            _nodos = nodos;
            _live = live;
        }

        [Route(HttpVerbs.Get, "/linex/config")]
        public Task GetConfig()
        {
            if (_cfg == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_cfg.Load());
        }

        [Route(HttpVerbs.Post, "/linex/config")]
        public async Task SaveConfig()
        {
            if (_cfg == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            LineXConfigDto dto;
            try { dto = await ReadJsonBodyAsync<LineXConfigDto>(); }
            catch { dto = null; }
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            _cfg.Save(dto);
            _live?.Reload();
            await WriteJsonAsync(new { ok = true });
        }

        // Nodos LineX descubiertos vía agp/linex/{uid}/announcement (4-part).
        // NodoRegistryService deriva el type del topic (parts[1]); filtramos por
        // "linex" case-insensitive.
        [Route(HttpVerbs.Get, "/linex/nodos")]
        public Task GetNodos()
        {
            if (_nodos == null) return WriteJsonAsync(new { ok = false, nodos = new object[0] });
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
            return WriteJsonAsync(new { ok = true, nodos = linex });
        }

        // Telemetría runtime (estado abierto/cerrado por surco, ángulo, pulso).
        [Route(HttpVerbs.Get, "/linex/live")]
        public Task GetLive()
        {
            if (_live == null) return WriteJsonAsync(new LineXLiveSnapshotDto { MonitoreoActivo = false });
            return WriteJsonAsync(_live.GetSnapshot());
        }

        // Publica config persistente al firmware en agp/linex/{uid}/config.
        // Body crudo se reenvía tal cual — la UI arma el objeto canónico que
        // entiende el firmware: { mdl:{section_count,pwm_freq,output_enable_pin,
        // comm_timeout_ms}, sections:[{idx,backend,channel,open_angle,close_angle,
        // min_us,max_us,travel_ms,failsafe_open,invert}] }.
        [Route(HttpVerbs.Post, "/linex/{uid}/config-push")]
        public async Task PushConfig(string uid)
        {
            if (_nodos == null) { await WriteJsonAsync(new { ok = false, error = "mqtt-unavailable" }); return; }
            if (string.IsNullOrWhiteSpace(uid)) { await WriteJsonAsync(new { ok = false, error = "uid-required" }); return; }

            string body = await ReadBodyAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }

            string topic = "agp/linex/" + uid + "/config";
            bool ok = await _nodos.PublishAsync(topic, body, false).ConfigureAwait(false);
            await WriteJsonAsync(new { ok, topic });
        }

        // Prueba manual de un surco en agp/linex/{uid}/test.
        // Body canónico: { "ch": <idx>, "angle": <grados> }  (servo, ángulo directo)
        //            o:  { "ch": <idx>, "state": "open"|"close" }
        [Route(HttpVerbs.Post, "/linex/{uid}/test")]
        public async Task SendTest(string uid)
        {
            if (_nodos == null) { await WriteJsonAsync(new { ok = false, error = "mqtt-unavailable" }); return; }
            if (string.IsNullOrWhiteSpace(uid)) { await WriteJsonAsync(new { ok = false, error = "uid-required" }); return; }

            string body = await ReadBodyAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }

            string topic = "agp/linex/" + uid + "/test";
            bool ok = await _nodos.PublishAsync(topic, body, false).ConfigureAwait(false);
            await WriteJsonAsync(new { ok, topic });
        }

        // Comando al nodo en agp/linex/{uid}/cmd. El firmware discrimina por el
        // campo "cmd" del payload: ping | get_config | reboot | clear_safe_mode | ota.
        // Body crudo (JSON con {cmd:…}) se reenvía tal cual.
        [Route(HttpVerbs.Post, "/linex/{uid}/cmd")]
        public async Task SendCmd(string uid, [QueryField] bool retain)
        {
            if (_nodos == null) { await WriteJsonAsync(new { ok = false, error = "mqtt-unavailable" }); return; }
            if (string.IsNullOrWhiteSpace(uid)) { await WriteJsonAsync(new { ok = false, error = "uid-required" }); return; }

            string body = await ReadBodyAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) body = "{}";

            string topic = "agp/linex/" + uid + "/cmd";
            bool ok = await _nodos.PublishAsync(topic, body, retain).ConfigureAwait(false);
            await WriteJsonAsync(new { ok, topic });
        }
    }
}
