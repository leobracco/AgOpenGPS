// FlagsClient.cs
//
// Cliente HTTP del endpoint /api/flags/state. Trae las banderas del operario
// (piedra, pozo, alambrado caído...) para que el mapa las dibuje junto al
// resto de la geometría (lindero, guías, tram). El widget banderas.html ya
// las crea/edita contra el motor (EngineFlagsService); esto solo las lee
// para el mapa nativo — sin esto el mapa nunca mostraba lo que se cargaba
// desde esa pantalla.
//
// Forma del payload (FlagsStateDto, snake_case por AgpJson):
//   { ok: true, flags: [ { number, id, color, notes, lat, lon,
//                          distance_m, easting, northing }, ... ] }

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class FlagPoint
{
    public int Number { get; set; }
    public int Id { get; set; }

    /// <summary>0 = rojo, 1 = verde, 2 = amarillo (mismo código que banderas.html).</summary>
    public int Color { get; set; }
    public string? Notes { get; set; }
    public double Easting { get; set; }
    public double Northing { get; set; }
}

internal sealed class FlagsStateResponse
{
    public bool Ok { get; set; }
    public List<FlagPoint>? Flags { get; set; }
}

public sealed class FlagsClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _baseUrl;

    public FlagsClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Trae la lista de banderas. null en error — el caller debe tolerarlo.</summary>
    public async Task<List<FlagPoint>?> GetFlagsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/flags/state", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<FlagsStateResponse>(json, _jsonOpts);
            if (dto == null || !dto.Ok) return null;
            return dto.Flags ?? new List<FlagPoint>();
        }
        catch { return null; }
    }
}
