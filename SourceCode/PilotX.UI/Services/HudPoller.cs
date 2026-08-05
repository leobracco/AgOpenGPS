// HudPoller.cs
//
// Servicio chiquito que consume GET /api/aog/state del AgpWebHost en loop
// y dispara un evento cada vez que llega snapshot nueva. Lo usa el chrome
// de PilotX.Desktop para mostrar speed/heading/GPS-status arriba del
// WebView, sin tocar todavia el render OpenGL del mapa.
//
// Cadencia: 4 Hz (250 ms). El piloto.js del Hub usa 10 Hz, pero para una
// barra de texto 4 Hz es suficiente y deja menos huella en CPU.
//
// Nota: el endpoint /api/aog/state lo serializa EmbedIO con Swan, que
// emite PascalCase. Usamos PropertyNameCaseInsensitive para tolerar
// ambos formatos sin tener que conocer cual esta activo.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

/// <summary>
/// Subset minimo del AogStateSnapshot que necesita el HUD. No mapea todo
/// para no acoplarse a cambios del DTO core; solo lo que se renderiza.
/// </summary>
public sealed class HudSnapshot
{
    // Nombres C# PascalCase; la politica SnakeCaseLower del _jsonOpts los
    // mapea al snake_case que emite el servidor (is_job_started, avg_speed...).
    public bool IsJobStarted { get; set; }
    public double AvgSpeed { get; set; }   // km/h
    public double Heading { get; set; }    // rad
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double WorkedAreaTotalM2 { get; set; }
    public double ActualAreaCoveredM2 { get; set; }

    // ---- Cluster del piloto (arriba-centro del mapa) --------------------
    // Con el piloto activo, la pantalla principal muestra giro / salteo /
    // distancia a la línea. Vienen del MISMO /api/aog/state que el resto.
    public bool IsAutoSteerOn { get; set; }
    public bool IsYouTurnOn { get; set; }
    public bool HasBoundary { get; set; }
    /// <summary>Ancho del salto del giro en guías (1 = contigua). El menú
    /// muestra guías SALTEADAS = ancho − 1.</summary>
    public int YouTurnSkipWidth { get; set; }
    /// <summary>Desvío respecto de la guía (m). 0 exacto suele ser "sin guía".</summary>
    public double CrossTrackErrorM { get; set; }
    /// <summary>Cantidad de guías del lote (tracks_total). El host la usa para
    /// cerrar el diálogo de Guías cuando aparece una nueva — la página no
    /// puede avisar (ver OnDialogNavigated en MainWindow).</summary>
    public int TracksTotal { get; set; }

    // ---- Campos para el mini-mapa cockpit -------------------------------
    // En metros locales, mismo frame de coordenadas que las boundaries.
    public double PivotEasting { get; set; }
    public double PivotNorthing { get; set; }
    public double ToolWidth { get; set; }

    /// <summary>Posición y rumbo de la HERRAMIENTA (no del tractor: el
    /// implemento va rezagado y en curva apunta distinto). El mapa dibuja ahí
    /// el sprite del implemento.</summary>
    public double ToolEasting { get; set; }
    public double ToolNorthing { get; set; }
    public double ToolHeading { get; set; }

    /// <summary>Distancia entre ejes (m) — tamaño del vehículo en el mapa.</summary>
    public double Wheelbase { get; set; }
    /// <summary>Trocha (m) — ancho del vehículo en el mapa.</summary>
    public double TrackWidth { get; set; }
    /// <summary>Ángulo del sensor de dirección (grados). El mapa gira con esto
    /// las ruedas delanteras, que se dibujan aparte del cuerpo.</summary>
    public double SteerAngleDeg { get; set; }

    // Primer ring = contorno exterior; rings siguientes = islas/drive-thru.
    public List<List<FieldPoint>>? Boundaries { get; set; }

    /// <summary>Línea de CABECERA por lindero (vacía si no se construyó).
    /// El motor ya la servía (head_lands en el state) — la UI no la leía y la
    /// cabecera construida no se veía en el mapa principal.</summary>
    public List<List<FieldPoint>>? Headlands { get; set; }

    /// <summary>Lindero que se está grabando manejando, todavía sin cerrar.
    /// null cuando no hay grabación. Va aparte de <see cref="Boundaries"/>:
    /// es una tira abierta que crece, no un anillo confirmado.</summary>
    public List<FieldPoint>? BoundaryBeingMade { get; set; }

    // ---- Datos del lote (consumidos por FieldDataPanel nativo) ----------
    public string? CurrentFieldDirectory { get; set; }
    public int NumSections { get; set; }
    public bool[]? SectionOnRequest { get; set; }
    public string? VehicleType { get; set; }
    public string? VehicleBrand { get; set; }
    public double ShapeCurrentDose { get; set; }
    public bool ShapeIsInside { get; set; }
}

/// <summary>Punto 2D en metros locales (Easting/Northing).</summary>
public sealed class FieldPoint
{
    public double E { get; set; }
    public double N { get; set; }
}

public sealed class HudPoller : IDisposable
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        // El servidor (AgpJson) serializa en snake_case con
        // JsonNamingPolicy.SnakeCaseLower. Usamos la MISMA politica aca para
        // que los nombres C# PascalCase mapeen 1:1 (case-insensitive NO cubre
        // los guiones bajos, por eso los campos multi-palabra no deserializaban).
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _baseUrl;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Se dispara cada vez que el poller obtiene un snapshot exitoso. Se
    /// invoca en el thread del Task; el listener (MainWindow) debe hacer
    /// marshalling al UI thread con Dispatcher.UIThread.Post().
    /// </summary>
    public event Action<HudSnapshot>? SnapshotReceived;

    /// <summary>
    /// Se dispara cuando una request falla (host caido, timeout, etc).
    /// El listener pinta un estado "desconectado" en el HUD.
    /// </summary>
    public event Action<Exception>? PollFailed;

    public HudPoller(string baseUrl = "http://127.0.0.1:5180/", int intervalMs = 250)
    {
        // Normalizo trailing slash para concatenar con "api/aog/state".
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
        _interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var url = _baseUrl + "api/aog/state";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var snap = JsonSerializer.Deserialize<HudSnapshot>(json, _jsonOpts);
                if (snap != null) SnapshotReceived?.Invoke(snap);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                PollFailed?.Invoke(ex);
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => Stop();
}
