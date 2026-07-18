// ============================================================================
// FlagsController.cs — REST del widget "Banderas" (banderas.html).
//   GET  /api/flags/state              → estado (lista + distancias live)
//   POST /api/flags/pick    {number}   → selecciona bandera (1-based)
//   POST /api/flags/delete             → borra la seleccionada
//   POST /api/flags/notes   {notes}    → notas de la seleccionada
//   POST /api/flags/add     {lat,lon,color,use_current} → crea bandera
//   POST /api/flags/close              → cierre del widget (deselecciona+guarda)
//   POST /api/flags/import             → import CSV (diálogo nativo)
//   POST /api/flags/export             → export CSV (diálogo nativo)
// Wire snake_case por AgpJson. Si el servicio no está inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class FlagsController : AgpControllerBase
    {
        private readonly IFlagsService _svc;

        public FlagsController(IFlagsService svc)
        {
            _svc = svc;
        }

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Get, "/flags/state")]
        public Task GetState() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.GetState());

        [Route(HttpVerbs.Post, "/flags/pick")]
        public async Task PostPick()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<NumberBody>();
            await WriteJsonAsync(_svc.Pick(body != null ? body.Number : 0));
        }

        [Route(HttpVerbs.Post, "/flags/delete")]
        public Task PostDelete() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Delete());

        [Route(HttpVerbs.Post, "/flags/notes")]
        public async Task PostNotes()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<NotesBody>();
            await WriteJsonAsync(_svc.SetNotes(body != null ? body.Notes : null));
        }

        [Route(HttpVerbs.Post, "/flags/add")]
        public async Task PostAdd()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<AddBody>();
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "body-invalido" });
                return;
            }
            await WriteJsonAsync(_svc.Add(body.Lat, body.Lon, body.Color, body.UseCurrent));
        }

        [Route(HttpVerbs.Post, "/flags/close")]
        public Task PostClose() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.CloseSession());

        [Route(HttpVerbs.Post, "/flags/import")]
        public Task PostImport() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Import());

        [Route(HttpVerbs.Post, "/flags/export")]
        public Task PostExport() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Export());

        private async Task<T> ReadBodyAsync<T>() where T : class
        {
            try { return await ReadJsonBodyAsync<T>(); }
            catch { return null; }
        }

        private sealed class NumberBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("number")]
            public int Number { get; set; }
        }

        private sealed class NotesBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("notes")]
            public string Notes { get; set; }
        }

        private sealed class AddBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("lat")]
            public double Lat { get; set; } = double.NaN;

            [System.Text.Json.Serialization.JsonPropertyName("lon")]
            public double Lon { get; set; } = double.NaN;

            [System.Text.Json.Serialization.JsonPropertyName("color")]
            public int Color { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("use_current")]
            public bool UseCurrent { get; set; }
        }
    }
}
