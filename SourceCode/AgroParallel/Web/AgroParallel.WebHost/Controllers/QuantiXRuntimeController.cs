// ============================================================================
// QuantiXRuntimeController.cs
// Endpoint REST: GET /api/quantix/runtime
// Devuelve runtime + techo operativo por motor (pps, rpm, dosis máxima a la
// velocidad y ancho actual, curva 5/7/10/12/15 km/h).
// Esta es la única fuente de "dosis máxima posible" para la UI — antes la
// calculaba JS replicando la fórmula del bridge; ahora se sirve desde C#.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO.Routing;
using EmbedIO;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuantiXRuntimeController : AgpControllerBase
    {
        private readonly IQuantiXRuntimeService _runtime;

        public QuantiXRuntimeController(IQuantiXRuntimeService runtime)
        {
            _runtime = runtime;
        }

        [Route(HttpVerbs.Get, "/quantix/runtime")]
        public Task GetRuntime()
        {
            var snap = _runtime != null ? _runtime.GetSnapshot() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }
    }
}
