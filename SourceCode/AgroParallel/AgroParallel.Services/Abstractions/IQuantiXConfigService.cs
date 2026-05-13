// ============================================================================
// IQuantiXConfigService — fachada del módulo QuantiX para la UI HTML.
//   * Motores: lectura/escritura de quantiX_motores.json + envío MQTT de
//     config por nodo.
//   * UDP: lectura/escritura de quantiX.json (salida UDP de dosis global).
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

        QxUdpConfigDto GetUdp();
        void SaveUdp(QxUdpConfigDto dto);

        /// <summary>Publica agp/quantix/{uid}/config con los PIDs/PWMs/MeterCal del nodo.</summary>
        Task<bool> SendNodoConfigAsync(string uid);

        /// <summary>Publica agp/quantix/{uid}/{verb} con payload JSON arbitrario.</summary>
        Task<bool> SendCmdAsync(string uid, string verb, string payload, bool retain);
    }
}
