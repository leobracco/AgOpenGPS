// OverlaysClient.cs
//
// Cliente fino para /api/overlays — preferencias de widgets ON/OFF sobre el
// mapa principal de PilotX. FormGPS relee overlayPrefs.json cada 250 ms y
// aplica los cambios en caliente, sin reiniciar.
//
// Contrato:
//   GET  /api/overlays  -> { qx_overlay: bool, vx_overlay: bool, fx_overlay: bool }
//   POST /api/overlays  body idem -> persiste a overlayPrefs.json

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class OverlayPrefs
{
    [JsonPropertyName("qx_overlay")] public bool QxOverlay { get; set; } = true;
    [JsonPropertyName("vx_overlay")] public bool VxOverlay { get; set; } = true;
    [JsonPropertyName("fx_overlay")] public bool FxOverlay { get; set; } = true;
}

public sealed class OverlaysClient
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

    public OverlaysClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<OverlayPrefs?> GetAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/overlays", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<OverlayPrefs>(json, _jsonOpts);
        }
        catch { return null; }
    }

    public async Task<bool> SaveAsync(OverlayPrefs prefs, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(prefs, _jsonOpts);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/overlays", content, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
