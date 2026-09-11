// GraficosClient.cs
//
// Cliente HTTP de las CUATRO pantallas de gráficos nativas. Mismos endpoints,
// mismo verbo y mismos campos snake_case que usaban los JS del Hub:
//
//   GET /api/aog/graph-steer      -> { actual_steer_deg, set_steer_deg }
//   GET /api/aog/graph-heading    -> { gps_heading_deg, imu_heading_deg }
//   GET /api/aog/graph-xte        -> { heading_error_deg, xte_cm }
//   GET /api/aog/graph-correction -> { correction_distance, easting,
//                                      uncorrected_easting, roll_degrees,
//                                      roll_present }
//
// Los cuatro los sirve AogStateController (AgroParallel.WebHost) desde
// IAogStateProvider. Acá NO se toca nada del contrato: el panel es un
// renderer, igual que la página.
//
// Devuelve null cuando el motor no contesta o contesta != 2xx — el panel pinta
// "sin conexión", que es exactamente el catch del fetch en el JS.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Muestra del gráfico de dirección (espejo de AgroParallel.Models
/// SteerGraphSample; el wire va en snake_case vía AgpJson).</summary>
public sealed class SteerGraphSampleDto
{
    /// <summary>Ángulo de dirección real medido (WAS), en grados.</summary>
    [JsonPropertyName("actual_steer_deg")] public double? ActualSteerDeg { get; set; }

    /// <summary>Ángulo de dirección que ordena el guiado, en grados.</summary>
    [JsonPropertyName("set_steer_deg")]    public double? SetSteerDeg    { get; set; }
}

/// <summary>Muestra del gráfico de rumbo (espejo de HeadingGraphSample).</summary>
public sealed class HeadingGraphSampleDto
{
    /// <summary>Rumbo GPS actual, en grados.</summary>
    [JsonPropertyName("gps_heading_deg")] public double? GpsHeadingDeg { get; set; }

    /// <summary>Rumbo del IMU corregido (fusión), en grados.</summary>
    [JsonPropertyName("imu_heading_deg")] public double? ImuHeadingDeg { get; set; }
}

/// <summary>Muestra del gráfico XTE (espejo de XteGraphSample).</summary>
public sealed class XteGraphSampleDto
{
    /// <summary>Error de rumbo actual del modo de guiado, en grados.</summary>
    [JsonPropertyName("heading_error_deg")] public double? HeadingErrorDeg { get; set; }

    /// <summary>Error de seguimiento (cross-track) actual, en centímetros.</summary>
    [JsonPropertyName("xte_cm")]            public double? XteCm           { get; set; }
}

/// <summary>Muestra del chequeo de roll (espejo de CorrectionGraphSample).</summary>
public sealed class CorrectionGraphSampleDto
{
    /// <summary>Distancia de corrección aplicada por el roll del IMU, en metros.</summary>
    [JsonPropertyName("correction_distance")] public double? CorrectionDistance { get; set; }

    /// <summary>Easting actual del fix GPS (ya corregido), en metros.</summary>
    [JsonPropertyName("easting")]             public double? Easting            { get; set; }

    /// <summary>Easting del fix GPS sin corregir por roll, en metros.</summary>
    [JsonPropertyName("uncorrected_easting")] public double? UncorrectedEasting { get; set; }

    /// <summary>Roll del IMU en grados. Sólo válido si RollPresent es true.</summary>
    [JsonPropertyName("roll_degrees")]        public double? RollDegrees        { get; set; }

    /// <summary>true si hay un IMU presente reportando roll.</summary>
    [JsonPropertyName("roll_present")]        public bool    RollPresent        { get; set; }
}

public sealed class GraficosClient
{
    // Timeout de TRANSPORTE, no de cancelación: el que corta cuando el operario
    // cierra el panel es el CancellationToken. Ojo con la trampa conocida — el
    // timeout de HttpClient tira TaskCanceledException (que ES una
    // OperationCanceledException); por eso el filtro de abajo mira
    // ct.IsCancellationRequested, para no confundir "cerré el panel" con "el
    // motor no contesta". Sin ese `when`, la UI se congela en sus defaults.
    //
    // 2 s (y no 5) porque el muestreo es a 5 Hz: una request colgada más de dos
    // segundos ya no sirve, la muestra que traería está vieja.
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public GraficosClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>GET /api/aog/graph-steer — dirección real (WAS) vs seteada.</summary>
    public Task<SteerGraphSampleDto?> GetSteerAsync(CancellationToken ct = default)
        => LeerAsync<SteerGraphSampleDto>("api/aog/graph-steer", ct);

    /// <summary>GET /api/aog/graph-heading — rumbo GPS vs IMU corregido.</summary>
    public Task<HeadingGraphSampleDto?> GetHeadingAsync(CancellationToken ct = default)
        => LeerAsync<HeadingGraphSampleDto>("api/aog/graph-heading", ct);

    /// <summary>GET /api/aog/graph-xte — error de rumbo + error de seguimiento.</summary>
    public Task<XteGraphSampleDto?> GetXteAsync(CancellationToken ct = default)
        => LeerAsync<XteGraphSampleDto>("api/aog/graph-xte", ct);

    /// <summary>GET /api/aog/graph-correction — corrección por roll + eastings.</summary>
    public Task<CorrectionGraphSampleDto?> GetCorrectionAsync(CancellationToken ct = default)
        => LeerAsync<CorrectionGraphSampleDto>("api/aog/graph-correction", ct);

    private async Task<T?> LeerAsync<T>(string ruta, CancellationToken ct) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + ruta);
            // Equivalente a fetch(..., { cache: 'no-store' }): son muestras en
            // vivo, una respuesta cacheada sería mentira.
            req.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                        .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // El host llega a mandar BOM en algunas respuestas (ya mordió en
            // /api/corex/gps): sin el TrimStart, Deserialize tira y el panel se
            // ve "sin conexión" con el motor vivo.
            return JsonSerializer.Deserialize<T>(json.TrimStart('﻿'), _jsonOpts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cierre del panel: no es un error, no hay nada que pintar.
            return null;
        }
        catch { return null; }
    }
}
