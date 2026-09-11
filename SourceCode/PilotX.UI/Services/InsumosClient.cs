// ============================================================================
// InsumosClient.cs — cliente HTTP del CATÁLOGO COMPARTIDO de insumos.
//
// Lo consume InsumosPanel (port nativo de pages/insumos.html + js/insumos.js).
// El catálogo es COMPARTIDO: VistaX lo abre desde su editor ("Catálogo de
// insumos"), QuantiX lo usa como dosis, FlowX como L/ha y los datos del lote
// para el ahorro estimado. Por eso el contrato NO se toca:
//
//   GET  /api/insumos          → el catálogo entero { version, activo_id, items }
//   POST /api/insumos          (body = el catálogo ENTERO) → { ok }
//   POST /api/insumos/activo   (body = { id })             → { ok, activo }
//
// Trampas del wire cubiertas acá:
//   · snake_case en CADA propiedad ([JsonPropertyName]): activo_id,
//     densidad_objetivo_sem_m, dosis_kgha, precio_usd_kg… El
//     PropertyNameCaseInsensitive de System.Text.Json NO cubre underscores y el
//     DTO deserializaría ceros en silencio.
//   · [JsonExtensionData] en el ítem y en el catálogo: recoge los campos que el
//     backend agregue y este cliente no modele, así que viajan de vuelta en el
//     POST en vez de perderse en el camino de ida.
//     OJO — eso NO garantiza que sobrevivan al guardado: el server deserializa
//     el body a InsumoCatalogDto / InsumoDto, que NO tienen extension data, así
//     que lo que ellos no modelan se descarta ahí. Hoy es inocuo (los DTO del
//     server y los de acá modelan lo mismo); si mañana el celular guarda un
//     campo nuevo, para que sobreviva hay que modelarlo en el server, no acá.
//   · BOM de EmbedIO: las respuestas pueden arrancar con el BOM U+FEFF y sin
//     recortarlo el Deserialize tira.
//   · Sin HttpClient.Timeout: la cancelación es por CancellationToken (con
//     .Timeout, un TaskCanceledException sin `when (ct.IsCancellationRequested)`
//     congela la UI en sus defaults).
//
// La página HTML NO se toca: el Hub remoto y la PWA del celular la siguen
// usando.
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

/// <summary>Un insumo del catálogo. Espejo EXACTO de AgroParallel.Models.InsumoDto
/// (mismo wire, mismos defaults).</summary>
public sealed class InsumoWire
{
    [JsonPropertyName("id")]      public string  Id      { get; set; } = "";
    [JsonPropertyName("nombre")]  public string  Nombre  { get; set; } = "";
    /// <summary>"semilla" | "fertilizante" | "fitosanitario".</summary>
    [JsonPropertyName("tipo")]    public string  Tipo    { get; set; } = "semilla";
    [JsonPropertyName("cultivo")] public string  Cultivo { get; set; } = "";

    // ---- VistaX (solo si tipo=="semilla") ---------------------------------
    [JsonPropertyName("densidad_objetivo_sem_m")]         public double DensidadObjetivoSemM        { get; set; }
    [JsonPropertyName("densidad_asumida_saturado_sem_m")] public double DensidadAsumidaSaturadoSemM { get; set; }
    [JsonPropertyName("singulacion_objetivo_pct")]        public double SingulacionObjetivoPct      { get; set; } = 97.0;
    [JsonPropertyName("drop_min_sem_m")]                  public double DropMinSemM                 { get; set; }
    [JsonPropertyName("drop_max_sem_m")]                  public double DropMaxSemM                 { get; set; }

    // ---- QuantiX / VistaX (densidad de siembra compartida) ----------------
    /// <summary>Valor de la densidad, en la unidad de <see cref="DosisUnidad"/>.
    /// El nombre del campo es legacy de cuando era solo kg/ha — NO se renombra:
    /// lo leen QuantiX, VistaX y los datos del lote.</summary>
    [JsonPropertyName("dosis_kgha")]   public double DosisKgha   { get; set; }
    /// <summary>"kg_ha" (default) | "sem_ha" | "sem_m".</summary>
    [JsonPropertyName("dosis_unidad")] public string DosisUnidad { get; set; } = "kg_ha";

