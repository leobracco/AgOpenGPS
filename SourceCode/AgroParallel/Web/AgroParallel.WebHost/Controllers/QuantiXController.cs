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
using System.IO;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SysJson = System.Text.Json.JsonSerializer;
using System.Text.Json;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuantiXController : WebApiController
    {
        // Usamos System.Text.Json (no Swan): los DTOs tienen [JsonPropertyName("snake_case")]
        // que Swan no honra. Con Swan, los campos como dosis_fija/kp/ki/kd quedaban en default
        // y al guardar se "borraba" la config del motor.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly INodoRegistryService _registry;
        private readonly IQuantiXConfigService _qx;

        public QuantiXController(INodoRegistryService registry, IQuantiXConfigService qx)
        {
            _registry = registry;
            _qx = qx;
        }

        [Route(HttpVerbs.Get, "/quantix/live")]
        public object GetLive()
        {
            if (_registry == null)
                return new { ok = false, count = 0, nodos = new List<NodoStatus>(), error = "service-unavailable" };

            var all = _registry.GetAll();
            var qx = new List<NodoStatus>();
            for (int i = 0; i < all.Count; i++)
            {
                var n = all[i];
                if (n.Type != null && n.Type.IndexOf("quantix", StringComparison.OrdinalIgnoreCase) >= 0)
                    qx.Add(n);
            }
            return new { ok = true, count = qx.Count, nodos = qx };
        }

        [Route(HttpVerbs.Get, "/quantix/motores")]
        public async Task GetMotores()
        {
            // Serializamos manualmente con System.Text.Json: el ResponseSerializer
            // default de EmbedIO (Swan) ignora [JsonPropertyName] y emite PascalCase
            // (Nodos/Motores/Kp/...), pero la UI espera snake_case (espejo del JSON
            // en disco). Sin esto, loadMotores() recibía Nodos en mayúscula y
            // renderMotores() veía nodos=undefined → "No hay nodos QuantiX configurados".
            string json = _qx == null
                ? SysJson.Serialize(new { ok = false, error = "service-unavailable" })
                : SysJson.Serialize(new { ok = true, config = _qx.GetMotores() });
            await HttpContext.SendStringAsync(json, "application/json", System.Text.Encoding.UTF8).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Put, "/quantix/motores")]
        public async Task<object> PutMotores()
        {
            if (_qx == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            QxMotoresConfigDto cfg;
            try { cfg = SysJson.Deserialize<QxMotoresConfigDto>(body, JsonOpts); }
            catch (Exception ex) { return new { ok = false, error = "bad-json: " + ex.Message }; }
            if (cfg == null) return new { ok = false, error = "empty-body" };
            _qx.SaveMotores(cfg);
            return new { ok = true };
        }

        [Route(HttpVerbs.Post, "/quantix/{uid}/send")]
        public async Task<object> SendConfig(string uid)
        {
            if (_qx == null) return new { ok = false, error = "service-unavailable" };
            bool ok = await _qx.SendNodoConfigAsync(uid).ConfigureAwait(false);
            return new { ok, topic = "agp/quantix/" + uid + "/config" };
        }

        [Route(HttpVerbs.Post, "/quantix/{uid}/cmd")]
        public async Task<object> SendCmd(string uid, [QueryField] string verb, [QueryField] bool retain)
        {
            if (_qx == null) return new { ok = false, error = "service-unavailable" };
            if (string.IsNullOrWhiteSpace(verb))
                return new { ok = false, error = "verb-required" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            bool ok = await _qx.SendCmdAsync(uid, verb, body, retain).ConfigureAwait(false);
            return new { ok, topic = "agp/quantix/" + uid + "/" + verb };
        }

        // Devuelve el último resultado de auto-tune recibido para el nodo.
        // La UI lo poolea después de disparar autotune_start hasta que el
        // timestamp supera el momento de inicio (o se agota el timeout).
        [Route(HttpVerbs.Get, "/quantix/{uid}/autotune")]
        public async Task GetAutoTune(string uid)
        {
            object payload;
            if (_qx == null)
            {
                payload = new { ok = false, error = "service-unavailable" };
            }
            else
            {
                var r = _qx.GetAutoTuneResult(uid);
                payload = r == null
                    ? new { ok = true, hasResult = false }
                    : (object)new
                    {
                        ok = true,
                        hasResult = true,
                        result = new
                        {
                            uid = r.Uid,
                            motorId = r.MotorId,
                            ok = r.Ok,
                            kp = r.Kp,
                            ki = r.Ki,
                            kd = r.Kd,
                            receivedUtc = r.ReceivedUtc.ToString("o")
                        }
                    };
            }
            string json = SysJson.Serialize(payload);
            await HttpContext.SendStringAsync(json, "application/json", System.Text.Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
