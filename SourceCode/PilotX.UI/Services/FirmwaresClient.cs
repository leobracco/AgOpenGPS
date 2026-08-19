// ============================================================================
// FirmwaresClient.cs — cliente HTTP del cache LOCAL de firmwares.
//
// Lo consume FirmwaresPanel (port nativo de pages/firmwares.html). Wire
// IDÉNTICO al que usaba la página + su firmwares.js — cero cambio de contrato,
// porque la página HTML sigue viva para el Hub remoto / la PWA del celular:
//
//   GET    /api/firmwares                        → catálogo agrupado por producto
//   POST   /api/firmwares/upload                 → sube un .bin (BYTES CRUDOS,
//          headers: X-AP-Producto, X-AP-Version, X-AP-Changelog-B64 (opcional,
//          Base64 de UTF-8 — ver SubirAsync: .NET no deja mandar acentos en un
//          header y la página HTML sigue usando el X-AP-Changelog viejo)
//          Content-Type: application/octet-stream — NO multipart)
//   DELETE /api/firmwares/{producto}/{version}   → borra del cache
//
// Trampas del wire cubiertas acá:
//   · snake_case: hash_sha256 / tamano_bytes / cache_dir / lan_ip / http_port /
//     max_bytes NO matchean con PropertyNameCaseInsensitive (el case-insensitive
//     de System.Text.Json no cubre underscores) — va [JsonPropertyName] en CADA
//     propiedad.
//   · BOM de EmbedIO: las respuestas pueden arrancar con el BOM U+FEFF y sin
//     TrimStart el Deserialize tira (mismo cuidado que SonidosClient).
//   · Sin HttpClient.Timeout: la cancelación es por CancellationToken. El
//     upload puede ser de 8 MB y un Timeout de 5 s lo cortaría a mitad de
//     camino dejando un .part en el disco del Hub.
//
// Los guards de seguridad (cap de 8 MB, mínimo 1 KB, regex de producto/versión)
// están DUPLICADOS a propósito: acá adelantan el feedback al operario y el
// FirmwaresController los vuelve a aplicar sobre el stream. Igual que el JS.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>Una versión del cache (o del catálogo cloud todavía sin bajar).</summary>
public sealed class FirmwareVersionWire
{
    [JsonPropertyName("version")]      public string? Version      { get; set; }
    [JsonPropertyName("hash_sha256")]  public string? HashSha256   { get; set; }
    [JsonPropertyName("tamano_bytes")] public long    TamanoBytes  { get; set; }
    [JsonPropertyName("changelog")]    public string? Changelog    { get; set; }
    /// <summary>Unix ms (manifest.descargado_at). 0 = sin dato.</summary>
    [JsonPropertyName("ts")]           public long    Ts           { get; set; }
    /// <summary>true = el .bin está en disco (se puede borrar y servir por LAN);
    /// false = solo figura en el index.json del cloud.</summary>
    [JsonPropertyName("local")]        public bool    Local        { get; set; }
}

public sealed class FirmwareProductoWire
{
    [JsonPropertyName("producto")]  public string? Producto { get; set; }
    [JsonPropertyName("versiones")] public List<FirmwareVersionWire> Versiones { get; set; } = new();
}

public sealed class FirmwareCatalogoWire
{
    [JsonPropertyName("ok")]         public bool    Ok        { get; set; }
    [JsonPropertyName("error")]      public string? Error     { get; set; }
    [JsonPropertyName("cache_dir")]  public string? CacheDir  { get; set; }
    [JsonPropertyName("lan_ip")]     public string? LanIp     { get; set; }
    [JsonPropertyName("http_port")]  public int     HttpPort  { get; set; }
    [JsonPropertyName("productos")]  public List<FirmwareProductoWire> Productos { get; set; } = new();
}

/// <summary>El JS distingue DOS fallas distintas y las muestra distinto:
/// `!data.ok` → "No se pudo leer el cache" + data.error; excepción de red →
/// "Error de red" + e.message. Sin este envoltorio se perdería esa diferencia.</summary>
public sealed class FirmwareCatalogoResultado
{
    public FirmwareCatalogoWire? Datos { get; set; }
    /// <summary>null = hubo respuesta (mirar Datos.Ok). No-null = falló la red.</summary>
    public string? ErrorRed { get; set; }
}

