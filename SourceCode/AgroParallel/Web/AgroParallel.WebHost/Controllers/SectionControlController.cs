// ============================================================================
// SectionControlController.cs
// Endpoint REST: GET /api/aog/sections → estado de las secciones (sectionOnRequest).
// La lógica de DECISIÓN sigue viva en Forms/Position*.cs por ahora;
// el view solo lee. Cuando el Core se migre, este controller deja de tocar PilotX.
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
    public sealed class SectionControlController : WebApiController
    {
        private readonly ISectionControlService _sections;

        public SectionControlController(ISectionControlService sections)
        {
            _sections = sections;
        }

        [Route(HttpVerbs.Get, "/aog/sections")]
        public async Task GetSections()
        {
            var snap = _sections != null ? _sections.GetSnapshot() : null;
            string json = SysJson.Serialize(new { ok = true, snapshot = snap }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
