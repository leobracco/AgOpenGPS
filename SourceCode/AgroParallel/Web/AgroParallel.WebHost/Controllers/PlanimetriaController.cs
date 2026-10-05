// ============================================================================
// PlanimetriaController.cs — planimetría fase 3 en la cabina.
//
//   GET  /api/planimetria              → estado + estadísticas + config
//   GET  /api/planimetria/capa         → capa del mapa (plano local del lote)
//   POST /api/planimetria/config       → cambios parciales (PlanimetriaConfigRequest)
//   POST /api/planimetria/calcular     → recalcula en segundo plano
//   POST /api/planimetria/guia         → {"cota_m":101.25|null, "nombre":""}
//   POST /api/planimetria/prescripcion → escribe y activa la prescripción por ambientes
//
// Solo se registra si el host inyecta IPlanimetriaService (hoy: el motor
// headless). Sin servicio: ok=false service-unavailable. Archivo nuevo, aditivo.
// ============================================================================

using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using System;
using System.Threading.Tasks;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class PlanimetriaController : AgpControllerBase
    {
        private readonly IPlanimetriaService _svc;

        public PlanimetriaController(IPlanimetriaService svc)
        {
            _svc = svc;
        }

        private Task Estado(Func<PlanimetriaEstadoDto> accion)
        {
            if (_svc == null) return WriteJsonAsync(new PlanimetriaEstadoDto { Ok = false, Error = "service-unavailable" });
            try { return WriteJsonAsync(accion()); }
            catch (Exception ex) { return WriteJsonAsync(new PlanimetriaEstadoDto { Ok = false, Error = ex.Message }); }
        }

        private Task Accion(Func<PlanimetriaAccionDto> accion)
        {
            if (_svc == null) return WriteJsonAsync(new PlanimetriaAccionDto { Ok = false, Error = "service-unavailable" });
            try { return WriteJsonAsync(accion()); }
            catch (Exception ex) { return WriteJsonAsync(new PlanimetriaAccionDto { Ok = false, Error = ex.Message }); }
        }

        [Route(HttpVerbs.Get, "/planimetria")]
        public Task Get() => Estado(() => _svc.Estado());

        [Route(HttpVerbs.Get, "/planimetria/capa")]
        public Task GetCapa()
        {
            if (_svc == null) return WriteJsonAsync(new PlanimetriaCapaDto { Ok = false, Error = "service-unavailable" });
            try { return WriteJsonAsync(_svc.Capa()); }
            catch (Exception ex) { return WriteJsonAsync(new PlanimetriaCapaDto { Ok = false, Error = ex.Message }); }
        }

        [Route(HttpVerbs.Post, "/planimetria/config")]
        public async Task Config()
        {
            PlanimetriaConfigRequest req;
            try { req = await ReadJsonBodyAsync<PlanimetriaConfigRequest>().ConfigureAwait(false); }
            catch { req = null; }
            if (req == null)
            {
                await WriteJsonAsync(new PlanimetriaEstadoDto { Ok = false, Error = "bad-json" }).ConfigureAwait(false);
                return;
            }
            await Estado(() => _svc.Configurar(req)).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/planimetria/calcular")]
        public Task Calcular() => Estado(() => _svc.Calcular());

        [Route(HttpVerbs.Post, "/planimetria/guia")]
        public async Task Guia()
        {
            PlanimetriaGuiaRequest req;
            try { req = await ReadJsonBodyAsync<PlanimetriaGuiaRequest>().ConfigureAwait(false); }
            catch { req = null; }
            await Accion(() => _svc.CrearGuia(req ?? new PlanimetriaGuiaRequest())).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/planimetria/prescripcion")]
        public Task Prescripcion() => Accion(() => _svc.CrearPrescripcion());
    }
}
