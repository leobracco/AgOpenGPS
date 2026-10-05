// ============================================================================
// DebugClient.cs — cliente HTTP del módulo Debug global (log unificado).
//
// Lo consume DebugPanel (port nativo de pages/debug.html + js/debug.js). Wire
// IDÉNTICO al del DebugController — cero cambio de contrato, porque debug.html
// sigue viva para el Hub remoto / la PWA del celular:
//
//   GET  /api/debug/snapshot?max=N          → { seq, known_modules, config,
//                                               recording, recording_file, entries }
//   GET  /api/debug/entries?since=S         → { ok, count, entries }
//   PUT  /api/debug/config                  ← { modules, min_level }
//   POST /api/debug/module?name=X&on=B      → { ok, name, on }
//   POST /api/debug/clear                   → { ok }
//   POST /api/debug/record?on=B             → { ok, file }
//
// LO ÚNICO QUE NO SE PORTA: el WebSocket /ws/debug. En su lugar se usa el
// endpoint REST hermano /api/debug/entries?since=S, que existe desde el día uno
// para exactamente esto (DebugLogService.GetEntriesSince devuelve TODO lo que
// siga en el buffer del servidor con seq > since) y ya se venía usando en el
// repo con el mismo criterio (ver QuantiXEditorClient: un ClientWebSocket con
// reconexión/heartbeat/frames agrega tres modos de falla nuevos para ganar
// milésimas que en un visor de log no se notan). El seq del servidor es
// monotónico y /api/debug/clear NO lo resetea, así que el "since" nunca se
// desincroniza: no se saltea ni se duplica ninguna línea.
//
// LO QUE ESTO NO GARANTIZA: que no se pierda NADA. El buffer del servidor es un
// ring de MaxBufferLines (5000 por defecto) que descarta por la cabeza, así que
// si entre dos pasadas del poll (500 ms) entran más de 5000 renglones, los más
// viejos ya no están cuando se los pide y no llegan nunca. Con el WS tampoco
// llegaban completos en esa situación, pero conviene tenerlo claro: en una
// tormenta de log el visor muestra la cola, no el todo.
//
// Trampas del wire cubiertas acá:
//   · snake_case: min_level / recording_file / known_modules / record_to_file /
//     max_buffer_lines NO matchean con PropertyNameCaseInsensitive (no cubre
//     underscores) → [JsonPropertyName] explícito en CADA propiedad.
//   · BOM de EmbedIO: las respuestas pueden arrancar con U+FEFF y sin TrimStart
//     el Deserialize tira.
//   · Sin HttpClient.Timeout: la cancelación es por CancellationToken (un
//     TaskCanceledException confundido con timeout deja la UI congelada).
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

