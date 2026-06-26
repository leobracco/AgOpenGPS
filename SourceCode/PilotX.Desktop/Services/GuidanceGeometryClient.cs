// GuidanceGeometryClient.cs
//
// Cliente HTTP del endpoint /api/aog/guidance/geometry. Stage 3 de la
// migracion OpenGL del mapa de guiado: trae la polyline de la linea/curva
// activa (AB/Curve/Contour) desde el AgpWebHost, que la extrae de FormGPS
// via IGuidanceCalculator.GetGeometry().
//
// Forma del payload:
//   { ok: true, snapshot: {
//       mode: "Off" | "AB" | "Curve" | "Contour",
//       points: [ { e: <easting>, n: <northing> }, ... ],
//       revision: 42                  // bump al cambiar modo o cuenta de puntos
//   } }
//
// Cadencia distinta a la del HUD: la geometria solo cambia al redefinir
// la linea — 1 Hz alcanza. El campo `revision` permite al MapGlSurface
// evitar re-upload del VBO cuando no hubo cambio.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ----- DTOs cliente (alineados al snapshot del servidor) ----------------
// Replicados aca para no obligar a referenciar AgroParallel.Models desde
// PilotX.Desktop (misma frontera que CoverageClient).

public sealed class GuidancePoint
{
    [JsonPropertyName("e")] public double E { get; set; }
    [JsonPropertyName("n")] public double N { get; set; }
}

public sealed class GuidanceGeometrySnapshot
{
    [JsonPropertyName("mode")]     public string?              Mode     { get; set; } = "Off";
    [JsonPropertyName("points")]   public List<GuidancePoint>? Points   { get; set; }
    [JsonPropertyName("revision")] public long                 Revision { get; set; }
}

public sealed class GuidanceGeometryResponse
{
    [JsonPropertyName("ok")]       public bool                       Ok       { get; set; }
    [JsonPropertyName("snapshot")] public GuidanceGeometrySnapshot?  Snapshot { get; set; }
    [JsonPropertyName("error")]    public string?                    Error    { get; set; }
}

// ----- Cliente HTTP ------------------------------------------------------

public sealed class GuidanceGeometryClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        // Payload tipico ~unos pocos KB (curvas largas pueden subir, pero
        // muy lejos de coverage). 5s alcanza de sobra.
        Timeout = TimeSpan.FromSeconds(5)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public GuidanceGeometryClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Trae el snapshot. null en error — el caller debe tolerarlo.</summary>
    public async Task<GuidanceGeometrySnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/guidance/geometry", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<GuidanceGeometryResponse>(json, _jsonOpts);
            if (dto == null || !dto.Ok) return null;
            return dto.Snapshot;
        }
        catch { return null; }
    }
}
