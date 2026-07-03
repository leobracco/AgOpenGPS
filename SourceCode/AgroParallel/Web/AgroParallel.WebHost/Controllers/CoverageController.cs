// ============================================================================
// CoverageController.cs
// Endpoints REST del Core/view split — pintura de cobertura.
//   GET  /api/aog/coverage          → snapshot completo (triStrip → JSON)
//   POST /api/aog/coverage/reset    → limpia patchList de PilotX
// La UI HTML (piloto.html) pintar puede:
//   (a) seguir pintando su propio canvas trapezoidal (rápido, ya implementado);
//   (b) mirrorear este snapshot — útil cuando se abre un lote ya empezado.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CoverageController : AgpControllerBase
    {
        private readonly ICoverageService _coverage;

        public CoverageController(ICoverageService coverage)
        {
            _coverage = coverage;
        }

        [Route(HttpVerbs.Get, "/aog/coverage")]
        public Task GetCoverage()
        {
            var snap = _coverage != null ? _coverage.GetSnapshot() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }

        [Route(HttpVerbs.Post, "/aog/coverage/reset")]
        public Task Reset()
        {
            if (_coverage == null)
                return WriteErrorAsync(503, "service-unavailable", "Servicio de cobertura no disponible.");
            _coverage.Reset();
            return WriteJsonAsync(new { ok = true });
        }
    }
}
