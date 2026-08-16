// ============================================================================
// TramMultiClient.cs — canal HTTP del panel TRAMLINES (multi) nativo, ex
// pages/tramlines.html (que a su vez reemplazó al WinForms FormTramLine).
//
// Qué quedó NATIVO: la pantalla entera — el lienzo interactivo (contornos,
// outer/inner tram, guías, trams guardados y de preview, los puntos A/B del
// corte, mover/acercar/tocar) y toda la columna de controles: ciclar guía,
// cambiar de lado, pasadas, pasada de inicio, tram exterior, agregar líneas,
// el corte de 3 toques, opacidad, borrar todos, guardar y salir.
//
// Qué SIGUE en HTML: pages/tramlines.html + js/tramlines.js, INTACTOS, para el
// Hub remoto / celular / Android. OJO — el editor del motor NO es thread-safe y
// hay UNA sola sesión global: si alguien abre la página desde el celular
// mientras el panel nativo está abierto, se pisan. Ya pasaba hoy entre dos
// browsers; el porteo no lo empeora, pero conviene no hacerlo.
//
// NO CONFUNDIR con TramGeometryClient (ese pega a /api/aog/tram y sirve para
// dibujar las huellas en el mapa GL; otro contrato, otros puntos {e,n}) ni con
// TramSimpleClient (/api/tram-simple/*, la pantalla de huellas por pasadas).
// Acá los puntos son double[2] = [easting, northing], no objetos.
//
// Wire: el MISMO /api/tramlines/* de siempre, snake_case (AgpJson). Cero
// cambios de backend. Sin polling: todos los POST menos /close y /cancel
// devuelven el estado COMPLETO reconstruido — se pinta con eso y listo.
//
// REGLA DE ORO del wire: la única fuente de verdad es el DTO que vuelve. El
// motor resetea passes/start por su cuenta en /swap, /outer y /delete-all; si
// el panel pintara valores locales optimistas, los steppers mentirían.
//
// OJO con /cancel: NO es un "deshacer". Tram_CancelSession() borra los trams
// GUARDADOS (no solo el preview), apaga el displayMode y PERSISTE el borrado.
// Es "salir sin tramlines". Por eso el panel lo rotula así y pide confirmación.
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
// SelIdx sin atributo) -------------------------------------------------------

public sealed class TramMultiTrack
{
    [JsonPropertyName("index")]  public int Index { get; set; }
    /// <summary>Nombre que le puso el operario: DATO, no se traduce nunca.</summary>
    [JsonPropertyName("name")]   public string? Name { get; set; }
    /// <summary>"ab" (recta) | "curve" (curva).</summary>
    [JsonPropertyName("mode")]   public string? Mode { get; set; }
    /// <summary>
    /// Puntos en E/N metros (double[2]). Las AB mandan SOLO los dos extremos
    /// extendidos; las curvas mandan la polilínea entera. Se dibuja tal cual,
    /// sin distinguir.
    /// </summary>
    [JsonPropertyName("points")] public double[][]? Points { get; set; }
}

public sealed class TramMultiState
{
    [JsonPropertyName("ok")]                   public bool Ok { get; set; } = true;
    /// <summary>Sin contorno no se puede construir NADA — y tampoco se aceptan toques de corte.</summary>
    [JsonPropertyName("has_boundary")]         public bool HasBoundary { get; set; }
    /// <summary>"m" | "ft" (el motor headless devuelve siempre "m").</summary>
    [JsonPropertyName("units")]                public string? Units { get; set; }
    /// <summary>Trocha del vehículo, en unidades display.</summary>
    [JsonPropertyName("track_width_display")]  public double TrackWidthDisplay { get; set; }
    [JsonPropertyName("tram_width_display")]   public double TramWidthDisplay { get; set; }
    [JsonPropertyName("tool_width_display")]   public double ToolWidthDisplay { get; set; }

    /// <summary>Guías AB/Curva VISIBLES del lote (las que sirven para generar trams).</summary>
    [JsonPropertyName("tracks")]               public List<TramMultiTrack>? Tracks { get; set; }
    /// <summary>-1 = ninguna guía seleccionada.</summary>
    [JsonPropertyName("sel_idx")]              public int SelIdx { get; set; } = -1;

