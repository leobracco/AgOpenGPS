// ============================================================================
// QuantiXController.cs
// Endpoints REST del módulo QuantiX:
//   GET  /api/quantix/live                        → telemetría live por nodo/motor
//   GET  /api/quantix/motores                     → quantiX_motores.json
//   PUT  /api/quantix/motores                     → persistir
//   GET  /api/quantix/udp                         → quantiX.json (UDP cfg)
//   PUT  /api/quantix/udp                         → persistir
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
using Swan.Formatters;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuantiXController : WebApiController
    {
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
        public object GetMotores()
        {
            if (_qx == null) return new { ok = false, error = "service-unavailable" };
            return new { ok = true, config = _qx.GetMotores() };
        }

        [Route(HttpVerbs.Put, "/quantix/motores")]
        public async Task<object> PutMotores()
        {
            if (_qx == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            QxMotoresConfigDto cfg;
            try { cfg = Json.Deserialize<QxMotoresConfigDto>(body); }
            catch (Exception ex) { return new { ok = false, error = "bad-json: " + ex.Message }; }
            if (cfg == null) return new { ok = false, error = "empty-body" };
            _qx.SaveMotores(cfg);
            return new { ok = true };
        }

        [Route(HttpVerbs.Get, "/quantix/udp")]
        public object GetUdp()
        {
            if (_qx == null) return new { ok = false, error = "service-unavailable" };
            return new { ok = true, config = _qx.GetUdp() };
        }

        [Route(HttpVerbs.Put, "/quantix/udp")]
        public async Task<object> PutUdp()
        {
            if (_qx == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            QxUdpConfigDto cfg;
            try { cfg = Json.Deserialize<QxUdpConfigDto>(body); }
            catch (Exception ex) { return new { ok = false, error = "bad-json: " + ex.Message }; }
            if (cfg == null) return new { ok = false, error = "empty-body" };
            _qx.SaveUdp(cfg);
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
    }
}
