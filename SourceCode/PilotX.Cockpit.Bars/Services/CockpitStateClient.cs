using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Cockpit.Bars.Services;

public sealed class CockpitStateClient : IDisposable
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _url;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    public event Action<CockpitSnapshot>? SnapshotReceived;
    public event Action<Exception>? PollFailed;

    public CockpitStateClient(string baseUrl = "http://127.0.0.1:5180/", int intervalMs = 250)
    {
        var b = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
              : baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        _url = b + "api/aog/state";
        _interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    public static CockpitSnapshot? Parse(string json) =>
        JsonSerializer.Deserialize<CockpitSnapshot>(json, _opts);

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(_url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var snap = Parse(json);
                if (snap != null) SnapshotReceived?.Invoke(snap);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { PollFailed?.Invoke(ex); }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => Stop();
}
