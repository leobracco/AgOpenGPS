// UsbFlashClient.cs
//
// Cliente HTTP del flasheo de firmware ESP32 (QuantiX/VistaX/SectionX/etc.)
// por cable USB directo desde la cabina, sin PC externa ni internet. Habla
// con UsbFlashController (AgroParallel.WebHost):
//
//   GET  /api/usb/puertos          -> puertos COM visibles
//   GET  /api/usb/flash/estado     -> polling del progreso (en_curso/fase/pct/...)
//   POST /api/usb/flash            -> inicia el flasheo (background en el server)
//   POST /api/usb/driver/instalar  -> instala driver USB-serial (CP210x/CH340, UAC)
//
// PilotX.UI es library portable (net9.0 puro, SIN ProjectReference a
// AgroParallel.Models/Services — ver PilotX.UI.csproj) asi que NO se reusan
// PuertoComDto/UsbFlashEstadoDto/UsbFlashRequest/UsbDriverRequest de
// AgroParallel.Models: se redeclaran aca como DTOs de wire, mismo shape
// snake_case, mismo estilo que FirmwaresClient — [JsonPropertyName] EXPLICITO
// en cada propiedad porque el case-insensitive de System.Text.Json NO cubre
// underscores (mismo cuidado que SectionXClient/NodosClient/UpdateClient).
//
// Timeout largo (20 s) para TODO el cliente, no solo el polling: el driver
// installer corre pnputil con WaitForExit() sin timeout propio (puede haber
// UAC de por medio), asi que un Timeout corto de "lectura rapida" lo cortaria
// a mitad de instalacion (mismo criterio que RedIpClient con el helper SYSTEM).
//
// TRAMPA (ver memoria feedback_httpclient_timeout_kills_polling): HttpClient
// .Timeout dispara TaskCanceledException, que ES OperationCanceledException.
// EstadoAsync lo llama el panel en loop mientras hay un flasheo en curso — si
// un catch generico tratara ese timeout igual que una cancelacion real
// (ct.IsCancellationRequested), el primer GET lento cortaria el polling para
// siempre y la UI quedaria congelada mostrando el ultimo estado. Por eso el
// catch especifico de cancelacion real va PRIMERO con `when
// (ct.IsCancellationRequested)`, y el catch generico despues (ahi cae el
// timeout: se trata como "sin dato todavia", el llamador reintenta en el
// proximo tick) — mismo idioma que usa HudPoller.RunLoopAsync.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Un puerto COM disponible en la PC, para elegir a que nodo flashear.</summary>
public sealed class UsbPuertoDto
{
    [JsonPropertyName("port")]        public string? Port { get; set; }
    [JsonPropertyName("descripcion")] public string? Descripcion { get; set; }
}

/// <summary>Pedido de flasheo: que producto/version grabar, por que puerto y en que modo.</summary>
public sealed class UsbFlashRequest
{
    [JsonPropertyName("producto")]     public string? Producto { get; set; }
    [JsonPropertyName("version")]      public string? Version { get; set; }
    [JsonPropertyName("puerto")]       public string? Puerto { get; set; }
    [JsonPropertyName("modo")]         public string? Modo { get; set; }        // "completo" | "app"
    [JsonPropertyName("borrar_antes")] public bool    BorrarAntes { get; set; }
}

/// <summary>Estado en vivo del flasheo en curso — lo que trae el polling.</summary>
public sealed class UsbFlashEstadoDto
{
    [JsonPropertyName("en_curso")]  public bool    EnCurso { get; set; }
    [JsonPropertyName("fase")]      public string? Fase { get; set; }      // conectando|borrando|escribiendo|verificando|reset|idle
    [JsonPropertyName("pct")]       public int     Pct { get; set; }
    [JsonPropertyName("resultado")] public string? Resultado { get; set; } // null | "ok" | "fail"
    [JsonPropertyName("codigo")]    public string? Codigo { get; set; }    // null | "AGP-USB-00X"
    [JsonPropertyName("log")]       public string? Log { get; set; }
}

