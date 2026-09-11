// ============================================================================
// RedWifiClient.cs — cliente HTTP del WiFi PROPIO de PilotX.
//
// Lo consume WifiPanel (port nativo de pages/wifi.html + js/wifi.js). Wire
// IDÉNTICO al de la página — cero cambio de contrato, porque wifi.html sigue
// viva para el Hub remoto / la PWA del celular:
//
//   GET  /api/red/wifi              → { ok, estado:{conectado,ssid,ip},
//                                       redes:[{ssid,senal_pct,segura,conectada}] }
//   POST /api/red/wifi/conectar     ← { ssid, clave }  → { ok, error?, estado }
//   POST /api/red/wifi/desconectar  → { ok, error? }
//
// EL ESCANEO FORZADO NO SE SALTEA. El fix histórico (8e25bf16, "aparecían solo
// las redes ya conectadas") vive del lado del servicio: RedWifiController.Listar
// llama a IWifiService.Escanear(), y WifiServiceWindows.Escanear() arranca
// SIEMPRE con ForzarScanNativo() (WlanScan + 3 s de espera, throttle de 30 s)
// antes de parsear `netsh wlan show networks`. Este cliente pega al MISMO GET
// /api/red/wifi que usaba el JS, así que hereda el escaneo tal cual: NO existe
// ni se usa ninguna variante "solo listar" ni cache local de redes.
//
// Consecuencia práctica: ese GET puede tardar >3 s (el scan nativo duerme 3 s
// cuando no está throttleado). Por eso el HttpClient va SIN Timeout — la
// cancelación es SIEMPRE por CancellationToken (un Timeout de 5 s cortaría el
// primer escaneo del panel y la lista saldría vacía "sin motivo").
//
// Trampas del wire cubiertas acá:
//   · snake_case: senal_pct NO matchea con PropertyNameCaseInsensitive (el
//     case-insensitive de System.Text.Json no cubre underscores) → va
//     [JsonPropertyName] explícito en CADA propiedad.
//   · BOM de EmbedIO: las respuestas pueden arrancar con U+FEFF y sin TrimStart
//     el Deserialize tira (mismo cuidado que SonidosClient/FirmwaresClient).
//   · SSID y clave con ñ/acentos viajan en el CUERPO JSON, nunca en headers:
//     .NET rechaza headers no-ASCII ("Request headers must contain only ASCII
//     characters") y el original ya los mandaba en el body — no se cambia nada.
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

/// <summary>Una red del aire tal cual la manda el Hub.</summary>
public sealed class WifiRedWire
{
    [JsonPropertyName("ssid")]      public string? Ssid      { get; set; }
    [JsonPropertyName("senal_pct")] public int     SenalPct  { get; set; }
    [JsonPropertyName("segura")]    public bool    Segura    { get; set; }
    [JsonPropertyName("conectada")] public bool    Conectada { get; set; }
    [JsonPropertyName("guardada")]  public bool    Guardada  { get; set; }
}

/// <summary>Estado de la interfaz WiFi del equipo.</summary>
public sealed class WifiEstadoWire
{
    [JsonPropertyName("conectado")] public bool    Conectado { get; set; }
    [JsonPropertyName("ssid")]      public string? Ssid      { get; set; }
    [JsonPropertyName("ip")]        public string? Ip        { get; set; }
}

public sealed class WifiListaWire
{
    [JsonPropertyName("ok")]     public bool    Ok     { get; set; }
    [JsonPropertyName("error")]  public string? Error  { get; set; }
    [JsonPropertyName("estado")] public WifiEstadoWire? Estado { get; set; }
    [JsonPropertyName("redes")]  public List<WifiRedWire> Redes { get; set; } = new();
}

public sealed class WifiConectarWire
{
    [JsonPropertyName("ok")]     public bool    Ok    { get; set; }
    [JsonPropertyName("error")]  public string? Error { get; set; }
    [JsonPropertyName("estado")] public WifiEstadoWire? Estado { get; set; }
}

