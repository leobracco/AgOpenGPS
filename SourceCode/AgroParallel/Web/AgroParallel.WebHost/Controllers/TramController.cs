// ============================================================================
// TramController.cs
// REST endpoint para tramlines (wheel tracks) + outer/inner boundary. Stage
// 4b de la migracion OpenGL del mapa PilotX.Desktop:
//   GET /api/aog/tram
//      { ok: true, snapshot: { display_mode, lines: [...], outer_boundary,
//                              inner_boundary, revision } }
//
// Cadencia esperada: 1 Hz (igual que guidance) - tram solo cambia al
// regenerar. Usa revision para que el cliente saltee re-upload del VBO.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class TramController : AgpControllerBase
    {
        private readonly ITramCalculator _tram;

        public TramController(ITramCalculator tram)
        {
            _tram = tram;
        }

        [Route(HttpVerbs.Get, "/aog/tram")]
        public Task GetTram()
        {
            var snap = _tram != null ? _tram.GetGeometry() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }
    }
}
