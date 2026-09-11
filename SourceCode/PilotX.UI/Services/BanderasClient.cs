// ============================================================================
// BanderasClient.cs — canal HTTP del panel BANDERAS nativo (ex pages/banderas.html,
// que a su vez reemplazó a FormFlags + FormEnterFlag).
//
// Mismo wire de siempre, sin un solo cambio de contrato (snake_case por AgpJson):
//   GET  /api/flags/state              → estado (lista + distancias live)
//   POST /api/flags/pick    {number}   → selecciona bandera (1-based)
//   POST /api/flags/delete             → borra la seleccionada
//   POST /api/flags/notes   {notes}    → notas de la seleccionada
//   POST /api/flags/add     {lat,lon,color,use_current} → crea bandera
//   POST /api/flags/close              → cierre del widget (deselecciona+guarda)
//   POST /api/flags/import             → import CSV (diálogo nativo)
//   POST /api/flags/export             → export CSV (diálogo nativo)
// TODAS devuelven el mismo estado, así que el panel pinta con la respuesta del
// POST sin esperar el próximo tick del poll — igual que hacía banderas.js.
//
// OJO con el /add: cuando el operario NO tocó lat/lon, el JS manda SOLO
// {color, use_current:true} — sin las claves lat/lon. Se replica al pie de la
// letra armando el JSON a mano: mandar lat:0/lon:0 con use_current true sería
// el mismo resultado hoy, pero es cambiarle el cuerpo al request y este panel
// no está para eso.
//
// Sin HttpClient.Timeout: cada llamada linkea el token del panel con un corte
// propio de 3 s. Si el corte fuera .Timeout, el TaskCanceledException que tira
// sería indistinguible del cierre del panel y la UI se quedaría congelada en
// sus valores (ver feedback_httpclient_timeout_kills_polling).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

// ---- DTOs del cable (snake_case EXPLÍCITO: PropertyNameCaseInsensitive NO
// cubre underscores — "distance_m" no matchea DistanceM sin atributo) --------

/// <summary>Una bandera del lote, tal cual la manda /api/flags/state.</summary>
public sealed class BanderaItemDto
{
    /// <summary>Posición 1-based en la lista (lo que viaja en /pick).</summary>
    [JsonPropertyName("number")]     public int Number { get; set; }

    /// <summary>ID interno (solo display; puede tener huecos).</summary>
    [JsonPropertyName("id")]         public int Id { get; set; }

    /// <summary>0 = roja, 1 = verde, 2 = amarilla (mismo código que banderas.html).</summary>
    [JsonPropertyName("color")]      public int Color { get; set; }

    [JsonPropertyName("notes")]      public string? Notes { get; set; }
    [JsonPropertyName("lat")]        public double Lat { get; set; }
    [JsonPropertyName("lon")]        public double Lon { get; set; }

    /// <summary>Distancia del tractor a la bandera, en metros.</summary>
    [JsonPropertyName("distance_m")] public double DistanceM { get; set; }

    [JsonPropertyName("easting")]    public double Easting { get; set; }
    [JsonPropertyName("northing")]   public double Northing { get; set; }
}

/// <summary>Estado completo del widget de banderas.</summary>
public sealed class BanderasEstadoDto
{
    // Default true a propósito: el JS solo trata la respuesta como error si
    // `s.ok === false`. Un payload sin la clave (undefined) seguía de largo, y
    // acá tiene que seguir de largo igual.
    [JsonPropertyName("ok")]        public bool Ok { get; set; } = true;

    [JsonPropertyName("has_field")] public bool HasField { get; set; }

    /// <summary>Bandera seleccionada (Number 1-based; 0 = ninguna).</summary>
    [JsonPropertyName("picked")]    public int Picked { get; set; }

    /// <summary>Posición actual del tractor (prefill de "nueva bandera").</summary>
    [JsonPropertyName("cur_lat")]   public double CurLat { get; set; }
    [JsonPropertyName("cur_lon")]   public double CurLon { get; set; }

    [JsonPropertyName("flags")]     public List<BanderaItemDto>? Flags { get; set; }

    [JsonPropertyName("error")]     public string? Error { get; set; }
}

public sealed class BanderasClient
{
    // Timeout.InfiniteTimeSpan: el corte lo pone el CTS linkeado de cada
    // llamada, no el HttpClient (ver cabecera).
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Corte por llamada. 3 s: el panel prefiere pintar "Sin conexión
    /// con PilotX." antes que quedarse esperando con el operario manejando.</summary>
    private static readonly TimeSpan Corte = TimeSpan.FromSeconds(3);

    private readonly string _base;

    public BanderasClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    // ---- lectura -----------------------------------------------------------

    public Task<BanderasEstadoDto?> GetEstadoAsync(CancellationToken ct = default)
        => PedirAsync(HttpMethod.Get, "api/flags/state", null, ct);

