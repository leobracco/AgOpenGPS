// ============================================================================
// VistaXEditorPanel.axaml.cs — shell del editor nativo de VistaX.
//
// QUÉ QUEDÓ NATIVO (esto cierra el hueco): las pantallas que antes vivían en
// pages/vistax.html y se abrían con "Configurar" — Insumo & calibración,
// Implemento y Config. Con esto el panel de VistaX ya no despierta Chromium
// para nada.
// QUÉ SIGUE EN HTML: la página vistax.html, vistax.js y vistax-insumo.js,
// intactos, porque los usa la PWA del celular (regla del repo: las páginas
// HTML no se borran).
// QUÉ NO SE PORTA A PROPÓSITO: el tab "Nodos" (la misma grilla ya está en el
// VistaXPanel del monitor; acá alcanza el desplegable de UID con online/
// offline) y los campos MQTT de la config (los maneja CoreX).
//
// Transporte: el mismo VistaXClient del monitor. El editor NO necesita el live
// a 2 Hz — con un tick de 3 s para refrescar los nodos vistos alcanza
// (frecuencia "nodos" de la doctrina). La ventana de calibración sí usa su
// propio polling de 250 ms con un CTS aparte.
//
// Regla del árbol bajo el dedo: el tick NO reconstruye la pantalla; solo
// refresca los desplegables de UID cuando cambió el set de nodos. Reconstruir
// en el tick tira el foco del TextBox y cierra el teclado nativo.
//
// Seguridad: Detach() cancela la ventana de calibración en el backend. Si no,
// cerrar el panel con los 5 s corriendo dejaba una calibración colgada y el
// próximo start podía fallar (agujero que la página HTML también tenía).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views.VistaXEditor;

namespace PilotX.Desktop.Views;

public partial class VistaXEditorPanel : UserControl, IPanelEmbebible
{
    private readonly VxCtx _ctx = new();
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, VxTab> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _tabBtns = new(StringComparer.Ordinal);
    private string _tabActiva = "insumo";

    // ---- calibración -------------------------------------------------------
    private CancellationTokenSource? _calibCts;
    private string _calibModo = "objetivo";
    private bool   _calibEnCurso;      // hay una ventana viva en el backend

    /// <summary>El operario cerró el editor.</summary>
    public Action? OnRequestCerrar { get; set; }
    /// <summary>Volver al monitor live de VistaX.</summary>
    public Action? OnRequestMonitor { get; set; }
    /// <summary>Abrir el catálogo de insumos (pantalla propia del Hub).</summary>
    public Action? OnRequestAbrirInsumos { get; set; }
    /// <summary>Abrir la configuración del implemento central.</summary>
    public Action? OnRequestAbrirConfigCentral { get; set; }

    /// <summary>Aviso corto → toast del host. Nunca modal.</summary>
    public event Action<string>? Aviso;

    private static readonly (string Clave, string Titulo)[] TABS =
    {
        ("insumo",     "Insumo & calibración"),
        ("implemento", "Implemento"),
        ("config",     "Config"),
    };

