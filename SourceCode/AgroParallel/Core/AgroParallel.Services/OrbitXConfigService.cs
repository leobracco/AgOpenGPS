// ============================================================================
// OrbitXConfigService.cs
// Implementación de IOrbitXConfigService — lee/escribe orbitX.json y testea
// la conexión al server (GET /api/health) con X-Device-ID + X-Auth-Token.
// Compatible con el OrbitXConfig legacy (mismo shape de JSON, mismo path).
// ============================================================================

using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class OrbitXConfigService : IOrbitXConfigService
    {
        private const string FileName = "orbitX.json";
        // URL del cloud fija — no editable por el operario. Pedido explícito
        // 2026-05-27: "Deja la url fija, que no la tenga que cargar el usuario".
        // Si en algún momento se mueve el server, hay que cambiar este string
        // y recompilar; no se acepta override desde orbitX.json.
        public const string FixedServerUrl = "https://orbitx.agroparallel.com";
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        private string _lastError = "";

        // ── Pairing in-memory state ─────────────────────────────────────────
        // El código + secret viven en memoria (no en disco): si el tractor
        // reinicia sin haber sido reclamado, se genera otro código nuevo, lo
        // cual es lo correcto — el código tiene TTL en el cloud (10 min).
        private static readonly object _pairLock = new object();
        private static string _pairCode;
        private static string _pairSecret;
        private static long _pairBornUtcMs;
        private static bool _pairInitSentOk;
        private static bool _pairJustClaimedFlag;
        private const int PairTtlSec = 10 * 60;
        // Alfabeto del cloud (en sync con routes/devices.js PAIR_ALPHABET).
        private const string PairAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        public OrbitXConfigDto Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                var def = Defaults();
                Save(def);
                return def;
            }
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cfg = JsonSerializer.Deserialize<OrbitXConfigDto>(File.ReadAllText(path), opts);
                if (cfg == null) return Defaults();
                if (string.IsNullOrEmpty(cfg.DeviceId)) cfg.DeviceId = GenerateDeviceId();
                // Forzar la URL del cloud — JSONs viejos podrían tener un valor
                // distinto (ej. localhost de pruebas). El operario NO la edita.
                cfg.ServerUrl = FixedServerUrl;
                return cfg;
            }
            catch { return Defaults(); }
        }

        public void Save(OrbitXConfigDto dto)
        {
            if (dto == null) return;
            // Cualquier intento de guardar otra URL se neutraliza acá.
            dto.ServerUrl = FixedServerUrl;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(dto, opts));
        }

        public OrbitXStatus GetStatus()
        {
            var cfg = Load();
            return new OrbitXStatus
            {
                Enabled = cfg.Enabled,
                CloudConnected = false, // se actualiza vía TestConnectionAsync
                LastError = _lastError,
                LastSync = cfg.LastSync,
                FilesSynced = cfg.FilesSynced,
                EstabSlug = cfg.EstabSlug,
                DeviceId = cfg.DeviceId
            };
        }

        public async Task<bool> TestConnectionAsync()
        {
            var cfg = Load();
            if (string.IsNullOrWhiteSpace(cfg.ServerUrl)) { _lastError = "ServerUrl vacío"; return false; }
            try
            {
                // El server OrbitX expone el health-check SIN prefijo /api
                // (server.js: `app.get("/health", ...)`). Cualquier /api/*
                // sin ruta cae en el catch-all que devuelve 404 JSON, que es
                // lo que veíamos en el botón "Probar conexión" del Hub.
                var url = cfg.ServerUrl.TrimEnd('/') + "/health";
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (!string.IsNullOrEmpty(cfg.DeviceId)) req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    var token = !string.IsNullOrEmpty(cfg.DeviceToken) ? cfg.DeviceToken : cfg.MasterToken;
                    if (!string.IsNullOrEmpty(token)) req.Headers.Add("X-Auth-Token", token);

                    using (var res = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (res.IsSuccessStatusCode) { _lastError = ""; return true; }
                        _lastError = "HTTP " + (int)res.StatusCode + " " + res.ReasonPhrase;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                _lastError = mapped.Code + " " + mapped.Friendly;
                return false;
            }
        }

        // -----------------------------------------------------------------

        private static OrbitXConfigDto Defaults() => new OrbitXConfigDto
        {
            Enabled = false,
            ServerUrl = FixedServerUrl,
            DeviceToken = "",
            MasterToken = "vx-device-token",
            DeviceId = GenerateDeviceId(),
            EstabSlug = "",
            SyncIntervalSec = 30,
            SyncAOG = true,
            SyncVistaX = true,
            SyncQuantiX = true,
            SyncSectionX = true,
            FirmwareMirrorEnabled = true,
            FirmwareCacheDir = "",
            FirmwareHttpPort = 8088,
            FirmwareSyncIntervalMin = 10,
            CamarasStreamingEnabled = false,
            CamarasRtspHost = "cam.agroparallel.com",
            CamarasRtspPort = 8554,
            CamarasFfmpegPath = "",
            LastSync = "",
            FilesSynced = 0
        };

        // ──────────────────────────────────────────────────────────────────
        //  PAIRING — flow táctil
        // ──────────────────────────────────────────────────────────────────

        public async Task<OrbitXPairInfo> GetPairInfoAsync()
        {
            var cfg = Load();
            var info = new OrbitXPairInfo
            {
                ServerUrl = cfg.ServerUrl,
                DeviceId  = cfg.DeviceId,
                EstabSlug = cfg.EstabSlug
            };

            // Caso 1 — ya vinculado: token presente y distinto del master.
            bool tokenOwn = !string.IsNullOrEmpty(cfg.DeviceToken)
                            && cfg.DeviceToken != cfg.MasterToken;
            if (tokenOwn)
            {
                info.Paired = true;
                info.Status = "ok";
                info.Hint   = string.IsNullOrEmpty(cfg.EstabSlug)
                    ? "Vinculado (sin establecimiento asignado)."
                    : "Vinculado a " + cfg.EstabSlug;
                // JustClaimed se devuelve UNA vez tras el claim.
                lock (_pairLock)
                {
                    if (_pairJustClaimedFlag)
                    {
                        info.JustClaimed = true;
                        _pairJustClaimedFlag = false;
                    }
                }
                return info;
            }

            // Caso 2 — sin vincular: generar/mantener código + secret en memoria.
            string code, secret;
            long ageSec;
            lock (_pairLock)
            {
                if (string.IsNullOrEmpty(_pairCode)
                    || (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pairBornUtcMs) > PairTtlSec * 1000L)
                {
                    _pairCode = GeneratePairCode();
                    _pairSecret = GenerateDeviceSecret();
                    _pairBornUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _pairInitSentOk = false;
                }
                code   = _pairCode;
                secret = _pairSecret;
                ageSec = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pairBornUtcMs) / 1000L;
            }
            info.Code         = code;
            info.ExpiresInSec = Math.Max(0, PairTtlSec - (int)ageSec);

            if (string.IsNullOrWhiteSpace(cfg.ServerUrl))
            {
                info.Status        = "offline";
                info.Hint          = "ServerUrl vacío en orbitX.json.";
                info.ErrorCode     = "AGP-NET-001";
                info.HintTechnical = "config.server_url is empty";
                return info;
            }

            // Mandar /pair/init una vez (idempotente en cloud, pero evitamos
            // tráfico innecesario). Si falla, lo reintentamos en el próximo poll.
            if (!_pairInitSentOk)
            {
                try
                {
                    var initPayload = new
                    {
                        code = code,
                        device_secret = secret,
                        device_id = cfg.DeviceId,
                        hostname = Environment.MachineName,
                        version = "tractor"
                    };
                    var initJson = JsonSerializer.Serialize(initPayload);
                    using (var req = new HttpRequestMessage(HttpMethod.Post,
                        cfg.ServerUrl.TrimEnd('/') + "/api/devices/pair/init"))
                    {
                        req.Content = new StringContent(initJson, Encoding.UTF8, "application/json");
                        using (var res = await _http.SendAsync(req).ConfigureAwait(false))
                        {
                            if (res.IsSuccessStatusCode) { _pairInitSentOk = true; }
                            else
                            {
                                info.Status        = "offline";
                                info.Hint          = "No se pudo anunciar el código al cloud. Reintentando…";
                                info.ErrorCode     = "AGP-NET-001";
                                info.HintTechnical = "POST /api/devices/pair/init → HTTP "
                                                     + (int)res.StatusCode + " " + res.ReasonPhrase;
                                return info;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    var mapped = AgpErrorMapper.FromException(ex);
                    info.Status        = "offline";
                    info.Hint          = mapped.Friendly;
                    info.ErrorCode     = mapped.Code;
                    info.HintTechnical = mapped.Technical;
                    return info;
                }
            }

            // Poll /pair/status/:code?secret=...
            try
            {
                var url = cfg.ServerUrl.TrimEnd('/')
                          + "/api/devices/pair/status/" + Uri.EscapeDataString(code)
                          + "?secret=" + Uri.EscapeDataString(secret);
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var res = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (res.StatusCode == (System.Net.HttpStatusCode)410
                        || res.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Código expirado en el cloud — regenerar local.
                        lock (_pairLock) { _pairCode = null; _pairInitSentOk = false; }
                        info.Status = "expired";
                        info.Hint   = "El código expiró. Generando uno nuevo…";
                        return info;
                    }
                    if (!res.IsSuccessStatusCode)
                    {
                        info.Status = "pending";
                        info.Hint   = "Esperando…  (HTTP " + (int)res.StatusCode + ")";
                        return info;
                    }

                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var parsed = JsonSerializer.Deserialize<PairStatusReply>(body, opts);
                    if (parsed != null && parsed.Status == "claimed"
                        && !string.IsNullOrEmpty(parsed.Token))
                    {
                        // Persistir el token + estab_slug en orbitX.json.
                        var disk = Load();
                        disk.DeviceToken = parsed.Token;
                        if (!string.IsNullOrEmpty(parsed.EstabSlug)) disk.EstabSlug = parsed.EstabSlug;
                        if (!string.IsNullOrEmpty(parsed.DeviceId))  disk.DeviceId  = parsed.DeviceId;
                        // Habilitar sync por default tras claim — el operario lo
                        // puede apagar si quiere, pero la expectativa de vincular
                        // ES poder sincronizar.
                        disk.Enabled = true;
                        Save(disk);

                        lock (_pairLock)
                        {
                            _pairCode = null;
                            _pairSecret = null;
                            _pairInitSentOk = false;
                            _pairJustClaimedFlag = true;
                        }
                        info.Paired      = true;
                        info.JustClaimed = true;
                        info.Code        = null;
                        info.EstabSlug   = disk.EstabSlug;
                        info.DeviceId    = disk.DeviceId;
                        info.Status      = "claimed";
                        info.Hint        = "✓ Vinculado a " + (disk.EstabSlug ?? "—");
                        return info;
                    }
                    info.Status = "pending";
                    info.Hint   = "Esperando que un operario en OrbitX confirme el código.";
                    return info;
                }
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                info.Status        = "offline";
                info.Hint          = mapped.Friendly;
                info.ErrorCode     = mapped.Code;
                info.HintTechnical = mapped.Technical;
                return info;
            }
        }

        public void ResetPairing()
        {
            var cfg = Load();
            cfg.DeviceToken = "";
            cfg.EstabSlug = "";
            cfg.Enabled = false;
            Save(cfg);
            lock (_pairLock)
            {
                _pairCode = null;
                _pairSecret = null;
                _pairInitSentOk = false;
                _pairJustClaimedFlag = false;
            }
        }

        private static string GeneratePairCode()
        {
            var rng = RandomNumberGenerator.Create();
            byte[] buf = new byte[6];
            rng.GetBytes(buf);
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++) sb.Append(PairAlphabet[buf[i] % PairAlphabet.Length]);
            return sb.ToString();
        }

        private static string GenerateDeviceSecret()
        {
            var rng = RandomNumberGenerator.Create();
            byte[] buf = new byte[32];
            rng.GetBytes(buf);
            return BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant();
        }

        private sealed class PairStatusReply
        {
            [System.Text.Json.Serialization.JsonPropertyName("status")]
            public string Status { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("device_id")]
            public string DeviceId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("token")]
            public string Token { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("estab_slug")]
            public string EstabSlug { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("nombre")]
            public string Nombre { get; set; }
        }

        private static string GenerateDeviceId()
        {
            try
            {
                using (var md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(Environment.MachineName + Environment.UserName));
                    return "OX-" + BitConverter.ToString(hash).Replace("-", "").Substring(0, 12).ToUpperInvariant();
                }
            }
            catch { return "OX-" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant(); }
        }
    }
}
