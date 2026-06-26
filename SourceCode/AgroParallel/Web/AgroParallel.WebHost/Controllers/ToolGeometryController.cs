// ============================================================================
// ToolGeometryController.cs
// REST endpoint para la barra del implemento en runtime (Stage 4a migracion
// OpenGL del mapa PilotX.Desktop):
//   GET /api/aog/tool/geometry
//      { ok: true, snapshot: { numSections, isValid, sections: [...] } }
//
// Cadencia esperada: 4 Hz (igual que el HUD). Cada seccion trae sus puntos
// leftPoint/rightPoint en coords mundo + estado vivo (isOn/isMapping/btn).
// No usa revision-cache: los puntos cambian cada frame que el tractor se
// mueve, asi que el cliente re-uploadea el VBO en cada poll.
//
// Esto NO reemplaza a VehicleToolController (que persiste la config del
// implemento). Aca solo emitimos runtime — el config CRUD vive aparte.
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
    public sealed class ToolGeometryController : WebApiController
    {
        private readonly IToolGeometryCalculator _tool;

        public ToolGeometryController(IToolGeometryCalculator tool)
        {
            _tool = tool;
        }

        [Route(HttpVerbs.Get, "/aog/tool/geometry")]
        public async Task GetToolGeometry()
        {
            var snap = _tool != null ? _tool.GetGeometry() : null;
            string json = SysJson.Serialize(new { ok = true, snapshot = snap }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
