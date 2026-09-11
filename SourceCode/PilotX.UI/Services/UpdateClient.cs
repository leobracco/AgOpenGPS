// UpdateClient.cs
//
// Cliente HTTP para el self-update de PilotX (pages/actualizar.html).
// Endpoints:
//   GET  /api/pilotx/update/status   -> estado actual (siempre disponible)
//   POST /api/pilotx/update/check    -> dispara consulta al canal de updates
//   POST /api/pilotx/update/download -> descarga el ZIP a staging local
//   POST /api/pilotx/update/apply    -> lanza Updater.exe y cierra PilotX
//
// Phases: 0=Idle, 1=Checking, 2=UpdateAvailable, 3=Downloading,
//         4=ReadyToApply, 5=Applying, 9=Error.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class UpdateStatus
{
    // OJO: el wire es snake_case (PilotXUpdateStatus sin atributos sale por
    // AgpControllerBase → SnakeCaseLower; actualizar.js lee current_version,
    // staging_ready, etc.) y el case-insensitive de System.Text.Json NO cubre
    // underscores: con "currentVersion"/"stagingReady" acá, el panel nativo
    // de Actualizar mostraba versiones "—" y la barra de descarga muerta.
    [JsonPropertyName("current_version")]    public string? CurrentVersion   { get; set; }
    [JsonPropertyName("available_version")]  public string? AvailableVersion { get; set; }
    [JsonPropertyName("size_bytes")]         public long    SizeBytes        { get; set; }
    [JsonPropertyName("sha256")]             public string? Sha256           { get; set; }
    [JsonPropertyName("last_check_unix_ms")] public long    LastCheckUnixMs  { get; set; }
    [JsonPropertyName("phase")]              public int     Phase            { get; set; }
    [JsonPropertyName("progress_pct")]       public double  ProgressPct      { get; set; }
    [JsonPropertyName("last_error")]         public string? LastError        { get; set; }
    [JsonPropertyName("changelog")]          public string? Changelog        { get; set; }
    [JsonPropertyName("staging_ready")]      public bool    StagingReady     { get; set; }
}

public sealed class UpdateStatusResponse
{
    [JsonPropertyName("ok")]     public bool          Ok     { get; set; }
    [JsonPropertyName("status")] public UpdateStatus? Status { get; set; }
}

public sealed class UpdateClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public UpdateClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public Task<UpdateStatusResponse?> GetStatusAsync(CancellationToken ct = default)
        => GetAsync("api/pilotx/update/status", ct);

    public Task<UpdateStatusResponse?> CheckAsync(CancellationToken ct = default)
        => PostAsync("api/pilotx/update/check", ct);

    public Task<UpdateStatusResponse?> DownloadAsync(CancellationToken ct = default)
        => PostAsync("api/pilotx/update/download", ct);

    public Task<UpdateStatusResponse?> ApplyAsync(CancellationToken ct = default)
        => PostAsync("api/pilotx/update/apply", ct);

    private async Task<UpdateStatusResponse?> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdateStatusResponse>(json, _jsonOpts);
        }
        catch { return null; }
    }

    private async Task<UpdateStatusResponse?> PostAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsync(_baseUrl + path, null, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdateStatusResponse>(json, _jsonOpts);
        }
        catch { return null; }
    }
}
