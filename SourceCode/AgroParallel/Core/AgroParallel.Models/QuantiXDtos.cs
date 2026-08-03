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

        // Un nodo tiene N canales de motor pero no siempre están todos cableados
        // (el de 2 motores puede llevar uno solo; el de 7, los que hagan falta).
        // Un canal sin motor recibía consigna igual y el PID se saturaba contra
        // la nada: PWM 4095 permanente y telemetría basura. Deshabilitado se le
        // publica pps=0/seccion_on=false, así queda en PWM 0 sin dejar de
        // refrescar CommTime (que es lo que CheckRelays mira para no cortar).
        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; } = true;

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

        /// <summary>Qué mide las vueltas del motor: "inductivo" (sensor de
        /// pocos pulsos por vuelta, el clásico) o "encoder" (LPD3806 y
        /// similares, cientos de pulsos por vuelta). Define el filtro
        /// antirrebote y el PPR típico — ver <see cref="PulseMinPara"/>.</summary>
        ///
        /// Arranca en null a propósito: las configs viejas no traen el campo y
        /// defaultearlas a "inductivo" etiquetaría mal a los motores que ya
        /// tienen encoder. Con null la UI lo deduce del PPR guardado.
        [JsonPropertyName("sensor_tipo")]
        public string SensorTipo { get; set; }

        /// <summary>Filtro antirrebote del ISR del nodo (µs mínimos entre
        /// pulsos). El default 2000 sirve para sensores inductivos de pocos
        /// pulsos/vuelta; con encoders de 600 ppr (LPD3806) usar ~100.</summary>
        [JsonPropertyName("pulse_min")]
        public int PulseMin { get; set; } = 2000;

        /// <summary>Filtro recomendado según el tipo de sensor.</summary>
        public static int PulseMinPara(string sensorTipo)
        {
            return string.Equals(sensorTipo, "encoder", System.StringComparison.OrdinalIgnoreCase)
                ? 100 : 2000;
        }

        /// <summary>PPR típico de cada tipo, para precargar el formulario.</summary>
        public static int PprPara(string sensorTipo)
        {
            return string.Equals(sensorTipo, "encoder", System.StringComparison.OrdinalIgnoreCase)
                ? 600 : 20;
        }

        /// <summary>Filtro máximo tolerable para un PPR dado.
        ///
        /// El ISR del nodo descarta cualquier pulso que llegue antes de
        /// `pulse_min` µs del anterior, así que el filtro impone un techo de
        /// lectura: 1e6/pulse_min pulsos por segundo. Pasado ese techo el nodo
        /// no "se queda en el máximo": empieza a contar uno de cada dos y
        /// reporta LA MITAD de las vueltas reales — el PID lee de menos y
        /// dosifica de más, sin ningún error a la vista.
        ///
        /// Con 600 ppr y el default de 2000 µs el techo eran 50 rpm: arriba de
        /// eso la lectura se partía al medio (medido en banco 2026-08-01).
        /// Acá dejamos margen hasta 300 rpm de motor.</summary>
        public static int PulseMinMaximo(int ppr)
        {
            if (ppr <= 0) return 2000;
            int techo = 200000 / ppr;          // 1e6 / (ppr * 300rpm/60)
            return techo < 20 ? 20 : techo;    // nunca menos de 20 µs
        }

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

        // Rampa de CONSIGNA en Hz/s (distinta del slew de PWM de arriba).
        // El default histórico del firmware era 50, que a 455 pps tarda 9 s en
        // llegar al target: la pasada arrancaba con ~35% menos de semilla.
        [JsonPropertyName("target_slew_hz_per_sec")]
        public double TargetSlewHzPerSec { get; set; } = 300;

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
