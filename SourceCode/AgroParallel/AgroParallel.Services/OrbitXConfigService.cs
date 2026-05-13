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
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        private string _lastError = "";

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
                return cfg;
            }
            catch { return Defaults(); }
        }

        public void Save(OrbitXConfigDto dto)
        {
            if (dto == null) return;
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
                var url = cfg.ServerUrl.TrimEnd('/') + "/api/health";
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
            catch (Exception ex) { _lastError = ex.Message; return false; }
        }

        // -----------------------------------------------------------------

        private static OrbitXConfigDto Defaults() => new OrbitXConfigDto
        {
            Enabled = false,
            ServerUrl = "https://orbitx.agroparallel.com",
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
