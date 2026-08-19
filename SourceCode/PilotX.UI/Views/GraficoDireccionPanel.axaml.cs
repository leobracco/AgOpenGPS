// GraficoDireccionPanel.axaml.cs
//
// Reemplazo nativo de pages/grafico-direccion.html (ex ventana WinForms
// FormGraphSteer). UI Avalonia + GraficosClient (HTTP a EmbedIO). Sin WebView,
// sin JS. El dibujo lo hace GraficoLineal, compartido con los otros tres
// gráficos.
//
// La lógica es la MISMA que js/grafico-direccion.js, paso por paso:
//   · GET /api/aog/graph-steer cada 200 ms (5 Hz)
//   · a = Number(actual_steer_deg) || 0 ; s = Number(set_steer_deg) || 0
//   · buffers rodantes de 120 muestras, eje simétrico ±escala
//   · lecturas con toFixed(1)
//   · escala fija 40° por defecto; "+ Escala" duplica hasta 180, "− Escala"
//     divide (Math.round) hasta un piso de 5, "Auto" alterna a escala
//     automática = Math.ceil(maxAbs/5)*5 con piso 5
//   · la pill dice "en vivo" al primer éxito y "sin conexión" al primer fallo
//     DESPUÉS de un éxito
//
// API: Attach(GraficosClient) arranca el muestreo; Detach() lo corta.
// MainWindow llama Attach() al abrir y Detach() al cerrar.

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

public partial class GraficoDireccionPanel : UserControl, IPanelEmbebible
{
    // POLL_MS del JS.
    private const int PollMs = 200;

    // Textos EXACTOS de la página (grafico-direccion.js). Son la CLAVE del
    // diccionario (castellano): lo que se pinta pasa siempre por T().
    private const string TxtSinDatos   = "—";
    private const string TxtEnVivo     = "en vivo";
    private const string TxtSinConexion = "sin conexión";

    private static string T(string t) => Traductor.T(t);

    private GraficosClient? _client;
    private CancellationTokenSource? _cts;

    private GraficoLineal? _lienzo;
    private SerieGrafico? _serActual;
    private SerieGrafico? _serSeteado;

    // Escala del eje Y. yMax = ±yScale (°); autoScale ajusta a datos.
    private double _yScale = 40;
    private bool _autoScale = false;

    // lastOk del JS: la pill solo cambia en los FLANCOS.
    private bool _lastOk = false;
    private bool _idiomaEnganchado = false;   // suscripción viva a Traductor.IdiomaCambio
    private string _estado = TxtSinDatos;

    // Últimas lecturas, para poder repintar el texto al cambiar de idioma sin
    // esperar al próximo tick (con el motor caído no habría próximo tick).
    private double _ultAct = 0, _ultSet = 0;

    /// <summary>Lo invoca el ✕ del header. El host (MainWindow) engancha acá su
    /// CloseGraficoDireccion — mismo contrato que EventosPanel.</summary>
    public Action? OnRequestCerrar { get; set; }

