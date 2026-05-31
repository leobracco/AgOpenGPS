// ============================================================================
// StormXConfig.cs - Configuración de la estación meteo móvil StormX
// Equivalente legacy del DTO StormXConfigDto, listo para ser consumido por
// el futuro StormXBridge (suscriptor MQTT + logger + lecturas para UI).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.StormX
{
    // Umbrales operativos para alertas de pulverización.
    public class StormXLimits
    {
        [JsonPropertyName("wind_max_ms")] public double WindMaxMs { get; set; }
        [JsonPropertyName("wind_min_ms")] public double WindMinMs { get; set; }
        [JsonPropertyName("hum_min_pct")] public double HumMinPct { get; set; }
        [JsonPropertyName("temp_max_c")] public double TempMaxC { get; set; }
        [JsonPropertyName("delta_t_max_c")] public double DeltaTMaxC { get; set; }

        public StormXLimits()
        {
            WindMaxMs = 5.5;
            WindMinMs = 1.0;
            HumMinPct = 50.0;
            TempMaxC = 28.0;
            DeltaTMaxC = 8.0;
        }
    }

    public class StormXNodoConfig
    {
        [JsonPropertyName("uid")] public string Uid { get; set; }
        [JsonPropertyName("nombre")] public string Nombre { get; set; }
        [JsonPropertyName("habilitado")] public bool Habilitado { get; set; }
        [JsonPropertyName("altura_m")] public double AlturaM { get; set; }
        [JsonPropertyName("offset_dir_deg")] public double OffsetDirDeg { get; set; }
        [JsonPropertyName("modelo_sensor")] public string ModeloSensor { get; set; }

        public StormXNodoConfig()
        {
            Uid = "";
            Nombre = "Nodo StormX";
            Habilitado = true;
            AlturaM = 2.0;
            OffsetDirDeg = 0.0;
            ModeloSensor = "";
        }
    }

    public class StormXConfig
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("log_interval_sec")] public int LogIntervalSec { get; set; }
        [JsonPropertyName("limits")] public StormXLimits Limits { get; set; }
        [JsonPropertyName("nodos")] public List<StormXNodoConfig> Nodos { get; set; }
        [JsonPropertyName("ignorados")] public List<string> Ignorados { get; set; }

        public StormXConfig()
        {
            Enabled = true;
            LogIntervalSec = 30;
            Limits = new StormXLimits();
            Nodos = new List<StormXNodoConfig>();
            Ignorados = new List<string>();
        }

        private static readonly string FileName = "stormX.json";

        public static StormXConfig Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                var def = new StormXConfig();
                def.Save();
                return def;
            }
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<StormXConfig>(File.ReadAllText(path), opts)
                    ?? new StormXConfig();
            }
            catch { return new StormXConfig(); }
        }

        public void Save()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
        }
    }
}
