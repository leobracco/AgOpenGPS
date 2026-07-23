// CoreXEcuClient.cs
//
// Cliente HTTP minimo para el CoreXEcuController:
//   GET /api/corex-ecu/status ->
//     {
//       ok, error_code?, error?, error_technical?,
//       firmware?, version?, ip?, ethernet, uptime_sec,
//       imu:  { present, mode, yaw_deg, roll_deg, pitch_deg, yaw_rate_dps },
//       was:  { source, angle_deg, zero_done, encoder_raw, zero_ticks,
//               ticks_per_deg, ads_present, ads_raw },
//       gps:  { speed_kmh, speed_knots, heading_deg, gga_seen },
//       can:  { keya_steer_enabled, keya_current_a },
//       autosteer: { running, guidance_active, watchdog, pwm, setpoint_deg }
//     }
//
// El server emite snake_case vía AgpJson ([JsonPropertyName] del DTO tiene
// precedencia, todos los campos del CoreXEcuStatusDto tienen atributos
// snake_case explícitos). Los [JsonPropertyName] de abajo replican esos nombres.
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
    [JsonPropertyName("present")]       public bool    Present    { get; set; }
    [JsonPropertyName("mode")]          public string? Mode       { get; set; }
    [JsonPropertyName("yaw_deg")]       public double? YawDeg     { get; set; }
    [JsonPropertyName("roll_deg")]      public double? RollDeg    { get; set; }
    [JsonPropertyName("pitch_deg")]     public double? PitchDeg   { get; set; }
    [JsonPropertyName("yaw_rate_dps")]  public double? YawRateDps { get; set; }
}

public sealed class CoreXEcuWas
{
    [JsonPropertyName("source")]        public string? Source      { get; set; }
    [JsonPropertyName("angle_deg")]     public double? AngleDeg    { get; set; }
    [JsonPropertyName("zero_done")]     public bool    ZeroDone    { get; set; }
    [JsonPropertyName("encoder_raw")]   public long?   EncoderRaw  { get; set; }
    [JsonPropertyName("zero_ticks")]    public long?   ZeroTicks   { get; set; }
    [JsonPropertyName("ticks_per_deg")] public double? TicksPerDeg { get; set; }
    [JsonPropertyName("ads_present")]   public bool    AdsPresent  { get; set; }
    [JsonPropertyName("ads_raw")]       public long?   AdsRaw      { get; set; }
}

public sealed class CoreXEcuGps
{
    [JsonPropertyName("speed_kmh")]   public double? SpeedKmh   { get; set; }
    [JsonPropertyName("speed_knots")] public double? SpeedKnots { get; set; }
    [JsonPropertyName("heading_deg")] public double? HeadingDeg { get; set; }
    [JsonPropertyName("gga_seen")]    public bool    GgaSeen    { get; set; }
}

public sealed class CoreXEcuCan
{
    [JsonPropertyName("keya_steer_enabled")] public bool    KeyaSteerEnabled { get; set; }
    [JsonPropertyName("keya_current_a")]     public double? KeyaCurrentA    { get; set; }
}

public sealed class CoreXEcuAutosteer
{
    [JsonPropertyName("running")]         public bool    Running        { get; set; }
    [JsonPropertyName("guidance_active")] public bool    GuidanceActive { get; set; }
    [JsonPropertyName("watchdog")]        public int?    Watchdog       { get; set; }
    [JsonPropertyName("pwm")]             public int?    Pwm            { get; set; }
    [JsonPropertyName("setpoint_deg")]    public double? SetpointDeg    { get; set; }
}

public sealed class CoreXEcuStatus
{
    [JsonPropertyName("ok")]              public bool    Ok             { get; set; }
    [JsonPropertyName("error_code")]      public string? ErrorCode      { get; set; }
    [JsonPropertyName("error")]           public string? Error          { get; set; }
    [JsonPropertyName("error_technical")] public string? ErrorTechnical { get; set; }
    [JsonPropertyName("firmware")]        public string? Firmware       { get; set; }
    [JsonPropertyName("version")]         public string? Version        { get; set; }
    [JsonPropertyName("ip")]              public string? Ip             { get; set; }
    [JsonPropertyName("ethernet")]        public bool    Ethernet       { get; set; }
    [JsonPropertyName("uptime_sec")]      public long?   UptimeSec      { get; set; }
    [JsonPropertyName("imu")]             public CoreXEcuImu?       Imu       { get; set; }
    [JsonPropertyName("was")]             public CoreXEcuWas?       Was       { get; set; }
    [JsonPropertyName("gps")]             public CoreXEcuGps?       Gps       { get; set; }
    [JsonPropertyName("can")]             public CoreXEcuCan?       Can       { get; set; }
    [JsonPropertyName("autosteer")]       public CoreXEcuAutosteer? Autosteer { get; set; }
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
