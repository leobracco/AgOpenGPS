// ============================================================================
// CalculadoraSiembraClient.cs — wire de la Calculadora de siembra nativa.
//
// QUÉ QUEDÓ NATIVO: lo único que la página pages/calculadora-siembra.html hacía
// por fetch, en el MISMO orden que su cargarContexto():
//
//     1) GET /api/implemento        — el JS lo guarda en state.impl
//     2) GET /api/tool              — ancho + secciones (= surcos) de PilotX
//     3) GET /api/quantix/motores   — motores configurados para el combo
//
// más el POST /api/teclado/abrir|cerrar del teclado nativo (misma señal HTTP
// que mandan las páginas del Hub).
//
// QUÉ SIGUE EN HTML: la página y su JS quedan intactos — los usa la PWA del
// celular. Acá NO se agregó ni se sacó ninguna llamada: la calculadora es toda
// cuenta local, el wire solo precarga la máquina real.
//
// OJO CON EL CASING: /api/quantix/motores e /api/implemento salen snake_case
// (AgpJson) y /api/tool sale camelCase — es la excepción histórica del wire.
// Por eso cada DTO lleva [JsonPropertyName] explícito: PropertyNameCaseInsensitive
// NO cubre underscores y un DTO mal casado deserializa ceros EN SILENCIO.
//
// El GET también limpia el BOM (U+FEFF) que a veces antepone EmbedIO, como
// hace ImplementoClient: sin eso System.Text.Json explota y la precarga
// quedaría muda.
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

// ---------------------------------------------------------------- DTOs (wire)

/// <summary>Motor QuantiX tal como lo guarda quantiX_motores.json. Solo los
/// campos que la calculadora precarga (el resto lo maneja el editor).</summary>
internal sealed class CalcMotorDto
{
    [JsonPropertyName("nombre")]            public string? Nombre { get; set; }
    [JsonPropertyName("semillas_vuelta")]   public double SemillasVuelta { get; set; }
    [JsonPropertyName("dientes_engranaje")] public double DientesEngranaje { get; set; }
    [JsonPropertyName("max_hz")]            public double MaxHz { get; set; }
    [JsonPropertyName("cortes")]            public List<int>? Cortes { get; set; }
    [JsonPropertyName("unidad_dosis")]      public string? UnidadDosis { get; set; }
    [JsonPropertyName("dosis_fija")]        public double DosisFija { get; set; }
}

internal sealed class CalcNodoDto
{
    [JsonPropertyName("uid")]     public string? Uid { get; set; }
    [JsonPropertyName("nombre")]  public string? Nombre { get; set; }
    [JsonPropertyName("motores")] public List<CalcMotorDto>? Motores { get; set; }
}

/// <summary>Respuesta de /api/quantix/motores. El JS hace
/// <c>var cfg = (dm &amp;&amp; dm.config) || dm;</c> — o sea que tolera que los
/// nodos vengan en la raíz. Se replica: si no hay "config", se usan los nodos
/// de la raíz.</summary>
internal sealed class CalcMotoresResp
{
    [JsonPropertyName("ok")]     public bool Ok { get; set; }
    [JsonPropertyName("config")] public CalcMotoresResp? Config { get; set; }
    [JsonPropertyName("nodos")]  public List<CalcNodoDto>? Nodos { get; set; }
}

/// <summary>/api/tool — camelCase, la excepción del wire.</summary>
internal sealed class CalcToolDto
{
    [JsonPropertyName("width")]       public double Width { get; set; }
    [JsonPropertyName("numSections")] public int NumSections { get; set; }
}

internal sealed class CalcToolResp
{
    [JsonPropertyName("tool")] public CalcToolDto? Tool { get; set; }
}

internal sealed class CalcImplementoResp
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
}

// ------------------------------------------------------------ modelo de la UI

/// <summary>Un motor aplanado, listo para el combo. La etiqueta se arma igual
/// que en el JS: <c>(nodo.nombre || nodo.uid) + ' · M' + i + ' ' + (m.nombre || '')</c>.</summary>
public sealed class CalcMotorItem
{
    public string Etiqueta { get; set; } = "";
    public double SemillasVuelta { get; set; }
    public double DientesEngranaje { get; set; }
    public double MaxHz { get; set; }
    /// <summary>Cantidad de cortes (surcos que alimenta). 0 = sin cortes.</summary>
    public int Cortes { get; set; }
    public string UnidadDosis { get; set; } = "";
    public double DosisFija { get; set; }
}

