// ============================================================================
// FlowXEditorPanel.axaml.cs — shell del editor nativo de FlowX.
//
// QUE QUEDO NATIVO (esto cierra el hueco): las pantallas que antes vivian en
// pages/flowx.html y se abrian con "Configurar" — Nodo activo (datos, ancho,
// PID y actuador, calibrar / auto-tune / barrido / PWM manual), Reguladoras,
// Cortes y secciones, Electrovalvulas + sincronizacion al firmware, Firmware
// (OTA) y Nodos en red. Con esto el panel de FlowX ya no despierta Chromium.
// QUE SIGUE EN HTML: la pagina flowx.html y js/flowx.js, intactos, porque los
// usa la PWA del celular (regla del repo: las paginas HTML no se borran).
// QUE NO SE PORTA A PROPOSITO: la pestana "Próximamente" (lista de roadmap sin
// funcion) y la pill decorativa "Preview" del encabezado.
//
// Transporte: el MISMO FlowXClient del monitor. Tick de 1 s para el live
// (pill + telemetria) y de 3 s para los nodos vistos por MQTT; la config se
// pide al entrar y con "Recargar", NUNCA en el tick — el DTO en memoria es lo
// que el operario esta editando y un refresco lo pisaria abajo del dedo.
//
// Modelo de guardado: cada control escribe en el DTO al cambiar y "Guardar"
// hace un POST del objeto ENTERO (el endpoint reemplaza el archivo). Por eso
// el DTO es de fidelidad completa: lo que el editor no conozca se perderia,
// incluida la lista "ignorados".
//
// Seguridad: Detach() cierra el buscador de PWM manual mandando manual_stop.
// Si no, cerrar el panel con la valvula abierta la dejaba girando hasta que el
// failsafe del firmware la corta a los 4 s.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.Controls;
using PilotX.Desktop.Views.FlowXEditor;

namespace PilotX.Desktop.Views;

public partial class FlowXEditorPanel : UserControl
{
    private readonly FxCtx _ctx = new();
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, FxTab> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _tabBtns = new(StringComparer.Ordinal);
    private string _tabActiva = "nodo";

    private bool _armando;                 // repoblando combos: ignorar handlers
    private List<string> _uidsCfg = new();
    private List<string> _uidsLan = new();
    private DispatcherTimer? _estadoTimer;

    // ---- PWM manual --------------------------------------------------------
    private string _pwmUid = "";
    private int    _pwmProdId;
    private int    _pwmProdIdx;
    private bool   _pwmNeg;                // false = abrir (+), true = cerrar (−)
    private bool   _pwmAplicado;
    private AgpStepper? _pwmStepper;
    private DispatcherTimer? _pwmHb;       // heartbeat 1,5 s
    private DispatcherTimer? _pwmLiveTimer;// telemetria propia 700 ms

    private TaskCompletionSource<string?>? _dlgTcs;

    /// <summary>El operario cerró el editor.</summary>
    public Action? OnRequestCerrar { get; set; }
    /// <summary>Volver al monitor live de FlowX.</summary>
    public Action? OnRequestMonitor { get; set; }

    /// <summary>Aviso corto → toast del host. Nunca modal.</summary>
    public event Action<string>? Aviso;

    private static readonly (string Clave, string Titulo)[] TABS =
    {
        ("nodo",        "Nodo activo"),
        ("reguladoras", "Reguladoras"),
        ("cortes",      "Cortes"),
        ("valvulas",    "Electroválvulas"),
        ("firmware",    "Firmware"),
        ("red",         "Nodos en red"),
    };

