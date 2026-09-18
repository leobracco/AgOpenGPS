// StormXClient.cs
//
// Cliente HTTP minimo para el StormXController:
//   GET /api/stormx/live   -> snapshot meteo (nodos + limits)
//   GET /api/stormx/config -> umbrales operativos
//
// Reemplaza al stormx.js del Hub WebView. Cuando el AgpWebHost desaparezca
// (objetivo del pivot), esta clase se sustituye por una llamada directa al
// IStormXLiveService en el mismo proceso — la API publica del cliente ya
// coincide con la del servicio para que sea un swap mecanico.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Umbrales operativos para advertir condiciones de pulverizacion.</summary>
public sealed class StormXLimits
{
    [JsonPropertyName("wind_max_ms")]   public double WindMaxMs   { get; set; }
    [JsonPropertyName("wind_min_ms")]   public double WindMinMs   { get; set; }
    [JsonPropertyName("hum_min_pct")]   public double HumMinPct   { get; set; }
    [JsonPropertyName("temp_max_c")]    public double TempMaxC    { get; set; }
    [JsonPropertyName("delta_t_max_c")] public double DeltaTMaxC  { get; set; }
}

/// <summary>Lectura meteo de un nodo StormX (subset usado por la UI).</summary>
public sealed class StormXNodoLive
{
    [JsonPropertyName("uid")]           public string? Uid       { get; set; }
    [JsonPropertyName("nombre")]        public string? Nombre    { get; set; }
    [JsonPropertyName("online")]        public bool   Online     { get; set; }
    [JsonPropertyName("wind_ms")]       public double WindMs     { get; set; }
    [JsonPropertyName("gust_ms")]       public double GustMs     { get; set; }
    [JsonPropertyName("wind_dir")]      public double WindDir    { get; set; }
    [JsonPropertyName("temp_c")]        public double TempC      { get; set; }
    [JsonPropertyName("hum_pct")]       public double HumPct     { get; set; }
    [JsonPropertyName("press_hpa")]     public double PressHpa   { get; set; }
    [JsonPropertyName("delta_t_c")]     public double DeltaTC    { get; set; }
    [JsonPropertyName("verdict")]       public string? Verdict   { get; set; }
    [JsonPropertyName("last_seen_iso")] public string? LastSeenIso { get; set; }
}

/// <summary>Snapshot consolidado para la UI de StormX.</summary>
public sealed class StormXLiveSnapshot
{
    [JsonPropertyName("monitoreo_activo")] public bool MonitoreoActivo { get; set; }
    [JsonPropertyName("limits")]           public StormXLimits? Limits { get; set; }
    [JsonPropertyName("nodos")]            public List<StormXNodoLive>? Nodos { get; set; }
}

public sealed class StormXClient
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

    public StormXClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>
    /// Trae el snapshot meteo. Devuelve null si el endpoint no responde
    /// (host caido, timeout) — el panel pinta estado "sin conexion".
    /// </summary>
    public async Task<StormXLiveSnapshot?> GetLiveAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/stormx/live", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<StormXLiveSnapshot>(json, _jsonOpts);
        }
        catch { return null; }
    }
}
