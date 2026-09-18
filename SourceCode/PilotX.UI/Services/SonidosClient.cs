// ============================================================================
// SonidosClient.cs — cliente HTTP de las alarmas sonoras de cabina.
//
// Lo consume SonidosPanel (port nativo de pages/sonidos.html). Wire IDÉNTICO
// al que usaba la página: GET/PUT /api/sonidos/config, GET /api/sonidos/estado,
// GET/POST /api/sonidos/archivos y el estático /sounds/<archivo>.wav. Cero
// cambio de contrato: la página HTML sigue viva para la PWA del celular.
//
// Qué quedó NATIVO con este cliente: config completa (habilitado, sonido,
// umbral, sostenido, repetir), mute, alarmas activas, probar el sonido y
// subir un .wav propio desde un pendrive. Ya NO queda nada de esta pantalla
// en HTML: el panel no abre WebView para nada.
//
// Dos trampas del wire que están cubiertas acá:
//   · snake_case: umbral_pct / sostenido_seg / repetir_seg NO matchean con
//     PropertyNameCaseInsensitive — va [JsonPropertyName] en CADA propiedad,
//     si no el PUT mandaría ceros y el server los tomaría como default.
//   · BOM de EmbedIO: las respuestas pueden arrancar con '﻿' y sin
//     TrimStart el Deserialize tira y el panel queda en "Hub no responde"
//     para siempre (mismo cuidado que SoundAlarmPoller).
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

/// <summary>Un evento configurable (piloto_on, dosis_baja, …).</summary>
public sealed class SonidoEventoWire
{
    [JsonPropertyName("id")]            public string Id { get; set; } = "";
    [JsonPropertyName("nombre")]        public string Nombre { get; set; } = "";
    [JsonPropertyName("habilitado")]    public bool Habilitado { get; set; } = true;
    [JsonPropertyName("sonido")]        public string Sonido { get; set; } = "";
    [JsonPropertyName("repetir_seg")]   public int RepetirSeg { get; set; }
    [JsonPropertyName("umbral_pct")]    public double UmbralPct { get; set; }
    [JsonPropertyName("sostenido_seg")] public double SostenidoSeg { get; set; }
}

public sealed class SonidosConfigWire
{
    [JsonPropertyName("mute")]    public bool Mute { get; set; }
    [JsonPropertyName("eventos")] public List<SonidoEventoWire> Eventos { get; set; } = new();
}

public sealed class SonidoDisparoWire
{
    [JsonPropertyName("seq")]     public long Seq { get; set; }
    [JsonPropertyName("evento")]  public string Evento { get; set; } = "";
    [JsonPropertyName("sonido")]  public string Sonido { get; set; } = "";
    [JsonPropertyName("detalle")] public string Detalle { get; set; } = "";
}

public sealed class SonidosEstadoWire
{
    [JsonPropertyName("seq")]      public long Seq { get; set; }
    [JsonPropertyName("mute")]     public bool Mute { get; set; }
    /// <summary>Revisión de la carpeta /sounds: se mueve cuando algún .wav
    /// cambió (lo subió esta pantalla, el celular o una copia a mano). Quien
    /// cachea wavs en memoria los tira cuando esto cambia.</summary>
    [JsonPropertyName("archivos_rev")] public long ArchivosRev { get; set; }
    [JsonPropertyName("activas")]  public List<SonidoDisparoWire> Activas { get; set; } = new();
    [JsonPropertyName("disparos")] public List<SonidoDisparoWire> Disparos { get; set; } = new();
}

/// <summary>Resultado de subir un .wav propio.</summary>
public sealed class SonidoSubidaResultado
{
    [JsonPropertyName("ok")]      public bool Ok { get; set; }
    [JsonPropertyName("archivo")] public string Archivo { get; set; } = "";
    [JsonPropertyName("error")]   public string Error { get; set; } = "";
}

public sealed class SonidosClient
{
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public SonidosClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Base del Hub (la usa el panel para el teclado nativo).</summary>
    public string BaseUrl => _baseUrl;

