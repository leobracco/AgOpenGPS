// ============================================================================
// LotesController.cs
// REST endpoints for gestión de lotes (Fields/) desde la UI HTML.
//   GET  /api/lotes              → list of FieldInfo (snake_case)
//   GET  /api/lotes/current      → { name: string|null }
//   POST /api/lotes/open?name=…  → { ok: bool }
//   POST /api/lotes/close        → { ok: bool }
//   POST /api/lotes/create?name= → { ok: bool }
//   POST /api/lotes/from-existing {template,name,applied,flags,guidance,headland}
//   POST /api/lotes/import-kml    → diálogo nativo KML     (ex FormJob)
//   POST /api/lotes/import-isoxml → diálogo nativo ISO-XML (ex FormJob)
// ============================================================================

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
    public sealed class LotesController : AgpControllerBase
    {
        private readonly ILotesService _lotes;

        public LotesController(ILotesService lotes) { _lotes = lotes; }

        [Route(HttpVerbs.Get, "/lotes")]
        public Task List()
        {
            var list = _lotes != null ? _lotes.ListFields() : new System.Collections.Generic.List<FieldInfo>();
            return WriteJsonAsync(list);
        }

        [Route(HttpVerbs.Get, "/lotes/current")]
        public Task Current()
        {
            string name = _lotes != null ? _lotes.GetCurrentFieldName() : null;
            return WriteJsonAsync(new { name });
        }

        [Route(HttpVerbs.Post, "/lotes/open")]
        public async Task Open([QueryField] string name)
        {
            bool ok = _lotes != null && await _lotes.OpenFieldAsync(name);
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Post, "/lotes/close")]
        public async Task Close()
        {
            bool ok = _lotes != null && await _lotes.CloseFieldAsync();
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Post, "/lotes/create")]
        public async Task Create([QueryField] string name)
        {
            bool ok = _lotes != null && await _lotes.CreateFieldAsync(name);
            await WriteJsonAsync(new { ok });
        }

        // Clonar un lote existente como template (ex FormFieldExisting).
        // Body JSON snake_case: { template, name, applied, flags, guidance, headland }
        [Route(HttpVerbs.Post, "/lotes/from-existing")]
        public async Task FromExisting()
        {
            if (_lotes == null) { await WriteJsonAsync(new { ok = false }); return; }
            FromExistingBody body = null;
            try { body = await ReadJsonBodyAsync<FromExistingBody>(); } catch { }
            if (body == null || string.IsNullOrWhiteSpace(body.Template) || string.IsNullOrWhiteSpace(body.Name))
            {
                await WriteJsonAsync(new { ok = false, error = "body-invalido" });
                return;
            }
            bool ok = await _lotes.CreateFromExistingAsync(
                body.Template, body.Name, body.Applied, body.Flags, body.Guidance, body.Headland);
            await WriteJsonAsync(new { ok });
        }

        // Imports nativos (diálogos WinForms) — ex FormJob KML / ISO-XML.
        [Route(HttpVerbs.Post, "/lotes/import-kml")]
        public async Task ImportKml()
        {
            bool ok = _lotes != null && await _lotes.ImportKmlAsync();
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Post, "/lotes/import-isoxml")]
        public async Task ImportIsoXml()
        {
            bool ok = _lotes != null && await _lotes.ImportIsoXmlAsync();
            await WriteJsonAsync(new { ok });
        }

        private sealed class FromExistingBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("template")]
            public string Template { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string Name { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("applied")]
            public bool Applied { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("flags")]
            public bool Flags { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("guidance")]
            public bool Guidance { get; set; } = true;

            [System.Text.Json.Serialization.JsonPropertyName("headland")]
            public bool Headland { get; set; } = true;
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
                await WriteJsonAsync(new { ok = false, error = "no-field" });
                return;
            }
            string vistaxDir = System.IO.Path.Combine(fieldDir, "VistaX");
            if (!Directory.Exists(vistaxDir))
            {
                HttpContext.Response.StatusCode = 404;
                await WriteJsonAsync(new { ok = false, error = "no-vistax-data" });
                return;
            }

            string fieldName = System.IO.Path.GetFileName(fieldDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            string fname = string.Format("vistax_{0}_{1:yyyyMMdd_HHmmss}.zip",
                                          fieldName, System.DateTime.Now);

            HttpContext.Response.ContentType = "application/zip";
            HttpContext.Response.Headers["Content-Disposition"] =
                "attachment; filename=\"" + fname + "\"";
            HttpContext.Response.Headers["Cache-Control"] = "no-store";

            // Stream directo al body — no buffereo en memoria por si la sesión
            // pesa decenas de MB (heatmap shapefiles + ndjson largos).
            // serialización especial a propósito: binario ZIP, no JSON.
            using (var zip = new ZipArchive(HttpContext.Response.OutputStream,
                                            ZipArchiveMode.Create, true))
            {
                foreach (var path in Directory.GetFiles(vistaxDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string entryName = System.IO.Path.GetFileName(path);
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
