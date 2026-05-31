using System;
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
using WebViewCore.Events;

namespace PilotX.Desktop;

public partial class MainWindow : Window
{
    private WebView _webView;
    private Border _headerBar;
    private Border _hudBar;
    private Border _bottomToolbar;
    private TextBlock _headerTitle;
    private TextBlock _headerSubtitle;
    private Border _rootBorder;

    // HUD
    private TextBlock _hudSpeed;
    private TextBlock _hudHeading;
    private TextBlock _hudArea;
    private TextBlock _hudStatusText;
    private Ellipse   _hudStatusDot;
    private Border    _hudStatusChip;
    private HudPoller? _hudPoller;
    // Para que un host caido no pinte "Reconectando..." en cada tick (parpadeo).
    private bool _hudWasConnected;

    // Toolbar inferior (state-aware). Engranaje OFF sin GPS, FieldTools OFF
    // sin lote, Tools SIEMPRE ON.
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

        _webView         = this.FindControl<WebView>("WebViewHost");
        _headerBar       = this.FindControl<Border>("HeaderBar");
        _hudBar          = this.FindControl<Border>("HudBar");
        _bottomToolbar   = this.FindControl<Border>("BottomToolbar");
        _headerTitle     = this.FindControl<TextBlock>("HeaderTitle");
        _headerSubtitle  = this.FindControl<TextBlock>("HeaderSubtitle");
        _rootBorder      = this.FindControl<Border>("RootBorder");

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
        // Arrancan deshabilitados; el primer snapshot del HUD los habilita
        // segun el estado (GPS fix / job activo). Tools queda siempre on.
        if (_btnSettings   != null) _btnSettings.IsEnabled   = false;
        if (_btnFieldTools != null) _btnFieldTools.IsEnabled = false;

        if (App.WindowMode == "float")
        {
            // Modo widget: borderless con chrome propio + arrastrabilidad
            // por el header. Encima de PilotX, deja ver el mapa detras.
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

            // Posicion inicial: esquina superior-derecha (deja ver el piloto).
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
            // Modo full (default) - Hub principal de cabina.
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.Maximized;
            if (_rootBorder != null)
            {
                _rootBorder.CornerRadius = new global::Avalonia.CornerRadius(0);
                _rootBorder.BorderThickness = new global::Avalonia.Thickness(0);
            }
            if (_headerBar     != null) _headerBar.IsVisible = false;
            // HUD cockpit solo en modo full (los widgets float no lo muestran).
            if (_hudBar        != null) _hudBar.IsVisible = true;
            // Toolbar inferior solo en modo full (los widgets float no la necesitan).
            if (_bottomToolbar != null) _bottomToolbar.IsVisible = true;
            // FAB X para cerrar (no hay header en modo full).
            var closeBtn = this.FindControl<Button>("CloseButton");
            if (closeBtn != null) closeBtn.IsVisible = true;
            // Mini-mapa visible por defecto en modo full. El pin queda oculto
            // hasta que el operario lo cierre con la X chiquita.
            if (_miniMapWrap != null) _miniMapWrap.IsVisible = true;
            if (_miniMapShow != null) _miniMapShow.IsVisible = false;
        }

        // Arranca el poller del HUD apuntando al host del WebView (mismo origen).
        // Solo en modo full: en widgets float el HUD no se muestra y el poller
        // seria gasto de red gratis.
        if (App.WindowMode != "float")
        {
            _hudPoller = new HudPoller(baseUrl: DeriveOrigin(App.TargetUrl), intervalMs: 250);
            _hudPoller.SnapshotReceived += OnHudSnapshot;
            _hudPoller.PollFailed       += OnHudPollFailed;
            _hudPoller.Start();
            Closed += (_, _) => _hudPoller?.Dispose();
        }

        if (_webView != null)
        {
            _webView.NavigationCompleted += OnNavigationCompleted;
            try { _webView.Url = new Uri(App.TargetUrl); } catch { }
        }

