// CamarasClient.cs
//
// Cliente HTTP para el monitor de camaras (pages/camaras.html).
// Endpoints:
//   GET /api/camaras/config             -> { ok, config: { camaras:[...], refrescoMs } }
//   GET /api/camaras/{idx}/snapshot?t=N -> JPEG (snapshot bajo demanda)
//
// El backend habla ISAPI con la Hikvision en LAN; aca solo consumimos los
// snapshots ya renderizados como JPEG.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class CamaraDto
{
    [JsonPropertyName("nombre")] public string? Nombre { get; set; }
    [JsonPropertyName("ip")]     public string? Ip     { get; set; }
    [JsonPropertyName("activa")] public bool    Activa { get; set; }
}

public sealed class CamarasConfig
{
    [JsonPropertyName("camaras")]    public List<CamaraDto>? Camaras    { get; set; }
    [JsonPropertyName("refrescoMs")] public int              RefrescoMs { get; set; } = 1000;
}

public sealed class CamarasConfigResponse
{
    [JsonPropertyName("ok")]     public bool           Ok     { get; set; }
    [JsonPropertyName("config")] public CamarasConfig? Config { get; set; }
    [JsonPropertyName("error")]  public string?        Error  { get; set; }
}

public sealed class CamarasClient
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

    public CamarasClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<CamarasConfigResponse?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/camaras/config", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CamarasConfigResponse>(json, _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Devuelve los bytes JPEG del snapshot o null si fallo.</summary>
    public async Task<byte[]?> GetSnapshotAsync(int idx, CancellationToken ct = default)
    {
        try
        {
            // El timestamp evita cacheo entre frames (igual que /api/camaras/{idx}/snapshot?t=Date.now()).
            var url = _baseUrl + "api/camaras/" + idx.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      "/snapshot?t=" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch { return null; }
    }
}
