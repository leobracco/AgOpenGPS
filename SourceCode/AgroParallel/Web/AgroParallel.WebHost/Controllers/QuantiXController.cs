// ============================================================================
// QuantiXController.cs
// Endpoints REST del módulo QuantiX:
//   GET  /api/quantix/live                        → telemetría live por nodo/motor
//   GET  /api/quantix/motores                     → quantiX_motores.json
//   PUT  /api/quantix/motores                     → persistir
//   POST /api/quantix/{uid}/send                  → publica agp/quantix/{uid}/config
//   POST /api/quantix/{uid}/cmd?verb=...&retain=  → publica agp/quantix/{uid}/{verb}
//
// La telemetría live viene del registry MQTT (NodoRegistryService); las escrituras
// usan IQuantiXConfigService que publica reusando la conexión del registry.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuantiXController : AgpControllerBase
    {
        private readonly INodoRegistryService _registry;
        private readonly IQuantiXConfigService _qx;

        public QuantiXController(INodoRegistryService registry, IQuantiXConfigService qx)
        {
            _registry = registry;
            _qx = qx;
        }

        [Route(HttpVerbs.Get, "/quantix/live")]
        public Task GetLive()
        {
            if (_registry == null)
                return WriteJsonAsync(new { ok = false, count = 0, nodos = new List<NodoStatus>(), error = "service-unavailable" });

            var all = _registry.GetAll();
            var qx = new List<NodoStatus>();
            for (int i = 0; i < all.Count; i++)
            {
                var n = all[i];
                if (n.Type != null && n.Type.IndexOf("quantix", StringComparison.OrdinalIgnoreCase) >= 0)
                    qx.Add(n);
            }
            return WriteJsonAsync(new { ok = true, count = qx.Count, nodos = qx });
        }

        [Route(HttpVerbs.Get, "/quantix/motores")]
        public Task GetMotores()
        {
            if (_qx == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, config = _qx.GetMotores() });
        }

        [Route(HttpVerbs.Put, "/quantix/motores")]
        public async Task PutMotores()
        {
            if (_qx == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            QxMotoresConfigDto cfg;
            try { cfg = await ReadJsonBodyAsync<QxMotoresConfigDto>().ConfigureAwait(false); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (cfg == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            _qx.SaveMotores(cfg);
            await WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/quantix/{uid}/send")]
        public async Task SendConfig(string uid)
        {
            if (_qx == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            bool ok = await _qx.SendNodoConfigAsync(uid).ConfigureAwait(false);
            await WriteJsonAsync(new { ok, topic = "agp/quantix/" + uid + "/config" });
        }

        [Route(HttpVerbs.Post, "/quantix/{uid}/cmd")]
        public async Task SendCmd(string uid, [QueryField] string verb, [QueryField] bool retain)
        {
            if (_qx == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            if (string.IsNullOrWhiteSpace(verb))
            {
                await WriteJsonAsync(new { ok = false, error = "verb-required" });
                return;
            }
            // El payload al ESP32 se reenvía crudo: no re-serializar.
            string body = await ReadBodyAsync().ConfigureAwait(false);
            bool ok = await _qx.SendCmdAsync(uid, verb, body, retain).ConfigureAwait(false);
            await WriteJsonAsync(new { ok, topic = "agp/quantix/" + uid + "/" + verb });
        }

        // Devuelve el último resultado de auto-tune recibido para el nodo.
        // La UI lo poolea después de disparar autotune_start hasta que el
        // timestamp supera el momento de inicio (o se agota el timeout).
        [Route(HttpVerbs.Get, "/quantix/{uid}/autotune")]
        public Task GetAutoTune(string uid)
        {
            if (_qx == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });

            var r = _qx.GetAutoTuneResult(uid);
            if (r == null)
                return WriteJsonAsync(new { ok = true, has_result = false });

            return WriteJsonAsync(new
            {
                ok = true,
                has_result = true,
                result = new
                {
                    uid = r.Uid,
                    motor_id = r.MotorId,
                    ok = r.Ok,
                    kp = r.Kp,
                    ki = r.Ki,
                    kd = r.Kd,
                    received_utc = r.ReceivedUtc.ToString("o")
                }
            });
        }
    }
}
