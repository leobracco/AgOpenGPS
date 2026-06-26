// ============================================================================
// QuantiXDtos.cs — POCOs para el módulo QuantiX (UI HTML).
//
//   QxMotoresConfigDto  → quantiX_motores.json  (trenes + nodos + motores)
//   QxTrenConfigDto     → un tren físico del implemento
//
// Shape espejo del legacy AgroParallel.QuantiX.* — los [JsonPropertyName]
// con snake_case son para compatibilidad byte-idéntica con el firmware
// y los archivos JSON existentes.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class QxMotorConfigDto
    {
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "Motor";

        [JsonPropertyName("dosis_fija")]
        public double DosisFija { get; set; }

        // Unidad de la dosis de ESTE motor: "kg_ha" (masa por hectárea, default)
        // o "sem_m" (semillas por metro de surco). Cambia la rama de cálculo de pps.
        [JsonPropertyName("unidad_dosis")]
        public string UnidadDosis { get; set; } = "kg_ha";

        // Calibración para unidad "sem_m": semillas que entrega el dosificador por
        // vuelta. Con dientes_engranaje (pulsos por vuelta) da semillas por pulso.
        [JsonPropertyName("semillas_vuelta")]
        public double SemillasVuelta { get; set; }

        [JsonPropertyName("campo_dosis")]
        public string CampoDosis { get; set; } = "";

        [JsonPropertyName("kp")]
        public double Kp { get; set; } = 80;

        [JsonPropertyName("ki")]
        public double Ki { get; set; } = 30;

        [JsonPropertyName("kd")]
        public double Kd { get; set; }

        [JsonPropertyName("pwm_min")]
        public int PwmMin { get; set; } = 600;

        [JsonPropertyName("pwm_max")]
        public int PwmMax { get; set; } = 4095;

        [JsonPropertyName("meter_cal")]
        public double MeterCal { get; set; } = 50;

        [JsonPropertyName("max_integral")]
        public double MaxIntegral { get; set; } = 1200;

        [JsonPropertyName("deadband")]
        public int Deadband { get; set; } = 2;

        [JsonPropertyName("slew_rate")]
        public int SlewRate { get; set; } = 40;

        [JsonPropertyName("dientes_engranaje")]
        public int DientesEngranaje { get; set; } = 20;

        [JsonPropertyName("motor_type")]
        public int MotorType { get; set; }

        [JsonPropertyName("max_hz")]
        public double MaxHz { get; set; } = 40;

        [JsonPropertyName("ff_gain")]
        public double FFGain { get; set; } = 1.0;

        [JsonPropertyName("alpha")]
        public double Alpha { get; set; } = 0.4;

        [JsonPropertyName("slew_rate_per_sec")]
        public double SlewRatePerSec { get; set; } = 5000;

        [JsonPropertyName("pid_time")]
        public int PIDTime { get; set; } = 50;

        [JsonPropertyName("cortes")]
        public List<int> Cortes { get; set; } = new List<int>();

        [JsonPropertyName("tren")]
        public int Tren { get; set; }
    }

    public sealed class QxNodoConfigDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; } = "";

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "Nodo QuantiX";

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; } = true;

        [JsonPropertyName("distancia_entre_trenes")]
        public double DistanciaEntreTrenes { get; set; }

        [JsonPropertyName("motores")]
        public QxMotorConfigDto[] Motores { get; set; }
    }

    /// <summary>
    /// Tren físico de siembra. Hoy la UI deja al operario nombrar N trenes con
    /// su distancia (m) respecto al tren 0 (= eje del implemento). El bridge
    /// legacy (QuantiXMotorBridge) sólo distingue tren 0 vs ≥1 — la geometría
    /// completa por tren queda como fase futura.
    /// </summary>
    public sealed class QxTrenConfigDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        /// <summary>Distancia (metros) hacia atrás respecto al tren 0.</summary>
        [JsonPropertyName("distancia_m")]
        public double DistanciaM { get; set; }
    }

    public sealed class QxMotoresConfigDto
    {
        /// <summary>Trenes físicos del implemento. El service siembra
        /// [Delantero 0m, Trasero 2m] cuando el archivo no trae esta sección.</summary>
        [JsonPropertyName("trenes")]
        public List<QxTrenConfigDto> Trenes { get; set; } = new List<QxTrenConfigDto>();

        [JsonPropertyName("nodos")]
        public List<QxNodoConfigDto> Nodos { get; set; } = new List<QxNodoConfigDto>();

        [JsonPropertyName("ignorados")]
        public List<string> Ignorados { get; set; } = new List<string>();
    }

}
