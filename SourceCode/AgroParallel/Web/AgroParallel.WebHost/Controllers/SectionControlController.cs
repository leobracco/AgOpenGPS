// ============================================================================
// SectionControlController.cs
// Endpoint REST: GET /api/aog/sections → estado de las secciones (sectionOnRequest).
// La lógica de DECISIÓN sigue viva en Forms/Position*.cs por ahora;
// el view solo lee. Cuando el Core se migre, este controller deja de tocar PilotX.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SectionControlController : AgpControllerBase
    {
        private readonly ISectionControlService _sections;

        public SectionControlController(ISectionControlService sections)
        {
            _sections = sections;
        }

        [Route(HttpVerbs.Get, "/aog/sections")]
        public Task GetSections()
        {
            var snap = _sections != null ? _sections.GetSnapshot() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }
    }
}
