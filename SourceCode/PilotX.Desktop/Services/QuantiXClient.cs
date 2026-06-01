// QuantiXClient.cs
//
// Cliente HTTP minimo para el QuantiXController:
//   GET /api/quantix/live -> { nodos: [{ uid, ip, firmware, online,
//                                        motorsLive: [{ id, ppsTarget, ppsReal,
//                                                       pwm, rpm, pulsos,
//                                                       lastSeenUtc }] }] }
//
// Reemplaza al pollLive() de quantix.js — pero SOLO para la tab Monitor que es
// cabin-critical. Las otras tabs (Motores CRUD, Shape upload, PID live-tune,
// Calibracion, Prueba) siguen en HTML — son flujos de configuracion/diagnostico
// que el operario no toca manejando.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class QuantiXMotorLive
{
    [JsonPropertyName("id")]          public int    Id          { get; set; }
    [JsonPropertyName("ppsTarget")]   public double PpsTarget   { get; set; }
    [JsonPropertyName("ppsReal")]     public double PpsReal     { get; set; }
    [JsonPropertyName("pwm")]         public int    Pwm         { get; set; }
    [JsonPropertyName("rpm")]         public int    Rpm         { get; set; }
    [JsonPropertyName("pulsos")]      public long   Pulsos      { get; set; }
    [JsonPropertyName("lastSeenUtc")] public string? LastSeenUtc { get; set; }
}

public sealed class QuantiXNodoLive
{
    [JsonPropertyName("uid")]        public string? Uid      { get; set; }
    [JsonPropertyName("ip")]         public string? Ip       { get; set; }
    [JsonPropertyName("firmware")]   public string? Firmware { get; set; }
    [JsonPropertyName("online")]     public bool   Online    { get; set; }
    [JsonPropertyName("motorsLive")] public List<QuantiXMotorLive>? MotorsLive { get; set; }
}

public sealed class QuantiXLiveSnapshot
{
    [JsonPropertyName("nodos")] public List<QuantiXNodoLive>? Nodos { get; set; }
}

public sealed class QuantiXClient
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

    public QuantiXClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<QuantiXLiveSnapshot?> GetLiveAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/quantix/live", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<QuantiXLiveSnapshot>(json, _jsonOpts);
        }
        catch { return null; }
    }
}
