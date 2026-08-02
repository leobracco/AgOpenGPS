// ============================================================================
// SonidosController.cs — API de las alarmas sonoras de cabina.
//   GET  /api/sonidos/config          → SonidosConfigDto
//   PUT  /api/sonidos/config          → guarda (mute, eventos, umbrales)
//   GET  /api/sonidos/estado?desde=N  → activas + disparos nuevos (seq > N)
//   GET  /api/sonidos/archivos        → wavs disponibles en wwwroot/sounds
//   POST /api/sonidos/archivos?nombre=x.wav → sube un wav propio (cap 2 MB)
// El que SUENA es el cliente (Desktop por winmm, la pantalla HTML por <audio>).
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SonidosController : AgpControllerBase
    {
        private readonly SonidosAlarmService _svc;
        private readonly string _soundsDir;

        public SonidosController(SonidosAlarmService svc, string wwwroot)
        {
            _svc = svc;
            _soundsDir = string.IsNullOrEmpty(wwwroot) ? null : Path.Combine(wwwroot, "sounds");
        }

        [Route(HttpVerbs.Get, "/sonidos/config")]
        public Task GetConfig()
            => WriteJsonAsync(_svc != null ? _svc.GetConfig() : SonidosAlarmService.ConfigDefault());

        [Route(HttpVerbs.Put, "/sonidos/config")]
        public async Task PutConfig()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var dto = await ReadJsonBodyAsync<SonidosConfigDto>();
            bool ok = dto != null && _svc.SaveConfig(dto);
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Get, "/sonidos/estado")]
        public Task GetEstado([QueryField] long desde)
            => WriteJsonAsync(_svc != null ? _svc.GetEstado(desde) : new SonidosEstadoDto());

        [Route(HttpVerbs.Get, "/sonidos/archivos")]
        public Task GetArchivos()
        {
            var lista = new System.Collections.Generic.List<string>();
            try
            {
                if (_soundsDir != null && Directory.Exists(_soundsDir))
                    lista = Directory.GetFiles(_soundsDir, "*.wav")
                        .Select(Path.GetFileName).OrderBy(n => n).ToList();
            }
            catch { }
            return WriteJsonAsync(new { ok = true, archivos = lista });
        }

        // Body = el .wav crudo. Sin multipart: mismo criterio que el upload de
        // firmwares del Hub (más simple para el WebView y para curl).
        [Route(HttpVerbs.Post, "/sonidos/archivos")]
        public async Task PostArchivo([QueryField] string nombre)
        {
            if (_soundsDir == null) { await WriteJsonAsync(new { ok = false, error = "sin-carpeta" }); return; }
            string limpio = Path.GetFileName(nombre ?? "").Trim();
            if (string.IsNullOrEmpty(limpio) || !limpio.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            { await WriteJsonAsync(new { ok = false, error = "nombre-invalido (.wav)" }); return; }

            byte[] datos;
            using (var ms = new MemoryStream())
            {
                await Request.InputStream.CopyToAsync(ms);
                datos = ms.ToArray();
            }
            if (datos.Length < 44 || datos.Length > 2 * 1024 * 1024)
            { await WriteJsonAsync(new { ok = false, error = "tamano (44 B – 2 MB)" }); return; }
            // Firma RIFF/WAVE: que al menos sea un wav de verdad.
            if (!(datos[0] == 'R' && datos[1] == 'I' && datos[2] == 'F' && datos[3] == 'F'))
            { await WriteJsonAsync(new { ok = false, error = "no-es-wav" }); return; }

            try
            {
                Directory.CreateDirectory(_soundsDir);
                File.WriteAllBytes(Path.Combine(_soundsDir, limpio), datos);
                await WriteJsonAsync(new { ok = true, archivo = limpio });
            }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = ex.Message }); }
        }
    }
}
