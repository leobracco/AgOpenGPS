// ============================================================================
// TrackBuilderController.cs — REST del gestor de tracks (tracks.html).
//   GET  /api/tracks/state                    → estado + lista
//   POST /api/tracks/open                     → iniciar sesión (backup)
//   POST /api/tracks/toggle-visibility {index}
//   POST /api/tracks/toggle-all {visible}
//   POST /api/tracks/select {index}
//   POST /api/tracks/delete
//   POST /api/tracks/duplicate {name}
//   POST /api/tracks/rename {name}
//   POST /api/tracks/move-up
//   POST /api/tracks/move-down
//   POST /api/tracks/swap-ab
//   POST /api/tracks/create-ab {heading_deg, name}
//   POST /api/tracks/use                      → guardar y salir
//   POST /api/tracks/cancel                   → revertir y salir
// Wire snake_case por AgpJson.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class TrackBuilderController : AgpControllerBase
    {
        private readonly ITrackBuilderService _svc;

        public TrackBuilderController(ITrackBuilderService svc) { _svc = svc; }

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Get, "/tracks/state")]
        public Task GetState() => _svc == null ? Unavailable() : WriteJsonAsync(_svc.GetState());

        [Route(HttpVerbs.Post, "/tracks/open")]
        public Task PostOpen() => _svc == null ? Unavailable() : WriteJsonAsync(_svc.Open());

        [Route(HttpVerbs.Post, "/tracks/toggle-visibility")]
        public async Task PostToggle()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<IndexBody>();
            await WriteJsonAsync(_svc.ToggleVisibility(b != null ? b.Index : -1));
        }

        [Route(HttpVerbs.Post, "/tracks/toggle-all")]
        public async Task PostToggleAll()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<VisibleBody>();
            await WriteJsonAsync(_svc.ToggleAll(b != null && b.Visible));
        }

        [Route(HttpVerbs.Post, "/tracks/select")]
        public async Task PostSelect()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<IndexBody>();
            await WriteJsonAsync(_svc.Select(b != null ? b.Index : -1));
        }

        [Route(HttpVerbs.Post, "/tracks/delete")]
        public Task PostDelete() => _svc == null ? Unavailable() : WriteJsonAsync(_svc.Delete());

        [Route(HttpVerbs.Post, "/tracks/duplicate")]
        public async Task PostDuplicate()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<NameBody>();
            await WriteJsonAsync(_svc.Duplicate(b?.Name));
        }

        [Route(HttpVerbs.Post, "/tracks/rename")]
        public async Task PostRename()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<NameBody>();
            await WriteJsonAsync(_svc.Rename(b?.Name));
        }

        [Route(HttpVerbs.Post, "/tracks/move-up")]
        public Task PostMoveUp() => _svc == null ? Unavailable() : WriteJsonAsync(_svc.MoveUp());

        [Route(HttpVerbs.Post, "/tracks/move-down")]
        public Task PostMoveDown() => _svc == null ? Unavailable() : WriteJsonAsync(_svc.MoveDown());

        [Route(HttpVerbs.Post, "/tracks/swap-ab")]
        public Task PostSwapAB() => _svc == null ? Unavailable() : WriteJsonAsync(_svc.SwapAB());

        [Route(HttpVerbs.Post, "/tracks/create-ab")]
        public async Task PostCreateAB()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<CreateABBody>();
            if (b == null) { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            await WriteJsonAsync(_svc.CreateABFromPivot(b.HeadingDeg, b.Name));
        }

        [Route(HttpVerbs.Post, "/tracks/use")]
        public Task PostUse()
        {
            if (_svc == null) return Unavailable();
            _svc.CloseUse();
            return WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/tracks/cancel")]
        public Task PostCancel()
        {
            if (_svc == null) return Unavailable();
            _svc.CloseCancel();
            return WriteJsonAsync(new { ok = true });
        }

        private async Task<T> ReadBody<T>() where T : class
        {
            try { return await ReadJsonBodyAsync<T>(); }
            catch { return null; }
        }

        private sealed class IndexBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("index")]
            public int Index { get; set; }
        }

        private sealed class VisibleBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("visible")]
            public bool Visible { get; set; }
        }

        private sealed class NameBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string Name { get; set; }
        }

        private sealed class CreateABBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("heading_deg")]
            public double HeadingDeg { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string Name { get; set; }
        }
    }
}