public sealed class WifiAccionWire
{
    [JsonPropertyName("ok")]    public bool    Ok    { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Falla de red con código dictable (AGP-NET-*/AGP-SYS-*). El JS solo
/// entraba al catch del fetch y pintaba un texto fijo; acá ese MISMO texto sigue
/// arriba y se le suma el código + el detalle técnico plegado, que es la
/// convención del repo para que el operario pueda dictarlo por teléfono.</summary>
public class WifiFallaRed
{
    /// <summary>true = la request se canceló porque el panel se cerró: no hay
    /// nada que pintar (no es un error para el operario).</summary>
    public bool Cancelado { get; set; }
    /// <summary>No-null = excepción de red (equivale al catch del fetch).</summary>
    public string? ErrorRed { get; set; }
    public string? ErrorCodigo { get; set; }
    public string? ErrorAmigable { get; set; }
    public string? ErrorTecnico { get; set; }
}

public sealed class WifiListaResultado : WifiFallaRed
{
    /// <summary>Cuerpo de la respuesta. null = ni siquiera hubo JSON legible.</summary>
    public WifiListaWire? Datos { get; set; }
}

public sealed class WifiConectarResultado : WifiFallaRed
{
    public WifiConectarWire? Datos { get; set; }
}

public sealed class WifiAccionResultado : WifiFallaRed
{
    public WifiAccionWire? Datos { get; set; }
}

public sealed class RedWifiClient
{
    // SIN Timeout: la cancelación va SIEMPRE por CancellationToken. El GET
    // dispara un escaneo nativo que puede dormir 3 s y el POST /conectar
    // bloquea hasta 15 s confirmando contra el estado de la interfaz.
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Corte del listado. 30 s: el escaneo nativo duerme 3 s y `netsh`
    /// corre adentro de un cmd (WaitForExit de 20 s en el servicio).</summary>
    private static readonly TimeSpan CorteListar = TimeSpan.FromSeconds(30);

    /// <summary>Corte del conectar. El servicio reintenta el estado 15 veces
    /// con 1 s de espera; 45 s deja margen para el add profile + el connect.</summary>
    private static readonly TimeSpan CorteConectar = TimeSpan.FromSeconds(45);

    private readonly string _baseUrl;

    public RedWifiClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Base del Hub (la usa el panel para el teclado nativo).</summary>
    public string BaseUrl => _baseUrl;

    // ------------------------------------------------------------------ lista

    /// <summary>GET /api/red/wifi — dispara el escaneo nativo del lado del
    /// servicio y devuelve estado + redes. No se reordena nada acá: el orden
    /// (conectada primero, después por señal) lo pone WifiServiceWindows.</summary>
    public async Task<WifiListaResultado> GetRedesAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteListar);
            using var resp = await _http.GetAsync(_baseUrl + "api/red/wifi", corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            var datos = JsonSerializer.Deserialize<WifiListaWire>(Limpio(json), _jsonOpts);
            if (datos == null)
                return Falla<WifiListaResultado>(new InvalidOperationException("respuesta ilegible"));
            return new WifiListaResultado { Datos = datos };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new WifiListaResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return Falla<WifiListaResultado>(ex);
        }
    }

    // --------------------------------------------------------------- conectar

