// ============================================================================
// NodoDetalleClient.cs — cliente HTTP del DETALLE de un nodo.
//
// Lo consume NodoDetallePanel (port nativo de pages/nodo-detalle.html +
// js/nodo-detalle.js). Wire IDÉNTICO al de la página — cero cambio de contrato,
// porque nodo-detalle.html sigue viva para el Hub remoto / la PWA del celular:
//
//   GET  /api/nodos/{uid}/estado         → matriz wifi/mqtt/target/status + meta
//   GET  /api/nodos/{uid}/firmwares      → versiones .bin en el cache LAN
//   POST /api/nodos/{uid}/ota            body: { "version": "1.2.3" }
//   GET  /api/nodos/{uid}/ota/progress   → último estado del OTA
//   POST /api/nodos/{uid}/cmd            body: { "cmd": "reiniciar" }
//   POST /api/nodos/asignacion-implemento body: { "uid": "...", "asignado": true }
//
// ── OTA: qué manda y qué NO manda este cliente ──────────────────────────────
// El body del OTA lleva SOLO `version`, exactamente como el JS
// (`postJson(..., { version: version })`). El campo `allow_downgrade` EXISTE en
// NodosController.OtaBody, pero la página NUNCA lo manda: al deserializar queda
// en `false` y el guard anti-downgrade semver de FirmwareOtaCoordinator
// (H1: IsDowngrade(current, requested)) queda ACTIVO. Mandarlo en true sería
// habilitar el downgrade desde una pantalla que hoy no lo ofrece — o sea,
// inventar una salida de seguridad que el original no tiene. Si alguna vez hace
// falta, va con confirmación aparte y se agrega acá y en el panel.
//
// El resto de los guards del OTA viven en el server / firmware y este cliente
// NO los toca ni los duplica: SHA-256 esperado en el payload MQTT, watchdog de
// 5 min que degrada a "error timeout_sin_resultado_5min", dedup ring del relay
// al cloud y el flujo de progreso agp/{producto}/{UID}/ota/progress.
//
// Trampas del wire cubiertas acá:
//   · snake_case: last_seen_utc / broker_connected / estado_curado / target_in /
//     progress_pct / hash_sha256 / tamano_bytes / es_actual / http_port /
//     lan_ip / firmware_actual / del_implemento_activo / boot_reason /
//     safe_mode / crash_count / config_sync NO matchean con
//     PropertyNameCaseInsensitive (el case-insensitive de System.Text.Json no
//     cubre underscores) — va [JsonPropertyName] en CADA propiedad.
//   · BOM de EmbedIO: las respuestas pueden arrancar con U+FEFF y sin TrimStart
//     el Deserialize tira (mismo cuidado que FirmwaresClient/SonidosClient).
//   · Sin HttpClient.Timeout: la cancelación va SIEMPRE por CancellationToken.
//     El POST del OTA espera el ack del envelope (ttl 10 s en el coordinator) y
//     un Timeout de 3-5 s lo cortaría con el flasheo YA disparado, dejando la
//     pantalla convencida de que no salió. Ese POST va con
//     CancellationToken.None a propósito: NO se cancela ni cerrando el panel —
//     una vez confirmado, el comando sale sí o sí (el panel solo apaga sus
//     polls, que esos sí cuelgan de su token).
//   · TaskCanceledException ES OperationCanceledException: el catch va con
//     `when (ct.IsCancellationRequested)` para no confundir "cerré el panel"
//     con "el Hub no contesta".
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

// ---------------------------------------------------------------- estado

/// <summary>Fila activa de la matriz + las 4 capas resueltas por el server.</summary>
public sealed class NodoMatrizWire
{
    [JsonPropertyName("wifi")]       public string? Wifi      { get; set; }
    [JsonPropertyName("mqtt")]       public string? Mqtt      { get; set; }
    [JsonPropertyName("target_in")]  public string? TargetIn  { get; set; }
    [JsonPropertyName("status_out")] public string? StatusOut { get; set; }
    /// <summary>broker-down | wifi-off | wifi-ok-mqtt-down | rx-ok-target-no |
    /// rx-no-target-ok | ok-pleno</summary>
    [JsonPropertyName("row")]        public string? Row       { get; set; }
}

/// <summary>Estado del OTA en curso (NodoOtaState del coordinator).</summary>
public sealed class NodoOtaWire
{
    /// <summary>idle | sent | iniciando | ok | error</summary>
    [JsonPropertyName("status")]          public string? Status       { get; set; }
    [JsonPropertyName("version")]         public string? Version      { get; set; }
    [JsonPropertyName("detalle")]         public string? Detalle      { get; set; }
    [JsonPropertyName("progress_pct")]    public double  ProgressPct  { get; set; }
    [JsonPropertyName("url")]             public string? Url          { get; set; }
    [JsonPropertyName("last_change_utc")] public string? LastChangeUtc { get; set; }
}

