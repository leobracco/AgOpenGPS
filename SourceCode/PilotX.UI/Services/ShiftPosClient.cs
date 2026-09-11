// ============================================================================
// ShiftPosClient.cs — canal HTTP del panel "Corregir posición" nativo
// (ex pages/corregir-posicion.html, reemplazo del WinForms FormShiftPos).
//
// Qué habla:
//   · GET  /api/aog/shift-pos            → estado inicial del corrimiento de
//     deriva GPS (north_cm / east_cm / offsets_on, snake_case AgpJson).
//   · POST /api/aog/guidance/command     → escritura, body {"cmd":"..."} con
//     shift_north_<cm> / shift_east_<cm> / shift_zero / offsets_on / offsets_off.
//
// EXACTAMENTE el mismo contrato que ya usaba la página HTML: cero endpoints
// nuevos, cero cambios de casing, cero campos agregados.
//
// El POST devuelve tres estados distintos y el panel los pinta distinto, así
// que acá se devuelve bool? en vez de bool:
//   true  = el motor lo aceptó
//   false = el motor lo RECHAZÓ (hoy es lo normal: GuidanceEngineHost.
//           ExecuteCommand todavía no conoce los shift_*/offsets_*; es carril
//           back-end, ver COORDINACION-SESIONES.md)
//   null  = no hubo respuesta usable (sin conexión / HTTP feo)
// La página escondía el rechazo en un console.warn — invisible en cabina.
// ============================================================================

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>
/// Corrimiento de deriva GPS tal cual lo manda el motor. Nullable en los tres
/// campos: si el back omite uno, el panel usa 0/false en vez de explotar.
/// </summary>
public sealed class ShiftPosDto
{
    // [JsonPropertyName] EXPLÍCITO en cada propiedad: PropertyNameCaseInsensitive
    // NO cubre underscores ("north_cm" no matchea NorthCm sin atributo).
    [JsonPropertyName("north_cm")]   public double? NorthCm   { get; set; }
    [JsonPropertyName("east_cm")]    public double? EastCm    { get; set; }
    [JsonPropertyName("offsets_on")] public bool?   OffsetsOn { get; set; }
}

public sealed class ShiftPosClient
{
    // 3 s: el panel prefiere pintar "sin conexión" antes que quedarse esperando
    // con el operario manejando.
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions _docOpts = new() { AllowTrailingCommas = true };

    private readonly string _base;

    public ShiftPosClient(string baseUrl = "http://127.0.0.1:5180/")
        => _base = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    /// <summary>
    /// Estado inicial del corrimiento (el `loadState` del JS). null = no se
    /// pudo leer: el panel pinta "sin conexión" y deja los valores en 0/0/off.
    /// </summary>
    public async Task<ShiftPosDto?> GetAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_base + "api/aog/shift-pos", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // El WebHost del motor manda el JSON CON BOM: sin sacarlo, el parseo
            // explota y el motor perfectamente vivo se vería como "sin conexión".
            json = json.TrimStart('\uFEFF', '\u200B').Trim();
            return JsonSerializer.Deserialize<ShiftPosDto>(json, _jsonOpts);
        }
        catch { return null; }   // el panel pinta "sin conexión", jamás explota
    }

    /// <summary>
    /// Manda un comando de guiado. true = aceptado, false = rechazado por el
    /// motor, null = sin conexión.
    /// </summary>
    public async Task<bool?> SendCommandAsync(string cmd, CancellationToken ct = default)
    {
        try
        {
            // El comando es un identificador ASCII fijo (shift_* / offsets_*) más
            // un entero con signo: el JSON se arma a mano sin riesgo de escape.
            using var cont = new StringContent("{\"cmd\":\"" + cmd + "\"}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_base + "api/aog/guidance/command", cont, ct)
                                        .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            json = json.TrimStart('\uFEFF', '\u200B').Trim();
            using var doc = JsonDocument.Parse(json, _docOpts);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("ok", out var ok)) return null;
            return ok.ValueKind == JsonValueKind.True;
        }
        catch { return null; }
    }
}
