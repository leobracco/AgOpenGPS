// ============================================================================
// IFlowXLiveService.cs — agregador de telemetría FlowX en vivo.
// Se suscribe (vía NodoRegistryService) a agp/flow/+/status_live y mantiene
// un snapshot consolidado por nodo (caudal real, PWM, estado PID).
//
// La UI consume vía GET /api/flowx/live — no toca MQTT directo.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IFlowXLiveService
    {
        /// <summary>Arranca la suscripción.</summary>
        void Start();

        /// <summary>Detiene el agregador.</summary>
        void Stop();

        /// <summary>Snapshot consolidado para enviar al cliente HTTP.</summary>
        FlowXLiveSnapshotDto GetSnapshot();

        /// <summary>True si está consumiendo telemetría activamente.</summary>
        bool IsRunning { get; }

        /// <summary>Recarga config (llamar tras un PUT a flowX.json).</summary>
        void Reload();

        /// <summary>
        /// Último resultado de auto-tune PID recibido en agp/flow/{uid}/autotune_result.
        /// Devuelve null si todavía no llegó nada. Se descarta cuando la UI
        /// hace POST de un nuevo cmd autotune_start.
        /// </summary>
        FxTuneResultDto GetAutoTuneResult(string uid);

        /// <summary>
        /// Último resultado de calibración recibido en agp/flow/{uid}/calibrar_result.
        /// Devuelve null si todavía no llegó nada.
        /// </summary>
        FxCalibrarResultDto GetCalibrarResult(string uid);

        /// <summary>Limpia el resultado cacheado de auto-tune para forzar otra ronda.</summary>
        void ClearAutoTuneResult(string uid);

        /// <summary>Limpia el resultado cacheado de calibración.</summary>
        void ClearCalibrarResult(string uid);

        /// <summary>
        /// Último payload crudo recibido en agp/flow/{uid}/caracterizar_result.
        /// Contiene la curva PWM/Hz, pwm_min y hz_max. Devuelve null si todavía
        /// no llegó nada.
        /// </summary>
        string GetCaracterizarResultRaw(string uid);

        /// <summary>Limpia el resultado cacheado de caracterización.</summary>
        void ClearCaracterizarResult(string uid);
    }
}
