// ============================================================================
// RecPathClient.cs — canal HTTP del panel "Rutas grabadas" nativo
// (ex pages/recpath.html, reemplazo de los WinForms FormRecordPicker +
// FormRecordName).
//
// Qué habla — EXACTAMENTE el mismo /api/recpath que ya usaba la página HTML
// (cero endpoints nuevos, cero cambios de casing, cero campos agregados):
//   · GET  /api/recpath/list             → {ok, paths:["nombre1", …]}
//   · POST /api/recpath/load    {name}   → {ok}
//   · POST /api/recpath/delete  {name}   → {ok, paths:[…]}  ← trae la lista NUEVA
//   · POST /api/recpath/off              → {ok}
//   · POST /api/recpath/save    {name}   → {ok}
//   · POST /api/recpath/discard          → {ok}
// Más el teclado nativo de PilotX: POST /api/teclado/abrir|cerrar.
//
// null = el Hub no contestó (o contestó algo impresentable). El panel lo pinta
// distinto de {ok:false}: uno es "no hay quien atienda", el otro es "el motor
// dijo que no". La página HTML mezclaba los dos en un "No hay rutas grabadas"
// que mentía cuando el que estaba caído era el Hub.
//
// El `name` viaja tal cual lo compone el panel: el back hace name + ".rec" sin
// validar nada, así que la sanitización de caracteres inválidos de archivo la
// hace el panel ANTES de llamar acá.
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

/// <summary>Respuesta de /api/recpath/list.</summary>
public sealed class RecPathListDto
{
    // [JsonPropertyName] EXPLÍCITO en cada propiedad, por doctrina: acá el wire
    // no tiene underscores, pero la regla no se afloja por página.
    [JsonPropertyName("ok")]    public bool Ok { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>
/// Respuesta de los POST. `paths` solo la trae /delete — se aprovecha para
/// repintar la lista sin un GET extra.
/// </summary>
public sealed class RecPathOkDto
{
    [JsonPropertyName("ok")]    public bool Ok { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class RecPathClient
{
    // 3 s: el panel prefiere decir "El Hub no responde" antes que dejar al
    // operario mirando un botón muerto con el tractor andando.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _base;

    public RecPathClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    public async Task<RecPathListDto?> GetListAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_base + "api/recpath/list", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Deserializar<RecPathListDto>(json);
        }
        catch { return null; }   // el panel pinta "El Hub no responde", jamás explota
    }

    public Task<RecPathOkDto?> LoadAsync(string name, CancellationToken ct = default)
        => PostAsync("api/recpath/load", JsonNombre(name), ct);

    public Task<RecPathOkDto?> DeleteAsync(string name, CancellationToken ct = default)
        => PostAsync("api/recpath/delete", JsonNombre(name), ct);

    public Task<RecPathOkDto?> OffAsync(CancellationToken ct = default)
        => PostAsync("api/recpath/off", "{}", ct);

    public Task<RecPathOkDto?> SaveAsync(string name, CancellationToken ct = default)
        => PostAsync("api/recpath/save", JsonNombre(name), ct);

    public Task<RecPathOkDto?> DiscardAsync(CancellationToken ct = default)
        => PostAsync("api/recpath/discard", "{}", ct);

    /// <summary>
    /// Teclado nativo de PilotX (TecladoWindow), misma señal HTTP que mandan las
    /// páginas del Hub. Catch mudo a propósito: sin teclado en pantalla el campo
    /// se sigue escribiendo con un teclado físico.
    /// </summary>
    public async Task TecladoAsync(bool abrir)
    {
        try
        {
            string body = abrir
                ? "{\"numerico\":false,\"titulo\":\"Nombre de la ruta grabada\"}"
                : "{}";
            using var cont = new StringContent(body, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(
                _base + "api/teclado/" + (abrir ? "abrir" : "cerrar"), cont).ConfigureAwait(false);
        }
        catch { /* el teclado es una comodidad, no puede voltear el panel */ }
    }

    // ------------------------------------------------------------------------

    private async Task<RecPathOkDto?> PostAsync(string ruta, string body, CancellationToken ct)
    {
        try
        {
            using var cont = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_base + ruta, cont, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Deserializar<RecPathOkDto>(json);
        }
        catch { return null; }
    }

    private static T? Deserializar<T>(string json) where T : class
    {
        // El WebHost del motor manda el JSON CON BOM: sin sacarlo el parseo
        // explota y un Hub perfectamente vivo se vería como "no responde".
        json = (json ?? "").TrimStart((char)0xFEFF, (char)0x200B).Trim();
        if (json.Length == 0) return null;
        return JsonSerializer.Deserialize<T>(json, _jsonOpts);
    }

    /// <summary>
    /// {"name":"…"} con el nombre escapado en serio: el operario escribe este
    /// texto a mano y una comilla armada a mano rompería el body.
    /// </summary>
    private static string JsonNombre(string name)
        => "{\"name\":" + JsonSerializer.Serialize(name ?? "") + "}";
}
