// ============================================================================
// CamarasController.cs
// Endpoints REST del módulo Cámaras:
//   GET  /api/camaras/config           → { ok, config: { camaras:[...], refrescoMs } }
//   PUT  /api/camaras/config           → recibe el mismo shape, persiste a JSON
//   GET  /api/camaras/{idx}/snapshot   → proxy a la cámara IP con auth Digest/Basic
//
// El snapshot proxy resuelve el problema de que <img> en el browser no puede
// hablar Digest auth con Hikvision. WebHost actúa de man-in-the-middle local.
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Swan.Formatters;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CamarasController : WebApiController
    {
        private readonly ICamarasConfigService _svc;

        public CamarasController(ICamarasConfigService svc) { _svc = svc; }

        [Route(HttpVerbs.Get, "/camaras/config")]
        public object GetConfig()
        {
            if (_svc == null)
                return new { ok = false, error = "service-unavailable" };
            return new { ok = true, config = _svc.GetConfig() };
        }

        [Route(HttpVerbs.Put, "/camaras/config")]
        public async Task<object> PutConfig()
        {
            if (_svc == null)
                return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            CamarasConfigDto cfg;
            try { cfg = Json.Deserialize<CamarasConfigDto>(body); }
            catch (Exception ex) { return new { ok = false, error = "bad-json: " + ex.Message }; }
            if (cfg == null) return new { ok = false, error = "empty-body" };
            _svc.SaveConfig(cfg);
            return new { ok = true };
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
