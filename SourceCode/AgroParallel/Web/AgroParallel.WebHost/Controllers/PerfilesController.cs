// ============================================================================
// PerfilesController.cs — gestión de perfiles de vehículo para pages/perfiles.html.
//   GET  /api/aog/perfiles                → { ok, activo, is_job_started, perfiles[] }
//   POST /api/aog/perfiles/cargar        { nombre }
//   POST /api/aog/perfiles/nuevo         { nombre, desde }   (desde vacío = perfil en blanco)
//   POST /api/aog/perfiles/copiar        { origen, nuevo }   (duplica XML, no activa)
//   POST /api/aog/perfiles/borrar        { nombre, clave }   (clave si está protegido)
//   POST /api/aog/perfiles/proteger      { nombre, clave }
//   POST /api/aog/perfiles/desproteger   { nombre, clave }
// El backup general sigue en POST /api/config/backup (ConfiguracionController).
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class PerfilesController : AgpControllerBase
    {
        private readonly IPerfilVehiculoService _svc;

        public PerfilesController(IPerfilVehiculoService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/aog/perfiles")]
        public Task GetPerfiles()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var snap = _svc.GetSnapshot();
            return WriteJsonAsync(new
            {
                ok = true,
                activo = snap.Activo,
                is_job_started = snap.IsJobStarted,
                perfiles = snap.Perfiles
            });
        }

        [Route(HttpVerbs.Post, "/aog/perfiles/cargar")]
        public Task PostCargar() { return Accion(b => _svc.Cargar(b.Nombre)); }

        [Route(HttpVerbs.Post, "/aog/perfiles/nuevo")]
        public Task PostNuevo() { return Accion(b => _svc.Nuevo(b.Nombre, b.Desde)); }

        [Route(HttpVerbs.Post, "/aog/perfiles/copiar")]
        public Task PostCopiar() { return Accion(b => _svc.Copiar(b.Origen, b.Nuevo)); }

        [Route(HttpVerbs.Post, "/aog/perfiles/borrar")]
        public Task PostBorrar() { return Accion(b => _svc.Borrar(b.Nombre, b.Clave)); }

        [Route(HttpVerbs.Post, "/aog/perfiles/proteger")]
        public Task PostProteger() { return Accion(b => _svc.Proteger(b.Nombre, b.Clave)); }

        [Route(HttpVerbs.Post, "/aog/perfiles/desproteger")]
        public Task PostDesproteger() { return Accion(b => _svc.Desproteger(b.Nombre, b.Clave)); }

        private async Task Accion(System.Func<PerfilBody, PerfilResultDto> fn)
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            PerfilBody body;
            try { body = await ReadJsonBodyAsync<PerfilBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "empty-body" });
                return;
            }
            var r = fn(body);
            await WriteJsonAsync(new { ok = r.Ok, error = r.Error });
        }

        private sealed class PerfilBody
        {
            public string Nombre { get; set; }
            public string Desde { get; set; }
            public string Origen { get; set; }
            public string Nuevo { get; set; }
            public string Clave { get; set; }
        }
    }
}
