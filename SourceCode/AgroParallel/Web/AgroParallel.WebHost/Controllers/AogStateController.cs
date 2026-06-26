// ============================================================================
// AogStateController.cs
// Endpoint REST: GET /api/aog/state         → snapshot JSON del estado PilotX.
//                GET /api/aog/shape         → polígonos del shapefile activo.
//                GET /api/aog/shape-fields  → columnas DBF del shapefile activo
//                                             (la UI QuantiX las usa para el
//                                             dropdown CampoDosis).
// Punto de entrada para la UI HTML cuando no se quiere suscribir al WS.
// ============================================================================

using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SysJson = System.Text.Json.JsonSerializer;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class AogStateController : WebApiController
    {
        private readonly IAogStateProvider _state;

        public AogStateController(IAogStateProvider state)
        {
            _state = state;
        }

        [Route(HttpVerbs.Get, "/aog/state")]
        public AogStateSnapshot GetState()
        {
            return _state.GetSnapshot();
        }

        [Route(HttpVerbs.Get, "/aog/shape")]
        public ShapeSnapshot GetShape()
        {
            return _state.GetShape();
        }

        [Route(HttpVerbs.Get, "/aog/shape-fields")]
        public async Task GetShapeFields()
        {
            // System.Text.Json directo: el ResponseSerializer default (Swan) emite
            // PascalCase y la UI espera camelCase (sourceToken/fields/name/numeric).
            var data = _state.GetShapeFields() ?? new ShapeFieldsSnapshot();
            string json = SysJson.Serialize(new
            {
                ok = true,
                sourceToken = data.SourceToken ?? string.Empty,
                fields = data.Fields
            }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
