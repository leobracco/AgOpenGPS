// MainWindow.axaml.cs
//
// PIVOT (PilotX.Desktop): UI 100% nativa Avalonia. El WebView (Chromium/
// Edge ~ 400MB residente) NO esta en el hot path. Centro de pantalla =
// MapPanel nativo Skia. Cuando el operario abre una pantalla todavia no
// portada (Hub/productos X-*) se instancia UN WebView lazy, se monta en
// WebViewSlot, y se hace Dispose al cerrarlo.
//
// Reglas (directiva bajo consumo):
//   - Durante el guiado NO hay WebView instanciado. Cero costo Chromium.
//   - HudPoller (HTTP polling) corre en background; UI marshalling con
//     Dispatcher.UIThread.Post.
//   - Render del mapa pasa por MapPanel.OnSnapshot — preparado para
//     reemplazar el control por OpenGlControlBase sin cambiar la API.
//   - El modo float (widgets) tambien crea su WebView lazy en el slot.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaWebView;
using PilotX.Desktop.Controls;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views;
using WebViewCore.Events;

namespace PilotX.Desktop;

public partial class MainWindow : Window
{
    // Chrome
    private Border _headerBar;
    private Border _hudBar;
    private Border _bottomToolbar;
    private TextBlock _headerTitle;
    private TextBlock _headerSubtitle;
    private Border _rootBorder;

    // Mapa nativo (siempre presente, ocupa el centro de la pantalla)
    private MapPanel? _mapHost;

    // WebView lazy: se instancia on-demand y se dispone al cerrar la pantalla.
    private Panel?   _webViewSlot;
    private Button?  _webViewBack;
    private WebView? _webView;
    private bool     _coldStartLogged;

    // HUD
    private TextBlock _hudSpeed;
    private TextBlock _hudHeading;
    private TextBlock _hudArea;
    private TextBlock _hudStatusText;
    private Ellipse   _hudStatusDot;
    private Border    _hudStatusChip;
    private HudPoller? _hudPoller;
    private bool _hudWasConnected;

    // Toolbar inferior (state-aware).
    private Button? _btnSettings;
    private Button? _btnFieldTools;

    // Mini-mapa cockpit (overlay esquina inf. izq.) + pin para reabrirlo.
    private MiniMapView? _miniMap;
    private Border?      _miniMapWrap;
    private Button?      _miniMapShow;