/// <summary>Tracker desired/reported (Tanda 2 #8). Solo viene si la PC le
/// publicó una config_desired al nodo al menos una vez.</summary>
public sealed class NodoConfigSyncWire
{
    /// <summary>in_sync | pending | drift | no_report</summary>
    [JsonPropertyName("status")]          public string? Status        { get; set; }
    [JsonPropertyName("desired_hash")]    public string? DesiredHash   { get; set; }
    [JsonPropertyName("desired_ts_utc")]  public string? DesiredTsUtc  { get; set; }
    [JsonPropertyName("reported_hash")]   public string? ReportedHash  { get; set; }
    [JsonPropertyName("reported_ts_utc")] public string? ReportedTsUtc { get; set; }
}

public sealed class NodoUmbralesWire
{
    [JsonPropertyName("online_sec")]       public int OnlineSec      { get; set; }
    [JsonPropertyName("status_fresh_sec")] public int StatusFreshSec { get; set; }
}

public sealed class NodoEstadoWire
{
    [JsonPropertyName("ok")]                    public bool    Ok                  { get; set; }
    [JsonPropertyName("error")]                 public string? Error               { get; set; }
    [JsonPropertyName("uid")]                   public string? Uid                 { get; set; }
    [JsonPropertyName("tipo")]                  public string? Tipo                { get; set; }
    [JsonPropertyName("alias")]                 public string? Alias               { get; set; }
    [JsonPropertyName("estado_curado")]         public string? EstadoCurado        { get; set; }
    [JsonPropertyName("ip")]                    public string? Ip                  { get; set; }
    [JsonPropertyName("firmware")]              public string? Firmware            { get; set; }
    [JsonPropertyName("online")]                public bool    Online              { get; set; }
    [JsonPropertyName("last_seen_utc")]         public string? LastSeenUtc         { get; set; }
    [JsonPropertyName("last_seen_sec")]         public int?    LastSeenSec         { get; set; }
    [JsonPropertyName("broker_connected")]      public bool    BrokerConnected     { get; set; }
    [JsonPropertyName("del_implemento_activo")] public bool    DelImplementoActivo { get; set; }
    [JsonPropertyName("boot_reason")]           public string? BootReason          { get; set; }
    [JsonPropertyName("safe_mode")]             public bool    SafeMode            { get; set; }
    // int? y no int: el JS pregunta `e.crash_count != null` antes de mostrar
    // "· N crashes". El server hoy siempre lo manda (0 incluido).
    [JsonPropertyName("crash_count")]           public int?    CrashCount          { get; set; }
    [JsonPropertyName("matriz")]                public NodoMatrizWire?     Matriz  { get; set; }
    [JsonPropertyName("ota")]                   public NodoOtaWire?        Ota     { get; set; }
    [JsonPropertyName("comandos_disponibles")]  public List<string>? ComandosDisponibles { get; set; }
    [JsonPropertyName("config_sync")]           public NodoConfigSyncWire? ConfigSync { get; set; }
    [JsonPropertyName("umbrales")]              public NodoUmbralesWire?   Umbrales   { get; set; }
}

// ------------------------------------------------------------- firmwares

public sealed class NodoFirmwareVersionWire
{
    [JsonPropertyName("version")]      public string? Version     { get; set; }
    [JsonPropertyName("hash_sha256")]  public string? HashSha256  { get; set; }
    [JsonPropertyName("tamano_bytes")] public long?   TamanoBytes { get; set; }
    [JsonPropertyName("changelog")]    public string? Changelog   { get; set; }
    [JsonPropertyName("es_actual")]    public bool    EsActual    { get; set; }
}

public sealed class NodoFirmwaresWire
{
    [JsonPropertyName("ok")]               public bool    Ok              { get; set; }
    [JsonPropertyName("error")]            public string? Error           { get; set; }
    [JsonPropertyName("uid")]              public string? Uid             { get; set; }
    [JsonPropertyName("producto")]         public string? Producto        { get; set; }
    [JsonPropertyName("firmware_actual")]  public string? FirmwareActual  { get; set; }
    [JsonPropertyName("http_port")]        public int     HttpPort        { get; set; }
    [JsonPropertyName("lan_ip")]           public string? LanIp           { get; set; }
    [JsonPropertyName("versiones")]        public List<NodoFirmwareVersionWire>? Versiones { get; set; }
}

// ------------------------------------------------------- respuestas POST

