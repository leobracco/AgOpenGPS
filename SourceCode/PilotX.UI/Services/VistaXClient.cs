// VistaXClient.cs
//
// Cliente HTTP minimo para el VistaXController:
//   GET /api/vistax/live ->
//     {
//       trenes: [{
//         tren, nombre, objetivo (SEM/M),
//         surcos: [{ tren, bajada, tipo, estado, spm (sem/min), sem_m,
//                    objetivo (sem/min), ratio_objetivo, uid, cable, muted,
//                    seccion_cortada, last_seen_iso }]
//       }],
//       spm_promedio (sem/min), velocidad (km/h), distancia_entre_surcos (m),
//       surcos_activos, fallas_activas, has_alarm, alarm_message,
//       nombre_implemento, tolerancia_desvio, monitoreo_activo,
//       nodos: [{ uid, online, sensors_reporting, last_seen_iso }]
//     }
//
// UNIDADES (la trampa de este endpoint): el wire mezcla dos unidades. `spm` y
// el `objetivo` del SURCO son por MINUTO — caudal, cambia con la velocidad. El
// `objetivo` del TREN y `sem_m` son por METRO — densidad, es lo que el operario
// decide y lo unico que se muestra (regla del repo: sem/m, sem/10m, sem/ha).
//
// Reemplaza al pollLive() de vistax.js.
//
// Desde 2026-08-16 este mismo cliente sirve TAMBIEN al editor nativo
// (VistaXEditorPanel): insumo & calibracion, implemento y config. Con eso el
// boton "Configurar" del VistaXPanel dejo de abrir pages/vistax.html y PilotX
// ya no despierta Chromium por VistaX.
//
// QUE SIGUE EN HTML: pages/vistax.html + vistax.js + vistax-insumo.js quedan
// intactos — los usa la PWA del celular (regla del repo: las paginas no se
// borran). El editor nativo consume EXACTAMENTE los mismos endpoints.
//
// CASING DEL WIRE: snake_case en cada [JsonPropertyName]; sin el atributo,
// PropertyNameCaseInsensitive NO cubre underscores y el DTO deserializa ceros
// en silencio. Los DTOs que se vuelven a mandar por PUT llevan
// [JsonExtensionData] para no pisar con nada los campos que el backend tenga y
// este cliente todavia no modele (topicos MQTT, mute por sensor, etc.).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class VistaXSurcoLive
{
    [JsonPropertyName("tren")]          public int     Tren          { get; set; }
    [JsonPropertyName("bajada")]        public int     Bajada        { get; set; }
    [JsonPropertyName("tipo")]          public string? Tipo          { get; set; }
    [JsonPropertyName("estado")]        public string? Estado        { get; set; }
    /// <summary>Lectura del sensor en SEM/MIN. Es un caudal: a 6 y a 9 km/h la
    /// MISMA siembra da dos numeros distintos. NO se le muestra al operario como
    /// dato principal — para eso esta <see cref="SemM"/>.</summary>
    [JsonPropertyName("spm")]           public double  Spm           { get; set; }
    /// <summary>SEMILLAS POR METRO — el backend ya la calcula
    /// (VistaXLiveService: spm / metros-por-minuto). Es la unidad con la que se
    /// piensa la siembra y la que va a la pantalla. 0 con el tractor detenido:
    /// sin avance no existe densidad.</summary>
    [JsonPropertyName("sem_m")]         public double  SemM          { get; set; }
    /// <summary>Objetivo del surco en SEM/MIN (el backend convierte el sem/m
    /// configurado con la velocidad viva). Para pantalla se vuelve a sem/m.</summary>
    [JsonPropertyName("objetivo")]      public double  Objetivo      { get; set; }
    [JsonPropertyName("ratio_objetivo")] public double  RatioObjetivo { get; set; }
    [JsonPropertyName("uid")]           public string? Uid           { get; set; }
    [JsonPropertyName("cable")]         public int     Cable         { get; set; }
    [JsonPropertyName("muted")]         public bool    Muted         { get; set; }
    /// <summary>La seccion de PilotX que alimenta este surco esta apagada
    /// (cabecera, fuera de boundary, master OFF). No es falla del sensor.</summary>
    [JsonPropertyName("seccion_cortada")] public bool  SeccionCortada { get; set; }
    /// <summary>Timestamp ISO de la ultima telemetria del sensor (para "hace Ns").</summary>
    [JsonPropertyName("last_seen_iso")]   public string? LastSeenIso  { get; set; }
}

