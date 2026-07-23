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

    /// <summary>
    /// Hook opcional de manejo local. Si está seteado y devuelve true, el
    /// comando se consideró manejado en proceso (ej. PilotX.Desktop abre la
    /// página HTML del comando en su propio WebView, o hace una acción de
    /// ventana) y NO se hace el POST al backend. En el Host sobre FormGPS queda
    /// null → todos los comandos van por HTTP como siempre.
    /// </summary>
    public Func<string, bool> LocalHandler { get; set; }

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
            // Manejo local (navegación a página HTML / acción de ventana) tiene
            // prioridad; si lo consume, no se envía al backend.
            if (LocalHandler != null && LocalHandler(cmd)) return true;
            var json = JsonSerializer.Serialize(new { cmd });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_url, content, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
