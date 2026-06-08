// ============================================================================
// FlowXConfig.cs - Configuración de FlowX (corte + dosis para pulverizadoras)
// Equivalente legacy de SectionXConfig: lee/escribe flowX.json con los
// mismos nombres snake_case que usan el DTO y el firmware ESP32.
// Pensado para ser usado por el futuro FlowXBridge.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.FlowX
{
    // Calibración y PID por "producto" (caudal de salida) dentro de un nodo.
    public class FxProducto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("nombre")] public string Nombre { get; set; }

        // Pulsos/L del caudalímetro.
        [JsonPropertyName("meter_cal")] public double MeterCal { get; set; }

        // Dosis objetivo en L/ha — se convierte a L/min con la velocidad y el
        // ancho de barra antes de empujar al nodo por MQTT.
        [JsonPropertyName("dosis_lha")] public double DosisLha { get; set; }

        [JsonPropertyName("pwm_min")] public int PwmMin { get; set; }
        // Techo del PID (0..4095). 0 ó ≤ PwmMin = sin techo extra (firmware usa 4095).
        [JsonPropertyName("pwm_max")] public int PwmMax { get; set; }
        [JsonPropertyName("kp")] public double Kp { get; set; }
        [JsonPropertyName("ki")] public double Ki { get; set; }
        [JsonPropertyName("kd")] public double Kd { get; set; }

        // Modo manual: target L/min fijo (ManualLmin) independiente de la
        // velocidad. En automático (false) se calcula con dosis_lha·vel·ancho/600.
        [JsonPropertyName("modo_manual")] public bool ModoManual { get; set; }
        [JsonPropertyName("manual_lmin")] public double ManualLmin { get; set; }

        // Pasos de ajuste de los botones −/+ del widget (por modo).
        [JsonPropertyName("paso_lha")] public double PasoLha { get; set; }
        [JsonPropertyName("paso_lmin")] public double PasoLmin { get; set; }

        // Tipo de reguladora: "valvula" (electroválvula motorizada) o "motor".
        // Fase 1: persiste pero el firmware aún no lo honra (Fase 2).
        [JsonPropertyName("tipo")] public string Tipo { get; set; }
        // Cuál de los 2 caudalímetros del nodo lee esta reguladora (0 ó 1).
        // Fase 1: persiste; el bridge sólo actúa producto 0 (Fase 2 = dual).
        [JsonPropertyName("flow_index")] public int FlowIndex { get; set; }
        // Invertir sentido de esta reguladora (por-producto). El nodo conserva
        // su invert_motor para compat del producto 0.
        [JsonPropertyName("invert_motor")] public bool InvertMotor { get; set; }

        public FxProducto()
        {
            Id = 0;
            Nombre = "Producto";
            MeterCal = 100.0;
            DosisLha = 100.0;
            PwmMin = 40;
            PwmMax = 4095;
            Kp = 1.0; Ki = 0.1; Kd = 0.0;
            ModoManual = false;
            ManualLmin = 0.0;
            PasoLha = 5.0;
            PasoLmin = 1.0;
            Tipo = "valvula";
            FlowIndex = 0;
            InvertMotor = false;
        }
    }

    public class FxCableMap
    {
        [JsonPropertyName("cable")] public int Cable { get; set; }
        [JsonPropertyName("seccion_aog")] public int SeccionAOG { get; set; }

        public FxCableMap()
        {
            Cable = 0;
            SeccionAOG = 0;
        }
    }

    public class FxNodoConfig
    {
        [JsonPropertyName("uid")] public string Uid { get; set; }
        [JsonPropertyName("nombre")] public string Nombre { get; set; }
        [JsonPropertyName("habilitado")] public bool Habilitado { get; set; }
        [JsonPropertyName("ancho_barra_m")] public double AnchoBarraM { get; set; }
        [JsonPropertyName("is_3wire")] public bool Is3Wire { get; set; }
        [JsonPropertyName("invert_relay")] public bool InvertRelay { get; set; }
        [JsonPropertyName("invert_motor")] public bool InvertMotor { get; set; }
        // Master: -1 = salida dedicada (firmware MasterPin), 0 = sin master,
        // 1..N = ese corte hace de master (el bridge le pone el bit = OR de
        // todas las secciones abiertas del nodo).
        [JsonPropertyName("master_cable")] public int MasterCable { get; set; }
        [JsonPropertyName("productos")] public List<FxProducto> Productos { get; set; }
        [JsonPropertyName("cables")] public List<FxCableMap> Cables { get; set; }

        public FxNodoConfig()
        {
            Uid = "";
            Nombre = "Nodo FlowX";
            Habilitado = true;
            AnchoBarraM = 0;
            Is3Wire = false;
            InvertRelay = false;
            InvertMotor = false;
            MasterCable = -1; // default: salida dedicada (firmware)
            Productos = new List<FxProducto>();
            Cables = new List<FxCableMap>();
        }
    }

    public class FlowXConfig
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("nodos")] public List<FxNodoConfig> Nodos { get; set; }
        [JsonPropertyName("ignorados")] public List<string> Ignorados { get; set; }

        public FlowXConfig()
        {
            Enabled = true;
            Nodos = new List<FxNodoConfig>();
            Ignorados = new List<string>();
        }

        private static readonly string FileName = "flowX.json";

        public static FlowXConfig Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                var def = new FlowXConfig();
                def.Save();
                return def;
            }
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<FlowXConfig>(File.ReadAllText(path), opts)
                    ?? new FlowXConfig();
            }
            catch { return new FlowXConfig(); }
        }

        public void Save()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
        }
    }
}
