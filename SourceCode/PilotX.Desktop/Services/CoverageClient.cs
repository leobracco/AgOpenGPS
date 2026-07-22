// CoverageClient.cs
//
// Cliente HTTP del endpoint /api/aog/coverage. Stage 2 de la migracion
// OpenGL del mapa de guiado: trae el worked area triangulado desde el
// AgpWebHost (que a su vez lo extrae de FormGPS.triStrip via
// ICoverageService).
//
// Forma del payload (CoverageSnapshot del servidor):
//   { ok: true, snapshot: {
//       fieldDirectory: "...",
//       revision: 123456,             // incrementa al agregarse cobertura
//       r: 75, g: 166, b: 63, a: 140, // color RGBA 0..255
//       sections: [
//         { index: 0, enabled: true,
//           strips: [ { vertices: [ {e:..,n:..}, ... ] }, ... ] },
//         ...
//       ] } }
//
// Cada `strip` es un OpenGL triangle strip: vertices intercalados que
// forman triangulos adyacentes (NO triangles independientes). Hay que
// renderearlos con GL_TRIANGLE_STRIP por strip.
//
// Performance:
//   - JSON ~3 MB para 8h de jornada (segun comentario del adapter en
//     FormGpsCoverageService). Por eso polling es 1 Hz, no 4 Hz como el
//     HUD, y el client usa el campo `revision` para no re-uploadear el
//     VBO si no cambio nada.
//   - Stage 2b/futuro: agregar /api/aog/coverage?since=<rev> para
//     incremental. Hoy el endpoint solo emite snapshot completo.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ----- DTOs cliente (forma alineada al snapshot del servidor) -----------
// Replicados aca para no obligar a referenciar AgroParallel.Models desde
// PilotX.Desktop. La frontera de proyectos lo agradece.

// Nombres C# PascalCase; SnakeCaseLower del _jsonOpts los mapea al
// snake_case del servidor (field_directory, etc.).
public sealed class CoverageVertex
{
    public double E { get; set; }
    public double N { get; set; }
}

public sealed class CoverageStrip
{
    public List<CoverageVertex>? Vertices { get; set; }
}

public sealed class CoverageSection
{
    public int                   Index   { get; set; }
    public bool                  Enabled { get; set; }
    public List<CoverageStrip>?  Strips  { get; set; }
}

public sealed class CoverageSnapshot
{
    public string?              FieldDirectory { get; set; }
    public long                 Revision       { get; set; }
    public int                  R              { get; set; } = 75;
    public int                  G              { get; set; } = 166;
    public int                  B              { get; set; } = 63;
    public int                  A              { get; set; } = 140;
    public List<CoverageSection>? Sections    { get; set; }
}

public sealed class CoverageResponse
{
    public bool              Ok       { get; set; }
    public CoverageSnapshot? Snapshot { get; set; }
    public string?           Error    { get; set; }
}

// ----- Cliente HTTP ------------------------------------------------------

public sealed class CoverageClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        // Coverage puede tirar 3 MB en steady-state — damos timeout
        // generoso pero no infinito. Si el server tarda mas de 10s,
        // el snapshot llego "stale" igual.
        Timeout = TimeSpan.FromSeconds(10)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        // Snake_case: misma politica que el servidor (AgpJson). Ver HudPoller.
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _baseUrl;

    public CoverageClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Trae el snapshot completo. Devuelve null en error (no tira).</summary>
    public async Task<CoverageSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/coverage", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<CoverageResponse>(json, _jsonOpts);
            if (dto == null || !dto.Ok) return null;
            return dto.Snapshot;
        }
        catch { return null; }
    }
}