    /// <summary>POST /api/red/wifi/conectar ← { ssid, clave }. Bloquea hasta que
    /// el servicio confirma (o se rinde a los ~15 s): es el mismo POST que el
    /// operario dispara una vez y espera mirando "Conectando…".
    ///
    /// El SSID y la clave van en el CUERPO, igual que en el original: con
    /// acentos/ñ un header los rompería en .NET.</summary>
    public async Task<WifiConectarResultado> ConectarAsync(string ssid, string clave,
                                                           CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteConectar);
            string cuerpo = "{\"ssid\":" + JsonSerializer.Serialize(ssid ?? "") +
                            ",\"clave\":" + JsonSerializer.Serialize(clave ?? "") + "}";
            using var contenido = new StringContent(cuerpo, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/red/wifi/conectar", contenido, corte.Token)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            var datos = JsonSerializer.Deserialize<WifiConectarWire>(Limpio(json), _jsonOpts);
            if (datos == null)
                return Falla<WifiConectarResultado>(new InvalidOperationException("respuesta ilegible"));
            return new WifiConectarResultado { Datos = datos };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new WifiConectarResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return Falla<WifiConectarResultado>(ex);
        }
    }

    // ------------------------------------------------------------ desconectar

    /// <summary>POST /api/red/wifi/desconectar. El JS ni mira la respuesta
    /// (`.then(() => …)`): pase lo que pase vuelve a listar. Se devuelve igual
    /// por si el panel quisiera contarlo — hoy no lo usa, a propósito.</summary>
    public async Task<WifiAccionResultado> DesconectarAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteListar);
            using var contenido = new StringContent("", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/red/wifi/desconectar", contenido, corte.Token)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            var datos = JsonSerializer.Deserialize<WifiAccionWire>(Limpio(json), _jsonOpts);
            return new WifiAccionResultado { Datos = datos };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new WifiAccionResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return Falla<WifiAccionResultado>(ex);
        }
    }

    // ------------------------------------------------------------- olvidar red

    /// <summary>POST /api/red/wifi/olvidar — borra el perfil guardado (clave +
    /// auto-conexión) de una red.</summary>
    public async Task<WifiAccionResultado> OlvidarAsync(string ssid, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteListar);
            string cuerpo = "{\"ssid\":" + JsonSerializer.Serialize(ssid ?? "") + "}";
            using var contenido = new StringContent(cuerpo, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/red/wifi/olvidar", contenido, corte.Token)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            var datos = JsonSerializer.Deserialize<WifiAccionWire>(Limpio(json), _jsonOpts);
            return new WifiAccionResultado { Datos = datos };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new WifiAccionResultado { Cancelado = true };
        }
        catch (Exception ex)
        {
            return Falla<WifiAccionResultado>(ex);
        }
    }

    // ---------------------------------------------------------------- teclado

    /// <summary>Abre/cierra la ventana del teclado nativo de PilotX. Misma señal
    /// que manda keyboard.js desde la página (en wifi.html el input de clave es
    /// type="password" → teclado qwerty). Catch mudo a propósito: sin teclado
    /// nativo el campo sigue editable con teclado físico.</summary>
    public async Task TecladoAsync(bool abrir, bool numerico = false, string titulo = "")
    {
        try
        {
            string cuerpo = abrir
                ? "{\"numerico\":" + (numerico ? "true" : "false") +
                  ",\"titulo\":" + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var contenido = new StringContent(cuerpo, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(
                _baseUrl + "api/teclado/" + (abrir ? "abrir" : "cerrar"), contenido).ConfigureAwait(false);
        }
        catch { }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Arma el resultado de falla con el código AGP correspondiente.
    /// El mapper es el MISMO de todo el ecosistema (archivo enlazado en el
    /// csproj): el número que el operario dicta significa lo mismo salga de
    /// donde salga.</summary>
    private static T Falla<T>(Exception ex) where T : WifiFallaRed, new()
    {
        var e = AgroParallel.Services.AgpErrorMapper.FromException(ex);
        return new T
        {
            ErrorRed = Motivo(ex),
            ErrorCodigo = e.Code,
            ErrorAmigable = e.Friendly,
            ErrorTecnico = e.Technical,
        };
    }

    private static string Motivo(Exception ex)
    {
        // Un corte por CorteListar/CorteConectar llega acá como
        // OperationCanceledException sin mensaje útil.
        if (ex is OperationCanceledException) return "el Hub no respondió";
        return ex.Message;
    }

    // El BOM de EmbedIO deja el JSON arrancando con U+FEFF y System.Text.Json no
    // lo perdona (escapes explícitos: como literales serían invisibles acá).
    private static string Limpio(string json) => (json ?? "").TrimStart('\uFEFF', '\u200B').TrimStart();
}