public sealed class VistaXTrenLive
{
    [JsonPropertyName("tren")]     public int     Tren     { get; set; }
    [JsonPropertyName("nombre")]   public string? Nombre   { get; set; }
    /// <summary>OJO: el objetivo del TREN viene en SEM/M (sale de
    /// setup.densidad_objetivo / objetivos_tren, o de la dosis viva de QuantiX).
    /// El del SURCO, en cambio, viene en sem/min. No mezclar: dividir este por
    /// la velocidad lo deja 100 veces mas chico.</summary>
    [JsonPropertyName("objetivo")] public double  Objetivo { get; set; }
    [JsonPropertyName("surcos")]   public List<VistaXSurcoLive>? Surcos { get; set; }
}

public sealed class VistaXNodoLive
{
    [JsonPropertyName("uid")]              public string? Uid              { get; set; }
    [JsonPropertyName("online")]           public bool    Online           { get; set; }
    [JsonPropertyName("sensors_reporting")] public int     SensorsReporting { get; set; }
    [JsonPropertyName("last_seen_iso")]      public string? LastSeenIso      { get; set; }
}

public sealed class VistaXLiveSnapshot
{
    [JsonPropertyName("trenes")]           public List<VistaXTrenLive>? Trenes       { get; set; }
    [JsonPropertyName("spm_promedio")]      public double? SpmPromedio                { get; set; }
    [JsonPropertyName("surcos_activos")]    public int     SurcosActivos              { get; set; }
    [JsonPropertyName("fallas_activas")]    public int     FallasActivas              { get; set; }
    [JsonPropertyName("has_alarm")]         public bool    HasAlarm                   { get; set; }
    [JsonPropertyName("alarm_message")]     public string? AlarmMessage               { get; set; }
    [JsonPropertyName("nombre_implemento")] public string? NombreImplemento           { get; set; }
    [JsonPropertyName("tolerancia_desvio")] public double? ToleranciaDesvio           { get; set; }
    [JsonPropertyName("monitoreo_activo")]  public bool    MonitoreoActivo            { get; set; }
    /// <summary>km/h — necesaria para pasar de sem/min (lo que entrega el
    /// backend) a SEM/M, que es como se piensa la siembra.</summary>
    [JsonPropertyName("velocidad")]         public double  Velocidad                  { get; set; }
    /// <summary>Distancia entre surcos (m). Con ella y sem/m sale sem/ha:
    /// sem/ha = sem/m · 10000 / distancia.</summary>
    [JsonPropertyName("distancia_entre_surcos")] public double DistanciaEntreSurcos   { get; set; }
    [JsonPropertyName("nodos")]            public List<VistaXNodoLive>? Nodos        { get; set; }
}

// ---------------------------------------------------------------------------
//  Editor: resultado de una escritura (PUT/POST)
// ---------------------------------------------------------------------------

/// <summary>Resultado de una escritura. `Error` trae el codigo AGP-CFG-001
/// cuando el backend rechaza la config; `Mensaje`/`Detalle` son el friendly y
/// el tecnico que el HTML pintaba como "codigo · mensaje — detalle".</summary>
public sealed class VxOpResult
{
    [JsonPropertyName("ok")]      public bool    Ok      { get; set; }
    [JsonPropertyName("error")]   public string? Error   { get; set; }
    [JsonPropertyName("mensaje")] public string? Mensaje { get; set; }
    [JsonPropertyName("detalle")] public string? Detalle { get; set; }

