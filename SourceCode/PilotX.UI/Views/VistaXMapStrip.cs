// ============================================================================
// VistaXMapStrip — franja MÍNIMA de sensores VistaX sobre el mapa (Avalonia).
//
// Réplica nativa de la franja HTML del stack WinForms (vistax-live.html),
// con el lenguaje de los monitores clásicos de siembra: una barrita por
// surco, relleno de abajo hacia arriba = ratio real/objetivo, color = estado.
// Tapado/sin datos/sección cortada quedan como bloque sólido.
//
// Se auto-minimiza cerca de la CABECERA (distancia del pivote a la línea de
// giro, del /api/aog/state): ahí el operario necesita el mapa para el giro,
// no los sensores. Histéresis de 5 m. Tocarla abre el panel VistaX completo.
//
// Vive en MapOverlaysHost (Canvas sobre el mapa) y se prende/apaga con el
// mismo toggle vx_overlay de overlayPrefs que el resto de los overlays.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public sealed class VistaXMapStrip : Control
{
    private const double AltoNormal = 40;
    private const double AltoMini = 14;
    private const double AnchoBarra = 13;
    private const double GapBarra = 2;
    private const int UmbralCabeceraM = 25;   // igual que vx_mini_cabecera_m WinForms
    private const int HisteresisM = 5;

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

    // Paleta sincronizada con vistax-live.html / VistaXPanel.
    private static readonly IBrush _riel     = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#2A332C"));
    private static readonly IBrush _ok       = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _bajo     = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#B5673A"));
    private static readonly IBrush _exceso   = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#3E7DBA"));
    private static readonly IBrush _tapado   = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#0D0D0D"));
    private static readonly IBrush _noData   = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#3A423C"));
    private static readonly IBrush _cortada  = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#23282456"));
    private static readonly IBrush _fondo    = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#E6F5F7F4"));
    private static readonly IBrush _texto    = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#535E54"));
    private static readonly Typeface _tf     = new Typeface("Segoe UI", weight: FontWeight.Bold);

    private VistaXClient? _client;
    private CancellationTokenSource? _cts;
    private VistaXLiveSnapshot? _live;
    private bool _mini;
    private string _baseUrl = "http://127.0.0.1:5180/";

    /// <summary>Tocar la franja abre el panel VistaX completo.</summary>
    public Action? OnTap { get; set; }

    public bool Mini => _mini;

    public VistaXMapStrip()
    {
        PointerPressed += (_, e) => { OnTap?.Invoke(); e.Handled = true; };
    }

    public void Attach(VistaXClient client, string baseUrl)
    {
        Detach();
        _client = client;
        if (!string.IsNullOrEmpty(baseUrl))
            _baseUrl = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        _cts = new CancellationTokenSource();
        _ = PollAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _client = null;
        _live = null;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var c = _client;
                if (c != null) _live = await c.GetLiveAsync(ct).ConfigureAwait(false);

                // Distancia a cabecera cada 2 ticks (1 s): decide el auto-mini.
                if (tick++ % 2 == 0) await LeerCabeceraAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { _live = null; }

            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Width = AnchoDeseado();
                    Height = _mini ? AltoMini : AltoNormal;
                    InvalidateVisual();
                });
            }
            catch { }

            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task LeerCabeceraAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/aog/state", ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            double d = doc.RootElement.TryGetProperty("distancia_cabecera_m", out var v)
                ? v.GetDouble() : -2222;
            // Histéresis: entra en mini a < umbral, sale a > umbral + 5.
            if (!_mini && d > 0 && d < UmbralCabeceraM) _mini = true;
            else if (_mini && (d <= 0 || d > UmbralCabeceraM + HisteresisM)) _mini = false;
        }
        catch { /* sin state: mantener el modo actual */ }
    }

    private List<VistaXSurcoLive> SurcosPlanos()
    {
        var lista = new List<VistaXSurcoLive>();
        var trenes = _live?.Trenes;
        if (trenes == null) return lista;
        foreach (var t in trenes)
        {
            if (t?.Surcos == null) continue;
            foreach (var s in t.Surcos)
                if (s != null) lista.Add(s);
        }
        lista.Sort((a, b) => a.Bajada.CompareTo(b.Bajada));
        return lista;
    }

    private double AnchoDeseado()
    {
        int n = Math.Max(1, SurcosPlanos().Count);
        return n * (AnchoBarra + GapBarra) + GapBarra + 8;
    }

    public override void Render(DrawingContext ctx)
    {
        var surcos = SurcosPlanos();
        double alto = Bounds.Height;
        double ancho = Bounds.Width;
        if (alto < 4 || ancho < 20) return;

        ctx.DrawRectangle(_fondo, null, new RoundedRect(new Rect(0, 0, ancho, alto), 5));
        if (surcos.Count == 0) return;

        bool monActivo = _live?.MonitoreoActivo ?? false;
        double x = GapBarra + 4;
        double margen = 3;
        double hBarra = alto - margen * 2;

        foreach (var s in surcos)
        {
            var rect = new Rect(x, margen, AnchoBarra, hBarra);
            string estado = (s.Estado ?? "no-data").ToLowerInvariant();

            if (!monActivo || estado == "no-data" || estado == "idle")
            {
                ctx.DrawRectangle(_noData, null, new RoundedRect(rect, 2));
            }
            else if (estado == "seccion-off")
            {
                ctx.DrawRectangle(_cortada, null, new RoundedRect(rect, 2));
            }
            else if (estado == "tapado" || estado == "falla")
            {
                ctx.DrawRectangle(_tapado, null, new RoundedRect(rect, 2));
            }
            else
            {
                // Barra de NIVEL: riel + relleno con el ratio, color por estado.
                ctx.DrawRectangle(_riel, null, new RoundedRect(rect, 2));
                double ratio = s.RatioObjetivo > 0 ? s.RatioObjetivo
                    : (s.Objetivo > 0 ? s.Spm / s.Objetivo : 0);
                double pct = Math.Max(0.06, Math.Min(1.0, ratio));
                double hFill = hBarra * pct;
                var fill = estado == "bajo" ? _bajo : (estado == "exceso" ? _exceso : _ok);
                ctx.DrawRectangle(fill, null,
                    new RoundedRect(new Rect(x, margen + (hBarra - hFill), AnchoBarra, hFill), 2));
            }

            // Número del surco solo en modo normal (en mini no entra).
            if (!_mini && hBarra >= 20)
            {
                var ft = new FormattedText(s.Bajada.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _tf, 8, _texto);
                ctx.DrawText(ft, new Point(x + (AnchoBarra - ft.Width) / 2, alto - margen - 9));
            }

            x += AnchoBarra + GapBarra;
        }
    }
}