    /// <summary>Preview: trams calculados pero TODAVÍA no confirmados con "Agregar".</summary>
    [JsonPropertyName("new_trams")]            public List<double[][]>? NewTrams { get; set; }
    /// <summary>Trams ya confirmados (los que se dibujan con la opacidad del operario).</summary>
    [JsonPropertyName("saved_trams")]          public List<double[][]>? SavedTrams { get; set; }

    /// <summary>Anillos del lote: [0] = exterior, el resto islas.</summary>
    [JsonPropertyName("fences")]               public List<double[][]>? Fences { get; set; }
    /// <summary>Huella perimetral exterior (vacía si "Tram exterior" está apagado).</summary>
    [JsonPropertyName("outer_bnd")]            public double[][]? OuterBnd { get; set; }
    [JsonPropertyName("inner_bnd")]            public double[][]? InnerBnd { get; set; }

    [JsonPropertyName("passes")]               public int Passes { get; set; } = 2;
    [JsonPropertyName("start_pass")]           public int StartPass { get; set; }
    [JsonPropertyName("is_outer")]             public bool IsOuter { get; set; }
    /// <summary>Opacidad de los trams guardados, 0.2 … 1 (la clampa el motor también).</summary>
    [JsonPropertyName("alpha")]                public double Alpha { get; set; } = 1.0;

    /// <summary>Corte de 3 toques: 0 = quieto · 1 = falta el B · 2 = falta el lado a borrar.</summary>
    [JsonPropertyName("cut_step")]             public int CutStep { get; set; }
    /// <summary>Punto A del corte; null/ausente si no se marcó (centinela 9000000 filtrado en el mapper).</summary>
    [JsonPropertyName("pt_a")]                 public double[]? PtA { get; set; }
    [JsonPropertyName("pt_b")]                 public double[]? PtB { get; set; }

    /// <summary>sin-contorno · sin-guias · error-interno · no-state · service-unavailable · bad-json.</summary>
    [JsonPropertyName("error")]                public string? Error { get; set; }
}

public sealed class TramMultiClient
{
    // 6 s (los otros paneles usan 3): /open y /add reconstruyen la geometría de
    // TODAS las pasadas contra el contorno del lote, y en un lote grande con una
    // curva de cientos de puntos eso tarda. Cortar a los 3 s pintaría "sin
    // conexión" con el motor trabajando bien.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _base;

    public TramMultiClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    // ---- estado -------------------------------------------------------------

    /// <summary>
    /// GET /state — devuelve el estado SIN tocar nada. Recurso de re-sincronización
    /// (volver al panel con la sesión ya abierta), NO un loop de polling.
    /// </summary>
    public Task<TramMultiState?> GetStateAsync(CancellationToken ct = default)
        => GetAsync<TramMultiState>("api/tramlines/state", ct);