    /// <summary>Texto listo para el renglon de estado del pie.</summary>
    public string Texto()
    {
        if (Ok) return "";
        if (!string.IsNullOrEmpty(Error))
            return Error + (string.IsNullOrEmpty(Mensaje) ? "" : " · " + Mensaje)
                         + (string.IsNullOrEmpty(Detalle) ? "" : " — " + Detalle);
        return string.IsNullOrEmpty(Mensaje) ? "error" : Mensaje!;
    }

    public static VxOpResult Fallo(string msg) => new VxOpResult { Ok = false, Mensaje = msg };
}

// ---------------------------------------------------------------------------
//  Editor: config global de VistaX (GET/PUT /api/vistax/config)
// ---------------------------------------------------------------------------

/// <summary>Los campos MQTT (broker/puerto/credenciales/topicos) NO se editan:
/// la conexion con los nodos la maneja CoreX con su broker embebido. Viajan
/// igual en el PUT para no borrarlos (mismo merge que hacia readConfigFromForm
/// en el JS).</summary>
public sealed class VxConfig
{
    [JsonPropertyName("enabled")]                 public bool    Enabled              { get; set; } = true;
    [JsonPropertyName("broker_address")]          public string  BrokerAddress        { get; set; } = "127.0.0.1";
    [JsonPropertyName("broker_port")]             public int     BrokerPort           { get; set; } = 1883;
    [JsonPropertyName("client_id")]               public string  ClientId             { get; set; } = "PilotX_VistaX";
    [JsonPropertyName("username")]                public string  Username             { get; set; } = "";
    [JsonPropertyName("password")]                public string  Password             { get; set; } = "";
    [JsonPropertyName("use_tls")]                 public bool    UseTls               { get; set; }
    [JsonPropertyName("telemetria_topic")]        public string  TelemetriaTopic      { get; set; } = "vistax/nodos/telemetria";
    [JsonPropertyName("speed_topic")]             public string  SpeedTopic           { get; set; } = "aog/machine/speed";
    [JsonPropertyName("sections_topic")]          public string  SectionsTopic        { get; set; } = "sections/state";
    [JsonPropertyName("implemento_json_path")]    public string  ImplementoJsonPath   { get; set; } = "";
    [JsonPropertyName("ui_update_interval_ms")]   public int     UiUpdateIntervalMs   { get; set; } = 500;
    [JsonPropertyName("sensor_timeout_ms")]       public int     SensorTimeoutMs      { get; set; } = 3000;
    [JsonPropertyName("log_to_field_record")]     public bool    LogToFieldRecord     { get; set; }
    [JsonPropertyName("metodo_inicio")]           public string  MetodoInicio         { get; set; } = "sensores";
    [JsonPropertyName("umbral_sensores_activos")] public int     UmbralSensoresActivos{ get; set; } = 3;
    [JsonPropertyName("tiempo_confirmacion_ms")]  public int     TiempoConfirmacionMs { get; set; } = 500;
    [JsonPropertyName("alarm_muted")]             public bool    AlarmMuted           { get; set; }
    [JsonPropertyName("log_output_drive")]        public string  LogOutputDrive       { get; set; } = "";

