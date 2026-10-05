// ============================================================================
// GuidanceController.cs
// Endpoint REST: GET /api/aog/guidance
//   { mode, is_line_set, is_auto_steer_on, xte_meters, heading_error_rad,
//     steer_angle_command_deg, distance_to_end_m, look_ahead }
// El view ahora puede mostrar el barómetro de XTE y el botón de autosteer
// sin tener que consultar 4 endpoints distintos.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.Control;
using AgroParallel.WebHost.Autoridad;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class GuidanceController : AgpControllerBase
    {
        private readonly IGuidanceCalculator _guidance;
        // Autoridad de control (null = sin puerta, todo pasa como antes).
        private readonly PuertaControl _puerta;

        public GuidanceController(IGuidanceCalculator guidance, PuertaControl puerta = null)
        {
            _guidance = guidance;
            _puerta = puerta;
        }

        [Route(HttpVerbs.Get, "/aog/guidance")]
        public Task GetGuidance()
        {
            var snap = _guidance != null ? _guidance.GetSnapshot() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }

        // Geometria de la linea/curva activa. Endpoint separado de /aog/guidance
        // porque cambia de cadencia: el control state se polea ~4 Hz, pero la
        // geometria solo cambia al redefinir la linea (1 Hz alcanza). El cliente
        // usa snapshot.revision para saltar re-uploads de VBO.
        [Route(HttpVerbs.Get, "/aog/guidance/geometry")]
        public Task GetGuidanceGeometry()
        {
            var snap = _guidance != null ? _guidance.GetGeometry() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }

        // Comando de guiado desde la barra rápida web (pages/guia-rapida.html):
        //   POST /api/aog/guidance/command  { "cmd": "center|nudge_left|nudge_right|contour|build|pick" }
        // Dispara el Click del botón nativo correspondiente en el hilo UI de PilotX.
        [Route(HttpVerbs.Post, "/aog/guidance/command")]
        public async Task PostGuidanceCommand()
        {
            if (_guidance == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            CommandBody body;
            try { body = await ReadJsonBodyAsync<CommandBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null || string.IsNullOrWhiteSpace(body.Cmd))
            {
                await WriteJsonAsync(new { ok = false, error = "empty-cmd" });
                return;
            }

            // Autoridad de control: el comando decide el nivel ("autosteer" con
            // el piloto puesto es desenganchar → pasa siempre). Sin control, en
            // modo Exigir se rechaza acá con 423; en SoloRegistro se anota y sigue.
            if (_puerta != null)
            {
                bool pilotoOn = false;
                try { pilotoOn = _guidance.GetSnapshot()?.IsAutoSteerOn ?? false; } catch { }
                var nivel = ClasificadorComandos.ClasificarComandoGuiado(body.Cmd, pilotoOn);
                bool direccion = ClasificadorComandos.EsComandoDeDireccion(body.Cmd, pilotoOn);
                var d = _puerta.Autorizar(ClienteHttp.De(HttpContext), nivel, "cmd " + body.Cmd.Trim(), direccion);
                if (!d.Permitido)
                {
                    HttpContext.Response.StatusCode = d.StatusHttp;
                    await WriteJsonAsync(new { ok = false, cmd = body.Cmd, error = "sin-control", mensaje = d.Motivo });
                    return;
                }
            }

            bool ok = _guidance.ExecuteCommand(body.Cmd);
            await WriteJsonAsync(new { ok, cmd = body.Cmd });
        }

        private sealed class CommandBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("cmd")]
            public string Cmd { get; set; }
        }
    }
}
