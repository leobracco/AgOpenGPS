// ============================================================================
// CabeceraLineasClient.cs — canal HTTP del panel CABECERA POR LÍNEAS nativo
// (ex pages/cabecera-lineas.html, ex FormHeadAche).
//
// Qué quedó NATIVO: la pantalla entera — el lienzo interactivo (contornos,
// líneas construidas, cabecera armada, puntos A/B, pan/zoom/tap) y toda la
// columna de controles (curva/recta, distancia, × ancho, ciclar/borrar línea,
// A±/B±, descartar toque, construir, reiniciar, secciones controladas, apagar).
//
// Qué SIGUE en HTML: pages/cabecera-lineas.html + js/cabecera-lineas.js,
// INTACTOS, para el Hub remoto / celular / Android (MainView los sigue
// ruteando). Esta pantalla se usa MANEJANDO, con el lote abierto: era el
// último diálogo del flujo de labor que despertaba Chromium encima del mapa.
//
// Wire: el MISMO /api/cabecera-lineas/* de siempre, snake_case (AgpJson). Cero
// cambios de backend. Todos los POST de acción devuelven el estado COMPLETO —
// por eso el panel no pollea: la geometría solo cambia cuando el operario toca.
//
// OJO con /close: no es cosmética. Del lado del motor equivale a cerrar el
// form nativo (FileSaveHeadLines + recálculo de isHeadlandOn), así que tiene
// que salir EXACTAMENTE UNA VEZ por sesión — el flag vive en el panel.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ---- DTOs del cable (snake_case EXPLÍCITO en cada propiedad: el
// PropertyNameCaseInsensitive NO cubre underscores — "sel_idx" no matchea
// SelIdx sin atributo) ------------------------------------------------------

public sealed class CabLinTrack
{
    [JsonPropertyName("index")]  public int Index { get; set; }
    [JsonPropertyName("name")]   public string? Name { get; set; }
    /// <summary>"curve" (curva) | "ab" (recta).</summary>
    [JsonPropertyName("mode")]   public string? Mode { get; set; }
    /// <summary>Puntos en E/N metros: cada uno es double[2] {easting, northing}.</summary>
    [JsonPropertyName("points")] public double[][]? Points { get; set; }
}

public sealed class CabLinState
{
    [JsonPropertyName("ok")]                    public bool Ok { get; set; } = true;
    [JsonPropertyName("job_started")]           public bool JobStarted { get; set; }
    [JsonPropertyName("has_boundary")]          public bool HasBoundary { get; set; }
    [JsonPropertyName("units")]                 public string? Units { get; set; }
    /// <summary>Ancho útil del implemento en unidades display (para "× ancho").</summary>
    [JsonPropertyName("tool_width_display")]    public double ToolWidthDisplay { get; set; }
    /// <summary>Anillos del lote: [contorno][punto][e,n]. Puede haber islas.</summary>
    [JsonPropertyName("fences")]                public List<double[][]>? Fences { get; set; }
    /// <summary>Contorno que agarró el primer tap (se pinta distinto).</summary>
    [JsonPropertyName("bnd_select")]            public int BndSelect { get; set; }
    [JsonPropertyName("tracks")]                public List<CabLinTrack>? Tracks { get; set; }
    /// <summary>-1 = ninguna línea seleccionada.</summary>
    [JsonPropertyName("sel_idx")]               public int SelIdx { get; set; } = -1;
    /// <summary>Cabecera ya armada.</summary>
    [JsonPropertyName("hd_line")]               public double[][]? HdLine { get; set; }
    /// <summary>Punto A tocado (null = sin tocar).</summary>
    [JsonPropertyName("a_point")]               public double[]? APoint { get; set; }
    [JsonPropertyName("b_point")]               public double[]? BPoint { get; set; }
    [JsonPropertyName("is_section_controlled")] public bool IsSectionControlled { get; set; }
    /// <summary>sin-lote, sin-contorno, mismo-punto, una-sola-linea, cruces…</summary>
    [JsonPropertyName("error")]                 public string? Error { get; set; }
}

