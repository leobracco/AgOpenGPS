// ============================================================================
// HeadlandController.cs — REST del editor de cabecera (cabecera.html).
//   GET  /api/headland/state              → estado + geometría (fence/hdLine E/N)
//   POST /api/headland/build   {distance} → offset Build Around (unidades display)
//   POST /api/headland/reset              → cabecera = copia del contorno
//   POST /api/headland/off                → apaga la cabecera
//   POST /api/headland/section-controlled {on} → toggle secciones por cabecera
// Reshape manual (fase 2, ex FormHeadLine slice):
//   POST /api/headland/open                → iniciar sesión de edición
//   POST /api/headland/tap {e,n,mode,distance} → punto A/B + línea de corte
//   POST /api/headland/cancel-touch        → descartar toque/línea
//   POST /api/headland/extend {end,grow}   → extender/encoger extremo
//   POST /api/headland/clip                → cortar cabecera con la línea
//   POST /api/headland/undo                → volver al backup pre-corte
//   POST /api/headland/close               → suavizar+persistir+paneles (beacon)
// Wire snake_case por AgpJson. Si el servicio no está inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class HeadlandController : AgpControllerBase
    {
        private readonly IHeadlandEditService _svc;

        public HeadlandController(IHeadlandEditService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/headland/state")]
        public Task GetState()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.GetState());
        }

        [Route(HttpVerbs.Post, "/headland/build")]
        public async Task PostBuild()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            DistanceBody body;
            try { body = await ReadJsonBodyAsync<DistanceBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            double dist = body != null ? body.Distance : 0.0;
            await WriteJsonAsync(_svc.BuildAround(dist));
        }

        [Route(HttpVerbs.Post, "/headland/reset")]
        public Task PostReset()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.Reset());
        }

        [Route(HttpVerbs.Post, "/headland/off")]
        public Task PostOff()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.TurnOff());
        }

        [Route(HttpVerbs.Post, "/headland/section-controlled")]
        public async Task PostSectionControlled()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            ToggleBody body;
            try { body = await ReadJsonBodyAsync<ToggleBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            bool on = body != null && body.On;
            bool effective = _svc.SetSectionControlled(on);
            await WriteJsonAsync(new { ok = true, is_section_controlled = effective });
        }

        // ── Reshape manual ──────────────────────────────────────────────────

        private Task Unavailable() =>
            WriteJsonAsync(new { ok = false, error = "service-unavailable" });

        [Route(HttpVerbs.Post, "/headland/open")]
        public Task PostOpen() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Open());

        [Route(HttpVerbs.Post, "/headland/tap")]
        public async Task PostTap()
        {
            if (_svc == null) { await Unavailable(); return; }
            TapBody body;
            try { body = await ReadJsonBodyAsync<TapBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null) { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            await WriteJsonAsync(_svc.Tap(body.E, body.N,
                body.Mode == "ab" ? "ab" : "curve", body.Distance));
        }

        [Route(HttpVerbs.Post, "/headland/cancel-touch")]
        public Task PostCancelTouch() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.CancelTouch());

        [Route(HttpVerbs.Post, "/headland/extend")]
        public async Task PostExtend()
        {
            if (_svc == null) { await Unavailable(); return; }
            ExtendBody body;
            try { body = await ReadJsonBodyAsync<ExtendBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null) { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            await WriteJsonAsync(_svc.Extend(body.End == "a" ? "a" : "b", body.Grow));
        }

        [Route(HttpVerbs.Post, "/headland/clip")]
        public Task PostClip() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Clip());

        [Route(HttpVerbs.Post, "/headland/undo")]
        public Task PostUndo() =>
            _svc == null ? Unavailable() : WriteJsonAsync(_svc.Undo());

        [Route(HttpVerbs.Post, "/headland/close")]
        public Task PostClose()
        {
            if (_svc == null) return Unavailable();
            _svc.CloseSession();
            return WriteJsonAsync(new { ok = true });
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

        private sealed class ExtendBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("end")]
            public string End { get; set; } = "b";

            [System.Text.Json.Serialization.JsonPropertyName("grow")]
            public bool Grow { get; set; } = true;
        }

        private sealed class DistanceBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("distance")]
            public double Distance { get; set; }
        }

        private sealed class ToggleBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("on")]
            public bool On { get; set; }
        }
    }
}
