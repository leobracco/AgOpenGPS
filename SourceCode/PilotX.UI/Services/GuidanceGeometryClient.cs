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

/// <summary>Camino del U-turn (réplica 6.8.5): verde armado, rojo fuera de
/// límites, violeta girando. Se consume en cada poll, fuera del corte por
/// revision (el camino se re-arma con el tractor andando).</summary>
public sealed class YouTurnPath
{
    [JsonPropertyName("points")]        public List<GuidancePoint>? Points { get; set; }
    [JsonPropertyName("phase")]         public int    Phase       { get; set; }
    [JsonPropertyName("triggered")]     public bool   Triggered   { get; set; }
    [JsonPropertyName("out_of_bounds")] public bool   OutOfBounds { get; set; }
    [JsonPropertyName("turn_left")]     public bool   TurnLeft    { get; set; }
    [JsonPropertyName("distance_m")]    public double DistanceM   { get; set; } = -1;
}

public sealed class GuidanceGeometrySnapshot
{
    [JsonPropertyName("mode")]     public string?              Mode     { get; set; } = "Off";
    [JsonPropertyName("points")]   public List<GuidancePoint>? Points   { get; set; }
    [JsonPropertyName("revision")] public long                 Revision { get; set; }
    [JsonPropertyName("you_turn")] public YouTurnPath?         YouTurn  { get; set; }

    // Info de desvío para el lightbar (se completa desde /api/aog/guidance,
    // no viene en /geometry). XteMeters: distancia lateral a la línea (m);
    // signo = lado. NaN si no hay guía activa.
    [JsonIgnore] public double XteMeters { get; set; } = double.NaN;
    [JsonIgnore] public bool   IsLineSet { get; set; }
    // Índice de paralela (howManyPathsAway): 0 inicial, - izq, + der. Viene de
    // /api/aog/guidance (no de /geometry). int.MinValue = sin dato.
    [JsonIgnore] public int    PathsAway { get; set; } = int.MinValue;
}

public sealed class GuidanceGeometryResponse
{
    [JsonPropertyName("ok")]       public bool                       Ok       { get; set; }
    [JsonPropertyName("snapshot")] public GuidanceGeometrySnapshot?  Snapshot { get; set; }
    [JsonPropertyName("error")]    public string?                    Error    { get; set; }
}

// /api/aog/guidance — snapshot de guiado con el XTE.
public sealed class GuidanceInfoSnapshot
{
    [JsonPropertyName("xte_meters")]          public double XteMeters { get; set; }
    [JsonPropertyName("is_line_set")]         public bool   IsLineSet { get; set; }
    [JsonPropertyName("how_many_paths_away")] public int    PathsAway { get; set; }
}

public sealed class GuidanceInfoResponse
{
    [JsonPropertyName("ok")]       public bool                  Ok       { get; set; }
    [JsonPropertyName("snapshot")] public GuidanceInfoSnapshot? Snapshot { get; set; }
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
            if (dto == null || !dto.Ok || dto.Snapshot == null) return null;

            // Completar el XTE desde /api/aog/guidance (para el lightbar). Va en
            // el mismo poll; si falla, se deja NaN y el lightbar no se dibuja.
            try
            {
                using var r2 = await _http.GetAsync(_baseUrl + "api/aog/guidance", ct).ConfigureAwait(false);
                if (r2.IsSuccessStatusCode)
                {
                    var j2 = await r2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var info = JsonSerializer.Deserialize<GuidanceInfoResponse>(j2, _jsonOpts);
                    if (info != null && info.Ok && info.Snapshot != null)
                    {
                        dto.Snapshot.XteMeters = info.Snapshot.IsLineSet ? info.Snapshot.XteMeters : double.NaN;
                        dto.Snapshot.IsLineSet = info.Snapshot.IsLineSet;
                        dto.Snapshot.PathsAway = info.Snapshot.IsLineSet ? info.Snapshot.PathsAway : int.MinValue;
                    }
                }
            }
            catch { /* xte opcional */ }

            return dto.Snapshot;
        }
        catch { return null; }
    }
}
