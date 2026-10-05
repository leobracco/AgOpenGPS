// ============================================================================
// MapasClient.cs — cliente HTTP del preview de mapas del lote.
//
// Lo consume MapasPanel (port nativo de pages/mapas.html + js/mapas.js).
// Wire IDÉNTICO al de la página — la página HTML sigue viva para el Hub remoto
// y la PWA del celular:
//
//   GET /api/mapas/sesiones                 → { lote, sesiones:[…] }
//   GET /api/mapas/sesion/{ts}/heatmap      → GeoJSON FeatureCollection (Polygon)
//   GET /api/mapas/sesion/{ts}/puntos       → GeoJSON FeatureCollection (Point)
//   GET /api/mapas/boundary                 → GeoJSON FeatureCollection (Polygon)
//   GET /api/mapas/headland                 → GeoJSON FeatureCollection (LineString)
//
// Todo en WGS84 [lon, lat] (orden RFC 7946) y con `bbox` = [minLon, minLat,
// maxLon, maxLat] cuando la capa tiene features.
//
// Trampas cubiertas acá:
//   · fetchJsonOrNull() del JS: 404 (sin lote / sin sesión / sin boundary) NO es
//     un error a mostrar — devuelve null y la capa simplemente no se dibuja.
//   · snake_case del listado (fecha_iso, has_heatmap, heatmap_celdas…): sin
//     [JsonPropertyName] el case-insensitive NO cubre underscores.
//   · Las `properties` del GeoJSON salen del DBF del shapefile y NO están
//     modeladas: se leen con las MISMAS claves y la MISMA semántica de
//     verdad/número que usaba el JS (ver PropsPunto/PropsClase).
//   · BOM de EmbedIO al principio del body.
//   · Sin HttpClient.Timeout: cancelación por CancellationToken.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Una sesión de monitoreo VistaX del lote actual.</summary>
public sealed class MapaSesionWire
{
    [JsonPropertyName("ts")]             public string? Ts            { get; set; }
    [JsonPropertyName("fecha_iso")]      public string? FechaIso      { get; set; }
    [JsonPropertyName("has_heatmap")]    public bool    HasHeatmap    { get; set; }
    [JsonPropertyName("has_puntos")]     public bool    HasPuntos     { get; set; }
    [JsonPropertyName("heatmap_celdas")] public int     HeatmapCeldas { get; set; }
    [JsonPropertyName("puntos")]         public int     Puntos        { get; set; }
}

public sealed class MapaSesionesWire
{
    [JsonPropertyName("lote")]     public string? Lote { get; set; }
    [JsonPropertyName("sesiones")] public List<MapaSesionWire>? Sesiones { get; set; }
}

/// <summary>Un feature ya desarmado para dibujar: nada de JsonElement vivo en el
/// hilo de render.</summary>
public sealed class MapaFeature
{
    /// <summary>"Point" | "LineString" | "Polygon".</summary>
    public string Tipo = "";

    /// <summary>Anillos del polígono ([0] = exterior, [1..] = agujeros) o el
    /// único trazo de la línea. Cada punto es [lon, lat].</summary>
    public List<double[][]>? Trazos;

    /// <summary>[lon, lat] si Tipo == "Point".</summary>
    public double[]? Punto;

    // ---- propiedades que la página leía del DBF ---------------------------

    /// <summary>`properties.clase` pasada por Number(). NaN = no numérica —
    /// cae en el gris del `default` del switch, igual que el JS.</summary>
    public double Clase = double.NaN;

    /// <summary>`properties.alerta` con la verdad de JS (0 y "" son falsos; OJO:
    /// el string "0" es VERDADERO, igual que en el browser).</summary>
    public bool Alerta;

    /// <summary>Texto del tooltip del punto, ya armado como lo armaba el JS:
    /// "surco N · SPM X".</summary>
    public string Tooltip = "";
}

public sealed class MapaCapa
{
    public List<MapaFeature> Features = new();
    /// <summary>[minLon, minLat, maxLon, maxLat] o null.</summary>
    public double[]? Bbox;
}

public sealed class MapasClient
{
    // Sin Timeout: la cancelación va SIEMPRE por CancellationToken.
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Un heatmap de una jornada entera puede tener miles de celdas.</summary>
    private static readonly TimeSpan Corte = TimeSpan.FromSeconds(20);

    private readonly string _baseUrl;

    public MapasClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public string BaseUrl => _baseUrl;

    // --------------------------------------------------------------- sesiones

