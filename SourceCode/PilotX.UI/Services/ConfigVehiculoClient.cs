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

public sealed class ConfigTramSec
{
    [JsonPropertyName("tram_width")]           public double? TramWidth { get; set; }
    [JsonPropertyName("display_tram_control")] public bool DisplayTramControl { get; set; }
    [JsonPropertyName("outer_inverted")]       public bool OuterInverted { get; set; }
}

/// <summary>
/// Snapshot de GET /api/aog/config. Solo se declaran las secciones que ya
/// consume alguna pestaña nativa; las que faltan (switches, relay, máquina,
/// rumbo, rolido, uturn, display, botones) se agregan cuando se porte su
/// pestaña — el JSON extra se ignora sin romper nada.
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
    [JsonPropertyName("tram")]        public ConfigTramSec? Tram { get; set; }
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
