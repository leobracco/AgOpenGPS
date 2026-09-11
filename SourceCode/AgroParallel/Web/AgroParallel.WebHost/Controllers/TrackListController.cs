// ============================================================================
// TrackListController.cs
// Endpoints REST de la lista de guías (AB/curvas) del lote activo:
//   GET  /api/aog/tracks          → { ok, tracks: [TrackItemDto...] }
//   POST /api/aog/tracks/select   { "index": N } → activa esa guía
//
// Consumido por el panel "Elegir guía" de pages/guia-rapida.html. Proxy
// delgado: la lectura de FormGPS.trk vive en FormGpsTrackListService.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class TrackListController : AgpControllerBase
    {
        private readonly ITrackListService _svc;

        public TrackListController(ITrackListService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/aog/tracks")]
        public Task GetTracks()
        {
            var tracks = _svc != null ? _svc.GetTracks() : new System.Collections.Generic.List<AgroParallel.Models.TrackItemDto>();
            return WriteJsonAsync(new { ok = true, tracks });
        }

        [Route(HttpVerbs.Post, "/aog/tracks/select")]
        public async Task PostSelect()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            SelectBody body;
            try { body = await ReadJsonBodyAsync<SelectBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "bad-json" });
                return;
            }
            bool ok = _svc.SelectTrack(body.Index);
            await WriteJsonAsync(new { ok, index = body.Index });
        }

        private sealed class SelectBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("index")]
            public int Index { get; set; }
        }
    }
}
