// ============================================================================
// CamarasController.cs
// Endpoints REST del módulo Cámaras:
//   GET  /api/camaras/config           → { ok, config: { camaras:[...], refresco_ms } }
//   PUT  /api/camaras/config           → recibe el mismo shape, persiste a JSON
//   GET  /api/camaras/{idx}/snapshot   → proxy a la cámara IP con auth Digest/Basic
//
// El snapshot proxy resuelve el problema de que <img> en el browser no puede
// hablar Digest auth con Hikvision. WebHost actúa de man-in-the-middle local.
// ============================================================================

using System;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CamarasController : AgpControllerBase
    {
        private readonly ICamarasConfigService _svc;

        public CamarasController(ICamarasConfigService svc) { _svc = svc; }

        [Route(HttpVerbs.Get, "/camaras/config")]
        public Task GetConfig()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, config = _svc.GetConfig() });
        }

        [Route(HttpVerbs.Put, "/camaras/config")]
        public async Task PutConfig()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" }).ConfigureAwait(false);
                return;
            }
            CamarasConfigDto cfg;
            try { cfg = await ReadJsonBodyAsync<CamarasConfigDto>().ConfigureAwait(false); }
            catch (Exception ex)
            {
                await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }).ConfigureAwait(false);
                return;
            }
            if (cfg == null)
            {
                await WriteJsonAsync(new { ok = false, error = "empty-body" }).ConfigureAwait(false);
                return;
            }
            _svc.SaveConfig(cfg);
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/camaras/{idx}/snapshot")]
        public async Task Snapshot(int idx)
        {
            if (_svc == null)
            {
                HttpContext.Response.StatusCode = 503;
                return;
            }
            var snap = await _svc.FetchSnapshotAsync(idx, HttpContext.CancellationToken)
                .ConfigureAwait(false);
            if (snap == null || snap.Bytes == null || snap.Bytes.Length == 0)
            {
                HttpContext.Response.StatusCode = 502;
                HttpContext.Response.Headers["X-Camera-Error"] = (snap == null ? "no-data" : (snap.Error ?? "no-data"));
                return;
            }
            HttpContext.Response.ContentType = string.IsNullOrEmpty(snap.ContentType) ? "image/jpeg" : snap.ContentType;
            HttpContext.Response.Headers["Cache-Control"] = "no-store";
            await HttpContext.Response.OutputStream
                .WriteAsync(snap.Bytes, 0, snap.Bytes.Length, HttpContext.CancellationToken)
                .ConfigureAwait(false);
        }
    }
}
