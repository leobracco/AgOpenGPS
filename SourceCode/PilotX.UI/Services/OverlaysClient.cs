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

    // Posición donde el operario dejó el overlay. -1 = nunca lo movió, va a su
    // rincón por defecto. El POST del server hace merge, así que mandar solo
    // estos dos campos no pisa los flags ni las posiciones de los otros widgets.
    [JsonPropertyName("qx_x")] public int QxX { get; set; } = -1;
    [JsonPropertyName("qx_y")] public int QxY { get; set; } = -1;
}

/// <summary>Solo la posición de QuantiX, para guardarla sin arrastrar el resto
/// del objeto (el server aplica únicamente los campos que llegan).</summary>
public sealed class OverlayPosQx
{
    [JsonPropertyName("qx_x")] public int QxX { get; set; }
    [JsonPropertyName("qx_y")] public int QxY { get; set; }
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
        => await PostAsync(prefs, ct).ConfigureAwait(false);

    /// <summary>Guarda dónde quedó el overlay de QuantiX después de moverlo.</summary>
    public async Task<bool> SavePosQxAsync(int x, int y, CancellationToken ct = default)
        => await PostAsync(new OverlayPosQx { QxX = x, QxY = y }, ct).ConfigureAwait(false);

    private async Task<bool> PostAsync<T>(T payload, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(payload, _jsonOpts);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/overlays", content, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
