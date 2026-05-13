// OrbitXConfigDto — DTO de la config de OrbitX cloud (orbitX.json).
// Idéntico en shape al OrbitXConfig legacy, sin atributos UI ni lógica.

namespace AgroParallel.Models
{
    public sealed class OrbitXConfigDto
    {
        public bool Enabled { get; set; }
        public string ServerUrl { get; set; }
        public string DeviceToken { get; set; }
        public string MasterToken { get; set; }
        public string DeviceId { get; set; }
        public string EstabSlug { get; set; }

        public int SyncIntervalSec { get; set; }
        public bool SyncAOG { get; set; }
        public bool SyncVistaX { get; set; }
        public bool SyncQuantiX { get; set; }
        public bool SyncSectionX { get; set; }

        public bool FirmwareMirrorEnabled { get; set; }
        public string FirmwareCacheDir { get; set; }
        public int FirmwareHttpPort { get; set; }
        public int FirmwareSyncIntervalMin { get; set; }

        public bool CamarasStreamingEnabled { get; set; }
        public string CamarasRtspHost { get; set; }
        public int CamarasRtspPort { get; set; }
        public string CamarasFfmpegPath { get; set; }

        public string LastSync { get; set; }
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
}