    // ---- FlowX (líquido) ---------------------------------------------------
    [JsonPropertyName("dosis_lha")] public double DosisLha { get; set; }

    // ---- Económicos --------------------------------------------------------
    [JsonPropertyName("precio_usd_kg")] public double PrecioUsdKg { get; set; }
    [JsonPropertyName("precio_usd_l")]  public double PrecioUsdL  { get; set; }

    // ---- Libre -------------------------------------------------------------
    [JsonPropertyName("notas")] public string Notas { get; set; } = "";

    /// <summary>Todo lo que el backend agregue y este DTO no modele. El JS
    /// posteaba de vuelta el objeto tal cual le llegó: sin esto, guardar desde
    /// la cabina PISARÍA esos campos con nada.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>El archivo entero (insumos.json) tal cual viaja por el wire.</summary>
public sealed class InsumoCatalogoWire
{
    [JsonPropertyName("version")]   public string Version  { get; set; } = "1.0";
    /// <summary>ID del insumo activo. "" = ninguno.</summary>
    [JsonPropertyName("activo_id")] public string ActivoId { get; set; } = "";
    [JsonPropertyName("items")]     public List<InsumoWire> Items { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class InsumosClient
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

    private static readonly JsonSerializerOptions _jsonOut = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Corte de las lecturas/escrituras cortas del catálogo.</summary>
    private static readonly TimeSpan Corte = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl;

    public InsumosClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Base del Hub (la usa el panel para el teclado nativo).</summary>
    public string BaseUrl => _baseUrl;

    // ------------------------------------------------------------------ load

    /// <summary>GET /api/insumos. null = falló la red o el body es ilegible —
    /// el JS solo hacía console.warn y renderizaba con lo que tenía.</summary>
    public async Task<InsumoCatalogoWire?> GetCatalogoAsync(CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            using var resp = await _http.GetAsync(_baseUrl + "api/insumos", corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<InsumoCatalogoWire>(Limpio(json), _jsonOpts);
            if (dto == null) return null;
            // `if (!Array.isArray(state.catalogo.items)) state.catalogo.items = []`
            dto.Items ??= new List<InsumoWire>();
            return dto;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;   // console.warn('[insumos] load:', e) — la UI no explota
        }
    }

    // ------------------------------------------------------------------ save

    /// <summary>POST /api/insumos con el catálogo ENTERO (persist() del JS).
    /// false = el backend contestó `ok:false` o falló la red.</summary>
    public async Task<bool> GuardarCatalogoAsync(InsumoCatalogoWire catalogo, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            string body = JsonSerializer.Serialize(catalogo, _jsonOut);
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/insumos", contenido, corte.Token)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(Limpio(json));
            // `if (!res || res.ok === false) console.warn(...)`
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.False) return false;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch { return false; }
    }

    /// <summary>POST /api/insumos/activo con { id }. id="" deselecciona.
    /// El JS actualiza su activo_id local pase lo que pase salvo excepción.</summary>
    public async Task<bool> SetActivoAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            string body = "{\"id\":" + JsonSerializer.Serialize(id ?? "") + "}";
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_baseUrl + "api/insumos/activo", contenido, corte.Token)
                                     .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- teclado

    /// <summary>Abre/cierra la ventana del teclado nativo de PilotX. Misma señal
    /// HTTP que mandan las páginas del Hub (keyboard.js). Catch mudo a propósito:
    /// sin teclado nativo el campo sigue editable con teclado físico.</summary>
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

    // El BOM de EmbedIO deja el JSON arrancando con U+FEFF y System.Text.Json no
    // lo perdona. Los caracteres van por código a propósito: como literales
    // serían invisibles en el fuente.
    private static string Limpio(string json)
        => (json ?? "").TrimStart((char)0xFEFF, (char)0x200B).TrimStart();
}
