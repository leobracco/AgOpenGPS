// ============================================================================
// RecPathController.cs — REST del picker/guardado de recorded paths.
//   GET  /api/recpath/list          → {ok, paths: ["name1", ...]}
//   POST /api/recpath/load  {name}  → cargar .rec
//   POST /api/recpath/delete {name} → borrar .rec
//   POST /api/recpath/off           → apagar recorded path
//   POST /api/recpath/save  {name}  → guardar con nombre
//   POST /api/recpath/discard       → descartar grabación
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class RecPathController : AgpControllerBase
    {
        private readonly IRecPathService _svc;

        public RecPathController(IRecPathService svc) { _svc = svc; }

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Get, "/recpath/list")]
        public Task GetList()
        {
            if (_svc == null) return Unavailable();
            var paths = _svc.ListPaths();
            return WriteJsonAsync(new { ok = true, paths });
        }

        [Route(HttpVerbs.Post, "/recpath/load")]
        public async Task PostLoad()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<NameBody>();
            if (b == null || string.IsNullOrWhiteSpace(b.Name))
            { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            bool ok = _svc.LoadPath(b.Name);
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Post, "/recpath/delete")]
        public async Task PostDelete()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<NameBody>();
            if (b == null || string.IsNullOrWhiteSpace(b.Name))
            { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            bool ok = _svc.DeletePath(b.Name);
            var paths = _svc.ListPaths();
            await WriteJsonAsync(new { ok, paths });
        }

        [Route(HttpVerbs.Post, "/recpath/off")]
        public Task PostOff()
        {
            if (_svc == null) return Unavailable();
            _svc.TurnOff();
            return WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/recpath/save")]
        public async Task PostSave()
        {
            if (_svc == null) { await Unavailable(); return; }
            var b = await ReadBody<NameBody>();
            if (b == null || string.IsNullOrWhiteSpace(b.Name))
            { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            bool ok = _svc.SaveWithName(b.Name);
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Post, "/recpath/discard")]
        public Task PostDiscard()
        {
            if (_svc == null) return Unavailable();
            _svc.DiscardRecording();
            return WriteJsonAsync(new { ok = true });
        }

        private async Task<T> ReadBody<T>() where T : class
        {
            try { return await ReadJsonBodyAsync<T>(); }
            catch { return null; }
        }

        private sealed class NameBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string Name { get; set; }
        }
    }
}
