// GraficoCorreccionPanel.axaml.cs
//
// Reemplazo nativo de pages/grafico-correccion.html — "Chequeo de roll" (ex
// ventana WinForms FormCorrection). UI Avalonia + GraficosClient (HTTP a
// EmbedIO). Sin WebView, sin JS. El dibujo lo hace GraficoLineal, compartido
// con los otros tres gráficos.
//
// La lógica es la MISMA que js/grafico-correccion.js, paso por paso:
//   · GET /api/aog/graph-correction cada 200 ms (5 Hz)
//   · corr / east / uncorr = Number(...) || 0
//   · Poste  : la serie de corrección se grafica tal cual (corr)
//     Movim. : se grafica corr + uncorr   (mismo criterio que isPole)
//   · Congelar: NO se empujan muestras a ningún buffer (el gráfico queda
//     quieto), pero las LECTURAS numéricas se siguen actualizando — es así en
//     el JS y se replica
//   · buffers rodantes de 120 muestras, eje simétrico ±escala
//   · orden de dibujo: sin corregir (ámbar), Este GPS (azul), corrección
//     (verde) — la corrección queda ARRIBA
//   · lecturas con toFixed(2); Roll IMU = toFixed(1) + "°" si roll_present,
//     "—" si no
//   · escala: arranca en AUTO (autoScale = true, yScale = 0.1). Auto =
//     Math.ceil(maxAbs*100)/100 con piso 0.05. "+ Escala" duplica hasta 10,
//     "− Escala" divide hasta un piso de 0.02, ambos pasando por toFixed(2)
//   · la pill dice "en vivo" al primer éxito y "sin conexión" al primer fallo
//     DESPUÉS de un éxito
//
// API: Attach(GraficosClient) arranca el muestreo; Detach() lo corta.

using System;
using System.Globalization;
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

public partial class GraficoCorreccionPanel : UserControl, IPanelEmbebible
{
    private const int PollMs = 200;

    // Textos EXACTOS de la página (grafico-correccion.js / .html).
    private const string TxtSinDatos    = "—";
    private const string TxtEnVivo      = "en vivo";
    private const string TxtSinConexion = "sin conexión";
    private const string TxtCongelar    = "Congelar";
    private const string TxtReanudar    = "Reanudar";
    private const string TxtPoste       = "Poste";
    private const string TxtMovimiento  = "Movimiento";

    private static string T(string t) => Traductor.T(t);

    private GraficosClient? _client;
    private CancellationTokenSource? _cts;

    private GraficoLineal? _lienzo;
    private SerieGrafico? _serUncorr;   // se dibuja primero (abajo)
    private SerieGrafico? _serEast;
    private SerieGrafico? _serCorr;     // se dibuja última (arriba)

    // Escala del eje Y. yMax = ±yScale (m); autoScale ajusta a datos.
    // OJO: acá el default es AUTO encendido (al revés que dirección y XTE).
    private double _yScale = 0.1;
    private bool _autoScale = true;

    // Toggles cliente (igual que FormCorrection).
    private bool _isFrozen = false;     // btnScroll: congela el avance del buffer
    private bool _isPole = true;        // btnPoleOrMoving

    private bool _lastOk = false;
    private bool _idiomaEnganchado = false;   // suscripción viva a Traductor.IdiomaCambio
    private string _estado = TxtSinDatos;

    private double _ultCorr = 0, _ultEast = 0, _ultUncorr = 0, _ultRoll = 0;
    private bool _ultRollPresente = false;

    // Pinceles del estado on/off de los dos toggles.
    private IBrush? _pinAcento, _pinSuperficie2, _pinTexto, _pinBorde;

    /// <summary>Lo invoca el ✕ del header. El host (MainWindow) engancha acá su
    /// CloseChequeoRoll.</summary>
    public Action? OnRequestCerrar { get; set; }