    /// <summary>
    /// POST /open — INICIA la sesión: exige contorno ("sin-contorno"), junta las
    /// guías AB/Curva visibles ("sin-guias" si no hay), auto-detecta de qué lado
    /// va cada una, arranca en la guía 0 con passes=2 y start = 1 si hay outer,
    /// y construye el primer preview. Guarda copias temporales de las guías, así
    /// que va SIEMPRE apareado con un /close o un /cancel.
    /// </summary>
    public Task<TramMultiState?> OpenAsync(CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/open", null, ct);

    // ---- acciones (todas devuelven el estado COMPLETO) ----------------------

    /// <summary>POST /cycle — cicla la guía activa; el motor da la vuelta en los dos extremos.</summary>
    public Task<TramMultiState?> CycleAsync(int dir, CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/cycle", new DirBody { Dir = dir }, ct);

    /// <summary>
    /// POST /swap — espeja de qué lado de la guía salen los trams. OJO: el motor
    /// además RESETEA passes=2 y start (1 si hay outer, 0 si no).
    /// </summary>
    public Task<TramMultiState?> SwapAsync(CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/swap", null, ct);

    /// <summary>POST /passes — piso 1, clampado en cliente y en el motor.</summary>
    public Task<TramMultiState?> SetPassesAsync(int passes, CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/passes", new PassesBody { Passes = passes }, ct);

    /// <summary>POST /start-pass — piso 0.</summary>
    public Task<TramMultiState?> SetStartPassAsync(int startPass, CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/start-pass", new StartPassBody { StartPass = startPass }, ct);

    /// <summary>
    /// POST /outer — prende/apaga las huellas perimetrales outer+inner. En los
    /// DOS sentidos el motor resetea passes/start: hay que repintar del estado
    /// devuelto, nunca del valor local.
    /// </summary>
    public Task<TramMultiState?> SetOuterAsync(bool on, CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/outer", new OnBody { On = on }, ct);

    /// <summary>POST /alpha — solo afecta el DIBUJO de los trams guardados. Rango 0.2 … 1.</summary>
    public Task<TramMultiState?> SetAlphaAsync(double alpha, CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/alpha", new AlphaBody { Alpha = alpha }, ct);

    /// <summary>POST /add — pasa el preview a guardados y limpia el preview.</summary>
    public Task<TramMultiState?> AddAsync(CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/add", null, ct);

    /// <summary>
    /// POST /delete-all — borra preview + guardados + outer/inner y resetea
    /// passes/start. Destructivo: el panel pide confirmación antes.
    /// </summary>
    public Task<TramMultiState?> DeleteAllAsync(CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/delete-all", null, ct);

    /// <summary>
    /// POST /tap — alimenta el corte de 3 toques: 1º = punto A, 2º = punto B,
    /// 3º = lado a eliminar (el motor borra los puntos de los trams NUEVOS —no
    /// los guardados— del lado tocado de la recta AB, solo en las polilíneas que
    /// esa recta cruza, y después vuelve al paso 0).
    /// </summary>
    public Task<TramMultiState?> TapAsync(double e, double n, CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/tap", new TapBody { E = e, N = n }, ct);

    /// <summary>POST /cancel-touch — vuelve el corte al paso 0 y borra A y B.</summary>
    public Task<TramMultiState?> CancelTouchAsync(CancellationToken ct = default)
        => PostAsync<TramMultiState>("api/tramlines/cancel-touch", null, ct);

    // ---- salidas (devuelven {"ok":true} pelado, NO el estado) ---------------

    /// <summary>
    /// POST /close — GUARDA: persiste Tram.txt y la opacidad en Settings, y
    /// limpia la sesión temporal. Es la salida normal (y la del cierre lateral).
    /// </summary>
    public async Task<bool> CloseAsync(CancellationToken ct = default)
    {
        var r = await PostAsync<OkResp>("api/tramlines/close", null, ct).ConfigureAwait(false);
        return r != null && r.Ok;
    }

    /// <summary>
    /// POST /cancel — NO es "deshacer": borra TODOS los trams (los guardados de
    /// antes incluidos), apaga el displayMode y PERSISTE el borrado. El panel lo
    /// rotula "Salir sin tramlines" y pide confirmación.
    /// </summary>
    public async Task<bool> CancelAsync(CancellationToken ct = default)
    {
        var r = await PostAsync<OkResp>("api/tramlines/cancel", null, ct).ConfigureAwait(false);
        return r != null && r.Ok;
    }

    private sealed class OkResp
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
    }

    // ---- cuerpos del request (snake_case a mano: los tipos anónimos de C# no
    // pueden tener una propiedad "start_pass") --------------------------------

    private sealed class DirBody
    {
        [JsonPropertyName("dir")] public int Dir { get; set; } = 1;
    }

    private sealed class PassesBody
    {
        [JsonPropertyName("passes")] public int Passes { get; set; } = 2;
    }

    private sealed class StartPassBody
    {
        [JsonPropertyName("start_pass")] public int StartPass { get; set; }
    }

    private sealed class OnBody
    {
        [JsonPropertyName("on")] public bool On { get; set; }
    }

    private sealed class AlphaBody
    {
        [JsonPropertyName("alpha")] public double Alpha { get; set; } = 1.0;
    }

    private sealed class TapBody
    {
        [JsonPropertyName("e")] public double E { get; set; }
        [JsonPropertyName("n")] public double N { get; set; }
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
                body == null ? "{}" : JsonSerializer.Serialize(body, body.GetType()),
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