    /// <summary>Todo lo que el backend agregue y este DTO no modele. Sin esto,
    /// guardar desde el panel lo PISARIA con nada.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ---------------------------------------------------------------------------
//  Editor: implemento VistaX (GET/PUT /api/vistax/implemento)
// ---------------------------------------------------------------------------

public sealed class VxSensorCfg
{
    [JsonPropertyName("uid")]         public string Uid        { get; set; } = "";
    [JsonPropertyName("cable")]       public int    Cable      { get; set; }
    [JsonPropertyName("pin")]         public int    Pin        { get; set; }
    [JsonPropertyName("bajada")]      public int    Bajada     { get; set; }
    [JsonPropertyName("surco_desde")] public int    SurcoDesde { get; set; }
    [JsonPropertyName("surco_hasta")] public int    SurcoHasta { get; set; }
    [JsonPropertyName("tipo")]        public string Tipo       { get; set; } = "semilla";
    [JsonPropertyName("nombre")]      public string Nombre     { get; set; } = "";
    [JsonPropertyName("tren")]        public int    Tren       { get; set; }
    [JsonPropertyName("is_active")]   public bool   IsActive   { get; set; } = true;
    [JsonPropertyName("seccion_aog")] public int    SeccionAog { get; set; }
    /// <summary>Silenciado desde el detalle del monitor. La pagina HTML lo
    /// PERDIA al guardar el implemento (armaba la fila de cero); acá la fila
    /// cargada se edita en su lugar, asi el mute sobrevive al guardado.</summary>
    [JsonPropertyName("muted")]       public bool   Muted      { get; set; }
    /// <summary>Objetivo propio del sensor (0 = hereda del tren). Se edita
    /// desde las barras del monitor — mismo motivo que `muted`.</summary>
    [JsonPropertyName("objetivo")]    public double Objetivo   { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VxTrenCfg
{
    [JsonPropertyName("id")]     public int     Id     { get; set; }
    [JsonPropertyName("nombre")] public string? Nombre { get; set; }
    [JsonPropertyName("surcos")] public int     Surcos { get; set; }
}

public sealed class VxSetup
{
    [JsonPropertyName("objetivo_fuente")]        public string  ObjetivoFuente       { get; set; } = "quantix";
    [JsonPropertyName("densidad_objetivo")]      public double  DensidadObjetivo     { get; set; }
    [JsonPropertyName("tolerancia_desvio")]      public double  ToleranciaDesvio     { get; set; }
    [JsonPropertyName("distancia_entre_surcos")] public double  DistanciaEntreSurcos { get; set; }
    [JsonPropertyName("factor_k_default")]       public double  FactorK              { get; set; }
    [JsonPropertyName("objetivos_tren")]         public Dictionary<string, double>? ObjetivosTren { get; set; }
    [JsonPropertyName("total_surcos")]           public int     TotalSurcos          { get; set; }
    [JsonPropertyName("secciones_aog")]          public int     SeccionesAog         { get; set; }
    [JsonPropertyName("ancho_implemento")]       public double  AnchoImplemento      { get; set; }
    [JsonPropertyName("max_densidad_sensor")]    public double  MaxDensidadSensor    { get; set; } = 20;
    [JsonPropertyName("insumo_activo_id")]       public string  InsumoActivoId       { get; set; } = "";
    [JsonPropertyName("torres")]                 public int     Torres               { get; set; }
    [JsonPropertyName("surcos_por_torre")]       public int     SurcosPorTorre       { get; set; }
    [JsonPropertyName("vista_modo_default")]     public string  VistaModoDefault     { get; set; } = "surcos";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VxImplemento
{
    [JsonPropertyName("id")]             public string  Id     { get; set; } = "";
    [JsonPropertyName("nombre")]         public string  Nombre { get; set; } = "";
    [JsonPropertyName("setup")]          public VxSetup Setup  { get; set; } = new VxSetup();
    [JsonPropertyName("trenes")]         public List<VxTrenCfg>?   Trenes        { get; set; }
    [JsonPropertyName("mapeo_sensores")] public List<VxSensorCfg>? MapeoSensores { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>GET /api/vistax/implemento devuelve `{path, implemento}`, pero el
/// JS toleraba tambien el implemento pelado. Se cubren los dos.</summary>
public sealed class VxImplementoCargado
{
    public string Path = "";
    public VxImplemento Implemento = new VxImplemento();
}

// ---------------------------------------------------------------------------
//  Editor: implemento CENTRAL (GET /api/implemento) — solo lectura
// ---------------------------------------------------------------------------

public sealed class VxCentralTren
{
    [JsonPropertyName("id")]     public int     Id     { get; set; }
    [JsonPropertyName("nombre")] public string? Nombre { get; set; }
}

public sealed class VxImplementoCentral
{
    [JsonPropertyName("nombre")]                   public string? Nombre               { get; set; }
    [JsonPropertyName("ancho_total_m")]            public double  AnchoTotalM          { get; set; }
    [JsonPropertyName("numero_surcos")]            public int     NumeroSurcos         { get; set; }
    [JsonPropertyName("distancia_entre_surcos_m")] public double  DistanciaEntreSurcosM{ get; set; }
    [JsonPropertyName("trenes")]                   public List<VxCentralTren>? Trenes  { get; set; }
    [JsonPropertyName("surcos")]                   public List<JsonElement>?   Surcos  { get; set; }
}

public sealed class VxCentralResp
{
    [JsonPropertyName("ok")]         public bool                 Ok         { get; set; }
    [JsonPropertyName("implemento")] public VxImplementoCentral? Implemento { get; set; }
}

// ---------------------------------------------------------------------------
//  Editor: catalogo de tipos de sensor + insumos + calibracion
// ---------------------------------------------------------------------------

public sealed class VxTipoSensor
{
    [JsonPropertyName("id")]       public string  Id       { get; set; } = "";
    [JsonPropertyName("etiqueta")] public string? Etiqueta { get; set; }
}

public sealed class VxInsumo
{
    [JsonPropertyName("id")]      public string  Id      { get; set; } = "";
    [JsonPropertyName("nombre")]  public string? Nombre  { get; set; }
    [JsonPropertyName("cultivo")] public string? Cultivo { get; set; }
    [JsonPropertyName("densidad_objetivo_sem_m")]          public double DensidadObjetivoSemM         { get; set; }
    [JsonPropertyName("densidad_asumida_saturado_sem_m")]  public double DensidadAsumidaSaturadoSemM  { get; set; }
    [JsonPropertyName("singulacion_objetivo_pct")]         public double SingulacionObjetivoPct       { get; set; }
}

public sealed class VxInsumoCatalogo
{
    [JsonPropertyName("activo_id")] public string?         ActivoId { get; set; }
    [JsonPropertyName("items")]     public List<VxInsumo>? Items    { get; set; }
}

public sealed class VxCalibState
{
    [JsonPropertyName("running")]            public bool       Running           { get; set; }
    [JsonPropertyName("modo")]               public string?    Modo              { get; set; }
    [JsonPropertyName("segundos_restantes")] public double     SegundosRestantes { get; set; }
    [JsonPropertyName("sem_m_actual")]       public double?    SemMActual        { get; set; }
    [JsonPropertyName("saturado")]           public bool       Saturado          { get; set; }
    [JsonPropertyName("muestras")]           public int        Muestras          { get; set; }
    [JsonPropertyName("surcos")]             public List<int>? Surcos            { get; set; }
    [JsonPropertyName("listo_para_aplicar")] public bool       ListoParaAplicar  { get; set; }
    [JsonPropertyName("valor_final_sem_m")]  public double     ValorFinalSemM    { get; set; }
}

/// <summary>Resultado de bajar el ZIP de sesiones del lote. En cabina no hay
/// "descarga" de browser: el archivo se guarda a disco y se avisa la ruta.</summary>
public sealed class VxZipResult
{
    public bool   Ok;
    public string Ruta  = "";
    public string Error = "";
}

public sealed class VistaXClient
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

    public VistaXClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<VistaXLiveSnapshot?> GetLiveAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/vistax/live", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<VistaXLiveSnapshot>(json, _jsonOpts);
        }
        catch { return null; }
    }

    // =======================================================================
    //  Plomeria comun del editor
    // =======================================================================

    private static readonly JsonSerializerOptions _jsonOut = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private async Task<T?> GetAsync<T>(string ruta, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + ruta, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, _jsonOpts);
        }
        catch { return null; }   // el panel pinta "sin datos", jamas explota
    }

