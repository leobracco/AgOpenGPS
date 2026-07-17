// ============================================================================
// AogStateController.cs
// Endpoint REST: GET /api/aog/state         → snapshot JSON del estado PilotX.
//                GET /api/aog/all-settings  → volcado "Todos los ajustes"
//                                             (reemplazo de FormAllSettings).
//                GET /api/aog/eventos        → registro de eventos (histórico
//                                             en disco + sesión actual;
//                                             reemplazo de FormEventViewer).
//                GET /api/aog/graph-xte      → muestra en vivo (error de rumbo
//                                             + XTE) para el gráfico de guiado
//                                             (reemplazo de FormGraphXTE).
//                GET /api/aog/graph-heading  → muestra en vivo (rumbo GPS vs IMU
//                                             corregido) para el gráfico de rumbo
//                                             (reemplazo de FormGraphHeading).
//                GET /api/aog/graph-steer    → muestra en vivo (dirección real vs
//                                             seteada) para el gráfico de dirección
//                                             (reemplazo de FormGraphSteer).
//                GET /api/aog/shift-pos      → corrimiento de deriva GPS (norte/
//                                             este cm + offsets on) para la
//                                             pantalla de corregir posición
//                                             (reemplazo de FormShiftPos; las
//                                             escrituras van por POST
//                                             /api/aog/guidance/command).
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

        [Route(HttpVerbs.Get, "/aog/all-settings")]
        public Task GetAllSettings()
        {
            return WriteJsonAsync(_state.GetAllSettings());
        }

        [Route(HttpVerbs.Get, "/aog/eventos")]
        public Task GetEventLog()
        {
            return WriteJsonAsync(_state.GetEventLog());
        }

        [Route(HttpVerbs.Get, "/aog/graph-xte")]
        public Task GetXteGraph()
        {
            return WriteJsonAsync(_state.GetXteGraphSample());
        }

        [Route(HttpVerbs.Get, "/aog/graph-heading")]
        public Task GetHeadingGraph()
        {
            return WriteJsonAsync(_state.GetHeadingGraphSample());
        }

        [Route(HttpVerbs.Get, "/aog/graph-steer")]
        public Task GetSteerGraph()
        {
            return WriteJsonAsync(_state.GetSteerGraphSample());
        }

        [Route(HttpVerbs.Get, "/aog/shift-pos")]
        public Task GetShiftPos()
        {
            return WriteJsonAsync(_state.GetShiftPos());
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
