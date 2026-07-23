// HubPanel.axaml.cs
//
// Reemplazo nativo de pages/hub.html. KPIs (velocidad/rumbo/dosis/secciones/
// posicion/lote) llegan via OnSnapshot(HudSnapshot) — no se hace polling
// propio del estado AOG. La lista de nodos se refresca a 3s contra
// /api/nodos/unified (reusa NodosClient). Los toggles QX/VX/FX hablan a
// /api/overlays (FormGPS los relee desde overlayPrefs.json cada 250ms).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

namespace PilotX.Desktop.Views;

public partial class HubPanel : UserControl
{
    // ---- Pills ----
    private Ellipse?   _dotJob;     private TextBlock? _txtJob;
    private Ellipse?   _dotBroker;  private TextBlock? _txtBroker;
    private Ellipse?   _dotNodos;   private TextBlock? _txtNodos;
    // ---- KPI cards ----
    private TextBlock? _kpiSpeed; private TextBlock? _kpiHeading;
    private TextBlock? _kpiDose;  private Ellipse?   _dotShape;  private TextBlock? _txtShape;
    private StackPanel? _sectionsRow;
    private TextBlock? _kpiSecCount; private TextBlock? _kpiToolWidth;
    private TextBlock? _kpiLatLon;   private TextBlock? _kpiField;
    // ---- Overlays toggles ----
    private Button?    _btnOvQx;   private Button? _btnOvVx;   private Button? _btnOvFx;
    // ---- Nodos ----
    private StackPanel? _nodosHost;

    private NodosClient? _nodosClient;
    private OverlaysClient? _overlaysClient;
    private CancellationTokenSource? _nodosPollCts;

    private OverlayPrefs _overlayPrefs = new();

    // Brushes cacheados (mismo patron que los otros paneles).
    private static readonly SolidColorBrush BrushOk      = new(Color.Parse("#4ABA3E"));
    private static readonly SolidColorBrush BrushWarn    = new(Color.Parse("#E2B53E"));
    private static readonly SolidColorBrush BrushErr     = new(Color.Parse("#E15A5A"));
    private static readonly SolidColorBrush BrushDim     = new(Color.Parse("#8FA092"));
    private static readonly SolidColorBrush BrushTextHi  = new(Color.Parse("#E2E7E2"));
    private static readonly SolidColorBrush BrushTextMid = new(Color.Parse("#C5CFC5"));
    private static readonly SolidColorBrush BrushBgHigh  = new(Color.Parse("#262C28"));
    private static readonly SolidColorBrush BrushBgMid   = new(Color.Parse("#1A1F1B"));
    private static readonly SolidColorBrush BrushBorder  = new(Color.Parse("#2A332C"));
    private static readonly SolidColorBrush BrushBorderHigh = new(Color.Parse("#535E54"));
    private static readonly SolidColorBrush BrushWhite   = new(Colors.White);

    /// <summary>
    /// Callbacks de los botones "Acciones rapidas" — los wirea MainWindow
    /// para que abran los overlays nativos (QuantiX, VistaX, Nodos via
    /// WebView lazy ya que esa pantalla todavia no esta portada).
    /// </summary>
    public Action? OnRequestQuantix { get; set; }
    public Action? OnRequestVistax  { get; set; }
    public Action? OnRequestNodos   { get; set; }