    /// <summary>PUT/POST con cuerpo JSON. El backend contesta `{ok:true}` o
    /// `{error:"AGP-CFG-001", mensaje, detalle}` con 400 — las dos formas caen
    /// en el mismo VxOpResult.</summary>
    private async Task<VxOpResult> SendAsync(HttpMethod metodo, string ruta, string? body,
                                             CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(metodo, _baseUrl + ruta);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            VxOpResult? r = null;
            try { r = JsonSerializer.Deserialize<VxOpResult>(json, _jsonOpts); } catch { }
            if (r != null && !string.IsNullOrEmpty(r.Error)) { r.Ok = false; return r; }
            if (!resp.IsSuccessStatusCode)
                return VxOpResult.Fallo("HTTP " + (int)resp.StatusCode);
            return r ?? new VxOpResult { Ok = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return VxOpResult.Fallo("cancelado");
        }
        catch (Exception ex) { return VxOpResult.Fallo(ex.Message); }
    }

    // =======================================================================
    //  Config global (tab Config)
    // =======================================================================

    public async Task<VxConfig?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/vistax/config", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // El backend contesta 200 con {ok:false, error:"service-unavailable"}
            // cuando VistaX no esta levantado: sin este guard el panel pintaba
            // una config de DEFAULTS como si fuera la guardada.
            using (var doc = JsonDocument.Parse(json))
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out _)) return null;
            return JsonSerializer.Deserialize<VxConfig>(json, _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>PUT del DTO ENTERO. Los campos MQTT que la UI no muestra viajan
    /// tal cual salieron del GET (merge, igual que el JS).</summary>
    public Task<VxOpResult> PutConfigAsync(VxConfig cfg, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, "api/vistax/config",
                     JsonSerializer.Serialize(cfg, _jsonOut), ct);

    // =======================================================================
    //  Implemento VistaX (tab Implemento)
    // =======================================================================

    /// <summary>Tolera las dos formas de respuesta: `{path, implemento}` y el
    /// implemento pelado.</summary>
    public async Task<VxImplementoCargado?> GetImplementoAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/vistax/implemento", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) return null;
            // {ok:false, error:"service-unavailable"} — no hay implemento que
            // mostrar; devolver un DTO vacio haria creer que se cargo.
            if (raiz.TryGetProperty("error", out _) && !raiz.TryGetProperty("implemento", out _))
                return null;

            var salida = new VxImplementoCargado();
            if (raiz.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                salida.Path = p.GetString() ?? "";

            JsonElement cuerpo = raiz;
            if (raiz.TryGetProperty("implemento", out var imp) && imp.ValueKind == JsonValueKind.Object)
                cuerpo = imp;

            var dto = JsonSerializer.Deserialize<VxImplemento>(cuerpo.GetRawText(), _jsonOpts);
            if (dto == null) return null;
            dto.Setup ??= new VxSetup();
            dto.MapeoSensores ??= new List<VxSensorCfg>();
            salida.Implemento = dto;
            return salida;
        }
        catch { return null; }
    }

    /// <summary>PUT del implemento. El backend IGNORA la geometria y los trenes
    /// (los re-deriva del implemento central) y reemplaza mapeo_sensores tal
    /// cual viene.</summary>
    public Task<VxOpResult> PutImplementoAsync(VxImplemento imp, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, "api/vistax/implemento",
                     JsonSerializer.Serialize(imp, _jsonOut), ct);

    /// <summary>Implemento CENTRAL — geometria de solo lectura y los trenes que
    /// alimentan el desplegable del mapeo.</summary>
    public async Task<VxImplementoCentral?> GetImplementoCentralAsync(CancellationToken ct = default)
    {
        var r = await GetAsync<VxCentralResp>("api/implemento", ct).ConfigureAwait(false);
        return r?.Implemento;
    }

    public async Task<List<VxTipoSensor>> GetSensorTiposAsync(CancellationToken ct = default)
    {
        var r = await GetAsync<List<VxTipoSensor>>("api/vistax/sensor/tipos", ct).ConfigureAwait(false);
        return r ?? new List<VxTipoSensor>();
    }

    // =======================================================================
    //  Insumos (tab Insumo & calibracion)
    // =======================================================================

    public Task<VxInsumoCatalogo?> GetInsumosAsync(CancellationToken ct = default)
        => GetAsync<VxInsumoCatalogo>("api/insumos", ct);

    public Task<VxOpResult> SetInsumoActivoAsync(string id, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "api/insumos/activo",
                     "{\"id\":" + JsonSerializer.Serialize(id ?? "") + "}", ct);

    // =======================================================================
    //  Calibracion "detectar densidad"
    // =======================================================================

    public Task<VxOpResult> CalibrarStartAsync(string insumoId, string modo, double segundos,
                                               CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "api/vistax/calibrar/start",
                     "{\"insumo_id\":" + JsonSerializer.Serialize(insumoId ?? "")
                     + ",\"modo\":" + JsonSerializer.Serialize(modo ?? "objetivo")
                     + ",\"segundos\":" + segundos.ToString(CultureInfo.InvariantCulture) + "}", ct);

    public Task<VxCalibState?> CalibrarStateAsync(CancellationToken ct = default)
        => GetAsync<VxCalibState>("api/vistax/calibrar/state", ct);

    public Task<VxOpResult> CalibrarApplyAsync(double valorOverride, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "api/vistax/calibrar/apply",
                     "{\"aceptar\":true,\"valor_override\":"
                     + valorOverride.ToString(CultureInfo.InvariantCulture) + "}", ct);

    public Task<VxOpResult> CalibrarCancelAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "api/vistax/calibrar/cancel", "{}", ct);

    // =======================================================================
    //  Datos del lote: ZIP de sesiones
    // =======================================================================

    /// <summary>Cliente aparte: bajar el ZIP del lote puede llevar bastante mas
    /// que los 3 s del resto (heatmap + NDJSON de una jornada entera).</summary>
    private static readonly HttpClient _httpLargo = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    /// <summary>En cabina no hay descarga de browser: el ZIP se guarda en
    /// Documentos\AgOpenGPS\Exportes y se devuelve la ruta para el aviso.</summary>
    public async Task<VxZipResult> DescargarZipLoteAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _httpLargo.GetAsync(_baseUrl + "api/lotes/vistax-zip",
                                                       HttpCompletionOption.ResponseHeadersRead, ct)
                                             .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 con {ok:false,error:"no-field"|"no-vistax-data"}
                string cuerpo = "";
                try { cuerpo = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                string motivo = cuerpo.Contains("no-field") ? "no hay lote abierto"
                              : cuerpo.Contains("no-vistax-data") ? "el lote no tiene sesiones de VistaX"
                              : "HTTP " + (int)resp.StatusCode;
                return new VxZipResult { Ok = false, Error = motivo };
            }

            string nombre = "";
            try
            {
                var cd = resp.Content.Headers.ContentDisposition;
                nombre = (cd?.FileNameStar ?? cd?.FileName ?? "").Trim('"');
            }
            catch { }
            if (string.IsNullOrEmpty(nombre))
                nombre = "vistax_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".zip";

            string carpeta = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AgOpenGPS", "Exportes");
            System.IO.Directory.CreateDirectory(carpeta);
            string destino = System.IO.Path.Combine(carpeta, nombre);

            using (var origen = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var archivo = System.IO.File.Create(destino))
                await origen.CopyToAsync(archivo, 81920, ct).ConfigureAwait(false);

            return new VxZipResult { Ok = true, Ruta = destino };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new VxZipResult { Ok = false, Error = "cancelado" };
        }
        catch (Exception ex) { return new VxZipResult { Ok = false, Error = ex.Message }; }
    }

    // =======================================================================
    //  Teclado nativo
    // =======================================================================

    /// <summary>Misma señal HTTP que mandan las paginas del Hub. Catch mudo a
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
