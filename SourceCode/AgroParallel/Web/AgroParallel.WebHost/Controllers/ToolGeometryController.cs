// ============================================================================
// ToolGeometryController.cs
// REST endpoint para la barra del implemento en runtime (Stage 4a migracion
// OpenGL del mapa PilotX.Desktop):
//   GET /api/aog/tool/geometry
//      { ok: true, snapshot: { num_sections, is_valid, sections: [...] } }
//
// Cadencia esperada: 4 Hz (igual que el HUD). Cada seccion trae sus puntos
// left_e/left_n/right_e/right_n en coords mundo + estado vivo (is_on/is_mapping/btn_state).
// No usa revision-cache: los puntos cambian cada frame que el tractor se
// mueve, asi que el cliente re-uploadea el VBO en cada poll.
//
// Esto NO reemplaza a VehicleToolController (que persiste la config del
// implemento). Aca solo emitimos runtime — el config CRUD vive aparte.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ToolGeometryController : AgpControllerBase
    {
        private readonly IToolGeometryCalculator _tool;

        public ToolGeometryController(IToolGeometryCalculator tool)
        {
            _tool = tool;
        }

        [Route(HttpVerbs.Get, "/aog/tool/geometry")]
        public Task GetToolGeometry()
        {
            var snap = _tool != null ? _tool.GetGeometry() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }
    }
}
