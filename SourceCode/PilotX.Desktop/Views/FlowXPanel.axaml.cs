// FlowXPanel.axaml.cs
//
// Reemplazo nativo (live-only) de pages/flowx.html. Combina 3 fuentes:
//   - HudSnapshot (PilotX a 4Hz): vel/secciones/area + section count
//   - FlowXLive (broker MQTT a 1Hz): caudal real + PWM + estado PID
//   - FlowXConfig (json cacheado): producto activo + ancho_barra + dosis
//
// El editor (productos / cables / PID) NO esta portado: el boton
// "Configurar" abre el WebView lazy sobre pages/flowx.html. Strangler-fig
// puro — al portar tablas dinamicas se elimina ese boton.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class FlowXPanel : UserControl
{
    private FlowXClient? _client;
    private CancellationTokenSource? _cts;
    private FlowXConfig? _config;
    private FlowXLiveSnapshot? _live;
    private HudSnapshot? _hud;

    // Callback opcional para que MainWindow abra el WebView con la pagina
    // del Hub cuando el operario pide "Configurar".
    public Action? OnRequestConfigurar { get; set; }

    private static readonly IBrush _brushOk   = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushErr  = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushDim  = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush _textHi    = new SolidColorBrush(Color.Parse("#E2E7E2"));

    public FlowXPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Arranca el polling (1Hz live + 5s config refresh).</summary>
    public void Attach(FlowXClient client)
    {
        _client = client;
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    /// <summary>
    /// Recibe el HudSnapshot del HudPoller principal. MainWindow lo pushea
    /// cuando el panel esta visible. Render se hace en cada tick local
    /// (no aca) para evitar redibujos a 4Hz innecesarios.
    /// </summary>
    public void OnSnapshot(HudSnapshot s)
    {
        _hud = s;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await RefreshConfigAsync(ct).ConfigureAwait(false);
        await TickAsync(ct).ConfigureAwait(false);
        int cfgEveryN = 5; // refresca config cada 5s
        int n = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            n++;
            if (n >= cfgEveryN) { n = 0; await RefreshConfigAsync(ct).ConfigureAwait(false); }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshConfigAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var c = await _client.GetConfigAsync(ct).ConfigureAwait(false);
        if (c != null) _config = c;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_client == null) return;
        _live = await _client.GetLiveAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(Render);
    }

    // Nodo activo del config: el primero habilitado, o el primero si ninguno.
    private FlowXNodoConfig? ActiveNodo()
    {
        var nodos = _config?.Nodos;
        if (nodos == null || nodos.Count == 0) return null;
        for (int i = 0; i < nodos.Count; i++)
            if (nodos[i].Habilitado) return nodos[i];
        return nodos[0];
    }

    private void Render()
    {
        var nodo = ActiveNodo();
        var hud  = _hud;

        // ---- Secciones / velocidad / ancho ------------------------------
        int onCount = 0, total = 0;
        if (hud != null)
        {
            total = hud.NumSections > 0 ? hud.NumSections
                                         : (hud.SectionOnRequest?.Length ?? 0);
            if (hud.SectionOnRequest != null)
            {
                for (int i = 0; i < hud.SectionOnRequest.Length; i++)
                    if (hud.SectionOnRequest[i]) onCount++;
            }
        }
        SetText("KpiSec", onCount.ToString(CultureInfo.InvariantCulture));
        SetText("KpiSecTotal", "de " + total.ToString(CultureInfo.InvariantCulture));

        double vel = hud != null ? hud.AvgSpeed : 0;
        SetText("KpiSpeed", vel.ToString("0.0", CultureInfo.InvariantCulture) + " km/h");

        double anchoTotal = nodo != null ? nodo.AnchoBarraM : 0;
        SetText("KpiAncho", anchoTotal > 0
            ? anchoTotal.ToString("0.0", CultureInfo.InvariantCulture) + " m"
            : "-- m");

        double anchoActivo = (total > 0 && anchoTotal > 0)
            ? anchoTotal * onCount / total
            : anchoTotal;

        // ---- Dosis del producto activo (primero del nodo) ---------------
        double dosis = 0;
        if (nodo?.Productos != null && nodo.Productos.Count > 0)
            dosis = nodo.Productos[0].DosisLha;
        SetText("KpiDose", dosis > 0
            ? dosis.ToString("0", CultureInfo.InvariantCulture)
            : "--");

        // Target L/min = dosis × vel × ancho_activo / 600  (formula del bridge).
        double target = (dosis > 0 && vel > 0 && anchoActivo > 0)
            ? dosis * vel * anchoActivo / 600.0
            : 0;
        SetText("KpiTarget", target > 0
            ? target.ToString("0.00", CultureInfo.InvariantCulture) + " L/min"
            : "-- L/min");

        // ---- Live del nodo activo (caudal real, PWM, PID estado) -------
        FlowXNodoLive? liveNodo = null;
        if (_live?.Nodos != null && nodo != null)
        {
            for (int i = 0; i < _live.Nodos.Count; i++)
            {
                if (string.Equals(_live.Nodos[i].Uid, nodo.Uid, StringComparison.OrdinalIgnoreCase))
                { liveNodo = _live.Nodos[i]; break; }
            }
        }

        bool liveOk = liveNodo != null && liveNodo.Online;
        SetText("KpiFlow", liveOk ? liveNodo!.CaudalLmin.ToString("0.00", CultureInfo.InvariantCulture) : "--");
        SetText("KpiPwm",  liveOk ? liveNodo!.Pwm.ToString(CultureInfo.InvariantCulture) : "--");
        SetText("KpiPid",  liveOk ? (string.IsNullOrEmpty(liveNodo!.PidEstado) ? "ok" : liveNodo.PidEstado)
                                   : (nodo == null ? "Sin config" : "Esperando telemetria"));

        // Pill de estado general (verde si live ok, ambar si esperando, rojo
        // si hay nodo configurado pero sin telemetria, gris si nada).
        var (dotBrush, label) = (nodo, liveOk) switch
        {
            (null, _)     => (_brushDim,  "Sin nodo configurado"),
            (_,    true)  => (_brushOk,   "Operando"),
            (_,    false) => (_brushWarn, "Esperando telemetria")
        };
        var dot = this.FindControl<Ellipse>("EstadoDot");
        var stxt = this.FindControl<TextBlock>("EstadoText");
        if (dot  != null) dot.Fill = dotBrush;
        if (stxt != null) { stxt.Text = label; stxt.Foreground = dotBrush; }

        // ---- Savings por corte automatico -------------------------------
        double netoHa = hud != null ? hud.ActualAreaCoveredM2 / 10000.0 : 0;
        double workedHa = hud != null ? hud.WorkedAreaTotalM2 / 10000.0 : 0;
        double overlapHa = Math.Max(0.0, workedHa - netoHa);
        double overlapPct = workedHa > 0.001 ? (overlapHa / workedHa) * 100.0 : 0;
        double savedL = overlapHa * dosis;

        SetText("KpiAreaNeta",    netoHa  > 0 ? FmtHa(netoHa)  : "--");
        SetText("KpiAreaOverlap", overlapHa > 0 ? FmtHa(overlapHa) : "--");
        SetText("KpiOverlapPct",  workedHa > 0.001
            ? overlapPct.ToString("0.0", CultureInfo.InvariantCulture)
            : "--");
        SetText("KpiSavedLitros", (overlapHa > 0 && dosis > 0)
            ? FmtAmount(savedL)
            : "--");

        // ---- Lista de nodos --------------------------------------------
        RenderNodos();
    }

    private void RenderNodos()
    {
        var list = this.FindControl<StackPanel>("NodosList");
        var empty = this.FindControl<TextBlock>("NodosEmpty");
        if (list == null) return;

        var nodos = _config?.Nodos;
        if (nodos == null || nodos.Count == 0)
        {
            for (int i = list.Children.Count - 1; i >= 0; i--)
                if (list.Children[i] != empty) list.Children.RemoveAt(i);
            if (empty != null) empty.IsVisible = true;
            return;
        }

        if (empty != null) empty.IsVisible = false;
        for (int i = list.Children.Count - 1; i >= 0; i--)
            if (list.Children[i] != empty) list.Children.RemoveAt(i);

        foreach (var n in nodos)
        {
            FlowXNodoLive? ln = null;
            if (_live?.Nodos != null)
            {
                for (int i = 0; i < _live.Nodos.Count; i++)
                {
                    if (string.Equals(_live.Nodos[i].Uid, n.Uid, StringComparison.OrdinalIgnoreCase))
                    { ln = _live.Nodos[i]; break; }
                }
            }
            bool online = ln != null && ln.Online;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };
            row.Children.Add(new TextBlock
            {
                Text = online ? "●" : "○",
                Foreground = online ? _brushOk : _brushDim,
                FontSize = 14,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 8, 0)
            });
            Grid.SetColumn((Control)row.Children[0], 0);

            var name = new TextBlock
            {
                Text = (string.IsNullOrEmpty(n.Nombre) ? n.Uid : n.Nombre) ?? "--",
                Foreground = _textHi,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            string meta = online && ln != null
                ? ln.CaudalLmin.ToString("0.00", CultureInfo.InvariantCulture) + " L/min · PWM " + ln.Pwm
                : "offline";
            var metaBlock = new TextBlock
            {
                Text = meta,
                Foreground = _brushDim,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(metaBlock, 2);
            row.Children.Add(metaBlock);

            list.Children.Add(row);
        }
    }

    private void OnConfigurarClick(object? sender, RoutedEventArgs e)
    {
        OnRequestConfigurar?.Invoke();
    }

    private static string FmtHa(double ha)
    {
        if (ha >= 100) return ha.ToString("0", CultureInfo.InvariantCulture);
        return ha.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FmtAmount(double v)
    {
        if (v >= 1000) return v.ToString("0", CultureInfo.InvariantCulture);
        if (v >= 10)   return v.ToString("0.0", CultureInfo.InvariantCulture);
        return v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void SetText(string name, string value)
    {
        var t = this.FindControl<TextBlock>(name);
        if (t != null) t.Text = value;
    }
}
