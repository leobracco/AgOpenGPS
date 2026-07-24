// ============================================================================
// SteerConfigController.cs
//
// Config del autoguiado (FormSteer) para la página direccion.html:
//   GET  /api/steer/config    → devuelve el config guardado (o {} si no hay)
//   POST /api/steer/config    → persiste el objeto de config recibido
//   POST /api/steer/zero-was  → pone el WAS en cero (stopgap)
//
// STOPGAP (Leonardo, carril UI — avisado a Santi): persiste el objeto TAL CUAL
// lo manda la UI a un JSON en ConfigRoot, para que el operario pueda verificar
// que cada campo se graba y persiste al reabrir. El mapeo REAL a
// Settings.Default.setAS_* (Kp/lowSteerPWM/highSteerPWM/countsPerDegree/…) y el
// envío del PGN 252 al módulo de dirección es carril de Santi (engine): cuando
// esté, este controller debería leer/escribir esos settings en vez del blob.
// ============================================================================

using AgroParallel.Common;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
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
            try
            {
                if (File.Exists(FilePath))
                {
                    byte[] bytes = File.ReadAllBytes(FilePath);
                    HttpContext.Response.StatusCode = 200;
                    HttpContext.Response.ContentType = "application/json";
                    HttpContext.Response.Headers["Cache-Control"] = "no-store";
                    HttpContext.Response.ContentLength64 = bytes.Length;
                    // serialización especial a propósito: se devuelve el JSON tal
                    // cual se guardó (mismas claves camelCase que espera la UI).
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
            // Stopgap: el cero real del WAS lo hace el módulo/engine (carril Santi).
            return WriteJsonAsync(new { ok = true });
        }
    }
}
