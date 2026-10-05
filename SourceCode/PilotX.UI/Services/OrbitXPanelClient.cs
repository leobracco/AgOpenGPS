// ============================================================================
// OrbitXPanelClient.cs — cliente HTTP de la pantalla OrbitX Cloud.
//
// Lo consume OrbitXPanel (port nativo de pages/orbitx.html + js/orbitx.js).
// Wire IDÉNTICO al de la página — cero cambio de contrato, porque el HTML
// sigue vivo para el Hub remoto y la PWA del celular:
//
//   GET  /api/orbitx/config      → OrbitXConfigDto   (snake_case, [JsonPropertyName])
//   POST /api/orbitx/config      → { ok }            (body = el MISMO DTO entero)
//   GET  /api/orbitx/status      → OrbitXStatus      (snake_case via AgpJson)
//   POST /api/orbitx/test        → { ok, error? }
//   GET  /api/orbitx/pair-info   → OrbitXPairInfo    (snake_case via AgpJson)
//   POST /api/orbitx/pair-reset  → { ok }
//
// La AUTENTICACIÓN contra el cloud (X-Device-ID + X-Auth-Token) NO se toca
// desde acá: vive entera en OrbitXConfigService / OrbitXSync, del lado del
// Engine. Este cliente habla SOLO con el host local (127.0.0.1:5180), igual
// que el fetch del navegador.
//
// server_url es de solo lectura por diseño: OrbitXConfigService la fuerza a
// FixedServerUrl en Load() y en Save(). Se manda igual en el POST (el JS hace
// Object.assign del DTO completo) y el service la vuelve a pisar — lo que la
// UI muestra es informativo.
//
// Trampas del wire cubiertas acá:
//   · snake_case: server_url / device_token / sync_interval_sec / files_synced /
//     cloud_connected / expires_in_sec / just_claimed / error_code /
//     hint_technical NO matchean con PropertyNameCaseInsensitive (el
//     case-insensitive de System.Text.Json NO cubre underscores) → va
//     [JsonPropertyName] en CADA propiedad.
//   · BOM de EmbedIO: las respuestas pueden arrancar con U+FEFF y sin
//     TrimStart el Deserialize tira.
//   · HttpClient.Timeout NO se usa como cancelación: corta con
//     TaskCanceledException (que ES OperationCanceledException) y sin el
//     filtro `when (ct.IsCancellationRequested)` la UI queda congelada en sus
//     defaults. Acá el corte va por CancellationTokenSource enlazado.
// ============================================================================

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Espejo de AgroParallel.Models.OrbitXConfigDto (orbitX.json).
/// Se lee ENTERO y se re-manda ENTERO: el JS hace
/// `Object.assign({}, formEl._dto)` y solo pisa los campos del formulario, así
/// que los que la UI no muestra (master_token, firmware_*, camaras_*) viajan
/// de vuelta tal cual. El controller igual hace MERGE sobre el disco.</summary>
public sealed class OrbitXConfigWire
{
    [JsonPropertyName("enabled")]                    public bool    Enabled                { get; set; }
    [JsonPropertyName("server_url")]                 public string? ServerUrl              { get; set; }
    [JsonPropertyName("device_token")]               public string? DeviceToken            { get; set; }
    [JsonPropertyName("master_token")]               public string? MasterToken            { get; set; }
    [JsonPropertyName("device_id")]                  public string? DeviceId               { get; set; }
    [JsonPropertyName("estab_slug")]                 public string? EstabSlug              { get; set; }

    [JsonPropertyName("sync_interval_sec")]          public int     SyncIntervalSec        { get; set; }
    [JsonPropertyName("sync_aog")]                   public bool    SyncAOG                { get; set; }
    [JsonPropertyName("sync_vistax")]                public bool    SyncVistaX             { get; set; }
    [JsonPropertyName("sync_quantix")]               public bool    SyncQuantiX            { get; set; }
    [JsonPropertyName("sync_sectionx")]              public bool    SyncSectionX           { get; set; }

    [JsonPropertyName("firmware_mirror_enabled")]    public bool    FirmwareMirrorEnabled  { get; set; }
    [JsonPropertyName("firmware_cache_dir")]         public string? FirmwareCacheDir       { get; set; }
    [JsonPropertyName("firmware_http_port")]         public int     FirmwareHttpPort       { get; set; }
    [JsonPropertyName("firmware_sync_interval_min")] public int     FirmwareSyncIntervalMin{ get; set; }

