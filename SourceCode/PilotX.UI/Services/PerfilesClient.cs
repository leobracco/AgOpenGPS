// PerfilesClient.cs
//
// Cliente HTTP de la pantalla "Perfiles del vehículo" (port nativo de
// pages/perfiles.html + js/perfiles.js). Cubre EXACTAMENTE el mismo wire que
// usaba el JS, sin agregar ni sacar un endpoint:
//
//   GET  api/aog/perfiles              -> { ok, activo, is_job_started, perfiles[] }
//   POST api/aog/perfiles/cargar       { nombre }
//   POST api/aog/perfiles/nuevo        { nombre, desde }
//   POST api/aog/perfiles/copiar       { origen, nuevo }
//   POST api/aog/perfiles/borrar       { nombre, clave }
//   POST api/aog/perfiles/proteger     { nombre, clave }
//   POST api/aog/perfiles/desproteger  { nombre, clave }
//   POST api/config/backup             (sin body ni query — igual que el JS)
//
// Contratos snake_case intactos: son los MISMOS endpoints que sigue usando la
// página HTML para el Hub remoto / celular (PerfilesController + EnginePerfilService).
//
// A DIFERENCIA de los otros clientes de este proyecto, acá los errores de red
// se PROPAGAN en vez de devolver null: la página distingue tres estados que el
// operario ve distinto — "Servicio de perfiles no disponible" (ok:false del
// server), "Sin conexión: <motivo>" (el fetch falló) y "Error: <motivo>" (falló
// la acción del diálogo). Tragarse la excepción acá borraría esa diferencia.
//
// Cancelación: SIEMPRE por CancellationToken, nunca por HttpClient.Timeout —
// un TaskCanceledException por Timeout es indistinguible de una cancelación
// real y congela el polling en sus valores viejos (memoria
// feedback_httpclient_timeout_kills_polling). El techo por request se arma con
// un CTS enlazado, así el que salta por tiempo NO trae ct.IsCancellationRequested
// y el panel lo puede pintar como "Sin conexión", que es lo que es.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Una fila de la lista (PerfilItemDto del server).</summary>
public sealed class PerfilItem
{
    [JsonPropertyName("nombre")]    public string? Nombre   { get; set; }
    [JsonPropertyName("protegido")] public bool    Protegido { get; set; }
    [JsonPropertyName("activo")]    public bool    Activo   { get; set; }
}

/// <summary>GET /api/aog/perfiles.</summary>
public sealed class PerfilesSnapshotResponse
{
    [JsonPropertyName("ok")]             public bool   Ok           { get; set; }
    [JsonPropertyName("activo")]         public string? Activo      { get; set; }
    [JsonPropertyName("is_job_started")] public bool   IsJobStarted { get; set; }
    [JsonPropertyName("perfiles")]       public List<PerfilItem>? Perfiles { get; set; }
    [JsonPropertyName("error")]          public string? Error       { get; set; }
}

