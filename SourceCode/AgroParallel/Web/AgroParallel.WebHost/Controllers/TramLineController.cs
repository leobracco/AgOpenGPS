// ============================================================================
// TramLineController.cs — REST del constructor de tramlines (tramlines.html).
//   GET  /api/tramlines/state                   → estado + geometría
//   POST /api/tramlines/open                    → iniciar sesión
//   POST /api/tramlines/cycle   {dir}           → ciclar línea de guiado
//   POST /api/tramlines/swap                    → cambiar lado
//   POST /api/tramlines/passes  {passes}        → ajustar cantidad
//   POST /api/tramlines/start-pass {start_pass} → ajustar inicio
//   POST /api/tramlines/outer   {on}            → toggle outer tram bnd
//   POST /api/tramlines/alpha   {alpha}         → opacidad trams guardados
//   POST /api/tramlines/add                     → confirmar trams nuevos
//   POST /api/tramlines/delete-all              → borrar todos
//   POST /api/tramlines/tap     {e,n}           → 3-tap cut (A/B/side)
//   POST /api/tramlines/cancel-touch            → descartar toques de corte
//   POST /api/tramlines/close                   → guardar y salir
//   POST /api/tramlines/cancel                  → revertir y salir
// Wire snake_case por AgpJson. Sin servicio inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class TramLineController : AgpControllerBase
    {
        private readonly ITramLineService _svc;

        public TramLineController(ITramLineService svc)
        {
            _svc = svc;
        }

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Get, "/tramlines/state")]
        public Task GetState() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.GetState());

        [Route(HttpVerbs.Post, "/tramlines/open")]
        public Task PostOpen() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Open());

        [Route(HttpVerbs.Post, "/tramlines/cycle")]
        public async Task PostCycle()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBody<DirBody>();
            await WriteJsonAsync(_svc.CycleTrack(body != null ? body.Dir : 1));
        }

        [Route(HttpVerbs.Post, "/tramlines/swap")]
        public Task PostSwap() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.SwapSide());

        [Route(HttpVerbs.Post, "/tramlines/passes")]
        public async Task PostPasses()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBody<PassesBody>();
            await WriteJsonAsync(_svc.SetPasses(body != null ? body.Passes : 2));
        }

        [Route(HttpVerbs.Post, "/tramlines/start-pass")]
        public async Task PostStartPass()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBody<StartPassBody>();
            await WriteJsonAsync(_svc.SetStartPass(body != null ? body.StartPass : 0));
        }

        [Route(HttpVerbs.Post, "/tramlines/outer")]
        public async Task PostOuter()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBody<OnBody>();
            await WriteJsonAsync(_svc.SetOuter(body != null && body.On));
        }

        [Route(HttpVerbs.Post, "/tramlines/alpha")]
        public async Task PostAlpha()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBody<AlphaBody>();
            await WriteJsonAsync(_svc.SetAlpha(body != null ? body.Alpha : 1.0));
        }

        [Route(HttpVerbs.Post, "/tramlines/add")]
        public Task PostAdd() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.AddLines());

        [Route(HttpVerbs.Post, "/tramlines/delete-all")]
        public Task PostDeleteAll() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.DeleteAll());

        [Route(HttpVerbs.Post, "/tramlines/tap")]
        public async Task PostTap()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBody<TapBody>();
            if (body == null) { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            await WriteJsonAsync(_svc.Tap(body.E, body.N));
        }

        [Route(HttpVerbs.Post, "/tramlines/cancel-touch")]
        public Task PostCancelTouch() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.CancelTouch());

        [Route(HttpVerbs.Post, "/tramlines/close")]
        public Task PostClose()
        {
            if (_svc == null) return Unavailable();
            _svc.CloseSession();
            return WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/tramlines/cancel")]
        public Task PostCancel()
        {
            if (_svc == null) return Unavailable();
            _svc.CancelSession();
            return WriteJsonAsync(new { ok = true });
        }

        private async Task<T> ReadBody<T>() where T : class
        {
            try { return await ReadJsonBodyAsync<T>(); }
            catch { return null; }
        }

        private sealed class DirBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("dir")]
            public int Dir { get; set; } = 1;
        }

        private sealed class PassesBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("passes")]
            public int Passes { get; set; } = 2;
        }

        private sealed class StartPassBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("start_pass")]
            public int StartPass { get; set; }
        }

        private sealed class OnBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("on")]
            public bool On { get; set; }
        }

        private sealed class AlphaBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("alpha")]
            public double Alpha { get; set; } = 1.0;
        }

        private sealed class TapBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("e")]
            public double E { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("n")]
            public double N { get; set; }
        }
    }
}