    public GraficoCorreccionPanel()
    {
        InitializeComponent();
        Traductor.Aplicar(this);

        ResolverPinceles();
        ArmarLienzo();
        PintarToggles();
        Dibujar();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ---------- ciclo de vida ------------------------------------------------

    public void Attach(GraficosClient client)
    {
        _client = client;
        // Los tokens del theme recién se resuelven con el control YA en el
        // árbol: en el ctor el lookup cae en los fallbacks (mismos hex).
        ResolverPinceles();
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
    /// idioma la pill volvería a "—", las lecturas a "0.00" y los toggles a su
    /// texto inicial. Se repintan después, en otro turno del dispatcher.</summary>
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

    private void ResolverPinceles()
    {
        _pinAcento      = new SolidColorBrush(RecursoColor.Buscar(this, "PilotXPanelAccentColor",     Color.Parse("#4ABA3E")));
        _pinSuperficie2 = new SolidColorBrush(RecursoColor.Buscar(this, "PilotXPanelSurface2Color",   Color.Parse("#EDF1EC")));
        _pinTexto       = new SolidColorBrush(RecursoColor.Buscar(this, "PilotXPanelTextColor",       Color.Parse("#101612")));
        _pinBorde       = new SolidColorBrush(RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5")));
    }

    private void ArmarLienzo()
    {
        _lienzo = this.FindControl<GraficoLineal>("Lienzo");
        if (_lienzo == null) return;

        _lienzo.Modo = ModoEjeGrafico.CentradoEnCero;
        _lienzo.LineaCero = true;
        _lienzo.FraccionesGrilla = new[] { 0.25, 0.75 };
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));

        // Orden de alta = orden de dibujo del JS: uncorr, east, corr.
        // Ámbar OSCURECIDO: el #E0A030 del original daba 2,29:1 sobre el blanco
        // de la tarjeta y un trazo tiene que llegar a 3:1 para verse. #B07A10
        // da 4,3:1 y es el mismo ámbar, más quemado.
        _serUncorr = _lienzo.NuevaSerie(Color.Parse("#B07A10"));
        _serEast   = _lienzo.NuevaSerie(Color.Parse("#3D87C6"));
        _serCorr   = _lienzo.NuevaSerie(RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E")));
    }

    /// <summary>Relee del theme lo que en el ctor cayó en fallback. El azul
    /// #3D87C6 y el ámbar #B07A10 NO tienen token en PilotXTheme: quedan
    /// literales (anotado).</summary>
    private void ResolverColores()
    {
        if (_lienzo == null) return;
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));
        if (_serCorr != null)
            _serCorr.Color = RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E"));
        _lienzo.Redibujar();
    }

    private void Dibujar()
    {
        if (_lienzo == null) return;
        double escala = EscalaActual();
        _lienzo.Escala = escala;
        _lienzo.Redibujar();

        var max = this.FindControl<TextBlock>("AxisMax");
        var min = this.FindControl<TextBlock>("AxisMin");
        if (max != null) max.Text = NumeroJs.Fijo(escala, 2);
        if (min != null) min.Text = "-" + NumeroJs.Fijo(escala, 2);
    }

