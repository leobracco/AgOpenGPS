// AutoTuneResult — DTO con el último resultado de Auto-Tune PID recibido
// vía MQTT desde un nodo QuantiX.

using System;

namespace AgroParallel.Models
{
    /// <summary>
    /// Último resultado de Auto-Tune PID recibido para un nodo. Lo emite el
    /// firmware sobre el topic <c>agp/quantix/{uid}/autotune_result</c> cuando
    /// termina el procedimiento Ziegler-Nichols. La UI lo consume vía
    /// <c>GET /api/quantix/{uid}/autotune</c> después de disparar un autotune.
    /// </summary>
    public sealed class AutoTuneResult
    {
        public string Uid { get; set; }
        public int MotorId { get; set; }
        public bool Ok { get; set; }
        public double Kp { get; set; }
        public double Ki { get; set; }
        public double Kd { get; set; }
        public DateTime ReceivedUtc { get; set; }
    }
}
