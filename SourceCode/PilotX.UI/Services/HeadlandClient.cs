// ============================================================================
// HeadlandClient.cs — canal HTTP del panel CABECERA nativo (ex pages/cabecera.html).
//
// Qué quedó NATIVO: la pantalla vigente entera — distancia (con precarga del
// ancho de herramienta), Construir (offset Build Around), Reset al contorno,
// Apagar cabecera y el toggle "Secciones controladas en cabecera". Más el
// /open al abrir y el /close al cerrar, que no son cosmética: el close SUAVIZA
// la cabecera y la PERSISTE en el lote.
//
// Qué NO se porta (y por qué):
//   · el canvas de preview (pan/zoom/pinch/fit) — el mapa GL vivo de atrás ya
//     dibuja lindero y cabecera y se refresca solo con el próximo
//     /api/aog/state (lo dice el propio EngineHeadlandEditService). Portarlo
//     sería un segundo mapa, peor;
//   · el flujo de "editar borde" (slice A/B, curva/recta, cortar/extender/
//     deshacer): la UI lo sacó el 2026-08-05 a pedido del usuario y la página
//     HTML vigente tampoco lo llama. Los endpoints /tap /extend /clip /undo
//     siguen vivos en el back "por si vuelve" — acá no se consumen.
//
// Qué SIGUE en HTML: wwwroot/pages/cabecera.html + js/cabecera.js, intactos,
// para el Hub remoto / celular / Android. cabecera-lineas.html ("Cabecera
// avanzada") es otra pantalla y no la toca este porteo.
//
// Wire: el MISMO /api/headland/* de siempre, snake_case (AgpJson). Cero
// cambios de backend. La geometría (fence/fences/headland/slice/a_point/…) NO
// se deserializa a propósito: el panel no dibuja, y bajarse polígonos de miles
// de puntos por acción es peso muerto. System.Text.Json ignora los campos que
// no están en el DTO, así que el contrato del cable no cambia.
// ============================================================================

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ---- DTOs del cable (snake_case EXPLÍCITO en cada propiedad: el
// PropertyNameCaseInsensitive NO cubre underscores — "tool_width_m" no matchea
// ToolWidthM sin atributo) --------------------------------------------------

/// <summary>Estado del editor: lo devuelven GET /state, POST /open y /cancel-touch.</summary>
public sealed class HeadlandStateDto
{
    [JsonPropertyName("has_field")]             public bool HasField { get; set; }
    [JsonPropertyName("has_boundary")]          public bool HasBoundary { get; set; }
    [JsonPropertyName("is_headland_on")]        public bool IsHeadlandOn { get; set; }
    [JsonPropertyName("is_section_controlled")] public bool IsSectionControlled { get; set; }
    /// <summary>"m" o "ft". El motor headless siempre manda "m"; el path WinForms puede mandar "ft".</summary>
    [JsonPropertyName("units")]                 public string? Units { get; set; }
    /// <summary>SIEMPRE metros: la conversión a display es de la pantalla.</summary>
    [JsonPropertyName("tool_width_m")]          public double? ToolWidthM { get; set; }
    [JsonPropertyName("error")]                 public string? Error { get; set; }
}

/// <summary>Resultado de una acción: lo devuelven /build, /reset y /off.</summary>
public sealed class HeadlandResultDto
{
    [JsonPropertyName("ok")]             public bool Ok { get; set; } = true;
    [JsonPropertyName("is_headland_on")] public bool IsHeadlandOn { get; set; }
    [JsonPropertyName("error")]          public string? Error { get; set; }
}

/// <summary>Respuesta del toggle de secciones. El bool que vale es el DEVUELTO.</summary>
public sealed class HeadlandToggleDto
{
    [JsonPropertyName("ok")]                    public bool Ok { get; set; } = true;
    [JsonPropertyName("is_section_controlled")] public bool IsSectionControlled { get; set; }
}

public sealed class HeadlandClient
{
    // 3 s: el panel prefiere pintar "sin conexión" antes que quedarse esperando
    // con el operario manejando.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _base;

    public HeadlandClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    /// <summary>
    /// Inicia la sesión de edición. Si la cabecera está vacía, el backend la
    /// carga con el contorno. Es lo primero que hace el panel al abrirse.
    /// </summary>
    public Task<HeadlandStateDto?> OpenAsync(CancellationToken ct = default)
        => PostAsync<HeadlandStateDto>("api/headland/open", null, ct);

    public Task<HeadlandStateDto?> GetStateAsync(CancellationToken ct = default)
        => GetAsync<HeadlandStateDto>("api/headland/state", ct);

    /// <summary>
    /// Offset Build Around. OJO: la distancia viaja en UNIDADES DISPLAY (el
    /// backend la multiplica por ftOrMtoM; en el motor headless es 1.0).
    /// </summary>
    public Task<HeadlandResultDto?> BuildAsync(double distanciaDisplay, CancellationToken ct = default)
        => PostAsync<HeadlandResultDto>("api/headland/build", new { distance = distanciaDisplay }, ct);

    /// <summary>Cabecera = copia del contorno (y queda encendida).</summary>
    public Task<HeadlandResultDto?> ResetAsync(CancellationToken ct = default)
        => PostAsync<HeadlandResultDto>("api/headland/reset", null, ct);

    /// <summary>Apaga la cabecera. La geometría queda, apagada.</summary>
    public Task<HeadlandResultDto?> OffAsync(CancellationToken ct = default)
        => PostAsync<HeadlandResultDto>("api/headland/off", null, ct);

    public Task<HeadlandToggleDto?> SetSectionControlledAsync(bool on, CancellationToken ct = default)
        => PostAsync<HeadlandToggleDto>("api/headland/section-controlled", new { on }, ct);

    /// <summary>
    /// Descarta toque/línea de corte. El panel no tiene UI de slice, pero lo
    /// llama después del Reset igual que hacía la página: si quedó una línea
    /// residual de una sesión anterior (o del Hub remoto), se limpia.
    /// </summary>
    public Task<HeadlandStateDto?> CancelTouchAsync(CancellationToken ct = default)
        => PostAsync<HeadlandStateDto>("api/headland/cancel-touch", null, ct);

    /// <summary>
    /// Cierra la sesión: SUAVIZA la cabecera, la PERSISTE en el lote y refresca
    /// los paneles. Reemplaza al navigator.sendBeacon de la página. No es
    /// cosmética: sin este POST la cabecera construida no se guarda suavizada.
    /// </summary>
    public Task CloseAsync(CancellationToken ct = default)
        => PostAsync<HeadlandResultDto>("api/headland/close", null, ct);

    /// <summary>
    /// Teclado nativo de PilotX (TecladoWindow) para el campo Distancia: misma
    /// señal HTTP que mandan las páginas HTML. Catch mudo a propósito — sin
    /// teclado en pantalla el campo sigue editable con teclado físico.
    /// </summary>
    public async Task TecladoAsync(bool abrir)
    {
        try
        {
            using var cont = new StringContent(
                abrir ? "{\"numerico\":true,\"titulo\":\"Distancia de cabecera\"}" : "{}",
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