/// <summary>Lo que devuelve la precarga. Espeja exactamente lo que el JS deja
/// en pantalla al terminar cargarContexto().</summary>
public sealed class CalcContexto
{
    /// <summary>El GET /api/implemento contestó ok. El JS lo guarda en
    /// state.impl y NO lo usa para ninguna cuenta — se conserva la llamada
    /// para no cambiar el comportamiento observable de la pantalla.</summary>
    public bool ImplementoOk { get; set; }

    /// <summary>Ancho de labor de PilotX (m). 0 = sin dato.</summary>
    public double Ancho { get; set; }

    /// <summary>Secciones de PilotX = surcos. 0 = sin dato.</summary>
    public int Surcos { get; set; }

    public List<CalcMotorItem> Motores { get; } = new List<CalcMotorItem>();
}

// -------------------------------------------------------------------- cliente

public sealed class CalculadoraSiembraClient
{
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>BOM (U+FEFF) y zero-width space (U+200B) que mete EmbedIO.</summary>
    private static readonly char[] Basura = { (char)0xFEFF, (char)0x200B };

    private readonly string _baseUrl;

    public CalculadoraSiembraClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public string BaseUrl => _baseUrl;

    // ---- plumbing ---------------------------------------------------------

    private async Task<T?> GetAsync<T>(string ruta, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + ruta, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(Limpio(json), _jsonOpts);
        }
        // TaskCanceledException HEREDA de OperationCanceledException: sin este
        // filtro un timeout del HttpClient se confundiría con la cancelación
        // del panel y la pantalla se quedaría clavada en sus defaults.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch { return null; }
    }

    private static string Limpio(string json) => (json ?? "").TrimStart(Basura).TrimStart();

    // ---- precarga (el cargarContexto() del JS, mismo orden) ----------------

    public async Task<CalcContexto> CargarContextoAsync(CancellationToken ct = default)
    {
        var ctx = new CalcContexto();

        // 1) Implemento activo. El JS lo pide y lo guarda; no entra en ninguna
        //    cuenta. Se mantiene la llamada tal cual.
        var impl = await GetAsync<CalcImplementoResp>("api/implemento", ct).ConfigureAwait(false);
        ctx.ImplementoOk = impl != null && impl.Ok;

        // 2) Geometría: TODA sale de la config de secciones de PilotX.
        var tool = await GetAsync<CalcToolResp>("api/tool", ct).ConfigureAwait(false);
        if (tool?.Tool != null)
        {
            ctx.Ancho = tool.Tool.Width;
            ctx.Surcos = tool.Tool.NumSections;
        }

        // 3) Motores QuantiX configurados.
        var mot = await GetAsync<CalcMotoresResp>("api/quantix/motores", ct).ConfigureAwait(false);
        var nodos = mot?.Config?.Nodos ?? mot?.Nodos;
        if (nodos != null)
        {
            foreach (var n in nodos)
            {
                var motores = n?.Motores;
                if (motores == null) continue;
                for (int i = 0; i < motores.Count; i++)
                {
                    var m = motores[i];
                    if (m == null) continue;
                    string nombreNodo = string.IsNullOrEmpty(n!.Nombre) ? (n.Uid ?? "") : n.Nombre!;
                    ctx.Motores.Add(new CalcMotorItem
                    {
                        Etiqueta = nombreNodo + " · M" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 + " " + (m.Nombre ?? ""),
                        SemillasVuelta = m.SemillasVuelta,
                        DientesEngranaje = m.DientesEngranaje,
                        MaxHz = m.MaxHz,
                        Cortes = m.Cortes?.Count ?? 0,
                        UnidadDosis = m.UnidadDosis ?? "",
                        DosisFija = m.DosisFija,
                    });
                }
            }
        }

        return ctx;
    }

    // ---- teclado nativo ---------------------------------------------------

    /// <summary>Misma señal HTTP que mandan las páginas del Hub. Catch mudo a
    /// propósito: sin teclado nativo el campo sigue editable.</summary>
    public async Task TecladoAsync(bool abrir, bool numerico = true, string titulo = "")
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string body = abrir
                ? "{\"numerico\":" + (numerico ? "true" : "false") + ",\"titulo\":"
                  + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_baseUrl + "api/teclado/" + (abrir ? "abrir" : "cerrar"),
                                                contenido, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }
}
