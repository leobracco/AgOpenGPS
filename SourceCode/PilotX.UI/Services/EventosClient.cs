// EventosClient.cs
//
// Cliente HTTP minimo para el visor de eventos:
//   GET /api/aog/eventos -> { file, history, session }   (wire snake_case)
//
// Reemplaza al js/eventos.js del Hub (pages/eventos.html). Mismo endpoint,
// mismo verbo, mismos campos: el panel nativo es un renderer sin logica
// propia, igual que la pagina.
//
// Cuando el AgpWebHost desaparezca (objetivo del pivot) esto se sustituye por
// una llamada directa a IAogStateProvider.GetEventLog() en el mismo proceso —
// la forma del DTO ya es la de EventLogSnapshot para que sea un swap mecanico.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Snapshot del registro de eventos (espejo de AgroParallel.Models
/// EventLogSnapshot; el wire va en snake_case via AgpJson).</summary>
public sealed class EventLogSnapshotDto
{
    /// <summary>Ruta del archivo de log en disco (informativa, va al pie).</summary>
    [JsonPropertyName("file")]    public string? File    { get; set; }

    /// <summary>Cola del log historico persistido (ultimos KB del archivo).</summary>
    [JsonPropertyName("history")] public string? History { get; set; }

    /// <summary>Eventos acumulados en la sesion actual (Log.sbEvents).</summary>
    [JsonPropertyName("session")] public string? Session { get; set; }
}

public sealed class EventosClient
{
    // Timeout de transporte, NO de cancelacion: el CancellationToken es el que
    // corta cuando el operario cierra el panel. Ojo con la trampa conocida —
    // el timeout de HttpClient tira TaskCanceledException (que ES una
    // OperationCanceledException), asi que el filtro de abajo mira
    // ct.IsCancellationRequested para no confundir "se cerro el panel" con
    // "el host no contesta".
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public EventosClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>
    /// Trae el registro de eventos. Devuelve null si el endpoint no responde o
    /// contesta != 2xx — el panel pinta "No se pudo cargar el registro de
    /// eventos.", igual que el catch del fetch en eventos.js.
    /// </summary>
    public async Task<EventLogSnapshotDto?> GetEventLogAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "api/aog/eventos");
            // Equivalente a fetch(..., { cache: 'no-store' }): el log cambia a
            // cada rato, una respuesta cacheada seria mentira.
            req.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                        .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EventLogSnapshotDto>(json, _jsonOpts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cierre del panel: no es un error, no hay nada que pintar.
            return null;
        }
        catch { return null; }
    }
}
