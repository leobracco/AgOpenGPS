// QuantiXPanel.axaml.cs
//
// Reemplazo nativo (Monitor live-only) de pages/quantix.html. Consume el
// /api/quantix/live a 2Hz mientras esta visible. Replica el flujo de
// renderMotorCard del JS:
//   - Pill estado por motor (en setpoint / ajustando / fuera de setpoint /
//     sin telemetria) segun delta% real-target y staleness (lastSeenUtc > 3s)
//   - Bigtext PPS real (con color por delta) + Objetivo
//   - Gauge horizontal 0..150% con marker al 100% (target)
//   - PWM como barra + "X / 4095 · Y%"
//   - KV: RPM, Pulsos, edad lectura
//
// El editor (Motores CRUD, Shape upload, PID live-tune, Calibracion, Prueba)
// sigue en HTML — el boton Configurar dispara OnRequestConfigurar.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class QuantiXPanel : UserControl
{
    private QuantiXClient? _client;
    private CancellationTokenSource? _cts;
    private QuantiXLiveSnapshot? _live;

    private const double FRESH_MS = 3000.0;

    // Callback opcional para que MainWindow abra el WebView lazy con la
    // pagina del Hub cuando el operario pide "Configurar".
    public Action? OnRequestConfigurar { get; set; }

    private static readonly IBrush _brushOk     = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn   = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushErr    = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushDim    = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush _textHi      = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush _textDim     = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush _textMid     = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush _bgMid       = new SolidColorBrush(Color.Parse("#13191A"));
    private static readonly IBrush _bgHigh      = new SolidColorBrush(Color.Parse("#1B231E"));
    private static readonly IBrush _border      = new SolidColorBrush(Color.Parse("#2A332C"));
    private static readonly IBrush _gaugeBg     = new SolidColorBrush(Color.Parse("#2A332C"));

    public QuantiXPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Arranca el polling 2Hz al /api/quantix/live.</summary>
    public void Attach(QuantiXClient client)
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

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Tick inmediato para no esperar 500ms en abrir.
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_client != null)
        {
            var snap = await _client.GetLiveAsync(ct).ConfigureAwait(false);
            _live = snap; // null si fallo: se renderiza como "sin datos"
        }
        await Dispatcher.UIThread.InvokeAsync(Render);
    }

    private void Render()
    {
        var nodosDot  = this.FindControl<Ellipse>("NodosDot");
        var nodosText = this.FindControl<TextBlock>("NodosText");
        var host      = this.FindControl<StackPanel>("NodosHost");
        var emptyHint = this.FindControl<TextBlock>("EmptyHint");

        var nodos = _live?.Nodos;
        int count = nodos?.Count ?? 0;

        // Header pill
        IBrush dot; string label;
        if (_live == null)        { dot = _brushDim;  label = "sin conexion"; }
        else if (count == 0)      { dot = _brushWarn; label = "0 nodos"; }
        else                      { dot = _brushOk;   label = count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " nodo" : " nodos"); }
        if (nodosDot  != null) nodosDot.Fill   = dot;
        if (nodosText != null) nodosText.Text  = label;

        // Lista de nodos
        if (host == null) return;
        host.Children.Clear();

        if (count == 0)
        {
            if (emptyHint != null) emptyHint.IsVisible = true;
            return;
        }
        if (emptyHint != null) emptyHint.IsVisible = false;

        foreach (var n in nodos!)
        {
            host.Children.Add(BuildNodoSection(n));
        }
    }

    // ---------- Nodo section ---------------------------------------------

    private Control BuildNodoSection(QuantiXNodoLive n)
    {
        bool online = n.Online;
        var sp = new StackPanel { Spacing = 8 };

        // Header del nodo: titulo + chip estado
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var headTitle = new StackPanel { Spacing = 2 };
        headTitle.Children.Add(new TextBlock
        {
            Text       = "QuantiX node " + (n.Uid ?? "?"),
            Foreground = _textHi,
            FontSize   = 15,
            FontWeight = FontWeight.SemiBold,
        });
        string subtitle = (n.Ip ?? "--") + " | fw " + (n.Firmware ?? "--");
        headTitle.Children.Add(new TextBlock
        {
            Text       = subtitle,
            Foreground = _textDim,
            FontSize   = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        });
        Grid.SetColumn(headTitle, 0);
        head.Children.Add(headTitle);

        var chip = new Border
        {
            Background      = _bgHigh,
            BorderBrush     = online ? _brushOk : _brushErr,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(999),
            Padding         = new global::Avalonia.Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chipSp = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        chipSp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = online ? _brushOk : _brushErr, VerticalAlignment = VerticalAlignment.Center });
        chipSp.Children.Add(new TextBlock { Text = online ? "online" : "offline", Foreground = online ? _brushOk : _brushErr, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        chip.Child = chipSp;
        Grid.SetColumn(chip, 1);
        head.Children.Add(chip);

        sp.Children.Add(head);

        // Cards de motor en grilla horizontal
        var motors = n.MotorsLive;
        if (motors == null || motors.Count == 0)
        {
            sp.Children.Add(new TextBlock
            {
                Text       = "Sin telemetria todavia - esperando status_live...",
                Foreground = _textDim,
                FontSize   = 12,
            });
        }
        else
        {
            // Orden por id estable
            motors.Sort((a, b) => a.Id.CompareTo(b.Id));
            var wrap = new WrapPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal };
            foreach (var m in motors)
                wrap.Children.Add(BuildMotorCard(online, m));
            sp.Children.Add(wrap);
        }

        // Border container
        return new Border
        {
            Background      = _bgMid,
            BorderBrush     = _border,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(10),
            Padding         = new global::Avalonia.Thickness(16, 14, 16, 14),
            Child           = sp,
        };
    }

    private Control BuildMotorCard(bool nodoOnline, QuantiXMotorLive m)
    {
        double target = m.PpsTarget;
        double real   = m.PpsReal;
        int    pwm    = m.Pwm;
        bool stale = AgeMs(m.LastSeenUtc) > FRESH_MS;
        bool healthy = nodoOnline && !stale;

        double delta = DeltaPct(target, real);
        double absDelta = Math.Abs(delta);

        // Color por delta% (verde <=2, ambar <=8, rojo >8)
        IBrush mainBrush = !healthy ? _brushErr
                          : absDelta <= 2 ? _brushOk
                          : absDelta <= 8 ? _brushWarn
                          : _brushErr;

        string pillLabel = !healthy ? "sin telemetria"
                          : absDelta <= 2 ? "en setpoint"
                          : absDelta <= 8 ? "ajustando"
                          : "fuera de setpoint";

        // Gauge real/target: 0..150% del target.
        double ratioPct = target > 0 ? Math.Min(150.0, Math.Max(0.0, (real / target) * 100.0)) : 0;
        double fillFrac = ratioPct / 150.0; // 0..1 dentro del width
        double markerFrac = target > 0 ? (100.0 / 150.0) : 0.0;
        int pwmPct = Math.Max(0, Math.Min(100, (int)Math.Round((pwm / 4095.0) * 100.0)));

        var sp = new StackPanel { Spacing = 8 };

        // Head: "Motor X" + pill
        var headG = new Grid();
        headG.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        headG.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var motorTitle = new TextBlock
        {
            Text       = "Motor " + ((m.Id + 1).ToString(CultureInfo.InvariantCulture)),
            Foreground = _textHi,
            FontSize   = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(motorTitle, 0);
        headG.Children.Add(motorTitle);

        var pill = new Border
        {
            Background      = _bgHigh,
            BorderBrush     = mainBrush,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(999),
            Padding         = new global::Avalonia.Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pillSp = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        pillSp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = mainBrush, VerticalAlignment = VerticalAlignment.Center });
        pillSp.Children.Add(new TextBlock { Text = pillLabel, Foreground = mainBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        pill.Child = pillSp;
        Grid.SetColumn(pill, 1);
        headG.Children.Add(pill);

        sp.Children.Add(headG);

        // Readouts: PPS real (grande, color por delta) + Objetivo
        var readG = new Grid();
        readG.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        readG.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var realSp = new StackPanel { Spacing = 2 };
        realSp.Children.Add(new TextBlock { Text = "PPS real", Foreground = _textDim, FontSize = 10, FontWeight = FontWeight.SemiBold });
        realSp.Children.Add(new TextBlock
        {
            Text       = real.ToString("0.0", CultureInfo.InvariantCulture),
            Foreground = mainBrush,
            FontSize   = 32,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        });
        Grid.SetColumn(realSp, 0);
        readG.Children.Add(realSp);

        var tgtSp = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        tgtSp.Children.Add(new TextBlock { Text = "Objetivo", Foreground = _textDim, FontSize = 10, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right });
        tgtSp.Children.Add(new TextBlock
        {
            Text       = target.ToString("0.0", CultureInfo.InvariantCulture),
            Foreground = _textHi,
            FontSize   = 32,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        Grid.SetColumn(tgtSp, 1);
        readG.Children.Add(tgtSp);

        sp.Children.Add(readG);

        // Gauge real/target (0..150%, marker visual del target al 100%)
        sp.Children.Add(BuildGauge(fillFrac, markerFrac, mainBrush, target > 0));

        // Leyenda gauge: 0 | Δ +X.X% | +50%
        string deltaStr = (delta >= 0 ? "+" : "") + delta.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        sp.Children.Add(BuildLegendRow("0", "delta " + deltaStr, "+50%", mainBrush));

        // PWM
        sp.Children.Add(new TextBlock
        {
            Text       = "PWM",
            Foreground = _textDim,
            FontSize   = 10,
            FontWeight = FontWeight.SemiBold,
            Margin     = new global::Avalonia.Thickness(0, 4, 0, 0),
        });
        sp.Children.Add(BuildGauge(pwmPct / 100.0, -1, _brushOk, false));
        sp.Children.Add(BuildLegendRow("0",
            pwm.ToString(CultureInfo.InvariantCulture) + " / 4095 - " + pwmPct.ToString(CultureInfo.InvariantCulture) + "%",
            "4095", _textDim));

        // KV: RPM, Pulsos, Visto
        var kv = new Grid { Margin = new global::Avalonia.Thickness(0, 6, 0, 0) };
        kv.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        kv.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        kv.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        kv.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        kv.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        kv.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        AddKv(kv, 0, 0, "RPM", m.Rpm.ToString(CultureInfo.InvariantCulture));
        AddKv(kv, 0, 2, "Pulsos", m.Pulsos.ToString("#,0", CultureInfo.InvariantCulture));
        double ageSec = AgeMs(m.LastSeenUtc) / 1000.0;
        AddKv(kv, 1, 0, "Visto", (stale ? "! " : "") + ageSec.ToString("0.0", CultureInfo.InvariantCulture) + "s");
        sp.Children.Add(kv);

        return new Border
        {
            Background      = _bgHigh,
            BorderBrush     = _border,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(8),
            Padding         = new global::Avalonia.Thickness(14, 12, 14, 12),
            Margin          = new global::Avalonia.Thickness(0, 0, 10, 10),
            Width           = 280,
            Child           = sp,
        };
    }

    private static Control BuildGauge(double fillFrac, double markerFrac, IBrush fillBrush, bool showMarker)
    {
        var grid = new Grid { Height = 12 };
        // Background track
        var track = new Border
        {
            Background    = _gaugeBg,
            CornerRadius  = new global::Avalonia.CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        grid.Children.Add(track);

        // Fill (proporcional). Uso un panel relativo con HorizontalAlignment Left + Width binding via Grid trick:
        // Avalonia no tiene "percent width" facil — uso ProgressBar disguised o calculo en code-behind.
        // Truco: usamos un Grid con 2 columnas que suman *: la primera fill, la segunda spacer.
        var fillGrid = new Grid();
        double safe = Math.Max(0.0, Math.Min(1.0, fillFrac));
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(safe, GridUnitType.Star)));
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.0 - safe, GridUnitType.Star)));
        var fillBar = new Border
        {
            Background    = fillBrush,
            CornerRadius  = new global::Avalonia.CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(fillBar, 0);
        fillGrid.Children.Add(fillBar);
        grid.Children.Add(fillGrid);

        // Marker (raya blanca vertical) — mismo truco columns
        if (showMarker && markerFrac >= 0)
        {
            var mGrid = new Grid();
            double mSafe = Math.Max(0.0, Math.Min(1.0, markerFrac));
            mGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(mSafe, GridUnitType.Star)));
            mGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.0 - mSafe, GridUnitType.Star)));
            var marker = new Border
            {
                Background = Brushes.White,
                Width      = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(marker, 0);
            mGrid.Children.Add(marker);
            grid.Children.Add(mGrid);
        }

        return grid;
    }

    private static Control BuildLegendRow(string left, string center, string right, IBrush centerBrush)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var l = new TextBlock { Text = left, Foreground = _textDim, FontSize = 10, FontFamily = new FontFamily("Consolas, Courier New, monospace") };
        var c = new TextBlock { Text = center, Foreground = centerBrush, FontSize = 10, FontFamily = new FontFamily("Consolas, Courier New, monospace"), HorizontalAlignment = HorizontalAlignment.Center };
        var r = new TextBlock { Text = right, Foreground = _textDim, FontSize = 10, FontFamily = new FontFamily("Consolas, Courier New, monospace"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(l, 0); Grid.SetColumn(c, 1); Grid.SetColumn(r, 2);
        g.Children.Add(l); g.Children.Add(c); g.Children.Add(r);
        return g;
    }

    private static void AddKv(Grid kv, int row, int col, string key, string val)
    {
        var k = new TextBlock { Text = key, Foreground = _textDim, FontSize = 10, FontWeight = FontWeight.SemiBold, Margin = new global::Avalonia.Thickness(0, 2, 6, 2) };
        var v = new TextBlock { Text = val, Foreground = _textMid, FontSize = 12, FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas, Courier New, monospace"), Margin = new global::Avalonia.Thickness(0, 2, 12, 2), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(k, row); Grid.SetColumn(k, col);
        Grid.SetRow(v, row); Grid.SetColumn(v, col + 1);
        kv.Children.Add(k);
        kv.Children.Add(v);
    }

    // ---------- helpers --------------------------------------------------

    private static double DeltaPct(double target, double real)
    {
        if (target <= 0) return 0;
        return (real - target) / target * 100.0;
    }

    private static double AgeMs(string? lastSeenIso)
    {
        if (string.IsNullOrEmpty(lastSeenIso)) return double.MaxValue;
        if (!DateTime.TryParse(lastSeenIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return double.MaxValue;
        var age = (DateTime.UtcNow - t).TotalMilliseconds;
        return age < 0 ? 0 : age;
    }

    private void OnConfigurarClick(object? sender, RoutedEventArgs e)
    {
        OnRequestConfigurar?.Invoke();
    }
}
