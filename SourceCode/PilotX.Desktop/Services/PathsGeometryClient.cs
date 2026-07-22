// PathsGeometryClient.cs
//
// Cliente HTTP del endpoint /api/aog/paths. Stage 5 de la migracion OpenGL
// del mapa de guiado: trae dos polilineas de "caminos":
//   . youTurn  -> el giro (Dubins/pattern) generado en cabecera.
//   . recorded -> el camino grabado manejando (record path).
//
// Forma del payload:
//   { ok: true, snapshot: {
//       youTurn:  [ { e, n }, ... ],
//       recorded: [ { e, n }, ... ],
//       revision: <long>
//   } }
//
// Cadencia: 1 Hz (igual que tram) — solo cambia al generar un giro o
// grabar/cargar un camino. Usa revision-cache para que el poller saltee la
// entrega del snapshot al render si la revision no cambio.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ----- DTOs cliente ------------------------------------------------------

public sealed class PathsFieldPoint
{
    [JsonPropertyName("e")] public double E { get; set; }
    [JsonPropertyName("n")] public double N { get; set; }
}

public sealed class PathsGeometrySnapshot
{
    [JsonPropertyName("youTurn")]  public List<PathsFieldPoint>? YouTurn  { get; set; }
    [JsonPropertyName("recorded")] public List<PathsFieldPoint>? Recorded { get; set; }
    [JsonPropertyName("revision")] public long                   Revision { get; set; }
}

public sealed class PathsGeometryResponse
{
    [JsonPropertyName("ok")]       public bool                    Ok       { get; set; }
    [JsonPropertyName("snapshot")] public PathsGeometrySnapshot?  Snapshot { get; set; }
    [JsonPropertyName("error")]    public string?                 Error    { get; set; }
}

// ----- Cliente HTTP ------------------------------------------------------

public sealed class PathsGeometryClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        // El payload puede crecer con un recorded path largo, pero igual
        // queda en pocos KB. 3s alcanza.
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public PathsGeometryClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Trae el snapshot. null en error — el caller debe tolerarlo.</summary>
    public async Task<PathsGeometrySnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/paths", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<PathsGeometryResponse>(json, _jsonOpts);
            if (dto == null || !dto.Ok) return null;
            return dto.Snapshot;
        }
        catch { return null; }
    }
}
