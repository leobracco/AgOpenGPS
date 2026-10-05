// ============================================================================
// ContornoClient.cs — canal HTTP del panel CONTORNO nativo (ex pages/contorno.html).
//
// Qué quedó NATIVO: todo lo que el operario usa manejando — la lista de
// contornos (área, puntos, "Cruzar" de los internos, borrar) y la grabación
// del lindero manejando (puntos/ha en vivo, pausa, punto manual, deshacer,
// offset/lado/antena/solo-con-secciones, reiniciar, cancelar, terminar).
//
// Qué SIGUE en HTML: la página wwwroot/pages/contorno.html entera, intacta,
// para el Hub remoto / celular / Android (MainView la sigue ruteando). Y los
// endpoints que la UI no consume (delete-all, import-kml, google-earth, mapa,
// from-tracks) quedan vivos en el back: abren ventanas WinForms y contra el
// motor headless devuelven "no-disponible-sin-ui", así que no tienen botón.
//
// Wire: el MISMO /api/contorno/* de siempre, snake_case (AgpJson). Cero
// cambios de backend. Todos los POST devuelven el estado nuevo — se aprovecha
// para pintar sin esperar el próximo tick del poll.
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
// PropertyNameCaseInsensitive NO cubre underscores — "area_ha" no matchea
// AreaHa sin atributo) ------------------------------------------------------

public sealed class ContornoItemDto
{
    [JsonPropertyName("index")]         public int Index { get; set; }
    /// <summary>index 0 = contorno exterior; el resto son internos (exclusiones).</summary>
    [JsonPropertyName("is_outer")]      public bool IsOuter { get; set; }
    [JsonPropertyName("area_ha")]       public double AreaHa { get; set; }
    /// <summary>Solo internos: se puede manejar por encima (el giro no lo esquiva).</summary>
    [JsonPropertyName("is_drive_thru")] public bool IsDriveThru { get; set; }
    [JsonPropertyName("points")]        public int Points { get; set; }
}

public sealed class ContornoEstadoDto
{
    [JsonPropertyName("ok")]          public bool Ok { get; set; } = true;
    [JsonPropertyName("job_started")] public bool JobStarted { get; set; }
    [JsonPropertyName("tool_width")]  public double ToolWidth { get; set; }
    /// <summary>Hay una grabación manejando en curso (isBndBeingMade).</summary>
    [JsonPropertyName("recording")]   public bool Recording { get; set; }
    [JsonPropertyName("boundaries")]  public List<ContornoItemDto>? Boundaries { get; set; }
    [JsonPropertyName("error")]       public string? Error { get; set; }
}

public sealed class ContornoGrabacionDto
{
    [JsonPropertyName("ok")]          public bool Ok { get; set; } = true;
    [JsonPropertyName("active")]      public bool Active { get; set; }
    /// <summary>No se agregan puntos automáticos (isOkToAddPoints == false).</summary>
    [JsonPropertyName("paused")]      public bool Paused { get; set; }
    [JsonPropertyName("points")]      public int Points { get; set; }
    [JsonPropertyName("area_ha")]     public double AreaHa { get; set; }
    [JsonPropertyName("offset_cm")]   public double OffsetCm { get; set; }
    [JsonPropertyName("right_side")]  public bool RightSide { get; set; }
    /// <summary>true = graba en la antena; false = en el implemento.</summary>
    [JsonPropertyName("at_pivot")]    public bool AtPivot { get; set; }
    [JsonPropertyName("section_rec")] public bool SectionRec { get; set; }
    [JsonPropertyName("error")]       public string? Error { get; set; }
}

public sealed class ContornoClient
{
    // 3 s: el panel prefiere pintar "sin conexión" antes que quedarse esperando
    // con el operario manejando.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _base;

    public ContornoClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    public Task<ContornoEstadoDto?> GetEstadoAsync(CancellationToken ct = default)
        => GetAsync<ContornoEstadoDto>("api/contorno/state", ct);

    public Task<ContornoGrabacionDto?> GetGrabacionAsync(CancellationToken ct = default)
        => GetAsync<ContornoGrabacionDto>("api/contorno/record/status", ct);

    /// <summary>POST que devuelve el ESTADO nuevo (drive-thru, delete).</summary>
    public Task<ContornoEstadoDto?> PostEstadoAsync(string ruta, object? body = null, CancellationToken ct = default)
        => PostAsync<ContornoEstadoDto>(ruta, body, ct);

    /// <summary>POST que devuelve la GRABACIÓN nueva (todo /record/*).</summary>
    public Task<ContornoGrabacionDto?> PostGrabacionAsync(string ruta, object? body = null, CancellationToken ct = default)
        => PostAsync<ContornoGrabacionDto>(ruta, body, ct);

    /// <summary>
    /// Teclado nativo de PilotX (TecladoWindow) para el campo Offset: misma
    /// señal HTTP que mandan las páginas HTML. Catch mudo a propósito — sin
    /// teclado en pantalla el campo sigue editable con teclado físico.
    /// </summary>
    public async Task TecladoAsync(bool abrir)
    {
        try
        {
            using var cont = new StringContent(
                abrir ? "{\"numerico\":true,\"titulo\":\"Offset (cm)\"}" : "{}",
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
