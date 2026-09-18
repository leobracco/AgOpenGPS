// ============================================================================
// TramSimpleController.cs — REST del editor "Tramlines simples" (tramline.html).
//   GET  /api/tram-simple/state          → estado inicial (Open: fija modo + build)
//   POST /api/tram-simple/passes {passes} → cambia pasadas + reconstruye
//   POST /api/tram-simple/alpha  {percent}→ cambia transparencia
//   POST /api/tram-simple/mode   {mode}   → cambia modo generación + reconstruye
//   POST /api/tram-simple/swap            → invierte A↔B + reconstruye
//   POST /api/tram-simple/commit {save}   → guarda (save=true) o descarta (false)
// Wire snake_case por AgpJson. Si el servicio no está inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class TramSimpleController : AgpControllerBase
    {
        private readonly ITramSimpleService _svc;

        public TramSimpleController(ITramSimpleService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/tram-simple/state")]
        public Task GetState()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.Open());
        }

        [Route(HttpVerbs.Post, "/tram-simple/passes")]
        public async Task PostPasses()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            PassesBody body;
            try { body = await ReadJsonBodyAsync<PassesBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            int passes = body != null ? body.Passes : 1;
            await WriteJsonAsync(_svc.SetPasses(passes));
        }

        [Route(HttpVerbs.Post, "/tram-simple/alpha")]
        public async Task PostAlpha()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            AlphaBody body;
            try { body = await ReadJsonBodyAsync<AlphaBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            int percent = body != null ? body.Percent : 0;
            await WriteJsonAsync(_svc.SetAlpha(percent));
        }

        [Route(HttpVerbs.Post, "/tram-simple/mode")]
        public async Task PostMode()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            ModeBody body;
            try { body = await ReadJsonBodyAsync<ModeBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            string mode = body != null ? body.Mode : "All";
            await WriteJsonAsync(_svc.SetMode(mode));
        }

        [Route(HttpVerbs.Post, "/tram-simple/swap")]
        public Task PostSwap()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.SwapAB());
        }

        [Route(HttpVerbs.Post, "/tram-simple/commit")]
        public async Task PostCommit()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            CommitBody body;
            try { body = await ReadJsonBodyAsync<CommitBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            bool save = body != null && body.Save;
            bool ok = _svc.Commit(save);
            await WriteJsonAsync(new { ok });
        }

        private sealed class PassesBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("passes")]
            public int Passes { get; set; }
        }

        private sealed class AlphaBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("percent")]
            public int Percent { get; set; }
        }

        private sealed class ModeBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("mode")]
            public string Mode { get; set; }
        }

        private sealed class CommitBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("save")]
            public bool Save { get; set; }
        }
    }
}
