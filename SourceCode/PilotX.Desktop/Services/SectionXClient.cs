// SectionXClient.cs
//
// Cliente HTTP minimo para el SectionXController:
//   GET /api/sectionx/status -> { connected, running, nodoCount, lastPublishMsAgo }
//
// Reemplaza a la parte cabin-live de sectionx.js (chip semaforo del bridge).
// El editor de mapeo surcos->secciones (POST /api/sectionx/config + test/relays
// + debug) sigue en HTML — strangler-fig: portamos solo lo que ve el operario
// en cabina. El estado de cada seccion lo aporta el HudSnapshot.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class SectionXStatus
{
    [JsonPropertyName("connected")]        public bool   Connected         { get; set; }
    [JsonPropertyName("running")]          public bool   Running           { get; set; }
    [JsonPropertyName("nodoCount")]        public int    NodoCount         { get; set; }
    [JsonPropertyName("lastPublishMsAgo")] public double? LastPublishMsAgo { get; set; }
}

public sealed class SectionXClient
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

    public SectionXClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<SectionXStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/sectionx/status", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SectionXStatus>(json, _jsonOpts);
        }
        catch { return null; }
    }
}
