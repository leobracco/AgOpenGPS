// ============================================================================
// ConfigVehiculoClient.cs — cliente HTTP de la Configuración del vehículo /
// implemento (lo que en HTML es pages/config.html + js/config.js).
//
// QUÉ QUEDÓ NATIVO con este cliente: el snapshot completo
// (GET /api/aog/config) que alimenta al ConfigPanel nativo y a sus pestañas.
// QUÉ SIGUE EN HTML: la página config.html entera y sus ~25 módulos embebidos
// (Hub, QuantiX, Cámaras, Ayuda, Mantenimiento…). Las pestañas que todavía no
// están portadas se siguen abriendo por WebView desde el propio panel nativo
// (strangler fig — la página NO se borra: la usa la PWA del celular).
//
// UN SOLO cliente para toda la config, a propósito: las pestañas que se vayan
// portando (Vehículo, Dimensiones, Antena, Enganche, …) reusan este GET y
// estos DTOs, y agregan su guardado con GuardarAsync("seccion", body).
//
// Trampas del wire cubiertas acá:
//   · snake_case: el back serializa con AgpJson (is_sections_not_zones,
//     look_ahead_on, tool_width…). PropertyNameCaseInsensitive NO cubre los
//     underscores, así que va [JsonPropertyName] EXPLÍCITO en cada propiedad.
//     Sin eso el panel mostraría ceros como si fueran datos reales.
//   · BOM de EmbedIO: las respuestas pueden arrancar con U+FEFF y sin
//     limpiarlo el Deserialize tira y el panel queda clavado en "sin datos".
//   · Unidades: TODO viene en METROS con signo (velocidades km/h, tiempos s).
//     La conversión a cm/in/ft la hace la UI según is_metric — igual que el JS.
//   · ok:false ("service-unavailable") NO es lo mismo que "no respondió": el
//     GET devuelve el objeto con Ok=false en el primer caso y null en el
//     segundo, para que la UI pueda distinguirlos como hace la página HTML.
// ============================================================================

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ---------------------------------------------------------------- DTOs (wire)

public sealed class ConfigVehiculoSec
{
    [JsonPropertyName("vehicle_type")]      public int VehicleType { get; set; }
    [JsonPropertyName("tractor_brand")]     public string? TractorBrand { get; set; }
    [JsonPropertyName("harvester_brand")]   public string? HarvesterBrand { get; set; }
    [JsonPropertyName("articulated_brand")] public string? ArticulatedBrand { get; set; }
    [JsonPropertyName("is_vehicle_image")]  public bool IsVehicleImage { get; set; }
    [JsonPropertyName("opacity")]           public int Opacity { get; set; }
}

public sealed class ConfigDimensionesSec
{
    [JsonPropertyName("wheelbase")]    public double? Wheelbase { get; set; }
    [JsonPropertyName("track_width")]  public double? TrackWidth { get; set; }
    [JsonPropertyName("hitch_length")] public double? HitchLength { get; set; }   // − = atrás
}

public sealed class ConfigAntenaSec
{
    [JsonPropertyName("antenna_height")] public double? AntennaHeight { get; set; }
    [JsonPropertyName("antenna_pivot")]  public double? AntennaPivot { get; set; }
    [JsonPropertyName("antenna_offset")] public double? AntennaOffset { get; set; } // + izquierda
}

public sealed class ConfigEngancheSec
{
    [JsonPropertyName("estilo")]                     public string? Estilo { get; set; }
    [JsonPropertyName("hitch_length")]               public double? HitchLength { get; set; }
    [JsonPropertyName("trailing_hitch_length")]      public double? TrailingHitchLength { get; set; }
    [JsonPropertyName("tank_trailing_hitch_length")] public double? TankTrailingHitchLength { get; set; }
}

public sealed class ConfigOffsetSec
{
    [JsonPropertyName("tool_offset")]  public double? ToolOffset { get; set; }   // + derecha
    [JsonPropertyName("tool_overlap")] public double? ToolOverlap { get; set; }  // + overlap / − gap
    [JsonPropertyName("trailing_tool_to_pivot_length")] public double? TrailingToolToPivotLength { get; set; }
}

public sealed class ConfigTimingSec
{
    [JsonPropertyName("look_ahead_on")]  public double? LookAheadOn { get; set; }   // s
    [JsonPropertyName("look_ahead_off")] public double? LookAheadOff { get; set; }
    [JsonPropertyName("turn_off_delay")] public double? TurnOffDelay { get; set; }
}

