// ICoreXBridgeService — puente de solo-lectura entre el Hub y CoreX (AgIO),
// el proceso sidecar que corre siempre en 127.0.0.1:5181 y habla con los
// módulos físicos (GPS, IMU, Machine, Steer) por MQTT/serial. A diferencia de
// ICoreXEcuService (firmware Teensy en una IP de LAN configurable), acá no
// hay config que persistir: el puerto es fijo.

using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ICoreXBridgeService
    {
        /// <summary>GET /api/corex/status de CoreX, resumido a lo que necesita la
        /// tira de estado del Hub. Ok=false si CoreX no contestó (no está
        /// corriendo, todavía no levantó, o timeout).</summary>
        Task<CoreXBridgeStatusDto> GetStatusAsync();
    }
}
