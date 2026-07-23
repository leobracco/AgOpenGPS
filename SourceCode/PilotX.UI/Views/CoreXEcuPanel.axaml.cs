// CoreXEcuPanel.axaml.cs
//
// Reemplazo nativo (Live tab) de pages/corex-ecu.html. Consume el
// /api/corex-ecu/status a 2Hz mientras esta visible. Pintado en 6 cards:
//   - IMU      (modo, yaw, roll, pitch, yaw rate)
//   - WAS      (fuente, angulo, auto-zero, encoder raw, centro, ticks/deg,
//               ADS1115 presente + raw)
//   - GPS      (km/h, knots, heading, GGA visto)
//   - CAN Keya (steer enable, corriente)
//   - Autosteer(loop, guidance, watchdog, PWM, setpoint)
//   - Sistema  (IP, ethernet, firmware, uptime)
//
// Si /status devuelve ok=false (o el cliente devuelve null), se muestra
// el chip de error con codigo/mensaje/tecnico — mismo flujo que el HTML.
//
// El editor (Estado / checklist, Calibracion, Conexion con Teensy) sigue
// en HTML — el boton Configurar dispara OnRequestConfigurar -> WebView lazy.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class CoreXEcuPanel : UserControl
{
    private CoreXEcuClient? _client;
    private CancellationTokenSource? _cts;
    private CoreXEcuStatus? _live;

    public Action? OnRequestConfigurar { get; set; }

    private static readonly IBrush _brushOk      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn    = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushErr     = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushDim     = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush _textHi       = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush _textMid      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush _textDim      = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush _bgMid        = new SolidColorBrush(Color.Parse("#1A1F1B"));
    private static readonly IBrush _bgHigh       = new SolidColorBrush(Color.Parse("#262C28"));
    private static readonly IBrush _border       = new SolidColorBrush(Color.Parse("#2A332C"));

    public CoreXEcuPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Attach(CoreXEcuClient client)
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
            var snap = await _client.GetStatusAsync(ct).ConfigureAwait(false);
            _live = snap;
        }
        await Dispatcher.UIThread.InvokeAsync(Render);
    }

    private void Render()
    {
        var subtitle  = this.FindControl<TextBlock>("SubtitleText");
        var ecuDot    = this.FindControl<Ellipse>("EcuDot");
        var ecuText   = this.FindControl<TextBlock>("EcuText");
        var errChip   = this.FindControl<Border>("ErrorChip");
        var errCode   = this.FindControl<TextBlock>("ErrorCode");
        var errMsg    = this.FindControl<TextBlock>("ErrorMsg");
        var errTech   = this.FindControl<TextBlock>("ErrorTech");
        var cardsHost = this.FindControl<UniformGrid>("CardsHost");

        var s = _live;

        // Subtitle: firmware + version si los devuelve
        if (subtitle != null)
        {
            string sub = "Telemetria del autosteer (Teensy)";
            if (s?.Ok == true && !string.IsNullOrEmpty(s.Firmware))
                sub += "  ·  " + s.Firmware + (string.IsNullOrEmpty(s.Version) ? "" : (" " + s.Version));
            subtitle.Text = sub;
        }

        // Pill conexion
        IBrush dotBr; IBrush textBr; string label;
        if (s == null)
        {
            dotBr = _brushDim; textBr = _textMid; label = "Hub no responde";
        }
        else if (!s.Ok)
        {
            dotBr = _brushErr; textBr = _brushErr; label = "ECU offline";
        }
        else
        {
            dotBr = _brushOk; textBr = _brushOk; label = "ECU conectado";
        }
        if (ecuDot  != null) ecuDot.Fill = dotBr;
        if (ecuText != null) { ecuText.Text = label; ecuText.Foreground = textBr; }

        // Error chip
        bool showErr = s == null || !s.Ok;
        if (errChip != null) errChip.IsVisible = showErr;
        if (showErr)
        {
            if (errCode != null) errCode.Text = s?.ErrorCode ?? "AGP-NET-201";
            if (errMsg  != null) errMsg.Text  = s?.Error ?? "No se pudo contactar al Hub local.";
            if (errTech != null)
            {
                string tech = s?.ErrorTechnical ?? "(sin detalle)";
                errTech.Text = tech;
                errTech.IsVisible = !string.IsNullOrEmpty(tech);
            }
        }

        // Cards (rebuild en cada tick — son 6, costo despreciable)
        if (cardsHost == null) return;
        cardsHost.Children.Clear();
        cardsHost.Children.Add(BuildImuCard(s?.Imu));
        cardsHost.Children.Add(BuildWasCard(s?.Was));
        cardsHost.Children.Add(BuildGpsCard(s?.Gps));
        cardsHost.Children.Add(BuildCanCard(s?.Can));
        cardsHost.Children.Add(BuildAutosteerCard(s?.Autosteer));
        cardsHost.Children.Add(BuildSistemaCard(s));
    }

    // ---------- card builders ----------------------------------------------

    private Control BuildImuCard(CoreXEcuImu? imu)
    {
        var rows = new List<(string K, string V, IBrush? Br)>
        {
            ("Modo",     imu?.Mode ?? "--", null),
            ("Yaw",      FmtDeg(imu?.YawDeg),     null),
            ("Roll",     FmtDeg(imu?.RollDeg),    null),
            ("Pitch",    FmtDeg(imu?.PitchDeg),   null),
            ("Yaw rate", FmtNum(imu?.YawRateDps, 1) + " °/s", null),
        };
        return BuildCard("IMU", rows);
    }

    private Control BuildWasCard(CoreXEcuWas? w)
    {
        string src = PrettySource(w?.Source);
        string zero = w == null ? "--" : (w.ZeroDone ? "OK (centro capturado)" : "Pendiente");
        IBrush? zeroBr = w == null ? null : (w.ZeroDone ? _brushOk : _brushWarn);

        string srcLow = (w?.Source ?? "").ToLowerInvariant();
        bool isAds = srcLow == "ads_se" || srcLow == "ads_diff";
        string adsPresent;
        if (w == null) adsPresent = "--";
        else if (isAds) adsPresent = w.AdsPresent ? "Sí (chip detectado)" : "No (sin respuesta I²C)";
        else            adsPresent = w.AdsPresent ? "Detectado (no activo)" : "--";
        IBrush? adsBr = w != null && isAds ? (w.AdsPresent ? _brushOk : _brushErr) : null;

        string adsRaw;
        if (w == null) adsRaw = "--";
        else if (isAds) adsRaw = FmtInt(w.AdsRaw);
        else            adsRaw = w.AdsPresent ? FmtInt(w.AdsRaw) : "--";

        var rows = new List<(string K, string V, IBrush? Br)>
        {
            ("Fuente",          src,                          null),
            ("Ángulo",          FmtDeg(w?.AngleDeg),          null),
            ("Auto-zero",       zero,                         zeroBr),
            ("Encoder raw",     FmtInt(w?.EncoderRaw),        null),
            ("Centro (ticks)",  FmtInt(w?.ZeroTicks),         null),
            ("Ticks por grado", FmtNum(w?.TicksPerDeg, 2),    null),
            ("ADS1115",         adsPresent,                   adsBr),
            ("ADS raw",         adsRaw,                       null),
        };
        return BuildCard("WAS (Wheel Angle Sensor)", rows);
    }

    private Control BuildGpsCard(CoreXEcuGps? g)
    {
        var rows = new List<(string K, string V, IBrush? Br)>
        {
            ("Velocidad", FmtNum(g?.SpeedKmh, 1) + " km/h", null),
            ("Knots",     FmtNum(g?.SpeedKnots, 2),         null),
            ("Heading",   FmtDeg(g?.HeadingDeg),            null),
            ("GGA visto", g == null ? "--" : YesNo(g.GgaSeen), g == null ? null : (g.GgaSeen ? _brushOk : _brushWarn)),
        };
        return BuildCard("GPS", rows);
    }

    private Control BuildCanCard(CoreXEcuCan? c)
    {
        var rows = new List<(string K, string V, IBrush? Br)>
        {
            ("Steer enable", c == null ? "--" : YesNo(c.KeyaSteerEnabled), c == null ? null : (c.KeyaSteerEnabled ? _brushOk : _brushDim)),
            ("Corriente",    FmtNum(c?.KeyaCurrentA, 2) + " A", null),
        };
        return BuildCard("CAN Keya", rows);
    }

    private Control BuildAutosteerCard(CoreXEcuAutosteer? a)
    {
        int wd = a?.Watchdog ?? 0;
        string wdStr = a == null ? "--" : (wd.ToString(CultureInfo.InvariantCulture) + (wd >= 100 ? " (caído)" : ""));
        IBrush? wdBr = a == null ? null : (wd < 100 ? _brushOk : _brushErr);

        var rows = new List<(string K, string V, IBrush? Br)>
        {
            ("Loop",      a == null ? "--" : (a.Running ? "Corriendo" : "Detenido"),
                          a == null ? null : (a.Running ? _brushOk : _brushErr)),
            ("Guidance",  a == null ? "--" : (a.GuidanceActive ? "Activa" : "Inactiva"),
                          a == null ? null : (a.GuidanceActive ? _brushOk : _brushDim)),
            ("Watchdog",  wdStr, wdBr),
            ("PWM",       FmtInt(a?.Pwm), null),
            ("Setpoint",  FmtDeg(a?.SetpointDeg), null),
        };
        return BuildCard("Autosteer", rows);
    }

    private Control BuildSistemaCard(CoreXEcuStatus? s)
    {
        var rows = new List<(string K, string V, IBrush? Br)>
        {
            ("IP del Teensy", s?.Ip ?? "--", null),
            ("Ethernet",      s == null ? "--" : (s.Ethernet ? "Link up" : "Link down"),
                              s == null ? null : (s.Ethernet ? _brushOk : _brushErr)),
            ("Firmware",      string.IsNullOrEmpty(s?.Firmware) ? "--"
                              : (s!.Firmware + (string.IsNullOrEmpty(s.Version) ? "" : (" " + s.Version))), null),
            ("Uptime",        FmtSeconds(s?.UptimeSec), null),
        };
        return BuildCard("Sistema", rows);
    }

    // ---------- generic card builder ---------------------------------------

    private static Control BuildCard(string title, List<(string K, string V, IBrush? Br)> rows)
    {
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = _textHi,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Margin = new global::Avalonia.Thickness(0, 0, 0, 4),
        });
        foreach (var row in rows)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var k = new TextBlock
            {
                Text = row.K,
                Foreground = _textDim,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var v = new TextBlock
            {
                Text = row.V,
                Foreground = row.Br ?? _textHi,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(k, 0); Grid.SetColumn(v, 1);
            g.Children.Add(k); g.Children.Add(v);
            sp.Children.Add(g);
        }

        return new Border
        {
            Background      = _bgMid,
            BorderBrush     = _border,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(10),
            Padding         = new global::Avalonia.Thickness(16, 12, 16, 14),
            Margin          = new global::Avalonia.Thickness(0, 0, 12, 12),
            Child           = sp,
        };
    }

    // ---------- formatters --------------------------------------------------

    private static string FmtNum(double? v, int decimals)
    {
        if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "--";
        return v.Value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static string FmtDeg(double? v)
    {
        if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "-- °";
        return v.Value.ToString("F1", CultureInfo.InvariantCulture) + " °";
    }

    private static string FmtInt(long? v)
    {
        if (v == null) return "--";
        return v.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FmtInt(int? v)
    {
        if (v == null) return "--";
        return v.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string YesNo(bool b) => b ? "Sí" : "No";

    private static string FmtSeconds(long? sec)
    {
        if (sec == null || sec.Value < 0) return "--";
        long s = sec.Value;
        long h = s / 3600;
        long m = (s % 3600) / 60;
        long ss = s % 60;
        if (h > 0) return h.ToString(CultureInfo.InvariantCulture) + "h " + m.ToString("00", CultureInfo.InvariantCulture) + "m";
        if (m > 0) return m.ToString(CultureInfo.InvariantCulture) + "m " + ss.ToString("00", CultureInfo.InvariantCulture) + "s";
        return ss.ToString(CultureInfo.InvariantCulture) + "s";
    }

    private static string PrettySource(string? src)
    {
        if (string.IsNullOrEmpty(src)) return "--";
        var s = src.ToLowerInvariant();
        switch (s)
        {
            case "keya":     return "Keya (CAN)";
            case "ads_se":   return "ADS1115 single-ended";
            case "ads_diff": return "ADS1115 diferencial";
            case "bno_was":  return "BNO RVC (yaw como WAS)";
            default:         return src!;
        }
    }

    private void OnConfigurarClick(object? sender, RoutedEventArgs e)
    {
        OnRequestConfigurar?.Invoke();
    }
}