public sealed class ConfigSeccionesSec
{
    [JsonPropertyName("is_sections_not_zones")]   public bool IsSectionsNotZones { get; set; }
    [JsonPropertyName("max_sections")]            public int MaxSections { get; set; }
    [JsonPropertyName("num_sections")]            public int NumSections { get; set; }
    [JsonPropertyName("default_section_width")]   public double? DefaultSectionWidth { get; set; }
    [JsonPropertyName("section_widths")]          public double[]? SectionWidths { get; set; }
    [JsonPropertyName("num_sections_multi")]      public int NumSectionsMulti { get; set; }
    [JsonPropertyName("section_width_multi")]     public double? SectionWidthMulti { get; set; }
    [JsonPropertyName("zones")]                   public int Zones { get; set; }
    [JsonPropertyName("zone_ranges")]             public int[]? ZoneRanges { get; set; }
    [JsonPropertyName("is_section_off_when_out")] public bool IsSectionOffWhenOut { get; set; }
    [JsonPropertyName("slow_speed_cutoff")]       public double? SlowSpeedCutoff { get; set; }  // km/h
    [JsonPropertyName("min_coverage")]            public int MinCoverage { get; set; }
    [JsonPropertyName("tool_width")]              public double? ToolWidth { get; set; }        // m
}

/// <summary>
/// Switches físicos de trabajo y de dirección cableados al módulo de máquina
/// (llegan por PGN; los interpreta CModuleComm.CheckWorkAndSteerSwitch).
///   · work_enabled / steer_enabled: el motor escucha ese switch remoto.
///   · work_active_low: contacto CERRADO = "trabajando" (default histórico
///     true). Ojo con invertirlo: la comparación del motor es
///     `workSwitchHigh != isWorkSwitchActiveLow`, así que darlo vuelta hace
///     que la máquina aplique al revés del switch físico.
///   · *_manual_sections: al activarse el switch las secciones pasan a master
///     Manual (true) o Auto (false). Es un solo bool por par, nunca tri-estado.
/// `is_remote_work_system_on` NO viaja: lo deriva el backend
/// (work_enabled || steer_enabled).
/// OJO: work_enabled y steer_enabled salen del RUNTIME (_engine.Mc.*), no de
/// Settings — hay que pintar siempre lo que dice el GET.
/// </summary>
public sealed class ConfigSwitchesSec
{
    [JsonPropertyName("work_enabled")]          public bool WorkEnabled { get; set; }
    [JsonPropertyName("work_active_low")]       public bool WorkActiveLow { get; set; }
    [JsonPropertyName("work_manual_sections")]  public bool WorkManualSections { get; set; }
    [JsonPropertyName("steer_enabled")]         public bool SteerEnabled { get; set; }
    [JsonPropertyName("steer_manual_sections")] public bool SteerManualSections { get; set; }
    /// <summary>ToolX: el perfil acepta el switch de trabajo inalámbrico
    /// (PGN 253 origen 0x7C). Hijo de work_enabled: sin "Activar" no manda.</summary>
    [JsonPropertyName("work_toolx_enabled")]    public bool WorkToolxEnabled { get; set; }
    /// <summary>RUNTIME, sólo lectura: llegó un frame de ToolX hace pocos segundos
    /// (CModuleComm.ToolXTimeoutSec).</summary>
    [JsonPropertyName("work_toolx_alive")]      public bool WorkToolxAlive { get; set; }
}

/// <summary>
/// Módulo de máquina: levante hidráulico + bytes de usuario. Es la sección que
/// alimenta la pestaña "Máquina", la única (junto con Pines relay) que NO
/// guarda al salir: se manda con "Enviar + Guardar" y sale como PGN 238.
///   · invert_relays / hyd_on: bits 0 y 1 del byte `setArdMac_setting0`.
///   · raise_time / lower_time: segundos, 1..255 — viajan crudos en el PGN.
///   · hyd_lift_look_ahead: segundos, 1..20. NO viaja en el PGN: es config
///     local del motor (_engine.Vehicle.hydLiftLookAheadTime).
///   · user1..4: bytes crudos 0..255 que interpreta el firmware del módulo.
///     No tienen unidad y no hay que inventarles una.
/// </summary>
public sealed class ConfigMaquinaSec
{
    [JsonPropertyName("invert_relays")]        public bool InvertRelays { get; set; }
    [JsonPropertyName("hyd_on")]               public bool HydOn { get; set; }
    [JsonPropertyName("raise_time")]           public int RaiseTime { get; set; }
    [JsonPropertyName("lower_time")]           public int LowerTime { get; set; }
    [JsonPropertyName("hyd_lift_look_ahead")]  public double HydLiftLookAhead { get; set; }
    [JsonPropertyName("user1")]                public int User1 { get; set; }
    [JsonPropertyName("user2")]                public int User2 { get; set; }
    [JsonPropertyName("user3")]                public int User3 { get; set; }
    [JsonPropertyName("user4")]                public int User4 { get; set; }
}

