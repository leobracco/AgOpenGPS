// ============================================================================
// QuickAbController.cs — REST del widget "AB rápido" (ab-rapido.html).
//   GET  /api/quickab/state             → estado (+tick del preview)
//   POST /api/quickab/start   {mode}    → arranca sesión (curve|ab|aplus)
//   POST /api/quickab/side              → alterna lado de referencia
//   POST /api/quickab/mark-a            → marca punto A / agrega punto (curva)
//   POST /api/quickab/mark-b            → marca punto B / cierra curva
//   POST /api/quickab/pause             → pausa/reanuda grabación (curva)
//   POST /api/quickab/heading {value}   → rumbo manual en grados (A+)
//   POST /api/quickab/commit            → confirma línea (ab/aplus) → nombre
//   POST /api/quickab/save    {name}    → guarda la guía y persiste
//   POST /api/quickab/cancel            → cancela y limpia preview
// Wire snake_case por AgpJson. Si el servicio no está inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuickAbController : AgpControllerBase
    {
        private readonly IQuickAbService _svc;

        public QuickAbController(IQuickAbService svc)
        {
            _svc = svc;
        }

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Get, "/quickab/state")]
        public Task GetState() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.GetState());

        [Route(HttpVerbs.Post, "/quickab/start")]
        public async Task PostStart()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<ModeBody>();
            await WriteJsonAsync(_svc.Start(body != null ? body.Mode : null));
        }

        [Route(HttpVerbs.Post, "/quickab/side")]
        public Task PostSide() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.ToggleSide());

        [Route(HttpVerbs.Post, "/quickab/mark-a")]
        public Task PostMarkA() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.MarkA());

        [Route(HttpVerbs.Post, "/quickab/mark-b")]
        public Task PostMarkB() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.MarkB());

        [Route(HttpVerbs.Post, "/quickab/pause")]
        public Task PostPause() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.PauseToggle());

        [Route(HttpVerbs.Post, "/quickab/heading")]
        public async Task PostHeading()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<ValueBody>();
            await WriteJsonAsync(_svc.SetHeading(body != null ? body.Value : 0));
        }

        [Route(HttpVerbs.Post, "/quickab/commit")]
        public Task PostCommit() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Commit());

        [Route(HttpVerbs.Post, "/quickab/save")]
        public async Task PostSave()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<NameBody>();
            await WriteJsonAsync(_svc.Save(body != null ? body.Name : null));
        }

        [Route(HttpVerbs.Post, "/quickab/cancel")]
        public Task PostCancel() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Cancel());

        private async Task<T> ReadBodyAsync<T>() where T : class
        {
            try { return await ReadJsonBodyAsync<T>(); }
            catch { return null; }
        }

        private sealed class ModeBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("mode")]
            public string Mode { get; set; }
        }

        private sealed class ValueBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("value")]
            public double Value { get; set; }
        }

        private sealed class NameBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string Name { get; set; }
        }
    }
}