    // ------------------------------------------------------------------ config

    public async Task<SonidosConfigWire?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/sonidos/config", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SonidosConfigWire>(Limpio(json), _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Guarda el cfg COMPLETO (el PUT es reemplazo total, no merge).</summary>
    public async Task<bool> PutConfigAsync(SonidosConfigWire cfg, CancellationToken ct = default)
    {
        if (cfg == null) return false;
        try
        {
            using var cuerpo = new StringContent(JsonSerializer.Serialize(cfg), Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(_baseUrl + "api/sonidos/config", cuerpo, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(Limpio(json));
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ estado

    /// <summary>Estado de alarmas. El panel pide desde=long.MaxValue: solo le
    /// interesan las ACTIVAS y el mute — sonar es trabajo de SoundAlarmPoller,
    /// que lleva su propio seq. Consumir disparos acá sonaría todo dos veces.</summary>
    public async Task<SonidosEstadoWire?> GetEstadoAsync(long desde, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                _baseUrl + "api/sonidos/estado?desde=" + desde.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SonidosEstadoWire>(Limpio(json), _jsonOpts);
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- archivos

    /// <summary>Los .wav disponibles en wwwroot/sounds. null = no respondió.</summary>
    public async Task<List<string>?> GetArchivosAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/sonidos/archivos", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(Limpio(json));
            if (!doc.RootElement.TryGetProperty("archivos", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<string>();
            var lista = new List<string>();
            foreach (var a in arr.EnumerateArray())
            {
                var n = a.GetString();
                if (!string.IsNullOrEmpty(n)) lista.Add(n!);
            }
            return lista;
        }
        catch { return null; }
    }

    /// <summary>Sube un .wav propio. El body son los BYTES CRUDOS (sin multipart),
    /// igual que el upload de firmwares del Hub.</summary>
    public async Task<SonidoSubidaResultado> SubirArchivoAsync(string nombre, byte[] datos, CancellationToken ct = default)
    {
        try
        {
            using var cuerpo = new ByteArrayContent(datos ?? Array.Empty<byte>());
            using var resp = await _http.PostAsync(
                _baseUrl + "api/sonidos/archivos?nombre=" + Uri.EscapeDataString(nombre ?? ""),
                cuerpo, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<SonidoSubidaResultado>(Limpio(json), _jsonOpts);
            return r ?? new SonidoSubidaResultado { Ok = false, Error = "respuesta ilegible" };
        }
        catch (Exception ex)
        {
            return new SonidoSubidaResultado { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>Baja un .wav para probarlo con el sink de audio del head.</summary>
    public async Task<byte[]?> GetWavAsync(string nombre, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(nombre)) return null;
        try
        {
            return await _http.GetByteArrayAsync(_baseUrl + "sounds/" + Uri.EscapeDataString(nombre), ct)
                              .ConfigureAwait(false);
        }
        catch { return null; }
    }

    // ----------------------------------------------------------------- teclado

    /// <summary>Abre/cierra la ventana del teclado nativo de PilotX. Misma señal
    /// que mandan las páginas HTML. Catch mudo a propósito: sin teclado nativo
    /// el campo sigue editable con teclado físico.</summary>
    public async Task TecladoAsync(bool abrir, string titulo = "", bool numerico = true)
    {
        try
        {
            string body = abrir
                ? "{\"numerico\":" + (numerico ? "true" : "false") +
                  ",\"titulo\":" + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var cuerpo = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(
                _baseUrl + "api/teclado/" + (abrir ? "abrir" : "cerrar"), cuerpo).ConfigureAwait(false);
        }
        catch { }
    }

    // El BOM de EmbedIO deja el JSON arrancando con U+FEFF y System.Text.Json
    // no lo perdona (escapes explícitos: como literales serían invisibles en
    // el fuente, igual que en Traductor.CargarDiccionarioAsync).
    private static string Limpio(string json) => (json ?? "").TrimStart('\uFEFF', '\u200B').TrimStart();
}