/// <summary>
/// Fuentes de rumbo (pestaña "GPS / IMU › Rumbo").
///   · heading_source: "Fix" (antena simple, rumbo por movimiento) | "Dual"
///     (dos antenas, rumbo real aunque el tractor esté parado).
///   · min_gps_step: paso mínimo de rumbo — true = 1,0 m (se muestra "10 cm" de
///     paso y "100 cm" de distancia de rumbo), false = 0,5 m ("5 cm" / "50 cm").
///     Los rótulos NO son el valor: son la tabla fija del original.
///   · fusion: posición de la BARRA 5..60, no el peso. El motor guarda
///     `setIMU_fusionWeight2 = barra × 0.002` y el snapshot devuelve
///     `(int)(peso × 500)` — round-trip exacto. Ojo: el número es el % GPS;
///     el % IMU es 100 − v.
///   · auto_switch_speed: SIEMPRE km/h en el wire, sea cual sea is_metric.
///   · imu_present: RUNTIME (imuHeading != 99999), no es configuración — es lo
///     que habilita el slider de fusión.
/// </summary>
public sealed class ConfigRumboSec
{
    [JsonPropertyName("heading_source")]        public string? HeadingSource { get; set; }
    [JsonPropertyName("min_gps_step")]          public bool MinGpsStep { get; set; }
    [JsonPropertyName("fusion")]                public int Fusion { get; set; }
    [JsonPropertyName("is_rtk")]                public bool IsRtk { get; set; }
    [JsonPropertyName("is_rtk_kill_autosteer")] public bool IsRtkKillAutosteer { get; set; }
    [JsonPropertyName("jump_fix_distance")]     public int JumpFixDistance { get; set; }   // cm, 0 = off
    [JsonPropertyName("dual_heading_offset")]   public double DualHeadingOffset { get; set; } // grados
    [JsonPropertyName("dual_reverse_distance")] public double DualReverseDistance { get; set; } // m
    [JsonPropertyName("reverse_on")]            public bool ReverseOn { get; set; }
    [JsonPropertyName("auto_switch_dual_fix")]  public bool AutoSwitchDualFix { get; set; }
    [JsonPropertyName("auto_switch_speed")]     public double AutoSwitchSpeed { get; set; } // km/h SIEMPRE
    [JsonPropertyName("imu_present")]           public bool ImuPresent { get; set; }
}

/// <summary>
/// Rolido de la IMU (pestaña "GPS / IMU › Rolido").
///   · roll_zero: el CERO en grados (setIMU_rollZero). Es un offset del lado de
///     PilotX, no algo que el ECU sepa.
///   · roll_filter: posición de la BARRA 0..98, no el peso. El motor guarda
///     `setIMU_rollFilter = barra × 0.01` y el snapshot devuelve
///     `(int)(peso × 100)` — round-trip exacto.
///   · imu_present / imu_roll: RUNTIME, no configuración. `imu_roll` vale
///     88888 cuando NO hay dato: es un CENTINELA, no un ángulo. Formatearlo sin
///     mirar imu_present pinta "+88888.0°" y vuelca el tractorcito.
/// </summary>
public sealed class ConfigRolidoSec
{
    [JsonPropertyName("roll_zero")]   public double RollZero { get; set; }
    [JsonPropertyName("roll_filter")] public int RollFilter { get; set; }
    [JsonPropertyName("invert_roll")] public bool InvertRoll { get; set; }
    [JsonPropertyName("imu_present")] public bool ImuPresent { get; set; }
    [JsonPropertyName("imu_roll")]    public double ImuRoll { get; set; }
}

