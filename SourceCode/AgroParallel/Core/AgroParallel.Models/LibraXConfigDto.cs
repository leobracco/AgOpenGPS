// ============================================================================
// LibraXConfigDto — config y snapshot live del módulo LibraX (libraX.json).
//
// LibraX es el monitor de rendimiento de Agro Parallel: un par óptico Banner
// enfrentado a través de la noria de grano limpio mide qué fracción del tiempo
// las paletas (y el grano que llevan) tapan el haz.
//
// Topics MQTT:
//   agp/librax/{uid}/status_live  ESP→PC  {ratio, paddle_hz, rpm, moist_mv,
//                                          sensor_ok, noise, up}
//   agp/librax/{uid}/announcement ESP→PC  {uid, ip, version, hw, device}
//
// FASE 1: el nodo publica CRUDO y PilotX solo muestra. Baseline de paletas,
// factor de calibración, humedad en % y qq/ha son material de fase 2 y NO
// tienen lugar en este DTO todavía.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Un nodo LibraX conocido (para ponerle nombre en la UI).</summary>
    public sealed class LibraXNodoConfigDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; }

        public LibraXNodoConfigDto()
        {
            Habilitado = true;
        }
    }

    public sealed class LibraXConfigDto
    {
        [JsonPropertyName("nodos")]
        public List<LibraXNodoConfigDto> Nodos { get; set; }

        /// <summary>Ms sin publicar antes de marcar el nodo offline. El nodo
        /// publica a 5 Hz, así que 3 s son 15 muestras perdidas.</summary>
        [JsonPropertyName("timeout_ms")]
        public int TimeoutMs { get; set; }

        /// <summary>Segundos de historial que grafica la pantalla.</summary>
        [JsonPropertyName("historial_seg")]
        public int HistorialSeg { get; set; }

        public LibraXConfigDto()
        {
            Nodos = new List<LibraXNodoConfigDto>();
            TimeoutMs = 3000;
            HistorialSeg = 60;
        }
    }

    /// <summary>Estado runtime de un nodo LibraX.</summary>
    public sealed class LbxNodoLiveDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("online")]
        public bool Online { get; set; }

        /// <summary>‰ de tiempo con el haz tapado, crudo, sin restar baseline.</summary>
        [JsonPropertyName("ratio_permil")]
        public int RatioPermil { get; set; }

        /// <summary>El mismo valor en %, para no repetir la división en la UI.</summary>
        [JsonPropertyName("ratio_pct")]
        public double RatioPct { get; set; }

        [JsonPropertyName("paddle_hz")]
        public int PaddleHz { get; set; }

        [JsonPropertyName("rpm")]
        public int Rpm { get; set; }

        /// <summary>Salida del sensor de humedad en milivolts, SIN convertir a %
        /// (la curva de humedad es fase 2).</summary>
        [JsonPropertyName("moist_mv")]
        public int MoistMv { get; set; }

        [JsonPropertyName("sensor_ok")]
        public bool SensorOk { get; set; }

        /// <summary>Glitches descartados por el nodo desde su arranque. Si crece
        /// rápido, hay EMI o el sensor está desalineado.</summary>
        [JsonPropertyName("noise")]
        public int Noise { get; set; }

        [JsonPropertyName("uptime_s")]
        public int UptimeS { get; set; }

        [JsonPropertyName("last_seen_iso")]
        public string LastSeenIso { get; set; }

        public LbxNodoLiveDto()
        {
            Uid = "";
            Nombre = "";
            LastSeenIso = "";
        }
    }

    public sealed class LibraXLiveSnapshotDto
    {
        [JsonPropertyName("monitoreo_activo")]
        public bool MonitoreoActivo { get; set; }

        [JsonPropertyName("nodos")]
        public List<LbxNodoLiveDto> Nodos { get; set; }

        public LibraXLiveSnapshotDto()
        {
            Nodos = new List<LbxNodoLiveDto>();
        }
    }
}