    public VistaXEditorPanel()
    {
        InitializeComponent();
        _ctx.Aviso              = m => Aviso?.Invoke(m);
        _ctx.AbrirInsumos       = () => OnRequestAbrirInsumos?.Invoke();
        _ctx.AbrirConfigCentral = () => OnRequestAbrirConfigCentral?.Invoke();
        _ctx.IniciarCalibracion = IniciarCalibracionAsync;
        ArmarTabStrip();

        // Teclado nativo del campo de override (sin esto queda ineditable en
        // la pantalla táctil de cabina).
        var ov = this.FindControl<TextBox>("CalibOverride");
        if (ov != null)
        {
            ov.GotFocus  += (_, __) => { if (_ctx.Client != null) _ = _ctx.Client.TecladoAsync(true, true, "Valor (sem/m)"); };
            ov.LostFocus += (_, __) => { if (_ctx.Client != null) _ = _ctx.Client.TecladoAsync(false); };
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio. "‹ Monitor" también se esconde: la Configuración
    /// deja OnRequestMonitor sin cablear a propósito (el monitor es un overlay
    /// del mapa y abrirlo cerraría la tarjeta) — un botón muerto confunde.</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnMonitor"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    // =======================================================================
    //  Ciclo de vida
    // =======================================================================

    public void Attach(VistaXClient client, string? tab = null)
    {
        _ctx.Client = client;
        string? destino = null;
        if (!string.IsNullOrEmpty(tab))
            foreach (var t in TABS) if (t.Clave == tab) { destino = tab; break; }

        if (_cts != null)
        {
            if (destino != null && destino != _tabActiva) _ = MostrarTabAsync(destino);
            else PintarTabStrip();
            return;
        }
        if (destino != null) _tabActiva = destino;
        _cts = new CancellationTokenSource();
        _ = ArrancarAsync(_cts.Token);
    }

    public void Detach()
    {
        // Una ventana de calibración abierta tiene que cancelarse en el
        // backend ANTES de soltar el token.
        try { if (_calibEnCurso) _ = _ctx.Client?.CalibrarCancelAsync(); } catch { }
        _calibEnCurso = false;
        try { _calibCts?.Cancel(); } catch { }
        _calibCts = null;
        var ov = this.FindControl<Border>("CalibOverlay");
        if (ov != null) ov.IsVisible = false;

        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _ = _ctx.Client?.TecladoAsync(false);

        // Lo leído del backend NO sobrevive al cierre: la próxima vez que se
        // abra el editor se vuelve a pedir todo, como hacía la página HTML al
        // cargarse. Con el cache vivo, un implemento guardado desde el celular
        // (o un insumo calibrado) no se veía, y "Guardar" lo pisaba con lo
        // viejo.
        _ctx.LimpiarCache();
    }

    private async Task ArrancarAsync(CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() => _ = MostrarTabAsync(_tabActiva));
        await RunLoopAsync(ct).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Tick liviano: solo los nodos vistos por MQTT (para el
    /// desplegable de UID) y el subtítulo.</summary>
    private async Task TickAsync(CancellationToken ct)
    {
        if (_ctx.Client == null) return;
        var live = await _ctx.Client.GetLiveAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => AplicarTick(live));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void AplicarTick(VistaXLiveSnapshot? live)
    {
        _ctx.Nodos = live?.Nodos ?? new List<VistaXNodoLive>();
        PintarSubtitulo(live);
        if (_tabs.TryGetValue(_tabActiva, out var tab)) tab.Live();
    }

    private void PintarSubtitulo(VistaXLiveSnapshot? live)
    {
        var lbl = this.FindControl<TextBlock>("SubtituloText");
        if (lbl == null) return;
        string txt = PilotX.Cockpit.Bars.Traductor.T("Monitoreo de siembra");
        if (!string.IsNullOrEmpty(live?.NombreImplemento)) txt += " · " + live!.NombreImplemento;
        double tol = live?.ToleranciaDesvio ?? 0;
        if (tol > 0) txt += " · " + PilotX.Cockpit.Bars.Traductor.T("tol") + " ±"
                          + tol.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        int n = _ctx.Nodos.Count;
        txt += " · " + n + " " + PilotX.Cockpit.Bars.Traductor.T(n == 1 ? "nodo" : "nodos");
        if (lbl.Text != txt) lbl.Text = txt;
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
            kv.Value.Background  = activa ? VxUi.BgFila : Brushes.Transparent;
            kv.Value.Foreground  = activa ? VxUi.Texto : VxUi.TextoMuted;
            kv.Value.BorderBrush = activa ? VxUi.Verde : Brushes.Transparent;
            kv.Value.FontWeight  = activa ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private async Task MostrarTabAsync(string clave)
    {
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
            if (tab is VxInsumoTab ins) _ctx.AlAplicarCalibracion = ins.RecargarInsumos;
        }

        var host = this.FindControl<StackPanel>("TabHost");
        if (host != null)
        {
            host.Children.Clear();
            host.Children.Add(tab);
        }

        tab.Rebuild();
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
        try { await tab.AlEntrarAsync().ConfigureAwait(true); } catch { }
    }

    private VxTab CrearTab(string clave) => clave switch
    {
        "implemento" => new VxImplementoTab(_ctx),
        "config"     => new VxConfigTab(_ctx),
        _            => new VxInsumoTab(_ctx),
    };

    // =======================================================================
    //  Calibración "detectar densidad" (overlay interno, jamás modal)
    // =======================================================================

    private async Task IniciarCalibracionAsync(string modo, string insumoId, string nombreInsumo)
    {
        if (_ctx.Client == null) return;
        if (string.IsNullOrEmpty(insumoId))
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
                "No hay insumo activo. Elegí uno antes de calibrar."));
            return;
        }

        var r = await _ctx.Client.CalibrarStartAsync(insumoId, modo, 5).ConfigureAwait(true);
        if (!r.Ok)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo iniciar la calibración")
                          + ": " + r.Texto());
            return;
        }

        _calibModo = modo;
        _calibEnCurso = true;
        AbrirOverlayCalib(modo, nombreInsumo);

        try { _calibCts?.Cancel(); } catch { }
        _calibCts = new CancellationTokenSource();
        _ = PollCalibAsync(_calibCts.Token);
    }

    private void AbrirOverlayCalib(string modo, string nombreInsumo)
    {
        Set<TextBlock>("CalibTitulo", t => t.Text = PilotX.Cockpit.Bars.Traductor.T(
            modo == "saturado" ? "Configurando modo flujo…" : "Detectando densidad…"));
        Set<TextBlock>("CalibInsumo", t => t.Text = PilotX.Cockpit.Bars.Traductor.T("Insumo")
                                                    + ": " + nombreInsumo);
        Set<TextBlock>("CalibLive", t => t.Text = "—");
        Set<TextBlock>("CalibSub", t => t.Text = TextoSub(5.0, 0, 0));
        Set<Border>("CalibSatPill", b => b.IsVisible = false);
        Set<Border>("CalibResultBox", b => b.IsVisible = false);
        Set<Button>("CalibApply", b => b.IsVisible = false);
        Set<Button>("CalibCancel", b => b.Content = PilotX.Cockpit.Bars.Traductor.T("Cancelar"));
        Set<Border>("CalibOverlay", b => b.IsVisible = true);
    }

    private static string TextoSub(double segundos, int muestras, int surcos)
        => segundos.ToString("0.0", CultureInfo.InvariantCulture) + " "
           + PilotX.Cockpit.Bars.Traductor.T("s restantes") + " · "
           + muestras + " " + PilotX.Cockpit.Bars.Traductor.T("muestras") + " · "
           + surcos + " " + PilotX.Cockpit.Bars.Traductor.T("surcos");

    private async Task PollCalibAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var s = _ctx.Client != null
                ? await _ctx.Client.CalibrarStateAsync(ct).ConfigureAwait(false)
                : null;
            if (ct.IsCancellationRequested) return;

            bool terminado = false;
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => terminado = AplicarEstadoCalib(s));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

            if (terminado) return;

            try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Devuelve true cuando la ventana terminó y ya hay resultado.</summary>
    private bool AplicarEstadoCalib(VxCalibState? s)
    {
        if (s == null) return false;   // degradación silenciosa, igual que el JS

        Set<TextBlock>("CalibLive", t => t.Text = s.SemMActual.HasValue
            ? s.SemMActual.Value.ToString("0.0", CultureInfo.InvariantCulture) : "—");
        Set<TextBlock>("CalibSub", t => t.Text = TextoSub(
            s.Running ? Math.Max(0, s.SegundosRestantes) : 0, s.Muestras, s.Surcos?.Count ?? 0));
        Set<Border>("CalibSatPill", b => b.IsVisible = s.Saturado);

        if (s.Running || !s.ListoParaAplicar) return false;

        MostrarResultadoCalib(s);
        return true;
    }

    private void MostrarResultadoCalib(VxCalibState s)
    {
        string modo = string.IsNullOrEmpty(s.Modo) ? _calibModo : s.Modo!;
        double valor = s.ValorFinalSemM;

        // Si el operario pidió "objetivo" pero el sensor saturó, se pasa solo a
        // modo flujo: el promedio no representa la densidad real.
        if (modo != "saturado" && s.Saturado) modo = "saturado";
        _calibModo = modo;

        string texto, valorInicial;
        if (modo == "saturado")
        {
            texto = PilotX.Cockpit.Bars.Traductor.T("Sensor saturado a") + " "
                  + valor.ToString("0.0", CultureInfo.InvariantCulture) + " sem/m. "
                  + PilotX.Cockpit.Bars.Traductor.T(
                      "Asumimos esto como densidad cuando hay flujo constante. Podés editar el "
                      + "número antes de guardar (típico: 80–120 soja, 6–10 maíz).");
            // Sin valor confiable (el sensor satura) se sugiere 90.
            valorInicial = valor > 0 ? valor.ToString("0.0", CultureInfo.InvariantCulture) : "90";
        }
        else
        {
            texto = PilotX.Cockpit.Bars.Traductor.T("Densidad detectada") + ": "
                  + valor.ToString("0.0", CultureInfo.InvariantCulture) + " sem/m. "
                  + PilotX.Cockpit.Bars.Traductor.T("¿Guardar como densidad objetivo del insumo?");
            valorInicial = valor.ToString("0.0", CultureInfo.InvariantCulture);
        }

        Set<TextBlock>("CalibResultLabel", t => t.Text = texto);
        Set<TextBox>("CalibOverride", t => t.Text = valorInicial);
        Set<Border>("CalibResultBox", b => b.IsVisible = true);
        Set<Button>("CalibApply", b =>
        {
            b.IsVisible = true;
            b.Content = PilotX.Cockpit.Bars.Traductor.T(
                modo == "saturado" ? "Guardar modo flujo" : "Guardar densidad objetivo");
        });
        Set<Button>("CalibCancel", b => b.Content = PilotX.Cockpit.Bars.Traductor.T("Descartar"));
    }

    private async void OnCalibApplyClick(object? s, RoutedEventArgs e)
    {
        if (_ctx.Client == null) return;
        var txt = this.FindControl<TextBox>("CalibOverride");
        double val = VxUi.LeerDouble(txt, double.NaN);
        if (!(val > 0))
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Valor inválido."));
            return;
        }
        var r = await _ctx.Client.CalibrarApplyAsync(val).ConfigureAwait(true);
        if (!r.Ok)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo guardar") + ": " + r.Texto());
            return;
        }
        _calibEnCurso = false;
        CerrarOverlayCalib();
        _ctx.AlAplicarCalibracion?.Invoke();
        Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Guardado al insumo"));
    }

    private async void OnCalibCancelClick(object? s, RoutedEventArgs e)
    {
        try { _calibCts?.Cancel(); } catch { }
        _calibCts = null;
        CerrarOverlayCalib();
        if (_calibEnCurso && _ctx.Client != null)
        {
            _calibEnCurso = false;
            await _ctx.Client.CalibrarCancelAsync().ConfigureAwait(true);
        }
    }

    private void CerrarOverlayCalib()
    {
        try { _calibCts?.Cancel(); } catch { }
        _calibCts = null;
        Set<Border>("CalibOverlay", b => b.IsVisible = false);
        _ = _ctx.Client?.TecladoAsync(false);
    }

    /// <summary>El backdrop absorbe el toque pero NO cierra: la ventana de 5 s
    /// se corta con Cancelar/Descartar, que además avisa al backend.</summary>
    private void OnCalibBackdropPressed(object? s, PointerPressedEventArgs e) => e.Handled = true;

    // =======================================================================
    //  Varios
    // =======================================================================

    private void Set<T>(string nombre, Action<T> accion) where T : Control
    {
        var c = this.FindControl<T>(nombre);
        if (c != null) accion(c);
    }

    private void OnCerrarClick(object? s, RoutedEventArgs e) => OnRequestCerrar?.Invoke();
    private void OnMonitorClick(object? s, RoutedEventArgs e) => OnRequestMonitor?.Invoke();
}
