// ============================================================================
// ImplementoClient.cs — cliente HTTP del IMPLEMENTO CENTRAL (/api/implemento).
//
// QUÉ QUEDÓ NATIVO con este cliente: la carta "Trenes de siembra" de la pestaña
// Secciones del ConfigPanel (Views/ConfigEditor/SeccionesTab.cs), que es la
// FUENTE ÚNICA de la asignación surco→tren y de la distancia del tren trasero.
// QUÉ SIGUE EN HTML: pages/config-implemento.html (surcos, semillas/ha, catálogo
// de sembradoras) y el resto del Hub. La página no se toca ni se borra: la usa
// la PWA del celular.
//
// ⚠️ LAS DOS CONFIGURACIONES DE IMPLEMENTO — leer antes de tocar nada:
//   · /api/aog/config  → la config de PilotX (guiado y corte de secciones),
//     respaldada por GuidanceEngineData\tool.json. La maneja ConfigVehiculoClient.
//   · /api/implemento  → ESTE. Surcos, trenes, secciones nombradas, metadata de
//     catálogo y semillas/ha de la pantalla de siembra, en implementos\<slug>.json.
//   NO están sincronizadas. La pestaña Secciones toca LAS DOS a propósito y en
//   este orden: primero la geometría (POST secciones) y recién si sale bien el
//   PUT del implemento, con la cantidad ya firme.
//
// ⚠️ EL PUT ES REEMPLAZO COMPLETO DEL DTO. Cualquier propiedad del ImplementoDto
// del backend que NO esté espejada acá se PIERDE en silencio al guardar trenes
// (marca, modelo, nodos_uids, overlap, flags is_*…). Por eso el DTO de abajo
// está COMPLETO y se round-trippea entero: se lee con GET, se muta solo lo que
// la pantalla edita y se devuelve tal cual. Si el backend agrega un campo a
// AgroParallel.Models\ImplementoDto.cs, HAY QUE AGREGARLO ACÁ EL MISMO DÍA.
//
// Trampas del wire cubiertas:
//   · snake_case explícito en cada propiedad ([JsonPropertyName]): el
//     case-insensitive de System.Text.Json NO cubre los underscores.
//   · BOM de EmbedIO (U+FEFF) adelante del JSON: se limpia antes de parsear.
//   · El PUT inválido NO devuelve {ok:false}: devuelve HTTP 400 con el envelope
//     de error de AgpControllerBase ({error, mensaje, detalle}) y el código
//     AGP-CFG-001. Se parsea ese cuerpo igual, para poder mostrarle al operario
//     qué estuvo mal en vez de un "error" pelado.
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

/// <summary>Tren físico de siembra (delantero / trasero). Espejo de
/// AgroParallel.Models.TrenDto.</summary>
public sealed class TrenDto
{
    [JsonPropertyName("id")]          public int Id { get; set; }
    [JsonPropertyName("nombre")]      public string Nombre { get; set; } = "";
    /// <summary>Metros que corta DESPUÉS del tren 1. El tren 1 va siempre en 0.</summary>
    [JsonPropertyName("distancia_m")] public double DistanciaM { get; set; }
}

/// <summary>Una hilera física. Espejo de AgroParallel.Models.SurcoDto.</summary>
public sealed class SurcoDto
{
    [JsonPropertyName("numero")]         public int Numero { get; set; }
    [JsonPropertyName("tren_id")]        public int TrenId { get; set; }
    [JsonPropertyName("seccion_pilotx")] public int SeccionPilotX { get; set; }
}

/// <summary>Sección nombrada del implemento (NO es la sección del guiado).
/// Espejo de AgroParallel.Models.SeccionDto.</summary>
public sealed class SeccionImplementoDto
{
    [JsonPropertyName("id")]            public int Id { get; set; }
    [JsonPropertyName("nombre")]        public string Nombre { get; set; } = "";
    [JsonPropertyName("lookahead_on")]  public double LookaheadOn { get; set; }
    [JsonPropertyName("lookahead_off")] public double LookaheadOff { get; set; }
}

/// <summary>Implemento central COMPLETO. Ver la advertencia de la cabecera: el
/// PUT reemplaza todo, así que este espejo tiene que estar completo.</summary>
public sealed class ImplementoDto
{
    [JsonPropertyName("nombre")]                   public string Nombre { get; set; } = "";
    [JsonPropertyName("ancho_total_m")]            public double AnchoTotalM { get; set; }
    [JsonPropertyName("numero_surcos")]            public int NumeroSurcos { get; set; }
    [JsonPropertyName("distancia_entre_surcos_m")] public double DistanciaEntreSurcosM { get; set; }

    [JsonPropertyName("trenes")]     public List<TrenDto> Trenes { get; set; } = new List<TrenDto>();
    [JsonPropertyName("surcos")]     public List<SurcoDto> Surcos { get; set; } = new List<SurcoDto>();
    [JsonPropertyName("secciones")]  public List<SeccionImplementoDto> Secciones { get; set; } = new List<SeccionImplementoDto>();
    [JsonPropertyName("nodos_uids")] public List<string> NodosUids { get; set; } = new List<string>();

