// RedIpClient.cs
// Cliente HTTP del RedIpController (EmbedIO :5180). Lo usa el RedIpPanel
// nativo para listar adaptadores y aplicar DHCP / IP fija.
// Timeout largo: aplicar espera al helper SYSTEM (~10 s).

using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class RedAdaptadorDto
{
    [JsonPropertyName("if_index")] public int IfIndex { get; set; }
    [JsonPropertyName("nombre")]   public string Nombre { get; set; }
    [JsonPropertyName("tipo")]     public string Tipo { get; set; }
    [JsonPropertyName("dhcp")]     public bool Dhcp { get; set; }
    [JsonPropertyName("ip")]       public string Ip { get; set; }
    [JsonPropertyName("prefix")]   public int Prefix { get; set; }
    [JsonPropertyName("gateway")]  public string Gateway { get; set; }
    [JsonPropertyName("dns")]      public List<string> Dns { get; set; }
    [JsonPropertyName("up")]       public bool Up { get; set; }
    [JsonPropertyName("metric")]   public int Metric { get; set; }
}

public sealed class RedIpClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = System.TimeSpan.FromSeconds(30)
    };
    private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    private readonly string _baseUrl;

    public RedIpClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    private sealed class ListaResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("adaptadores")] public List<RedAdaptadorDto> Adaptadores { get; set; }
    }

    private sealed class OkResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    public async Task<List<RedAdaptadorDto>> ListarAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/red/adaptadores", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new List<RedAdaptadorDto>();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<ListaResponse>(json, _opts);
            return (r != null && r.Ok && r.Adaptadores != null) ? r.Adaptadores : new List<RedAdaptadorDto>();
        }
        catch { return new List<RedAdaptadorDto>(); }
    }

    public async Task<(bool ok, string error)> AplicarAsync(int ifIndex, string mode, string ip, int prefix,
                                                            string gateway, IEnumerable<string> dns, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                if_index = ifIndex,
                mode,
                ip,
                prefix,
                gateway,
                dns = dns ?? new List<string>(),
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/red/ip", content, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<OkResponse>(json, _opts);
            if (r == null) return (false, "respuesta inválida");
            return (r.Ok, r.Error);
        }
        catch (System.Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Abre/cierra el teclado nativo de PilotX (mismo contrato que
    /// RedWifiClient). Los campos lo piden en GotFocus/LostFocus; sin esto el
    /// teclado no se engancha a los TextBox de este panel.</summary>
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
}