/// <summary>Respuesta común de cargar/nuevo/copiar/borrar/proteger/desproteger.</summary>
public sealed class PerfilAccionResponse
{
    [JsonPropertyName("ok")]    public bool    Ok    { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>POST /api/config/backup (el mismo backup general que sistema.html).</summary>
public sealed class PerfilBackupResponse
{
    [JsonPropertyName("ok")]      public bool    Ok      { get; set; }
    [JsonPropertyName("carpeta")] public string? Carpeta { get; set; }
    [JsonPropertyName("nombre")]  public string? Nombre  { get; set; }
    [JsonPropertyName("error")]   public string? Error   { get; set; }
    [JsonPropertyName("detail")]  public string? Detail  { get; set; }
}

public sealed class PerfilesClient
{
    // Timeout.InfiniteTimeSpan a propósito: el techo lo pone el CTS enlazado de
    // cada request (ver cabecera).
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    // BOM / zero-width que a veces deja el host delante del JSON: System.Text.Json
    // explota con eso y el síntoma sería "Sin conexión" con el engine vivo.
    // (escapes explícitos: como literales serían invisibles en el fuente)
    private static readonly char[] Basura = new[] { '\uFEFF', '\u200B' };

    private const int SegundosPorRequest = 8;

    private readonly string _baseUrl;

    public PerfilesClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Origen del Hub local (termina en "/").</summary>
    public string BaseUrl => _baseUrl;

    // ---- lectura -----------------------------------------------------------

    /// <summary>GET /api/aog/perfiles. Lanza si el fetch falla (el panel lo
    /// pinta como "Sin conexión", igual que el catch del JS).</summary>
    public async Task<PerfilesSnapshotResponse?> GetPerfilesAsync(CancellationToken ct = default)
    {
        var json = await LeerAsync(HttpMethod.Get, "api/aog/perfiles", null, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PerfilesSnapshotResponse>(json, _jsonOpts);
    }

    // ---- acciones ----------------------------------------------------------

    /// <summary>POST /api/aog/perfiles/{ruta} con body JSON. `ruta` va SIN barra
    /// inicial ("cargar", "nuevo", …). Las claves del body son las mismas
    /// palabras que mandaba el JS.</summary>
    public async Task<PerfilAccionResponse?> AccionAsync(
        string ruta, Dictionary<string, string?> body, CancellationToken ct = default)
    {
        string payload = JsonSerializer.Serialize(body ?? new Dictionary<string, string?>());
        var json = await LeerAsync(HttpMethod.Post, "api/aog/perfiles/" + ruta, payload, ct)
                        .ConfigureAwait(false);
        return JsonSerializer.Deserialize<PerfilAccionResponse>(json, _jsonOpts);
    }

    public Task<PerfilAccionResponse?> CargarAsync(string nombre, CancellationToken ct = default)
        => AccionAsync("cargar", new Dictionary<string, string?> { ["nombre"] = nombre }, ct);

    public Task<PerfilAccionResponse?> NuevoAsync(string nombre, string desde, CancellationToken ct = default)
        => AccionAsync("nuevo", new Dictionary<string, string?> { ["nombre"] = nombre, ["desde"] = desde }, ct);

    public Task<PerfilAccionResponse?> CopiarAsync(string origen, string nuevo, CancellationToken ct = default)
        => AccionAsync("copiar", new Dictionary<string, string?> { ["origen"] = origen, ["nuevo"] = nuevo }, ct);

    public Task<PerfilAccionResponse?> BorrarAsync(string nombre, string clave, CancellationToken ct = default)
        => AccionAsync("borrar", new Dictionary<string, string?> { ["nombre"] = nombre, ["clave"] = clave }, ct);

    public Task<PerfilAccionResponse?> ProtegerAsync(string nombre, string clave, CancellationToken ct = default)
        => AccionAsync("proteger", new Dictionary<string, string?> { ["nombre"] = nombre, ["clave"] = clave }, ct);

    public Task<PerfilAccionResponse?> DesprotegerAsync(string nombre, string clave, CancellationToken ct = default)
        => AccionAsync("desproteger", new Dictionary<string, string?> { ["nombre"] = nombre, ["clave"] = clave }, ct);

    /// <summary>POST /api/config/backup — SIN body y SIN query-string, igual que
    /// el JS (el server toma `tipo` de la query y con null hace el backup normal
    /// forzado).</summary>
    public async Task<PerfilBackupResponse?> BackupAsync(CancellationToken ct = default)
    {
        var json = await LeerAsync(HttpMethod.Post, "api/config/backup", null, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PerfilBackupResponse>(json, _jsonOpts);
    }

    // ---- teclado nativo ----------------------------------------------------

    /// <summary>Abre/cierra la ventana del teclado nativo de PilotX. Misma señal
    /// HTTP que manda keyboard.js en las páginas del Hub. Catch mudo a propósito:
    /// sin teclado en pantalla el campo se sigue editando con uno físico.</summary>
    public async Task TecladoAsync(bool abrir, string titulo = "")
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string body = abrir
                ? "{\"numerico\":false,\"titulo\":" + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_baseUrl + "api/teclado/" + (abrir ? "abrir" : "cerrar"),
                                                contenido, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    // ---- plomería ----------------------------------------------------------

    private async Task<string> LeerAsync(HttpMethod metodo, string ruta, string? bodyJson, CancellationToken ct)
    {
        using var techo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        techo.CancelAfter(TimeSpan.FromSeconds(SegundosPorRequest));

        using var req = new HttpRequestMessage(metodo, _baseUrl + ruta);
        if (bodyJson != null)
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, techo.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException("HTTP " + (int)resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync(techo.Token).ConfigureAwait(false);
        return (json ?? "").TrimStart(Basura).TrimStart();
    }
}
