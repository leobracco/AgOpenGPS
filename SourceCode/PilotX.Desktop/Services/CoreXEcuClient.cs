// CoreXEcuClient.cs
//
// Cliente HTTP minimo para el CoreXEcuController:
//   GET /api/corex-ecu/status ->
//     {
//       ok, errorCode?, error?, errorTechnical?,
//       firmware?, version?, ip?, ethernet, uptimeSec,
//       imu:  { present, mode, yawDeg, rollDeg, pitchDeg, yawRateDps },
//       was:  { source, angleDeg, zeroDone, encoderRaw, zeroTicks,
//               ticksPerDeg, adsPresent, adsRaw },
//       gps:  { speedKmh, speedKnots, headingDeg, ggaSeen },
//       can:  { keyaSteerEnabled, keyaCurrentA },
//       autosteer: { running, guidanceActive, watchdog, pwm, setpointDeg }
//     }
//
// Reemplaza al pollStatus() de corex-ecu.js — pero SOLO para la tab Live, que
// es la unica cabin-critical. Las tabs Estado (checklist), Calibracion (motor
// manual + barrido PWM Keya) y Conexion (config con Teensy) siguen en HTML —
// son flujos que se hacen parados (galpon, calibracion inicial), no en cabina.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class CoreXEcuImu
{
    [JsonPropertyName("present")]    public bool    Present    { get; set; }
    [JsonPropertyName("mode")]       public string? Mode       { get; set; }
    [JsonPropertyName("yawDeg")]     public double? YawDeg     { get; set; }
    [JsonPropertyName("rollDeg")]    public double? RollDeg    { get; set; }
    [JsonPropertyName("pitchDeg")]   public double? PitchDeg   { get; set; }
    [JsonPropertyName("yawRateDps")] public double? YawRateDps { get; set; }
}

public sealed class CoreXEcuWas
{
    [JsonPropertyName("source")]      public string? Source      { get; set; }
    [JsonPropertyName("angleDeg")]    public double? AngleDeg    { get; set; }
    [JsonPropertyName("zeroDone")]    public bool    ZeroDone    { get; set; }
    [JsonPropertyName("encoderRaw")]  public long?   EncoderRaw  { get; set; }
    [JsonPropertyName("zeroTicks")]   public long?   ZeroTicks   { get; set; }
    [JsonPropertyName("ticksPerDeg")] public double? TicksPerDeg { get; set; }
    [JsonPropertyName("adsPresent")]  public bool    AdsPresent  { get; set; }
    [JsonPropertyName("adsRaw")]      public long?   AdsRaw      { get; set; }
}

public sealed class CoreXEcuGps
{
    [JsonPropertyName("speedKmh")]   public double? SpeedKmh   { get; set; }
    [JsonPropertyName("speedKnots")] public double? SpeedKnots { get; set; }
    [JsonPropertyName("headingDeg")] public double? HeadingDeg { get; set; }
    [JsonPropertyName("ggaSeen")]    public bool    GgaSeen    { get; set; }
}

public sealed class CoreXEcuCan
{
    [JsonPropertyName("keyaSteerEnabled")] public bool    KeyaSteerEnabled { get; set; }
    [JsonPropertyName("keyaCurrentA")]     public double? KeyaCurrentA    { get; set; }
}

public sealed class CoreXEcuAutosteer
{
    [JsonPropertyName("running")]        public bool    Running        { get; set; }
    [JsonPropertyName("guidanceActive")] public bool    GuidanceActive { get; set; }
    [JsonPropertyName("watchdog")]       public int?    Watchdog       { get; set; }
    [JsonPropertyName("pwm")]            public int?    Pwm            { get; set; }
    [JsonPropertyName("setpointDeg")]    public double? SetpointDeg    { get; set; }
}

public sealed class CoreXEcuStatus
{
    [JsonPropertyName("ok")]             public bool    Ok             { get; set; }
    [JsonPropertyName("errorCode")]      public string? ErrorCode      { get; set; }
    [JsonPropertyName("error")]          public string? Error          { get; set; }
    [JsonPropertyName("errorTechnical")] public string? ErrorTechnical { get; set; }
    [JsonPropertyName("firmware")]       public string? Firmware       { get; set; }
    [JsonPropertyName("version")]        public string? Version        { get; set; }
    [JsonPropertyName("ip")]             public string? Ip             { get; set; }
    [JsonPropertyName("ethernet")]       public bool    Ethernet       { get; set; }
    [JsonPropertyName("uptimeSec")]      public long?   UptimeSec      { get; set; }
    [JsonPropertyName("imu")]            public CoreXEcuImu?       Imu       { get; set; }
    [JsonPropertyName("was")]            public CoreXEcuWas?       Was       { get; set; }
    [JsonPropertyName("gps")]            public CoreXEcuGps?       Gps       { get; set; }
    [JsonPropertyName("can")]            public CoreXEcuCan?       Can       { get; set; }
    [JsonPropertyName("autosteer")]      public CoreXEcuAutosteer? Autosteer { get; set; }
}

public sealed class CoreXEcuClient
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

    public CoreXEcuClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<CoreXEcuStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/corex-ecu/status", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CoreXEcuStatus>(json, _jsonOpts);
        }
        catch { return null; }
    }
}
