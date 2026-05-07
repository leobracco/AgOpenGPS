// ============================================================================
// OrbitXConfig.cs - Configuración de conexión a OrbitX Cloud
// ============================================================================

using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.OrbitX
{
    public class OrbitXConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("server_url")]
        public string ServerUrl { get; set; }

        [JsonPropertyName("device_token")]
        public string DeviceToken { get; set; }

        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; }

        [JsonPropertyName("estab_slug")]
        public string EstabSlug { get; set; }

        // Intervalo de sync en segundos.
        [JsonPropertyName("sync_interval_sec")]
        public int SyncIntervalSec { get; set; }

        // Qué módulos sincronizar.
        [JsonPropertyName("sync_aog")]
        public bool SyncAOG { get; set; }

        [JsonPropertyName("sync_vistax")]
        public bool SyncVistaX { get; set; }

        [JsonPropertyName("sync_quantix")]
        public bool SyncQuantiX { get; set; }

        [JsonPropertyName("sync_sectionx")]
        public bool SyncSectionX { get; set; }

        // ── Firmware OTA mirror (LAN) ────────────────────────────────────
        // Espejo local de firmwares descargados de OrbitX cloud + servidor
        // HTTP para distribuirlos a los nodos ESP32 (QuantiX, VistaX, etc.)
        // que están detrás del PC con AOG en LAN.

        [JsonPropertyName("firmware_mirror_enabled")]
        public bool FirmwareMirrorEnabled { get; set; }

        [JsonPropertyName("firmware_cache_dir")]
        public string FirmwareCacheDir { get; set; }

        [JsonPropertyName("firmware_http_port")]
        public int FirmwareHttpPort { get; set; }

        [JsonPropertyName("firmware_sync_interval_min")]
        public int FirmwareSyncIntervalMin { get; set; }

        // ── Cámaras: streaming remoto (push RTSP a MediaMTX en el server) ───
        [JsonPropertyName("camaras_streaming_enabled")]
        public bool CamarasStreamingEnabled { get; set; }

        // Host del MediaMTX (default: derivado de ServerUrl). Útil si MediaMTX
        // está en otro droplet o detrás de otro DNS (ej cam.agroparallel.com).
        [JsonPropertyName("camaras_rtsp_host")]
        public string CamarasRtspHost { get; set; }

        [JsonPropertyName("camaras_rtsp_port")]
        public int CamarasRtspPort { get; set; }

        // Path al ffmpeg.exe — vacío = busca ffmpeg en PATH o en App folder.
        [JsonPropertyName("camaras_ffmpeg_path")]
        public string CamarasFfmpegPath { get; set; }

        // Estado.
        [JsonPropertyName("last_sync")]
        public string LastSync { get; set; }

        [JsonPropertyName("files_synced")]
        public int FilesSynced { get; set; }

        public OrbitXConfig()
        {
            Enabled = false;
            ServerUrl = "https://orbitx.agroparallel.com";
            DeviceToken = "";
            DeviceId = GenerateDeviceId();
            EstabSlug = "";
            SyncIntervalSec = 30;
            SyncAOG = true;
            SyncVistaX = true;
            SyncQuantiX = true;
            SyncSectionX = true;
            FirmwareMirrorEnabled = true;
            FirmwareCacheDir = "";  // vacío → <AOG>/firmware-cache
            FirmwareHttpPort = 8088;
            FirmwareSyncIntervalMin = 10;
            CamarasStreamingEnabled = false;
            CamarasRtspHost = "";
            CamarasRtspPort = 8554;
            CamarasFfmpegPath = "";
            LastSync = "";
            FilesSynced = 0;
        }

        private static readonly string FileName = "orbitX.json";

        public static OrbitXConfig Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                var def = new OrbitXConfig();
                def.Save();
                return def;
            }
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cfg = JsonSerializer.Deserialize<OrbitXConfig>(File.ReadAllText(path), opts);
                if (cfg != null && string.IsNullOrEmpty(cfg.DeviceId))
                    cfg.DeviceId = GenerateDeviceId();
                return cfg ?? new OrbitXConfig();
            }
            catch { return new OrbitXConfig(); }
        }

        public void Save()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
        }

        // Persiste sólo campos que actualiza OrbitXSync (LastSync, FilesSynced,
        // EstabSlug) sin pisar cambios que el usuario haya hecho desde otras
        // pantallas (ej. CamarasStreamingEnabled en FormCamarasConfig).
        // Solución al bug: el sync corría cada 30s y reescribía el JSON entero
        // con su instancia cacheada, revirtiendo flags recién guardados.
        public static void SaveRuntimeFields(string lastSync, int filesSynced, string estabSlug)
        {
            try
            {
                var disk = Load();
                if (lastSync != null) disk.LastSync = lastSync;
                disk.FilesSynced = filesSynced;
                if (!string.IsNullOrEmpty(estabSlug)) disk.EstabSlug = estabSlug;
                disk.Save();
            }
            catch { }
        }

        /// Genera un Device ID único basado en el MAC address (compatible con OrbitX-Sync).
        private static string GenerateDeviceId()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == OperationalStatus.Up
                        && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        string mac = nic.GetPhysicalAddress().ToString();
                        if (!string.IsNullOrEmpty(mac) && mac.Length >= 12)
                        {
                            using (var md5 = MD5.Create())
                            {
                                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(mac));
                                return "OX-" + BitConverter.ToString(hash)
                                    .Replace("-", "").Substring(0, 12).ToUpperInvariant();
                            }
                        }
                    }
                }
            }
            catch { }
            return "OX-" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
        }
    }
}
