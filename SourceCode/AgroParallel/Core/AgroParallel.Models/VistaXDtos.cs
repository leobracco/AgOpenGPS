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
        public string ClientId { get; set; } = "PilotX_VistaX";
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

        /// <summary>
        /// Tope físico del sensor en sem/m. Por encima de este valor el sensor
        /// ya no resuelve semillas individuales y entra en "modo flujo". Default
        /// 20 — calibrable desde la UI sin restart.
        /// </summary>
        [JsonPropertyName("max_densidad_sensor")]
        public double MaxDensidadSensor { get; set; } = 20;

        /// <summary>Id del insumo activo (referencia al InsumoCatalogService).
        /// Define la densidad objetivo y la densidad asumida al saturar.</summary>
        [JsonPropertyName("insumo_activo_id")]
        public string InsumoActivoId { get; set; } = "";

        /// <summary>
        /// Cantidad de torres (módulos físicos) del implemento. 0 = sin agrupar.
        /// Sembradoras como Crucianelli / John Deere DB tienen varias torres
        /// que comparten dosificador — agrupar por torre permite "vista
        /// torres" en VistaX-Live: una celda por torre en lugar de N por surco.
        /// </summary>
        [JsonPropertyName("torres")]
        public int Torres { get; set; }

        /// <summary>
        /// Surcos por torre (uniforme). 0 = derivar como TotalSurcos/Torres.
        /// Asumimos torres uniformes; para layouts irregulares se calcula desde
        /// el mapeo de sensores y este campo es solo el default sugerido.
        /// </summary>
        [JsonPropertyName("surcos_por_torre")]
        public int SurcosPorTorre { get; set; }

        /// <summary>
        /// Vista preferida del overlay live: "surcos" | "torres". El operario
        /// puede cambiarla en runtime; este campo guarda la última elegida.
        /// </summary>
        [JsonPropertyName("vista_modo_default")]
        public string VistaModoDefault { get; set; } = "surcos";
        // NOTA: el toggle "overlay activo sobre PilotX" NO se persiste acá.
        // La fuente de verdad es VistaXConfig.Enabled (vistaX.json) — la lee
        // FormGPS.InitVistaX() para decidir si crear el panel nativo.
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

        /// <summary>
        /// Silencia este sensor: no contribuye al conteo de fallas global ni
        /// dispara alarma. Útil cuando una bajada está fuera de servicio o
        /// genera ruido conocido. Persiste en el implemento.json.
        /// </summary>
        [JsonPropertyName("muted")]
        public bool Muted { get; set; }

        /// <summary>
        /// Objetivo por-sensor en sem/min (o unidades equivalentes para tipos
        /// no-semilla — RPM para turbina, kg/h para tolva, etc.). 0 = usar el
        /// objetivo del tren. Útil para los sensores "otros" (turbina/tolva/
        /// bajada_herramienta) donde la UI muestra barras horizontales y el
        /// operario quiere fijar un setpoint distinto al de la siembra.
        /// </summary>
        [JsonPropertyName("objetivo")]
        public double Objetivo { get; set; }
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
        /// <summary>UID del nodo MQTT que reporta este surco (para mute per-sensor).</summary>
        public string Uid { get; set; } = "";
        /// <summary>Cable del nodo asignado a este surco.</summary>
        public int Cable { get; set; }
        /// <summary>Última lectura cruda (sem/m, o valor del firmware).</summary>
        public double Valor { get; set; }
        /// <summary>Semillas por minuto estimadas.</summary>
        public double Spm { get; set; }
        /// <summary>Objetivo del tren (sem/min) — útil para pintar el ratio sin recalcular.</summary>
        public double Objetivo { get; set; }
        /// <summary>Ratio Spm/Objetivo. 1.0 = exacto, &lt;1 sub-objetivo, &gt;1 exceso. 0 = tapado.</summary>
        public double RatioObjetivo { get; set; }
        public bool Alerta { get; set; }
        public bool SeccionCortada { get; set; }
        /// <summary>True si está silenciado por config (no dispara alarma).</summary>
        public bool Muted { get; set; }
        /// <summary>
        /// ok    → SPM dentro de tolerancia
        /// bajo  → 0 &lt; SPM &lt; obj·(1-tol)  (fade negro→verde por ratio)
        /// tapado→ SPM≈0 con telemetría reciente (sensor reportando pero sin pulsos)
        /// exceso→ SPM &gt; obj·(1+tol)        (azul)
        /// no-data→ sin lectura reciente / timeout
        /// muted → silenciado por config (no cuenta para alarma)
        /// </summary>
        public string Estado { get; set; } = "no-data";
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

        // ----- Layout torres (vista agrupada del overlay live) --------------
        // Se reflejan acá para que el cliente HTML pueda dibujar la vista
        // torres sin tener que pegarle a /api/vistax/implemento aparte.
        public int Torres { get; set; }
        public int SurcosPorTorre { get; set; }
        public string VistaModoDefault { get; set; } = "surcos";
        public List<VistaXTrenLiveDto> Trenes { get; set; } = new List<VistaXTrenLiveDto>();
        public List<VistaXNodoLiveDto> Nodos { get; set; } = new List<VistaXNodoLiveDto>();
    }
}
