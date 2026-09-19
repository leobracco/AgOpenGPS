// ============================================================================
// ChatPanelClient.cs — cliente HTTP de la pantalla de Chat de soporte.
//
// Lo consume ChatPanel. Habla SOLO con el host local (127.0.0.1:5180): el chat
// de verdad (poll al cloud + auth por dispositivo) vive en el Engine
// (ChatSoporteService), acá sólo se lee su estado y se postea lo que escribe el
// operario.
//
//   GET  /api/chat/estado    → { ok, rev, no_leidos }          (liviano, badge)
//   GET  /api/chat/mensajes  → { ok, rev, no_leidos, mensajes:[{rol,texto,ts}] }
//   POST /api/chat/enviar    ← { texto }  → { ok }
//
// Mismas trampas del wire que OrbitXPanelClient: snake_case con
// [JsonPropertyName], BOM de EmbedIO, y corte por CancellationToken (no por
// Timeout de HttpClient) para que cerrar el panel no se disfrace de error.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Un cambio de config sugerido por el bot (valores como texto).</summary>
public sealed class ChatCambioWire
{
    [JsonPropertyName("clave")]        public string? Clave       { get; set; }
    [JsonPropertyName("valor_actual")] public string? ValorActual { get; set; }
    [JsonPropertyName("valor_nuevo")]  public string? ValorNuevo  { get; set; }
}

/// <summary>Payload de un mensaje tipo "propuesta_config".</summary>
public sealed class ChatPropuestaWire
{
    [JsonPropertyName("cambios")] public List<ChatCambioWire>? Cambios { get; set; }
    [JsonPropertyName("estado")]  public string? Estado { get; set; }
}

/// <summary>Un mensaje del chat tal como lo manda el Engine. tipo es "texto"
/// (default) o "propuesta_config"; en ese caso viene payload con los cambios.</summary>
public sealed class ChatMensajeWire
{
    [JsonPropertyName("rol")]     public string? Rol   { get; set; }
    [JsonPropertyName("texto")]   public string? Texto { get; set; }
    [JsonPropertyName("ts")]      public long    Ts    { get; set; }
    [JsonPropertyName("tipo")]    public string? Tipo  { get; set; }
    [JsonPropertyName("payload")] public ChatPropuestaWire? Payload { get; set; }
}

/// <summary>GET /api/chat/estado — liviano, para el badge del menú.</summary>
public sealed class ChatEstadoWire
{
    [JsonPropertyName("ok")]        public bool Ok       { get; set; }
    [JsonPropertyName("rev")]       public long Rev      { get; set; }
    [JsonPropertyName("no_leidos")] public bool NoLeidos { get; set; }
}

/// <summary>GET /api/chat/mensajes — la conversación entera.</summary>
public sealed class ChatMensajesWire
{
    [JsonPropertyName("ok")]        public bool Ok       { get; set; }
    [JsonPropertyName("rev")]       public long Rev      { get; set; }
    [JsonPropertyName("no_leidos")] public bool NoLeidos { get; set; }
    [JsonPropertyName("mensajes")]  public List<ChatMensajeWire>? Mensajes { get; set; }
}

/// <summary>Resultado de un GET: Excepcion != null = ni siquiera respondió el
/// host local (o el panel se estaba cerrando → Cancelado).</summary>
public sealed class ChatEstadoResultado
{
    public ChatEstadoWire? Estado { get; set; }
    public string? Excepcion { get; set; }
    public bool Cancelado { get; set; }
}

public sealed class ChatMensajesResultado
{
    public ChatMensajesWire? Datos { get; set; }
    public string? Excepcion { get; set; }
    public bool Cancelado { get; set; }
}

public sealed class ChatEnviarResultado
{
    public bool Ok { get; set; }
    public string? Excepcion { get; set; }
    public bool Cancelado { get; set; }
}

public sealed class ChatPropuestaResultado
{
    public bool Ok { get; set; }
    public string? Estado { get; set; }   // "aplicada" | "aceptada" | "rechazada"
    public string? Detalle { get; set; }
    public string? Excepcion { get; set; }
    public bool Cancelado { get; set; }
}

public sealed class ChatPanelClient
{
    // Timeout INFINITO a propósito: el corte real lo pone CancellationToken.
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan Corte = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl;

    public ChatPanelClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public string BaseUrl => _baseUrl;

    /// <summary>GET /api/chat/estado — no marca leído ni acelera el poll. Para
    /// el badge del menú cuando el chat está cerrado.</summary>
    public async Task<ChatEstadoResultado> EstadoAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            var e = await LeerAsync<ChatEstadoWire>("api/chat/estado", corte.Token).ConfigureAwait(false);
            return new ChatEstadoResultado { Estado = e };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ChatEstadoResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new ChatEstadoResultado { Excepcion = Motivo(ex) };
        }
    }

    /// <summary>GET /api/chat/mensajes — la conversación entera. El Engine marca
    /// todo leído y acelera el poll al cloud (el operario está mirando).</summary>
    public async Task<ChatMensajesResultado> MensajesAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            var d = await LeerAsync<ChatMensajesWire>("api/chat/mensajes", corte.Token).ConfigureAwait(false);
            return new ChatMensajesResultado { Datos = d };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ChatMensajesResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new ChatMensajesResultado { Excepcion = Motivo(ex) };
        }
    }

    /// <summary>POST /api/chat/enviar con { texto }. El Engine lo relaya al cloud
    /// y agrega el eco local; el próximo MensajesAsync ya lo trae.</summary>
    public async Task<ChatEnviarResultado> EnviarAsync(string texto, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);

            string body = "{\"texto\":" + JsonSerializer.Serialize(texto ?? "") + "}";
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/chat/enviar", contenido, corte.Token)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(Limpio(json));
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl)
                      && okEl.ValueKind == JsonValueKind.True;
            return new ChatEnviarResultado { Ok = ok };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ChatEnviarResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new ChatEnviarResultado { Excepcion = Motivo(ex) };
        }
    }

    /// <summary>POST /api/chat/propuesta con { ts, decision }. El Engine aplica
    /// los cambios seguros (si decision=="aceptar") y avisa al cloud. Devuelve el
    /// estado final ("aplicada"|"aceptada"|"rechazada") y el detalle.</summary>
    public async Task<ChatPropuestaResultado> ResolverPropuestaAsync(long ts, string decision, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);

            string body = "{\"ts\":" + ts.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          ",\"decision\":" + JsonSerializer.Serialize(decision ?? "") + "}";
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/chat/propuesta", contenido, corte.Token)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(Limpio(json));
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string? estado = root.TryGetProperty("estado", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            string? detalle = root.TryGetProperty("detalle", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return new ChatPropuestaResultado { Ok = ok, Estado = estado, Detalle = detalle };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ChatPropuestaResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return new ChatPropuestaResultado { Excepcion = Motivo(ex) };
        }
    }

    /// <summary>Abre/cierra el teclado virtual propio de PilotX (misma señal
    /// que mandan las páginas HTML). Catch mudo: sin teclado nativo el campo
    /// sigue editable con el físico.</summary>
    public async Task TecladoAsync(bool abrir, string titulo = "", bool numerico = false)
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

    private async Task<T?> LeerAsync<T>(string ruta, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + ruta);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                    .ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(Limpio(json), _jsonOpts);
    }

    private static string Motivo(Exception ex)
    {
        if (ex is OperationCanceledException) return "el servicio local no respondió";
        return ex.Message;
    }

    private static string Limpio(string json)
        => (json ?? "").TrimStart('﻿', '​').TrimStart();
}
