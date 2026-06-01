// ============================================================================
// TramController.cs
// REST endpoint para tramlines (wheel tracks) + outer/inner boundary. Stage
// 4b de la migracion OpenGL del mapa PilotX.Desktop:
//   GET /api/aog/tram
//      { ok: true, snapshot: { displayMode, lines: [...], outerBoundary,
//                              innerBoundary, revision } }
//
// Cadencia esperada: 1 Hz (igual que guidance) - tram solo cambia al
// regenerar. Usa revision para que el cliente saltee re-upload del VBO.
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
    public sealed class TramController : WebApiController
    {
        private readonly ITramCalculator _tram;

        public TramController(ITramCalculator tram)
        {
            _tram = tram;
        }

        [Route(HttpVerbs.Get, "/aog/tram")]
        public async Task GetTram()
        {
            var snap = _tram != null ? _tram.GetGeometry() : null;
            string json = SysJson.Serialize(new { ok = true, snapshot = snap }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
