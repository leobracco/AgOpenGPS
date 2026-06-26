// ============================================================================
// CoverageController.cs
// Endpoints REST del Core/view split — pintura de cobertura.
//   GET  /api/aog/coverage          → snapshot completo (triStrip → JSON)
//   POST /api/aog/coverage/reset    → limpia patchList de PilotX
// La UI HTML (piloto.html) pintar puede:
//   (a) seguir pintando su propio canvas trapezoidal (rápido, ya implementado);
//   (b) mirrorear este snapshot — útil cuando se abre un lote ya empezado.
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
    public sealed class CoverageController : WebApiController
    {
        private readonly ICoverageService _coverage;

        public CoverageController(ICoverageService coverage)
        {
            _coverage = coverage;
        }

        [Route(HttpVerbs.Get, "/aog/coverage")]
        public async Task GetCoverage()
        {
            var snap = _coverage != null ? _coverage.GetSnapshot() : null;
            string json = SysJson.Serialize(new { ok = true, snapshot = snap }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/aog/coverage/reset")]
        public object Reset()
        {
            if (_coverage == null) return new { ok = false, error = "service-unavailable" };
            _coverage.Reset();
            return new { ok = true };
        }
    }
}