    [JsonPropertyName("camaras_streaming_enabled")]  public bool    CamarasStreamingEnabled{ get; set; }
    [JsonPropertyName("camaras_rtsp_host")]          public string? CamarasRtspHost        { get; set; }
    [JsonPropertyName("camaras_rtsp_port")]          public int     CamarasRtspPort        { get; set; }
    [JsonPropertyName("camaras_ffmpeg_path")]        public string? CamarasFfmpegPath      { get; set; }

    [JsonPropertyName("last_sync")]                  public string? LastSync               { get; set; }
    [JsonPropertyName("files_synced")]               public int     FilesSynced            { get; set; }
}

/// <summary>Espejo de AgroParallel.Models.OrbitXStatus.</summary>
public sealed class OrbitXStatusWire
{
    [JsonPropertyName("enabled")]         public bool    Enabled        { get; set; }
    [JsonPropertyName("cloud_connected")] public bool    CloudConnected { get; set; }
    [JsonPropertyName("last_error")]      public string? LastError      { get; set; }
    [JsonPropertyName("last_sync")]       public string? LastSync       { get; set; }
    [JsonPropertyName("files_synced")]    public int     FilesSynced    { get; set; }
    [JsonPropertyName("estab_slug")]      public string? EstabSlug      { get; set; }
    [JsonPropertyName("device_id")]       public string? DeviceId       { get; set; }
}

/// <summary>Espejo de AgroParallel.Models.OrbitXPairInfo (flow de vinculación
/// táctil, RFC 8628-style).</summary>
public sealed class OrbitXPairInfoWire
{
    [JsonPropertyName("paired")]         public bool    Paired       { get; set; }
    [JsonPropertyName("just_claimed")]   public bool    JustClaimed  { get; set; }
    [JsonPropertyName("code")]           public string? Code         { get; set; }
    [JsonPropertyName("expires_in_sec")] public int     ExpiresInSec { get; set; }
    [JsonPropertyName("device_id")]      public string? DeviceId     { get; set; }
    [JsonPropertyName("estab_slug")]     public string? EstabSlug    { get; set; }
    [JsonPropertyName("server_url")]     public string? ServerUrl    { get; set; }
    /// <summary>"pending" | "claimed" | "expired" | "offline" | "ok".</summary>
    [JsonPropertyName("status")]         public string? Status       { get; set; }
    [JsonPropertyName("hint")]           public string? Hint         { get; set; }
    /// <summary>Código AGP-* dictable a soporte. Vacío = no hubo falla.</summary>
    [JsonPropertyName("error_code")]     public string? ErrorCode    { get; set; }
    [JsonPropertyName("hint_technical")] public string? HintTechnical{ get; set; }

    /// <summary>`error` del sobre `{ ok:false, error:"service-unavailable" }`
    /// que devuelve el controller cuando el service no está montado. La página
    /// lo mira con `info.error` y pinta la card en rojo.</summary>
    [JsonPropertyName("error")]          public string? Error        { get; set; }

    /// <summary>OJO — replica un detalle REAL del JS, no es un typo de este
    /// port: la página evalúa `if (!info || (info.error &amp;&amp; !info.deviceId))`
    /// con `deviceId` en camelCase, pero el wire manda `device_id`. O sea que
    /// esa propiedad NUNCA existe y la guarda se reduce a "si vino error →
    /// card offline". Se conserva la propiedad para que la condición del panel
    /// sea literalmente la misma; siempre queda null.</summary>
    [JsonPropertyName("deviceId")]       public string? DeviceIdCamel{ get; set; }
}

/// <summary>Resultado del `Promise.all([config, status])` de load(): si
/// CUALQUIERA de las dos falla (o el body no es JSON), el JS cae al catch y
/// solo escribe el hint — NO pinta ni el form ni el estado.</summary>
public sealed class OrbitXCargaResultado
{
    public OrbitXConfigWire? Config { get; set; }
    public OrbitXStatusWire? Status { get; set; }
    /// <summary>No-null = catch del fetch (`e.message`). Null = todo ok.</summary>
    public string? Error { get; set; }
    /// <summary>El panel se estaba cerrando: no hay nada que pintar ni que
    /// avisar (distinto de un error de red real).</summary>
    public bool Cancelado { get; set; }
}

