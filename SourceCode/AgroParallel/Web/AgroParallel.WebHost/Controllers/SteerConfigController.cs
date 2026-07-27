// ============================================================================
// SteerConfigController.cs
//
// Config del autoguiado (port del FormSteer nativo) para la página
// direccion.html:
//   GET  /api/steer/config    → SteerConfigDto leído de los settings reales
//   POST /api/steer/config    → persiste + aplica + manda PGN 252/251 al módulo
//   POST /api/steer/zero-was  → cero del sensor de ángulo (WAS) con lectura viva
//
// La lógica real vive en ISteerConfigService (implementación compartida
// AgroParallel.Adapters.SteerConfigService, la usan tanto FormGPS como el motor
// headless). Si el host no inyecta el servicio — caso del Hub Android, que hoy
// no tiene módulo de dirección — se cae al comportamiento viejo: persistir el
// objeto TAL CUAL a un JSON en ConfigRoot, para que la pantalla siga guardando
// y releyendo sin romperse.
// ============================================================================

using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SteerConfigController : AgpControllerBase
    {
        // Cap defensivo: el config son unos KB; 512 KB frena un POST gigante.
        private const long MaxBytes = 512L * 1024;

        private readonly ISteerConfigService _svc;

        public SteerConfigController(ISteerConfigService svc = null)
        {
            _svc = svc;
        }

        private static string FilePath
        {
            get
            {
                string root = string.IsNullOrEmpty(AgpPaths.ConfigRoot)
                    ? AppContext.BaseDirectory
                    : AgpPaths.ConfigRoot;
                return Path.Combine(root, "steer-config.json");
            }
        }

        [Route(HttpVerbs.Get, "/steer/config")]
        public async Task Get()
        {
            if (_svc != null)
            {
                await WriteJsonAsync(_svc.Get()).ConfigureAwait(false);
                return;
            }

            // ---- fallback sin servicio: devolver el blob guardado tal cual ----
            try
            {
                if (File.Exists(FilePath))
                {
                    byte[] bytes = File.ReadAllBytes(FilePath);
                    HttpContext.Response.StatusCode = 200;
                    HttpContext.Response.ContentType = "application/json";
                    HttpContext.Response.Headers["Cache-Control"] = "no-store";
                    HttpContext.Response.ContentLength64 = bytes.Length;
                    await HttpContext.Response.OutputStream
                        .WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                    return;
                }
            }
            catch { /* si falla la lectura, cae a {} y la UI usa defaults */ }

            await WriteJsonAsync(new { }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/steer/config")]
        public async Task Post()
        {
            if (_svc != null)
            {
                SteerConfigDto cfg;
                try { cfg = await ReadJsonBodyAsync<SteerConfigDto>().ConfigureAwait(false); }
                catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }).ConfigureAwait(false); return; }

                if (cfg == null)
                {
                    await WriteJsonAsync(new { ok = false, error = "bad-json" }).ConfigureAwait(false);
                    return;
                }

                try
                {
                    bool ok = _svc.Save(cfg);
                    await WriteJsonAsync(new { ok }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await WriteJsonAsync(new { ok = false, error = ex.Message }).ConfigureAwait(false);
                }
                return;
            }

            // ---- fallback sin servicio: guardar el body crudo ----
            string body;
            try
            {
                using (var input = HttpContext.Request.InputStream)
                using (var buf = new MemoryStream())
                {
                    byte[] chunk = new byte[16 * 1024];
                    int n; long total = 0;
                    while ((n = await input.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > MaxBytes)
                        {
                            await WriteJsonAsync(new { ok = false, error = "file-too-large" }).ConfigureAwait(false);
                            return;
                        }
                        await buf.WriteAsync(chunk, 0, n).ConfigureAwait(false);
                    }
                    body = Encoding.UTF8.GetString(buf.ToArray());
                }

                // Validación mínima: que parezca un objeto JSON.
                string trimmed = (body ?? string.Empty).TrimStart();
                if (!trimmed.StartsWith("{"))
                {
                    await WriteJsonAsync(new { ok = false, error = "invalid-json" }).ConfigureAwait(false);
                    return;
                }

                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, body, new UTF8Encoding(false));

                await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
        }

        [Route(HttpVerbs.Post, "/steer/zero-was")]
        public Task ZeroWas()
        {
            if (_svc == null)
            {
                // Sin módulo de dirección detrás: la UI ya trata ok=false como
                // "módulo offline" y deja el slider como estaba.
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            }

            try { return WriteJsonAsync(_svc.ZeroWas()); }
            catch (Exception ex) { return WriteJsonAsync(new { ok = false, error = ex.Message }); }
        }
    }
}
