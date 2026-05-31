// OrbitXConfigDto — DTO de la config de OrbitX cloud (orbitX.json).
// Idéntico en shape al OrbitXConfig legacy, sin atributos UI ni lógica.
//
// IMPORTANTE: el JSON en disco usa snake_case (legacy + lo que escribe el
// service actual). PropertyNameCaseInsensitive=true SOLO ignora mayúsculas,
// NO ignora underscores — así que sin estos [JsonPropertyName] el deserialize
// perdía device_token, server_url, firmware_mirror_enabled, etc. al cargar.

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class OrbitXConfigDto
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
        [JsonPropertyName("server_url")]
        public string ServerUrl { get; set; }
        [JsonPropertyName("device_token")]
        public string DeviceToken { get; set; }
        [JsonPropertyName("master_token")]
        public string MasterToken { get; set; }
        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; }
        [JsonPropertyName("estab_slug")]
        public string EstabSlug { get; set; }

        [JsonPropertyName("sync_interval_sec")]
        public int SyncIntervalSec { get; set; }
        [JsonPropertyName("sync_aog")]
        public bool SyncAOG { get; set; }
        [JsonPropertyName("sync_vistax")]
        public bool SyncVistaX { get; set; }
        [JsonPropertyName("sync_quantix")]
        public bool SyncQuantiX { get; set; }
        [JsonPropertyName("sync_sectionx")]
        public bool SyncSectionX { get; set; }

        [JsonPropertyName("firmware_mirror_enabled")]
        public bool FirmwareMirrorEnabled { get; set; }
        [JsonPropertyName("firmware_cache_dir")]
        public string FirmwareCacheDir { get; set; }
        [JsonPropertyName("firmware_http_port")]
        public int FirmwareHttpPort { get; set; }
        [JsonPropertyName("firmware_sync_interval_min")]
        public int FirmwareSyncIntervalMin { get; set; }

        [JsonPropertyName("camaras_streaming_enabled")]
        public bool CamarasStreamingEnabled { get; set; }
        [JsonPropertyName("camaras_rtsp_host")]
        public string CamarasRtspHost { get; set; }
        [JsonPropertyName("camaras_rtsp_port")]
        public int CamarasRtspPort { get; set; }
        [JsonPropertyName("camaras_ffmpeg_path")]
        public string CamarasFfmpegPath { get; set; }

        [JsonPropertyName("last_sync")]
        public string LastSync { get; set; }
        [JsonPropertyName("files_synced")]
        public int FilesSynced { get; set; }
    }

    /// <summary>Snapshot de runtime para el dashboard de OrbitX.</summary>
    public sealed class OrbitXStatus
    {
        public bool Enabled { get; set; }
        public bool CloudConnected { get; set; }
        public string LastError { get; set; }
        public string LastSync { get; set; }
        public int FilesSynced { get; set; }
        public string EstabSlug { get; set; }
        public string DeviceId { get; set; }
    }

    /// <summary>
    /// Estado del flow de vinculación táctil. La UI HTML pollea este endpoint
    /// cada ~4s y muestra <see cref="Code"/> en pantalla mientras
    /// <see cref="Paired"/> sea false. Al pasar a true (operario confirmó en
    /// el panel cloud), JustClaimed queda en true una sola vez para que la UI
    /// dispare un toast de éxito.
    /// </summary>
    public sealed class OrbitXPairInfo
    {
        public bool Paired { get; set; }
        public bool JustClaimed { get; set; }
        public string Code { get; set; }
        public int ExpiresInSec { get; set; }
        public string DeviceId { get; set; }
        public string EstabSlug { get; set; }
        public string ServerUrl { get; set; }
        public string Status { get; set; }   // "pending"|"claimed"|"expired"|"offline"|"ok"
        public string Hint { get; set; }

        // Cuando Status == "offline" (o cualquier otra falla observable),
        // ErrorCode trae el código AGP-* que el operario puede dictar al
        // bot de soporte por WhatsApp, y HintTechnical guarda el detalle
        // crudo de la excepción para mostrar en un <details> colapsado.
        // Si todo está bien, ambos quedan null/vacíos.
        public string ErrorCode { get; set; }
        public string HintTechnical { get; set; }
    }
}