    // ---- acciones ----------------------------------------------------------

    public Task<BanderasEstadoDto?> PickAsync(int number, CancellationToken ct = default)
        => PedirAsync(HttpMethod.Post, "api/flags/pick",
                      "{\"number\":" + number.ToString(CultureInfo.InvariantCulture) + "}", ct);

    public Task<BanderasEstadoDto?> BorrarAsync(CancellationToken ct = default)
        => PedirAsync(HttpMethod.Post, "api/flags/delete", "{}", ct);

    public Task<BanderasEstadoDto?> NotasAsync(string? notas, CancellationToken ct = default)
        => PedirAsync(HttpMethod.Post, "api/flags/notes",
                      "{\"notes\":" + JsonSerializer.Serialize(notas ?? "") + "}", ct);

    /// <summary>Alta con lat/lon tipeadas por el operario (use_current:false).</summary>
    public Task<BanderasEstadoDto?> AgregarEnLatLonAsync(double lat, double lon, int color,
                                                         CancellationToken ct = default)
        => PedirAsync(HttpMethod.Post, "api/flags/add",
                      "{\"lat\":" + Num(lat) + ",\"lon\":" + Num(lon)
                      + ",\"color\":" + color.ToString(CultureInfo.InvariantCulture)
                      + ",\"use_current\":false}", ct);

    /// <summary>Alta en la posición del tractor. Sin claves lat/lon, igual que el JS.</summary>
    public Task<BanderasEstadoDto?> AgregarEnPosicionActualAsync(int color, CancellationToken ct = default)
        => PedirAsync(HttpMethod.Post, "api/flags/add",
                      "{\"color\":" + color.ToString(CultureInfo.InvariantCulture)
                      + ",\"use_current\":true}", ct);

    /// <summary>Cierre del widget: deselecciona + guarda (ex btnExit de FormFlags).</summary>
    public Task<BanderasEstadoDto?> CerrarSesionAsync(CancellationToken ct = default)
        => PedirAsync(HttpMethod.Post, "api/flags/close", "{}", ct);

    public Task<BanderasEstadoDto?> ImportarAsync(CancellationToken ct = default)
        => PedirAsync(HttpMethod.Post, "api/flags/import", "{}", ct);

    public Task<BanderasEstadoDto?> ExportarAsync(CancellationToken ct = default)
        => PedirAsync(HttpMethod.Post, "api/flags/export", "{}", ct);

    /// <summary>
    /// Teclado nativo de PilotX (TecladoWindow) para los campos de texto: misma
    /// señal HTTP que mandan las páginas del Hub. Catch mudo a propósito — sin
    /// teclado en pantalla el campo sigue editable con uno físico.
    /// </summary>
    public async Task TecladoAsync(bool abrir, bool numerico = true, string titulo = "")
    {
        try
        {
            using var cts = new CancellationTokenSource(Corte);
            string cuerpo = abrir
                ? "{\"numerico\":" + (numerico ? "true" : "false") + ",\"titulo\":"
                  + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var contenido = new StringContent(cuerpo, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_base + "api/teclado/" + (abrir ? "abrir" : "cerrar"),
                                                contenido, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    // ---- plomería ----------------------------------------------------------

    private async Task<BanderasEstadoDto?> PedirAsync(HttpMethod verbo, string ruta,
                                                      string? cuerpo, CancellationToken ct)
    {
        using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
        corte.CancelAfter(Corte);
        try
        {
            using var req = new HttpRequestMessage(verbo, _base + ruta);
            if (cuerpo != null)
                req.Content = new StringContent(cuerpo, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            return Deserializar(json);
        }
        // El panel se está cerrando: que se entere el caller y corte el bucle.
        // TaskCanceledException HEREDA de OperationCanceledException, así que
        // sin el `when` el corte de 3 s se confundiría con el cierre.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Corte de 3 s, red caída, JSON roto → null = "Sin conexión con PilotX."
        catch { return null; }
    }

    // El BOM al principio del cuerpo ya rompió parseos antes (ver la traza del
    // /api/corex/gps): se saca siempre, sale gratis.
    private static BanderasEstadoDto? Deserializar(string json)
    {
        try { return JsonSerializer.Deserialize<BanderasEstadoDto>(json.TrimStart('﻿'), _jsonOpts); }
        catch { return null; }
    }

    /// <summary>
    /// Número al JSON con el mismo formato que JSON.stringify: punto decimal,
    /// nunca coma. Con la cultura es-AR, un ToString() suelto mandaría
    /// "-33,123" y el motor leería una bandera en cualquier lado.
    /// </summary>
    private static string Num(double v)
        => v.ToString("R", CultureInfo.InvariantCulture);
}