/// <summary>Respuesta de POST /api/orbitx/config y de /api/orbitx/pair-reset.</summary>
public sealed class OrbitXOkResultado
{
    public bool Ok { get; set; }
    /// <summary>No-null = excepción (el JS muestra "Error: " + e.message).</summary>
    public string? Excepcion { get; set; }
    public bool Cancelado { get; set; }
}

/// <summary>Respuesta de POST /api/orbitx/test: `{ ok, error? }`.</summary>
public sealed class OrbitXTestResultado
{
    public bool Ok { get; set; }
    /// <summary>`data.error` del body — el JS lo muestra como "✗ " + error.</summary>
    public string? Error { get; set; }
    /// <summary>No-null = excepción del fetch ("Error: " + e.message).</summary>
    public string? Excepcion { get; set; }
    public bool Cancelado { get; set; }
}

/// <summary>Resultado de GET /api/orbitx/pair-info.</summary>
public sealed class OrbitXPairResultado
{
    public OrbitXPairInfoWire? Info { get; set; }
    /// <summary>No-null = catch del fetch: la página pinta
    /// "Sin conexión local al servicio: " + e.message.</summary>
    public string? Excepcion { get; set; }
    public bool Cancelado { get; set; }
}

public sealed class OrbitXPanelClient
{
    // Timeout INFINITO a propósito: el corte real lo pone CancellationToken
    // (ver cabecera). Un Timeout de HttpClient se disfraza de "el operario
    // cerró el panel" y deja la pantalla congelada.
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Corte de cada request contra el host LOCAL. El pair-info puede
    /// tardar: adentro del Engine hace de puente al cloud (POST /pair/init +
    /// GET /pair/status), con su propio HttpClient de 8 s. 12 s deja margen
    /// para los dos saltos sin trabar el poll de 4 s.</summary>
    private static readonly TimeSpan CorteCorto = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CortePair  = TimeSpan.FromSeconds(12);

    private readonly string _baseUrl;

    public OrbitXPanelClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Base del host local (la usa el panel para el teclado nativo).</summary>
    public string BaseUrl => _baseUrl;

    // ====================================================================
    //  load() — config + status en paralelo (Promise.all)
    // ====================================================================

