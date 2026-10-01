// ============================================================================
// SteerCalController.cs — ASISTENTE DE CALIBRACIÓN DE LA DIRECCIÓN.
//
//   GET  /api/steer/cal           → estado (y latido de la pantalla)
//   POST /api/steer/cal/iniciar   → arranca con la config actual
//   POST /api/steer/cal/accion    → {"accion":"empezar|siguiente|saltar|repetir|
//                                    aceptar|rechazar|cancelar|aplicar|deshacer"}
//   POST /api/steer/cal/latido    → {"apretado":true|false} (hombre muerto)
//
// Solo se registra si el host inyecta ISteerCalService (hoy: el motor
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
    public sealed class SteerCalController : AgpControllerBase
    {
        private readonly ISteerCalService _svc;

        public SteerCalController(ISteerCalService svc)
        {
            _svc = svc;
        }

        private Task Responder(Func<SteerCalEstadoDto> accion)
        {
            if (_svc == null)
                return WriteJsonAsync(new SteerCalEstadoDto { Ok = false, Error = "service-unavailable", Paso = "inactivo" });
            try { return WriteJsonAsync(accion()); }
            catch (Exception ex)
            {
                return WriteJsonAsync(new SteerCalEstadoDto { Ok = false, Error = ex.Message, Paso = "inactivo" });
            }
        }

        [Route(HttpVerbs.Get, "/steer/cal")]
        public Task Get() => Responder(() => _svc.Estado());

        [Route(HttpVerbs.Post, "/steer/cal/iniciar")]
        public Task Iniciar() => Responder(() => _svc.Iniciar());

        [Route(HttpVerbs.Post, "/steer/cal/accion")]
        public async Task Accion()
        {
            AccionRequest req;
            try { req = await ReadJsonBodyAsync<AccionRequest>().ConfigureAwait(false); }
            catch { req = null; }
            if (req == null || string.IsNullOrEmpty(req.Accion))
            {
                await WriteJsonAsync(new SteerCalEstadoDto { Ok = false, Error = "bad-json" }).ConfigureAwait(false);
                return;
            }
            await Responder(() => _svc.Accion(req.Accion)).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/steer/cal/latido")]
        public async Task Latido()
        {
            LatidoRequest req;
            try { req = await ReadJsonBodyAsync<LatidoRequest>().ConfigureAwait(false); }
            catch { req = null; }
            // Body roto = soltado: el hombre muerto falla cerrado.
            bool apretado = req != null && req.Apretado;
            await Responder(() => _svc.Latido(apretado)).ConfigureAwait(false);
        }

        private sealed class AccionRequest
        {
            [JsonPropertyName("accion")] public string Accion { get; set; }
        }

        private sealed class LatidoRequest
        {
            [JsonPropertyName("apretado")] public bool Apretado { get; set; }
        }
    }
}
