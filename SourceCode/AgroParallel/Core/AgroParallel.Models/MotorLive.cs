// MotorLive — telemetría en vivo de un motor QuantiX recibida por MQTT.
// Topic: agp/quantix/{UID}/status_live
// Payload típico: {"id":0,"pps_target":12.3,"pps_real":11.9,"pwm":2700,"rpm":85,"pulsos":1234567}

using System;

namespace AgroParallel.Models
{
    /// <summary>Estado runtime de un motor QuantiX (id 0..N).</summary>
    public sealed class MotorLive
    {
        public int Id { get; set; }
        public double PpsTarget { get; set; }
        public double PpsReal { get; set; }
        public int Pwm { get; set; }
        public int Rpm { get; set; }
        public long Pulsos { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }
}
