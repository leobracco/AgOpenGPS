// ============================================================================
// ConfigVehiculoController.cs — réplica HTML de FormConfig (pages/config.html).
//   GET  /api/aog/config                     → { ok, ...snapshot completo }
//   POST /api/aog/config/{seccion}          body ConfigGuardarBody → { ok, error }
//   POST /api/aog/config/rolido/accion      { accion } → { ok, error, roll_zero, imu_roll, imu_present }
//   POST /api/aog/config/secciones/preparar → { ok, error }  (apaga masters con lote abierto)
// Secciones: vehiculo, dimensiones, antena, enganche_estilo, enganche_dist,
// offset_implemento, pivote, timing, secciones, switches, relay, maquina,
// rumbo, rolido, uturn, tram, display, botones.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ConfigVehiculoController : AgpControllerBase
    {
        private readonly IConfigVehiculoService _svc;

        public ConfigVehiculoController(IConfigVehiculoService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/aog/config")]
        public Task GetConfig()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var snap = _svc.GetSnapshot();
            return WriteJsonAsync(new
            {
                ok = true,
                is_metric = snap.IsMetric,
                is_job_started = snap.IsJobStarted,
                perfil_activo = snap.PerfilActivo,
                vehiculo = snap.Vehiculo,
                dimensiones = snap.Dimensiones,
                antena = snap.Antena,
                enganche = snap.Enganche,
                offset = snap.Offset,
                timing = snap.Timing,
                secciones = snap.Secciones,
                switches = snap.Switches,
                relay = snap.Relay,
                maquina = snap.Maquina,
                rumbo = snap.Rumbo,
                rolido = snap.Rolido,
                uturn = snap.Uturn,
                tram = snap.Tram,
                display = snap.Display,
                botones = snap.Botones
            });
        }

        [Route(HttpVerbs.Post, "/aog/config/rolido/accion")]
        public async Task PostRolidoAccion()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            AccionBody body;
            try { body = await ReadJsonBodyAsync<AccionBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            var r = _svc.AccionRolido(body?.Accion);
            await WriteJsonAsync(new
            {
                ok = r.Ok,
                error = r.Error,
                roll_zero = r.RollZero,
                imu_roll = r.ImuRoll,
                imu_present = r.ImuPresent
            });
        }

        [Route(HttpVerbs.Post, "/aog/config/secciones/preparar")]
        public async Task PostPrepararSecciones()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            var r = _svc.PrepararSecciones();
            await WriteJsonAsync(new { ok = r.Ok, error = r.Error });
        }

        [Route(HttpVerbs.Post, "/aog/config/{seccion}")]
        public async Task PostGuardar(string seccion)
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            ConfigGuardarBody body;
            try { body = await ReadJsonBodyAsync<ConfigGuardarBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "empty-body" });
                return;
            }
            var r = _svc.Guardar(seccion, body);
            await WriteJsonAsync(new { ok = r.Ok, error = r.Error });
        }

        private sealed class AccionBody
        {
            public string Accion { get; set; }
        }
    }
}
