// ============================================================================
// FlowXClient.cs
//
// Cliente HTTP del modulo FlowX (corte + dosis para pulverizadoras):
//   GET    /api/flowx/config             -> FlowXConfig (nodos/productos/cables)
//   POST   /api/flowx/config             <- FlowXConfig ENTERA (reemplazo total)
//   GET    /api/flowx/live               -> FlowXLiveSnapshot (caudal, PWM, PID)
//   GET    /api/flowx/nodos              -> nodos vistos por MQTT en la LAN
//   POST   /api/flowx/{uid}/cmd?verb=..  -> agp/flow/{uid}/cmd/{verb}
//   POST   /api/flowx/{uid}/config-push  -> agp/flow/{uid}/config (camelCase!)
//   GET    /api/flowx/{uid}/{autotune|calibrar|caracterizar}
//   DELETE /api/flowx/{uid}/caracterizar
//   GET    /api/aog/state                -> secciones / ancho / velocidad
//
// QUE QUEDO NATIVO: todo lo que hacia js/flowx.js — el monitor live y, desde
// 2026-08-16, tambien el EDITOR (FlowXEditorPanel). El WebView no se instancia
// mas por FlowX.
// QUE SIGUE EN HTML: pages/flowx.html + js/flowx.js, intactos, porque los usa
// la PWA del celular (regla del repo: las paginas HTML no se borran).
//
// El DTO de config es de FIDELIDAD COMPLETA a proposito: el POST reemplaza el
// archivo entero, asi que cualquier campo que el DTO no conozca se perderia en
// el round-trip (incluido "ignorados", que la UI no muestra).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ---------------------------------------------------------------------------
//  Config persistida (flowX.json)
// ---------------------------------------------------------------------------

/// <summary>Una "reguladora": caudalimetro + valvula/motor con PID propio.</summary>
public sealed class FlowXProducto
{
    [JsonPropertyName("id")]           public int    Id          { get; set; }
    [JsonPropertyName("nombre")]       public string? Nombre     { get; set; } = "Producto";
    [JsonPropertyName("meter_cal")]    public double MeterCal    { get; set; } = 100.0;
    [JsonPropertyName("dosis_lha")]    public double DosisLha    { get; set; } = 100.0;
    [JsonPropertyName("pwm_min")]      public int    PwmMin      { get; set; } = 40;
    [JsonPropertyName("pwm_max")]      public int    PwmMax      { get; set; } = 4095;
    [JsonPropertyName("kp")]           public double Kp          { get; set; } = 1.0;
    [JsonPropertyName("ki")]           public double Ki          { get; set; } = 0.1;
    [JsonPropertyName("kd")]           public double Kd          { get; set; }
    [JsonPropertyName("modo_manual")]  public bool   ModoManual  { get; set; }
    [JsonPropertyName("manual_lmin")]  public double ManualLmin  { get; set; }
    [JsonPropertyName("paso_lha")]     public double PasoLha     { get; set; } = 5.0;
    [JsonPropertyName("paso_lmin")]    public double PasoLmin    { get; set; } = 1.0;
    [JsonPropertyName("tipo")]         public string? Tipo       { get; set; } = "valvula";
    [JsonPropertyName("flow_index")]   public int    FlowIndex   { get; set; }
    [JsonPropertyName("invert_motor")] public bool   InvertMotor { get; set; }
}

/// <summary>Una salida del PCA9685 (un "corte") atada a una seccion de PilotX.
/// Un corte que agrupa varias secciones aparece repetido con el mismo cable.</summary>
public sealed class FlowXCableMap
{
    [JsonPropertyName("cable")]       public int Cable      { get; set; }
    [JsonPropertyName("seccion_aog")] public int SeccionAog { get; set; }
}

