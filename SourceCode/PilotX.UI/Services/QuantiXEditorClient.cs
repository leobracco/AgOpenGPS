// ============================================================================
// QuantiXEditorClient.cs — wire completo del EDITOR nativo de QuantiX.
//
// QUÉ QUEDÓ NATIVO: todo lo que la página pages/quantix.html hacía por fetch:
// config de motores (GET/PUT), send MQTT por nodo, comandos cal/test/config/
// cmd, autotune, estado de PilotX, implemento, tool, shapefile y biblioteca de
// prescripciones. Lo consume QuantiXEditorPanel + sus 6 tabs.
//
// QUÉ SIGUE EN HTML: la página y su JS quedan intactos — los usa la PWA del
// celular. El WS /ws/quantix NO se porta: en el panel nativo alcanza el
// polling de 500 ms (doctrina PORTING-AVALONIA §5) y un ClientWebSocket con
// reconexión/heartbeat/frames binarios agregaría tres modos de falla nuevos
// para ganar 250 ms que en un editor no se notan.
//
// QuantiXClient.cs (el del monitor live) queda como está: este cliente es el
// del editor y no lo reemplaza.
//
// CASING DEL WIRE: el backend serializa con AgpJson → snake_case
// (motors_live, pps_real, last_seen_utc). La ÚNICA excepción es /api/tool, que
// sale camelCase (numSections, width). Los DTOs de acá llevan
// [JsonPropertyName] explícito en cada propiedad porque
// PropertyNameCaseInsensitive NO cubre underscores: un DTO mal casado
// deserializa ceros EN SILENCIO (motores "muertos" sin ningún error).
//
// PUT /api/quantix/motores manda la config ENTERA: el DTO espeja campo por
// campo QxMotorConfigDto del backend e incluye [JsonExtensionData] para que
// un campo futuro del motor no se pierda al guardar desde el panel.
// ============================================================================

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

// ---------------------------------------------------------------------------
//  Config persistida (quantiX_motores.json) — espejo de QxMotorConfigDto
// ---------------------------------------------------------------------------

public sealed class QxMotorConfig
{
    [JsonPropertyName("nombre")]            public string Nombre { get; set; } = "Motor";
    [JsonPropertyName("habilitado")]        public bool   Habilitado { get; set; } = true;
    [JsonPropertyName("dosis_fija")]        public double DosisFija { get; set; }
    [JsonPropertyName("unidad_dosis")]      public string UnidadDosis { get; set; } = "kg_ha";
    [JsonPropertyName("semillas_vuelta")]   public double SemillasVuelta { get; set; }
    [JsonPropertyName("tipo_dosificacion")] public string? TipoDosificacion { get; set; }
    [JsonPropertyName("campo_dosis")]       public string CampoDosis { get; set; } = "";
    [JsonPropertyName("manual_mode")]       public bool   ManualMode { get; set; }
    [JsonPropertyName("manual_dosis")]      public double ManualDosis { get; set; }
    [JsonPropertyName("kp")]                public double Kp { get; set; } = 80;
    [JsonPropertyName("ki")]                public double Ki { get; set; } = 30;
    [JsonPropertyName("kd")]                public double Kd { get; set; }
    [JsonPropertyName("pwm_min")]           public int    PwmMin { get; set; } = 600;
    [JsonPropertyName("pwm_max")]           public int    PwmMax { get; set; } = 4095;
    [JsonPropertyName("meter_cal")]         public double MeterCal { get; set; } = 50;
    [JsonPropertyName("max_integral")]      public double MaxIntegral { get; set; } = 1200;
    [JsonPropertyName("deadband")]          public int    Deadband { get; set; } = 2;
    [JsonPropertyName("slew_rate")]         public int    SlewRate { get; set; } = 40;
    [JsonPropertyName("dientes_engranaje")] public int    DientesEngranaje { get; set; } = 20;
    [JsonPropertyName("sensor_tipo")]       public string? SensorTipo { get; set; }
    [JsonPropertyName("pulse_min")]         public int    PulseMin { get; set; } = 2000;
    [JsonPropertyName("motor_type")]        public int    MotorType { get; set; }
    [JsonPropertyName("max_hz")]            public double MaxHz { get; set; } = 40;
    [JsonPropertyName("ff_gain")]           public double FFGain { get; set; } = 1.0;
    [JsonPropertyName("alpha")]             public double Alpha { get; set; } = 0.4;
    [JsonPropertyName("slew_rate_per_sec")] public double SlewRatePerSec { get; set; } = 5000;
    [JsonPropertyName("target_slew_hz_per_sec")] public double TargetSlewHzPerSec { get; set; } = 300;
    [JsonPropertyName("pid_time")]          public int    PIDTime { get; set; } = 50;
    [JsonPropertyName("cortes")]            public List<int> Cortes { get; set; } = new();
    [JsonPropertyName("tren")]              public int    Tren { get; set; }