    /// <summary>GET /api/orbitx/config + GET /api/orbitx/status, los dos con
    /// `cache: no-store`. Si alguno tira, el resultado trae Error y el panel
    /// NO repinta nada (igual que el catch del JS).</summary>
    public async Task<OrbitXCargaResultado> CargarAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteCorto);

            // Promise.all: las dos salen juntas.
            var tCfg = LeerAsync<OrbitXConfigWire>("api/orbitx/config", corte.Token);
            var tSta = LeerAsync<OrbitXStatusWire>("api/orbitx/status", corte.Token);
            await Task.WhenAll(tCfg, tSta).ConfigureAwait(false);

            return new OrbitXCargaResultado { Config = tCfg.Result, Status = tSta.Result };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El panel se cerró: nadie va a mirar el resultado.
            return new OrbitXCargaResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new OrbitXCargaResultado { Error = Motivo(ex) };
        }
    }

    // ====================================================================
    //  Guardar / Probar conexión
    // ====================================================================

    /// <summary>POST /api/orbitx/config con el DTO ENTERO (application/json).
    /// El controller hace MERGE sobre orbitX.json y pisa server_url con
    /// FixedServerUrl: mandarla no la cambia.</summary>
    public async Task<OrbitXOkResultado> GuardarAsync(OrbitXConfigWire dto, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteCorto);

            string body = JsonSerializer.Serialize(dto ?? new OrbitXConfigWire());
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/orbitx/config", contenido, corte.Token)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            // `(await res.json()).ok` — si el body no es JSON, el JS tira y cae
            // al catch ("Error: …"). Acá lo mismo: Parse propaga.
            using var doc = JsonDocument.Parse(Limpio(json));
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl)
                      && okEl.ValueKind == JsonValueKind.True;
            return new OrbitXOkResultado { Ok = ok };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new OrbitXOkResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new OrbitXOkResultado { Excepcion = Motivo(ex) };
        }
    }

    /// <summary>POST /api/orbitx/test → `{ ok, error? }`. El Engine pega un
    /// GET &lt;server_url&gt;/health con X-Device-ID + X-Auth-Token; acá solo se
    /// dispara y se lee el veredicto.</summary>
    public async Task<OrbitXTestResultado> ProbarAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // El health-check del cloud tiene 8 s de timeout propio en el
            // Engine: cortar antes mostraría "sin respuesta" con el cloud sano.
            corte.CancelAfter(CortePair);

            using var resp = await _http.PostAsync(_baseUrl + "api/orbitx/test", null, corte.Token)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(Limpio(json));
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl)
                      && okEl.ValueKind == JsonValueKind.True;
            string? err = null;
            if (doc.RootElement.TryGetProperty("error", out var errEl)
                && errEl.ValueKind == JsonValueKind.String)
                err = errEl.GetString();
            return new OrbitXTestResultado { Ok = ok, Error = err };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new OrbitXTestResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new OrbitXTestResultado { Excepcion = Motivo(ex) };
        }
    }

    // ====================================================================
    //  Pairing
    // ====================================================================

    /// <summary>GET /api/orbitx/pair-info (`cache: no-store`), el poll de 4 s
    /// de la página. Devuelve Excepcion != null cuando ni siquiera hubo
    /// respuesta del host local.</summary>
    public async Task<OrbitXPairResultado> PairInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CortePair);
            var info = await LeerAsync<OrbitXPairInfoWire>("api/orbitx/pair-info", corte.Token)
                             .ConfigureAwait(false);
            return new OrbitXPairResultado { Info = info };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new OrbitXPairResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new OrbitXPairResultado { Excepcion = Motivo(ex) };
        }
    }

    /// <summary>POST /api/orbitx/pair-reset — borra device_token + estab_slug y
    /// apaga enabled en orbitX.json, y tira el código en memoria. Destructivo:
    /// el panel SIEMPRE confirma antes.</summary>
    public async Task<OrbitXOkResultado> PairResetAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteCorto);
            using var resp = await _http.PostAsync(_baseUrl + "api/orbitx/pair-reset", null, corte.Token)
                                        .ConfigureAwait(false);
            // El JS ni mira el body: `await fetch(...)` y sigue al pollPair().
            _ = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            return new OrbitXOkResultado { Ok = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new OrbitXOkResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new OrbitXOkResultado { Excepcion = Motivo(ex) };
        }
    }

    // ====================================================================
    //  Teclado nativo
    // ====================================================================

    /// <summary>Abre/cierra la ventana del teclado virtual propio de PilotX.
    /// Misma señal que mandan las páginas HTML (keyboard.js). NUNCA osk.exe.
    /// Catch mudo: sin teclado nativo el campo sigue editable con el físico.</summary>
    public async Task TecladoAsync(bool abrir, string titulo = "", bool numerico = false)
    {
        try
        {
            string body = abrir
                ? "{\"numerico\":" + (numerico ? "true" : "false") +
                  ",\"titulo\":" + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var cuerpo = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(
                _baseUrl + "api/teclado/" + (abrir ? "abrir" : "cerrar"), cuerpo).ConfigureAwait(false);
        }
        catch { }
    }

    // ====================================================================
    //  helpers
    // ====================================================================

    /// <summary>GET + deserialize. NO mira el status code: `fetch` tampoco tira
    /// con 4xx/5xx — el JS hace `await res.json()` igual, y un
    /// `{ ok:false, error:"service-unavailable" }` termina pintando el
    /// formulario con todos los campos vacíos. Se conserva ese comportamiento.</summary>
    private async Task<T?> LeerAsync<T>(string ruta, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + ruta);
        // Equivalente a fetch(..., { cache: 'no-store' }).
        req.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                    .ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(Limpio(json), _jsonOpts);
    }

    private static string Motivo(Exception ex)
    {
        // Un corte por CorteCorto/CortePair llega como OperationCanceledException
        // sin mensaje útil: se traduce a algo que el operario pueda dictar.
        if (ex is OperationCanceledException) return "el servicio local no respondió";
        return ex.Message;
    }

    // El BOM de EmbedIO deja el JSON arrancando con U+FEFF y System.Text.Json
    // no lo perdona (escapes explícitos: como literales serían invisibles acá).
    private static string Limpio(string json)
        => (json ?? "").TrimStart('\uFEFF', '\u200B').TrimStart();
}