        // Esc cierra; F12 abre DevTools del WebView2 subyacente.
        KeyDown += OnKeyDown;
    }

    private void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e)
    {
        if (App.ColdStart != null && App.ColdStart.IsRunning)
        {
            App.ColdStart.Stop();
            var ms = App.ColdStart.ElapsedMilliseconds;
            // Stdout para captura via `dotnet run > coldstart.log`. Util para
            // comparar contra WinForms+WebView2 (objetivo <500ms al primer
            // render, baseline Fase 1).
            Console.WriteLine("[PilotX.Desktop] Cold-start (Main -> NavigationCompleted): " + ms + " ms  url=" + App.TargetUrl);
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Cold-start: " + ms + " ms");
            if (_headerSubtitle != null && App.WindowMode == "float")
            {
                var current = _headerSubtitle.Text ?? string.Empty;
                _headerSubtitle.Text = current + (string.IsNullOrEmpty(current) ? "" : "  -  ") + ms + " ms";
            }
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key == Key.F12 && _webView != null)
        {
            try { _webView.OpenDevToolsWindow(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] DevTools error: " + ex.Message); }
        }
    }

    // BeginMoveDrag delega al window-manager del SO el movimiento mientras el
    // mouse este presionado. Lo enganchamos al header entero (sin marcas
    // exclusivas) para que el operario pueda agarrarlo de cualquier punto.
    // Doble-click sobre el header maximiza/restaura (gesto estandar).
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

    // ---------- HUD --------------------------------------------------------

    private void OnHudSnapshot(HudSnapshot s)
    {
        // Polling corre en un task del threadpool; el update de UI necesita
        // marshalling al dispatcher de Avalonia.
        Dispatcher.UIThread.Post(() =>
        {
            if (_hudSpeed   != null) _hudSpeed.Text   = s.AvgSpeed.ToString("0.0");
            // Heading viene en rad. Lo paso a grados normalizados 0..360.
            double deg = (s.Heading * 180.0 / Math.PI) % 360.0;
            if (deg < 0) deg += 360.0;
            if (_hudHeading != null) _hudHeading.Text = deg.ToString("0");
            // Area neta cubierta (sin solapamiento) en hectareas.
            if (_hudArea != null)
            {
                double ha = s.ActualAreaCoveredM2 / 10000.0;
                _hudArea.Text = ha >= 100 ? ha.ToString("0") : ha.ToString("0.0");
            }

            bool hasGpsFix = s.Latitude != 0 || s.Longitude != 0;
            UpdateStatusChip(connected: true, jobActive: s.IsJobStarted, hasGpsFix: hasGpsFix);
            // Estado toolbar inferior coherente con project_pilotx_toolbar_icons:
            //   Engranaje  -> requiere GPS fix (ajustes de vehiculo/implemento
            //                 sin posicion no tienen valor operativo)
            //   FieldTools -> requiere job iniciado (sin lote no hay datos)
            //   Tools      -> queda siempre habilitado (no se toca aca)
            if (_btnSettings   != null) _btnSettings.IsEnabled   = hasGpsFix;
            if (_btnFieldTools != null) _btnFieldTools.IsEnabled = s.IsJobStarted;

            // Empuja el snapshot al mini-mapa (redibuja si esta visible).
            // OnSnapshot ya cachea el bbox del boundary internamente.
            _miniMap?.OnSnapshot(s);

            _hudWasConnected = true;
        });
    }

    private void OnHudPollFailed(Exception _)
    {
        // Solo repinto si veniamos de "conectado": evita re-aplicar el mismo
        // estado en cada tick fallido (4 Hz seria un flicker innecesario).
        if (!_hudWasConnected) return;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatusChip(connected: false, jobActive: false, hasGpsFix: false);
            // Host caido = sin info confiable. Desactivo Settings/FieldTools
            // para evitar que el operario navegue a paginas que no van a
            // responder. Tools sigue activo (productos X-* / Hub funcionan
            // contra otros endpoints, no contra /api/aog/state).
            if (_btnSettings   != null) _btnSettings.IsEnabled   = false;
            if (_btnFieldTools != null) _btnFieldTools.IsEnabled = false;
            _hudWasConnected = false;
        });
    }

    // Pinta el chip de estado segun la combinacion conexion/job/fix:
    //   sin host         -> gris,    texto "Sin conexion"
    //   host pero no job -> ambar,   texto "Sin trabajo"
    //   job + fix        -> verde,   texto "Trabajo activo"
    //   job sin fix      -> rojo,    texto "Sin GPS fix"
    private void UpdateStatusChip(bool connected, bool jobActive, bool hasGpsFix)
    {
        if (_hudStatusDot == null || _hudStatusText == null || _hudStatusChip == null) return;

        // Pinto los dots con literales hex para no depender del lookup del
        // ResourceDictionary en runtime. Los valores son los mismos que en
        // Theme/PilotXTheme.axaml — si la paleta cambia, actualizar ambos.
        IBrush dotBrush;
        string label;
        if (!connected)                  { dotBrush = _brushDim;  label = "Sin conexion"; }
        else if (jobActive && hasGpsFix) { dotBrush = _brushOk;   label = "Trabajo activo"; }
        else if (jobActive)              { dotBrush = _brushErr;  label = "Sin GPS fix"; }
        else                             { dotBrush = _brushWarn; label = "Sin trabajo"; }

        _hudStatusDot.Fill = dotBrush;
        _hudStatusText.Text = label;
    }

    // Brushes de estado del HUD — espejados de Theme/PilotXTheme.axaml.
    private static readonly IBrush _brushOk   = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushErr  = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushDim  = new SolidColorBrush(Color.Parse("#8FA092"));

    // Saca el origin (scheme + host + port) de cualquier URL para apuntar el
    // HudPoller al host correcto. Si App.TargetUrl es http://x:5180/pages/foo.html,
    // el poller tiene que pegar a http://x:5180/api/aog/state.
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
    // Los 3 botones (Engranaje/FieldTools/Tools) ahora navegan el WebView
    // a paginas concretas del wwwroot. Tools usa un MenuFlyout en el XAML
    // que dispara los handlers OnNav* de abajo. Engranaje y FieldTools
    // navegan directo. Quedan deshabilitados via OnHudSnapshot segun el
    // estado del PilotX (sin GPS / sin job).

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        // "sistema.html" es el shell de ajustes del Hub (atajos a vehiculo/
        // implemento/sistema). Si en el futuro se decide enganchar la UI
        // nativa Avalonia de Settings, se cambia aca por un cambio de View.
        NavigateTo("pages/sistema.html");
    }

    private void OnFieldToolsClick(object? sender, RoutedEventArgs e)
    {
        // btnFieldStats del FormGPS abre datos-lote.html en el Hub
        // (project_aog_popups_html). Mantengo el mismo destino para que la
        // experiencia entre shells WinForms y Avalonia sea identica.
        NavigateTo("pages/datos-lote.html");
    }

    // Flyout Tools - productos X-* + utilidades. Cada handler navega a
    // su pagina; no abrimos ventanas nuevas (eso vendria con widgets float
    // si en el futuro se quieren overlays encima del mapa).
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

    // Construye URL absoluta a partir del origen del WebView actual + path
    // relativo. Asi si el usuario arranco con --url=http://otra-pc:5180/
    // los botones siguen apuntando al mismo host (no a 127.0.0.1 hardcoded).
    // ---------- Mini-mapa show/hide ---------------------------------------
    //
    // El mini-mapa arranca visible. Si el operario quiere todo el ancho del
    // WebView para una pagina del Hub, lo oculta con la X chiquita y el pin
    // "[M]" aparece en el mismo lugar para reabrirlo despues. No persistimos
    // el estado entre sesiones por ahora — siempre arranca visible.

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

    private void NavigateTo(string relativePath)
    {
        if (_webView == null) return;
        try
        {
            var origin = DeriveOrigin(App.TargetUrl);
            var full   = origin + (relativePath ?? string.Empty).TrimStart('/');
            _webView.Url = new Uri(full);
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] navigate -> " + full);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] navigate error: " + ex.Message);
        }
    }

    // El subtitulo del header es informativo: muestra que pagina esta
    // cargada. Por ejemplo /pages/camaras.html -> "Camaras". Si no podemos
    // inferirlo, queda vacio y el header solo muestra "PilotX".
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
