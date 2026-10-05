// ============================================================================
// ControlClient.cs — cliente de la autoridad de control (/api/control/*).
//
// La cabina late cada ~1,5 s (POST /api/control/latido, que devuelve el
// estado) y desde el indicador puede ceder, rechazar o recuperar. Habla SOLO
// con el host local: desde 127.0.0.1 el Engine la reconoce como cabina.
// Corte por CancellationToken, no por Timeout de HttpClient (mismo criterio
// que ChatPanelClient).
// ============================================================================

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class ControlAccionRemotaWire
{
    [JsonPropertyName("nombre")]        public string? Nombre       { get; set; }
    [JsonPropertyName("ip")]            public string? Ip           { get; set; }
    [JsonPropertyName("accion")]        public string? Accion       { get; set; }
    [JsonPropertyName("tenia_control")] public bool    TeniaControl { get; set; }
    [JsonPropertyName("hace_ms")]       public long    HaceMs       { get; set; }
}

public sealed class ControlEstadoWire
{
    [JsonPropertyName("ok")]               public bool    Ok             { get; set; }
    [JsonPropertyName("modo")]             public string? Modo           { get; set; }
    [JsonPropertyName("dueno_es_cabina")]  public bool    DuenoEsCabina  { get; set; } = true;
    [JsonPropertyName("dueno_nombre")]     public string? DuenoNombre    { get; set; }
    [JsonPropertyName("dueno_ip")]         public string? DuenoIp        { get; set; }
    [JsonPropertyName("hay_pedido")]       public bool    HayPedido      { get; set; }
    [JsonPropertyName("pedido_nombre")]    public string? PedidoNombre   { get; set; }
    [JsonPropertyName("pedido_ip")]        public string? PedidoIp       { get; set; }
    [JsonPropertyName("ultima_accion_remota")] public ControlAccionRemotaWire? UltimaAccionRemota { get; set; }
}

public sealed class ControlClient
{
    private static readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan Corte = TimeSpan.FromSeconds(3);

    private readonly string _baseUrl;

    public ControlClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    /// <summary>Latido de la cabina. null = el motor no contestó (o es uno viejo
    /// sin /api/control: 404 → null, el indicador queda escondido).</summary>
    public Task<ControlEstadoWire?> LatidoAsync(CancellationToken ct) => PostEstadoAsync("api/control/latido", ct);

    public Task<ControlEstadoWire?> CederAsync(CancellationToken ct) => PostEstadoAsync("api/control/ceder", ct);
    public Task<ControlEstadoWire?> RechazarAsync(CancellationToken ct) => PostEstadoAsync("api/control/rechazar", ct);
    public Task<ControlEstadoWire?> RecuperarAsync(CancellationToken ct) => PostEstadoAsync("api/control/recuperar", ct);

    private async Task<ControlEstadoWire?> PostEstadoAsync(string ruta, CancellationToken ct)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(Corte);
            using var contenido = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + ruta, contenido, corte.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            json = json.TrimStart('﻿');
            using var doc = JsonDocument.Parse(json);
            // latido devuelve el estado plano; ceder/rechazar/recuperar lo traen en "estado".
            var raiz = doc.RootElement.TryGetProperty("estado", out var est) && est.ValueKind == JsonValueKind.Object
                ? est : doc.RootElement;
            return raiz.Deserialize<ControlEstadoWire>(_jsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
