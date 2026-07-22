using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Cockpit.Bars.Services;

/// <summary>Único canal de acción de las barras: POST /api/aog/guidance/command
/// {cmd}. El WebHost lo enruta a FormGPS.ExecuteGuidanceCommand, que dispara el
/// botón WinForms nativo correspondiente.</summary>
public sealed class GuidanceCommandClient
{
    private readonly HttpClient _http;
    private readonly string _url;

    public GuidanceCommandClient(HttpClient http, string baseUrl = "http://127.0.0.1:5180/")
    {
        _http = http;
        var b = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
              : baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        _url = b + "api/aog/guidance/command";
    }

    public async Task<bool> SendAsync(string cmd, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { cmd });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_url, content, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
