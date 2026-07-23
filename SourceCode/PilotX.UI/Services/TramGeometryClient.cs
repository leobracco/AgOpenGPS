// TramGeometryClient.cs
//
// Cliente HTTP del endpoint /api/aog/tram. Stage 4b de la migracion OpenGL
// del mapa de guiado: trae las tramlines (wheel tracks generados adentro del
// lote) + outer/inner boundary tracks + displayMode.
//
// Forma del payload:
//   { ok: true, snapshot: {
//       displayMode: "None" | "All" | "FillTracks" | "BoundaryTracks",
//       lines: [ { points: [ { e, n }, ... ] }, ... ],
//       outerBoundary: [ { e, n }, ... ],
//       innerBoundary: [ { e, n }, ... ],
//       revision: <long>
//   } }
//
// Cadencia: 1 Hz (igual que guidance) — solo cambia al regenerar tram.
// Usa revision-cache para que el poller saltee la entrega del snapshot al
// render si la revision no cambio.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ----- DTOs cliente ------------------------------------------------------

// Nombres C# PascalCase; SnakeCaseLower del _jsonOpts los mapea al
// snake_case del servidor (display_mode, outer_boundary...).
public sealed class TramFieldPoint
{
    public double E { get; set; }
    public double N { get; set; }
}

public sealed class TramLineDto
{
    public List<TramFieldPoint>? Points { get; set; }
}

public sealed class TramGeometrySnapshot
{
    public string?              DisplayMode   { get; set; }
    public List<TramLineDto>?   Lines         { get; set; }
    public List<TramFieldPoint>? OuterBoundary { get; set; }
    public List<TramFieldPoint>? InnerBoundary { get; set; }
    public long                 Revision      { get; set; }
}

public sealed class TramGeometryResponse
{
    public bool                   Ok       { get; set; }
    public TramGeometrySnapshot?  Snapshot { get; set; }
    public string?                Error    { get; set; }
}

// ----- Cliente HTTP ------------------------------------------------------

public sealed class TramGeometryClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        // El payload puede crecer si el lote es grande (boundary largo + N
        // tramlines), pero igual queda en pocos KB. 3s alcanza.
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        // Snake_case: misma politica que el servidor (AgpJson). Ver HudPoller.
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _baseUrl;

    public TramGeometryClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Trae el snapshot. null en error — el caller debe tolerarlo.</summary>
    public async Task<TramGeometrySnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/tram", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<TramGeometryResponse>(json, _jsonOpts);
            if (dto == null || !dto.Ok) return null;
            return dto.Snapshot;
        }
        catch { return null; }
    }
}
