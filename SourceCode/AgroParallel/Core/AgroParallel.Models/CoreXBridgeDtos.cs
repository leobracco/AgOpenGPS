// CoreXBridgeDtos.cs — snapshot resumido de GET /api/corex/status (CoreX,
// servicio CoreX en 127.0.0.1:5181) para la tira de estado del Hub. Solo los
// campos que necesitan las pills: si CoreX contestó, GPS vivo, y para
// steer/machine/imu si el módulo está configurado y si mandó "hello" (nodo
// presente y respondiendo). No confundir con CoreXEcuStatusDto (ese es el
// firmware Teensy de autosteer, otra IP, otro puerto, otro proyecto).

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class CoreXBridgeStatusDto
    {
        [JsonPropertyName("ok")]         public bool Ok { get; set; }
        [JsonPropertyName("error_code")] public string ErrorCode { get; set; }
        [JsonPropertyName("error")]      public string Error { get; set; }

        [JsonPropertyName("gps_alive")] public bool GpsAlive { get; set; }

        [JsonPropertyName("steer_configured")]   public bool SteerConfigured { get; set; }
        [JsonPropertyName("steer_hello")]        public bool SteerHello { get; set; }
        [JsonPropertyName("machine_configured")] public bool MachineConfigured { get; set; }
        [JsonPropertyName("machine_hello")]      public bool MachineHello { get; set; }
        [JsonPropertyName("imu_configured")]     public bool ImuConfigured { get; set; }
        [JsonPropertyName("imu_hello")]          public bool ImuHello { get; set; }

        public CoreXBridgeStatusDto()
        {
            ErrorCode = "";
            Error = "";
        }
    }
}
