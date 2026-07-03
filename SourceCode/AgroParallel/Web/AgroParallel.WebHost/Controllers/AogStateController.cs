// ============================================================================
// AogStateController.cs
// Endpoint REST: GET /api/aog/state         → snapshot JSON del estado PilotX.
//                GET /api/aog/shape         → polígonos del shapefile activo.
//                GET /api/aog/shape-fields  → columnas DBF del shapefile activo
//                                             (la UI QuantiX las usa para el
//                                             dropdown CampoDosis).
// Punto de entrada para la UI HTML cuando no se quiere suscribir al WS.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class AogStateController : AgpControllerBase
    {
        private readonly IAogStateProvider _state;

        public AogStateController(IAogStateProvider state)
        {
            _state = state;
        }

        [Route(HttpVerbs.Get, "/aog/state")]
        public Task GetState()
        {
            return WriteJsonAsync(_state.GetSnapshot());
        }

        [Route(HttpVerbs.Get, "/aog/shape")]
        public Task GetShape()
        {
            return WriteJsonAsync(_state.GetShape());
        }

        [Route(HttpVerbs.Get, "/aog/shape-fields")]
        public Task GetShapeFields()
        {
            var data = _state.GetShapeFields() ?? new ShapeFieldsSnapshot();
            return WriteJsonAsync(new
            {
                ok = true,
                source_token = data.SourceToken ?? string.Empty,
                fields = data.Fields
            });
        }
    }
}
