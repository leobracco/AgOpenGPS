// ============================================================================
// QuantiXMotoresConfig.cs
// POCOs + persistencia del archivo `quantiX_motores.json` consumidos por
// QuantiXMotorBridge. Extraído del FormQuantiXMotores legacy al migrar la UI
// a HTML (WebView2) — la UI vive ahora en AgroParallel/Web/AgroParallel.WebUI.
//
// Modelo:
//   - Un NODO QuantiX = ESP32 con UID único (MAC).
//   - Cada nodo tiene 2 MOTORES (M0/M1).
//   - Cada motor controla hasta 7 cortes (secciones PilotX).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.QuantiX
{
    // Un motor dentro de un nodo (M0 o M1).
    public class QxMotorConfig
    {
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        // Dosis fija (kg/ha o L/ha). Si > 0, se usa cuando NO hay mapa.
        // Esta es la dosis "fuera de mapa" configurada en Hub → QuantiX.
        [JsonPropertyName("dosis_fija")]
        public double DosisFija { get; set; }

        // Modo manual runtime (toggle MAN/AUTO en widget pantalla principal).
        // Si true, la dosis efectiva = ManualDosis (override total).
        [JsonPropertyName("manual_mode")]
        public bool ManualMode { get; set; }

        // Dosis en modo manual (kg/ha o L/ha). Sobrevive a toggles MAN→AUTO→MAN.
        [JsonPropertyName("manual_dosis")]
        public double ManualDosis { get; set; }

        // Campo del shapefile (DBF) que tiene la dosis para este motor.
        [JsonPropertyName("campo_dosis")]
        public string CampoDosis { get; set; }

        [JsonPropertyName("kp")]
        public double Kp { get; set; }

        [JsonPropertyName("ki")]
        public double Ki { get; set; }

        [JsonPropertyName("kd")]
        public double Kd { get; set; }

        [JsonPropertyName("pwm_min")]
        public int PwmMin { get; set; }

        [JsonPropertyName("pwm_max")]
        public int PwmMax { get; set; }

        [JsonPropertyName("meter_cal")]
        public double MeterCal { get; set; }

        [JsonPropertyName("max_integral")]
        public double MaxIntegral { get; set; }

        [JsonPropertyName("deadband")]
        public int Deadband { get; set; }

        [JsonPropertyName("slew_rate")]
        public int SlewRate { get; set; }

        // Dientes del engranaje donde lee el sensor (= pulsos por vuelta).
        [JsonPropertyName("dientes_engranaje")]
        public int DientesEngranaje { get; set; }

        // Tipo de motor: 0=Eléctrico, 1=Hidráulico
        [JsonPropertyName("motor_type")]
        public int MotorType { get; set; }

        // Feedforward: Hz medidos a PWM máximo
        [JsonPropertyName("max_hz")]
        public double MaxHz { get; set; }

        // Ganancia feedforward (default 1.0)
        [JsonPropertyName("ff_gain")]
        public double FFGain { get; set; }

        // Coeficiente filtro exponencial sensor (0.4 eléctrico, 0.2 hidráulico)
        [JsonPropertyName("alpha")]
        public double Alpha { get; set; }

        // Rampa PWM/segundo (independiente de PIDtime)
        [JsonPropertyName("slew_rate_per_sec")]
        public double SlewRatePerSec { get; set; }

        // Intervalo PID en ms (50 eléctrico, 200 hidráulico)
        [JsonPropertyName("pid_time")]
        public int PIDTime { get; set; }

        // Cortes (secciones PilotX) que controla este motor (1-based).
        [JsonPropertyName("cortes")]
        public List<int> Cortes { get; set; }

        // Tren físico al que pertenece el motor (0 = delantero, 1 = trasero).
        // Para sembradoras de doble tren con desfase entre filas.
        [JsonPropertyName("tren")]
        public int Tren { get; set; }

        public QxMotorConfig()
        {
            Nombre = "Motor";
            MotorType = 0;
            Kp = 80; Ki = 30; Kd = 0;
            PwmMin = 600; PwmMax = 4095;
            MeterCal = 50;
            MaxIntegral = 1200;
            Deadband = 2; SlewRate = 40;
            MaxHz = 40; FFGain = 1.0; Alpha = 0.4;
            SlewRatePerSec = 5000; PIDTime = 50;
            DientesEngranaje = 20;
            Cortes = new List<int>();
        }
    }

    // Un nodo ESP32 con 2 motores.
    public class QxNodoConfig
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; }

        [JsonPropertyName("motores")]
        public QxMotorConfig[] Motores { get; set; }

        // Distancia entre tren delantero y trasero (metros). 0 = sin doble tren.
        [JsonPropertyName("distancia_entre_trenes")]
        public double DistanciaEntreTrenes { get; set; }

        public QxNodoConfig()
        {
            Uid = "";
            Nombre = "Nodo QuantiX";
            Habilitado = true;
            Motores = new QxMotorConfig[]
            {
                new QxMotorConfig { Nombre = "Producto 1", Cortes = new List<int> { 1,2,3,4,5,6,7 } },
                new QxMotorConfig { Nombre = "Producto 2", Cortes = new List<int>() }
            };
        }
    }

    // Archivo de persistencia (quantiX_motores.json).
    public class MotoresConfig
    {
        [JsonPropertyName("nodos")]
        public List<QxNodoConfig> Nodos { get; set; }

        // UIDs ignorados (borrados manualmente — no volver a auto-registrar).
        [JsonPropertyName("ignorados")]
        public List<string> Ignorados { get; set; }

        public MotoresConfig()
        {
            Nodos = new List<QxNodoConfig>();
            Ignorados = new List<string>();
        }

        private static readonly string FileName = "quantiX_motores.json";

        public static MotoresConfig Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path)) return new MotoresConfig();
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<MotoresConfig>(File.ReadAllText(path), opts)
                    ?? new MotoresConfig();
            }
            catch { return new MotoresConfig(); }
        }

        public void Save()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
        }
    }
}
