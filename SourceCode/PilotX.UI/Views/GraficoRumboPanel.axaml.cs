// GraficoRumboPanel.axaml.cs
//
// Reemplazo nativo de pages/grafico-rumbo.html (ex ventana WinForms
// FormGraphHeading). UI Avalonia + GraficosClient (HTTP a EmbedIO). Sin
// WebView, sin JS. El dibujo lo hace GraficoLineal, compartido con los otros
// tres gráficos.
//
// La lógica es la MISMA que js/grafico-rumbo.js, paso por paso:
//   · GET /api/aog/graph-heading cada 200 ms (5 Hz)
//   · g = Number(gps_heading_deg) || 0 ; m = Number(imu_heading_deg) || 0
//   · buffers rodantes de 120 muestras
//   · el eje NO se centra en cero (los rumbos son absolutos 0-360°): se
//     auto-encuadra al [min, max] de AMBAS series con 10% de padding, y con un
//     ancho mínimo de 2° para que una señal casi plana no colapse la escala.
//     Sin muestras: lo=0, hi=360 (que con el padding da -36 / 396)
//   · lecturas GPS/IMU con toFixed(1); "Diferencia" = (g − m).toFixed(1)
//   · etiquetas del eje con toFixed(0)
//   · grilla en 0.25, 0.5 y 0.75 (las TRES suaves; acá no hay línea de cero)
//   · NO hay botones de escala: el encuadre es siempre automático
//
// API: Attach(GraficosClient) arranca el muestreo; Detach() lo corta.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public partial class GraficoRumboPanel : UserControl, IPanelEmbebible
{
    private const int PollMs = 200;

    private const string TxtSinDatos    = "—";
    private const string TxtEnVivo      = "en vivo";
    private const string TxtSinConexion = "sin conexión";

    private static string T(string t) => Traductor.T(t);

    private GraficosClient? _client;
    private CancellationTokenSource? _cts;

    private GraficoLineal? _lienzo;
    private SerieGrafico? _serGps;
    private SerieGrafico? _serImu;

    private bool _lastOk = false;
    private bool _idiomaEnganchado = false;   // suscripción viva a Traductor.IdiomaCambio
    private string _estado = TxtSinDatos;

    private double _ultGps = 0, _ultImu = 0;

    /// <summary>Lo invoca el ✕ del header. El host (MainWindow) engancha acá su
    /// CloseGraficoRumbo.</summary>
    public Action? OnRequestCerrar { get; set; }

    public GraficoRumboPanel()
    {
        InitializeComponent();
        Traductor.Aplicar(this);

        ArmarLienzo();
        Dibujar();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ---------- ciclo de vida ------------------------------------------------

    public void Attach(GraficosClient client)
    {
        _client = client;
        // Los tokens del theme recién se resuelven con el control YA en el
        // árbol: en el ctor el lookup cae en los fallbacks (mismos hex).
        ResolverColores();
        // Se engancha el idioma acá y no en el ctor: IdiomaCambio es estático y
        // una suscripción de por vida deja al panel repintando desde el fondo,
        // cerrado, para siempre. Como puede haber cambiado el idioma con el
        // panel cerrado, al abrir se repinta todo lo dinámico.
        EngancharIdioma();
        RepintarVivo();
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = BucleAsync(_cts.Token);
    }

    public void Detach()
    {
        SoltarIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    /// <summary>Aplicar() restaura el texto CACHEADO del XAML: al cambiar de
    /// idioma la pill y las lecturas volverían a su valor inicial. Se repintan
    /// después, en otro turno del dispatcher (por eso el Post).</summary>
    private void OnIdiomaCambio() => Dispatcher.UIThread.Post(RepintarVivo);

    private void EngancharIdioma()
    {
        if (_idiomaEnganchado) return;
        Traductor.IdiomaCambio += OnIdiomaCambio;
        _idiomaEnganchado = true;
    }

    private void SoltarIdioma()
    {
        if (!_idiomaEnganchado) return;
        Traductor.IdiomaCambio -= OnIdiomaCambio;
        _idiomaEnganchado = false;
    }

    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        var raiz = this.FindControl<Grid>("ContenidoRaiz");
        if (raiz != null) raiz.Margin = new Thickness(0);
    }

    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<Border>("HeaderPill"));

    private void OnCerrarClick(object? s, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    // ---------- lienzo -------------------------------------------------------

    private void ArmarLienzo()
    {
        _lienzo = this.FindControl<GraficoLineal>("Lienzo");
        if (_lienzo == null) return;

        // Encuadre por rango, sin línea de cero y con los TRES tercios suaves.
        _lienzo.Modo = ModoEjeGrafico.RangoAuto;
        _lienzo.LineaCero = false;
        _lienzo.FraccionesGrilla = new[] { 0.25, 0.5, 0.75 };
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));

        _serGps = _lienzo.NuevaSerie(RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E")));
        _serImu = _lienzo.NuevaSerie(Color.Parse("#3D87C6"));
    }

    /// <summary>Relee del theme lo que en el ctor cayó en fallback. El azul
    /// #3D87C6 NO tiene token en PilotXTheme: queda literal (anotado).</summary>
    private void ResolverColores()
    {
        if (_lienzo == null) return;
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));
        if (_serGps != null)
            _serGps.Color = RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E"));
        _lienzo.Redibujar();
    }

    private void Dibujar()
    {
        if (_lienzo == null) return;
        var (lo, hi) = RangoActual();
        _lienzo.RangoLo = lo;
        _lienzo.RangoHi = hi;
        _lienzo.Redibujar();

        var max = this.FindControl<TextBlock>("AxisMax");
        var min = this.FindControl<TextBlock>("AxisMin");
        if (max != null) max.Text = NumeroJs.Fijo(hi, 0);
        if (min != null) min.Text = NumeroJs.Fijo(lo, 0);
    }

    /// <summary>`currentRange()` del JS, calcado.</summary>
    private (double lo, double hi) RangoActual()
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        if (_lienzo != null) (lo, hi) = _lienzo.MinMax();

        if (!double.IsFinite(lo) || !double.IsFinite(hi)) { lo = 0; hi = 360; }

        double span = hi - lo;
        if (span < 2)
        {
            double mid = (hi + lo) / 2;
            lo = mid - 1;
            hi = mid + 1;
            span = 2;
        }
        double pad = span * 0.1;
        return (lo - pad, hi + pad);
    }

    // ---------- muestreo -----------------------------------------------------

    private async Task BucleAsync(CancellationToken ct)
    {
        await PollAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await PollAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var d = await _client.GetHeadingAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Aplicar(d));
    }

    private void Aplicar(HeadingGraphSampleDto? d)
    {
        if (d == null)
        {
            // Mismo detalle del JS: si el PRIMER pedido falla, la pill se queda
            // en "—" (lastOk todavía es false). Se replica tal cual.
            if (_lastOk) { _estado = TxtSinConexion; PintarEstado(); _lastOk = false; }
            return;
        }

        double g = NumeroJs.ANumero(d.GpsHeadingDeg);
        double m = NumeroJs.ANumero(d.ImuHeadingDeg);

        _serGps?.Empujar(g);
        _serImu?.Empujar(m);

        _ultGps = g;
        _ultImu = m;
        PintarLecturas();

        if (!_lastOk) { _estado = TxtEnVivo; PintarEstado(); _lastOk = true; }
        Dibujar();
    }

    private void PintarLecturas()
    {
        var gps  = this.FindControl<TextBlock>("ValGps");
        var imu  = this.FindControl<TextBlock>("ValImu");
        var diff = this.FindControl<TextBlock>("ValDiff");
        if (gps  != null) gps.Text  = NumeroJs.Fijo(_ultGps, 1);
        if (imu  != null) imu.Text  = NumeroJs.Fijo(_ultImu, 1);
        // Diferencia CRUDA g − m: el JS no la normaliza a ±180°, así que
        // cruzando el norte salta a ~±360. Se replica (no se "arregla").
        if (diff != null) diff.Text = NumeroJs.Fijo(_ultGps - _ultImu, 1);
    }

    private void PintarEstado()
    {
        var st = this.FindControl<TextBlock>("StatusText");
        if (st != null) st.Text = T(_estado);
    }

    private void RepintarVivo()
    {
        PintarEstado();
        PintarLecturas();
        Dibujar();
    }
}
