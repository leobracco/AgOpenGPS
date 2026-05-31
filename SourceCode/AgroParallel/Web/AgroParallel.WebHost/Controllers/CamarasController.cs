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
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SysJson = System.Text.Json.JsonSerializer;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CamarasController : WebApiController
    {
        // El response-serializer default de EmbedIO (Swan) ignora
        // [JsonPropertyName] y emite PascalCase. La UI JS lee lowercase
        // (c.nombre, c.ip, state.config.camaras...), así que serializamos a
        // mano con System.Text.Json — mismo patrón que QuantiXController.
        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ICamarasConfigService _svc;

        public CamarasController(ICamarasConfigService svc) { _svc = svc; }

        [Route(HttpVerbs.Get, "/camaras/config")]
        public async Task GetConfig()
        {
            string json = _svc == null
                ? SysJson.Serialize(new { ok = false, error = "service-unavailable" })
                : SysJson.Serialize(new { ok = true, config = _svc.GetConfig() });
            await HttpContext
                .SendStringAsync(json, "application/json", System.Text.Encoding.UTF8)
                .ConfigureAwait(false);
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
            try { cfg = SysJson.Deserialize<CamarasConfigDto>(body, ReadOpts); }
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
