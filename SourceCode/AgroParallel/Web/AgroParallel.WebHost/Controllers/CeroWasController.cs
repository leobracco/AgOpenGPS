// ============================================================================
// CeroWasController.cs — CERO AUTOMÁTICO DEL WAS (propone, no aplica solo).
//
//   GET  /api/steer/cero-was-auto            → estado + propuesta
//   POST /api/steer/cero-was-auto/activar    → {"on":true|false}
//   POST /api/steer/cero-was-auto/aplicar    → aplica la propuesta (piloto suelto)
//   POST /api/steer/cero-was-auto/deshacer   → vuelve al offset previo
//   POST /api/steer/cero-was-auto/reiniciar  → descarta lo medido
//
// Solo se registra si el host inyecta ICeroWasService (hoy: el motor
// headless). Sin servicio no se inventa nada: ok=false service-unavailable.
// Archivo nuevo, aditivo.
// ============================================================================

using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CeroWasController : AgpControllerBase
    {
        private readonly ICeroWasService _svc;

        public CeroWasController(ICeroWasService svc)
        {
            _svc = svc;
        }

        private Task Responder(Func<CeroWasEstadoDto> accion)
        {
            if (_svc == null)
                return WriteJsonAsync(new CeroWasEstadoDto { Ok = false, Error = "service-unavailable", Estado = "apagado" });
            try { return WriteJsonAsync(accion()); }
            catch (Exception ex)
            {
                return WriteJsonAsync(new CeroWasEstadoDto { Ok = false, Error = ex.Message, Estado = "apagado" });
            }
        }

        [Route(HttpVerbs.Get, "/steer/cero-was-auto")]
        public Task Get() => Responder(() => _svc.Estado());

        [Route(HttpVerbs.Post, "/steer/cero-was-auto/activar")]
        public async Task Activar()
        {
            ActivarRequest req;
            try { req = await ReadJsonBodyAsync<ActivarRequest>().ConfigureAwait(false); }
            catch { req = null; }
            if (req == null)
            {
                await WriteJsonAsync(new CeroWasEstadoDto { Ok = false, Error = "bad-json" }).ConfigureAwait(false);
                return;
            }
            await Responder(() => _svc.Activar(req.On)).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/steer/cero-was-auto/aplicar")]
        public Task Aplicar() => Responder(() => _svc.Aplicar());

        [Route(HttpVerbs.Post, "/steer/cero-was-auto/deshacer")]
        public Task Deshacer() => Responder(() => _svc.Deshacer());

        [Route(HttpVerbs.Post, "/steer/cero-was-auto/reiniciar")]
        public Task Reiniciar() => Responder(() => _svc.Reiniciar());

        private sealed class ActivarRequest
        {
            [JsonPropertyName("on")] public bool On { get; set; }
        }
    }
}