public sealed class FlowXNodoConfig
{
    [JsonPropertyName("uid")]              public string? Uid        { get; set; }
    [JsonPropertyName("nombre")]           public string? Nombre     { get; set; } = "Nodo FlowX";
    [JsonPropertyName("habilitado")]       public bool   Habilitado  { get; set; } = true;
    [JsonPropertyName("ancho_barra_m")]    public double AnchoBarraM { get; set; }
    [JsonPropertyName("is_3wire")]         public bool   Is3Wire     { get; set; }
    [JsonPropertyName("invert_relay")]     public bool   InvertRelay { get; set; }
    [JsonPropertyName("invert_motor")]     public bool   InvertMotor { get; set; }
    /// <summary>-1 = salida dedicada del firmware, 0 = sin master, 1..N = ese corte.</summary>
    [JsonPropertyName("master_cable")]     public int    MasterCable { get; set; } = -1;
    /// <summary>Cuántas válvulas tiene la barra. 0 = no declarado: se infiere de
    /// cables[] (configs anteriores y las que guarda la PWA). Se persiste desde
    /// que hay asignación manual: si el operario deja cortes sin usar, contar
    /// los cables asignados perdía la cantidad real de salidas.</summary>
    [JsonPropertyName("cortes")]           public int    Cortes      { get; set; }
    /// <summary>SIEMPRE 10 enteros: -1 global, 0 = 2 cables, 1 = 3 cables.</summary>
    [JsonPropertyName("section_is_3wire")] public List<int>? SectionIs3Wire { get; set; }
    [JsonPropertyName("productos")]        public List<FlowXProducto>? Productos { get; set; }
    [JsonPropertyName("cables")]           public List<FlowXCableMap>? Cables    { get; set; }
}

public sealed class FlowXConfig
{
    [JsonPropertyName("enabled")]   public bool Enabled { get; set; }
    [JsonPropertyName("nodos")]     public List<FlowXNodoConfig>? Nodos { get; set; }
    /// <summary>UIDs que el operario decidio ignorar. La UI no los muestra pero
    /// tienen que sobrevivir al round-trip: el POST reemplaza el archivo.</summary>
    [JsonPropertyName("ignorados")] public List<string>? Ignorados { get; set; }
}

// ---------------------------------------------------------------------------
//  Telemetria live
// ---------------------------------------------------------------------------

public sealed class FlowXNodoLive
{
    [JsonPropertyName("uid")]          public string? Uid       { get; set; }
    [JsonPropertyName("nombre")]       public string? Nombre    { get; set; }
    [JsonPropertyName("online")]       public bool   Online     { get; set; }
    [JsonPropertyName("caudal_lmin")]  public double CaudalLmin { get; set; }
    [JsonPropertyName("target_lmin")]  public double TargetLmin { get; set; }
    [JsonPropertyName("pwm")]          public int    Pwm        { get; set; }
    [JsonPropertyName("pid_estado")]   public string? PidEstado { get; set; }
    /// <summary>Pulsos crudos del ISR. Si la bomba gira y esto no sube, el
    /// caudalimetro no engancha (sensor / cable / nivel).</summary>
    [JsonPropertyName("pulsos")]       public long   Pulsos     { get; set; }
}

public sealed class FlowXLiveSnapshot
{
    [JsonPropertyName("monitoreo_activo")] public bool MonitoreoActivo { get; set; }
    [JsonPropertyName("nodos")]            public List<FlowXNodoLive>? Nodos { get; set; }
}

// ---------------------------------------------------------------------------
//  Descubrimiento LAN (registry MQTT)
// ---------------------------------------------------------------------------

/// <summary>OJO: `nombre` aca es el TYPE del topic ("flow"), no el nombre que
/// el operario le puso en la config. Para mostrar se usa el uid.</summary>
public sealed class FlowXNodoLan
{
    [JsonPropertyName("uid")]         public string? Uid        { get; set; }
    [JsonPropertyName("nombre")]      public string? Nombre     { get; set; }
    [JsonPropertyName("ip")]          public string? Ip         { get; set; }
    [JsonPropertyName("firmware")]    public string? Firmware   { get; set; }
    [JsonPropertyName("online")]      public bool   Online      { get; set; }
    [JsonPropertyName("uptime")]      public long   Uptime      { get; set; }
    [JsonPropertyName("boot_reason")] public string? BootReason { get; set; }
    [JsonPropertyName("safe_mode")]   public bool   SafeMode    { get; set; }
    [JsonPropertyName("crash_count")] public int    CrashCount  { get; set; }
}

public sealed class FlowXNodosResp
{
    [JsonPropertyName("ok")]    public bool Ok { get; set; }
    [JsonPropertyName("nodos")] public List<FlowXNodoLan>? Nodos { get; set; }
}

/// <summary>Lo que el editor consume de /api/aog/state.</summary>
public sealed class FlowXAogState
{
    [JsonPropertyName("num_sections")]           public int    NumSections { get; set; }
    [JsonPropertyName("tool_width")]             public double ToolWidth   { get; set; }
    [JsonPropertyName("avg_speed")]              public double AvgSpeed    { get; set; }
    [JsonPropertyName("is_job_started")]         public bool   IsJobStarted { get; set; }
    [JsonPropertyName("worked_area_total_m2")]   public double WorkedAreaTotalM2 { get; set; }
    [JsonPropertyName("actual_area_covered_m2")] public double ActualAreaCoveredM2 { get; set; }
}

