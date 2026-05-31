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
        [JsonPropertyName("kp")] public double Kp { get; set; }
        [JsonPropertyName("ki")] public double Ki { get; set; }
        [JsonPropertyName("kd")] public double Kd { get; set; }

        public FxProducto()
        {
            Id = 0;
            Nombre = "Producto";
            MeterCal = 100.0;
            DosisLha = 100.0;
            PwmMin = 40;
            Kp = 1.0; Ki = 0.1; Kd = 0.0;
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