    public MainWindow()
    {
        InitializeComponent();

        Title = App.WindowTitle;

        _headerBar       = this.FindControl<Border>("HeaderBar");
        _hudBar          = this.FindControl<Border>("HudBar");
        _bottomToolbar   = this.FindControl<Border>("BottomToolbar");
        _headerTitle     = this.FindControl<TextBlock>("HeaderTitle");
        _headerSubtitle  = this.FindControl<TextBlock>("HeaderSubtitle");
        _rootBorder      = this.FindControl<Border>("RootBorder");

        _mapHost         = this.FindControl<MapPanel>("MapHost");
        _webViewSlot     = this.FindControl<Panel>("WebViewSlot");
        _webViewBack     = this.FindControl<Button>("WebViewBack");

        _hudSpeed        = this.FindControl<TextBlock>("HudSpeed");
        _hudHeading      = this.FindControl<TextBlock>("HudHeading");
        _hudArea         = this.FindControl<TextBlock>("HudArea");
        _hudStatusText   = this.FindControl<TextBlock>("HudStatusText");
        _hudStatusDot    = this.FindControl<Ellipse>("HudStatusDot");
        _hudStatusChip   = this.FindControl<Border>("HudStatusChip");

        _btnSettings     = this.FindControl<Button>("BtnSettings");
        _btnFieldTools   = this.FindControl<Button>("BtnFieldTools");

        _miniMap         = this.FindControl<MiniMapView>("MiniMap");
        _miniMapWrap     = this.FindControl<Border>("MiniMapWrap");
        _miniMapShow     = this.FindControl<Button>("MiniMapShow");

        if (_btnSettings   != null) _btnSettings.IsEnabled   = false;
        if (_btnFieldTools != null) _btnFieldTools.IsEnabled = false;

        if (App.WindowMode == "float")
        {
            // Modo widget: borderless con chrome propio + arrastrabilidad por
            // el header. NO arranca el mapa ni el HUD — es un widget HTML
            // (camaras, monitores, etc.) y se abre con WebView lazy.
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.Normal;
            Width  = App.WindowWidth  > 0 ? App.WindowWidth  : 640;
            Height = App.WindowHeight > 0 ? App.WindowHeight : 400;
            Topmost = true;
            CanResize = true;
            ShowInTaskbar = true;

            if (_headerBar     != null) _headerBar.IsVisible = true;
            if (_hudBar        != null) _hudBar.IsVisible = false;
            if (_bottomToolbar != null) _bottomToolbar.IsVisible = false;
            if (_rootBorder    != null) _rootBorder.CornerRadius = new global::Avalonia.CornerRadius(10);
            if (_headerTitle   != null) _headerTitle.Text = App.WindowTitle ?? "PilotX";
            if (_headerSubtitle != null)
                _headerSubtitle.Text = DeriveSubtitleFromUrl(App.TargetUrl);

            // El widget float HTML necesita el WebView desde el arranque:
            // todo lo que se ve en la ventana es esa pagina. (Cuando esa
            // pagina sea portada a nativo, se elimina esta rama y la
            // ventana renderiza directo el control nativo.)
            if (_mapHost != null) _mapHost.IsVisible = false;
            ShowWebView(App.TargetUrl, showBackButton: false);

            // Posicion inicial: esquina superior-derecha.
            WindowStartupLocation = WindowStartupLocation.Manual;
            try
            {
                var screen = Screens.Primary;
                if (screen != null)
                {
                    var wa = screen.WorkingArea;
                    Position = new global::Avalonia.PixelPoint(
                        wa.X + wa.Width - (int)Width - 20,
                        wa.Y + 20);
                }
            }
            catch { }
        }
        else
        {
            // Modo full (default) - cockpit principal con MAPA NATIVO.
            // El WebView NO se crea hasta que el operario navegue a una
            // pantalla del Hub (Settings/FieldTools/Tools).
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.Maximized;
            if (_rootBorder != null)
            {
                _rootBorder.CornerRadius = new global::Avalonia.CornerRadius(0);
                _rootBorder.BorderThickness = new global::Avalonia.Thickness(0);
            }
            if (_headerBar     != null) _headerBar.IsVisible = false;
            if (_hudBar        != null) _hudBar.IsVisible = true;
            if (_bottomToolbar != null) _bottomToolbar.IsVisible = true;
            var closeBtn = this.FindControl<Button>("CloseButton");
            if (closeBtn != null) closeBtn.IsVisible = true;
            if (_miniMapWrap != null) _miniMapWrap.IsVisible = true;
            if (_miniMapShow != null) _miniMapShow.IsVisible = false;
        }

        if (App.WindowMode != "float")
        {
            _hudPoller = new HudPoller(baseUrl: DeriveOrigin(App.TargetUrl), intervalMs: 250);
            _hudPoller.SnapshotReceived += OnHudSnapshot;
            _hudPoller.PollFailed       += OnHudPollFailed;
            _hudPoller.Start();
            Closed += (_, _) => _hudPoller?.Dispose();
            // En modo full ya marcamos cold-start una vez que el primer
            // snapshot del HUD entra (no esperamos al WebView, que es lazy).
            _hudPoller.SnapshotReceived += LogColdStartOnce;
        }

        Closed += (_, _) => CloseWebView();
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc: si hay WebView abierto en modo full, lo cierro (vuelvo al
            // mapa). Si ya estaba en el mapa, cierro la ventana.
            if (App.WindowMode != "float" && _webView != null)
            {
                CloseWebView();
                return;
            }
            Close();
            return;
        }
        if (e.Key == Key.F12 && _webView != null)
        {
            try { _webView.OpenDevToolsWindow(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] DevTools error: " + ex.Message); }
        }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return;
            }
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ---------- WebView lazy lifecycle -----------------------------------
    //
    // ShowWebView instancia el WebView UNA vez (si no existe), lo agrega al
    // slot, navega a la URL. CloseWebView lo saca del slot y hace Dispose:
    // el GC libera Chromium en el proximo ciclo. Esto cumple la directiva
    // "el WebView no es residente durante el guiado".

    private void ShowWebView(string url, bool showBackButton)
    {
        if (_webViewSlot == null) return;
        try
        {
            if (_webView == null)
            {
                _webView = new WebView();
                _webView.NavigationCompleted += OnWebViewNavigated;
                _webViewSlot.Children.Add(_webView);
            }
            _webView.Url = new Uri(url);
            _webViewSlot.IsVisible = true;
            if (_mapHost != null) _mapHost.IsVisible = false;
            if (_webViewBack != null) _webViewBack.IsVisible = showBackButton;
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView open -> " + url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView open error: " + ex.Message);
        }
    }