// ---------------------------------------------------------------------------
//  Resultados de escritura / comandos
// ---------------------------------------------------------------------------

/// <summary>Resultado de POST. `Error` trae AGP-CFG-001 cuando el backend
/// rechaza la config; `Mensaje`/`Detalle` son el friendly y el tecnico.</summary>
public sealed class FlowXOpResult
{
    [JsonPropertyName("ok")]      public bool    Ok      { get; set; }
    [JsonPropertyName("error")]   public string? Error   { get; set; }
    [JsonPropertyName("mensaje")] public string? Mensaje { get; set; }
    [JsonPropertyName("detalle")] public string? Detalle { get; set; }
    [JsonPropertyName("topic")]   public string? Topic   { get; set; }

    /// <summary>Texto corto para el renglon de estado (codigo + friendly).</summary>
    public string Texto()
    {
        if (Ok) return "";
        if (!string.IsNullOrEmpty(Error))
            return Error + (string.IsNullOrEmpty(Mensaje) ? "" : " · " + Mensaje);
        return string.IsNullOrEmpty(Mensaje) ? "fallo MQTT" : Mensaje!;
    }

    public static FlowXOpResult Fallo(string msg) => new FlowXOpResult { Ok = false, Mensaje = msg };
}

// ---------------------------------------------------------------------------
//  Cliente
// ---------------------------------------------------------------------------