    // Campos heredados del ToolConfigDto legacy: no los edita esta pantalla,
    // pero viajan de ida y vuelta porque el PUT es reemplazo total.
    [JsonPropertyName("overlap_m")]                public double OverlapM { get; set; }
    [JsonPropertyName("offset_m")]                 public double OffsetM { get; set; }
    [JsonPropertyName("hitch_length_m")]           public double HitchLengthM { get; set; }
    [JsonPropertyName("trailing_hitch_length_m")]  public double TrailingHitchLengthM { get; set; }
    [JsonPropertyName("trailing_tool_to_pivot_m")] public double TrailingToolToPivotM { get; set; }
    [JsonPropertyName("lookahead_on_s")]           public double LookaheadOnS { get; set; }
    [JsonPropertyName("lookahead_off_s")]          public double LookaheadOffS { get; set; }
    [JsonPropertyName("turn_off_delay_s")]         public double TurnOffDelayS { get; set; }
    [JsonPropertyName("is_trailing")]              public bool IsTrailing { get; set; }
    [JsonPropertyName("is_tbt")]                   public bool IsTBT { get; set; }
    [JsonPropertyName("is_rear_fixed")]            public bool IsRearFixed { get; set; }
    [JsonPropertyName("is_front_fixed")]           public bool IsFrontFixed { get; set; }
    [JsonPropertyName("section_off_when_out")]     public bool SectionOffWhenOut { get; set; }

    // Metadata del catálogo de sembradoras.
    [JsonPropertyName("categoria")]           public string Categoria { get; set; } = "";
    [JsonPropertyName("marca")]               public string Marca { get; set; } = "";
    [JsonPropertyName("modelo")]              public string Modelo { get; set; } = "";
    [JsonPropertyName("tipo_cultivo")]        public string TipoCultivo { get; set; } = "";
    [JsonPropertyName("tipo_siembra")]        public string TipoSiembra { get; set; } = "";
    [JsonPropertyName("tipo_dosificador")]    public string TipoDosificador { get; set; } = "";
    [JsonPropertyName("numero_torres")]       public int NumeroTorres { get; set; }
    [JsonPropertyName("tiene_fertilizacion")] public bool TieneFertilizacion { get; set; }
    [JsonPropertyName("tipo_estructura")]     public string TipoEstructura { get; set; } = "";
}

/// <summary>Respuesta de GET /api/implemento.</summary>
public sealed class ImplementoRespuesta
{
    [JsonPropertyName("ok")]         public bool Ok { get; set; }
    [JsonPropertyName("error")]      public string? Error { get; set; }
    [JsonPropertyName("slug")]       public string? Slug { get; set; }
    [JsonPropertyName("path")]       public string? Path { get; set; }
    [JsonPropertyName("implemento")] public ImplementoDto? Implemento { get; set; }
}

/// <summary>Respuesta del PUT: {ok, slug} si salió bien; el envelope de error
/// ({error, mensaje, detalle}) con HTTP 400 si la validación falló.</summary>
public sealed class ImplementoGuardado
{
    [JsonPropertyName("ok")]      public bool Ok { get; set; }
    [JsonPropertyName("slug")]    public string? Slug { get; set; }
    [JsonPropertyName("error")]   public string? Error { get; set; }
    [JsonPropertyName("mensaje")] public string? Mensaje { get; set; }
    [JsonPropertyName("detalle")] public string? Detalle { get; set; }

    /// <summary>Texto para el operario: el friendly del envelope si vino, si no
    /// el código crudo. Nunca vacío.</summary>
    public string Motivo()
    {
        if (!string.IsNullOrWhiteSpace(Mensaje)) return Mensaje!;
        if (!string.IsNullOrWhiteSpace(Error)) return Error!;
        return "error";
    }
}

// -------------------------------------------------------------------- cliente

public sealed class ImplementoClient
{
    // Timeout más generoso que el de la config: el PUT escribe el JSON del
    // implemento a disco y encima dispara el write-back al Tool nativo.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>BOM (U+FEFF) y zero-width space (U+200B) que mete EmbedIO.</summary>
    private static readonly char[] Basura = { (char)0xFEFF, (char)0x200B };

    private readonly string _baseUrl;

    public ImplementoClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public string BaseUrl => _baseUrl;

    /// <summary>GET del implemento ACTIVO. null = el Hub no respondió;
    /// Ok=false = respondió pero el servicio no está. La UI los distingue:
    /// sin implemento la carta de trenes se apaga y no se guarda NADA de
    /// trenes (nunca se inventa un implemento vacío que después pisaría al
    /// bueno con un PUT).</summary>
    public async Task<ImplementoRespuesta?> GetActivoAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/implemento", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ImplementoRespuesta>(Limpio(json), _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>PUT del implemento ACTIVO (reemplazo COMPLETO). null = no
    /// respondió. Ojo: el 400 de validación SÍ trae cuerpo y se devuelve
    /// parseado con Ok=false, para poder decirle al operario qué falló.</summary>
    public async Task<ImplementoGuardado?> PutActivoAsync(ImplementoDto dto, CancellationToken ct = default)
    {
        if (dto == null) return null;
        try
        {
            string txt = JsonSerializer.Serialize(dto);
            using var contenido = new StringContent(txt, Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(_baseUrl + "api/implemento", contenido, ct)
                                        .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<ImplementoGuardado>(Limpio(json), _jsonOpts);
            if (r == null) return null;
            // El envelope de error no trae "ok": si el status no fue 2xx, es
            // fallo aunque el bool haya quedado en su default.
            if (!resp.IsSuccessStatusCode) r.Ok = false;
            return r;
        }
        catch { return null; }
    }

    private static string Limpio(string json) => (json ?? "").TrimStart(Basura).TrimStart();
}