    /// <summary>Todo lo que el backend agregue y este DTO no modele todavía.
    /// Sin esto, guardar desde el panel PISARÍA esos campos con nada.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Motor de RELLENO que inventó la UI para tener algo que dibujar
    /// (nodo sin motores configurados). No es un motor de la máquina: nace
    /// deshabilitado y NO se persiste mientras el operario no lo toque — ver
    /// QxEditorCtx.MotoresDelNodo / EsPlaceholderSinTocar. [JsonIgnore] a
    /// propósito: la marca es de la pantalla, no del wire ni del archivo.</summary>
    [JsonIgnore] public bool EsPlaceholder { get; set; }

    /// <summary>Copia de los campos "del fierro y del lazo" (⧉ Copiar a todos).
    /// Quedan afuera nombre, cortes y habilitado: copiarlos pisaría el reparto
    /// de la sembradora y encendería canales sin motor.</summary>
    public void CopiarFierroDesde(QxMotorConfig o)
    {
        SensorTipo = o.SensorTipo;
        DientesEngranaje = o.DientesEngranaje;
        PulseMin = o.PulseMin;
        MotorType = o.MotorType;
        PwmMin = o.PwmMin;
        PwmMax = o.PwmMax;
        Kp = o.Kp; Ki = o.Ki; Kd = o.Kd;
        MaxHz = o.MaxHz;
        FFGain = o.FFGain;
        Alpha = o.Alpha;
        PIDTime = o.PIDTime;
        SlewRatePerSec = o.SlewRatePerSec;
        TargetSlewHzPerSec = o.TargetSlewHzPerSec;
        MeterCal = o.MeterCal;
        SemillasVuelta = o.SemillasVuelta;
        UnidadDosis = o.UnidadDosis;
        DosisFija = o.DosisFija;
        CampoDosis = o.CampoDosis;
        TipoDosificacion = o.TipoDosificacion;
    }
}

