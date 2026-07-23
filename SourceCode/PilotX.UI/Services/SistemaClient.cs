// SistemaClient.cs
//
// Cliente HTTP minimo para los endpoints del SistemaController (EmbedIO,
// puerto 5180). Se usa desde el SistemaPanel nativo: brillo + power.
//
// Es OBJETIVAMENTE puente temporal: cuando el AgpWebHost desaparezca
// (objetivo del pivot), estas llamadas se reemplazan por invocaciones
// directas al ISistemaService dentro del mismo proceso. La API del
// cliente (GetBrightness/SetBrightness/Power) ya esta alineada con esa
// interfaz para que el swap sea un sed-rename.
//
// No usa Refit ni Flurl para no inflar el binario: HttpClient pelado +
// JsonSerializer suficientes para 3 endpoints.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public enum PowerAction
{
    Shutdown,
    Restart,
    Suspend,
    ExitApp
}

public sealed class SistemaClient
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseUrl;

    public SistemaClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    private sealed class BrilloResponse
    {
        [JsonPropertyName("ok")]    public bool Ok    { get; set; }
        [JsonPropertyName("value")] public int  Value { get; set; }
    }

    private sealed class OkResponse
    {
        [JsonPropertyName("ok")]    public bool   Ok    { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    /// <summary>
    /// Devuelve el brillo actual 0..100, o -1 si no hay control de brillo
    /// disponible (laptops sin WMI ni monitores DDC/CI). Si la request
    /// falla, tambien devuelve -1.
    /// </summary>
    public async Task<int> GetBrightnessAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/sistema/brillo", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return -1;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<BrilloResponse>(json, _jsonOpts);
            if (r == null || !r.Ok) return -1;
            return r.Value;
        }
        catch { return -1; }
    }

    /// <summary>Aplica brillo 0..100. Devuelve true si la PC reportó ok.</summary>
    public async Task<bool> SetBrightnessAsync(int value, CancellationToken ct = default)
    {
        try
        {
            int v = Math.Clamp(value, 0, 100);
            var url = _baseUrl + "api/sistema/brillo?value=" + v;
            using var resp = await _http.PostAsync(url, content: null, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<OkResponse>(json, _jsonOpts);
            return r != null && r.Ok;
        }
        catch { return false; }
    }

    /// <summary>Ejecuta la accion de power. Devuelve true si la PC reportó ok.</summary>
    public async Task<bool> PowerAsync(PowerAction action, CancellationToken ct = default)
    {
        try
        {
            string a;
            switch (action)
            {
                case PowerAction.Shutdown: a = "shutdown"; break;
                case PowerAction.Restart:  a = "restart";  break;
                case PowerAction.Suspend:  a = "suspend";  break;
                case PowerAction.ExitApp:  a = "exitApp";  break;
                default: return false;
            }
            var url = _baseUrl + "api/sistema/power?action=" + a;
            using var resp = await _http.PostAsync(url, content: null, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<OkResponse>(json, _jsonOpts);
            return r != null && r.Ok;
        }
        catch { return false; }
    }
}