    /// <summary>`currentScale()`: fija, o automática con piso 0.05 m redondeada
    /// a 2 decimales hacia arriba.</summary>
    private double EscalaActual()
    {
        if (!_autoScale) return _yScale;
        double m = _lienzo?.MaxAbsoluto(0.05) ?? 0.05;
        return Math.Ceiling(m * 100) / 100;
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
        var d = await _client.GetCorrectionAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Aplicar(d));
    }

    private void Aplicar(CorrectionGraphSampleDto? d)
    {
        if (d == null)
        {
            // Mismo detalle del JS: si el PRIMER pedido falla, la pill se queda
            // en "—" (lastOk todavía es false). Se replica tal cual.
            if (_lastOk) { _estado = TxtSinConexion; PintarEstado(); _lastOk = false; }
            return;
        }

        double corr   = NumeroJs.ANumero(d.CorrectionDistance);
        double east   = NumeroJs.ANumero(d.Easting);
        double uncorr = NumeroJs.ANumero(d.UncorrectedEasting);

        // Poste: la corrección es tal cual. Movimiento: corr + sin-corregir
        // (mismo criterio que isPole en FormCorrection).
        double corrPlot = _isPole ? corr : (corr + uncorr);

        if (!_isFrozen)
        {
            _serCorr?.Empujar(corrPlot);
            _serEast?.Empujar(east);
            _serUncorr?.Empujar(uncorr);
        }

        _ultCorr = corr;
        _ultEast = east;
        _ultUncorr = uncorr;
        _ultRoll = NumeroJs.ANumero(d.RollDegrees);
        _ultRollPresente = d.RollPresent;
        PintarLecturas();

        if (!_lastOk) { _estado = TxtEnVivo; PintarEstado(); _lastOk = true; }
        Dibujar();
    }

    private void PintarLecturas()
    {
        var corr   = this.FindControl<TextBlock>("ValCorr");
        var east   = this.FindControl<TextBlock>("ValEast");
        var uncorr = this.FindControl<TextBlock>("ValUncorr");
        var roll   = this.FindControl<TextBlock>("ValRoll");
        if (corr   != null) corr.Text   = NumeroJs.Fijo(_ultCorr, 2);
        if (east   != null) east.Text   = NumeroJs.Fijo(_ultEast, 2);
        if (uncorr != null) uncorr.Text = NumeroJs.Fijo(_ultUncorr, 2);
        if (roll   != null) roll.Text   = _ultRollPresente
                                            ? NumeroJs.Fijo(_ultRoll, 1) + "°"
                                            : T(TxtSinDatos);
    }

    private void PintarEstado()
    {
        var st = this.FindControl<TextBlock>("StatusText");
        if (st != null) st.Text = T(_estado);
    }

    /// <summary>Estilo y texto de los dos toggles. El "on" del CSS era verde de
    /// marca con texto blanco; sobre fondo claro el blanco sobre #4ABA3E da
    /// 2.9:1 y al sol no se lee, así que el relleno verde lleva el texto OSCURO
    /// del theme (7.3:1), que es la regla documentada de la paleta clara.</summary>
    private void PintarToggles()
    {
        var freeze = this.FindControl<Button>("BtnFreeze");
        var modo   = this.FindControl<Button>("BtnMode");

        if (freeze != null)
        {
            freeze.Content = T(_isFrozen ? TxtReanudar : TxtCongelar);
            freeze.Background  = _isFrozen ? _pinAcento : _pinSuperficie2;
            freeze.BorderBrush = _isFrozen ? _pinAcento : _pinBorde;
            freeze.Foreground  = _pinTexto;
        }
        if (modo != null)
        {
            modo.Content = T(_isPole ? TxtPoste : TxtMovimiento);
            modo.Background  = _isPole ? _pinAcento : _pinSuperficie2;
            modo.BorderBrush = _isPole ? _pinAcento : _pinBorde;
            modo.Foreground  = _pinTexto;
        }
    }

    private void RepintarVivo()
    {
        PintarEstado();
        PintarLecturas();
        PintarToggles();
        Dibujar();
    }

    // ---------- controles de escala (solo cliente) ---------------------------

    private void OnEscalaMasClick(object? s, RoutedEventArgs e)
    {
        _autoScale = false;
        // +(yScale * 2).toFixed(2): el JS redondea a 2 decimales ANTES de
        // comparar contra el techo. Se replica igual.
        _yScale = Math.Min(10, ADosDecimales(_yScale * 2));
        Dibujar();
    }

    private void OnEscalaMenosClick(object? s, RoutedEventArgs e)
    {
        _autoScale = false;
        _yScale = Math.Max(0.02, ADosDecimales(_yScale / 2));
        Dibujar();
    }

    private void OnAutoClick(object? s, RoutedEventArgs e)
    {
        // Igual que la página: el botón no cambia de aspecto al activarse.
        _autoScale = !_autoScale;
        Dibujar();
    }

    // ---------- toggles (solo cliente) ---------------------------------------

    private void OnCongelarClick(object? s, RoutedEventArgs e)
    {
        _isFrozen = !_isFrozen;
        PintarToggles();
    }

    private void OnModoClick(object? s, RoutedEventArgs e)
    {
        // Cambia SOLO cómo se grafican las muestras NUEVAS: lo que ya está en
        // el buffer se queda como se guardó (igual que el JS).
        _isPole = !_isPole;
        PintarToggles();
    }

    /// <summary>`+(v).toFixed(2)` de JavaScript: formatear a 2 decimales y
    /// volver a número.</summary>
    private static double ADosDecimales(double v)
        => double.Parse(NumeroJs.Fijo(v, 2), NumberStyles.Float, CultureInfo.InvariantCulture);
}
