// StormXConfigDto — DTO de la config de StormX (stormX.json).
//
// StormX es la estación meteorológica móvil de Agro Parallel: ESP32 + sensor
// multifunción (típicamente RS485 Modbus) que reporta velocidad/dirección de
// viento, temperatura, humedad y presión. Se monta sobre el tractor o la
// pulverizadora y publica telemetría por MQTT.
//
// Topics MQTT planificados:
//   agp/storm/{uid}/status_live  ESP→PC  {wind_ms, wind_dir, temp_c, hum_pct, press_hpa, ...}
//   agp/storm/{uid}/cmd/...      PC→ESP  comandos (calibrar offset, reset, etc.)
//   agp/storm/announcement       ESP→PC  {uid, ip, fw, sensor}
//
// La UI del Hub muestra la lectura en tiempo real y advierte cuando viento o
// humedad salen del rango operativo para pulverizar.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Umbrales operativos para alertas de pulverización.</summary>
    public sealed class StormXLimitsDto
    {
        /// <summary>Viento máximo recomendado (m/s) para pulverizar. Default 5.5.</summary>
        [JsonPropertyName("wind_max_ms")]
        public double WindMaxMs { get; set; }

        /// <summary>Viento mínimo recomendado (m/s) para evitar deriva por convección. Default 1.0.</summary>
        [JsonPropertyName("wind_min_ms")]
        public double WindMinMs { get; set; }

        /// <summary>Humedad relativa mínima (%). Default 50.</summary>
        [JsonPropertyName("hum_min_pct")]
        public double HumMinPct { get; set; }

        /// <summary>Temperatura máxima (°C). Default 28.</summary>
        [JsonPropertyName("temp_max_c")]
        public double TempMaxC { get; set; }

        /// <summary>Delta-T máximo (°C) — depresión psicrométrica.</summary>
        [JsonPropertyName("delta_t_max_c")]
        public double DeltaTMaxC { get; set; }

        public StormXLimitsDto()
        {
            WindMaxMs = 5.5;
            WindMinMs = 1.0;
            HumMinPct = 50.0;
            TempMaxC = 28.0;
            DeltaTMaxC = 8.0;
        }
    }

    public sealed class StormXNodoConfigDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; }

        /// <summary>Altura del sensor sobre el suelo (metros). Para correcciones de viento.</summary>
        [JsonPropertyName("altura_m")]
        public double AlturaM { get; set; }

        /// <summary>Offset de dirección (grados) para alinear el norte del sensor con el del tractor.</summary>
        [JsonPropertyName("offset_dir_deg")]
        public double OffsetDirDeg { get; set; }

        /// <summary>Modelo del sensor (libre: "RS485-WindMet-4in1", "Davis Vantage", etc.).</summary>
        [JsonPropertyName("modelo_sensor")]
        public string ModeloSensor { get; set; }

        public StormXNodoConfigDto()
        {
            Uid = "";
            Nombre = "Nodo StormX";
            Habilitado = true;
            AlturaM = 2.0;
            OffsetDirDeg = 0.0;
            ModeloSensor = "";
        }
    }

    public sealed class StormXConfigDto
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>Cadencia de logging a disco (segundos). 0 = no loggear.</summary>
        [JsonPropertyName("log_interval_sec")]
        public int LogIntervalSec { get; set; }

        [JsonPropertyName("limits")]
        public StormXLimitsDto Limits { get; set; }

        [JsonPropertyName("nodos")]
        public List<StormXNodoConfigDto> Nodos { get; set; }

        [JsonPropertyName("ignorados")]
        public List<string> Ignorados { get; set; }

        public StormXConfigDto()
        {
            Enabled = true;
            LogIntervalSec = 30;
            Limits = new StormXLimitsDto();
            Nodos = new List<StormXNodoConfigDto>();
            Ignorados = new List<string>();
        }
    }

    // =============================================================
    // DTOs de telemetría live — producidos por StormXLiveService a
    // partir del topic agp/storm/{uid}/status_live. La UI los consume
    // por GET /api/stormx/live (sin tocar MQTT).
    //
    // Payload esperado del firmware StormX (4-part canonical):
    //   {
    //     "uid":"a1b2c3d4",
    //     "wind_ms":3.2, "wind_dir":135, "gust_ms":4.5,
    //     "temp_c":22.5, "hum_pct":62.0, "press_hpa":1013.4,
    //     "delta_t_c":4.1, "rain_mm":0.0
    //   }
    // =============================================================

    /// <summary>Lectura meteorológica de un nodo StormX.</summary>
    public sealed class StxNodoLiveDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("online")]
        public bool Online { get; set; }

        /// <summary>Velocidad de viento promedio (m/s).</summary>
        [JsonPropertyName("wind_ms")]
        public double WindMs { get; set; }

        /// <summary>Ráfaga (m/s) — pico reciente.</summary>
        [JsonPropertyName("gust_ms")]
        public double GustMs { get; set; }

        /// <summary>Dirección del viento (grados, 0..360, 0=norte).</summary>
        [JsonPropertyName("wind_dir")]
        public double WindDir { get; set; }

        /// <summary>Temperatura (°C).</summary>
        [JsonPropertyName("temp_c")]
        public double TempC { get; set; }

        /// <summary>Humedad relativa (%).</summary>
        [JsonPropertyName("hum_pct")]
        public double HumPct { get; set; }

        /// <summary>Presión atmosférica (hPa).</summary>
        [JsonPropertyName("press_hpa")]
        public double PressHpa { get; set; }

        /// <summary>Delta-T (°C) — depresión psicrométrica calculada.</summary>
        [JsonPropertyName("delta_t_c")]
        public double DeltaTC { get; set; }

        /// <summary>Lluvia acumulada en el intervalo (mm). 0 si no hay pluviómetro.</summary>
        [JsonPropertyName("rain_mm")]
        public double RainMm { get; set; }

        /// <summary>Veredicto resumido evaluando los Limits: "ok" | "warn" | "bad" | "no-data".</summary>
        [JsonPropertyName("verdict")]
        public string Verdict { get; set; }

        /// <summary>ISO timestamp de la última telemetría recibida.</summary>
        [JsonPropertyName("last_seen_iso")]
        public string LastSeenIso { get; set; }

        public StxNodoLiveDto()
        {
            Uid = "";
            Nombre = "";
            Verdict = "no-data";
            LastSeenIso = "";
        }
    }

    /// <summary>Snapshot consolidado de todos los nodos StormX para la UI.</summary>
    public sealed class StormXLiveSnapshotDto
    {
        [JsonPropertyName("monitoreo_activo")]
        public bool MonitoreoActivo { get; set; }

        /// <summary>Umbrales en uso (eco del config para que la UI compute igual sin re-fetch).</summary>
        [JsonPropertyName("limits")]
        public StormXLimitsDto Limits { get; set; }

        [JsonPropertyName("nodos")]
        public List<StxNodoLiveDto> Nodos { get; set; }

        public StormXLiveSnapshotDto()
        {
            Limits = new StormXLimitsDto();
            Nodos = new List<StxNodoLiveDto>();
        }
    }
}
