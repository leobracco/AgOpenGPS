// NodosClient.cs
//
// Cliente HTTP minimo para /api/nodos/unified — vista combinada curado + live
// que consume la pagina cabina-alarmas.html del Hub. Solo se necesita para
// filtrar nodos `del_implemento_activo && !online` y dispararle el banner al
// operario.
//
// Polling: 2s (igual al JS legacy). Cada 2s vale: lo suficientemente rapido
// para detectar caidas + razonable para el broker MQTT + no spamea la PC.

using System;
using System.Collections.Generic;
using System.Net.Http;
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
    [JsonPropertyName("ok")]              public bool   Ok              { get; set; }
    [JsonPropertyName("count")]           public int    Count           { get; set; }
    [JsonPropertyName("nodos")]           public List<NodoUnified>? Nodos { get; set; }
    [JsonPropertyName("brokerConnected")] public bool   BrokerConnected { get; set; }
    [JsonPropertyName("implementoSlug")]  public string? ImplementoSlug { get; set; }
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
}
