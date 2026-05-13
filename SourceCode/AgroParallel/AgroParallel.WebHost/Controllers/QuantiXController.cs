// ============================================================================
// QuantiXController.cs
// Endpoints REST del módulo QuantiX:
//   GET /api/quantix/live → [{ uid, ip, firmware, online, lastSeenUtc, motors: [{id, ppsTarget, ppsReal, pwm, rpm, pulsos, lastSeenUtc}] }]
//
// La data viene del registry MQTT (NodoRegistryService) que ya está suscrito a
// agp/quantix/+/status_live y populando NodoStatus.MotorsLive. Por eso este
// controller no abre su propio cliente MQTT — sólo filtra los nodos QuantiX.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuantiXController : WebApiController
    {
        private readonly INodoRegistryService _registry;

        public QuantiXController(INodoRegistryService registry)
        {
            _registry = registry;
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
    }
}
