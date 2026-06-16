// VistaXPanel.axaml.cs
//
// Reemplazo nativo (Monitor live-only) de pages/vistax.html. Consume el
// /api/vistax/live a 2Hz mientras esta visible. Replica el flujo de
// renderMonitor() del JS:
//   - KPI strip: SPM promedio, objetivo (max entre trenes), surcos activos,
//     fallas, implemento, chip alarma.
//   - Badges por estado: OK / bajo / tapado / exceso / silenciado / sin datos.
//   - Por cada tren: header con nombre + objetivo + dos sub-secciones:
//       * "tubitos" (semilla + fertilizante) — sensores chicos con SPM grande
//         y color por estado (ok / tapado / exceso / muted / bajo con gradiente
//         por ratioObjetivo).
//       * "barras" (turbina, tolva_*, bajada_herramienta, rotacion_eje, etc.)
//         — fila horizontal con nombre/tipo + valor + barra de fill por ratio.
//   - Grilla de nodos VistaX vistos via MQTT (online/offline + sensores
//     reportando + edad).
//
// El editor (Insumo & calibracion, Implemento, Nodos config, Config global)
// sigue en HTML — el boton Configurar dispara OnRequestConfigurar.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class VistaXPanel : UserControl
{
    private VistaXClient? _client;
    private CancellationTokenSource? _cts;
    private VistaXLiveSnapshot? _live;

    public Action? OnRequestConfigurar { get; set; }

    // ---------- palette (sincronizada con vistax.css) ----------
    private static readonly IBrush _brushOk      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn    = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushExceso  = new SolidColorBrush(Color.Parse("#6E2E0E"));
    private static readonly IBrush _brushErr     = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushTapado  = new SolidColorBrush(Color.Parse("#1F0606")); // casi negro
    private static readonly IBrush _brushMuted   = new SolidColorBrush(Color.Parse("#3E4A41"));
    private static readonly IBrush _brushNoData  = new SolidColorBrush(Color.Parse("#222926"));
    private static readonly IBrush _brushDim     = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush _textHi       = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush _textMid      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush _textDim      = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush _bgMid        = new SolidColorBrush(Color.Parse("#1A1F1B"));
    private static readonly IBrush _bgHigh       = new SolidColorBrush(Color.Parse("#262C28"));
    private static readonly IBrush _border       = new SolidColorBrush(Color.Parse("#2A332C"));
    private static readonly IBrush _gaugeBg      = new SolidColorBrush(Color.Parse("#2A332C"));

    public VistaXPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Arranca el polling 2Hz al /api/vistax/live.</summary>
    public void Attach(VistaXClient client)
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
            var snap = await _client.GetLiveAsync(ct).ConfigureAwait(false);
            _live = snap;
        }
        await Dispatcher.UIThread.InvokeAsync(Render);
    }

    private void Render()
    {
        var subtitle    = this.FindControl<TextBlock>("SubtitleText");
        var kpiSpm      = this.FindControl<TextBlock>("KpiSpm");
        var kpiObj      = this.FindControl<TextBlock>("KpiObj");
        var kpiActivos  = this.FindControl<TextBlock>("KpiActivos");
        var kpiFallas   = this.FindControl<TextBlock>("KpiFallas");
        var kpiImp      = this.FindControl<TextBlock>("KpiImp");
        var alarmDot    = this.FindControl<Ellipse>("AlarmDot");
        var alarmText   = this.FindControl<TextBlock>("AlarmText");
        var badgesHost  = this.FindControl<WrapPanel>("BadgesHost");
        var trenesHost  = this.FindControl<StackPanel>("TrenesHost");
        var emptyHint   = this.FindControl<TextBlock>("EmptyHint");
        var nodosHost   = this.FindControl<WrapPanel>("NodosHost");
        var nodosEmpty  = this.FindControl<TextBlock>("NodosEmpty");

        var live = _live;
        var trenes = live?.Trenes ?? new List<VistaXTrenLive>();
        // Cuando el back dice "no estamos sensando" (sembradora levantada,
        // velocidad <1 km/h o menos de UmbralSensoresActivos surcos con SPM>0)
        // forzamos todos los sensores a gris. Respeta el contrato de SeedMonitor.
        bool monActivo = live != null && live.MonitoreoActivo;

        // Subtitle (impl + tolerancia)
        string impName = string.IsNullOrEmpty(live?.NombreImplemento) ? "--" : live!.NombreImplemento!;
        string tolStr  = live?.ToleranciaDesvio is double t && t > 0
            ? " · tol ±" + t.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "";
        if (subtitle != null) subtitle.Text = "Monitoreo de siembra · " + impName + tolStr;

        // KPI strip
        if (kpiSpm != null)
            kpiSpm.Text = live?.SpmPromedio is double sp ? sp.ToString("0.0", CultureInfo.InvariantCulture) : "--";
        double objMax = 0;
        foreach (var tr in trenes) if (tr.Objetivo > objMax) objMax = tr.Objetivo;
        if (kpiObj != null)
            kpiObj.Text = objMax > 0 ? objMax.ToString("0", CultureInfo.InvariantCulture) + " sem/min" : "--";
        if (kpiActivos != null) kpiActivos.Text = (live?.SurcosActivos ?? 0).ToString(CultureInfo.InvariantCulture);
        if (kpiFallas  != null) kpiFallas.Text  = (live?.FallasActivas ?? 0).ToString(CultureInfo.InvariantCulture);
        if (kpiImp     != null) kpiImp.Text     = impName;

        // Alarm chip
        if (alarmDot != null && alarmText != null)
        {
            if (live == null)
            {
                alarmDot.Fill = _brushDim;
                alarmText.Foreground = _textMid;
                alarmText.Text = "sin conexión";
            }
            else if (live.HasAlarm)
            {
                alarmDot.Fill = _brushErr;
                alarmText.Foreground = _brushErr;
                alarmText.Text = string.IsNullOrEmpty(live.AlarmMessage) ? "alarma" : live.AlarmMessage!;
            }
            else if (!live.MonitoreoActivo)
            {
                alarmDot.Fill = _brushWarn;
                alarmText.Foreground = _brushWarn;
                alarmText.Text = "monitor detenido";
            }
            else
            {
                alarmDot.Fill = _brushOk;
                alarmText.Foreground = _brushOk;
                alarmText.Text = "sin alarma";
            }
        }

        // Badges: conteo por estado
        if (badgesHost != null)
        {
            badgesHost.Children.Clear();
            int okC = 0, badC = 0, tapC = 0, excC = 0, muC = 0, ndC = 0;
            foreach (var tr in trenes)
            {
                if (tr.Surcos == null) continue;
                foreach (var s in tr.Surcos)
                {
                    var st = NormalizeEstado(s.Estado);
                    switch (st)
                    {
                        case "ok":     okC++;  break;
                        case "bajo":   badC++; break;
                        case "tapado": tapC++; break;
                        case "exceso": excC++; break;
                        case "muted":  muC++;  break;
                        default:       ndC++;  break;
                    }
                }
            }
            if (okC  > 0) badgesHost.Children.Add(BuildBadge(okC  + " OK",          _brushOk));
            if (badC > 0) badgesHost.Children.Add(BuildBadge(badC + " bajo",        _brushErr));
            if (tapC > 0) badgesHost.Children.Add(BuildBadge(tapC + " tapado",      _brushTapado));
            if (excC > 0) badgesHost.Children.Add(BuildBadge(excC + " exceso",      _brushExceso));
            if (muC  > 0) badgesHost.Children.Add(BuildBadge(muC  + " silenciado",  _brushMuted));
            if (ndC  > 0) badgesHost.Children.Add(BuildBadge(ndC  + " sin datos",   _brushNoData));
            if (badgesHost.Children.Count == 0)
                badgesHost.Children.Add(BuildBadge("sin sensores", _brushDim));
        }

        // Trenes
        if (trenesHost != null)
        {
            trenesHost.Children.Clear();
            if (trenes.Count == 0)
            {
                if (emptyHint != null) emptyHint.IsVisible = true;
            }
            else
            {
                if (emptyHint != null) emptyHint.IsVisible = false;
                foreach (var tr in trenes)
                    trenesHost.Children.Add(BuildTrenSection(tr, monActivo));
            }
        }

        // Nodos
        if (nodosHost != null)
        {
            nodosHost.Children.Clear();
            var nodos = live?.Nodos ?? new List<VistaXNodoLive>();
            if (nodos.Count == 0)
            {
                if (nodosEmpty != null) nodosEmpty.IsVisible = true;
            }
            else
            {
                if (nodosEmpty != null) nodosEmpty.IsVisible = false;
                foreach (var n in nodos)
                    nodosHost.Children.Add(BuildNodoCard(n));
            }
        }
    }

    // ---------- builders ----------------------------------------------------

    private static Control BuildBadge(string text, IBrush dotBrush)
    {
        var b = new Border
        {
            Background      = _bgHigh,
            BorderBrush     = _border,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(999),
            Padding         = new global::Avalonia.Thickness(10, 4, 12, 4),
            Margin          = new global::Avalonia.Thickness(0, 0, 8, 8),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dotBrush, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, Foreground = _textMid, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        b.Child = sp;
        return b;
    }

    private Control BuildTrenSection(VistaXTrenLive tr, bool monActivo)
    {
        var outer = new StackPanel { Spacing = 8 };

        // Header tren
        var headSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        headSp.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(tr.Nombre) ? ("Tren " + tr.Tren.ToString(CultureInfo.InvariantCulture)) : tr.Nombre!,
            Foreground = _textHi,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (tr.Objetivo > 0)
            headSp.Children.Add(new TextBlock
            {
                Text = "· objetivo " + tr.Objetivo.ToString("0", CultureInfo.InvariantCulture) + " sem/min",
                Foreground = _textDim,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        outer.Children.Add(headSp);

        // Split tubitos (semilla/ferti) vs barras
        var tubitos = new List<VistaXSurcoLive>();
        var barras  = new List<VistaXSurcoLive>();
        var surcos = tr.Surcos ?? new List<VistaXSurcoLive>();
        foreach (var s in surcos)
        {
            var tipo = (s.Tipo ?? "semilla").ToLowerInvariant();
            if (tipo == "semilla" || tipo == "fertilizante" || tipo.StartsWith("ferti"))
                tubitos.Add(s);
            else
                barras.Add(s);
        }

        if (tubitos.Count > 0)
        {
            tubitos.Sort((a, b) => a.Bajada.CompareTo(b.Bajada));
            outer.Children.Add(new TextBlock
            {
                Text = "Semilla / Fertilizante · " + tubitos.Count.ToString(CultureInfo.InvariantCulture),
                Foreground = _textDim,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Margin = new global::Avalonia.Thickness(0, 4, 0, 0),
            });
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var s in tubitos)
            {
                var sensorClosure = s;
                var cell = BuildSensorCell(sensorClosure, tr.Objetivo, monActivo);
                cell.Cursor = new Cursor(StandardCursorType.Hand);
                // Tapped es el evento "click/touch" de alto nivel de Avalonia:
                // se dispara tras Pointer{Press,Release} en el mismo control
                // y NO es interferido por hijos con Background (el "tube" lo era).
                cell.Tapped += (_, e) => { e.Handled = true; ShowSensorDetail(sensorClosure, tr.Objetivo, tr, monActivo); };
                wrap.Children.Add(cell);
            }
            outer.Children.Add(wrap);
        }

        if (barras.Count > 0)
        {
            barras.Sort((a, b) =>
            {
                int c = string.Compare(a.Tipo ?? "", b.Tipo ?? "", StringComparison.Ordinal);
                return c != 0 ? c : a.Bajada.CompareTo(b.Bajada);
            });
            outer.Children.Add(new TextBlock
            {
                Text = "Otros sensores · " + barras.Count.ToString(CultureInfo.InvariantCulture),
                Foreground = _textDim,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Margin = new global::Avalonia.Thickness(0, 4, 0, 0),
            });
            var stack = new StackPanel { Spacing = 6 };
            foreach (var s in barras)
            {
                var sensorClosure = s;
                var bar = BuildSensorBar(sensorClosure, tr.Objetivo, monActivo);
                bar.Cursor = new Cursor(StandardCursorType.Hand);
                bar.Tapped += (_, e) => { e.Handled = true; ShowSensorDetail(sensorClosure, tr.Objetivo, tr, monActivo); };
                stack.Children.Add(bar);
            }
            outer.Children.Add(stack);
        }

        return new Border
        {
            Background      = _bgMid,
            BorderBrush     = _border,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(10),
            Padding         = new global::Avalonia.Thickness(16, 12, 16, 14),
            Child           = outer,
        };
    }

    private static Control BuildSensorCell(VistaXSurcoLive s, double objTren, bool monActivo)
    {
        var st = NormalizeEstado(s.Estado);
        double sp = s.Spm;
        double obj = s.Objetivo > 0 ? s.Objetivo : objTren;

        IBrush tubeBrush;
        string label;
        IBrush labelBrush;

        if (!monActivo)
        {
            // Sembradora no esta sembrando segun SeedMonitor (sin velocidad o
            // sin minimo de sensores activos). Todo gris, sin alarma por sensor.
            tubeBrush  = _brushMuted;
            label      = "OFF";
            labelBrush = _textDim;
        }
        else
        {
            switch (st)
            {
                case "ok":      tubeBrush = _brushOk;     break;
                case "tapado":  tubeBrush = _brushTapado; break;
                case "exceso":  tubeBrush = _brushExceso; break;
                case "muted":   tubeBrush = _brushMuted;  break;
                case "no-data": tubeBrush = _brushNoData; break;
                case "bajo":
                {
                    // gradiente manual negro->ok segun ratioObjetivo (replica del JS)
                    double r = Math.Max(0.0, Math.Min(1.0, s.RatioObjetivo));
                    int g  = (int)Math.Round(0x4B * r);
                    int rr = (int)Math.Round(0x05 + (0x40 - 0x05) * r);
                    int bb = (int)Math.Round(0x05 + (0x18 - 0x05) * r);
                    tubeBrush = new SolidColorBrush(Color.FromRgb((byte)rr, (byte)(g + 0x0A), (byte)bb));
                    break;
                }
                default: tubeBrush = _brushNoData; break;
            }

            switch (st)
            {
                case "no-data": label = "sin señal";     labelBrush = _textDim;  break;
                case "tapado":  label = "TAPADO";        labelBrush = _brushErr; break;
                case "bajo":    label = "bajo objetivo"; labelBrush = _brushErr; break;
                case "exceso":  label = "exceso";        labelBrush = _brushWarn; break;
                case "muted":   label = "silenciado";    labelBrush = _textDim;  break;
                case "ok":      label = "sem/min";       labelBrush = _brushOk;  break;
                default:        label = "sem/min";       labelBrush = _textDim;  break;
            }
        }

        string spmTxt = sp >= 100 ? sp.ToString("0", CultureInfo.InvariantCulture)
                                  : sp.ToString("0.0", CultureInfo.InvariantCulture);
        string objTxt = obj > 0 ? obj.ToString("0", CultureInfo.InvariantCulture) : "--";

        var sp1 = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };

        // "Tube" (rectángulo con el color del estado)
        var tube = new Border
        {
            Background = tubeBrush,
            Width = 56,
            Height = 28,
            CornerRadius = new global::Avalonia.CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // Cuando monActivo=false todo el monitor esta apagado: omitimos el
        // tag MUTE individual para no confundir (la franja gris ya transmite
        // "off" y el detalle del sensor sigue exponiendo Muted=true al tocarlo).
        if (s.Muted && monActivo)
        {
            // overlay "MUTE" leyenda chiquita
            var muteTag = new TextBlock
            {
                Text = "MUTE",
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var g = new Grid();
            g.Children.Add(tube);
            g.Children.Add(muteTag);
            sp1.Children.Add(g);
        }
        else
        {
            sp1.Children.Add(tube);
        }

        sp1.Children.Add(new TextBlock
        {
            Text = "B" + s.Bajada.ToString(CultureInfo.InvariantCulture),
            Foreground = _textDim,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp1.Children.Add(new TextBlock
        {
            Text = spmTxt,
            Foreground = _textHi,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp1.Children.Add(new TextBlock
        {
            Text = "real · obj " + objTxt,
            Foreground = _textDim,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp1.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = labelBrush,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        return new Border
        {
            Background      = _bgHigh,
            BorderBrush     = _border,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(8),
            Padding         = new global::Avalonia.Thickness(8, 8, 8, 8),
            Margin          = new global::Avalonia.Thickness(0, 0, 6, 6),
            Width           = 78,
            Child           = sp1,
        };
    }

    private static Control BuildSensorBar(VistaXSurcoLive s, double objTren, bool monActivo)
    {
        var st = NormalizeEstado(s.Estado);
        double sp = s.Spm;
        double obj = s.Objetivo > 0 ? s.Objetivo : 0;
        double ratio = obj > 0 ? Math.Max(0.0, Math.Min(1.3, sp / obj)) : 0.0;
        if (st == "no-data") ratio = 0;
        if (st == "tapado")  ratio = Math.Max(ratio, 0.06);

        string nombreTipo = PrettifyTipo(s.Tipo);
        string estadoTxt;
        IBrush estadoBrush;
        if (!monActivo)
        {
            // Mismo principio que las celdas: en off forzamos gris.
            estadoTxt   = "OFF";
            estadoBrush = _textDim;
            ratio       = 0;
        }
        else
        {
            switch (st)
            {
                case "no-data": estadoTxt = "sin señal";  estadoBrush = _textDim;  break;
                case "tapado":  estadoTxt = "TAPADO";     estadoBrush = _brushErr; break;
                case "bajo":    estadoTxt = "BAJO";       estadoBrush = _brushErr; break;
                case "exceso":  estadoTxt = "EXCESO";     estadoBrush = _brushWarn; break;
                case "muted":   estadoTxt = "silenciado"; estadoBrush = _textDim;  break;
                case "ok":      estadoTxt = "OK";         estadoBrush = _brushOk;  break;
                default:        estadoTxt = "--";         estadoBrush = _textDim;  break;
            }
        }

        string valTxt = sp >= 100 ? sp.ToString("0", CultureInfo.InvariantCulture)
                                  : sp.ToString("0.0", CultureInfo.InvariantCulture);
        string idTag = (s.Bajada != 0)
            ? " B" + s.Bajada.ToString(CultureInfo.InvariantCulture)
            : (s.Cable != 0 ? " cable " + s.Cable.ToString(CultureInfo.InvariantCulture) : "");

        var outer = new StackPanel { Spacing = 4 };

        var headG = new Grid();
        headG.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        headG.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var title = new TextBlock { Foreground = _textHi, FontSize = 12, FontWeight = FontWeight.SemiBold };
        title.Inlines = new global::Avalonia.Controls.Documents.InlineCollection
        {
            new global::Avalonia.Controls.Documents.Run { Text = nombreTipo },
            new global::Avalonia.Controls.Documents.Run { Text = idTag, Foreground = _textDim, FontWeight = FontWeight.Normal },
        };
        Grid.SetColumn(title, 0);
        headG.Children.Add(title);
        var val = new TextBlock
        {
            Text = valTxt,
            Foreground = estadoBrush,
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(val, 1);
        headG.Children.Add(val);
        outer.Children.Add(headG);

        outer.Children.Add(BuildBar(ratio, estadoBrush));

        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        foot.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var estadoTb = new TextBlock { Text = estadoTxt, Foreground = estadoBrush, FontSize = 10, FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(estadoTb, 0);
        foot.Children.Add(estadoTb);
        var objTb = new TextBlock
        {
            Text = "obj " + (obj > 0 ? obj.ToString("0", CultureInfo.InvariantCulture) : "--"),
            Foreground = _textDim,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(objTb, 1);
        foot.Children.Add(objTb);
        outer.Children.Add(foot);

        return new Border
        {
            Background      = _bgHigh,
            BorderBrush     = _border,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(8),
            Padding         = new global::Avalonia.Thickness(12, 8, 12, 8),
            Child           = outer,
        };
    }

    private static Control BuildBar(double ratio, IBrush fillBrush)
    {
        // Truco percent-width: 2 columnas Star
        double safe = Math.Max(0.0, Math.Min(1.0, ratio));
        var g = new Grid { Height = 10 };
        // bg track
        g.Children.Add(new Border
        {
            Background    = _gaugeBg,
            CornerRadius  = new global::Avalonia.CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(safe, GridUnitType.Star)));
        inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.0 - safe, GridUnitType.Star)));
        var fill = new Border
        {
            Background    = fillBrush,
            CornerRadius  = new global::Avalonia.CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(fill, 0);
        inner.Children.Add(fill);
        g.Children.Add(inner);
        return g;
    }

    private static Control BuildNodoCard(VistaXNodoLive n)
    {
        var sp = new StackPanel { Spacing = 4 };

        sp.Children.Add(new TextBlock
        {
            Text = n.Uid ?? "--",
            Foreground = _textHi,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        });

        var chipSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        chipSp.Children.Add(new Ellipse
        {
            Width = 8, Height = 8,
            Fill = n.Online ? _brushOk : _brushErr,
            VerticalAlignment = VerticalAlignment.Center,
        });
        chipSp.Children.Add(new TextBlock
        {
            Text = n.Online ? "online" : "offline",
            Foreground = n.Online ? _brushOk : _brushErr,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        chipSp.Children.Add(new TextBlock
        {
            Text = "· " + n.SensorsReporting.ToString(CultureInfo.InvariantCulture) + " sensores",
            Foreground = _textDim,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(chipSp);

        sp.Children.Add(new TextBlock
        {
            Text = "hace " + AgeStr(n.LastSeenIso),
            Foreground = _textDim,
            FontSize = 10,
        });

        return new Border
        {
            Background      = _bgMid,
            BorderBrush     = _border,
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius    = new global::Avalonia.CornerRadius(8),
            Padding         = new global::Avalonia.Thickness(12, 10, 12, 10),
            Margin          = new global::Avalonia.Thickness(0, 0, 8, 8),
            Width           = 180,
            Child           = sp,
        };
    }

    // ---------- helpers -----------------------------------------------------

    private static string NormalizeEstado(string? raw)
    {
        var st = (raw ?? "no-data").ToLowerInvariant();
        if (st == "bad")  return "bajo";
        if (st == "warn") return "exceso";
        return st;
    }

    private static string PrettifyTipo(string? tipo)
    {
        if (string.IsNullOrEmpty(tipo)) return "Sensor";
        var t = tipo!.ToLowerInvariant();
        switch (t)
        {
            case "bajada_herramienta": return "Bajada herr.";
            case "rotacion_eje":       return "Rotación eje";
            case "tolva_vacia":        return "Tolva vacía";
            case "tolva_llena":        return "Tolva llena";
            case "final_carrera":      return "Final carrera";
            case "turbina":            return "Turbina";
            default:
                return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }
    }

    private static string AgeStr(string? lastSeenIso)
    {
        if (string.IsNullOrEmpty(lastSeenIso)) return "--";
        if (!DateTime.TryParse(lastSeenIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return "--";
        var s = (DateTime.UtcNow - t).TotalSeconds;
        if (s < 0) s = 0;
        if (s < 60)    return s.ToString("0", CultureInfo.InvariantCulture) + "s";
        if (s < 3600)  return (s / 60.0).ToString("0", CultureInfo.InvariantCulture) + "m";
        return (s / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + "h";
    }

    private void OnConfigurarClick(object? sender, RoutedEventArgs e)
    {
        OnRequestConfigurar?.Invoke();
    }

    // ---------- detalle de un solo sensor ----------------------------------

    /// <summary>
    /// Abre el overlay con la informacion completa del sensor tocado.
    /// El cuerpo se reconstruye en cada apertura (no hay binding) — es info
    /// puntual de un evento de toque, no se actualiza mientras esta abierto.
    /// </summary>
    private void ShowSensorDetail(VistaXSurcoLive s, double objTren, VistaXTrenLive tr, bool monActivo)
    {
        var overlay  = this.FindControl<Border>("DetailOverlay");
        var title    = this.FindControl<TextBlock>("DetailTitle");
        var subtitle = this.FindControl<TextBlock>("DetailSubtitle");
        var body     = this.FindControl<StackPanel>("DetailBody");
        var footer   = this.FindControl<TextBlock>("DetailFooter");
        if (overlay == null || title == null || subtitle == null || body == null || footer == null) return;

        string trenNombre = string.IsNullOrEmpty(tr.Nombre)
            ? "Tren " + tr.Tren.ToString(CultureInfo.InvariantCulture)
            : tr.Nombre!;
        string bajadaStr  = s.Bajada != 0 ? "B" + s.Bajada.ToString(CultureInfo.InvariantCulture) : "—";
        string tipoStr    = PrettifyTipo(s.Tipo);

        title.Text    = tipoStr + " · " + bajadaStr;
        subtitle.Text = trenNombre + (s.Cable != 0 ? " · cable " + s.Cable.ToString(CultureInfo.InvariantCulture) : "");

        var stReal = NormalizeEstado(s.Estado);
        string estadoLabel;
        IBrush estadoBrush;
        if (!monActivo)
        {
            estadoLabel = "OFF (sembradora no está sembrando)";
            estadoBrush = _textDim;
        }
        else
        {
            switch (stReal)
            {
                case "ok":      estadoLabel = "OK";         estadoBrush = _brushOk;     break;
                case "tapado":  estadoLabel = "TAPADO";     estadoBrush = _brushErr;    break;
                case "bajo":    estadoLabel = "BAJO";       estadoBrush = _brushErr;    break;
                case "exceso":  estadoLabel = "EXCESO";     estadoBrush = _brushWarn;   break;
                case "muted":   estadoLabel = "silenciado"; estadoBrush = _textDim;     break;
                case "no-data": estadoLabel = "sin señal";  estadoBrush = _textDim;     break;
                default:        estadoLabel = stReal;       estadoBrush = _textDim;     break;
            }
        }

        double obj = s.Objetivo > 0 ? s.Objetivo : objTren;
        double ratio = obj > 0 ? Math.Max(0.0, Math.Min(1.3, s.Spm / obj)) : 0.0;

        body.Children.Clear();

        // Estado grande
        body.Children.Add(BuildDetailRow("Estado", estadoLabel, estadoBrush, big: true));

        // SPM real + Objetivo
        string spmTxt = s.Spm.ToString(s.Spm >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
        body.Children.Add(BuildDetailRow("Lectura actual",
            spmTxt + " sem/min",
            monActivo ? _textHi : _textDim,
            big: true));
        body.Children.Add(BuildDetailRow("Objetivo",
            (obj > 0 ? obj.ToString("0", CultureInfo.InvariantCulture) + " sem/min" : "—"),
            _textHi));
        // Barra ratio
        body.Children.Add(BuildBar(monActivo ? Math.Min(1.0, ratio) : 0, estadoBrush));

        // Metadata
        body.Children.Add(BuildDetailRow("Tipo",   tipoStr, _textMid));
        body.Children.Add(BuildDetailRow("Bajada", bajadaStr, _textMid));
        body.Children.Add(BuildDetailRow("Cable",  s.Cable != 0 ? s.Cable.ToString(CultureInfo.InvariantCulture) : "—", _textMid));
        body.Children.Add(BuildDetailRow("Nodo UID", string.IsNullOrEmpty(s.Uid) ? "—" : s.Uid!, _textMid));
        body.Children.Add(BuildDetailRow("Silenciado", s.Muted ? "sí" : "no",
            s.Muted ? _brushMuted : _textMid));

        footer.Text = monActivo
            ? "Datos en vivo · /api/vistax/live · 2 Hz. Tocá fuera o presioná Esc para cerrar."
            : "Monitor detenido: el back marca MonitoreoActivo=false (velocidad <1 km/h, sembradora levantada o menos del umbral de sensores). Al arrancar, los sensores se encienden solos.";

        overlay.IsVisible = true;
        // Si la ventana no tiene foco para Escape, igual el backdrop cubre todo.
        Focus();
    }

    private static Control BuildDetailRow(string label, string value, IBrush valueBrush, bool big = false)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var lab = new TextBlock
        {
            Text = label,
            Foreground = _textDim,
            FontSize = big ? 12 : 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var val = new TextBlock
        {
            Text = value,
            Foreground = valueBrush,
            FontSize = big ? 20 : 13,
            FontWeight = big ? FontWeight.Bold : FontWeight.SemiBold,
            FontFamily = big ? new FontFamily("Consolas, Courier New, monospace") : FontFamily.Default,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lab, 0);
        Grid.SetColumn(val, 1);
        g.Children.Add(lab);
        g.Children.Add(val);
        return g;
    }

    private void OnDetailBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        var overlay = this.FindControl<Border>("DetailOverlay");
        if (overlay != null) overlay.IsVisible = false;
    }

    private void OnDetailCardPressed(object? sender, PointerPressedEventArgs e)
    {
        // Absorbo el evento para que no burbujee al backdrop y cierre el overlay
        // cuando el operario toca dentro de la tarjeta.
        e.Handled = true;
    }

    private void OnDetailCloseClick(object? sender, RoutedEventArgs e)
    {
        var overlay = this.FindControl<Border>("DetailOverlay");
        if (overlay != null) overlay.IsVisible = false;
    }
}
