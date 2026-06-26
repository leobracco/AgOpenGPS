// ============================================================================
// LotesController.cs
// REST endpoints for gestión de lotes (Fields/) desde la UI HTML.
//   GET  /api/lotes              → list of FieldInfo
//   GET  /api/lotes/current      → { name: string|null }
//   POST /api/lotes/open?name=…  → { ok: bool }
//   POST /api/lotes/close        → { ok: bool }
//   POST /api/lotes/create?name= → { ok: bool }
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class LotesController : WebApiController
    {
        private readonly ILotesService _lotes;

        public LotesController(ILotesService lotes) { _lotes = lotes; }

        [Route(HttpVerbs.Get, "/lotes")]
        public IList<FieldInfo> List()
        {
            if (_lotes == null) return new List<FieldInfo>();
            return _lotes.ListFields();
        }

        [Route(HttpVerbs.Get, "/lotes/current")]
        public object Current()
        {
            string name = _lotes != null ? _lotes.GetCurrentFieldName() : null;
            return new { name = name };
        }

        [Route(HttpVerbs.Post, "/lotes/open")]
        public async Task<object> Open([QueryField] string name)
        {
            bool ok = _lotes != null && await _lotes.OpenFieldAsync(name);
            return new { ok = ok };
        }

        [Route(HttpVerbs.Post, "/lotes/close")]
        public async Task<object> Close()
        {
            bool ok = _lotes != null && await _lotes.CloseFieldAsync();
            return new { ok = ok };
        }

        [Route(HttpVerbs.Post, "/lotes/create")]
        public async Task<object> Create([QueryField] string name)
        {
            bool ok = _lotes != null && await _lotes.CreateFieldAsync(name);
            return new { ok = ok };
        }

        // Devuelve un ZIP con todo lo que el VistaXFieldLogger dejó en
        // <Field>/VistaX/* (ndjson + shapefiles + .prj). Pensado para que el
        // operario descargue la sesión completa desde el Hub y la pase a un
        // agrónomo (QGIS) o la archive offline.
        [Route(HttpVerbs.Get, "/lotes/vistax-zip")]
        public async Task VistaXZip()
        {
            string fieldDir = _lotes != null ? _lotes.GetCurrentFieldDirectory() : null;
            if (string.IsNullOrEmpty(fieldDir) || !Directory.Exists(fieldDir))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"no-field\"}", "application/json", System.Text.Encoding.UTF8).ConfigureAwait(false);
                return;
            }
            string vistaxDir = Path.Combine(fieldDir, "VistaX");
            if (!Directory.Exists(vistaxDir))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"no-vistax-data\"}", "application/json", System.Text.Encoding.UTF8).ConfigureAwait(false);
                return;
            }

            string fieldName = Path.GetFileName(fieldDir.TrimEnd(Path.DirectorySeparatorChar));
            string fname = string.Format("vistax_{0}_{1:yyyyMMdd_HHmmss}.zip",
                                          fieldName, System.DateTime.Now);

            HttpContext.Response.ContentType = "application/zip";
            HttpContext.Response.Headers["Content-Disposition"] =
                "attachment; filename=\"" + fname + "\"";
            HttpContext.Response.Headers["Cache-Control"] = "no-store";

            // Stream directo al body — no buffereo en memoria por si la sesión
            // pesa decenas de MB (heatmap shapefiles + ndjson largos).
            using (var zip = new ZipArchive(HttpContext.Response.OutputStream,
                                            ZipArchiveMode.Create, true))
            {
                foreach (var path in Directory.GetFiles(vistaxDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string entryName = Path.GetFileName(path);
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (var es = entry.Open())
                    using (var fs = File.OpenRead(path))
                    {
                        await fs.CopyToAsync(es).ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