    public HubPanel()
    {
        InitializeComponent();

        _dotJob       = this.FindControl<Ellipse>("DotJob");
        _txtJob       = this.FindControl<TextBlock>("TxtJob");
        _dotBroker    = this.FindControl<Ellipse>("DotBroker");
        _txtBroker    = this.FindControl<TextBlock>("TxtBroker");
        _dotNodos     = this.FindControl<Ellipse>("DotNodos");
        _txtNodos     = this.FindControl<TextBlock>("TxtNodos");
        _kpiSpeed     = this.FindControl<TextBlock>("KpiSpeed");
        _kpiHeading   = this.FindControl<TextBlock>("KpiHeading");
        _kpiDose      = this.FindControl<TextBlock>("KpiDose");
        _dotShape     = this.FindControl<Ellipse>("DotShape");
        _txtShape     = this.FindControl<TextBlock>("TxtShape");
        _sectionsRow  = this.FindControl<StackPanel>("SectionsRow");
        _kpiSecCount  = this.FindControl<TextBlock>("KpiSecCount");
        _kpiToolWidth = this.FindControl<TextBlock>("KpiToolWidth");
        _kpiLatLon    = this.FindControl<TextBlock>("KpiLatLon");
        _kpiField     = this.FindControl<TextBlock>("KpiField");
        _btnOvQx      = this.FindControl<Button>("BtnOvQX");
        _btnOvVx      = this.FindControl<Button>("BtnOvVX");
        _btnOvFx      = this.FindControl<Button>("BtnOvFX");
        _nodosHost    = this.FindControl<StackPanel>("NodosHost");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Attach(NodosClient nodosClient, OverlaysClient overlaysClient)
    {
        _nodosClient = nodosClient;
        _overlaysClient = overlaysClient;

        // Carga inicial de prefs de overlays. Si falla, quedan los defaults.
        _ = LoadOverlayPrefsAsync();

        _nodosPollCts?.Cancel();
        _nodosPollCts = new CancellationTokenSource();
        _ = NodosPollLoopAsync(_nodosPollCts.Token);
    }

    public void Detach()
    {
        _nodosPollCts?.Cancel();
        _nodosPollCts = null;
    }

    /// <summary>
    /// Push del HudSnapshot — MainWindow lo invoca a 4Hz mientras el
    /// overlay esta visible. Mantiene KPIs vivos sin red propia.
    /// </summary>
    public void OnSnapshot(HudSnapshot s)
    {
        if (_kpiSpeed != null)
            _kpiSpeed.Text = s.AvgSpeed.ToString("0.0", CultureInfo.InvariantCulture);

        if (_kpiHeading != null)
        {
            double deg = (s.Heading * 180.0 / Math.PI) % 360.0;
            if (deg < 0) deg += 360.0;
            _kpiHeading.Text = Math.Round(deg).ToString("0", CultureInfo.InvariantCulture) + "°";
        }

        if (_kpiDose != null)
        {
            _kpiDose.Text = s.ShapeCurrentDose > 0
                ? s.ShapeCurrentDose.ToString("0", CultureInfo.InvariantCulture)
                : "—";
        }
        UpdateShapePill(s.ShapeCurrentDose, s.ShapeIsInside);

        if (_kpiToolWidth != null)
        {
            _kpiToolWidth.Text = s.ToolWidth > 0
                ? s.ToolWidth.ToString("0.00", CultureInfo.InvariantCulture) + " m"
                : "— m";
        }

        RenderSections(s.NumSections, s.SectionOnRequest);

        if (_kpiLatLon != null)
            _kpiLatLon.Text = FmtLatLon(s.Latitude, s.Longitude);

        if (_kpiField != null)
            _kpiField.Text = string.IsNullOrEmpty(s.CurrentFieldDirectory) ? "—" : s.CurrentFieldDirectory;

        SetPill(_dotJob, _txtJob,
            s.IsJobStarted ? BrushOk : BrushDim,
            s.IsJobStarted ? "Trabajo activo" : "Sin trabajo",
            s.IsJobStarted ? BrushTextHi : BrushTextMid);
    }

    private static string FmtLatLon(double lat, double lon)
    {
        if ((lat == 0 && lon == 0) || double.IsNaN(lat) || double.IsNaN(lon)) return "—";
        return lat.ToString("0.00000", CultureInfo.InvariantCulture) + "°, "
             + lon.ToString("0.00000", CultureInfo.InvariantCulture) + "°";
    }

    private void UpdateShapePill(double dose, bool inside)
    {
        if (_dotShape == null || _txtShape == null) return;
        if (dose > 0 && inside) { _dotShape.Fill = BrushOk;   _txtShape.Text = "dentro de zona"; _txtShape.Foreground = BrushTextHi; }
        else if (dose > 0)      { _dotShape.Fill = BrushWarn; _txtShape.Text = "fuera de zona"; _txtShape.Foreground = BrushTextHi; }
        else                    { _dotShape.Fill = BrushDim;  _txtShape.Text = "sin shape";     _txtShape.Foreground = BrushTextMid; }
    }

    private void RenderSections(int num, bool[]? on)
    {
        if (_sectionsRow == null) return;
        int n = Math.Max(0, Math.Min(16, num));

        // Rebuild si cambio la cantidad.
        if (_sectionsRow.Children.Count != n)
        {
            _sectionsRow.Children.Clear();
            for (int i = 0; i < n; i++)
            {
                var cell = new Border
                {
                    MinWidth = 24,
                    Height = 32,
                    CornerRadius = new CornerRadius(4),
                    Background = BrushBgHigh,
                    BorderBrush = BrushBorder,
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                // En StackPanel horizontal, hago que cada celda crezca igual:
                // truco simple = ancho minimo y dejar que el spacing distribuya.
                // (Sin Grid star, las celdas mantienen ancho proporcional al contenido.)
                _sectionsRow.Children.Add(cell);
            }
        }

        int openCount = 0;
        for (int i = 0; i < n; i++)
        {
            bool active = on != null && i < on.Length && on[i];
            if (_sectionsRow.Children[i] is Border b)
            {
                b.Background  = active ? BrushOk    : BrushBgHigh;
                b.BorderBrush = active ? BrushOk    : BrushBorder;
            }
            if (active) openCount++;
        }

        if (_kpiSecCount != null)
            _kpiSecCount.Text = openCount + " de " + n + (n == 1 ? " abierta" : " abiertas");
    }

    private static void SetPill(Ellipse? dot, TextBlock? txt, IBrush dotFill, string text, IBrush txtFg)
    {
        if (dot != null) dot.Fill = dotFill;
        if (txt != null) { txt.Text = text; txt.Foreground = txtFg; }
    }

    // ===================== Nodos =====================
    private async Task NodosPollLoopAsync(CancellationToken ct)
    {
        if (_nodosClient == null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var data = await _nodosClient.GetUnifiedAsync(ct).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => ApplyNodos(data));
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyNodos(NodosUnifiedResponse? data)
    {
        // Pill broker
        bool brokerOk = data != null && data.Ok && data.BrokerConnected;
        SetPill(_dotBroker, _txtBroker,
            brokerOk ? BrushOk : BrushErr,
            brokerOk ? "Broker MQTT" : "Broker MQTT offline",
            brokerOk ? BrushTextHi : BrushTextMid);

        var nodos = data?.Nodos ?? new List<NodoUnified>();
        int online = nodos.Count(n => n.Online);

        // Pill nodos
        SetPill(_dotNodos, _txtNodos,
            online > 0 ? BrushOk : BrushDim,
            online + (online == 1 ? " nodo" : " nodos"),
            online > 0 ? BrushTextHi : BrushTextMid);

        // Lista (max 6)
        if (_nodosHost == null) return;
        _nodosHost.Children.Clear();

        if (nodos.Count == 0)
        {
            _nodosHost.Children.Add(new Border
            {
                Background = BrushBgMid,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Child = new TextBlock
                {
                    Text = "Sin nodos detectados todavia",
                    Foreground = BrushDim,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            });
            return;
        }

        foreach (var n in nodos.Take(6))
            _nodosHost.Children.Add(BuildNodoRow(n));
    }

    private static Border BuildNodoRow(NodoUnified n)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        var led = new Ellipse
        {
            Width = 10, Height = 10,
            Fill = n.Online ? BrushOk : BrushDim,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(led, 0);
        grid.Children.Add(led);

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(n.Tipo) ? "?" : n.Tipo,
            Foreground = BrushTextHi,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold
        });
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(n.Alias) ? (n.Uid ?? "") : (n.Alias + "  ·  " + (n.Uid ?? "")),
            Foreground = BrushDim,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        // Estado pill
        var pill = new Border
        {
            Background = BrushBgHigh,
            BorderBrush = n.Online ? BrushOk : BrushBorderHigh,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = n.Online ? "online" : "offline",
                Foreground = n.Online ? BrushOk : BrushTextMid,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            }
        };
        Grid.SetColumn(pill, 3);
        grid.Children.Add(pill);

        return new Border
        {
            Background = BrushBgMid,
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Child = grid
        };
    }

    // ===================== Overlays toggles =====================
    private async Task LoadOverlayPrefsAsync()
    {
        if (_overlaysClient == null) return;
        var prefs = await _overlaysClient.GetAsync().ConfigureAwait(false);
        if (prefs == null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _overlayPrefs = prefs;
            PaintOverlayButtons();
        });
    }

