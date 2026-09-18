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

        /// <summary>Motivo cuando <see cref="Ok"/> es false, tal cual lo manda
        /// el firmware ("El motor no giro…", "No se detectaron oscilaciones…").
        /// Se muestra al operario: un motor que no gira y uno que no oscila se
        /// resuelven distinto, y el mensaje genérico los tapaba a los dos.</summary>
        public string Msg { get; set; }

        public DateTime ReceivedUtc { get; set; }
    }
}