    public FlowXEditorPanel()
    {
        InitializeComponent();

        _ctx.Aviso          = m => Aviso?.Invoke(m);
        _ctx.Estado         = SetEstado;
        _ctx.Confirmar      = ConfirmarAsync;
        _ctx.Alertar        = AlertarAsync;
        _ctx.Pedir          = PedirAsync;
        _ctx.GuardarAsync   = GuardarConfigAsync;
        _ctx.RefrescarSelector = () => { ArmarSelectorNodos(); RebuildTabActiva(); };
        _ctx.RebuildTab     = RebuildTabActiva;
        _ctx.IrANodoActivo  = () => MostrarTab("nodo");
        _ctx.AbrirPwmManual = AbrirPwmManual;

        ArmarTabStrip();

        var sel = this.FindControl<ComboBox>("NodoSelect");
        if (sel != null) sel.SelectionChanged += OnNodoSelectChanged;

        var chk = this.FindControl<CheckBox>("ChkEnabled");
        if (chk != null)
            chk.IsCheckedChanged += (_, __) =>
            {
                if (_armando || _ctx.Cfg == null) return;
                _ctx.Cfg.Enabled = chk.IsChecked == true;
            };

        // Teclado nativo del campo del diálogo (volumen de calibración).
        var dlgTxt = this.FindControl<TextBox>("DlgEntrada");
        if (dlgTxt != null)
        {
            dlgTxt.GotFocus  += (_, __) => { if (_ctx.Client != null) _ = _ctx.Client.TecladoAsync(true, true, "Valor"); };
            dlgTxt.LostFocus += (_, __) => { if (_ctx.Client != null) _ = _ctx.Client.TecladoAsync(false); };
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // =======================================================================
    //  Ciclo de vida
    // =======================================================================

    public void Attach(FlowXClient client)
    {
        _ctx.Client = client;
        if (_cts != null) { PintarTabStrip(); return; }   // guard anti doble-Attach
        _cts = new CancellationTokenSource();
        _ctx.Ct = _cts.Token;
        _ = ArrancarAsync(_cts.Token);
    }

    public void Detach()
    {
        // La válvula girando tiene que parar ANTES de soltar el token: el
        // heartbeat usa el CTS del panel para su propio HTTP.
        CerrarPwm(pararMotor: true);

        try { _estadoTimer?.Stop(); } catch { }
        _estadoTimer = null;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _ = _ctx.Client?.TecladoAsync(false);

        // Un diálogo abierto no puede quedar esperando para siempre.
        _dlgTcs?.TrySetResult(null);
        _dlgTcs = null;
        var dlg = this.FindControl<Border>("DialogoOverlay");
        if (dlg != null) dlg.IsVisible = false;

        // Lo leído del backend NO sobrevive al cierre: entre medio se pudo
        // guardar desde el celular con la página, y "Guardar" pisaría con lo
        // viejo.
        _ctx.LimpiarCache();
        _tabs.Clear();
    }

    private async Task ArrancarAsync(CancellationToken ct)
    {
        await RecargarConfigAsync(ct).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ArmarSelectorNodos();
            MostrarTab(_tabActiva);
        });
        await RunLoopAsync(ct).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        int n = 0;
        await TickAsync(ct, nodos: true).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            n++;
            bool nodos = n % 3 == 0;   // los nodos LAN, cada 3 s
            await TickAsync(ct, nodos).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct, bool nodos)
    {
        if (_ctx.Client == null) return;
        var live = await _ctx.Client.GetLiveAsync(ct).ConfigureAwait(false);
        var aog  = await _ctx.Client.GetAogStateAsync(ct).ConfigureAwait(false);
        List<FlowXNodoLan>? lan = nodos
            ? await _ctx.Client.GetNodosAsync(ct).ConfigureAwait(false)
            : null;
        if (ct.IsCancellationRequested) return;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => AplicarTick(live, aog, lan));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void AplicarTick(FlowXLiveSnapshot? live, FlowXAogState? aog, List<FlowXNodoLan>? lan)
    {
        _ctx.Live = live;
        if (aog != null) _ctx.Aog = aog;
        if (lan != null)
        {
            _ctx.Lan = lan;
            ArmarSelectorLan();
        }
        PintarPill();
        if (_tabs.TryGetValue(_tabActiva, out var tab)) tab.Live();
    }

