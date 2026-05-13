// ============================================================================
// VistaXDtos.cs — POCOs para el módulo VistaX (UI HTML).
//
//   VistaXConfigDto        → vistaX.json (broker + topics + layout + límites)
//   VistaXImplementoDto    → JSON de implemento (setup + trenes + sensores)
//   VistaXSensorConfigDto  → un sensor mapeado
//   VistaXTrenConfigDto    → un tren de siembra
//   VistaXSurcoStateDto    → estado runtime de un surco
//   VistaXLiveSnapshotDto  → snapshot consolidado para UI live
//   VistaXNodoLiveDto      → estado por nodo VistaX visto en MQTT
//
// Shape espejo de los JSON existentes (snake_case en JsonPropertyName) para
// compatibilidad byte-idéntica con los archivos del legacy.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    // ---------- Config global (vistaX.json) ----------
    public sealed class VistaXConfigDto
    {
        public bool Enabled { get; set; }
        public string BrokerAddress { get; set; } = "127.0.0.1";
        public int BrokerPort { get; set; } = 1883;
        public string ClientId { get; set; } = "AgOpenGPS_VistaX";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool UseTls { get; set; }

        public string TelemetriaTopic { get; set; } = "vistax/nodos/telemetria";
        public string SpeedTopic { get; set; } = "aog/machine/speed";
        public string SectionsTopic { get; set; } = "sections/state";

        public string ImplementoJsonPath { get; set; } = "";

        public int UiUpdateIntervalMs { get; set; } = 500;
        public int SensorTimeoutMs { get; set; } = 3000;
        public bool LogToFieldRecord { get; set; } = true;

        public string MetodoInicio { get; set; } = "sensores";
        public int UmbralSensoresActivos { get; set; } = 3;
        public int TiempoConfirmacionMs { get; set; } = 500;

        public bool AlarmMuted { get; set; }
        public string LogOutputDrive { get; set; } = "";
    }

    // ---------- Implemento (perfil JSON completo) ----------
    public sealed class VistaXSetupDto
    {
        [JsonPropertyName("densidad_objetivo")]
        public double DensidadObjetivo { get; set; } = 16;

        [JsonPropertyName("tolerancia_desvio")]
        public double ToleranciaDesvio { get; set; } = 20;

        [JsonPropertyName("distancia_entre_surcos")]
        public double DistanciaEntreSurcos { get; set; } = 0.191;

        [JsonPropertyName("factor_k_default")]
        public double FactorK { get; set; } = 0.15;

        [JsonPropertyName("objetivos_tren")]
        public Dictionary<string, double> ObjetivosTren { get; set; } = new Dictionary<string, double>();

        [JsonPropertyName("total_surcos")]
        public int TotalSurcos { get; set; }

        [JsonPropertyName("secciones_aog")]
        public int SeccionesAOG { get; set; }

        [JsonPropertyName("ancho_implemento")]
        public double AnchoImplemento { get; set; }
    }

    public sealed class VistaXTrenConfigDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        [JsonPropertyName("surcos")]
        public int Surcos { get; set; }
    }

    public sealed class VistaXSensorConfigDto
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; } = "";

        [JsonPropertyName("cable")]
        public int Cable { get; set; }

        [JsonPropertyName("pin")]
        public int Pin { get; set; }

        [JsonPropertyName("bajada")]
        public int Bajada { get; set; }

        [JsonPropertyName("surco_desde")]
        public int SurcoDesde { get; set; }

        [JsonPropertyName("surco_hasta")]
        public int SurcoHasta { get; set; }

        [JsonPropertyName("tipo")]
        public string Tipo { get; set; } = "semilla";

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        [JsonPropertyName("tren")]
        public int Tren { get; set; } = 1;

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;

        [JsonPropertyName("seccion_aog")]
        public int SeccionAOG { get; set; }
    }

    public sealed class VistaXImplementoDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";

        [JsonPropertyName("setup")]
        public VistaXSetupDto Setup { get; set; } = new VistaXSetupDto();

        [JsonPropertyName("trenes")]
        public List<VistaXTrenConfigDto> Trenes { get; set; } = new List<VistaXTrenConfigDto>();

        [JsonPropertyName("mapeo_sensores")]
        public List<VistaXSensorConfigDto> MapeoSensores { get; set; } = new List<VistaXSensorConfigDto>();
    }

    // ---------- Live snapshot (consumido por la UI) ----------
    public sealed class VistaXSurcoStateDto
    {
        public int Bajada { get; set; }
        public string Tipo { get; set; } = "semilla";
        public int Tren { get; set; }
        /// <summary>Última lectura cruda (sem/m, o valor del firmware).</summary>
        public double Valor { get; set; }
        /// <summary>Semillas por minuto estimadas.</summary>
        public double Spm { get; set; }
        public bool Alerta { get; set; }
        public bool SeccionCortada { get; set; }
        public string Estado { get; set; } = "no-data"; // ok | warn | bad | no-data
        public string LastSeenIso { get; set; } = "";
    }

    public sealed class VistaXTrenLiveDto
    {
        public int Tren { get; set; }
        public string Nombre { get; set; } = "";
        public double Objetivo { get; set; }
        public List<VistaXSurcoStateDto> Surcos { get; set; } = new List<VistaXSurcoStateDto>();
    }

    public sealed class VistaXNodoLiveDto
    {
        public string Uid { get; set; } = "";
        public string LastSeenIso { get; set; } = "";
        public int SensorsReporting { get; set; }
        public bool Online { get; set; }
    }

    public sealed class VistaXLiveSnapshotDto
    {
        public bool MonitoreoActivo { get; set; }
        public bool HasAlarm { get; set; }
        public string AlarmMessage { get; set; } = "";
        public double Velocidad { get; set; }
        public double SpmPromedio { get; set; }
        public int FallasActivas { get; set; }
        public int SurcosActivos { get; set; }
        public double ToleranciaDesvio { get; set; }
        public string NombreImplemento { get; set; } = "";
        public List<VistaXTrenLiveDto> Trenes { get; set; } = new List<VistaXTrenLiveDto>();
        public List<VistaXNodoLiveDto> Nodos { get; set; } = new List<VistaXNodoLiveDto>();
    }
}
