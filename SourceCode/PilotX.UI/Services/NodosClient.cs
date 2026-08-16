// NodosClient.cs
//
// Cliente HTTP del modulo Nodos. Cubre TODO el wire que usaba pages/nodos.js:
//   GET    api/nodos/unified              -> lista curada + live (cabina-alarmas, Hub, NodosPanel)
//   GET    api/nodos/diagnostic           -> diagnostico MQTT (broker, subs, gaps, log)
//   POST   api/nodos/aceptar              { uid, tipo, alias }
//   POST   api/nodos/ignorar              { uid }
//   POST   api/nodos/restaurar            { uid }
//   POST   api/nodos/renombrar            { uid, alias }
//   DELETE api/nodos/{uid}
//   POST   api/nodos/asignacion-implemento { uid, asignado }
//   POST   api/nodos/reconnect            (sin body, devuelve el diag nuevo)
//   POST   api/nodos/wildcard?on=true     (QUERYSTRING, body vacio)
//
// Contratos snake_case sin tocar: son los MISMOS endpoints que sigue usando la
// pagina HTML para el Hub remoto / celular. Todo metodo atrapa y devuelve
// null/false: el panel pinta "sin datos", jamas explota.
//
// Polling: unified 3s (igual al JS legacy), diagnostic 2s y SOLO mientras la
// pantalla de Diagnostico esta a la vista.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class NodoUnified
{
    [JsonPropertyName("uid")]                   public string? Uid                 { get; set; }
    [JsonPropertyName("tipo")]                  public string? Tipo                { get; set; }
    [JsonPropertyName("alias")]                 public string? Alias               { get; set; }
    [JsonPropertyName("estado")]                public string? Estado              { get; set; }
    [JsonPropertyName("online")]                public bool    Online              { get; set; }
    [JsonPropertyName("ip")]                    public string? Ip                  { get; set; }
    [JsonPropertyName("firmware")]              public string? Firmware            { get; set; }
    [JsonPropertyName("last_seen_utc")]         public string? LastSeenUtc         { get; set; }
    [JsonPropertyName("fecha_alta_utc")]        public string? FechaAltaUtc        { get; set; }
    [JsonPropertyName("crash_count")]           public int     CrashCount          { get; set; }
    [JsonPropertyName("del_implemento_activo")] public bool    DelImplementoActivo { get; set; }
    [JsonPropertyName("boot_reason")]           public string? BootReason          { get; set; }
    [JsonPropertyName("safe_mode")]             public bool    SafeMode            { get; set; }
}

public sealed class NodosUnifiedResponse
{
    [JsonPropertyName("ok")]               public bool   Ok              { get; set; }
    [JsonPropertyName("count")]            public int    Count           { get; set; }
    [JsonPropertyName("nodos")]            public List<NodoUnified>? Nodos { get; set; }
    [JsonPropertyName("broker_connected")] public bool   BrokerConnected { get; set; }
    [JsonPropertyName("implemento_slug")]  public string? ImplementoSlug { get; set; }
}

/// <summary>Un mensaje MQTT capturado (log del diagnostico).</summary>
public sealed class NodoDiagMsg
{
    [JsonPropertyName("timestamp_utc")] public string? TimestampUtc { get; set; }
    [JsonPropertyName("topic")]         public string? Topic        { get; set; }
    [JsonPropertyName("payload")]       public string? Payload      { get; set; }
}

/// <summary>Hueco en la secuencia `seq` de un firmware (mensajes perdidos).</summary>
public sealed class NodoDiagGap
{
    [JsonPropertyName("timestamp_utc")] public string? TimestampUtc { get; set; }
    [JsonPropertyName("uid")]           public string? Uid          { get; set; }
    [JsonPropertyName("schema")]        public string? Schema       { get; set; }
    [JsonPropertyName("last_seq")]      public int     LastSeq      { get; set; }
    [JsonPropertyName("new_seq")]       public int     NewSeq       { get; set; }
    [JsonPropertyName("missed")]        public int     Missed       { get; set; }
}

public sealed class NodoDiag
{
    [JsonPropertyName("broker_address")]       public string? BrokerAddress      { get; set; }
    [JsonPropertyName("broker_port")]          public int     BrokerPort         { get; set; }
    [JsonPropertyName("connected")]            public bool    Connected          { get; set; }
    [JsonPropertyName("wildcard_capture_on")]  public bool    WildcardCaptureOn  { get; set; }
    [JsonPropertyName("subscriptions")]        public List<string>? Subscriptions { get; set; }
    [JsonPropertyName("recent_messages")]      public List<NodoDiagMsg>? RecentMessages { get; set; }
    [JsonPropertyName("known_nodes_count")]    public int     KnownNodesCount    { get; set; }
    [JsonPropertyName("last_error")]           public string? LastError          { get; set; }
    [JsonPropertyName("last_error_code")]      public string? LastErrorCode      { get; set; }
    [JsonPropertyName("last_error_technical")] public string? LastErrorTechnical { get; set; }
    [JsonPropertyName("last_error_utc")]       public string? LastErrorUtc       { get; set; }
    [JsonPropertyName("last_connected_utc")]   public string? LastConnectedUtc   { get; set; }
    [JsonPropertyName("connect_attempts")]     public int?    ConnectAttempts    { get; set; }
    [JsonPropertyName("seq_gap_count")]        public long    SeqGapCount        { get; set; }
    [JsonPropertyName("seq_reset_count")]      public long    SeqResetCount      { get; set; }
    [JsonPropertyName("recent_seq_gaps")]      public List<NodoDiagGap>? RecentSeqGaps { get; set; }
}