public sealed class UsbFlashClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public UsbFlashClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Codigo AGP-USB-*** del ultimo fallo de FlashAsync/InstalarDriverAsync
    /// (null si el ultimo pedido salio bien o todavia no se llamo nada). El
    /// panel lo muestra junto al mensaje amigable — convencion AGP-* del resto
    /// de PilotX: codigo dictable por telefono, detalle aparte.</summary>
    public string? UltimoErrorCodigo { get; private set; }

    /// <summary>Mensaje amigable del ultimo fallo (el `mensaje` que arma
    /// WriteErrorAsync en el controller). Null si no hubo error.</summary>
    public string? UltimoErrorMensaje { get; private set; }

    private sealed class PuertosResponse
    {
        [JsonPropertyName("ok")]      public bool Ok { get; set; }
        [JsonPropertyName("puertos")] public List<UsbPuertoDto>? Puertos { get; set; }
    }

    /// <summary>Shape comun de las respuestas de accion: `{ok:true}` si salio
    /// bien, o el `{error,mensaje,detalle}` de WriteErrorAsync si fallo.</summary>
    private sealed class OpResponse
    {
        [JsonPropertyName("ok")]      public bool    Ok      { get; set; }
        [JsonPropertyName("error")]   public string? Error   { get; set; }   // codigo AGP-USB-***
        [JsonPropertyName("mensaje")] public string? Mensaje { get; set; }   // amigable
    }

    // ---------------------------------------------------------------- puertos

    /// <summary>GET /api/usb/puertos. Lista vacia si el Hub no respondio
    /// (nunca null: el panel no tiene que manejar dos formas de "sin datos").</summary>
    public async Task<IReadOnlyList<UsbPuertoDto>> PuertosAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/usb/puertos", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<UsbPuertoDto>();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<PuertosResponse>(json, _jsonOpts);
            return (r != null && r.Ok && r.Puertos != null) ? r.Puertos : Array.Empty<UsbPuertoDto>();
        }
        catch { return Array.Empty<UsbPuertoDto>(); }
    }

    // ----------------------------------------------------------------- estado

    /// <summary>GET /api/usb/flash/estado. El panel lo llama en loop mientras
    /// hay un flasheo en curso. Null = sin dato todavia (timeout del Hub o
    /// respuesta ilegible) — el llamador reintenta en el proximo tick, jamas
    /// lo interpreta como "se cancelo el flasheo" (ver TRAMPA arriba).</summary>
    public async Task<UsbFlashEstadoDto?> EstadoAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/usb/flash/estado", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<UsbFlashEstadoDto>(json, _jsonOpts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelacion real (el operario cerro el panel): no hay a quien
            // devolverle el dato.
            return null;
        }
        catch (Exception)
        {
            // Aca cae TAMBIEN el timeout de HttpClient (TaskCanceledException
            // con ct SIN cancelar): se trata igual que cualquier otro fallo de
            // red — "sin dato, reintentar" — nunca como el `when` de arriba,
            // que confundiria el timeout con un cierre del panel y cortaria el
            // polling para siempre.
            return null;
        }
    }

    // ------------------------------------------------------------------ flash

    /// <summary>POST /api/usb/flash. El server lo corre en background (esta
    /// llamada solo dispara el inicio); el progreso se sigue con EstadoAsync.
    /// false = no arranco (puerto ocupado, .bin faltante, etc.) — mirar
    /// UltimoErrorCodigo/UltimoErrorMensaje.</summary>
    public async Task<bool> FlashAsync(UsbFlashRequest request, CancellationToken ct = default)
    {
        var r = await PostAsync("api/usb/flash", request, ct).ConfigureAwait(false);
        return AplicarResultado(r);
    }

    // ----------------------------------------------------------------- driver

    /// <summary>POST /api/usb/driver/instalar. Instala el driver USB-serial
    /// ("cp210x" | "ch340" | "ambos") via pnputil en el server; puede tardar
    /// (UAC de por medio), por eso el cliente entero usa un Timeout largo.</summary>
    public async Task<bool> InstalarDriverAsync(string driver, CancellationToken ct = default)
    {
        var r = await PostAsync("api/usb/driver/instalar", new { driver = driver ?? "ambos" }, ct)
                    .ConfigureAwait(false);
        return AplicarResultado(r);
    }

    // ----------------------------------------------------------------- helpers

    private async Task<OpResponse?> PostAsync(string ruta, object body, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(body);
            using var contenido = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + ruta, contenido, ct).ConfigureAwait(false);
            var texto = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Los errores TAMBIEN traen JSON en el body ({error,mensaje,detalle}
            // de WriteErrorAsync): se deserializa igual sin mirar el status
            // primero, asi UltimoErrorCodigo/Mensaje quedan poblados aunque el
            // POST haya devuelto 400/404/409/500.
            return JsonSerializer.Deserialize<OpResponse>(texto, _jsonOpts);
        }
        catch { return null; }
    }

    private bool AplicarResultado(OpResponse? r)
    {
        if (r != null && r.Ok)
        {
            UltimoErrorCodigo = null;
            UltimoErrorMensaje = null;
            return true;
        }
        UltimoErrorCodigo = r?.Error;
        UltimoErrorMensaje = r?.Mensaje;
        return false;
    }
}