/// <summary>Una línea del log unificado.</summary>
public sealed class DebugEntryWire
{
    /// <summary>Timestamp UTC ISO-8601 ("O"). El visor lo muestra en hora local.</summary>
    [JsonPropertyName("ts")]      public string? Ts      { get; set; }
    [JsonPropertyName("seq")]     public long    Seq     { get; set; }
    [JsonPropertyName("module")]  public string? Module  { get; set; }
    [JsonPropertyName("level")]   public string? Level   { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class DebugConfigWire
{
    [JsonPropertyName("modules")]          public Dictionary<string, bool>? Modules { get; set; }
    [JsonPropertyName("min_level")]        public string? MinLevel       { get; set; }
    [JsonPropertyName("record_to_file")]   public bool    RecordToFile   { get; set; }
    [JsonPropertyName("record_dir")]       public string? RecordDir      { get; set; }
    [JsonPropertyName("max_buffer_lines")] public int     MaxBufferLines { get; set; }
}

public sealed class DebugSnapshotWire
{
    [JsonPropertyName("seq")]            public long     Seq           { get; set; }
    [JsonPropertyName("known_modules")]  public string[]? KnownModules { get; set; }
    [JsonPropertyName("config")]         public DebugConfigWire? Config { get; set; }
    [JsonPropertyName("recording")]      public bool     Recording     { get; set; }
    [JsonPropertyName("recording_file")] public string?  RecordingFile { get; set; }
    [JsonPropertyName("entries")]        public List<DebugEntryWire> Entries { get; set; } = new();
}

public sealed class DebugEntriesWire
{
    [JsonPropertyName("ok")]      public bool    Ok    { get; set; }
    [JsonPropertyName("count")]   public int     Count { get; set; }
    [JsonPropertyName("entries")] public List<DebugEntryWire> Entries { get; set; } = new();
}

public sealed class DebugRecordWire
{
    [JsonPropertyName("ok")]   public bool    Ok   { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
}

public sealed class DebugClient
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

    /// <summary>Corte de cada lectura. 5 s: el visor pollea a 2 Hz, si una
    /// pasada se cuelga más que esto ya no sirve.</summary>
    private static readonly TimeSpan Corte = TimeSpan.FromSeconds(5);

    private readonly string _baseUrl;

    public DebugClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public string BaseUrl => _baseUrl;

    // --------------------------------------------------------------- snapshot

    /// <summary>GET /api/debug/snapshot?max=N. null = falló (el JS entraba al
    /// catch del loadSnapshot y pintaba "No se pudo conectar al servicio
    /// Debug.", sin conectar el push).</summary>
    public async Task<DebugSnapshotWire?> GetSnapshotAsync(int max, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            using var resp = await _http.GetAsync(
                _baseUrl + "api/debug/snapshot?max=" + max.ToString(CultureInfo.InvariantCulture),
                corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<DebugSnapshotWire>(Limpio(json), _jsonOpts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;   // el panel se cerró
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- entries

    /// <summary>GET /api/debug/entries?since=S — todo lo que entró después de
    /// `since`. Reemplaza al push del WebSocket. null = no se pudo hablar con
    /// el servicio (el panel pasa la pill a "Desconectado", igual que el
    /// ws.onclose del original).</summary>
    public async Task<DebugEntriesWire?> GetEntriesAsync(long since, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            using var resp = await _http.GetAsync(
                _baseUrl + "api/debug/entries?since=" + since.ToString(CultureInfo.InvariantCulture),
                corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<DebugEntriesWire>(Limpio(json), _jsonOpts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ----------------------------------------------------------------- config

    /// <summary>PUT /api/debug/config ← { modules, min_level }. El original
    /// manda EXACTAMENTE esas dos claves (no el DTO completo) y se traga
    /// cualquier error en silencio.</summary>
    public async Task PutConfigAsync(Dictionary<string, bool> modules, string minLevel,
                                     CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            var sb = new StringBuilder();
            sb.Append("{\"modules\":{");
            bool primero = true;
            foreach (var kv in modules ?? new Dictionary<string, bool>())
            {
                if (!primero) sb.Append(',');
                primero = false;
                sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':').Append(kv.Value ? "true" : "false");
            }
            sb.Append("},\"min_level\":").Append(JsonSerializer.Serialize(minLevel ?? "")).Append('}');

            using var contenido = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
            using var _ = await _http.PutAsync(_baseUrl + "api/debug/config", contenido, corte.Token)
                                     .ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>POST /api/debug/module?name=X&amp;on=B. Error en silencio, igual
    /// que el original: el chip ya se pintó del lado del UI.</summary>
    public async Task SetModuloAsync(string nombre, bool on, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            string url = _baseUrl + "api/debug/module?name=" + Uri.EscapeDataString(nombre ?? "")
                       + "&on=" + (on ? "true" : "false");
            using var contenido = new StringContent("", Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(url, contenido, corte.Token).ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>POST /api/debug/clear — vacía el ring buffer del servidor.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            using var contenido = new StringContent("", Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_baseUrl + "api/debug/clear", contenido, corte.Token)
                                     .ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>POST /api/debug/record?on=B → { ok, file }. null = la request
    /// falló: el original REVIERTE el botón en ese caso (y solo en ese).</summary>
    public async Task<DebugRecordWire?> RecordAsync(bool on, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            string url = _baseUrl + "api/debug/record?on=" + (on ? "true" : "false");
            using var contenido = new StringContent("", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, contenido, corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<DebugRecordWire>(Limpio(json), _jsonOpts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    // El BOM de EmbedIO deja el JSON arrancando con U+FEFF y System.Text.Json no
    // lo perdona (escapes explícitos: como literales serían invisibles acá).
    private static string Limpio(string json) => (json ?? "").TrimStart('\uFEFF', '\u200B').TrimStart();
}
