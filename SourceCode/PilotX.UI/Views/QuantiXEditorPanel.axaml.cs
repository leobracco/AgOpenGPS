// ============================================================================
// QuantiXEditorPanel.axaml.cs — shell del editor nativo de QuantiX.
//
// QUÉ QUEDÓ NATIVO (esto cierra el hueco): las 6 tabs que antes vivían en
// pages/quantix.html y se abrían con "Configurar" — Siembra, Motores, Shape,
// PID live, Calibración y Prueba. Con esto el panel de QuantiX ya no despierta
// Chromium para nada.
// QUÉ SIGUE EN HTML: la página quantix.html y sus JS, intactos, porque los usa
// la PWA del celular (regla del repo: las páginas HTML no se borran).
//
// Transporte: polling (500 ms en las tabs vivas, 2000 ms en las de config)
// a /api/quantix/live + /api/aog/state. El WS /ws/quantix NO se porta a
// propósito — ver el comentario de QuantiXEditorClient.
//
// Regla del árbol bajo el dedo: el tick actualiza SOLO labels live
// (tab.Live()); el rebuild completo pasa por acción del operario o por cambio
// de modo Configurar↔En marcha. Reconstruir en el tick tira el foco del
// TextBox y cierra el teclado nativo.
//
// Seguridad: Detach() y el cambio de tab llaman AlSalirAsync() de la tab
// activa, que manda STOP a cualquier motor girando (rampa, Max Hz, corrida de
// calibración). verb=test NO tiene meta: si nadie manda el stop, el motor
// queda girando con el panel cerrado.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.QuantiXEditor;

namespace PilotX.Desktop.Views;

public partial class QuantiXEditorPanel : UserControl
{
    private readonly QxEditorCtx _ctx = new();
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, QxTab> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _tabBtns = new(StringComparer.Ordinal);
    private string _tabActiva = "siembra";
    private bool _enMarchaPrev;

    private TaskCompletionSource<bool>? _confirmTcs;

    /// <summary>El operario cerró el editor.</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Aviso corto → toast del host. Nunca modal.</summary>
    public event Action<string>? Aviso;

    private static readonly (string Clave, string Titulo)[] TABS =
    {
        ("siembra",  "Siembra"),
        ("motores",  "Motores"),
        ("shape",    "Shape"),
        ("pid",      "PID live"),
        ("calibrar", "Calibración"),
        ("prueba",   "Prueba"),
    };

