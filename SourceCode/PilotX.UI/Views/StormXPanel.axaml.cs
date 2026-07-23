// StormXPanel.axaml.cs
//
// Reemplazo nativo de pages/stormx.html. Polling 1Hz a /api/stormx/live;
// computa el veredicto y refresca los KPIs. Sin WebView, sin JS.
//
// API: Attach(StormXClient) prende el polling; Detach() lo apaga.
// MainWindow llama Attach() al abrir y Detach() al cerrar — el polling
// vive solo mientras el panel esta visible (zero cost si nunca se abre).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class StormXPanel : UserControl
{
    private StormXClient? _client;
    private CancellationTokenSource? _cts;
    private DateTime _lastSampleUtc = DateTime.MinValue;

    // Brushes para la pill de estado y el verdict color.
    private static readonly IBrush _brushOk   = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushErr  = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushDim  = new SolidColorBrush(Color.Parse("#8FA092"));

    public StormXPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Inyecta el cliente y arranca el polling. Idempotente: si ya estaba
    /// corriendo no duplica el loop.
    /// </summary>
    public void Attach(StormXClient client)
    {
        _client = client;
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    /// <summary>Detiene el polling. Lo llama MainWindow al cerrar el panel.</summary>
    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Primer tick inmediato para no esperar 1s antes de pintar nada.
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var snap = await _client.GetLiveAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Render(snap));
    }

    private void Render(StormXLiveSnapshot? snap)
    {
        // ---- Limits (eco del config) ------------------------------------
        var lim = snap?.Limits;
        SetText("LimWindMax",  lim != null ? lim.WindMaxMs.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "--");
        SetText("LimWindMin",  lim != null ? lim.WindMinMs.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "--");
        SetText("LimHumMin",   lim != null ? lim.HumMinPct.ToString("0", CultureInfo.InvariantCulture)   + " %"   : "--");
        SetText("LimTempMax",  lim != null ? lim.TempMaxC.ToString("0", CultureInfo.InvariantCulture)    + " °C"  : "--");
        SetText("LimDeltaTMax",lim != null ? lim.DeltaTMaxC.ToString("0", CultureInfo.InvariantCulture)  + " °C"  : "--");

        // ---- Nodo activo (primero online; sino primero de la lista) -----
        StormXNodoLive? active = null;
        var nodos = snap?.Nodos;
        if (nodos != null && nodos.Count > 0)
        {
            for (int i = 0; i < nodos.Count; i++)
            {
                if (nodos[i].Online) { active = nodos[i]; break; }
            }
            if (active == null) active = nodos[0];
        }

        bool hasSample = active != null && active.Online;
        if (hasSample) _lastSampleUtc = DateTime.UtcNow;

        // ---- KPIs -------------------------------------------------------
        SetText("KpiWind", hasSample ? active!.WindMs.ToString("0.0", CultureInfo.InvariantCulture) : "--");
        SetText("KpiDir",  hasSample ? active!.WindDir.ToString("0", CultureInfo.InvariantCulture) + " °" : "-- °");
        SetText("KpiTemp", hasSample ? active!.TempC.ToString("0.0", CultureInfo.InvariantCulture) : "--");
        SetText("KpiDeltaT", hasSample ? active!.DeltaTC.ToString("0.0", CultureInfo.InvariantCulture) + " °C" : "-- °C");
        SetText("KpiHum",  hasSample ? active!.HumPct.ToString("0", CultureInfo.InvariantCulture) : "--");
        SetText("KpiPress",hasSample ? active!.PressHpa.ToString("0", CultureInfo.InvariantCulture) + " hPa" : "-- hPa");

        // ---- Veredicto + pill -------------------------------------------
        // Mismas reglas que stormx.js: si algun umbral viola -> NO PULVERIZAR.
        var (verdictText, verdictBrush, pillLabel) = ComputeVerdict(active, lim, hasSample);
        var advice = this.FindControl<TextBlock>("KpiAdvice");
        if (advice != null) { advice.Text = verdictText; advice.Foreground = verdictBrush; }
        var dot = this.FindControl<Ellipse>("EstadoDot");
        var pillTxt = this.FindControl<TextBlock>("EstadoText");
        if (dot     != null) dot.Fill = verdictBrush;
        if (pillTxt != null) { pillTxt.Text = pillLabel; pillTxt.Foreground = verdictBrush; }

        // Edad de la ultima muestra.
        SetText("KpiAge", _lastSampleUtc == DateTime.MinValue ? "--" : AgeStr(_lastSampleUtc));

        // ---- Lista de nodos --------------------------------------------
        RenderNodos(nodos);
    }

    private (string text, IBrush brush, string pill) ComputeVerdict(
        StormXNodoLive? s, StormXLimits? lim, bool hasSample)
    {
        if (!hasSample || s == null || lim == null)
            return ("--", _brushDim, "Condiciones --");

        var fails = new List<string>();
        if (s.WindMs > lim.WindMaxMs)   fails.Add("viento alto");
        if (s.WindMs < lim.WindMinMs)   fails.Add("viento muy bajo");
        if (s.HumPct < lim.HumMinPct)   fails.Add("humedad baja");
        if (s.TempC  > lim.TempMaxC)    fails.Add("temp alta");
        if (s.DeltaTC> lim.DeltaTMaxC)  fails.Add("Delta-T alto");

        if (fails.Count == 0)
            return ("PULVERIZAR OK", _brushOk, "Condiciones OK");
        return ("NO PULVERIZAR · " + string.Join(", ", fails), _brushWarn, "Condiciones malas");
    }

    private void RenderNodos(List<StormXNodoLive>? nodos)
    {
        var list = this.FindControl<StackPanel>("NodosList");
        var empty = this.FindControl<TextBlock>("NodosEmpty");
        if (list == null) return;

        if (nodos == null || nodos.Count == 0)
        {
            // Dejo solo el placeholder y limpio rows previas.
            for (int i = list.Children.Count - 1; i >= 0; i--)
            {
                var ch = list.Children[i];
                if (ch != empty) list.Children.RemoveAt(i);
            }
            if (empty != null) empty.IsVisible = true;
            return;
        }

        if (empty != null) empty.IsVisible = false;
        // Limpio rows previas (todo lo que no sea el placeholder).
        for (int i = list.Children.Count - 1; i >= 0; i--)
        {
            var ch = list.Children[i];
            if (ch != empty) list.Children.RemoveAt(i);
        }

        foreach (var n in nodos)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };
            var dot = new TextBlock
            {
                Text = n.Online ? "●" : "○",
                Foreground = n.Online ? _brushOk : _brushDim,
                FontSize = 14,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            var name = new TextBlock
            {
                Text = string.IsNullOrEmpty(n.Uid) ? "--" : n.Uid,
                Foreground = new SolidColorBrush(Color.Parse("#E2E7E2")),
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            string sub = "fw " + (string.IsNullOrEmpty(n.LastSeenIso) ? "?" : "ok");
            var meta = new TextBlock
            {
                Text = sub,
                Foreground = _brushDim,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(meta, 2);
            row.Children.Add(meta);

            list.Children.Add(row);
        }
    }

    private static string AgeStr(DateTime utc)
    {
        var s = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - utc).TotalSeconds));
        if (s < 60)   return s + "s atras";
        if (s < 3600) return (s / 60) + " min atras";
        return (s / 3600) + " h atras";
    }

    private void SetText(string name, string value)
    {
        var t = this.FindControl<TextBlock>(name);
        if (t != null) t.Text = value;
    }
}
