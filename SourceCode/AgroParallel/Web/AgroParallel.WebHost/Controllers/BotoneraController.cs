// ============================================================================
// BotoneraController.cs
// Layout de la botonera HTML (pages/botonera.html): orden de TODOS los
// botones + carpetas armadas por el operario. Se persiste como CSV editable
// en AppDomain.BaseDirectory\botonera-layout.csv (mismo criterio que
// overlayPrefs.json) para que el layout quede FIJO en el equipo — no vive
// en el localStorage del WebView.
//
//   GET  /api/aog/botonera  → { ok, csv }   (csv = "" si no hay layout aún)
//   POST /api/aog/botonera  { csv }         → { ok }
//
// Formato CSV (lo arma/parsea botonera.js, editable a mano):
//   carpeta,cmd
//   ,center            ← suelto (carpeta vacía), orden = orden de línea
//   Guías,nudge_left   ← dentro de la carpeta "Guías"
//   Riego,             ← carpeta vacía (declarada sin cmd)
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class BotoneraController : AgpControllerBase
    {
        private static string LayoutPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "botonera-layout.csv");

        private static readonly object _ioLock = new object();

        [Route(HttpVerbs.Get, "/aog/botonera")]
        public Task GetLayout()
        {
            string csv = "";
            try
            {
                lock (_ioLock)
                {
                    if (File.Exists(LayoutPath))
                        csv = File.ReadAllText(LayoutPath, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                return WriteJsonAsync(new { ok = false, error = ex.Message });
            }
            return WriteJsonAsync(new { ok = true, csv });
        }

        [Route(HttpVerbs.Post, "/aog/botonera")]
        public async Task SaveLayout()
        {
            LayoutBody body;
            try { body = await ReadJsonBodyAsync<LayoutBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null || body.Csv == null)
            {
                await WriteJsonAsync(new { ok = false, error = "empty-body" });
                return;
            }
            try
            {
                lock (_ioLock)
                {
                    File.WriteAllText(LayoutPath, body.Csv, Encoding.UTF8);
                }
                await WriteJsonAsync(new { ok = true });
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(new { ok = false, error = ex.Message });
            }
        }

        private sealed class LayoutBody
        {
            [JsonPropertyName("csv")]
            public string Csv { get; set; }
        }
    }
}
