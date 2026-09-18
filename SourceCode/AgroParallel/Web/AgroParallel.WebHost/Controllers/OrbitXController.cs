// ============================================================================
// OrbitXController.cs
// Endpoints REST del módulo OrbitX:
//   GET  /api/orbitx/config          → OrbitXConfigDto  (snake_case via [JsonPropertyName])
//   POST /api/orbitx/config          (body = OrbitXConfigDto snake_case) → { ok }
//   GET  /api/orbitx/status          → OrbitXStatus (snake_case via AgpJson)
//   POST /api/orbitx/test            → { ok, error? }
//   GET  /api/orbitx/pair-info       → OrbitXPairInfo (snake_case via AgpJson)
//   POST /api/orbitx/pair-reset      → { ok }
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class OrbitXController : AgpControllerBase
    {
        private readonly IOrbitXConfigService _cfg;

        public OrbitXController(IOrbitXConfigService cfg)
        {
            _cfg = cfg;
        }

        [Route(HttpVerbs.Get, "/orbitx/config")]
        public Task GetConfig()
        {
            if (_cfg == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            // OrbitXConfigDto tiene [JsonPropertyName] snake_case → AgpJson los respeta.
            return WriteJsonAsync(_cfg.Load());
        }

        [Route(HttpVerbs.Post, "/orbitx/config")]
        public async Task SaveConfig()
        {
            if (_cfg == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }

            OrbitXConfigDto incoming;
            try { incoming = await ReadJsonBodyAsync<OrbitXConfigDto>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            if (incoming == null) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            // MERGE sobre la config persistida — NUNCA reconstruir el DTO desde
            // cero. El body llega en snake_case (via [JsonPropertyName]) y la UI
            // manda solo los campos editables; los campos no enviados (null/default)
            // se preservan de la config en disco para no borrar device_token,
            // master_token, firmware_*, camaras_* accidentalmente.
            var cfg = _cfg.Load();
            // Campos editables desde la UI:
            cfg.Enabled = incoming.Enabled;
            if (!string.IsNullOrEmpty(incoming.EstabSlug)) cfg.EstabSlug = incoming.EstabSlug;
            if (!string.IsNullOrEmpty(incoming.DeviceToken)) cfg.DeviceToken = incoming.DeviceToken;
            if (incoming.SyncIntervalSec > 0) cfg.SyncIntervalSec = incoming.SyncIntervalSec;
            cfg.SyncAOG = incoming.SyncAOG;
            cfg.SyncVistaX = incoming.SyncVistaX;
            cfg.SyncQuantiX = incoming.SyncQuantiX;
            cfg.SyncSectionX = incoming.SyncSectionX;

            _cfg.Save(cfg);
            await WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Get, "/orbitx/status")]
        public Task GetStatus()
        {
            if (_cfg == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_cfg.GetStatus());
        }

        [Route(HttpVerbs.Post, "/orbitx/test")]
        public async Task Test()
        {
            if (_cfg == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            bool ok = await _cfg.TestConnectionAsync();
            await WriteJsonAsync(new { ok, error = ok ? null : _cfg.GetStatus().LastError });
        }

        // ── Pairing flow (UI lo pollea cada ~4s en la página OrbitX) ────────
        [Route(HttpVerbs.Get, "/orbitx/pair-info")]
        public async Task GetPairInfo()
        {
            if (_cfg == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            // OrbitXPairInfo serializado via AgpJson → snake_case:
            // paired, just_claimed, code, expires_in_sec, device_id, estab_slug,
            // server_url, status, hint, error_code, hint_technical
            var info = await _cfg.GetPairInfoAsync();
            await WriteJsonAsync(info);
        }

        [Route(HttpVerbs.Post, "/orbitx/pair-reset")]
        public Task ResetPair()
        {
            if (_cfg == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            _cfg.ResetPairing();
            return WriteJsonAsync(new { ok = true });
        }
    }
}
