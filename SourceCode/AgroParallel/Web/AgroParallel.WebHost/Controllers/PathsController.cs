// ============================================================================
// PathsController.cs
// REST endpoint para caminos: youturn (giro de cabecera) + recorded path
// (camino grabado). Stage 5 de la migracion OpenGL del mapa PilotX.Desktop:
//   GET /api/aog/paths
//      { ok: true, snapshot: { you_turn: [...], recorded: [...], revision } }
//
// Cadencia esperada: 1 Hz (igual que tram) — solo cambia al generar un giro
// o grabar/cargar un camino. Usa revision para que el cliente saltee
// re-upload del VBO.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class PathsController : AgpControllerBase
    {
        private readonly IPathsGeometryCalculator _paths;

        public PathsController(IPathsGeometryCalculator paths)
        {
            _paths = paths;
        }

        [Route(HttpVerbs.Get, "/aog/paths")]
        public Task GetPaths()
        {
            var snap = _paths != null ? _paths.GetGeometry() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }
    }
}