/// <summary>Respuesta corta de /section-controlled: trae el valor EFECTIVO.</summary>
public sealed class CabLinSeccionResp
{
    [JsonPropertyName("ok")]                    public bool Ok { get; set; }
    [JsonPropertyName("is_section_controlled")] public bool IsSectionControlled { get; set; }
}

public sealed class CabeceraLineasClient
{
    // 3 s: el panel prefiere pintar "Sin conexión con PilotX." antes que
    // quedarse esperando con el operario manejando.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _base;

    public CabeceraLineasClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    // ---- estado -------------------------------------------------------------

    /// <summary>GET del estado. Recurso de re-sincronización, NO un loop.</summary>
    public Task<CabLinState?> GetStateAsync(CancellationToken ct = default)
        => GetAsync<CabLinState>("api/cabecera-lineas/state", ct);

    /// <summary>Abre la sesión de edición y devuelve el estado completo.</summary>
    public Task<CabLinState?> OpenAsync(CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/open", null, ct);

    // ---- acciones (todas devuelven el estado completo) ----------------------

    /// <summary>Toque sobre el contorno: el motor decide si es A o si cierra la línea.</summary>
    public Task<CabLinState?> TapAsync(double e, double n, string mode, double distance,
                                       CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/tap",
               new { e, n, mode = mode == "ab" ? "ab" : "curve", distance }, ct);

    public Task<CabLinState?> CancelTouchAsync(CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/cancel-touch", null, ct);

    public Task<CabLinState?> CycleAsync(int dir, CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/cycle", new { dir }, ct);

    public Task<CabLinState?> DeleteTrackAsync(CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/delete-track", null, ct);

    /// <summary>Extiende/acorta una punta hasta que las líneas se crucen.</summary>
    public Task<CabLinState?> ExtendAsync(string end, bool grow, CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/extend",
               new { end = end == "a" ? "a" : "b", grow }, ct);

    public Task<CabLinState?> BuildAsync(CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/build", null, ct);

    public Task<CabLinState?> ResetAsync(CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/reset", null, ct);

    public Task<CabLinState?> OffAsync(CancellationToken ct = default)
        => PostAsync<CabLinState>("api/cabecera-lineas/off", null, ct);

    /// <summary>
    /// Secciones controladas por la cabecera. Devuelve el valor EFECTIVO (el
    /// motor puede rechazar el cambio); null = sin respuesta.
    /// </summary>
    public async Task<bool?> SetSectionControlledAsync(bool on, CancellationToken ct = default)
    {
        var r = await PostAsync<CabLinSeccionResp>(
            "api/cabecera-lineas/section-controlled", new { on }, ct).ConfigureAwait(false);
        return r == null ? (bool?)null : r.IsSectionControlled;
    }

    /// <summary>
    /// Cierre de sesión: del lado del motor guarda las líneas y recalcula
    /// isHeadlandOn. Best-effort, catch mudo — el panel ya se está yendo.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        try
        {
            using var cont = new StringContent("{}", Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_base + "api/cabecera-lineas/close", cont, ct)
                                     .ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>
    /// Teclado nativo de PilotX (TecladoWindow) para el campo Distancia: misma
    /// señal HTTP que mandan las páginas. Catch mudo a propósito — sin teclado
    /// en pantalla el campo sigue editable con teclado físico.
    /// </summary>
    public async Task TecladoAsync(bool abrir)
    {
        try
        {
            using var cont = new StringContent(
                abrir ? "{\"numerico\":true,\"titulo\":\"Distancia hacia adentro\"}" : "{}",
                Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_base + "api/teclado/" + (abrir ? "abrir" : "cerrar"), cont)
                                     .ConfigureAwait(false);
        }
        catch { }
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
        catch { return null; }   // el panel pinta "Sin conexión con PilotX", jamás explota
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
