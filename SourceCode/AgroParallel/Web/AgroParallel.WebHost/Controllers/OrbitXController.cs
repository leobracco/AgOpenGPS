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

            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(body); }
            catch { return new { ok = false, error = "invalid-body" }; }
            if (el.ValueKind != JsonValueKind.Object)
                return new { ok = false, error = "invalid-body" };

            // MERGE sobre la config persistida — NUNCA reconstruir el DTO desde
            // cero. El response de GetConfig sale PascalCase (serializer Swan de
            // EmbedIO, que ignora [JsonPropertyName]), y la UI reenvía esas claves;
            // al deserializar a OrbitXConfigDto (que bindea snake_case) los campos
            // con underscore NO matchean y quedaban en default → Save pisaba
            // device_token / master_token / firmware_* / camaras_* con vacío y
            // desvinculaba el tractor al apretar "Grabar". Bug 2026-06-09.
            // Acá tomamos sólo los campos editables, aceptando cualquier casing,
            // y preservamos lo demás de la config en disco.
            var cfg = _cfg.Load();
            cfg.Enabled = GetBool(el, cfg.Enabled, "enabled", "Enabled");
            cfg.EstabSlug = GetString(el, cfg.EstabSlug, "estab_slug", "estabSlug", "EstabSlug");
            cfg.DeviceToken = GetString(el, cfg.DeviceToken, "device_token", "deviceToken", "DeviceToken");
            cfg.SyncIntervalSec = GetInt(el, cfg.SyncIntervalSec, "sync_interval_sec", "syncIntervalSec", "SyncIntervalSec");
            cfg.SyncAOG = GetBool(el, cfg.SyncAOG, "sync_aog", "syncAOG", "SyncAOG");
            cfg.SyncVistaX = GetBool(el, cfg.SyncVistaX, "sync_vistax", "syncVistaX", "SyncVistaX");
            cfg.SyncQuantiX = GetBool(el, cfg.SyncQuantiX, "sync_quantix", "syncQuantiX", "SyncQuantiX");
            cfg.SyncSectionX = GetBool(el, cfg.SyncSectionX, "sync_sectionx", "syncSectionX", "SyncSectionX");

            _cfg.Save(cfg);
            return new { ok = true };
        }

        // Helpers tolerantes al casing — TryGetProperty es case-sensitive, así que
        // probamos snake_case / camelCase / PascalCase. Un string vacío se trata
        // como "no enviado" (devolvemos el default) para que la UI no pueda borrar
        // el device_token ni el estab_slug por accidente; el desvinculado real va
        // por ResetPairing().
        private static bool GetBool(JsonElement o, bool dflt, params string[] keys)
        {
            foreach (var k in keys)
                if (o.TryGetProperty(k, out var v))
                {
                    if (v.ValueKind == JsonValueKind.True) return true;
                    if (v.ValueKind == JsonValueKind.False) return false;
                    if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
                }
            return dflt;
        }

        private static int GetInt(JsonElement o, int dflt, params string[] keys)
        {
            foreach (var k in keys)
                if (o.TryGetProperty(k, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
                    if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
                }
            return dflt;
        }

        private static string GetString(JsonElement o, string dflt, params string[] keys)
        {
            foreach (var k in keys)
                if (o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            return dflt;
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
