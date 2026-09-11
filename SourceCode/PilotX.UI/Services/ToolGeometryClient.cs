// ToolGeometryClient.cs
//
// Cliente HTTP del endpoint /api/aog/tool/geometry. Stage 4a de la
// migracion OpenGL del mapa de guiado: trae la barra del implemento
// (N secciones) con sus puntos en coords mundo + estado vivo desde
// el AgpWebHost (que lo extrae de FormGPS via IToolGeometryCalculator).
//
// Forma del payload:
//   { ok: true, snapshot: {
//       numSections: N,
//       isValid: true,
//       sections: [
//         { index, leftE, leftN, rightE, rightN, isOn, isMapping, btnState },
//         ...
//       ] } }
//
// Cadencia: 4 Hz (igual que el HUD). NO usa revision-cache porque los
// puntos cambian cada frame que el tractor se mueve. El payload es muy
// chico (~16 secciones × 6 doubles = ~1KB JSON) asi que el costo de
// re-upload en cada poll es trivial.

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
// snake_case del servidor (left_e, is_on, btn_state, num_sections...).
public sealed class ToolSectionGeometry
{
    public int    Index     { get; set; }
    public double LeftE     { get; set; }
    public double LeftN     { get; set; }
    public double RightE    { get; set; }
    public double RightN    { get; set; }
    public bool   IsOn      { get; set; }
    public bool   IsMapping { get; set; }
    /// <summary>0=Off, 1=Auto, 2=On (manual).</summary>
    public int    BtnState  { get; set; }
    /// <summary>Tren de siembra (1 = delantero). Las secciones de trenes
    /// traseros llegan con coords y estado YA retrasados a su posición física:
    /// el mapa las dibuja tal cual y quedan las dos barras.</summary>
    public int    TrenId    { get; set; } = 1;
}

public sealed class ToolGeometrySnapshot
{
    public int                          NumSections { get; set; }
    public bool                         IsValid     { get; set; }
    public List<ToolSectionGeometry>?   Sections    { get; set; }
}

public sealed class ToolGeometryResponse
{
    public bool                   Ok       { get; set; }
    public ToolGeometrySnapshot?  Snapshot { get; set; }
    public string?                Error    { get; set; }
}

// ----- Cliente HTTP ------------------------------------------------------

public sealed class ToolGeometryClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        // Payload <1 KB en cualquier configuracion realista. 2s alcanza.
        Timeout = TimeSpan.FromSeconds(2)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        // Snake_case: misma politica que el servidor (AgpJson). Ver HudPoller.
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _baseUrl;

    public ToolGeometryClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Trae el snapshot. null en error — el caller debe tolerarlo.</summary>
    public async Task<ToolGeometrySnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/tool/geometry", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<ToolGeometryResponse>(json, _jsonOpts);
            if (dto == null || !dto.Ok) return null;
            return dto.Snapshot;
        }
        catch { return null; }
    }
}