public sealed class NodosDiagResponse
{
    [JsonPropertyName("ok")]    public bool      Ok    { get; set; }
    [JsonPropertyName("error")] public string?   Error { get; set; }
    [JsonPropertyName("diag")]  public NodoDiag? Diag  { get; set; }
}

/// <summary>Respuesta comun de las acciones de curado (aceptar/asignar/...).</summary>
public sealed class NodosOpResponse
{
    [JsonPropertyName("ok")]              public bool    Ok             { get; set; }
    [JsonPropertyName("error")]           public string? Error          { get; set; }
    [JsonPropertyName("auto_asignado")]   public bool?   AutoAsignado   { get; set; }
    [JsonPropertyName("implemento_slug")] public string? ImplementoSlug { get; set; }
    [JsonPropertyName("slug")]            public string? Slug           { get; set; }
    [JsonPropertyName("asignado")]        public bool?   Asignado       { get; set; }
    [JsonPropertyName("sin_cambios")]     public bool?   SinCambios     { get; set; }
}

public sealed class NodosClient
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

    public NodosClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Origen del Hub local (termina en "/"). Lo usa el panel para
    /// pedir el teclado nativo, que no es del modulo Nodos.</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>Abre/cierra la ventana del teclado nativo de PilotX. Misma senal
    /// que mandan las paginas HTML. Catch mudo a proposito: sin teclado nativo el
    /// campo sigue editable con teclado fisico.</summary>
    public async Task TecladoAsync(bool abrir, string titulo = "")
    {
        try
        {
            string body = abrir
                ? "{\"numerico\":false,\"titulo\":" + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(
                _baseUrl + "api/teclado/" + (abrir ? "abrir" : "cerrar"), contenido).ConfigureAwait(false);
        }
        catch { }
    }

    public async Task<NodosUnifiedResponse?> GetUnifiedAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/nodos/unified", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NodosUnifiedResponse>(json, _jsonOpts);
        }
        catch { return null; }
    }

    public async Task<NodosDiagResponse?> GetDiagnosticAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/nodos/diagnostic", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NodosDiagResponse>(json, _jsonOpts);
        }
        catch { return null; }
    }

    // ---------- acciones de curado ----------

    public Task<NodosOpResponse?> AceptarAsync(string uid, string? tipo, string alias, CancellationToken ct = default)
        => PostAsync("api/nodos/aceptar", new { uid, tipo = tipo ?? "", alias }, ct);

    public async Task<bool> IgnorarAsync(string uid, CancellationToken ct = default)
        => (await PostAsync("api/nodos/ignorar", new { uid }, ct).ConfigureAwait(false))?.Ok == true;

    public async Task<bool> RestaurarAsync(string uid, CancellationToken ct = default)
        => (await PostAsync("api/nodos/restaurar", new { uid }, ct).ConfigureAwait(false))?.Ok == true;

    public async Task<bool> RenombrarAsync(string uid, string alias, CancellationToken ct = default)
        => (await PostAsync("api/nodos/renombrar", new { uid, alias }, ct).ConfigureAwait(false))?.Ok == true;

    public async Task<bool> EliminarAsync(string uid, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.DeleteAsync(
                _baseUrl + "api/nodos/" + Uri.EscapeDataString(uid), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NodosOpResponse>(json, _jsonOpts)?.Ok == true;
        }
        catch { return false; }
    }

    /// <summary>Ata / desata el nodo del implemento ACTIVO (los que alarman al caer).</summary>
    public Task<NodosOpResponse?> AsignarImplementoAsync(string uid, bool asignado, CancellationToken ct = default)
        => PostAsync("api/nodos/asignacion-implemento", new { uid, asignado }, ct);

    // ---------- diagnostico ----------

    /// <summary>Reconecta al broker. Devuelve el diag NUEVO (el server lo manda
    /// junto con el ok) para ahorrarse el GET siguiente.</summary>
    public async Task<NodosDiagResponse?> ReconnectAsync(CancellationToken ct = default)
    {
        try
        {
            using var contenido = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/nodos/reconnect", contenido, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NodosDiagResponse>(json, _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Captura wildcard "#" on/off. El `on` va por QUERYSTRING (asi lo
    /// declara el controller con [QueryField]), no en el body.</summary>
    public async Task<bool> SetWildcardAsync(bool on, CancellationToken ct = default)
    {
        try
        {
            using var contenido = new StringContent("", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(
                _baseUrl + "api/nodos/wildcard?on=" + (on ? "true" : "false"), contenido, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NodosOpResponse>(json, _jsonOpts)?.Ok == true;
        }
        catch { return false; }
    }

    private async Task<NodosOpResponse?> PostAsync(string ruta, object body, CancellationToken ct)
    {
        try
        {
            using var contenido = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + ruta, contenido, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NodosOpResponse>(json, _jsonOpts);
        }
        catch { return null; }
    }
}
