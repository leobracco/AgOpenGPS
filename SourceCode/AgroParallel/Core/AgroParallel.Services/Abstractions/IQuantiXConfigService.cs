// ============================================================================
// IQuantiXConfigService — fachada del módulo QuantiX para la UI HTML.
//   * Motores: lectura/escritura de quantiX_motores.json + envío MQTT de
//     config por nodo.
//   * Comandos: cmd genérico (PID live-tune, calibración start/stop, etc.).
// La implementación vive en AgroParallel.Services; usa INodoRegistryService
// para publicar MQTT reutilizando la conexión existente.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IQuantiXConfigService
    {
        QxMotoresConfigDto GetMotores();
        void SaveMotores(QxMotoresConfigDto dto);

        /// <summary>Publica agp/quantix/{uid}/config con los PIDs/PWMs/MeterCal del nodo.</summary>
        Task<bool> SendNodoConfigAsync(string uid);

        /// <summary>Publica agp/quantix/{uid}/{verb} con payload JSON arbitrario.</summary>
        Task<bool> SendCmdAsync(string uid, string verb, string payload, bool retain);

        /// <summary>
        /// Último resultado de Auto-Tune recibido para el nodo, o null si no
        /// llegó ninguno desde el arranque. Lo cachea internamente al captar
        /// <c>agp/quantix/{uid}/autotune_result</c>. La UI lo consulta tras
        /// disparar un autotune para saber si converge.
        /// </summary>
        AutoTuneResult GetAutoTuneResult(string uid);
    }
}