public sealed class FirmwareUploadWire
{
    [JsonPropertyName("ok")]           public bool    Ok          { get; set; }
    [JsonPropertyName("error")]        public string? Error       { get; set; }
    [JsonPropertyName("producto")]     public string? Producto    { get; set; }
    [JsonPropertyName("version")]      public string? Version     { get; set; }
    [JsonPropertyName("hash_sha256")]  public string? HashSha256  { get; set; }
    [JsonPropertyName("tamano_bytes")] public long    TamanoBytes { get; set; }
    [JsonPropertyName("max_bytes")]    public long    MaxBytes    { get; set; }
    [JsonPropertyName("bytes")]        public long    Bytes       { get; set; }
    [JsonPropertyName("detail")]       public string? Detail      { get; set; }
}

public sealed class FirmwareUploadResultado
{
    /// <summary>Código HTTP crudo: el JS arma "HTTP nnn" cuando el body no trae
    /// `error`. 0 = ni siquiera hubo respuesta.</summary>
    public int Status { get; set; }
    public FirmwareUploadWire? Datos { get; set; }
    /// <summary>No-null = falló la red (xhr.onerror del original).</summary>
    public string? ErrorRed { get; set; }

    /// <summary>Código dictable de la falla (AGP-NET-001…), para que el operario
    /// se lo pueda pasar a soporte por teléfono. Vacío si no hubo excepción.</summary>
    public string? ErrorCodigo { get; set; }
    /// <summary>Mensaje amigable del mapper (el que va arriba, en castellano).</summary>
    public string? ErrorAmigable { get; set; }
    /// <summary>Tipo + mensaje reales de la excepción: va en el desplegable de
    /// detalle técnico. Sin esto, un header inválido o un disco lleno se veían
    /// los dos como "Error de red", que MIENTE cuando la red está sana.</summary>
    public string? ErrorTecnico { get; set; }
}

public sealed class FirmwareBorradoResultado
{
    public bool Ok { get; set; }
    /// <summary>`data.error` del body (el JS lo muestra como "No se pudo borrar: …").</summary>
    public string? Error { get; set; }
    /// <summary>No-null = excepción (el JS lo muestra como "Error: …").</summary>
    public string? ErrorRed { get; set; }
}

public sealed class FirmwaresClient
{
    // Sin Timeout: la cancelación va SIEMPRE por CancellationToken (un upload de
    // 8 MB no entra en los 5 s que usan los clientes de polling).
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Corte para las lecturas cortas (lista / borrado). El upload NO
    /// lo usa: se cancela solo cuando el operario cierra el panel.</summary>
    private static readonly TimeSpan CorteLectura = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl;

    public FirmwaresClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Base del Hub (la usa el panel para el teclado nativo).</summary>
    public string BaseUrl => _baseUrl;

    // ------------------------------------------------------------------ lista

