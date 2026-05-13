// ============================================================================
// NodosController.cs
// Endpoints REST del módulo Nodos:
//   GET /api/nodos → [{ uid, type, ip, firmware, motors, uptime, lastSeenUtc, online }]
// Si no se inyectó INodoRegistryService, responde lista vacía con ok=false.
// ============================================================================

using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using System.Collections.Generic;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class NodosController : WebApiController
    {
        private readonly INodoRegistryService _registry;

        public NodosController(INodoRegistryService registry)
        {
            _registry = registry;
        }

        [Route(HttpVerbs.Get, "/nodos")]
        public object GetAll()
        {
            if (_registry == null)
                return new { ok = false, nodos = new List<NodoStatus>(), error = "service-unavailable" };
            var list = _registry.GetAll();
            return new { ok = true, count = list.Count, nodos = list };
        }
    }
}
