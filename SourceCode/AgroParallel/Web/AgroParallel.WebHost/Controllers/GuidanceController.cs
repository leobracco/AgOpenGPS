// ============================================================================
// GuidanceController.cs
// Endpoint REST: GET /api/aog/guidance
//   { mode, isLineSet, isAutoSteerOn, xteMeters, headingErrorRad,
//     steerAngleCommandDeg, distanceToEndM, lookAhead }
// El view ahora puede mostrar el barómetro de XTE y el botón de autosteer
// sin tener que consultar 4 endpoints distintos.
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
    public sealed class GuidanceController : WebApiController
    {
        private readonly IGuidanceCalculator _guidance;

        public GuidanceController(IGuidanceCalculator guidance)
        {
            _guidance = guidance;
        }

        [Route(HttpVerbs.Get, "/aog/guidance")]
        public async Task GetGuidance()
        {
            var snap = _guidance != null ? _guidance.GetSnapshot() : null;
            string json = SysJson.Serialize(new { ok = true, snapshot = snap }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
