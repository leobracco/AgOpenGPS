// ImuCalibracionDtos.cs — snapshot de la calibración de roll del IMU interno
// de PilotX (clase CAHRS / campo FormGPS.ahrs). No confundir con el IMU del
// firmware CoreX-ECU (BNO085, filtro EMA en /api/corex-ecu/params) ni con el
// IMU del módulo CoreX/AgIO (solo lectura, pill de presencia en el Hub) — este
// es el "Zero Roll" nativo que antes solo existía en la solapa Roll del
// asistente de dirección de AgOpenGPS WinForms.

using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class ImuCalibracionSnapshot
    {
        /// <summary>False mientras no llegó un dato válido de IMU todavía
        /// (sentinels imuRoll==88888 / imuHeading==99999 de CAHRS).</summary>
        [JsonPropertyName("present")] public bool Present { get; set; }

        [JsonPropertyName("imu_heading")]    public double ImuHeading { get; set; }
        [JsonPropertyName("imu_roll")]       public double ImuRoll { get; set; }
        [JsonPropertyName("roll_zero")]      public double RollZero { get; set; }
        [JsonPropertyName("roll_filter")]    public double RollFilter { get; set; }
        [JsonPropertyName("is_roll_invert")] public bool IsRollInvert { get; set; }
    }
}
