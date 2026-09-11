// ============================================================================
// ContornoController.cs — REST de la página "Contorno" (contorno.html).
//   GET  /api/contorno/state                     → lista de contornos
//   POST /api/contorno/drive-thru {index,value}  → drive-thru de un interno
//   POST /api/contorno/delete     {index}        → borra uno (confirmado en UI)
//   POST /api/contorno/delete-all                → borra todos
//   POST /api/contorno/import-kml {multi}        → KML (diálogo nativo)
//   POST /api/contorno/import-kml-upload?multi=  → KML subido (body = archivo)
//   POST /api/contorno/google-earth              → KML posición actual + abrir
//   POST /api/contorno/mapa                      → FormMap (dibujar satelital)
//   POST /api/contorno/from-tracks               → FormBuildBoundaryFromTracks
//   POST /api/contorno/record/start              → arranca grabación manejando
//   GET  /api/contorno/record/status             → estado grabación (poll 500ms)
//   POST /api/contorno/record/set {offset_cm,right_side,at_pivot,section_rec}
//   POST /api/contorno/record/pause              → toggle grabar/pausa
//   POST /api/contorno/record/add-point          → punto manual (en pausa)
//   POST /api/contorno/record/undo               → borra último punto
//   POST /api/contorno/record/restart            → limpia puntos (confirmado)
//   POST /api/contorno/record/save               → cierra y guarda contorno
//   POST /api/contorno/record/cancel             → aborta sin guardar
// Wire snake_case por AgpJson. Sin servicio inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ContornoController : AgpControllerBase
    {
        private readonly IContornoService _svc;

        public ContornoController(IContornoService svc)
        {
            _svc = svc;
        }

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Get, "/contorno/state")]
        public Task GetState() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.GetState());

        [Route(HttpVerbs.Post, "/contorno/drive-thru")]
        public async Task PostDriveThru()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<IndexValueBody>();
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "body-invalido" });
                return;
            }
            await WriteJsonAsync(_svc.SetDriveThru(body.Index, body.Value));
        }

        [Route(HttpVerbs.Post, "/contorno/delete")]
        public async Task PostDelete()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<IndexBody>();
            await WriteJsonAsync(_svc.Delete(body != null ? body.Index : -1));
        }

        [Route(HttpVerbs.Post, "/contorno/delete-all")]
        public Task PostDeleteAll() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.DeleteAll());

        [Route(HttpVerbs.Post, "/contorno/import-kml")]
        public async Task PostImportKml()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<MultiBody>();
            await WriteJsonAsync(_svc.ImportKml(body != null && body.Multi));
        }

        // Import de KML SIN diálogo: la pantalla manda el archivo como body
        // crudo (texto KML) + ?multi=1 para reemplazar todo. Un KML de lindero
        // son KB; 8 MB frena un POST desubicado sin molestar a nadie real.
        [Route(HttpVerbs.Post, "/contorno/import-kml-upload")]
        public async Task PostImportKmlUpload([QueryField] string multi)
        {
            if (_svc == null) { await Unavailable(); return; }

            // String a mano: el binder de EmbedIO revienta la CONEXIÓN entera
            // (ni siquiera devuelve error) si le llega ?multi=1 a un bool.
            bool esMulti = multi == "1" ||
                           string.Equals(multi, "true", System.StringComparison.OrdinalIgnoreCase);

            string kml;
            try
            {
                using (var input = HttpContext.Request.InputStream)
                using (var buf = new System.IO.MemoryStream())
                {
                    byte[] chunk = new byte[64 * 1024];
                    int n; long total = 0;
                    while ((n = await input.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > 8L * 1024 * 1024)
                        {
                            await WriteJsonAsync(new { ok = false, error = "archivo-demasiado-grande" });
                            return;
                        }
                        await buf.WriteAsync(chunk, 0, n).ConfigureAwait(false);
                    }
                    kml = System.Text.Encoding.UTF8.GetString(buf.ToArray());
                }
            }
            catch (System.Exception ex)
            {
                await WriteJsonAsync(new { ok = false, error = ex.Message });
                return;
            }

            await WriteJsonAsync(_svc.ImportKmlUpload(kml, esMulti));
        }

        [Route(HttpVerbs.Post, "/contorno/google-earth")]
        public Task PostGoogleEarth() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.OpenGoogleEarth());

        [Route(HttpVerbs.Post, "/contorno/mapa")]
        public Task PostMapa() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.OpenMapa());

        [Route(HttpVerbs.Post, "/contorno/from-tracks")]
        public Task PostFromTracks() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.BuildFromTracks());

        // ---------------- grabación manejando ----------------

        [Route(HttpVerbs.Post, "/contorno/record/start")]
        public Task PostRecordStart() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RecordStart());

        [Route(HttpVerbs.Get, "/contorno/record/status")]
        public Task GetRecordStatus() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RecordStatus());

        [Route(HttpVerbs.Post, "/contorno/record/set")]
        public async Task PostRecordSet()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<RecordSetBody>();
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "body-invalido" });
                return;
            }
            await WriteJsonAsync(_svc.RecordSet(body.OffsetCm, body.RightSide, body.AtPivot, body.SectionRec));
        }

        [Route(HttpVerbs.Post, "/contorno/record/pause")]
        public Task PostRecordPause() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RecordPause());

        [Route(HttpVerbs.Post, "/contorno/record/add-point")]
        public Task PostRecordAddPoint() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RecordAddPoint());

        [Route(HttpVerbs.Post, "/contorno/record/undo")]
        public Task PostRecordUndo() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RecordUndo());

        [Route(HttpVerbs.Post, "/contorno/record/restart")]
        public Task PostRecordRestart() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RecordRestart());

        [Route(HttpVerbs.Post, "/contorno/record/save")]
        public Task PostRecordSave() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RecordSave());

        [Route(HttpVerbs.Post, "/contorno/record/cancel")]
        public Task PostRecordCancel() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.RecordCancel());

        private async Task<T> ReadBodyAsync<T>() where T : class
        {
            try { return await ReadJsonBodyAsync<T>(); }
            catch { return null; }
        }

        private sealed class IndexBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("index")]
            public int Index { get; set; } = -1;
        }

        private sealed class IndexValueBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("index")]
            public int Index { get; set; } = -1;

            [System.Text.Json.Serialization.JsonPropertyName("value")]
            public bool Value { get; set; }
        }

        private sealed class MultiBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("multi")]
            public bool Multi { get; set; }
        }

        private sealed class RecordSetBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("offset_cm")]
            public double? OffsetCm { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("right_side")]
            public bool? RightSide { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("at_pivot")]
            public bool? AtPivot { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("section_rec")]
            public bool? SectionRec { get; set; }
        }
    }
}