    /// <summary>GET /api/firmwares — catálogo agrupado por producto, ya ordenado
    /// por el server (versión desc). No se reordena acá.</summary>
    public async Task<FirmwareCatalogoResultado> GetCatalogoAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteLectura);
            using var resp = await _http.GetAsync(_baseUrl + "api/firmwares", corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            var datos = JsonSerializer.Deserialize<FirmwareCatalogoWire>(Limpio(json), _jsonOpts);
            // Body ilegible = el JS entraba al catch del fetch ("Error de red").
            if (datos == null)
                return new FirmwareCatalogoResultado { ErrorRed = "respuesta ilegible" };
            return new FirmwareCatalogoResultado { Datos = datos };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El panel se cerró: nadie va a mirar el resultado.
            return new FirmwareCatalogoResultado { ErrorRed = null, Datos = null };
        }
        catch (Exception ex)
        {
            return new FirmwareCatalogoResultado { ErrorRed = Motivo(ex) };
        }
    }

    // ----------------------------------------------------------------- borrar

    /// <summary>DELETE /api/firmwares/{producto}/{version}. Borra el .bin del
    /// disco — el panel SIEMPRE confirma antes de llamar acá.</summary>
    public async Task<FirmwareBorradoResultado> BorrarAsync(string producto, string version,
                                                            CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteLectura);
            string url = _baseUrl + "api/firmwares/"
                       + Uri.EscapeDataString(producto ?? "") + "/"
                       + Uri.EscapeDataString(version ?? "");
            using var resp = await _http.DeleteAsync(url, corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(Limpio(json));
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string? err = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
            return new FirmwareBorradoResultado { Ok = ok, Error = err };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new FirmwareBorradoResultado { Ok = false };
        }
        catch (Exception ex)
        {
            return new FirmwareBorradoResultado { Ok = false, ErrorRed = Motivo(ex) };
        }
    }

    // ----------------------------------------------------------------- upload

    /// <summary>POST /api/firmwares/upload con los BYTES CRUDOS del .bin y los
    /// headers X-AP-Producto / X-AP-Version / X-AP-Changelog-B64. `progreso`
    /// recibe 0..100 mientras se manda el cuerpo (equivalente a
    /// xhr.upload.onprogress).
    ///
    /// El changelog viaja en un HEADER, igual que en el original, pero en
    /// BASE64 de UTF-8 y con nombre propio: .NET RECHAZA los headers con
    /// caracteres no-ASCII ("Request headers must contain only ASCII
    /// characters"), o sea que un changelog con acento o con ñ — lo normal en
    /// una cabina en castellano — hacía fallar el upload ENTERO antes de
    /// mandar un solo byte. En el browser no pasaba porque setRequestHeader
    /// acepta ByteString. El header viejo X-AP-Changelog NO se manda desde acá;
    /// el controller lo sigue leyendo para la página HTML y la PWA, que no
    /// cambian.
    ///
    /// Lo que se guarda en el manifest es el MISMO texto: el Base64 no recorta
    /// ni normaliza nada (tampoco los saltos de línea, que en el original
    /// invalidaban el request).</summary>
    public async Task<FirmwareUploadResultado> SubirAsync(
        string producto, string version, string changelog, byte[] datos,
        Action<double>? progreso = null, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "api/firmwares/upload");
            req.Content = new ContenidoConProgreso(datos ?? Array.Empty<byte>(), progreso);
            req.Headers.TryAddWithoutValidation("X-AP-Producto", producto ?? "");
            req.Headers.TryAddWithoutValidation("X-AP-Version", version ?? "");
            // `if (chg) xhr.setRequestHeader(...)`: vacío = no se manda el header.
            // Base64 de UTF-8 = siempre ASCII, así que el acento/la ñ viajan sin
            // romper el request.
            if (!string.IsNullOrEmpty(changelog))
                req.Headers.TryAddWithoutValidation("X-AP-Changelog-B64",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(changelog)));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            FirmwareUploadWire? cuerpo = null;
            try { cuerpo = JsonSerializer.Deserialize<FirmwareUploadWire>(Limpio(json), _jsonOpts); }
            catch { /* JSON.parse dentro de try{}catch{} en el original: queda null */ }
            return new FirmwareUploadResultado
            {
                Status = (int)resp.StatusCode,
                Datos = cuerpo
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new FirmwareUploadResultado { Status = 0 };
        }
        catch (Exception ex)
        {
            // El mapper le pone código + mensaje amigable a la falla y conserva
            // el detalle técnico aparte (convención AGP-*): el panel muestra
            // los tres, en vez de tragarse ex.Message.
            var e = AgroParallel.Services.AgpErrorMapper.FromException(ex);
            return new FirmwareUploadResultado
            {
                Status = 0,
                ErrorRed = Motivo(ex),
                ErrorCodigo = e.Code,
                ErrorAmigable = e.Friendly,
                ErrorTecnico = e.Technical
            };
        }
    }

    // ---------------------------------------------------------------- teclado

    /// <summary>Abre/cierra la ventana del teclado nativo de PilotX. Misma señal
    /// que mandan las páginas HTML (keyboard.js). Catch mudo a propósito: sin
    /// teclado nativo el campo sigue editable con teclado físico.</summary>
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

    // ---------------------------------------------------------------- helpers

    private static string Motivo(Exception ex)
    {
        // Un corte por CorteLectura llega acá como OperationCanceledException sin
        // mensaje útil: se traduce a algo que el operario pueda dictar.
        if (ex is OperationCanceledException) return "el Hub no respondió";
        return ex.Message;
    }

    // El BOM de EmbedIO deja el JSON arrancando con U+FEFF y System.Text.Json no
    // lo perdona (escapes explícitos: como literales serían invisibles acá).
    private static string Limpio(string json) => (json ?? "").TrimStart('\uFEFF', '\u200B').TrimStart();

    /// <summary>Cuerpo binario que reporta avance mientras se escribe. Reemplaza
    /// a xhr.upload.onprogress (HttpClient no expone progreso de subida).</summary>
    private sealed class ContenidoConProgreso : HttpContent
    {
        private const int Bloque = 64 * 1024;
        private readonly byte[] _datos;
        private readonly Action<double>? _progreso;

        public ContenidoConProgreso(byte[] datos, Action<double>? progreso)
        {
            _datos = datos;
            _progreso = progreso;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            int total = _datos.Length;
            int enviado = 0;
            if (total == 0) { _progreso?.Invoke(100); return; }
            while (enviado < total)
            {
                int n = Math.Min(Bloque, total - enviado);
                await stream.WriteAsync(_datos.AsMemory(enviado, n)).ConfigureAwait(false);
                enviado += n;
                try { _progreso?.Invoke(enviado * 100.0 / total); } catch { }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _datos.Length;
            return true;
        }
    }
}