    public QuantiXEditorPanel()
    {
        InitializeComponent();
        _ctx.Aviso = m => Aviso?.Invoke(m);
        _ctx.Confirmar = ConfirmarAsync;
        _ctx.IrATab = clave => _ = MostrarTabAsync(clave);
        ArmarTabStrip();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // =======================================================================
    //  Ciclo de vida
    // =======================================================================

    /// <summary>Arranca el editor. `tab` cubre los deep-links que en HTML eran
    /// quantix.html?tab=shape (accesos "Prescripciones").</summary>
    public void Attach(QuantiXEditorClient client, string? tab = null)
    {
        _ctx.Client = client;
        if (!string.IsNullOrEmpty(tab))
        {
            foreach (var t in TABS) if (t.Clave == tab) { _tabActiva = tab; break; }
        }
        if (_cts != null) { PintarTabStrip(); return; }   // guard anti doble-Attach
        _cts = new CancellationTokenSource();
        _ = ArrancarAsync(_cts.Token);
    }

    public void Detach()
    {
        // Cualquier motor girando tiene que parar ANTES de soltar el token:
        // el flujo de medición usa el CTS del panel para su propio HTTP.
        try
        {
            if (_tabs.TryGetValue(_tabActiva, out var t)) _ = t.AlSalirAsync();
        }
        catch { }
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _ = _ctx.Client?.TecladoAsync(false);
    }

    private async Task ArrancarAsync(CancellationToken ct)
    {
        // Carga inicial: config de motores + implemento + columnas del shape.
        // El planter no se puede dibujar sin esto.
        var cfg = await _ctx.Client.GetMotoresAsync(ct).ConfigureAwait(false);
        if (cfg != null) _ctx.Cfg = cfg;
        _ctx.Cfg.Nodos ??= new List<QxNodoConfig>();

        var (impl, ancho) = await _ctx.Client.GetImplementoAsync(ct).ConfigureAwait(false);
        _ctx.Impl = impl;
        _ctx.AnchoPilotX = ancho;

        var campos = await _ctx.Client.GetShapeFieldsAsync(ct).ConfigureAwait(false);
        _ctx.ShapeSource = campos.SourceToken;
        _ctx.ShapeFields = campos.Fields;

        await Dispatcher.UIThread.InvokeAsync(() => _ = MostrarTabAsync(_tabActiva));
        await RunLoopAsync(ct).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            int periodo = EsTabViva(_tabActiva) ? 500 : 2000;
            try { await Task.Delay(TimeSpan.FromMilliseconds(periodo), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool EsTabViva(string tab)
        => tab == "siembra" || tab == "pid" || tab == "calibrar" || tab == "prueba";

    private async Task TickAsync(CancellationToken ct)
    {
        if (_ctx.Client == null) return;

        var live = await _ctx.Client.GetLiveAsync(ct).ConfigureAwait(false);
        // La velocidad se refresca SIEMPRE, no solo en Siembra: sem/m, sem/ha y
        // kg/ha se calculan dividiendo por la velocidad, así que una velocidad
        // congelada da dosis inventadas en PID live, Calibración y Prueba.
        var estado = await _ctx.Client.GetAogStateAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => AplicarTick(live, estado));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void AplicarTick(QxLiveSnapshot? live, QxAogState? estado)
    {
        // --- live ---
        if (live == null)
        {
            _ctx.LiveOk = false;
            PintarPill(QxUi.Err, "Sin conexión");
        }
        else
        {
            _ctx.LiveOk = true;
            var nodos = live.Nodos ?? new List<QxNodoLive>();
            _ctx.LiveNodos = nodos.Count;
            foreach (var n in nodos)
                if (!string.IsNullOrEmpty(n.Uid)) _ctx.LiveByUid[n.Uid!] = n;
            PintarPill(nodos.Count > 0 ? QxUi.Ok : QxUi.Warn,
                       nodos.Count.ToString(CultureInfo.InvariantCulture)
                       + (nodos.Count == 1 ? " nodo QuantiX" : " nodos QuantiX"));
        }

        // --- estado de PilotX ---
        if (estado != null)
        {
            _ctx.AogJobStarted = estado.IsJobStarted;
            _ctx.SectionOn = estado.SectionOnRequest?.ToArray();
            if (estado.NumSections > 0) _ctx.AogNumSections = estado.NumSections;
            _ctx.AogSpeed = estado.AvgSpeed;
            _ctx.AogAreaHa = estado.AreaM2 * 0.0001;
        }

        _ctx.ComputeEnMarcha();

        if (!_tabs.TryGetValue(_tabActiva, out var tab)) return;

        // En Siembra el cambio de modo Configurar↔En marcha rearma la tab UNA
        // vez; en modo config NO se reconstruye con cada push (destruía el
        // árbol abajo del dedo, reporte 2026-08-10 sobre el HTML).
        if (_tabActiva == "siembra" && _enMarchaPrev != _ctx.SiembraEnMarcha)
        {
            _enMarchaPrev = _ctx.SiembraEnMarcha;
            tab.Rebuild();
            PilotX.Cockpit.Bars.Traductor.Aplicar(this);
            return;
        }
        tab.Live();
    }

    private void PintarPill(IBrush color, string texto)
    {
        var dot = this.FindControl<Ellipse>("EstadoDot");
        var lbl = this.FindControl<TextBlock>("EstadoText");
        if (dot != null) dot.Fill = color;
        if (lbl != null) lbl.Text = PilotX.Cockpit.Bars.Traductor.T(texto);
    }

    // =======================================================================
    //  Tabs
    // =======================================================================

    private void ArmarTabStrip()
    {
        var strip = this.FindControl<StackPanel>("TabStrip");
        if (strip == null) return;
        strip.Children.Clear();
        _tabBtns.Clear();
        foreach (var (clave, titulo) in TABS)
        {
            var b = new Button
            {
                Content = PilotX.Cockpit.Bars.Traductor.T(titulo),
                MinHeight = 42,
                Padding = new Thickness(16, 8, 16, 8),
                CornerRadius = new CornerRadius(8),
                FontSize = 13,
                BorderThickness = new Thickness(0, 0, 0, 3),
            };
            string c = clave;
            b.Click += (_, __) => _ = MostrarTabAsync(c);
            _tabBtns[clave] = b;
            strip.Children.Add(b);
        }
        PintarTabStrip();
    }

    private void PintarTabStrip()
    {
        foreach (var kv in _tabBtns)
        {
            bool activa = kv.Key == _tabActiva;
            kv.Value.Background = activa ? QxUi.BgFila : Brushes.Transparent;
            kv.Value.Foreground = activa ? QxUi.Texto : QxUi.TextoMuted;
            kv.Value.BorderBrush = activa ? QxUi.Verde : Brushes.Transparent;
            kv.Value.FontWeight = activa ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private async Task MostrarTabAsync(string clave)
    {
        // La tab que se deja tiene que parar lo que haya puesto a girar.
        if (_tabs.TryGetValue(_tabActiva, out var vieja) && _tabActiva != clave)
        {
            try { await vieja.AlSalirAsync().ConfigureAwait(true); } catch { }
        }
        _ = _ctx.Client?.TecladoAsync(false);

        _tabActiva = clave;
        PintarTabStrip();

        if (!_tabs.TryGetValue(clave, out var tab))
        {
            tab = CrearTab(clave);
            _tabs[clave] = tab;
        }

        var host = this.FindControl<StackPanel>("TabHost");
        if (host != null)
        {
            host.Children.Clear();
            host.Children.Add(tab);
        }

        _ctx.ComputeEnMarcha();
        _enMarchaPrev = _ctx.SiembraEnMarcha;
        tab.Rebuild();
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
        try { await tab.AlEntrarAsync().ConfigureAwait(true); } catch { }
    }

    private QxTab CrearTab(string clave) => clave switch
    {
        "motores"  => new MotoresTab(_ctx),
        "shape"    => new ShapeTab(_ctx),
        "pid"      => new PidTab(_ctx),
        "calibrar" => new CalibracionTab(_ctx),
        "prueba"   => new PruebaTab(_ctx),
        _          => new SiembraTab(_ctx),
    };

    // =======================================================================
    //  Confirmación (overlay interno — jamás ShowDialog ni Flyout)
    // =======================================================================

    private Task<bool> ConfirmarAsync(string titulo, string mensaje)
    {
        var t = this.FindControl<TextBlock>("ConfirmTitulo");
        var m = this.FindControl<TextBlock>("ConfirmMensaje");
        var ov = this.FindControl<Border>("ConfirmOverlay");
        if (t != null) t.Text = PilotX.Cockpit.Bars.Traductor.T(titulo);
        if (m != null) m.Text = PilotX.Cockpit.Bars.Traductor.T(mensaje);
        if (ov != null) ov.IsVisible = true;
        _confirmTcs?.TrySetResult(false);
        _confirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _confirmTcs.Task;
    }

    private void CerrarConfirm(bool r)
    {
        var ov = this.FindControl<Border>("ConfirmOverlay");
        if (ov != null) ov.IsVisible = false;
        var tcs = _confirmTcs;
        _confirmTcs = null;
        tcs?.TrySetResult(r);
    }

    private void OnConfirmSiClick(object? s, RoutedEventArgs e) => CerrarConfirm(true);
    private void OnConfirmNoClick(object? s, RoutedEventArgs e) => CerrarConfirm(false);

    private void OnCerrarClick(object? s, RoutedEventArgs e) => OnRequestCerrar?.Invoke();
}
