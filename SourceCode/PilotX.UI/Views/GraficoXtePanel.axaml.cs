// GraficoXtePanel.axaml.cs
//
// Reemplazo nativo de pages/grafico-xte.html (ex ventana WinForms FormGraphXTE).
// UI Avalonia + GraficosClient (HTTP a EmbedIO). Sin WebView, sin JS. El dibujo
// lo hace GraficoLineal, compartido con los otros tres gráficos.
//
// La lógica es la MISMA que js/grafico-xte.js, paso por paso:
//   · GET /api/aog/graph-xte cada 200 ms (5 Hz)
//   · he = Number(heading_error_deg) || 0 ; xt = Number(xte_cm) || 0
//   · el XTE se ACOTA a ±5120 cm ANTES de entrar al buffer: cuando el tractor
//     no está sobre ninguna guía el motor manda un centinela enorme que si no
//     destruye la escala (la ventana vieja tenía escala fija y lo recortaba)
//   · buffers rodantes de 120 muestras, eje simétrico ±escala
//   · lecturas: error de rumbo con toFixed(1), XTE con Math.round (entero)
//   · escala fija 80 por defecto; "+ Escala" duplica hasta 5120, "− Escala"
//     divide (Math.round) hasta un piso de 10, "Auto" alterna a escala
//     automática = Math.ceil(maxAbs/10)*10 con piso 10
//   · la pill dice "en vivo" al primer éxito y "sin conexión" al primer fallo
//     DESPUÉS de un éxito
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

public partial class GraficoXtePanel : UserControl, IPanelEmbebible
{
    private const int PollMs = 200;

    // XTE_MAX_CM del JS: tope del centinela "sin guía".
    private const double XteMaxCm = 5120;

    private const string TxtSinDatos    = "—";
    private const string TxtEnVivo      = "en vivo";
    private const string TxtSinConexion = "sin conexión";

    private static string T(string t) => Traductor.T(t);

    private GraficosClient? _client;
    private CancellationTokenSource? _cts;

    private GraficoLineal? _lienzo;
    private SerieGrafico? _serHe;
    private SerieGrafico? _serXte;

    // Escala del eje Y. yMax = ±yScale (cm/°); autoScale ajusta a datos.
    private double _yScale = 80;
    private bool _autoScale = false;

    private bool _lastOk = false;
    private bool _idiomaEnganchado = false;   // suscripción viva a Traductor.IdiomaCambio
    private string _estado = TxtSinDatos;

    private double _ultHe = 0, _ultXte = 0;

    /// <summary>Lo invoca el ✕ del header. El host (MainWindow) engancha acá su
    /// CloseGraficoXte.</summary>
    public Action? OnRequestCerrar { get; set; }

    public GraficoXtePanel()
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

        _lienzo.Modo = ModoEjeGrafico.CentradoEnCero;
        _lienzo.LineaCero = true;
        _lienzo.FraccionesGrilla = new[] { 0.25, 0.75 };
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));

        // Orden de alta = orden de dibujo del JS: error de rumbo y después XTE.
        _serHe  = _lienzo.NuevaSerie(RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E")));
        _serXte = _lienzo.NuevaSerie(Color.Parse("#3D87C6"));
    }

    /// <summary>Relee del theme lo que en el ctor cayó en fallback. El azul
    /// #3D87C6 NO tiene token en PilotXTheme: queda literal (anotado).</summary>
    private void ResolverColores()
    {
        if (_lienzo == null) return;
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));
        if (_serHe != null)
            _serHe.Color = RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E"));
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
        if (max != null) max.Text = NumeroJs.Texto(escala);
        if (min != null) min.Text = "-" + NumeroJs.Texto(escala);
    }

    /// <summary>`currentScale()`: fija, o automática con piso 10 redondeada al
    /// múltiplo de 10 de arriba.</summary>
    private double EscalaActual()
    {
        if (!_autoScale) return _yScale;
        double m = _lienzo?.MaxAbsoluto(10) ?? 10;
        return Math.Ceiling(m / 10) * 10;
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
        var d = await _client.GetXteAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Aplicar(d));
    }

    private void Aplicar(XteGraphSampleDto? d)
    {
        if (d == null)
        {
            // Mismo detalle del JS: si el PRIMER pedido falla, la pill se queda
            // en "—" (lastOk todavía es false). Se replica tal cual.
            if (_lastOk) { _estado = TxtSinConexion; PintarEstado(); _lastOk = false; }
            return;
        }

        double he = NumeroJs.ANumero(d.HeadingErrorDeg);
        double xt = NumeroJs.ANumero(d.XteCm);

        // El motor devuelve el XTE crudo del guiado. Sin guía activa ese valor
        // no es un error de guiado sino una distancia enorme (centinela): se
        // acota para que no destruya la escala, igual que la ventana vieja.
        if (xt > XteMaxCm) xt = XteMaxCm;
        else if (xt < -XteMaxCm) xt = -XteMaxCm;

        _serHe?.Empujar(he);
        _serXte?.Empujar(xt);

        _ultHe = he;
        _ultXte = xt;
        PintarLecturas();

        if (!_lastOk) { _estado = TxtEnVivo; PintarEstado(); _lastOk = true; }
        Dibujar();
    }

    private void PintarLecturas()
    {
        var he  = this.FindControl<TextBlock>("ValHe");
        var xte = this.FindControl<TextBlock>("ValXte");
        if (he  != null) he.Text  = NumeroJs.Fijo(_ultHe, 1);
        // Math.round(xt) de JS, sin decimales.
        if (xte != null) xte.Text = NumeroJs.Texto(NumeroJs.Redondear(_ultXte));
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

    // ---------- controles de escala (solo cliente) ---------------------------

    private void OnEscalaMasClick(object? s, RoutedEventArgs e)
    {
        _autoScale = false;
        _yScale = Math.Min(5120, _yScale * 2);
        Dibujar();
    }

    private void OnEscalaMenosClick(object? s, RoutedEventArgs e)
    {
        _autoScale = false;
        _yScale = Math.Max(10, NumeroJs.Redondear(_yScale / 2));
        Dibujar();
    }

    private void OnAutoClick(object? s, RoutedEventArgs e)
    {
        // Igual que la página: el botón no cambia de aspecto al activarse.
        _autoScale = !_autoScale;
        Dibujar();
    }
}