    private void CloseWebView()
    {
        if (_webView == null && _webViewSlot == null) return;
        try
        {
            if (_webView != null)
            {
                _webView.NavigationCompleted -= OnWebViewNavigated;
                // Intento navegar a about:blank antes de soltarlo: libera el
                // contenido y reduce el working set del proceso WebView2 hijo
                // antes de que el GC lo finalize.
                try { _webView.Url = new Uri("about:blank"); } catch { }
                _webViewSlot?.Children.Remove(_webView);
                // Avalonia.WebView NO expone IDisposable; el control y su
                // proceso Chromium subyacente se liberan cuando el GC
                // finalize la referencia. Workstation GC + nullification
                // explicita habilita ese ciclo. Forzamos GC en el proximo
                // idle para no esperar al cycle natural.
                _webView = null;
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            }
            if (_webViewSlot != null) _webViewSlot.IsVisible = false;
            if (_webViewBack != null) _webViewBack.IsVisible = false;
            if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView disposed -> back to native");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView close error: " + ex.Message);
        }
    }

    private void OnWebViewBack(object? sender, RoutedEventArgs e) => CloseWebView();

    private void OnWebViewNavigated(object? sender, WebViewUrlLoadedEventArg e)
    {
        // Cold-start: si el primer paint del shell fue una pantalla web (modo
        // float), marca el cold-start aca. En modo full el cold-start lo
        // marca el primer snapshot del HUD (ver LogColdStartOnce).
        LogColdStart("WebView NavigationCompleted");
        if (_headerSubtitle != null && App.WindowMode == "float" && App.ColdStart != null)
        {
            var ms = App.ColdStart.ElapsedMilliseconds;
            var current = _headerSubtitle.Text ?? string.Empty;
            _headerSubtitle.Text = current + (string.IsNullOrEmpty(current) ? "" : "  -  ") + ms + " ms";
        }
    }

    private void LogColdStartOnce(HudSnapshot _) => LogColdStart("first HUD snapshot");

    private void LogColdStart(string trigger)
    {
        if (_coldStartLogged) return;
        if (App.ColdStart == null || !App.ColdStart.IsRunning) return;
        App.ColdStart.Stop();
        _coldStartLogged = true;
        var ms = App.ColdStart.ElapsedMilliseconds;
        Console.WriteLine("[PilotX.Desktop] Cold-start (Main -> " + trigger + "): " + ms + " ms");
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Cold-start: " + ms + " ms (" + trigger + ")");
    }

    // ---------- HUD --------------------------------------------------------

    private void OnHudSnapshot(HudSnapshot s)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_hudSpeed   != null) _hudSpeed.Text   = s.AvgSpeed.ToString("0.0", CultureInfo.InvariantCulture);
            double deg = (s.Heading * 180.0 / Math.PI) % 360.0;
            if (deg < 0) deg += 360.0;
            if (_hudHeading != null) _hudHeading.Text = deg.ToString("0", CultureInfo.InvariantCulture);
            if (_hudArea != null)
            {
                double ha = s.ActualAreaCoveredM2 / 10000.0;
                _hudArea.Text = ha >= 100
                    ? ha.ToString("0", CultureInfo.InvariantCulture)
                    : ha.ToString("0.0", CultureInfo.InvariantCulture);
            }

            bool hasGpsFix = s.Latitude != 0 || s.Longitude != 0;
            UpdateStatusChip(connected: true, jobActive: s.IsJobStarted, hasGpsFix: hasGpsFix);
            if (_btnSettings   != null) _btnSettings.IsEnabled   = hasGpsFix;
            if (_btnFieldTools != null) _btnFieldTools.IsEnabled = s.IsJobStarted;

            // Push al render nativo: mapa principal + mini-mapa (si visible).
            _mapHost?.OnSnapshot(s);
            _miniMap?.OnSnapshot(s);

            _hudWasConnected = true;
        });
    }

    private void OnHudPollFailed(Exception _)
    {
        if (!_hudWasConnected) return;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatusChip(connected: false, jobActive: false, hasGpsFix: false);
            if (_btnSettings   != null) _btnSettings.IsEnabled   = false;
            if (_btnFieldTools != null) _btnFieldTools.IsEnabled = false;
            _hudWasConnected = false;
        });
    }

    private void UpdateStatusChip(bool connected, bool jobActive, bool hasGpsFix)
    {
        if (_hudStatusDot == null || _hudStatusText == null || _hudStatusChip == null) return;
        IBrush dotBrush;
        string label;
        if (!connected)                  { dotBrush = _brushDim;  label = "Sin conexion"; }
        else if (jobActive && hasGpsFix) { dotBrush = _brushOk;   label = "Trabajo activo"; }
        else if (jobActive)              { dotBrush = _brushErr;  label = "Sin GPS fix"; }
        else                             { dotBrush = _brushWarn; label = "Sin trabajo"; }
        _hudStatusDot.Fill = dotBrush;
        _hudStatusText.Text = label;
    }

    private static readonly IBrush _brushOk   = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushErr  = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushDim  = new SolidColorBrush(Color.Parse("#8FA092"));

    private static string DeriveOrigin(string url)
    {
        if (string.IsNullOrEmpty(url)) return "http://127.0.0.1:5180/";
        try
        {
            var u = new Uri(url);
            return u.Scheme + "://" + u.Authority + "/";
        }
        catch { return "http://127.0.0.1:5180/"; }
    }

    // ---------- Toolbar inferior: navegacion ------------------------------
    //
    // Cada handler abre el WebView LAZY sobre el slot, ocultando el mapa.
    // Cuando el operario cierra (boton "<-" o Esc) se hace Dispose y vuelve
    // el mapa nativo. Mientras esta abierto un WebView en cabina, el HUD
    // y la toolbar siguen visibles arriba/abajo.

    private void OnSettingsClick(object? sender, RoutedEventArgs e)   => NavigateTo("pages/sistema.html");
    private void OnFieldToolsClick(object? sender, RoutedEventArgs e) => NavigateTo("pages/datos-lote.html");

    private void OnNavHub      (object? s, RoutedEventArgs e) => NavigateTo("");
    private void OnNavCamaras  (object? s, RoutedEventArgs e) => NavigateTo("pages/camaras.html");
    private void OnNavVistaX   (object? s, RoutedEventArgs e) => NavigateTo("pages/vistax.html");
    private void OnNavQuantiX  (object? s, RoutedEventArgs e) => NavigateTo("pages/quantix.html");
    private void OnNavSectionX (object? s, RoutedEventArgs e) => NavigateTo("pages/sectionx.html");
    private void OnNavFlowX    (object? s, RoutedEventArgs e) => NavigateTo("pages/flowx.html");
    private void OnNavStormX   (object? s, RoutedEventArgs e) => NavigateTo("pages/stormx.html");
    private void OnNavCoreX    (object? s, RoutedEventArgs e) => NavigateTo("pages/corex-ecu.html");
    private void OnNavNodos    (object? s, RoutedEventArgs e) => NavigateTo("pages/nodos.html");
    private void OnNavFirmwares(object? s, RoutedEventArgs e) => NavigateTo("pages/firmwares.html");
    private void OnNavOrbitX   (object? s, RoutedEventArgs e) => NavigateTo("pages/orbitx.html");
    private void OnNavDebug    (object? s, RoutedEventArgs e) => NavigateTo("pages/debug.html");

    private void NavigateTo(string relativePath)
    {
        var origin = DeriveOrigin(App.TargetUrl);
        var full   = origin + (relativePath ?? string.Empty).TrimStart('/');
        // Lazy: si no hay WebView, lo crea; si ya hay uno abierto, solo cambia
        // la URL. Show con back button = true: el operario ve la flecha "<-"
        // arriba a la izq. para volver al mapa.
        ShowWebView(full, showBackButton: true);
    }

    // ---------- Mini-mapa show/hide ---------------------------------------

    private void OnMiniMapHide(object? sender, RoutedEventArgs e)
    {
        if (_miniMapWrap != null) _miniMapWrap.IsVisible = false;
        if (_miniMapShow != null) _miniMapShow.IsVisible = true;
    }

    private void OnMiniMapShow(object? sender, RoutedEventArgs e)
    {
        if (_miniMapWrap != null) _miniMapWrap.IsVisible = true;
        if (_miniMapShow != null) _miniMapShow.IsVisible = false;
    }

    private static string DeriveSubtitleFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try
        {
            var u = new Uri(url);
            var seg = u.AbsolutePath.TrimEnd('/');
            int slash = seg.LastIndexOf('/');
            if (slash < 0) return string.Empty;
            string file = seg.Substring(slash + 1);
            if (string.IsNullOrEmpty(file)) return string.Empty;
            int dot = file.LastIndexOf('.');
            string name = dot > 0 ? file.Substring(0, dot) : file;
            switch (name.ToLowerInvariant())
            {
                case "camaras":        return "Camaras";
                case "camaras-widget": return "Camaras";
                case "flowx":          return "FlowX - Dosificacion";
                case "stormx":         return "StormX - Meteo";
                case "vistax":         return "VistaX - Siembra";
                case "quantix":        return "QuantiX - Motores";
                case "sectionx":       return "SectionX - Secciones";
                case "nodos":          return "Nodos";
                case "debug":          return "Debug";
                default:
                    return char.ToUpperInvariant(name[0]) + name.Substring(1);
            }
        }
        catch { return string.Empty; }
    }
}
