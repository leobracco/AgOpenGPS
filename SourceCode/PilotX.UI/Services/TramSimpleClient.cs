// ============================================================================
// TramSimpleClient.cs — canal HTTP del panel TRAMLINES nativo (ex
// pages/tramline.html, que a su vez reemplazó al WinForms FormTram).
//
// Qué quedó NATIVO: la pantalla entera — pasadas entre huellas, modo de
// generación, transparencia, invertir A↔B, los tres anchos de solo lectura y
// guardar/descartar. Es una pantalla de LABOR (se usa con el tractor adentro
// del lote) y la vista previa se dibuja SOBRE EL MAPA, así que la ventana de
// Chromium de 350x340 tapaba justo lo único que hay para mirar.
//
// Qué SIGUE en HTML: la página wwwroot/pages/tramline.html entera, intacta,
// para el Hub remoto / celular / Android (MainView.axaml.cs la sigue ruteando).
// El editor multi/por cortes (pages/tramlines.html, comando "tram_multi") es
// OTRA pantalla y sigue en HTML.
//
// Wire: el MISMO /api/tram-simple/* de siempre, snake_case (AgpJson). Cero
// cambios de backend. Todos los POST menos /commit devuelven el estado nuevo —
// se aprovecha para pintar sin pedir otro GET (y sin polling: nadie más muta
// el editor mientras está abierto).
//
// OJO con el GET /state: NO es inocente. Del lado del motor ejecuta Open(),
// que recalcula halfWidth, elige el modo de generación y CONSTRUYE la preview.
// Se pide UNA vez por apertura del panel; nunca en un loop.
// ============================================================================

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ---- DTO del cable (snake_case EXPLÍCITO en cada propiedad: el
// PropertyNameCaseInsensitive NO cubre underscores — "has_track" no matchea
// HasTrack sin atributo) -----------------------------------------------------

public sealed class TramSimpleStateDto
{
    [JsonPropertyName("ok")]                 public bool Ok { get; set; } = true;
    /// <summary>Hay una guía activa (AB o curva). Sin ella no hay nada que construir.</summary>
    [JsonPropertyName("has_track")]          public bool HasTrack { get; set; }
    /// <summary>Hay contorno: sin él los modos de borde no tienen sentido (el back fuerza FillTracks).</summary>
    [JsonPropertyName("has_boundary")]       public bool HasBoundary { get; set; }
    [JsonPropertyName("is_curve")]           public bool IsCurve { get; set; }
    [JsonPropertyName("passes")]             public int Passes { get; set; }
    [JsonPropertyName("alpha_percent")]      public int AlphaPercent { get; set; }
    /// <summary>"All" | "FillTracks" | "BoundaryTracks" | "None".</summary>
    [JsonPropertyName("mode")]               public string? Mode { get; set; }
    [JsonPropertyName("tool_width_display")] public double ToolWidthDisplay { get; set; }
    [JsonPropertyName("tram_width_display")] public double TramWidthDisplay { get; set; }
    [JsonPropertyName("track_width_display")]public double TrackWidthDisplay { get; set; }
    /// <summary>"m" | "ft" (el motor headless devuelve siempre "m").</summary>
    [JsonPropertyName("units")]              public string? Units { get; set; }
    /// <summary>Solo con ok:false: "no-state" | "error-interno" | "service-unavailable" | "bad-json".</summary>
    [JsonPropertyName("error")]              public string? Error { get; set; }
}

public sealed class TramSimpleClient
{
    // 3 s: el panel prefiere pintar "sin conexión" antes que quedarse esperando
    // con el operario manejando.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _base;

    public TramSimpleClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    /// <summary>GET /state — abre el editor en el motor y devuelve el estado.</summary>
    public Task<TramSimpleStateDto?> GetStateAsync(CancellationToken ct = default)
        => GetAsync<TramSimpleStateDto>("api/tram-simple/state", ct);

    /// <summary>POST /passes — el piso 1 lo clampan cliente y back; no hay techo.</summary>
    public Task<TramSimpleStateDto?> SetPassesAsync(int passes, CancellationToken ct = default)
        => PostAsync<TramSimpleStateDto>("api/tram-simple/passes", new { passes }, ct);

    /// <summary>POST /alpha — solo redibuja (no reconstruye geometría).</summary>
    public Task<TramSimpleStateDto?> SetAlphaAsync(int percent, CancellationToken ct = default)
        => PostAsync<TramSimpleStateDto>("api/tram-simple/alpha", new { percent }, ct);

    /// <summary>POST /mode — puede devolver OTRO modo (sin contorno fuerza FillTracks).</summary>
    public Task<TramSimpleStateDto?> SetModeAsync(string mode, CancellationToken ct = default)
        => PostAsync<TramSimpleStateDto>("api/tram-simple/mode", new { mode }, ct);

    /// <summary>
    /// POST /swap — invierte la guía activa. OJO: el back GUARDA LAS GUÍAS A
    /// DISCO en el acto; "Cancelar" descarta el tram, NO deshace el swap
    /// (comportamiento heredado de FormTram, no es un bug del port).
    /// </summary>
    public Task<TramSimpleStateDto?> SwapAsync(CancellationToken ct = default)
        => PostAsync<TramSimpleStateDto>("api/tram-simple/swap", null, ct);

    /// <summary>
    /// POST /commit — save:true deja la geometría y la guarda a disco;
    /// save:false limpia el tram y apaga la preview del mapa. En los dos casos
    /// persiste pasadas/transparencia. Devuelve {"ok":bool} pelado.
    /// </summary>
    public async Task<bool> CommitAsync(bool save, CancellationToken ct = default)
    {
        var r = await PostAsync<CommitRespDto>("api/tram-simple/commit", new { save }, ct).ConfigureAwait(false);
        return r != null && r.Ok;
    }

    private sealed class CommitRespDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
    }

    // ---- plomería ----------------------------------------------------------

    private async Task<T?> GetAsync<T>(string ruta, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await _http.GetAsync(_base + ruta, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Deserializar<T>(json);
        }
        catch { return null; }   // el panel pinta "Sin conexión con PilotX.", jamás explota
    }

    private async Task<T?> PostAsync<T>(string ruta, object? body, CancellationToken ct) where T : class
    {
        try
        {
            using var cont = new StringContent(
                body == null ? "{}" : JsonSerializer.Serialize(body),
                Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_base + ruta, cont, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Deserializar<T>(json);
        }
        catch { return null; }
    }

    // El BOM al principio del cuerpo ya rompió parseos antes (ver la traza del
    // /api/corex/gps): se saca siempre, sale gratis.
    private static T? Deserializar<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json.TrimStart('﻿'), _jsonOpts); }
        catch { return null; }
    }
}
