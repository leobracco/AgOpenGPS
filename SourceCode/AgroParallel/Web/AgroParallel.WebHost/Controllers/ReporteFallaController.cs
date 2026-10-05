// ============================================================================
// ReporteFallaController.cs — puente UI ↔ "Reportar falla" en un toque.
//
// La pantalla no habla con el cloud: le pasa al Engine lo que sólo ella tiene
// (la captura y sus propios logs) y el ReporteFallaService del Engine arma el
// ZIP con el resto, lo encola y lo sube a OrbitX.
//
//   POST /api/soporte/reporte          ← { descripcion, captura_png_b64, logs_pantalla:{nombre:texto} }
//                                      → { ok, codigo, estado, vinculado, bytes, omitidos[] }
//   GET  /api/soporte/reporte/estado?codigo=RF-XXX-XXX
//                                      → { ok, codigo, estado, vinculado, error }
//   POST /api/soporte/reporte/pendrive ← { codigo } → { ok, ruta, error }
//
// No hay endpoint para BAJAR el ZIP: el host escucha en la LAN y el reporte,
// aunque sanitizado, trae logs y lote del cliente.
//
// Sin service montado (host sin Engine, Android) → { ok:false,
// error:"service-unavailable" }, igual que ChatController.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Soporte;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ReporteFallaController : AgpControllerBase
    {
        // La captura en base64 de una pantalla de 1920x1080 ronda 2-3 MB; más
        // que esto no es una captura.
        private const int MaxCapturaBytes = 8 * 1024 * 1024;

        private readonly ReporteFallaService _svc;

        public ReporteFallaController(ReporteFallaService svc)
        {
            _svc = svc;
        }

        public sealed class CrearReq
        {
            public string descripcion { get; set; }
            public string captura_png_b64 { get; set; }
            public Dictionary<string, string> logs_pantalla { get; set; }
        }

        public sealed class CodigoReq
        {
            public string codigo { get; set; }
        }

        [Route(HttpVerbs.Post, "/soporte/reporte")]
        public async Task Crear()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }

            CrearReq req;
            try { req = await ReadJsonBodyAsync<CrearReq>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            if (req == null) req = new CrearReq();

            byte[] png = null;
            if (!string.IsNullOrEmpty(req.captura_png_b64))
            {
                try
                {
                    png = Convert.FromBase64String(req.captura_png_b64);
                    if (png.Length > MaxCapturaBytes) png = null;
                }
                catch (FormatException) { png = null; } // sin captura el reporte sale igual
            }

            // Armar el ZIP lee archivos y comprime: fuera del hilo del server.
            var r = await Task.Run(() => _svc.Crear(req.descripcion, png, req.logs_pantalla)).ConfigureAwait(false);
            await WriteJsonAsync(new
            {
                ok = r.Ok,
                codigo = r.Codigo,
                estado = r.Estado,
                vinculado = _svc.Vinculado,
                bytes = r.Bytes,
                nivel = r.Nivel,
                omitidos = r.Omitidos,
                error = r.Error,
            });
        }

        [Route(HttpVerbs.Get, "/soporte/reporte/estado")]
        public Task Estado([QueryField] string codigo)
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            if (!ReporteFallaArmador.CodigoValido(codigo))
                return WriteJsonAsync(new { ok = false, error = "codigo-invalido" });
            string estado = _svc.Estado(codigo);
            return WriteJsonAsync(new
            {
                ok = estado != null,
                codigo,
                estado,
                vinculado = _svc.Vinculado,
                error = estado == null ? "no-existe" : _svc.UltimoError,
            });
        }

        [Route(HttpVerbs.Post, "/soporte/reporte/pendrive")]
        public async Task Pendrive()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            CodigoReq req;
            try { req = await ReadJsonBodyAsync<CodigoReq>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            string codigo = req?.codigo;
            if (!ReporteFallaArmador.CodigoValido(codigo))
            {
                await WriteJsonAsync(new { ok = false, error = "Código de reporte inválido" });
                return;
            }
            var r = await Task.Run(() => _svc.CopiarAPendrive(codigo)).ConfigureAwait(false);
            await WriteJsonAsync(new { ok = r.Ok, ruta = r.Ruta, error = r.Error });
        }
    }
}
