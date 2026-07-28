// WidgetQuantiXClient.cs
//
// Cliente del overlay de QuantiX que se ve SOBRE el mapa mientras se trabaja.
// Es el mismo backend que alimenta al widget HTML de la app WinForms, así que
// los dos muestran exactamente los mismos números:
//
//   GET  /api/widget-quantix/state       -> nodos + motores (dosis ya en las
//                                           unidades del operario, nunca pps)
//   POST /api/widget-quantix/manual-all  -> MAN/AUTO + dosis para TODOS
//
// La dosis viaja resuelta desde el backend (real y objetivo, en kg/ha o sem/m).
// Acá no se recalcula nada: si el overlay y la máquina mostraran números
// distintos sería imposible saber cuál creer.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class QxWidgetMotor
{
    [JsonPropertyName("idx")]          public int     Idx         { get; set; }
    [JsonPropertyName("nombre")]       public string? Nombre      { get; set; }
    [JsonPropertyName("manual_mode")]  public bool    ManualMode  { get; set; }
    [JsonPropertyName("manual_dosis")] public double  ManualDosis { get; set; }
    /// <summary>"kg_ha" o "sem_m". Decide cómo se rotula la dosis.</summary>
    [JsonPropertyName("unidad")]       public string? Unidad      { get; set; }
    [JsonPropertyName("objetivo")]     public double  Objetivo    { get; set; }
    /// <summary>Lo que el motor está entregando de verdad, según su encoder.</summary>
    [JsonPropertyName("real")]         public double  Real        { get; set; }
    [JsonPropertyName("rpm")]          public int     Rpm         { get; set; }
    /// <summary>El motor está trabajando (nodo online y hay dosis o pulsos).</summary>
    [JsonPropertyName("activo")]       public bool    Activo      { get; set; }
}

public sealed class QxWidgetNodo
{
    [JsonPropertyName("uid")]     public string? Uid    { get; set; }
    [JsonPropertyName("nombre")]  public string? Nombre { get; set; }
    [JsonPropertyName("online")]  public bool    Online { get; set; }
    [JsonPropertyName("motores")] public List<QxWidgetMotor>? Motores { get; set; }
}

public sealed class QxWidgetState
{
    [JsonPropertyName("ok")]        public bool   Ok       { get; set; }
    [JsonPropertyName("connected")] public bool   Connected { get; set; }
    [JsonPropertyName("speed_kmh")] public double SpeedKmh { get; set; }
    [JsonPropertyName("ancho_m")]   public double AnchoM   { get; set; }
    [JsonPropertyName("nodos")]     public List<QxWidgetNodo>? Nodos { get; set; }
}

public sealed class WidgetQuantiXClient
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

    public WidgetQuantiXClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<QxWidgetState?> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/widget-quantix/state", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<QxWidgetState>(json, _jsonOpts);
        }
        catch { return null; }
    }

    /// <summary>MAN/AUTO + dosis para todos los motores a la vez. Es lo que se
    /// usa desde la cabina: el operario corrige la dosis del equipo, no
    /// motor por motor.</summary>
    public async Task<bool> SetManualAllAsync(bool manual, double dosis, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { manual, dosis });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + "api/widget-quantix/manual-all", content, ct)
                                        .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Cuánto sube o baja cada toque de + / −. Escalonado como en el
    /// widget HTML: con dosis chicas hace falta precisión fina, con dosis
    /// grandes moverse de a 0,1 sería inusable con guante.</summary>
    public static double PasoDosis(double valor)
    {
        double v = Math.Abs(valor);
        if (v < 5) return 0.1;
        if (v < 30) return 0.5;
        if (v < 100) return 1;
        if (v < 500) return 5;
        return 10;
    }

    public static string FormatoDosis(double valor, string? unidad)
    {
        bool semillas = string.Equals(unidad, "sem_m", StringComparison.OrdinalIgnoreCase);
        double v = Math.Abs(valor);
        int dec = semillas || v < 10 ? 1 : 0;
        return valor.ToString("F" + dec.ToString(System.Globalization.CultureInfo.InvariantCulture),
                              System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string EtiquetaUnidad(string? unidad)
        => string.Equals(unidad, "sem_m", StringComparison.OrdinalIgnoreCase) ? "sem/m" : "kg/ha";
}
