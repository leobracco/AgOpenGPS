// HudPoller.cs
//
// Servicio chiquito que consume GET /api/aog/state del AgpWebHost en loop
// y dispara un evento cada vez que llega snapshot nueva. Lo usa el chrome
// de PilotX.Desktop para mostrar speed/heading/GPS-status arriba del
// WebView, sin tocar todavia el render OpenGL del mapa.
//
// Cadencia: 4 Hz (250 ms). El piloto.js del Hub usa 10 Hz, pero para una
// barra de texto 4 Hz es suficiente y deja menos huella en CPU.
//
// Nota: el endpoint /api/aog/state lo serializa EmbedIO con Swan, que
// emite PascalCase. Usamos PropertyNameCaseInsensitive para tolerar
// ambos formatos sin tener que conocer cual esta activo.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>
/// Subset minimo del AogStateSnapshot que necesita el HUD. No mapea todo
/// para no acoplarse a cambios del DTO core; solo lo que se renderiza.
/// </summary>
public sealed class HudSnapshot
{
    [JsonPropertyName("isJobStarted")] public bool IsJobStarted { get; set; }
    [JsonPropertyName("avgSpeed")]     public double AvgSpeed { get; set; }   // km/h
    [JsonPropertyName("heading")]      public double Heading { get; set; }    // rad
    [JsonPropertyName("latitude")]     public double Latitude { get; set; }
    [JsonPropertyName("longitude")]    public double Longitude { get; set; }
    [JsonPropertyName("workedAreaTotalM2")]  public double WorkedAreaTotalM2 { get; set; }
    [JsonPropertyName("actualAreaCoveredM2")] public double ActualAreaCoveredM2 { get; set; }
}

public sealed class HudPoller : IDisposable
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Se dispara cada vez que el poller obtiene un snapshot exitoso. Se
    /// invoca en el thread del Task; el listener (MainWindow) debe hacer
    /// marshalling al UI thread con Dispatcher.UIThread.Post().
    /// </summary>
    public event Action<HudSnapshot>? SnapshotReceived;

    /// <summary>
    /// Se dispara cuando una request falla (host caido, timeout, etc).
    /// El listener pinta un estado "desconectado" en el HUD.
    /// </summary>
    public event Action<Exception>? PollFailed;

    public HudPoller(string baseUrl = "http://127.0.0.1:5180/", int intervalMs = 250)
    {
        // Normalizo trailing slash para concatenar con "api/aog/state".
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
        _interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var url = _baseUrl + "api/aog/state";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var snap = JsonSerializer.Deserialize<HudSnapshot>(json, _jsonOpts);
                if (snap != null) SnapshotReceived?.Invoke(snap);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                PollFailed?.Invoke(ex);
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => Stop();
}
