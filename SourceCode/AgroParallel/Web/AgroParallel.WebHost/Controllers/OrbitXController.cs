// ============================================================================
// OrbitXController.cs
// Endpoints REST del módulo OrbitX:
//   GET  /api/orbitx/config          → OrbitXConfigDto
//   POST /api/orbitx/config          (body = OrbitXConfigDto) → { ok }
//   GET  /api/orbitx/status          → OrbitXStatus
//   POST /api/orbitx/test            → { ok, error? }
// ============================================================================

using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class OrbitXController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IOrbitXConfigService _cfg;

        public OrbitXController(IOrbitXConfigService cfg)
        {
            _cfg = cfg;
        }

        [Route(HttpVerbs.Get, "/orbitx/config")]
        public object GetConfig()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            return _cfg.Load();
        }

        [Route(HttpVerbs.Post, "/orbitx/config")]
        public async Task<object> SaveConfig()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            var dto = JsonSerializer.Deserialize<OrbitXConfigDto>(body, JsonOpts);
            if (dto == null) return new { ok = false, error = "invalid-body" };
            _cfg.Save(dto);
            return new { ok = true };
        }

        [Route(HttpVerbs.Get, "/orbitx/status")]
        public object GetStatus()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            return _cfg.GetStatus();
        }

        [Route(HttpVerbs.Post, "/orbitx/test")]
        public async Task<object> Test()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            bool ok = await _cfg.TestConnectionAsync();
            return new { ok, error = ok ? null : _cfg.GetStatus().LastError };
        }

        // ── Pairing flow (UI lo pollea cada ~4s en la página OrbitX) ────────
        [Route(HttpVerbs.Get, "/orbitx/pair-info")]
        public async Task<object> GetPairInfo()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            var info = await _cfg.GetPairInfoAsync();
            return new
            {
                paired = info.Paired,
                justClaimed = info.JustClaimed,
                code = info.Code,
                expiresInSec = info.ExpiresInSec,
                deviceId = info.DeviceId,
                estabSlug = info.EstabSlug,
                serverUrl = info.ServerUrl,
                status = info.Status,
                hint = info.Hint,
                errorCode = info.ErrorCode,
                hintTechnical = info.HintTechnical
            };
        }

        [Route(HttpVerbs.Post, "/orbitx/pair-reset")]
        public object ResetPair()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            _cfg.ResetPairing();
            return new { ok = true };
        }
    }
}