public sealed class QxNodoConfig
{
    [JsonPropertyName("uid")]        public string Uid { get; set; } = "";
    [JsonPropertyName("nombre")]     public string Nombre { get; set; } = "Nodo QuantiX";
    [JsonPropertyName("habilitado")] public bool   Habilitado { get; set; } = true;
    [JsonPropertyName("distancia_entre_trenes")] public double DistanciaEntreTrenes { get; set; }
    [JsonPropertyName("motores")]    public List<QxMotorConfig> Motores { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class QxTrenConfig
{
    [JsonPropertyName("id")]          public int    Id { get; set; }
    [JsonPropertyName("nombre")]      public string Nombre { get; set; } = "";
    [JsonPropertyName("distancia_m")] public double DistanciaM { get; set; }
}

public sealed class QxMotoresConfig
{
    [JsonPropertyName("trenes")]    public List<QxTrenConfig> Trenes { get; set; } = new();
    [JsonPropertyName("nodos")]     public List<QxNodoConfig> Nodos { get; set; } = new();
    [JsonPropertyName("ignorados")] public List<string> Ignorados { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class QxMotoresConfigResp
{
    [JsonPropertyName("ok")]     public bool Ok { get; set; }
    [JsonPropertyName("config")] public QxMotoresConfig? Config { get; set; }
}

// ---------------------------------------------------------------------------
//  Telemetría live (snake_case — AgpJson)
// ---------------------------------------------------------------------------

public sealed class QxMotorLive
{
    [JsonPropertyName("id")]            public int    Id { get; set; }
    [JsonPropertyName("pps_target")]    public double PpsTarget { get; set; }
    [JsonPropertyName("pps_real")]      public double PpsReal { get; set; }
    [JsonPropertyName("pwm")]           public int    Pwm { get; set; }
    [JsonPropertyName("rpm")]           public int    Rpm { get; set; }
    [JsonPropertyName("pulsos")]        public long   Pulsos { get; set; }
    [JsonPropertyName("last_seen_utc")] public string? LastSeenUtc { get; set; }
}

public sealed class QxNodoLive
{
    [JsonPropertyName("uid")]         public string? Uid { get; set; }
    [JsonPropertyName("ip")]          public string? Ip { get; set; }
    [JsonPropertyName("firmware")]    public string? Firmware { get; set; }
    [JsonPropertyName("online")]      public bool    Online { get; set; }
    [JsonPropertyName("motors_live")] public List<QxMotorLive>? MotorsLive { get; set; }
}

public sealed class QxLiveSnapshot
{
    [JsonPropertyName("ok")]    public bool Ok { get; set; }
    [JsonPropertyName("count")] public int  Count { get; set; }
    [JsonPropertyName("nodos")] public List<QxNodoLive>? Nodos { get; set; }
}

// ---------------------------------------------------------------------------
//  Estado de PilotX / implemento / tool
// ---------------------------------------------------------------------------

public sealed class QxAogState
{
    [JsonPropertyName("is_job_started")]           public bool   IsJobStarted { get; set; }
    [JsonPropertyName("section_on_request")]       public List<bool>? SectionOnRequest { get; set; }
    [JsonPropertyName("num_sections")]             public int    NumSections { get; set; }
    [JsonPropertyName("avg_speed")]                public double AvgSpeed { get; set; }
    [JsonPropertyName("actual_area_covered_m2")]   public double AreaM2 { get; set; }
}

public sealed class QxSurcoImpl
{
    [JsonPropertyName("numero")]         public int Numero { get; set; }
    [JsonPropertyName("seccion_pilotx")] public int SeccionPilotx { get; set; }
    [JsonPropertyName("tren_id")]        public int TrenId { get; set; }
}

public sealed class QxImplemento
{
    [JsonPropertyName("ancho_total_m")]              public double AnchoTotalM { get; set; }
    [JsonPropertyName("numero_surcos")]              public int    NumeroSurcos { get; set; }
    [JsonPropertyName("distancia_entre_surcos_m")]   public double DistanciaEntreSurcosM { get; set; }
    [JsonPropertyName("trenes")]                     public List<QxTrenConfig>? Trenes { get; set; }
    [JsonPropertyName("surcos")]                     public List<QxSurcoImpl>? Surcos { get; set; }
}

internal sealed class QxImplementoResp
{
    [JsonPropertyName("ok")]         public bool Ok { get; set; }
    [JsonPropertyName("implemento")] public QxImplemento? Implemento { get; set; }
}

/// <summary>OJO: /api/tool sale en camelCase, no snake — es la excepción.</summary>
internal sealed class QxToolDto
{
    [JsonPropertyName("width")]       public double Width { get; set; }
    [JsonPropertyName("numSections")] public int    NumSections { get; set; }
}
internal sealed class QxToolResp
{
    [JsonPropertyName("tool")] public QxToolDto? Tool { get; set; }
}

// ---------------------------------------------------------------------------
//  Shape / prescripciones
// ---------------------------------------------------------------------------

public sealed class QxShapeField
{
    public string Name { get; set; } = "";
    public bool   Numeric { get; set; } = true;
}

public sealed class QxShapeFieldsInfo
{
    public bool Ok { get; set; }
    public string SourceToken { get; set; } = "";
    public List<QxShapeField> Fields { get; set; } = new();
}

/// <summary>Una zona del shapefile activo, en metros locales (norte = +Y).
/// Rings: cada anillo es el plano [x0,y0,x1,y1,…].</summary>
public sealed class QxShapeZona
{
    public List<double[]> Rings { get; set; } = new();
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public int A { get; set; } = 255;
    /// <summary>Valor de dosis de la zona. null = zona sin valor.</summary>
    public double? V { get; set; }
    /// <summary>Índice de la FEATURE DEL ARCHIVO. Es el que viaja al server al
    /// editar la dosis — NUNCA el de dibujo: los MultiPolygon se parten en
    /// piezas y corren la numeración.</summary>
    public int Fi { get; set; } = -1;
}

public sealed class QxShapeLayer
{
    public List<QxShapeZona> Polygons { get; set; } = new();
    public string StyleField { get; set; } = "";
    public double? StyleMin { get; set; }
    public double? StyleMax { get; set; }
    public string SourceToken { get; set; } = "";
}

public sealed class QxPrescripcion
{
    [JsonPropertyName("id")]            public string Id { get; set; } = "";
    [JsonPropertyName("nombre")]        public string? Nombre { get; set; }
    [JsonPropertyName("fecha_mod_utc")] public string? FechaModUtc { get; set; }
    [JsonPropertyName("activo")]        public bool   Activo { get; set; }
    [JsonPropertyName("propiedades_candidatas")] public List<string>? PropiedadesCandidatas { get; set; }
}

internal sealed class QxPrescripcionesResp
{
    [JsonPropertyName("ok")]    public bool Ok { get; set; }
    [JsonPropertyName("items")] public List<QxPrescripcion>? Items { get; set; }
}

public sealed class QxPrescActiva
{
    [JsonPropertyName("id")]              public string Id { get; set; } = "";
    [JsonPropertyName("propiedad_dosis")] public string? PropiedadDosis { get; set; }
}
internal sealed class QxPrescActivaResp
{
    [JsonPropertyName("ok")]     public bool Ok { get; set; }
    [JsonPropertyName("activa")] public QxPrescActiva? Activa { get; set; }
}

public sealed class QxAutoTuneResult
{
    [JsonPropertyName("motor_id")]     public int    MotorId { get; set; }
    [JsonPropertyName("ok")]           public bool   Ok { get; set; }
    [JsonPropertyName("kp")]           public double Kp { get; set; }
    [JsonPropertyName("ki")]           public double Ki { get; set; }
    [JsonPropertyName("kd")]           public double Kd { get; set; }
    /// <summary>Motivo del fallo tal cual lo manda el nodo (motor que no gira
    /// vs motor que no oscila: se arreglan distinto).</summary>
    [JsonPropertyName("msg")]          public string? Msg { get; set; }
    [JsonPropertyName("received_utc")] public string? ReceivedUtc { get; set; }
}
internal sealed class QxAutoTuneResp
{
    [JsonPropertyName("ok")]         public bool Ok { get; set; }
    [JsonPropertyName("has_result")] public bool HasResult { get; set; }
    [JsonPropertyName("result")]     public QxAutoTuneResult? Result { get; set; }
}

/// <summary>Respuesta genérica {ok, error, mensaje, detalle, polygon_count}.
/// El PUT de motores devuelve AGP-CFG-001 con mensaje+detalle cuando la
/// config no valida.</summary>
public sealed class QxOpResult
{
    [JsonPropertyName("ok")]            public bool Ok { get; set; }
    [JsonPropertyName("error")]         public string? Error { get; set; }
    [JsonPropertyName("mensaje")]       public string? Mensaje { get; set; }
    [JsonPropertyName("detalle")]       public string? Detalle { get; set; }
    [JsonPropertyName("polygon_count")] public int PolygonCount { get; set; }

    /// <summary>Texto listo para el operario cuando algo falla.</summary>
    public string TextoError()
    {
        if (string.Equals(Error, "AGP-CFG-001", StringComparison.Ordinal))
            return "✕ AGP-CFG-001 · " + (Mensaje ?? "config inválida")
                 + (string.IsNullOrEmpty(Detalle) ? "" : " — " + Detalle);
        return "✕ " + (string.IsNullOrEmpty(Error) ? "error" : Error);
    }
}

// ---------------------------------------------------------------------------
//  Cliente
// ---------------------------------------------------------------------------

public sealed class QuantiXEditorClient
{
    // Timeout POR OPERACIÓN (CTS), no global: el POST del shapefile va en
    // base64 y puede pesar megas, y el poll de autotune espera hasta 50 s.
    // Un Timeout global de 3 s los mataba a los dos.
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _base;

    public QuantiXEditorClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    public string BaseUrl => _base;

    // ---- plumbing --------------------------------------------------------

    private static CancellationTokenSource Cts(CancellationToken ct, int segundos)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(segundos));
        return cts;
    }

    private async Task<string?> GetTextAsync(string ruta, int segundos, CancellationToken ct)
    {
        try
        {
            using var cts = Cts(ct, segundos);
            using var resp = await _http.GetAsync(_base + ruta, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch { return null; }
    }

    private async Task<T?> GetAsync<T>(string ruta, int segundos, CancellationToken ct) where T : class
    {
        var json = await GetTextAsync(ruta, segundos, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, _opts); }
        catch { return null; }
    }

    private async Task<QxOpResult> SendAsync(HttpMethod metodo, string ruta, string? body,
                                             int segundos, CancellationToken ct)
    {
        try
        {
            using var cts = Cts(ct, segundos);
            using var req = new HttpRequestMessage(metodo, _base + ruta);
            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return new QxOpResult { Ok = resp.IsSuccessStatusCode, Error = resp.IsSuccessStatusCode ? null : "HTTP " + (int)resp.StatusCode };
            var r = JsonSerializer.Deserialize<QxOpResult>(json, _opts);
            return r ?? new QxOpResult { Ok = false, Error = "respuesta vacía" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new QxOpResult { Ok = false, Error = "cancelado" };
        }
        catch (Exception ex)
        {
            return new QxOpResult { Ok = false, Error = ex.Message };
        }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s ?? "");

    // ---- QuantiX ---------------------------------------------------------

    public Task<QxLiveSnapshot?> GetLiveAsync(CancellationToken ct = default)
        => GetAsync<QxLiveSnapshot>("api/quantix/live", 4, ct);

    /// <summary>Live FRESCO de UN motor, pedido en el momento. Para MEDIR
    /// (Max Hz, rampas) hay que usar este y no la caché del loop: muestrear la
    /// caché a 200 ms devolvía el mismo valor repetido y daba 133 Hz de un
    /// motor que hace 695 (banco 2026-08-01).</summary>
    public async Task<QxMotorLive?> FetchLiveMotorAsync(string uid, int mi, CancellationToken ct = default)
    {
        var snap = await GetLiveAsync(ct).ConfigureAwait(false);
        var nodos = snap?.Nodos;
        if (nodos == null) return null;
        foreach (var n in nodos)
        {
            if (!string.Equals(n.Uid, uid, StringComparison.Ordinal)) continue;
            var ms = n.MotorsLive;
            if (ms == null) continue;
            foreach (var m in ms) if (m.Id == mi) return m;
        }
        return null;
    }

    public async Task<QxMotoresConfig?> GetMotoresAsync(CancellationToken ct = default)
    {
        var r = await GetAsync<QxMotoresConfigResp>("api/quantix/motores", 6, ct).ConfigureAwait(false);
        return (r != null && r.Ok) ? r.Config : null;
    }

    public Task<QxOpResult> PutMotoresAsync(QxMotoresConfig cfg, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, "api/quantix/motores",
                     JsonSerializer.Serialize(cfg, _opts), 10, ct);

    public Task<QxOpResult> SendNodoAsync(string uid, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "api/quantix/" + Esc(uid) + "/send", "", 10, ct);

    /// <summary>POST /api/quantix/{uid}/cmd?verb=…&amp;retain=… — el payload
    /// viaja crudo al nodo.</summary>
    public Task<QxOpResult> CmdAsync(string uid, string verb, string payload,
                                     bool retain = false, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post,
                     "api/quantix/" + Esc(uid) + "/cmd?verb=" + Esc(verb) + "&retain=" + (retain ? "true" : "false"),
                     payload, 8, ct);

    /// <summary>verb=test: PWM directo, SIN meta. OJO: no para solo —
    /// todo camino de salida tiene que terminar en TestStopAsync.</summary>
    public Task<QxOpResult> TestAsync(string uid, int mi, int pwm, CancellationToken ct = default)
        => CmdAsync(uid, "test",
                    pwm > 0
                        ? "{\"cmd\":\"start\",\"id\":" + mi + ",\"pwm\":" + pwm + "}"
                        : "{\"cmd\":\"stop\",\"id\":" + mi + ",\"pwm\":0}",
                    false, ct);

    public Task<QxOpResult> TestStopAsync(string uid, int mi, CancellationToken ct = default)
        => TestAsync(uid, mi, 0, ct);

    /// <summary>verb=cal: gira a PWM fijo hasta acumular `pulsos` y para solo.
    /// CONTRATO FIRMWARE: en 'start' el nodo RESETEA su contador a 0.</summary>
    public Task<QxOpResult> CalStartAsync(string uid, int mi, int pulsos, int pwm, CancellationToken ct = default)
        => CmdAsync(uid, "cal",
                    "{\"cmd\":\"start\",\"id\":" + mi + ",\"pulsos\":" + pulsos + ",\"pwm\":" + pwm + "}",
                    false, ct);

    public Task<QxOpResult> CalStopAsync(string uid, int mi, CancellationToken ct = default)
        => CmdAsync(uid, "cal", "{\"cmd\":\"stop\",\"id\":" + mi + "}", false, ct);

    /// <summary>Config PARCIAL de PID (el firmware la mergea).</summary>
    public Task<QxOpResult> PushPidAsync(string uid, int mi, double kp, double ki, double kd,
                                         CancellationToken ct = default)
    {
        string j = "{\"configs\":[{\"idx\":" + mi + ",\"config_pid\":{"
                 + "\"kp\":" + kp.ToString(CultureInfo.InvariantCulture)
                 + ",\"ki\":" + ki.ToString(CultureInfo.InvariantCulture)
                 + ",\"kd\":" + kd.ToString(CultureInfo.InvariantCulture) + "}}]}";
        return CmdAsync(uid, "config", j, true, ct);
    }

    public Task<QxOpResult> AutoTuneStartAsync(string uid, int mi, CancellationToken ct = default)
        => CmdAsync(uid, "cmd", "{\"cmd\":\"autotune_start\",\"id\":" + mi + "}", false, ct);

    /// <summary>Corta un autotune en curso y frena el motor. Se manda al
    /// abandonar la espera: el nodo sigue tuneando —y girando— por su cuenta
    /// aunque la pantalla haya dejado de escuchar.</summary>
    public Task<QxOpResult> AutoTuneStopAsync(string uid, int mi, CancellationToken ct = default)
        => CmdAsync(uid, "cmd", "{\"cmd\":\"autotune_stop\",\"id\":" + mi + "}", false, ct);

    public async Task<QxAutoTuneResult?> GetAutoTuneAsync(string uid, CancellationToken ct = default)
    {
        var r = await GetAsync<QxAutoTuneResp>("api/quantix/" + Esc(uid) + "/autotune", 5, ct).ConfigureAwait(false);
        return (r != null && r.Ok && r.HasResult) ? r.Result : null;
    }

    public Task<QxOpResult> BorrarNodoAsync(string uid, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, "api/nodos/" + Esc(uid), null, 8, ct);

    // ---- PilotX (estado, implemento, tool) --------------------------------

    public Task<QxAogState?> GetAogStateAsync(CancellationToken ct = default)
        => GetAsync<QxAogState>("api/aog/state", 4, ct);

    /// <summary>Implemento central con el ancho/secciones PISADOS por
    /// /api/tool: PilotX es la fuente de verdad de la geometría. Devuelve
    /// también el ancho que reportó PilotX (0 = sin ancho configurado).</summary>
    public async Task<(QxImplemento? Impl, double AnchoPilotX)> GetImplementoAsync(CancellationToken ct = default)
    {
        var r = await GetAsync<QxImplementoResp>("api/implemento", 6, ct).ConfigureAwait(false);
        var impl = (r != null && r.Ok) ? r.Implemento : null;
        if (impl == null) return (null, 0);

        double ancho = 0;
        var tool = await GetAsync<QxToolResp>("api/tool", 6, ct).ConfigureAwait(false);
        if (tool?.Tool != null)
        {
            ancho = tool.Tool.Width;
            int nSec = tool.Tool.NumSections;
            if (ancho > 0)
            {
                impl.AnchoTotalM = ancho;
                if (nSec > 0)
                {
                    impl.NumeroSurcos = nSec;
                    impl.DistanciaEntreSurcosM = ancho / nSec;
                }
            }
        }
        return (impl, ancho);
    }

    // ---- Shapefile --------------------------------------------------------

    public async Task<QxShapeFieldsInfo> GetShapeFieldsAsync(CancellationToken ct = default)
    {
        var info = new QxShapeFieldsInfo();
        var json = await GetTextAsync("api/aog/shape-fields", 6, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return info;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return info;
            info.Ok = true;
            if (root.TryGetProperty("source_token", out var st) && st.ValueKind == JsonValueKind.String)
                info.SourceToken = st.GetString() ?? "";
            if (root.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fs.EnumerateArray())
                {
                    // Los elementos pueden ser strings sueltos o el objeto
                    // {name,numeric,min,max,count}: se toleran los dos.
                    if (f.ValueKind == JsonValueKind.String)
                    {
                        info.Fields.Add(new QxShapeField { Name = f.GetString() ?? "", Numeric = true });
                    }
                    else if (f.ValueKind == JsonValueKind.Object)
                    {
                        string nm = f.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        bool num = !f.TryGetProperty("numeric", out var nu) || nu.ValueKind != JsonValueKind.False;
                        if (!string.IsNullOrEmpty(nm)) info.Fields.Add(new QxShapeField { Name = nm, Numeric = num });
                    }
                }
            }
        }
        catch { }
        return info;
    }

    public async Task<QxShapeLayer?> GetShapeAsync(CancellationToken ct = default)
    {
        var json = await GetTextAsync("api/aog/shape", 8, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var layer = new QxShapeLayer();
            if (root.TryGetProperty("style_field", out var sf) && sf.ValueKind == JsonValueKind.String)
                layer.StyleField = sf.GetString() ?? "";
            if (root.TryGetProperty("source_token", out var stk) && stk.ValueKind == JsonValueKind.String)
                layer.SourceToken = stk.GetString() ?? "";
            if (root.TryGetProperty("style_min", out var mn) && mn.ValueKind == JsonValueKind.Number)
                layer.StyleMin = mn.GetDouble();
            if (root.TryGetProperty("style_max", out var mx) && mx.ValueKind == JsonValueKind.Number)
                layer.StyleMax = mx.GetDouble();

            if (root.TryGetProperty("polygons", out var pol) && pol.ValueKind == JsonValueKind.Array)
            {
                foreach (var z in pol.EnumerateArray())
                {
                    var zona = new QxShapeZona();
                    if (z.TryGetProperty("r", out var r)) zona.R = r.GetInt32();
                    if (z.TryGetProperty("g", out var g)) zona.G = g.GetInt32();
                    if (z.TryGetProperty("b", out var b)) zona.B = b.GetInt32();
                    if (z.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.Number) zona.A = a.GetInt32();
                    if (z.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number) zona.V = v.GetDouble();
                    if (z.TryGetProperty("fi", out var fi) && fi.ValueKind == JsonValueKind.Number) zona.Fi = fi.GetInt32();

                    if (z.TryGetProperty("rings", out var rings) && rings.ValueKind == JsonValueKind.Array)
                    {
                        // rings puede ser [[x,y,…],[…]] (anidado) o [x,y,…] (plano).
                        bool anidado = false;
                        foreach (var el in rings.EnumerateArray()) { anidado = el.ValueKind == JsonValueKind.Array; break; }
                        if (anidado)
                        {
                            foreach (var anillo in rings.EnumerateArray())
                                zona.Rings.Add(LeerPlano(anillo));
                        }
                        else
                        {
                            zona.Rings.Add(LeerPlano(rings));
                        }
                    }
                    layer.Polygons.Add(zona);
                }
            }
            return layer;
        }
        catch { return null; }
    }

    private static double[] LeerPlano(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<double>();
        var lista = new List<double>(arr.GetArrayLength());
        foreach (var n in arr.EnumerateArray())
            if (n.ValueKind == JsonValueKind.Number) lista.Add(n.GetDouble());
        return lista.ToArray();
    }

    /// <summary>Sube .shp/.shx/.dbf (+ .prj/.cpg) en base64. Timeout largo: el
    /// payload puede pesar megas.</summary>
    public Task<QxOpResult> SubirShapeAsync(List<(string Nombre, byte[] Bytes)> archivos, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append("{\"files\":[");
        for (int i = 0; i < archivos.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(JsonSerializer.Serialize(archivos[i].Nombre))
              .Append(",\"b64\":\"").Append(Convert.ToBase64String(archivos[i].Bytes)).Append("\"}");
        }
        sb.Append("]}");
        return SendAsync(HttpMethod.Post, "api/aog/shape", sb.ToString(), 180, ct);
    }

    public Task<QxOpResult> QuitarShapeAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, "api/aog/shape", null, 15, ct);

    // ---- Prescripciones ---------------------------------------------------

    public async Task<List<QxPrescripcion>> ListarPrescripcionesAsync(CancellationToken ct = default)
    {
        var r = await GetAsync<QxPrescripcionesResp>("api/prescripciones/list", 8, ct).ConfigureAwait(false);
        return (r != null && r.Ok && r.Items != null) ? r.Items : new List<QxPrescripcion>();
    }

    public async Task<QxPrescActiva?> GetPrescActivaAsync(CancellationToken ct = default)
    {
        var r = await GetAsync<QxPrescActivaResp>("api/prescripciones/activa", 6, ct).ConfigureAwait(false);
        return (r != null && r.Ok) ? r.Activa : null;
    }

    public Task<QxOpResult> ActivarPrescripcionAsync(string id, string propiedad, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "api/prescripciones/activa",
                     "{\"id\":" + JsonSerializer.Serialize(id) + ",\"propiedad_dosis\":"
                     + JsonSerializer.Serialize(propiedad ?? "") + "}", 15, ct);

    public Task<QxOpResult> QuitarPrescripcionActivaAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "api/prescripciones/activa/clear", "", 15, ct);

    public Task<QxOpResult> SetDosisZonaAsync(string id, int zonaFi, double dosis, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "api/prescripciones/dosis-zona",
                     "{\"id\":" + JsonSerializer.Serialize(id) + ",\"zona\":" + zonaFi
                     + ",\"dosis\":" + dosis.ToString(CultureInfo.InvariantCulture) + "}", 15, ct);

    // ---- Teclado nativo ---------------------------------------------------

    /// <summary>Misma señal HTTP que mandan las páginas del Hub. Catch mudo a
    /// propósito: sin teclado nativo el campo sigue editable.</summary>
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
            using var _ = await _http.PostAsync(_base + "api/teclado/" + (abrir ? "abrir" : "cerrar"),
                                                contenido, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }
}