public sealed class FlowXClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public FlowXClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    // ---- lecturas ---------------------------------------------------------

    public async Task<FlowXConfig?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/flowx/config", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FlowXConfig>(json, _jsonOpts);
        }
        catch { return null; }
    }

    public async Task<FlowXLiveSnapshot?> GetLiveAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/flowx/live", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FlowXLiveSnapshot>(json, _jsonOpts);
        }
        catch { return null; }
    }

    public async Task<List<FlowXNodoLan>> GetNodosAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/flowx/nodos", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new List<FlowXNodoLan>();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var body = JsonSerializer.Deserialize<FlowXNodosResp>(json, _jsonOpts);
            return body?.Nodos ?? new List<FlowXNodoLan>();
        }
        catch { return new List<FlowXNodoLan>(); }
    }

    public async Task<FlowXAogState?> GetAogStateAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/state", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FlowXAogState>(json, _jsonOpts);
        }
        catch { return null; }
    }

    // ---- escrituras -------------------------------------------------------

    /// <summary>POST de la config ENTERA (reemplazo total). Devuelve el cuerpo
    /// del error para poder mostrar AGP-CFG-001 con su detalle.</summary>
    public async Task<FlowXOpResult> SaveConfigAsync(FlowXConfig cfg, CancellationToken ct = default)
    {
        try
        {
            Normalizar(cfg);
            string body = JsonSerializer.Serialize(cfg);
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/flowx/config", contenido, ct).ConfigureAwait(false);
            string txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<FlowXOpResult>(txt, _jsonOpts);
            return r ?? FlowXOpResult.Fallo("respuesta vacia");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return FlowXOpResult.Fallo("cancelado");
        }
        catch { return FlowXOpResult.Fallo("Error de red"); }
    }

    /// <summary>Listas en null → listas vacias antes de subir. El POST se
    /// persiste tal cual: un "productos": null en el archivo hace que el bridge
    /// tenga que defenderse en cada lectura, y el JS nunca mandaba nulls.</summary>
    private static void Normalizar(FlowXConfig cfg)
    {
        cfg.Nodos ??= new List<FlowXNodoConfig>();
        cfg.Ignorados ??= new List<string>();
        foreach (var n in cfg.Nodos)
        {
            if (n == null) continue;
            n.Productos ??= new List<FlowXProducto>();
            n.Cables ??= new List<FlowXCableMap>();
            // section_is_3wire SIEMPRE con 10 enteros: mandar menos tiene
            // comportamiento indefinido en el firmware.
            var arr = new List<int>(10);
            for (int i = 0; i < 10; i++)
            {
                int v = (n.SectionIs3Wire != null && i < n.SectionIs3Wire.Count) ? n.SectionIs3Wire[i] : -1;
                arr.Add(v == 0 || v == 1 ? v : -1);
            }
            n.SectionIs3Wire = arr;
        }
    }

    /// <summary>Publica agp/flow/{uid}/cmd/{verb} con el payload crudo.</summary>
    public Task<FlowXOpResult> SendCmdAsync(string uid, string verb, object payload,
                                            CancellationToken ct = default)
        => PostRawAsync("api/flowx/" + Uri.EscapeDataString(uid ?? "") + "/cmd?verb="
                        + Uri.EscapeDataString(verb ?? ""),
                        JsonSerializer.Serialize(payload ?? new { }), ct);

    /// <summary>Publica agp/flow/{uid}/config. OJO: el payload es camelCase —
    /// es contrato del firmware, no del wire de PilotX. Un campo con otro
    /// nombre lo ignora el nodo en silencio.</summary>
    public Task<FlowXOpResult> PushConfigAsync(string uid, double meterCal, bool is3Wire,
                                               bool invertRelay, bool invertMotor,
                                               IReadOnlyList<int> sectionIs3Wire,
                                               CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append("{\"meterCal\":").Append(meterCal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"is3Wire\":").Append(is3Wire ? "true" : "false");
        sb.Append(",\"invertRelay\":").Append(invertRelay ? "true" : "false");
        sb.Append(",\"invertMotor\":").Append(invertMotor ? "true" : "false");
        sb.Append(",\"sectionIs3Wire\":[");
        for (int i = 0; i < 10; i++)
        {
            if (i > 0) sb.Append(',');
            int v = (sectionIs3Wire != null && i < sectionIs3Wire.Count) ? sectionIs3Wire[i] : -1;
            if (v != 0 && v != 1) v = -1;
            sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append("]}");
        return PostRawAsync("api/flowx/" + Uri.EscapeDataString(uid ?? "") + "/config-push", sb.ToString(), ct);
    }

    private async Task<FlowXOpResult> PostRawAsync(string ruta, string body, CancellationToken ct)
    {
        try
        {
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + ruta, contenido, ct).ConfigureAwait(false);
            string txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<FlowXOpResult>(txt, _jsonOpts);
            return r ?? FlowXOpResult.Fallo("respuesta vacia");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return FlowXOpResult.Fallo("cancelado");
        }
        catch { return FlowXOpResult.Fallo("Error de red"); }
    }

    /// <summary>Limpia el resultado cacheado de caracterizacion. Si no se llama,
    /// el poll siguiente lee una corrida VIEJA como si fuera nueva.</summary>
    public async Task ClearCaracterizarAsync(string uid, CancellationToken ct = default)
    {
        try
        {
            using var _ = await _http.DeleteAsync(
                _baseUrl + "api/flowx/" + Uri.EscapeDataString(uid ?? "") + "/caracterizar", ct)
                .ConfigureAwait(false);
        }
        catch { }   // un fallo de limpieza no bloquea el start (igual que el JS)
    }

    // ---- resultados asincronicos del firmware ------------------------------

    /// <summary>Un GET corto a /{uid}/{kind}. Devuelve el `result` clonado si
    /// ya llego, null si todavia no hay.</summary>
    public async Task<JsonElement?> GetResultAsync(string uid, string kind, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                _baseUrl + "api/flowx/" + Uri.EscapeDataString(uid ?? "") + "/" + kind, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(txt);
            if (!doc.RootElement.TryGetProperty("has_result", out var hr) || !hr.GetBoolean()) return null;
            if (!doc.RootElement.TryGetProperty("result", out var res)) return null;
            return res.Clone();
        }
        catch { return null; }
    }

    /// <summary>Serie de GETs cortos cada 500 ms hasta `timeoutMs`. NO se hace
    /// un GET largo: el HttpClient corta a los 3 s y el operario que cierra el
    /// panel tiene que poder abandonar el poll sin colgar la UI.</summary>
    public async Task<JsonElement?> PollResultAsync(string uid, string kind, int timeoutMs,
                                                    CancellationToken ct = default)
    {
        var t0 = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var r = await GetResultAsync(uid, kind, ct).ConfigureAwait(false);
            if (r.HasValue) return r;
            if ((DateTime.UtcNow - t0).TotalMilliseconds > timeoutMs) return null;
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    // ---- teclado nativo ---------------------------------------------------

    /// <summary>Misma senal HTTP que mandan las paginas del Hub. Catch mudo a
    /// proposito: sin teclado nativo el campo sigue editable.</summary>
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
}
