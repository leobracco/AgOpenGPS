// FlowXClient.cs
//
// Cliente HTTP minimo para el FlowXController:
//   GET /api/flowx/config -> FlowXConfig (nodos + productos + cables)
//   GET /api/flowx/live   -> FlowXLiveSnapshot (caudal real, PWM, PID estado)
//
// Reemplaza al flowx.js del Hub para el flujo "ver datos cabin-live". El
// editor de config (productos / cables / PID) sigue por ahora en el WebView
// HTML — strangler-fig: portamos lo cabin-critical primero.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class FlowXProducto
{
    [JsonPropertyName("id")]        public int    Id        { get; set; }
    [JsonPropertyName("nombre")]    public string? Nombre   { get; set; }
    [JsonPropertyName("dosis_lha")] public double DosisLha  { get; set; }
}

public sealed class FlowXNodoConfig
{
    [JsonPropertyName("uid")]            public string? Uid          { get; set; }
    [JsonPropertyName("nombre")]         public string? Nombre       { get; set; }
    [JsonPropertyName("habilitado")]     public bool   Habilitado    { get; set; }
    [JsonPropertyName("ancho_barra_m")]  public double AnchoBarraM   { get; set; }
    [JsonPropertyName("productos")]      public List<FlowXProducto>? Productos { get; set; }
}

public sealed class FlowXConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("nodos")]   public List<FlowXNodoConfig>? Nodos { get; set; }
}

public sealed class FlowXNodoLive
{
    [JsonPropertyName("uid")]          public string? Uid       { get; set; }
    [JsonPropertyName("nombre")]       public string? Nombre    { get; set; }
    [JsonPropertyName("online")]       public bool   Online     { get; set; }
    [JsonPropertyName("caudal_lmin")]  public double CaudalLmin { get; set; }
    [JsonPropertyName("target_lmin")]  public double TargetLmin { get; set; }
    [JsonPropertyName("pwm")]          public int    Pwm        { get; set; }
    [JsonPropertyName("pid_estado")]   public string? PidEstado { get; set; }
}

public sealed class FlowXLiveSnapshot
{
    [JsonPropertyName("monitoreo_activo")] public bool MonitoreoActivo { get; set; }
    [JsonPropertyName("nodos")]            public List<FlowXNodoLive>? Nodos { get; set; }
}

public sealed class FlowXClient
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

    public FlowXClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<FlowXConfig?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/flowx/config", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FlowXConfig>(json, _jsonOpts);
        }
        catch { return null; }
    }

    public async Task<FlowXLiveSnapshot?> GetLiveAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/flowx/live", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FlowXLiveSnapshot>(json, _jsonOpts);
        }
        catch { return null; }
    }
}