/// <summary>Respuesta del POST /ota: la URL y el topic son informativos (debug);
/// lo que mira la UI es `ok` y, si falla, `error` (p. ej.
/// "downgrade_bloqueado: nodo en 1.14.0, pedido 1.9.0").</summary>
public sealed class NodoOtaEnvioWire
{
    [JsonPropertyName("ok")]    public bool    Ok    { get; set; }
    [JsonPropertyName("url")]   public string? Url   { get; set; }
    [JsonPropertyName("topic")] public string? Topic { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class NodoCmdWire
{
    [JsonPropertyName("ok")]    public bool    Ok    { get; set; }
    [JsonPropertyName("topic")] public string? Topic { get; set; }
    [JsonPropertyName("cmd")]   public string? Cmd   { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class NodoOtaProgressWire
{
    [JsonPropertyName("ok")]  public bool         Ok  { get; set; }
    [JsonPropertyName("uid")] public string?      Uid { get; set; }
    [JsonPropertyName("ota")] public NodoOtaWire? Ota { get; set; }
}

public sealed class NodoAsignacionWire
{
    [JsonPropertyName("ok")]          public bool    Ok         { get; set; }
    [JsonPropertyName("error")]       public string? Error      { get; set; }
    [JsonPropertyName("slug")]        public string? Slug       { get; set; }
    [JsonPropertyName("asignado")]    public bool?   Asignado   { get; set; }
    [JsonPropertyName("sin_cambios")] public bool?   SinCambios { get; set; }
}

/// <summary>
/// Envoltorio de TODA llamada: el JS distingue dos fallas distintas y las
/// muestra distinto (`!data.ok` → el `error` del body; excepción del fetch →
/// "Error: " + e.message). Sin esto se perdería esa diferencia.
///
/// Cuando hay excepción se guarda además el trío AGP (código dictable + mensaje
/// amigable + detalle técnico) que arma AgpErrorMapper: el panel muestra el
/// código y el mensaje arriba y lo técnico en el desplegable.
/// </summary>
public sealed class NodoDetalleResultado<T> where T : class
{
    public T? Datos { get; set; }
    /// <summary>No-null = falló la red / el parseo (el catch del fetch).</summary>
    public string? ErrorRed { get; set; }
    /// <summary>Código AGP-* dictable por teléfono. Vacío si no hubo excepción.</summary>
    public string? ErrorCodigo { get; set; }
    /// <summary>Mensaje amigable del mapper (castellano).</summary>
    public string? ErrorAmigable { get; set; }
    /// <summary>Tipo + mensaje reales de la excepción (desplegable de soporte).</summary>
    public string? ErrorTecnico { get; set; }
    /// <summary>El panel se cerró en medio: no hay a quién mostrarle nada.</summary>
    public bool Cancelado { get; set; }
}

public sealed class NodoDetalleClient
{
    // Sin Timeout: la cancelación va SIEMPRE por CancellationToken (ver cabecera).
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Corte de las lecturas cortas (estado, firmwares, progreso, cmd).
    /// El POST del OTA NO lo usa.</summary>
    private static readonly TimeSpan CorteLectura = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl;

    public NodoDetalleClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Base del Hub local (termina en "/").</summary>
    public string BaseUrl => _baseUrl;

    // ------------------------------------------------------------- lecturas

    /// <summary>GET /api/nodos/{uid}/estado</summary>
    public Task<NodoDetalleResultado<NodoEstadoWire>> GetEstadoAsync(string uid, CancellationToken ct = default)
        => GetAsync<NodoEstadoWire>("api/nodos/" + Uri.EscapeDataString(uid ?? "") + "/estado", ct);

    /// <summary>GET /api/nodos/{uid}/firmwares</summary>
    public Task<NodoDetalleResultado<NodoFirmwaresWire>> GetFirmwaresAsync(string uid, CancellationToken ct = default)
        => GetAsync<NodoFirmwaresWire>("api/nodos/" + Uri.EscapeDataString(uid ?? "") + "/firmwares", ct);

    /// <summary>GET /api/nodos/{uid}/ota/progress</summary>
    public Task<NodoDetalleResultado<NodoOtaProgressWire>> GetOtaProgressAsync(string uid, CancellationToken ct = default)
        => GetAsync<NodoOtaProgressWire>("api/nodos/" + Uri.EscapeDataString(uid ?? "") + "/ota/progress", ct);

    // ------------------------------------------------------------- acciones

    /// <summary>
    /// POST /api/nodos/{uid}/ota con body { "version": "..." }.
    ///
    /// ESTE ES EL DISPARO REAL DEL FLASHEO. Sin corte de tiempo propio: el
    /// coordinator publica el comando y espera el ack del envelope (ttl 10 s);
    /// cortar antes dejaría al operario creyendo que no salió cuando el nodo ya
    /// está bajando el .bin. Solo se cancela si el panel se cierra.
    ///
    /// `allow_downgrade` NO se manda (ver cabecera del archivo): el guard
    /// anti-downgrade semver del server queda activo, igual que en la página.
    /// </summary>
    public async Task<NodoDetalleResultado<NodoOtaEnvioWire>> EnviarOtaAsync(
        string uid, string version, CancellationToken ct = default)
    {
        try
        {
            string body = "{\"version\":" + JsonSerializer.Serialize(version ?? "") + "}";
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(
                _baseUrl + "api/nodos/" + Uri.EscapeDataString(uid ?? "") + "/ota",
                contenido, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var datos = JsonSerializer.Deserialize<NodoOtaEnvioWire>(Limpio(json), _jsonOpts);
            if (datos == null)
                return new NodoDetalleResultado<NodoOtaEnvioWire> { ErrorRed = "respuesta ilegible" };
            return new NodoDetalleResultado<NodoOtaEnvioWire> { Datos = datos };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new NodoDetalleResultado<NodoOtaEnvioWire> { Cancelado = true };
        }
        catch (Exception ex)
        {
            return Falla<NodoOtaEnvioWire>(ex);
        }
    }

    /// <summary>POST /api/nodos/{uid}/cmd con body { "cmd": "..." }. El server
    /// valida contra su lista corta (reiniciar / borrar_wifi / estado / ping /
    /// clear_safe_mode) y rechaza el resto con "cmd-no-soportado".</summary>
    public Task<NodoDetalleResultado<NodoCmdWire>> EnviarCmdAsync(
        string uid, string cmd, CancellationToken ct = default)
        => PostAsync<NodoCmdWire>("api/nodos/" + Uri.EscapeDataString(uid ?? "") + "/cmd",
                                  "{\"cmd\":" + JsonSerializer.Serialize(cmd ?? "") + "}", ct);

    /// <summary>POST /api/nodos/asignacion-implemento — ata / desata el nodo del
    /// implemento ACTIVO (los que disparan alarma al caer offline).</summary>
    public Task<NodoDetalleResultado<NodoAsignacionWire>> AsignarImplementoAsync(
        string uid, bool asignado, CancellationToken ct = default)
        => PostAsync<NodoAsignacionWire>("api/nodos/asignacion-implemento",
               "{\"uid\":" + JsonSerializer.Serialize(uid ?? "") +
               ",\"asignado\":" + (asignado ? "true" : "false") + "}", ct);

    // -------------------------------------------------------------- helpers

    private async Task<NodoDetalleResultado<T>> GetAsync<T>(string ruta, CancellationToken ct) where T : class
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteLectura);
            using var resp = await _http.GetAsync(_baseUrl + ruta, corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            var datos = JsonSerializer.Deserialize<T>(Limpio(json), _jsonOpts);
            if (datos == null)
                return new NodoDetalleResultado<T> { ErrorRed = "respuesta ilegible" };
            return new NodoDetalleResultado<T> { Datos = datos };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El panel se cerró: nadie va a mirar el resultado.
            return new NodoDetalleResultado<T> { Cancelado = true };
        }
        catch (Exception ex)
        {
            return Falla<T>(ex);
        }
    }

    private async Task<NodoDetalleResultado<T>> PostAsync<T>(string ruta, string body, CancellationToken ct)
        where T : class
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteLectura);
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + ruta, contenido, corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            var datos = JsonSerializer.Deserialize<T>(Limpio(json), _jsonOpts);
            if (datos == null)
                return new NodoDetalleResultado<T> { ErrorRed = "respuesta ilegible" };
            return new NodoDetalleResultado<T> { Datos = datos };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new NodoDetalleResultado<T> { Cancelado = true };
        }
        catch (Exception ex)
        {
            return Falla<T>(ex);
        }
    }

    /// <summary>Traduce la excepción al trío AGP (código + amigable + técnico).</summary>
    private static NodoDetalleResultado<T> Falla<T>(Exception ex) where T : class
    {
        var e = AgroParallel.Services.AgpErrorMapper.FromException(ex);
        return new NodoDetalleResultado<T>
        {
            ErrorRed = Motivo(ex),
            ErrorCodigo = e.Code,
            ErrorAmigable = e.Friendly,
            ErrorTecnico = e.Technical
        };
    }

    private static string Motivo(Exception ex)
    {
        // Un corte por CorteLectura llega acá como OperationCanceledException sin
        // mensaje útil: se traduce a algo que el operario pueda dictar.
        if (ex is OperationCanceledException) return "el Hub no respondió";
        return ex.Message;
    }

    // El BOM de EmbedIO deja el JSON arrancando con U+FEFF y System.Text.Json no
    // lo perdona (escapes explícitos: como literales serían invisibles acá).
    private static string Limpio(string json) => (json ?? "").TrimStart('\uFEFF', '\u200B').TrimStart();
}
