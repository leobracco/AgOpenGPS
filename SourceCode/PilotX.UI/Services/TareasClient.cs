// ============================================================================
// TareasClient.cs — canal HTTP del panel "Tarea" nativo (tareas de trabajo del
// lote abierto). Habla con /api/tareas/* del motor (TareasController):
//   · GET  /api/tareas/estado
//   · POST /api/tareas/crear     {cultivo, tipo_trabajo, notas}
//   · POST /api/tareas/pausar | reanudar | cerrar
//   · POST /api/tareas/exportar  {id, destino}
// Más el teclado nativo de PilotX: POST /api/teclado/abrir|cerrar.
//
// null = el motor no contestó (o contestó algo impresentable); el panel lo
// pinta como "sin conexión", distinto de {ok:false} que trae el motivo.
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

public sealed class TareaVistaDto
{
    [JsonPropertyName("id")]               public string? Id { get; set; }
    [JsonPropertyName("estado")]           public string? Estado { get; set; }
    [JsonPropertyName("estado_texto")]     public string? EstadoTexto { get; set; }
    [JsonPropertyName("tipo_trabajo")]     public string? TipoTrabajo { get; set; }
    [JsonPropertyName("tipo_texto")]       public string? TipoTexto { get; set; }
    [JsonPropertyName("cultivo")]          public string? Cultivo { get; set; }
    [JsonPropertyName("notas")]            public string? Notas { get; set; }
    [JsonPropertyName("insumo")]           public string? Insumo { get; set; }
    [JsonPropertyName("dosis_texto")]      public string? DosisTexto { get; set; }
    [JsonPropertyName("inicio_texto")]     public string? InicioTexto { get; set; }
    [JsonPropertyName("fin_texto")]        public string? FinTexto { get; set; }
    [JsonPropertyName("area_ha")]          public double AreaHa { get; set; }
    [JsonPropertyName("area_texto")]       public string? AreaTexto { get; set; }
    [JsonPropertyName("duracion_texto")]   public string? DuracionTexto { get; set; }
    [JsonPropertyName("archivo_sugerido")] public string? ArchivoSugerido { get; set; }
}

public sealed class TareasEstadoDto
{
    [JsonPropertyName("ok")]                 public bool Ok { get; set; }
    [JsonPropertyName("error")]              public string? Error { get; set; }
    [JsonPropertyName("hay_lote")]           public bool HayLote { get; set; }
    [JsonPropertyName("lote")]               public string? Lote { get; set; }
    [JsonPropertyName("abierta")]            public TareaVistaDto? Abierta { get; set; }
    [JsonPropertyName("cerradas")]           public List<TareaVistaDto>? Cerradas { get; set; }
    [JsonPropertyName("insumo_activo")]      public string? InsumoActivo { get; set; }
    [JsonPropertyName("insumo_dosis_texto")] public string? InsumoDosisTexto { get; set; }
    [JsonPropertyName("cultivo_sugerido")]   public string? CultivoSugerido { get; set; }
    [JsonPropertyName("tipo_sugerido")]      public string? TipoSugerido { get; set; }
}

public sealed class TareaExportDto
{
    [JsonPropertyName("ok")]        public bool Ok { get; set; }
    [JsonPropertyName("error")]     public string? Error { get; set; }
    [JsonPropertyName("carpeta")]   public string? Carpeta { get; set; }
    [JsonPropertyName("archivos")]  public List<string>? Archivos { get; set; }
    [JsonPropertyName("poligonos")] public int Poligonos { get; set; }
}

public sealed class TareasClient
{
    // 4 s para lo normal; el export escribe SHP en un pendrive y puede tardar.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly HttpClient _httpLargo = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _base;

    public TareasClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    public async Task<TareasEstadoDto?> EstadoAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_base + "api/tareas/estado", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return Deserializar<TareasEstadoDto>(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch { return null; }
    }

    public Task<TareasEstadoDto?> CrearAsync(string tipo, string cultivo, string notas)
        => PostAsync<TareasEstadoDto>(_http, "api/tareas/crear", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["tipo_trabajo"] = tipo ?? "",
            ["cultivo"] = cultivo ?? "",
            ["notas"] = notas ?? "",
        }));

    public Task<TareasEstadoDto?> PausarAsync()   => PostAsync<TareasEstadoDto>(_http, "api/tareas/pausar", "{}");
    public Task<TareasEstadoDto?> ReanudarAsync() => PostAsync<TareasEstadoDto>(_http, "api/tareas/reanudar", "{}");
    public Task<TareasEstadoDto?> CerrarAsync()   => PostAsync<TareasEstadoDto>(_http, "api/tareas/cerrar", "{}");

    public Task<TareaExportDto?> ExportarAsync(string id, string destino)
        => PostAsync<TareaExportDto>(_httpLargo, "api/tareas/exportar", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = id ?? "",
            ["destino"] = destino ?? "",
        }));

    /// <summary>Teclado nativo de PilotX (nunca osk.exe). Catch mudo: sin
    /// teclado en pantalla el campo se sigue escribiendo con uno físico.</summary>
    public async Task TecladoAsync(bool abrir, string titulo = "")
    {
        try
        {
            string body = abrir
                ? "{\"numerico\":false,\"titulo\":" + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var cont = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(
                _base + "api/teclado/" + (abrir ? "abrir" : "cerrar"), cont).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task<T?> PostAsync<T>(HttpClient http, string ruta, string body) where T : class
    {
        try
        {
            using var cont = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(_base + ruta, cont).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return Deserializar<T>(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch { return null; }
    }

    private static T? Deserializar<T>(string json) where T : class
    {
        // El WebHost del motor manda el JSON CON BOM: sin sacarlo el parseo
        // explota y un motor vivo se vería como "sin conexión".
        json = (json ?? "").TrimStart((char)0xFEFF, (char)0x200B).Trim();
        if (json.Length == 0) return null;
        try { return JsonSerializer.Deserialize<T>(json, _jsonOpts); }
        catch { return null; }
    }
}
