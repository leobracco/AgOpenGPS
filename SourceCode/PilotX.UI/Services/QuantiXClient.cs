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

/// <summary>Objetivo y techo de UN motor, en las unidades del operario.
/// El panel lo cruza con la telemetria por (uid, indice de motor).</summary>
public sealed class QuantiXMotorRuntimeDto
{
    [JsonPropertyName("nodo_uid")]      public string? NodoUid    { get; set; }
    [JsonPropertyName("motor_index")]   public int    MotorIndex  { get; set; }
    [JsonPropertyName("nombre")]        public string? Nombre     { get; set; }
    [JsonPropertyName("dosis_objetivo")] public double DosisObjetivo { get; set; }
    /// <summary>"kg_ha" o "sem_m" — determina como se rotula la dosis.</summary>
    [JsonPropertyName("unidad_dosis")]  public string? UnidadDosis { get; set; }
    [JsonPropertyName("target_rpm")]    public double TargetRpm   { get; set; }
    [JsonPropertyName("max_rpm")]       public double MaxRpm      { get; set; }
    /// <summary>Techo de dosis a la velocidad actual. -1 = desconocido
    /// (motor sin calibrar o tractor detenido): se muestra como guion.</summary>
    [JsonPropertyName("max_dose_at_current_speed")] public double MaxDoseAtCurrentSpeed { get; set; }
}

public sealed class QuantiXRuntimeSnapshotDto
{
    [JsonPropertyName("motores")]              public List<QuantiXMotorRuntimeDto>? Motores { get; set; }
    [JsonPropertyName("current_speed_kmh")]    public double CurrentSpeedKmh { get; set; }
    [JsonPropertyName("current_tool_width_m")] public double CurrentToolWidthM { get; set; }
}

public sealed class QuantiXRuntimeResponse
{
    [JsonPropertyName("ok")]       public bool Ok { get; set; }
    [JsonPropertyName("snapshot")] public QuantiXRuntimeSnapshotDto? Snapshot { get; set; }
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

    /// <summary>Objetivo de dosis y techo por motor. Va aparte de /live porque
    /// /live es telemetria del firmware y esto es lo que la PC le esta pidiendo:
    /// juntos son "lo que se pidio" contra "lo que esta haciendo".</summary>
    public async Task<QuantiXRuntimeSnapshotDto?> GetRuntimeAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/quantix/runtime", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<QuantiXRuntimeResponse>(json, _jsonOpts)?.Snapshot;
        }
        catch { return null; }
    }
}
