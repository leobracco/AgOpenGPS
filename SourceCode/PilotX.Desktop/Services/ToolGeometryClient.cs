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

public sealed class ToolSectionGeometry
{
    [JsonPropertyName("index")]     public int    Index     { get; set; }
    [JsonPropertyName("leftE")]     public double LeftE     { get; set; }
    [JsonPropertyName("leftN")]     public double LeftN     { get; set; }
    [JsonPropertyName("rightE")]    public double RightE    { get; set; }
    [JsonPropertyName("rightN")]    public double RightN    { get; set; }
    [JsonPropertyName("isOn")]      public bool   IsOn      { get; set; }
    [JsonPropertyName("isMapping")] public bool   IsMapping { get; set; }
    /// <summary>0=Off, 1=Auto, 2=On (manual).</summary>
    [JsonPropertyName("btnState")]  public int    BtnState  { get; set; }
}

public sealed class ToolGeometrySnapshot
{
    [JsonPropertyName("numSections")] public int                          NumSections { get; set; }
    [JsonPropertyName("isValid")]     public bool                         IsValid     { get; set; }
    [JsonPropertyName("sections")]    public List<ToolSectionGeometry>?   Sections    { get; set; }
}

public sealed class ToolGeometryResponse
{
    [JsonPropertyName("ok")]       public bool                   Ok       { get; set; }
    [JsonPropertyName("snapshot")] public ToolGeometrySnapshot?  Snapshot { get; set; }
    [JsonPropertyName("error")]    public string?                Error    { get; set; }
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
        PropertyNameCaseInsensitive = true
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
