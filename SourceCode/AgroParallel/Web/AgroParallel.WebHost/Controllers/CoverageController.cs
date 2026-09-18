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
        public Task GetCoverage([QueryField] string cursor)
        {
            // Con cursor ("j:p:v;...") la respuesta es INCREMENTAL: solo lo
            // pintado desde entonces — pintado fluido a 3-5 Hz sin pagar el
            // snapshot completo (MBs en jornadas largas) en cada poll.
            var snap = _coverage != null
                ? (string.IsNullOrEmpty(cursor) ? _coverage.GetSnapshot() : _coverage.GetSnapshot(cursor))
                : null;
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
