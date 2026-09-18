// ============================================================================
// CabeceraLineasController.cs — REST del constructor de cabecera por líneas
// (cabecera-lineas.html, ex FormHeadAche).
//   GET  /api/cabecera-lineas/state                 → estado + geometría
//   POST /api/cabecera-lineas/open                  → iniciar sesión de edición
//   POST /api/cabecera-lineas/tap {e,n,mode,distance} → punto A/B + crear línea
//   POST /api/cabecera-lineas/cancel-touch          → descartar punto A
//   POST /api/cabecera-lineas/cycle {dir}           → ciclar línea seleccionada
//   POST /api/cabecera-lineas/delete-track          → borrar línea seleccionada
//   POST /api/cabecera-lineas/extend {end,grow}     → extender/encoger extremo
//   POST /api/cabecera-lineas/build                 → armar cabecera (cruces)
//   POST /api/cabecera-lineas/reset                 → limpiar cabecera
//   POST /api/cabecera-lineas/off                   → apagar cabecera
//   POST /api/cabecera-lineas/section-controlled {on}
//   POST /api/cabecera-lineas/close                 → cierre de sesión (beacon)
// Wire snake_case por AgpJson. Sin servicio inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CabeceraLineasController : AgpControllerBase
    {
        private readonly ICabeceraLineasService _svc;

        public CabeceraLineasController(ICabeceraLineasService svc)
        {
            _svc = svc;
        }

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Get, "/cabecera-lineas/state")]
        public Task GetState() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.GetState());

        [Route(HttpVerbs.Post, "/cabecera-lineas/open")]
        public Task PostOpen() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Open());

        [Route(HttpVerbs.Post, "/cabecera-lineas/tap")]
        public async Task PostTap()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<TapBody>();
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "body-invalido" });
                return;
            }
            await WriteJsonAsync(_svc.Tap(body.E, body.N,
                body.Mode == "ab" ? "ab" : "curve", body.Distance));
        }

        [Route(HttpVerbs.Post, "/cabecera-lineas/cancel-touch")]
        public Task PostCancelTouch() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.CancelTouch());

        [Route(HttpVerbs.Post, "/cabecera-lineas/cycle")]
        public async Task PostCycle()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<DirBody>();
            await WriteJsonAsync(_svc.Cycle(body != null ? body.Dir : 1));
        }

        [Route(HttpVerbs.Post, "/cabecera-lineas/delete-track")]
        public Task PostDeleteTrack() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.DeleteTrack());

        [Route(HttpVerbs.Post, "/cabecera-lineas/extend")]
        public async Task PostExtend()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<ExtendBody>();
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "body-invalido" });
                return;
            }
            await WriteJsonAsync(_svc.Extend(body.End == "a" ? "a" : "b", body.Grow));
        }

        [Route(HttpVerbs.Post, "/cabecera-lineas/build")]
        public Task PostBuild() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.BuildHeadland());

        [Route(HttpVerbs.Post, "/cabecera-lineas/reset")]
        public Task PostReset() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.ResetHeadland());

        [Route(HttpVerbs.Post, "/cabecera-lineas/off")]
        public Task PostOff() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.TurnOff());

        [Route(HttpVerbs.Post, "/cabecera-lineas/section-controlled")]
        public async Task PostSectionControlled()
        {
            if (_svc == null) { await Unavailable(); return; }
            var body = await ReadBodyAsync<OnBody>();
            bool effective = _svc.SetSectionControlled(body != null && body.On);
            await WriteJsonAsync(new { ok = true, is_section_controlled = effective });
        }

        [Route(HttpVerbs.Post, "/cabecera-lineas/close")]
        public Task PostClose()
        {
            if (_svc == null) return Unavailable();
            _svc.CloseSession();
            return WriteJsonAsync(new { ok = true });
        }

        private async Task<T> ReadBodyAsync<T>() where T : class
        {
            try { return await ReadJsonBodyAsync<T>(); }
            catch { return null; }
        }

        private sealed class TapBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("e")]
            public double E { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("n")]
            public double N { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("mode")]
            public string Mode { get; set; } = "curve";

            [System.Text.Json.Serialization.JsonPropertyName("distance")]
            public double Distance { get; set; }
        }

        private sealed class DirBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("dir")]
            public int Dir { get; set; } = 1;
        }

        private sealed class ExtendBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("end")]
            public string End { get; set; } = "b";

            [System.Text.Json.Serialization.JsonPropertyName("grow")]
            public bool Grow { get; set; } = true;
        }

        private sealed class OnBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("on")]
            public bool On { get; set; }
        }
    }
}