    public GraficoDireccionPanel()
    {
        InitializeComponent();
        // Sin esto la pantalla quedaría SIEMPRE en castellano aunque el equipo
        // esté en en/pt (la página HTML sí traducía). Una sola vez, en el ctor.
        Traductor.Aplicar(this);

        ArmarLienzo();
        Dibujar();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ---------- ciclo de vida ------------------------------------------------

    /// <summary>Inyecta el cliente y arranca el muestreo a 5 Hz (equivale a
    /// abrir la página: el JS hace poll() + setInterval()).</summary>
    public void Attach(GraficosClient client)
    {
        _client = client;
        // Los tokens del theme recién se resuelven con el control YA en el
        // árbol: en el ctor el lookup cae en los fallbacks (que son los mismos
        // hex, así que nunca se ve mal — pero si el theme cambia, esto manda).
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

    /// <summary>Corta el muestreo. Lo llama MainWindow al cerrar el panel:
    /// cerrado no se hace red (es el `visibilitychange` → stop() del JS). Los
    /// buffers NO se vacían, igual que en la página.</summary>
    public void Detach()
    {
        SoltarIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    /// <summary>Aplicar() restaura el texto CACHEADO del XAML: al cambiar de
    /// idioma la pill volvería a "—" y las lecturas a "0.0". Se repintan
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

    /// <summary>Adentro de la Configuración: sin marco de tarjeta y sin la
    /// cabecera propia (título grande + ✕).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        var raiz = this.FindControl<Grid>("ContenidoRaiz");
        if (raiz != null) raiz.Margin = new Thickness(0);
    }

    /// <summary>La pill de estado va a la barra de contexto del shell.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<Border>("HeaderPill"));

    private void OnCerrarClick(object? s, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    // ---------- lienzo -------------------------------------------------------

    private void ArmarLienzo()
    {
        _lienzo = this.FindControl<GraficoLineal>("Lienzo");
        if (_lienzo == null) return;

        // Mismo encuadre que el JS: centrado en cero, línea de cero + tercios.
        _lienzo.Modo = ModoEjeGrafico.CentradoEnCero;
        _lienzo.LineaCero = true;
        _lienzo.FraccionesGrilla = new[] { 0.25, 0.75 };
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));

        // Orden de alta = orden de dibujo, igual que en draw(): real y después
        // seteada. Colores idénticos a los de la página.
        _serActual  = _lienzo.NuevaSerie(RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E")));
        _serSeteado = _lienzo.NuevaSerie(Color.Parse("#3D87C6"));
    }

    /// <summary>Relee del theme lo que en el ctor cayó en fallback. El azul
    /// #3D87C6 NO tiene token en PilotXTheme: queda literal (anotado).</summary>
    private void ResolverColores()
    {
        if (_lienzo == null) return;
        _lienzo.ColorGrilla = RecursoColor.Buscar(this, "PilotXPanelBorderHighColor", Color.Parse("#C5CFC5"));
        if (_serActual != null)
            _serActual.Color = RecursoColor.Buscar(this, "PilotXPanelAccentColor", Color.Parse("#4ABA3E"));
        _lienzo.Redibujar();
    }

    /// <summary>El `draw()` del JS: recalcula la escala, repinta el lienzo y
    /// escribe las etiquetas del eje.</summary>
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

    /// <summary>`currentScale()`: fija, o automática con piso 5 redondeada al
    /// múltiplo de 5 de arriba.</summary>
    private double EscalaActual()
    {
        if (!_autoScale) return _yScale;
        double m = _lienzo?.MaxAbsoluto(5) ?? 5;
        return Math.Ceiling(m / 5) * 5;
    }

    // ---------- muestreo -----------------------------------------------------

    private async Task BucleAsync(CancellationToken ct)
    {
        // poll() inmediato y después cada 200 ms, igual que el JS.
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
        var d = await _client.GetSteerAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Aplicar(d));
    }

    private void Aplicar(SteerGraphSampleDto? d)
    {
        if (d == null)
        {
            // catch del fetch. OJO: si el PRIMER pedido falla, lastOk todavía
            // es false y la pill se queda en "—" para siempre. Es el
            // comportamiento del JS y se replica tal cual (no se "arregla").
            if (_lastOk) { _estado = TxtSinConexion; PintarEstado(); _lastOk = false; }
            return;
        }

        double a = NumeroJs.ANumero(d.ActualSteerDeg);
        double s = NumeroJs.ANumero(d.SetSteerDeg);

        _serActual?.Empujar(a);
        _serSeteado?.Empujar(s);

        _ultAct = a;
        _ultSet = s;
        PintarLecturas();

        if (!_lastOk) { _estado = TxtEnVivo; PintarEstado(); _lastOk = true; }
        Dibujar();
    }

    private void PintarLecturas()
    {
        var act = this.FindControl<TextBlock>("ValAct");
        var set = this.FindControl<TextBlock>("ValSet");
        if (act != null) act.Text = NumeroJs.Fijo(_ultAct, 1);
        if (set != null) set.Text = NumeroJs.Fijo(_ultSet, 1);
    }

    private void PintarEstado()
    {
        var st = this.FindControl<TextBlock>("StatusText");
        if (st != null) st.Text = T(_estado);
    }

    /// <summary>Cambio de idioma: Traductor.Aplicar ya devolvió los textos
    /// cacheados del XAML, así que hay que reescribir todo lo dinámico.</summary>
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
        _yScale = Math.Min(180, _yScale * 2);
        Dibujar();
    }

    private void OnEscalaMenosClick(object? s, RoutedEventArgs e)
    {
        _autoScale = false;
        // Math.round de JS = medio hacia arriba, NO el "al par" de .NET.
        _yScale = Math.Max(5, NumeroJs.Redondear(_yScale / 2));
        Dibujar();
    }

    private void OnAutoClick(object? s, RoutedEventArgs e)
    {
        // Igual que la página: el botón no cambia de aspecto al activarse
        // (btnGainAuto no tenía estado visual). Se replica tal cual.
        _autoScale = !_autoScale;
        Dibujar();
    }
}