    /// <summary>GET /api/mapas/sesiones. null = 404 o falló la red (el JS
    /// pintaba "Lote · –" y "— sin sesiones —").</summary>
    public async Task<MapaSesionesWire?> GetSesionesAsync(CancellationToken ct = default)
    {
        string? json = await LeerAsync("api/mapas/sesiones", ct).ConfigureAwait(false);
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<MapaSesionesWire>(json, _jsonOpts); }
        catch { return null; }
    }

    // ----------------------------------------------------------------- capas

    public Task<MapaCapa?> GetHeatmapAsync(string ts, CancellationToken ct = default)
        => GetCapaAsync("api/mapas/sesion/" + Uri.EscapeDataString(ts ?? "") + "/heatmap", ct);

    public Task<MapaCapa?> GetPuntosAsync(string ts, CancellationToken ct = default)
        => GetCapaAsync("api/mapas/sesion/" + Uri.EscapeDataString(ts ?? "") + "/puntos", ct);

    public Task<MapaCapa?> GetBoundaryAsync(CancellationToken ct = default)
        => GetCapaAsync("api/mapas/boundary", ct);

    public Task<MapaCapa?> GetHeadlandAsync(CancellationToken ct = default)
        => GetCapaAsync("api/mapas/headland", ct);

    /// <summary>Descarga y desarma una capa GeoJSON. null cuando el JS también
    /// se quedaba sin capa: red caída, 404, body ilegible o `features` vacío.</summary>
    private async Task<MapaCapa?> GetCapaAsync(string ruta, CancellationToken ct)
    {
        string? json = await LeerAsync(ruta, ct).ConfigureAwait(false);
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) return null;
            if (!raiz.TryGetProperty("features", out var feats) || feats.ValueKind != JsonValueKind.Array)
                return null;
            // `if (!data || !data.features || data.features.length === 0) return null;`
            if (feats.GetArrayLength() == 0) return null;

            var capa = new MapaCapa();
            foreach (var f in feats.EnumerateArray())
            {
                var mf = LeerFeature(f);
                if (mf != null) capa.Features.Add(mf);
            }
            if (raiz.TryGetProperty("bbox", out var bb) && bb.ValueKind == JsonValueKind.Array
                && bb.GetArrayLength() >= 4)
            {
                var v = new double[4];
                int i = 0;
                foreach (var n in bb.EnumerateArray())
                {
                    if (i >= 4) break;
                    v[i++] = n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0;
                }
                capa.Bbox = v;
            }
            return capa;
        }
        catch { return null; }
    }

    private static MapaFeature? LeerFeature(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object) return null;
        if (!f.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object) return null;
        if (!g.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
        if (!g.TryGetProperty("coordinates", out var c)) return null;

        string tipo = t.GetString() ?? "";
        var mf = new MapaFeature { Tipo = tipo };

        switch (tipo)
        {
            case "Point":
                mf.Punto = LeerPunto(c);
                if (mf.Punto == null) return null;
                break;

            case "LineString":
                {
                    var pts = LeerLinea(c);
                    if (pts == null) return null;
                    mf.Trazos = new List<double[][]> { pts };
                    break;
                }

            case "MultiLineString":
            case "Polygon":
                {
                    var trazos = new List<double[][]>();
                    if (c.ValueKind != JsonValueKind.Array) return null;
                    foreach (var anillo in c.EnumerateArray())
                    {
                        var pts = LeerLinea(anillo);
                        if (pts != null) trazos.Add(pts);
                    }
                    if (trazos.Count == 0) return null;
                    mf.Trazos = trazos;
                    // MultiLineString se dibuja como líneas, Polygon como relleno:
                    // el tipo queda tal cual vino.
                    break;
                }

            case "MultiPolygon":
                {
                    var trazos = new List<double[][]>();
                    if (c.ValueKind != JsonValueKind.Array) return null;
                    foreach (var poly in c.EnumerateArray())
                    {
                        if (poly.ValueKind != JsonValueKind.Array) continue;
                        foreach (var anillo in poly.EnumerateArray())
                        {
                            var pts = LeerLinea(anillo);
                            if (pts != null) trazos.Add(pts);
                        }
                    }
                    if (trazos.Count == 0) return null;
                    mf.Trazos = trazos;
                    mf.Tipo = "Polygon";
                    break;
                }

            default:
                return null;
        }

        // ---- properties (mismas claves y misma semántica que el JS) --------
        JsonElement props = default;
        bool hayProps = f.TryGetProperty("properties", out props)
                        && props.ValueKind == JsonValueKind.Object;

        // styleHeatmap: `(feature.properties && feature.properties.clase) || 0`
        // y después Number(clase). El `|| 0` convierte "" y 0 en 0, que cae en
        // el default gris igual que un valor ausente.
        if (hayProps && props.TryGetProperty("clase", out var clase))
            mf.Clase = ANumero(clase);

        if (tipo == "Point")
        {
            JsonElement alerta = default, spm = default, surco = default;
            bool hayAlerta = hayProps && props.TryGetProperty("alerta", out alerta);
            bool haySpm    = hayProps && props.TryGetProperty("spm", out spm);
            bool haySurco  = hayProps && props.TryGetProperty("surco", out surco);

            mf.Alerta = hayAlerta && EsVerdadJs(alerta);

            // 'surco ' + (properties.surco || '–') + ' · SPM ' +
            // (spm != null ? Number(spm).toFixed(0) : '–')
            string surcoTxt = haySurco && EsVerdadJs(surco) ? ATexto(surco) : "–";
            string spmTxt = "–";
            if (haySpm && spm.ValueKind != JsonValueKind.Null && spm.ValueKind != JsonValueKind.Undefined)
                spmTxt = ToFixed(ANumero(spm), 0);
            mf.Tooltip = "surco " + surcoTxt + " · SPM " + spmTxt;
        }

        return mf;
    }

    private static double[]? LeerPunto(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2) return null;
        double lon = 0, lat = 0;
        int i = 0;
        foreach (var n in c.EnumerateArray())
        {
            double v = n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0;
            if (i == 0) lon = v; else if (i == 1) lat = v;
            i++;
            if (i >= 2) break;
        }
        return new[] { lon, lat };
    }

    private static double[][]? LeerLinea(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Array) return null;
        var lista = new List<double[]>(c.GetArrayLength());
        foreach (var p in c.EnumerateArray())
        {
            var pt = LeerPunto(p);
            if (pt != null) lista.Add(pt);
        }
        return lista.Count == 0 ? null : lista.ToArray();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>fetchJsonOrNull() del JS: cualquier no-200 (típico 404 "no hay
    /// lote"/"no-boundary") o excepción devuelve null, sin mostrar error.</summary>
    private async Task<string?> LeerAsync(string ruta, CancellationToken ct)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            // cache:'no-store' del fetch: el HttpClient de .NET no cachea.
            using var resp = await _http.GetAsync(_baseUrl + ruta, corte.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            return Limpio(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch { return null; }
    }

    /// <summary>Verdad de JavaScript: false para null/undefined/false/0/NaN/"".
    /// El string "0" es VERDADERO — se replica tal cual (en el browser un
    /// atributo alerta="0" del DBF también pintaba el punto en rojo).</summary>
    private static bool EsVerdadJs(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.True:   return true;
            case JsonValueKind.False:  return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: return false;
            case JsonValueKind.Number:
                {
                    double v = e.GetDouble();
                    return v != 0 && !double.IsNaN(v);
                }
            case JsonValueKind.String: return (e.GetString() ?? "").Length > 0;
            default: return true;   // objetos y arrays son truthy en JS
        }
    }

    /// <summary>Number(x) de JS: número tal cual, string parseado (vacío = 0,
    /// basura = NaN), true = 1, false = 0.</summary>
    private static double ANumero(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number: return e.GetDouble();
            case JsonValueKind.True:   return 1;
            case JsonValueKind.False:  return 0;
            case JsonValueKind.Null:   return 0;
            case JsonValueKind.String:
                {
                    string s = (e.GetString() ?? "").Trim();
                    if (s.Length == 0) return 0;
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                        ? v : double.NaN;
                }
            default: return double.NaN;
        }
    }

    /// <summary>String(x) para el tooltip: los números del DBF salen sin ceros
    /// de más, igual que la concatenación de JS.</summary>
    private static string ATexto(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return e.GetString() ?? "";
            case JsonValueKind.Number:
                {
                    double v = e.GetDouble();
                    return v.ToString("R", CultureInfo.InvariantCulture);
                }
            case JsonValueKind.True:  return "true";
            case JsonValueKind.False: return "false";
            default: return e.ToString();
        }
    }

    /// <summary>toFixed(n) de JS: redondea al MÁS GRANDE cuando hay empate
    /// (0.5 → "1"), a diferencia del "F0"/Math.Round de .NET, que redondea al
    /// par (0.5 → "0"). El formato custom con InvariantCulture asegura el punto
    /// decimal.</summary>
    private static string ToFixed(double valor, int decimales)
    {
        if (double.IsNaN(valor)) return "NaN";
        if (double.IsInfinity(valor)) return valor > 0 ? "Infinity" : "-Infinity";
        string formato = decimales <= 0 ? "0" : "0." + new string('0', decimales);
        double escala = Math.Pow(10, decimales);
        double ajustado = valor < 0
            ? -Math.Floor(-valor * escala + 0.5) / escala
            :  Math.Floor( valor * escala + 0.5) / escala;
        return ajustado.ToString(formato, CultureInfo.InvariantCulture);
    }

    // El BOM de EmbedIO deja el JSON arrancando con U+FEFF y System.Text.Json no
    // lo perdona. Los caracteres van por código a propósito: como literales
    // serían invisibles en el fuente.
    private static string Limpio(string json)
        => (json ?? "").TrimStart((char)0xFEFF, (char)0x200B).TrimStart();
}
