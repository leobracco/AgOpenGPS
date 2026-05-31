// ============================================================================
// QuantiXRuntimeController.cs
// Endpoint REST: GET /api/quantix/runtime
// Devuelve runtime + techo operativo por motor (pps, rpm, dosis máxima a la
// velocidad y ancho actual, curva 5/7/10/12/15 km/h).
// Esta es la única fuente de "dosis máxima posible" para la UI — antes la
// calculaba JS replicando la fórmula del bridge; ahora se sirve desde C#.
// ============================================================================

using System.Text;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SysJson = System.Text.Json.JsonSerializer;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuantiXRuntimeController : WebApiController
    {
        private readonly IQuantiXRuntimeService _runtime;

        public QuantiXRuntimeController(IQuantiXRuntimeService runtime)
        {
            _runtime = runtime;
        }

        [Route(HttpVerbs.Get, "/quantix/runtime")]
        public async Task GetRuntime()
        {
            var snap = _runtime != null ? _runtime.GetSnapshot() : null;
            string json = SysJson.Serialize(new { ok = true, snapshot = snap }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
