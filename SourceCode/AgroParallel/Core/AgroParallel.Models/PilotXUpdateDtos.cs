// ============================================================================
// PilotXUpdateDtos.cs
// DTOs para el auto-update del propio PilotX (la app de PC).
// El cloud OrbitX lista "PilotX" como un producto más del catálogo OTA,
// pero el archivo no es un .bin de ESP32 — es un ZIP con el build completo.
// El cliente:
//   1. CheckAsync  → consulta /api/ota/catalogo?producto=PilotX
//   2. DownloadAsync → baja ZIP a <install>/AgroParallel/Updates/<ver>/
//   3. ApplyAsync   → lanza Updater.exe (espera PilotX, unzipea, relanza)
// ============================================================================

namespace AgroParallel.Models
{
    /// <summary>Estados de la máquina de auto-update.</summary>
    public enum PilotXUpdatePhase
    {
        Idle = 0,
        Checking = 1,
        UpdateAvailable = 2,
        Downloading = 3,
        ReadyToApply = 4,
        Applying = 5,
        Error = 9
    }

    public sealed class PilotXUpdateStatus
    {
        public PilotXUpdatePhase Phase { get; set; }
        public string CurrentVersion { get; set; }
        public string AvailableVersion { get; set; }
        public string Changelog { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        /// <summary>0..100 durante Downloading. -1 si no aplica.</summary>
        public int ProgressPct { get; set; }
        public string LastError { get; set; }
        public long LastCheckUnixMs { get; set; }
        /// <summary>True si el ZIP ya está descargado y verificado en staging.</summary>
        public bool StagingReady { get; set; }
    }
}