    private void PaintOverlayButtons()
    {
        PaintOverlayButton(_btnOvQx, _overlayPrefs.QxOverlay);
        PaintOverlayButton(_btnOvVx, _overlayPrefs.VxOverlay);
        PaintOverlayButton(_btnOvFx, _overlayPrefs.FxOverlay);
    }

    private static void PaintOverlayButton(Button? btn, bool on)
    {
        if (btn == null) return;
        btn.Opacity = on ? 1.0 : 0.55;
        btn.Background = on ? BrushOk : BrushBgHigh;
        btn.BorderBrush = on ? BrushOk : BrushBorderHigh;
        btn.Foreground = on ? BrushWhite : BrushTextMid;
    }

    private void OnToggleQx(object? s, RoutedEventArgs e) { _overlayPrefs.QxOverlay = !_overlayPrefs.QxOverlay; PaintOverlayButtons(); _ = SaveOverlaysAsync(); }
    private void OnToggleVx(object? s, RoutedEventArgs e) { _overlayPrefs.VxOverlay = !_overlayPrefs.VxOverlay; PaintOverlayButtons(); _ = SaveOverlaysAsync(); }
    private void OnToggleFx(object? s, RoutedEventArgs e) { _overlayPrefs.FxOverlay = !_overlayPrefs.FxOverlay; PaintOverlayButtons(); _ = SaveOverlaysAsync(); }

    private async Task SaveOverlaysAsync()
    {
        if (_overlaysClient == null) return;
        await _overlaysClient.SaveAsync(_overlayPrefs).ConfigureAwait(false);
    }

    // ===================== Acciones rapidas =====================
    private void OnGoQuantix(object? sender, RoutedEventArgs e) => OnRequestQuantix?.Invoke();
    private void OnGoVistax (object? sender, RoutedEventArgs e) => OnRequestVistax?.Invoke();
    private void OnGoNodos  (object? sender, RoutedEventArgs e) => OnRequestNodos?.Invoke();
}