/// <summary>
/// Sección `uturn` del snapshot (pestaña "Otros › U-Turn"): la GEOMETRÍA del
/// giro de cabecera. Todo en METROS y sin signo:
///   · `radius` — radio del U (server: mínimo 2 m, SIN techo; el 100 m de la UI
///     es cortesía nuestra);
///   · `distance_from_boundary` — a qué distancia del límite arranca el giro
///     (server: mínimo 0,2 m);
///   · `extension_length` — metros ENTEROS que se extiende la línea de giro
///     (server: 3..50);
///   · `smoothing` — suavizado ADIMENSIONAL (server: 8..50; el "paso de 2" lo
///     garantiza SOLO la UI, así que un valor impar guardado desde otro cliente
///     es perfectamente posible y hay que pintarlo tal cual).
/// OJO: el toggle que muestra u oculta el botón U-Turn de la pantalla principal
/// (`feature_uturn`) NO es de esta sección — vive en `botones`.
/// </summary>
public sealed class ConfigUturnSec
{
    [JsonPropertyName("radius")]                 public double Radius { get; set; }
    [JsonPropertyName("distance_from_boundary")] public double DistanceFromBoundary { get; set; }
    [JsonPropertyName("extension_length")]       public int ExtensionLength { get; set; }
    [JsonPropertyName("smoothing")]              public int Smoothing { get; set; }
}

public sealed class ConfigTramSec
{
    [JsonPropertyName("tram_width")]           public double? TramWidth { get; set; }
    [JsonPropertyName("display_tram_control")] public bool DisplayTramControl { get; set; }
    [JsonPropertyName("outer_inverted")]       public bool OuterInverted { get; set; }
}

/// <summary>
/// Snapshot de GET /api/aog/config. Solo se declaran las secciones que ya
/// consume alguna pestaña nativa; las que faltan (relay, display, botones) se
/// agregan cuando se porte su pestaña — el JSON extra se ignora sin romper nada.
/// </summary>
public sealed class ConfigSnapshot
{
    [JsonPropertyName("ok")]             public bool Ok { get; set; }
    [JsonPropertyName("error")]          public string? Error { get; set; }
    [JsonPropertyName("is_metric")]      public bool IsMetric { get; set; }
    [JsonPropertyName("is_job_started")] public bool IsJobStarted { get; set; }
    [JsonPropertyName("perfil_activo")]  public string? PerfilActivo { get; set; }

    [JsonPropertyName("vehiculo")]    public ConfigVehiculoSec? Vehiculo { get; set; }
    [JsonPropertyName("dimensiones")] public ConfigDimensionesSec? Dimensiones { get; set; }
    [JsonPropertyName("antena")]      public ConfigAntenaSec? Antena { get; set; }
    [JsonPropertyName("enganche")]    public ConfigEngancheSec? Enganche { get; set; }
    [JsonPropertyName("offset")]      public ConfigOffsetSec? Offset { get; set; }
    [JsonPropertyName("timing")]      public ConfigTimingSec? Timing { get; set; }
    [JsonPropertyName("secciones")]   public ConfigSeccionesSec? Secciones { get; set; }
    [JsonPropertyName("switches")]    public ConfigSwitchesSec? Switches { get; set; }
    [JsonPropertyName("maquina")]     public ConfigMaquinaSec? Maquina { get; set; }
    [JsonPropertyName("rumbo")]       public ConfigRumboSec? Rumbo { get; set; }
    [JsonPropertyName("rolido")]      public ConfigRolidoSec? Rolido { get; set; }
    [JsonPropertyName("uturn")]       public ConfigUturnSec? Uturn { get; set; }
    [JsonPropertyName("tram")]        public ConfigTramSec? Tram { get; set; }
}

/// <summary>
/// Respuesta de POST /api/aog/config/rolido/accion (las acciones inmediatas del
/// cero: poner en cero, quitar offset, ±0,1°, reiniciar IMU). El backend YA
/// aplicó y persistió cuando contesta ok:true — el panel no calcula nada, solo
/// muestra lo que vuelve.
/// `imu_roll` = 88888 cuando no hay dato (coherente con imu_present:false).
/// </summary>
public sealed class ConfigRolidoAccionResult
{
    [JsonPropertyName("ok")]          public bool Ok { get; set; }
    [JsonPropertyName("error")]       public string? Error { get; set; }
    [JsonPropertyName("roll_zero")]   public double RollZero { get; set; }
    [JsonPropertyName("imu_roll")]    public double ImuRoll { get; set; }
    [JsonPropertyName("imu_present")] public bool ImuPresent { get; set; }
}

/// <summary>
/// Muestra en vivo de GET /api/aog/graph-correction. DTO mínimo: la pantalla de
/// Rolido solo consume `roll_degrees` (rolido QUE USA PILOTX — cero e inversión
/// ya aplicados por el motor) y `roll_present`. Los eastings del sample son del
/// gráfico de chequeo de corrección, otra página.
/// </summary>
public sealed class CorrectionSample
{
    [JsonPropertyName("roll_degrees")] public double RollDegrees { get; set; }
    [JsonPropertyName("roll_present")] public bool RollPresent { get; set; }
}