    private void PintarPill()
    {
        var dot = this.FindControl<Ellipse>("EstadoDot");
        var lbl = this.FindControl<TextBlock>("EstadoPillText");
        if (dot == null || lbl == null) return;

        var n = _ctx.NodoActual();
        IBrush color; string texto;
        if (n == null)
        {
            color = FxUi.Dim;
            texto = _ctx.Nodos().Count == 0 ? "Sin nodos en la configuración" : "Sin nodo elegido";
        }
        else
        {
            var reg = _ctx.LanDe(n.Uid);
            bool online = reg != null && reg.Online;
            color = online ? FxUi.Ok : FxUi.Err;
            texto = online ? "Nodo en línea" : "Nodo fuera de línea";
        }
        dot.Fill = color;
        lbl.Text = PilotX.Cockpit.Bars.Traductor.T(texto);
        lbl.Foreground = color;
    }

    // =======================================================================
    //  Config: cargar / guardar
    // =======================================================================

    private async Task RecargarConfigAsync(CancellationToken ct)
    {
        if (_ctx.Client == null) return;
        var cfg = await _ctx.Client.GetConfigAsync(ct).ConfigureAwait(false);
        var aog = await _ctx.Client.GetAogStateAsync(ct).ConfigureAwait(false);
        var lan = await _ctx.Client.GetNodosAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _ctx.Cfg = cfg ?? new FlowXConfig { Enabled = true, Nodos = new List<FlowXNodoConfig>() };
            _ctx.Cfg.Nodos ??= new List<FlowXNodoConfig>();
            _ctx.Aog = aog;
            _ctx.Lan = lan;
            _ctx.SelectedProdIdx = 0;
            var nodos = _ctx.Nodos();
            _ctx.CurrentUid = nodos.Count > 0 ? nodos[0].Uid : null;
            if (cfg == null)
                SetEstado(PilotX.Cockpit.Bars.Traductor.T("La pantalla no responde — no se pudo leer la configuración."), "err");
        });
    }

    private async Task<bool> GuardarConfigAsync()
    {
        if (_ctx.Client == null || _ctx.Cfg == null) return false;
        SetEstado("Guardando…", "");
        var r = await _ctx.Client.SaveConfigAsync(_ctx.Cfg, _ctx.Ct).ConfigureAwait(true);
        if (r.Ok)
        {
            SetEstado("Guardado ✓", "ok", limpiarEnMs: 2500);
            return true;
        }
        if (string.Equals(r.Error, "AGP-CFG-001", StringComparison.Ordinal))
        {
            // Código + friendly en el pie, el detalle técnico en el diálogo:
            // tragárselo dejaba al operario guardando en vacío.
            SetEstado(r.Error + " · " + (r.Mensaje ?? ""), "err");
            await AlertarAsync(r.Error + " · " + (r.Mensaje ?? ""), r.Detalle ?? "").ConfigureAwait(true);
            return false;
        }
        SetEstado(PilotX.Cockpit.Bars.Traductor.T("Error") + ": "
                  + (string.IsNullOrEmpty(r.Error) ? r.Texto() : r.Error!), "err");
        return false;
    }

    private void SetEstado(string texto, string clase) => SetEstado(texto, clase, 0);

    private void SetEstado(string texto, string clase, int limpiarEnMs)
    {
        var t = this.FindControl<TextBlock>("EstadoText");
        if (t == null) return;
        t.Text = PilotX.Cockpit.Bars.Traductor.T(texto);
        t.Foreground = clase == "ok" ? FxUi.Ok : clase == "err" ? FxUi.Err : FxUi.TextoMuted;

        try { _estadoTimer?.Stop(); } catch { }
        _estadoTimer = null;
        if (limpiarEnMs <= 0) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(limpiarEnMs) };
        timer.Tick += (_, __) =>
        {
            try { timer.Stop(); } catch { }
            var lbl = this.FindControl<TextBlock>("EstadoText");
            if (lbl != null) { lbl.Text = ""; lbl.Foreground = FxUi.TextoMuted; }
        };
        _estadoTimer = timer;
        timer.Start();
    }

    // =======================================================================
    //  Selector de nodos
    // =======================================================================

    private void ArmarSelectorNodos()
    {
        var sel = this.FindControl<ComboBox>("NodoSelect");
        var chk = this.FindControl<CheckBox>("ChkEnabled");
        if (sel == null) return;

        _armando = true;
        try
        {
            var nodos = _ctx.Nodos();
            var etiquetas = new List<string>();
            _uidsCfg = new List<string>();
            foreach (var n in nodos)
            {
                string nom = string.IsNullOrEmpty(n.Nombre) ? (n.Uid ?? "?") : n.Nombre!;
                etiquetas.Add(nom + " — " + (n.Uid ?? "?"));
                _uidsCfg.Add(n.Uid ?? "");
            }
            if (etiquetas.Count == 0)
                etiquetas.Add(PilotX.Cockpit.Bars.Traductor.T("(sin nodos en la configuración)"));

            sel.ItemsSource = etiquetas;
            int idx = _ctx.CurrentUid != null ? _uidsCfg.IndexOf(_ctx.CurrentUid) : -1;
            if (idx < 0 && _uidsCfg.Count > 0) { idx = 0; _ctx.CurrentUid = _uidsCfg[0]; }
            sel.SelectedIndex = idx >= 0 ? idx : 0;
            sel.IsEnabled = _uidsCfg.Count > 0;

            if (chk != null) chk.IsChecked = _ctx.Cfg?.Enabled ?? false;
        }
        finally { _armando = false; }

        ArmarSelectorLan();
        PintarPill();
    }

    /// <summary>Nodos vistos en la red que todavía NO están en la config. Solo
    /// se rearma cuando cambia el conjunto: si no, el desplegable se cierra
    /// solo cada 3 s abajo del dedo.</summary>
    private void ArmarSelectorLan()
    {
        var sel = this.FindControl<ComboBox>("LanSelect");
        var btn = this.FindControl<Button>("BtnImportar");
        if (sel == null) return;

        var enCfg = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in _ctx.Nodos()) if (!string.IsNullOrEmpty(n.Uid)) enCfg.Add(n.Uid!);

        var uids = new List<string>();
        var etiquetas = new List<string>();
        foreach (var l in _ctx.Lan)
        {
            if (string.IsNullOrEmpty(l.Uid) || enCfg.Contains(l.Uid!)) continue;
            uids.Add(l.Uid!);
            etiquetas.Add(l.Uid + " · " + (string.IsNullOrEmpty(l.Ip) ? "?" : l.Ip));
        }

        bool igual = uids.Count == _uidsLan.Count;
        if (igual) for (int i = 0; i < uids.Count; i++) if (uids[i] != _uidsLan[i]) { igual = false; break; }
        if (igual && sel.ItemsSource != null) return;

        _armando = true;
        try
        {
            _uidsLan = uids;
            if (etiquetas.Count == 0)
                etiquetas.Add(PilotX.Cockpit.Bars.Traductor.T("(no hay nodos nuevos)"));
            sel.ItemsSource = etiquetas;
            sel.SelectedIndex = 0;
            sel.IsEnabled = _uidsLan.Count > 0;
            if (btn != null) btn.IsEnabled = _uidsLan.Count > 0;
        }
        finally { _armando = false; }
    }

    private void OnNodoSelectChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_armando) return;
        var sel = this.FindControl<ComboBox>("NodoSelect");
        if (sel == null) return;
        int i = sel.SelectedIndex;
        if (i < 0 || i >= _uidsCfg.Count) return;
        if (string.Equals(_ctx.CurrentUid, _uidsCfg[i], StringComparison.Ordinal)) return;
        _ctx.CurrentUid = _uidsCfg[i];
        _ctx.SelectedProdIdx = 0;
        PintarPill();
        RebuildTabActiva();
    }

    /// <summary>Alta de nodo SOLO desde lo descubierto por MQTT: nunca tipeando
    /// un UID (regla del repo — evita typos que dejan el nodo mudo).</summary>
    private void OnImportarClick(object? s, RoutedEventArgs e)
    {
        var sel = this.FindControl<ComboBox>("LanSelect");
        if (sel == null || _ctx.Cfg == null) return;
        int i = sel.SelectedIndex;
        if (i < 0 || i >= _uidsLan.Count) return;
        string uid = _uidsLan[i];
        if (string.IsNullOrEmpty(uid)) return;
        foreach (var n in _ctx.Nodos())
            if (string.Equals(n.Uid, uid, StringComparison.Ordinal)) return;

        var lan = _ctx.LanDe(uid);
        _ctx.Nodos().Add(_ctx.NodoNuevo(uid, lan?.Nombre));
        _ctx.CurrentUid = uid;
        _ctx.SelectedProdIdx = 0;
        ArmarSelectorNodos();
        RebuildTabActiva();
        SetEstado(PilotX.Cockpit.Bars.Traductor.T(
            "Nodo agregado. Revisá el ancho de barra y tocá Guardar."), "");
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
            b.Click += (_, __) => MostrarTab(c);
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
            kv.Value.Background  = activa ? FxUi.BgFila : Brushes.Transparent;
            kv.Value.Foreground  = activa ? FxUi.Texto : FxUi.TextoMuted;
            kv.Value.BorderBrush = activa ? FxUi.Verde : Brushes.Transparent;
            kv.Value.FontWeight  = activa ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private void MostrarTab(string clave)
    {
        if (_tabs.TryGetValue(_tabActiva, out var vieja) && _tabActiva != clave)
        {
            try { _ = vieja.AlSalirAsync(); } catch { }
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
        tab.Rebuild();
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private void RebuildTabActiva()
    {
        if (!_tabs.TryGetValue(_tabActiva, out var tab)) return;
        tab.Rebuild();
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private FxTab CrearTab(string clave) => clave switch
    {
        "reguladoras" => new FxReguladorasTab(_ctx),
        "cortes"      => new FxCortesTab(_ctx),
        "valvulas"    => new FxOverrideTab(_ctx),
        "firmware"    => new FxOtaTab(_ctx),
        "red"         => new FxNodosTab(_ctx),
        _             => new FxNodoTab(_ctx),
    };

    // =======================================================================
    //  Diálogos internos (jamás ShowDialog ni Flyout)
    // =======================================================================

    private async Task<bool> ConfirmarAsync(string titulo, string mensaje)
    {
        var r = await AbrirDialogo(titulo, mensaje, conEntrada: false, conCancelar: true,
                                   valorInicial: "").ConfigureAwait(true);
        return r != null;
    }

    private Task AlertarAsync(string titulo, string mensaje)
        => AbrirDialogo(titulo, mensaje, conEntrada: false, conCancelar: false, valorInicial: "");

    private Task<string?> PedirAsync(string titulo, string mensaje, string valorInicial)
        => AbrirDialogo(titulo, mensaje, conEntrada: true, conCancelar: true, valorInicial);

    private Task<string?> AbrirDialogo(string titulo, string mensaje, bool conEntrada,
                                       bool conCancelar, string valorInicial)
    {
        var t = this.FindControl<TextBlock>("DlgTitulo");
        var m = this.FindControl<TextBlock>("DlgMensaje");
        var e = this.FindControl<TextBox>("DlgEntrada");
        var c = this.FindControl<Button>("DlgCancelar");
        var ov = this.FindControl<Border>("DialogoOverlay");
        if (t != null) t.Text = PilotX.Cockpit.Bars.Traductor.T(titulo);
        if (m != null) m.Text = PilotX.Cockpit.Bars.Traductor.T(mensaje);
        if (e != null) { e.IsVisible = conEntrada; e.Text = valorInicial; }
        if (c != null) c.IsVisible = conCancelar;
        if (ov != null) ov.IsVisible = true;

        _dlgTcs?.TrySetResult(null);
        _dlgTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _dlgTcs.Task;
    }

    private void CerrarDialogo(string? resultado)
    {
        var ov = this.FindControl<Border>("DialogoOverlay");
        if (ov != null) ov.IsVisible = false;
        _ = _ctx.Client?.TecladoAsync(false);
        var tcs = _dlgTcs;
        _dlgTcs = null;
        tcs?.TrySetResult(resultado);
    }

    private void OnDlgAceptarClick(object? s, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("DlgEntrada");
        CerrarDialogo(txt != null && txt.IsVisible ? (txt.Text ?? "") : "");
    }

    private void OnDlgCancelarClick(object? s, RoutedEventArgs e) => CerrarDialogo(null);

    /// <summary>El fondo absorbe el toque pero NO cierra: atrás puede haber una
    /// calibración corriendo con la bomba andando.</summary>
    private void OnBackdropPressed(object? s, PointerPressedEventArgs e) => e.Handled = true;

    // =======================================================================
    //  Buscador de PWM mínimo (overlay con el motor girando de verdad)
    // =======================================================================

    private void AbrirPwmManual(FlowXNodoConfig n, int prodIdx)
    {
        var ps = FxCtx.Productos(n);
        if (prodIdx < 0 || prodIdx >= ps.Count) return;
        var p = ps[prodIdx];

        _pwmUid = n.Uid ?? "";
        if (string.IsNullOrEmpty(_pwmUid)) return;
        _pwmProdId = p.Id;
        _pwmProdIdx = prodIdx;
        _pwmNeg = false;
        _pwmAplicado = false;

        var titulo = this.FindControl<TextBlock>("PwmTitulo");
        if (titulo != null)
            titulo.Text = PilotX.Cockpit.Bars.Traductor.T("PWM mínimo a mano") + " — "
                        + (string.IsNullOrEmpty(p.Nombre) ? "reg. " + (prodIdx + 1) : p.Nombre!);

        var host = this.FindControl<StackPanel>("PwmStepperHost");
        if (host != null)
        {
            host.Children.Clear();
            _pwmStepper = new AgpStepper(p.PwmMin > 0 ? p.PwmMin : 200, AgpStepperModo.Int, 5, 0, 4095);
            // Con un PWM ya aplicado, cada toque de −/+ se manda al instante.
            _pwmStepper.ValorCambiado += v => { if (_pwmAplicado) _ = AplicarPwmAsync(); };
            host.Children.Add(_pwmStepper);
        }

        PintarDireccion();
        PintarPwmLive(null);
        var ov = this.FindControl<Border>("PwmOverlay");
        if (ov != null) ov.IsVisible = true;

        // Heartbeat: el firmware corta el motor a los 4 s sin comando.
        _pwmHb = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pwmHb.Tick += (_, __) => { if (_pwmAplicado) _ = AplicarPwmAsync(); };
        _pwmHb.Start();

        _pwmLiveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _pwmLiveTimer.Tick += (_, __) => _ = RefrescarPwmLiveAsync();
        _pwmLiveTimer.Start();
        _ = RefrescarPwmLiveAsync();
    }

    private void PintarDireccion()
    {
        var pos = this.FindControl<Button>("PwmDirPos");
        var neg = this.FindControl<Button>("PwmDirNeg");
        if (pos != null)
        {
            pos.Background = !_pwmNeg ? FxUi.Verde : FxUi.BgFila;
            pos.Foreground = !_pwmNeg ? Brushes.White : FxUi.Texto;
            pos.BorderBrush = !_pwmNeg ? FxUi.Verde : FxUi.Borde;
        }
        if (neg != null)
        {
            neg.Background = _pwmNeg ? FxUi.Verde : FxUi.BgFila;
            neg.Foreground = _pwmNeg ? Brushes.White : FxUi.Texto;
            neg.BorderBrush = _pwmNeg ? FxUi.Verde : FxUi.Borde;
        }
    }

    private int PwmMagnitud() => _pwmStepper?.ValorInt ?? 0;

    private async Task AplicarPwmAsync()
    {
        if (_ctx.Client == null || string.IsNullOrEmpty(_pwmUid)) return;
        _pwmAplicado = true;
        int mag = PwmMagnitud();
        await _ctx.Client.SendCmdAsync(_pwmUid, "manual_pwm",
            new { producto_id = _pwmProdId, value = _pwmNeg ? -mag : mag }, _ctx.Ct)
            .ConfigureAwait(true);
    }

    private async Task RefrescarPwmLiveAsync()
    {
        if (_ctx.Client == null) return;
        var live = await _ctx.Client.GetLiveAsync(_ctx.Ct).ConfigureAwait(true);
        _ctx.Live = live;
        FlowXNodoLive? ln = null;
        if (live?.Nodos != null)
            foreach (var l in live.Nodos)
                if (string.Equals(l.Uid, _pwmUid, StringComparison.OrdinalIgnoreCase)) { ln = l; break; }
        PintarPwmLive(ln);
    }

    private void PintarPwmLive(FlowXNodoLive? ln)
    {
        var flow = this.FindControl<TextBlock>("PwmFlow");
        var pwm  = this.FindControl<TextBlock>("PwmAplicado");
        var pul  = this.FindControl<TextBlock>("PwmPulsos");
        var hint = this.FindControl<TextBlock>("PwmHint");

        if (ln == null || !ln.Online)
        {
            if (flow != null) flow.Text = "—";
            if (pwm  != null) pwm.Text  = "—";
            if (pul  != null) pul.Text  = "—";
            if (hint != null)
            {
                hint.Text = PilotX.Cockpit.Bars.Traductor.T("Esperando telemetría del nodo…");
                hint.Foreground = FxUi.TextoMuted;
            }
            return;
        }

        if (flow != null) flow.Text = FxUi.Num(ln.CaudalLmin, 2);
        if (pwm  != null) pwm.Text  = ln.Pwm.ToString(CultureInfo.InvariantCulture);
        if (pul  != null) pul.Text  = ln.Pulsos.ToString(CultureInfo.InvariantCulture);
        if (hint == null) return;

        if (!_pwmAplicado)
        {
            hint.Text = PilotX.Cockpit.Bars.Traductor.T("Listo. Aplicá un valor para empezar.");
            hint.Foreground = FxUi.TextoMuted;
        }
        else if (ln.CaudalLmin > 0.05)
        {
            hint.Text = PilotX.Cockpit.Bars.Traductor.T("Hay caudal — la reguladora abre con este valor.");
            hint.Foreground = FxUi.Ok;
        }
        else
        {
            hint.Text = PilotX.Cockpit.Bars.Traductor.T("Sin caudal — subí el valor con +.");
            hint.Foreground = FxUi.Err;
        }
    }

    /// <summary>Todo camino de salida pasa por acá: frena los timers y manda
    /// parar el motor.</summary>
    private void CerrarPwm(bool pararMotor)
    {
        try { _pwmHb?.Stop(); } catch { }
        _pwmHb = null;
        try { _pwmLiveTimer?.Stop(); } catch { }
        _pwmLiveTimer = null;

        if (pararMotor && _ctx.Client != null && !string.IsNullOrEmpty(_pwmUid))
        {
            int pid = _pwmProdId;
            string uid = _pwmUid;
            // Sin await a propósito: el cierre no puede quedar esperando la red.
            _ = _ctx.Client.SendCmdAsync(uid, "manual_stop", new { producto_id = pid });
        }
        _pwmAplicado = false;
        _pwmUid = "";
        var ov = this.FindControl<Border>("PwmOverlay");
        if (ov != null) ov.IsVisible = false;
    }

    private void OnPwmDirPosClick(object? s, RoutedEventArgs e)
    {
        _pwmNeg = false; PintarDireccion();
        if (_pwmAplicado) _ = AplicarPwmAsync();
    }

    private void OnPwmDirNegClick(object? s, RoutedEventArgs e)
    {
        _pwmNeg = true; PintarDireccion();
        if (_pwmAplicado) _ = AplicarPwmAsync();
    }

    private void OnPwmAplicarClick(object? s, RoutedEventArgs e) => _ = AplicarPwmAsync();

    private async void OnPwmPararClick(object? s, RoutedEventArgs e)
    {
        _pwmAplicado = false;
        if (_ctx.Client == null || string.IsNullOrEmpty(_pwmUid)) return;
        await _ctx.Client.SendCmdAsync(_pwmUid, "manual_pwm",
            new { producto_id = _pwmProdId, value = 0 }, _ctx.Ct).ConfigureAwait(true);
    }

    private void OnPwmCerrarClick(object? s, RoutedEventArgs e) => CerrarPwm(pararMotor: true);

    private void OnPwmBackdropPressed(object? s, PointerPressedEventArgs e)
    {
        // Tocar el fondo cierra Y para el motor (igual que el modal del HTML).
        if (ReferenceEquals(e.Source, this.FindControl<Border>("PwmOverlay")))
            CerrarPwm(pararMotor: true);
        e.Handled = true;
    }

    private async void OnPwmGuardarClick(object? s, RoutedEventArgs e)
    {
        if (_ctx.Client == null || string.IsNullOrEmpty(_pwmUid)) return;
        int mag = PwmMagnitud();
        bool neg = _pwmNeg;
        string uid = _pwmUid;
        int idx = _pwmProdIdx;

        await _ctx.Client.SendCmdAsync(uid, "save_pwm_min",
            new { producto_id = _pwmProdId, dir = neg ? "neg" : "pos", value = mag }, _ctx.Ct)
            .ConfigureAwait(true);

        // El piso "abrir" es el PWM mínimo que usa el PID. Además de guardarlo en
        // el nodo hay que persistirlo: si no, el bridge sigue reenviando el valor
        // viejo en cada target (~200 ms) y pisa lo que el nodo acaba de guardar.
        // El sentido "cerrar" va a otro registro del nodo que la UI no edita.
        if (!neg)
        {
            foreach (var n in _ctx.Nodos())
            {
                if (!string.Equals(n.Uid, uid, StringComparison.Ordinal)) continue;
                var ps = FxCtx.Productos(n);
                if (idx >= 0 && idx < ps.Count) ps[idx].PwmMin = mag;
                break;
            }
        }

        CerrarPwm(pararMotor: true);
        RebuildTabActiva();
        if (!neg) await GuardarConfigAsync().ConfigureAwait(true);

        await AlertarAsync("PWM mínimo guardado",
            PilotX.Cockpit.Bars.Traductor.T("Se guardó el PWM mínimo")
            + " (" + PilotX.Cockpit.Bars.Traductor.T(neg ? "cerrar" : "abrir") + ") = "
            + mag.ToString(CultureInfo.InvariantCulture)
            + (neg ? PilotX.Cockpit.Bars.Traductor.T(" en el nodo.")
                   : PilotX.Cockpit.Bars.Traductor.T(" en el nodo y en la configuración."))).ConfigureAwait(true);
    }

    // =======================================================================
    //  Pie / header
    // =======================================================================

    private void OnGuardarClick(object? s, RoutedEventArgs e) => _ = GuardarConfigAsync();

    private async void OnRecargarClick(object? s, RoutedEventArgs e)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        await RecargarConfigAsync(ct).ConfigureAwait(true);
        ArmarSelectorNodos();
        RebuildTabActiva();
        SetEstado("Configuración recargada", "", limpiarEnMs: 2500);
    }

    private void OnCerrarClick(object? s, RoutedEventArgs e) => OnRequestCerrar?.Invoke();
    private void OnMonitorClick(object? s, RoutedEventArgs e) => OnRequestMonitor?.Invoke();
}
