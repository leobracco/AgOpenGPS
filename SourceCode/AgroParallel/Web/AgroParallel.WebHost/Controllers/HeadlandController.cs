// ============================================================================
// HeadlandController.cs — REST del editor de cabecera (cabecera.html).
//   GET  /api/headland/state              → estado + geometría (fence/hdLine E/N)
//   POST /api/headland/build   {distance} → offset Build Around (unidades display)
//   POST /api/headland/reset              → cabecera = copia del contorno
//   POST /api/headland/off                → apaga la cabecera
//   POST /api/headland/section-controlled {on} → toggle secciones por cabecera
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