/// <summary>Respuesta de POST /api/aog/config/{seccion}.</summary>
public sealed class ConfigResultado
{
    [JsonPropertyName("ok")]    public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

// -------------------------------------------------------------------- cliente

public sealed class ConfigVehiculoClient
{
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>BOM (U+FEFF) y zero-width space (U+200B): los mete EmbedIO
    /// adelante del JSON. Se escriben por código y no como literal para que no
    /// queden caracteres invisibles en el fuente.</summary>
    private static readonly char[] Basura = { (char)0xFEFF, (char)0x200B };

    private readonly string _baseUrl;

    public ConfigVehiculoClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Base del Hub (la usan las pestañas para el teclado nativo).</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>
    /// Snapshot completo. null = el Hub no respondió (o timeout);
    /// Ok=false = respondió pero el servicio de configuración no está.
    /// La UI tiene que mostrar mensajes distintos para cada caso, igual que
    /// hace config.js ("Sin conexión" vs "Servicio de configuración no
    /// disponible").
    /// </summary>
    public async Task<ConfigSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/config", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ConfigSnapshot>(Limpio(json), _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>
    /// POST /api/aog/config/{seccion}. El `body` va tal cual al wire, así que
    /// tiene que venir con las claves en snake_case (p. ej.
    /// `new { vehicle_type = 1, is_vehicle_image = false }`). El backend
    /// aplica MERGE: lo que no viaja, no se toca.
    /// null = no respondió.
    /// </summary>
    public async Task<ConfigResultado?> GuardarAsync(string seccion, object body, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(seccion)) return null;
        try
        {
            string txt = JsonSerializer.Serialize(body ?? new { });
            using var contenido = new StringContent(txt, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/aog/config/" + seccion, contenido, ct)
                                        .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ConfigResultado>(Limpio(json), _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>
    /// POST /api/aog/config/secciones/preparar — réplica del Enter nativo de la
    /// pestaña Secciones: CON LOTE ABIERTO APAGA LOS MASTERS Auto/Manual de
    /// sección. Es un efecto de lado REAL y buscado (editar la geometría con el
    /// corte activo deja el aplicador en un estado raro), así que la pestaña lo
    /// dispara al entrar igual que la página HTML.
    /// Fire-and-forget: si falla no se avisa nada — no es un guardado.
    /// </summary>
    public Task<ConfigResultado?> PrepararSeccionesAsync(CancellationToken ct = default)
        => GuardarAsync("secciones/preparar", new { }, ct);

    /// <summary>
    /// POST /api/aog/config/rolido/accion — acciones INMEDIATAS del cero de
    /// rolido: "zero" | "quitar" | "subir" | "bajar" | "reset_imu". Aplican y
    /// persisten al toque en el motor (no esperan a ningún botón Guardar).
    /// null = no respondió; ok:false con error "sin-imu" cuando la acción
    /// necesita dato de IMU y no lo hay.
    /// </summary>
    public async Task<ConfigRolidoAccionResult?> PostRolidoAccionAsync(string accion, CancellationToken ct = default)
    {
        try
        {
            string txt = JsonSerializer.Serialize(new { accion = accion ?? "" });
            using var contenido = new StringContent(txt, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/aog/config/rolido/accion", contenido, ct)
                                        .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ConfigRolidoAccionResult>(Limpio(json), _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>
    /// GET /api/aog/graph-correction — la muestra en vivo del rolido (poll de la
    /// pestaña Rolido, 500 ms). null = el motor no contestó: quien llama tiene
    /// que CONSERVAR el último cuadro, no blanquear (un timeout no es "sin IMU").
    /// </summary>
    public async Task<CorrectionSample?> GetCorrectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/graph-correction", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CorrectionSample>(Limpio(json), _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Teclado nativo de PilotX (misma señal HTTP que mandan las
    /// páginas del Hub). Catch mudo a propósito: sin teclado el campo se sigue
    /// pudiendo editar con uno físico.</summary>
    public async Task TecladoAsync(bool abrir, bool numerico = true, string titulo = "")
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string body = abrir
                ? "{\"numerico\":" + (numerico ? "true" : "false") + ",\"titulo\":"
                  + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_baseUrl + "api/teclado/" + (abrir ? "abrir" : "cerrar"),
                                                contenido, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private static string Limpio(string json) => (json ?? "").TrimStart(Basura).TrimStart();
}
