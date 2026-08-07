// VistaXClient.cs
//
// Cliente HTTP minimo para el VistaXController:
//   GET /api/vistax/live ->
//     {
//       trenes: [{
//         tren, nombre, objetivo,
//         surcos: [{ tren, bajada, tipo, estado, spm, objetivo,
//                    ratioObjetivo, uid, cable, muted }]
//       }],
//       spmPromedio, surcosActivos, fallasActivas,
//       hasAlarm, alarmMessage,
//       nombreImplemento, toleranciaDesvio, monitoreoActivo,
//       nodos: [{ uid, online, sensorsReporting, lastSeenIso }]
//     }
//
// Reemplaza al pollLive() de vistax.js — pero SOLO para la tab Monitor que es
// cabin-critical. Las otras tabs (Insumo & calibracion, Implemento, Nodos,
// Config) siguen en HTML — son flujos de configuracion/diagnostico que el
// operario no toca manejando, los edita en el galpon.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class VistaXSurcoLive
{
    [JsonPropertyName("tren")]          public int     Tren          { get; set; }
    [JsonPropertyName("bajada")]        public int     Bajada        { get; set; }
    [JsonPropertyName("tipo")]          public string? Tipo          { get; set; }
    [JsonPropertyName("estado")]        public string? Estado        { get; set; }
    [JsonPropertyName("spm")]           public double  Spm           { get; set; }
    [JsonPropertyName("objetivo")]      public double  Objetivo      { get; set; }
    [JsonPropertyName("ratio_objetivo")] public double  RatioObjetivo { get; set; }
    [JsonPropertyName("uid")]           public string? Uid           { get; set; }
    [JsonPropertyName("cable")]         public int     Cable         { get; set; }
    [JsonPropertyName("muted")]         public bool    Muted         { get; set; }
}

public sealed class VistaXTrenLive
{
    [JsonPropertyName("tren")]     public int     Tren     { get; set; }
    [JsonPropertyName("nombre")]   public string? Nombre   { get; set; }
    [JsonPropertyName("objetivo")] public double  Objetivo { get; set; }
    [JsonPropertyName("surcos")]   public List<VistaXSurcoLive>? Surcos { get; set; }
}

public sealed class VistaXNodoLive
{
    [JsonPropertyName("uid")]              public string? Uid              { get; set; }
    [JsonPropertyName("online")]           public bool    Online           { get; set; }
    [JsonPropertyName("sensors_reporting")] public int     SensorsReporting { get; set; }
    [JsonPropertyName("last_seen_iso")]      public string? LastSeenIso      { get; set; }
}

public sealed class VistaXLiveSnapshot
{
    [JsonPropertyName("trenes")]           public List<VistaXTrenLive>? Trenes       { get; set; }
    [JsonPropertyName("spm_promedio")]      public double? SpmPromedio                { get; set; }
    [JsonPropertyName("surcos_activos")]    public int     SurcosActivos              { get; set; }
    [JsonPropertyName("fallas_activas")]    public int     FallasActivas              { get; set; }
    [JsonPropertyName("has_alarm")]         public bool    HasAlarm                   { get; set; }
    [JsonPropertyName("alarm_message")]     public string? AlarmMessage               { get; set; }
    [JsonPropertyName("nombre_implemento")] public string? NombreImplemento           { get; set; }
    [JsonPropertyName("tolerancia_desvio")] public double? ToleranciaDesvio           { get; set; }
    [JsonPropertyName("monitoreo_activo")]  public bool    MonitoreoActivo            { get; set; }
    /// <summary>km/h — necesaria para pasar de sem/min (lo que entrega el
    /// backend) a SEM/M, que es como se piensa la siembra.</summary>
    [JsonPropertyName("velocidad")]         public double  Velocidad                  { get; set; }
    [JsonPropertyName("nodos")]            public List<VistaXNodoLive>? Nodos        { get; set; }
}

public sealed class VistaXClient
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

    public VistaXClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<VistaXLiveSnapshot?> GetLiveAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/vistax/live", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<VistaXLiveSnapshot>(json, _jsonOpts);
        }
        catch { return null; }
    }
}
