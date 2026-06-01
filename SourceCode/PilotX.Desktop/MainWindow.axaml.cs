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

    // Coverage poller (Stage 2 mapa GL): solo se instancia cuando UseGl
    // esta activo. Cadencia 1 Hz contra /api/aog/coverage; revision-based
    // caching dentro de MapGlSurface evita re-uploads cuando no cambio.
    private CoveragePoller? _coveragePoller;

    // Guidance geometry poller (Stage 3 mapa GL): polyline AB/Curve/Contour.
    // Cadencia 1 Hz contra /api/aog/guidance/geometry. Solo cuando UseGl=on
    // (la surface Skia legacy no pinta la linea de guidance — queda como
    // segunda pasada si vale la pena cuando GL llegue a paridad).
    private GuidanceGeometryPoller? _guidancePoller;

    // Tool geometry poller (Stage 4a mapa GL): barra del implemento +
    // estado por seccion. Cadencia 4 Hz contra /api/aog/tool/geometry
    // — los puntos cambian cada frame que el tractor se mueve. Solo
    // con UseGl=on.
    private ToolGeometryPoller? _toolPoller;
    // Tram geometry poller (Stage 4b mapa GL): wheel tracks + outer/inner.
    // Cadencia 1 Hz contra /api/aog/tram — solo cambia al regenerar.
    // Revision-cache filtra snapshots iguales. Solo con UseGl=on.
    private TramGeometryPoller? _tramPoller;

    // Toolbar inferior (state-aware).
    private Button? _btnSettings;
    private Button? _btnFieldTools;

    // Mini-mapa cockpit (overlay esquina inf. izq.) + pin para reabrirlo.
    private MiniMapView? _miniMap;
    private Border?      _miniMapWrap;
    private Button?      _miniMapShow;

    // Field data nativo (overlay que reemplaza datos-lote.html en el flujo
    // de FieldTools). Es un UserControl Avalonia, NO un WebView.
    private FieldDataPanel? _fieldDataHost;

    // Sistema nativo (brillo + power). Reemplaza pages/sistema.html en el
    // flujo de Settings. Tambien Avalonia puro.
    private SistemaPanel? _sistemaHost;
    private SistemaClient? _sistemaClient;

    // Datos GPS nativo (vel/heading/lat/lon/easting). Reemplaza pages/
    // datos-gps.html. Avalonia puro, sin red propia: consume el HUD.
    private GpsDataPanel? _gpsDataHost;

    // StormX nativo (estacion meteo movil). Reemplaza pages/stormx.html.
    // Tiene su propio polling 1Hz a /api/stormx/live mientras esta abierto.
    private StormXPanel? _stormXHost;
    private StormXClient? _stormXClient;

    // FlowX nativo (live-only). Reemplaza la parte cabin-critical de
    // pages/flowx.html. El editor de config sigue en HTML (lazy WebView).
    private FlowXPanel? _flowXHost;
    private FlowXClient? _flowXClient;

    // SectionX nativo (live-only). Chip de estado del bridge + grilla de
    // secciones (consume HudSnapshot). Editor de mapeo + test de reles +
    // debug MQTT siguen en HTML (lazy WebView via boton "Configurar").
    private SectionXPanel? _sectionXHost;
    private SectionXClient? _sectionXClient;

    // QuantiX nativo (Monitor tab live-only). Ver dosis real/target + PWM +
    // estado PID por motor. Tabs de Motores CRUD / Shape / PID-tune /
    // Calibracion / Prueba siguen en HTML (lazy WebView via "Configurar").
    private QuantiXPanel? _quantiXHost;
    private QuantiXClient? _quantiXClient;

    // VistaX nativo (Monitor tab live-only). SPM por surco, badges por estado,
    // trenes con tubitos (semilla/ferti) y barras (otros sensores). Tabs de
    // Insumo & calibracion / Implemento / Nodos / Config siguen en HTML
    // (lazy WebView via "Configurar").
    private VistaXPanel? _vistaXHost;
    private VistaXClient? _vistaXClient;

    // CoreX-ECU nativo (Live tab only). Telemetria del autosteer Teensy
    // (IMU, WAS, GPS, CAN Keya, Autosteer, Sistema). Las otras tabs
    // (Estado/checklist, Calibracion, Conexion con Teensy) siguen en HTML
    // (lazy WebView via "Configurar"). NO se toca firmware.
    private CoreXEcuPanel? _coreXEcuHost;
    private CoreXEcuClient? _coreXEcuClient;

    // Cabina-alarmas overlay (10mo port). Banner top-most que se autogestiona:
    // polling 2s a /api/nodos/unified, muestra/oculta segun haya nodos del
    // implemento activo offline. NO requiere navegacion del operario.
    private CabinaAlarmasOverlay? _cabinaAlarmasHost;
    private NodosClient? _nodosClient;

    // Hub home nativo (11vo port). Reemplazo de pages/hub.html — KPIs del
    // HudSnapshot, lista de nodos via NodosClient, toggles QX/VX/FX via
    // OverlaysClient. NO requiere WebView.
    private HubPanel? _hubHost;
    private OverlaysClient? _overlaysClient;

    // Nodos overlay (12vo port). Reemplazo nativo parcial de pages/nodos.html:
    // tabs + tabla + banner alarma. Las acciones de curado (aceptar/ignorar/
    // renombrar/restaurar) + diagnostico MQTT siguen en HTML via "Configurar".
    private NodosPanel? _nodosHost;

    // Actualizar overlay (13vo port). Reemplazo nativo de pages/actualizar.html:
    // self-update PilotX via OrbitX OTA. Polling 1s; acciones POST check/download/apply.
    private ActualizarPanel? _actualizarHost;
    private UpdateClient? _updateClient;

    // Camaras overlay (14vo port). Reemplazo nativo (parcial) de pages/camaras.html:
    // Tab Monitor con snapshot JPEG polling. Config (forms) sigue en HTML.
    private CamarasPanel? _camarasHost;
    private CamarasClient? _camarasClient;

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

        _fieldDataHost   = this.FindControl<FieldDataPanel>("FieldDataHost");
        _sistemaHost     = this.FindControl<SistemaPanel>("SistemaHost");
        _gpsDataHost     = this.FindControl<GpsDataPanel>("GpsDataHost");
        _stormXHost      = this.FindControl<StormXPanel>("StormXHost");
        _flowXHost       = this.FindControl<FlowXPanel>("FlowXHost");
        _sectionXHost    = this.FindControl<SectionXPanel>("SectionXHost");
        _quantiXHost     = this.FindControl<QuantiXPanel>("QuantiXHost");
        _vistaXHost      = this.FindControl<VistaXPanel>("VistaXHost");
        _coreXEcuHost    = this.FindControl<CoreXEcuPanel>("CoreXEcuHost");
        _cabinaAlarmasHost = this.FindControl<CabinaAlarmasOverlay>("CabinaAlarmasHost");
        _hubHost           = this.FindControl<HubPanel>("HubHost");
        _nodosHost         = this.FindControl<NodosPanel>("NodosHost");
        _actualizarHost    = this.FindControl<ActualizarPanel>("ActualizarHost");
        _camarasHost       = this.FindControl<CamarasPanel>("CamarasHost");

        if (_camarasHost != null)
        {
            // Boton "Configurar" del CamarasPanel: abre camaras.html en
            // WebView lazy para la tab Configuracion (formulario IP/usuario/
            // clave por camara — el live monitor ya esta nativo).
            _camarasHost.OnRequestConfigurar = () => NavigateTo("pages/camaras.html");
        }

        if (_nodosHost != null)
        {
            // El boton "Configurar" del NodosPanel abre nodos.html en WebView
            // lazy para acceder a las acciones de curado (aceptar/ignorar/
            // renombrar) + diagnostico MQTT (wildcard + msg log). En cabina
            // tactil el monitor con tabs alcanza; las acciones admin van en
            // HTML mientras no haya teclado virtual integrado.
            _nodosHost.OnRequestConfigurar = () => NavigateTo("pages/nodos.html");
        }

        if (_hubHost != null)
        {
            // Acciones rapidas del Hub: el callback abre el overlay nativo
            // correspondiente (QuantiX/VistaX) o el WebView lazy para
            // pantallas todavia no portadas (Nodos).
            _hubHost.OnRequestQuantix = () => ShowQuantiX();
            _hubHost.OnRequestVistax  = () => ShowVistaX();
            _hubHost.OnRequestNodos   = () => ShowNodos();
        }

        if (_flowXHost != null)
        {
            // Callback del boton "Configurar" del overlay FlowX: abre el
            // editor de config en WebView lazy (la edicion de productos/
            // cables/PID sigue en HTML por ahora — solo el live es nativo).
            _flowXHost.OnRequestConfigurar = () => NavigateTo("pages/flowx.html");
        }
        if (_sectionXHost != null)
        {
            // Mismo patron: el editor (mapeo surcos->secciones, test reles,
            // debug MQTT) sigue en HTML — abre WebView lazy on-demand.
            _sectionXHost.OnRequestConfigurar = () => NavigateTo("pages/sectionx.html");
        }
        if (_quantiXHost != null)
        {
            // Boton Configurar abre el resto de las tabs (Motores CRUD,
            // Shape, PID live-tune, Calibracion, Prueba) en WebView lazy.
            _quantiXHost.OnRequestConfigurar = () => NavigateTo("pages/quantix.html");
        }
        if (_vistaXHost != null)
        {
            // Boton Configurar abre las tabs editor (Insumo & calibracion,
            // Implemento, Nodos, Config) en WebView lazy.
            _vistaXHost.OnRequestConfigurar = () => NavigateTo("pages/vistax.html");
        }
        if (_coreXEcuHost != null)
        {
            // Boton Configurar abre las tabs editor (Estado/checklist,
            // Calibracion / motor manual + barrido PWM, Conexion con Teensy)
            // en WebView lazy. NO se toca firmware.
            _coreXEcuHost.OnRequestConfigurar = () => NavigateTo("pages/corex-ecu.html");
        }

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

            // Stage 2 mapa GL: poller dedicado para coverage (worked
            // area triangulado). Solo lo enchufamos si UseGl=on; el
            // render Skia legacy no pinta coverage (volumen de
            // triangulos no compite con DrawingContext).
            if (App.UseGl)
            {
                var cov = new CoverageClient(DeriveOrigin(App.TargetUrl));
                _coveragePoller = new CoveragePoller(cov, snap =>
                {
                    _mapHost?.OnCoverage(snap);
                }, periodMs: 1000);
                _coveragePoller.Start();
                Closed += (_, _) => _coveragePoller?.Stop();
            }

            // Stages 3/4: pollers de guidance/tool/tram. Corren tanto
            // con UseGl=on como con UseGl=off — MapSkiaSurface tambien
            // pinta estas capas (paridad parcial para que el toggle no
            // pierda referencia visual). Coverage se queda solo en GL.
            var gg = new GuidanceGeometryClient(DeriveOrigin(App.TargetUrl));
            _guidancePoller = new GuidanceGeometryPoller(gg, snap =>
            {
                _mapHost?.OnGuidance(snap);
            }, periodMs: 1000);
            _guidancePoller.Start();
            Closed += (_, _) => _guidancePoller?.Stop();

            // Tool / sections (Stage 4a). 4 Hz porque los puntos siguen
            // al tractor; sin revision-cache, cada poll va al render.
            var tg = new ToolGeometryClient(DeriveOrigin(App.TargetUrl));
            _toolPoller = new ToolGeometryPoller(tg, snap =>
            {
                _mapHost?.OnTool(snap);
            }, periodMs: 250);
            _toolPoller.Start();
            Closed += (_, _) => _toolPoller?.Stop();

            // Tram (Stage 4b). 1 Hz con revision-cache — solo cambia
            // al regenerar passes/ancho/displayMode del tram.
            var trc = new TramGeometryClient(DeriveOrigin(App.TargetUrl));
            _tramPoller = new TramGeometryPoller(trc, snap =>
            {
                _mapHost?.OnTram(snap);
            }, periodMs: 1000);
            _tramPoller.Start();
            Closed += (_, _) => _tramPoller?.Stop();
            // En modo full ya marcamos cold-start una vez que el primer
            // snapshot del HUD entra (no esperamos al WebView, que es lazy).
            _hudPoller.SnapshotReceived += LogColdStartOnce;

            // Cabina-alarmas: arranca con la app y se autogestiona. Solo en
            // modo full — en widgets float no hay banner cabin-critical.
            if (_cabinaAlarmasHost != null)
            {
                _nodosClient = new NodosClient(DeriveOrigin(App.TargetUrl));
                _cabinaAlarmasHost.Attach(_nodosClient);
                Closed += (_, _) => _cabinaAlarmasHost?.Detach();
            }
        }

        Closed += (_, _) => CloseWebView();
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc: cierra el overlay activo (FieldData o WebView) si hay
            // alguno. Si ya estaba en el mapa, cierra la ventana.
            if (App.WindowMode != "float")
            {
                if (_fieldDataHost != null && _fieldDataHost.IsVisible)
                {
                    CloseFieldData();
                    return;
                }
                if (_sistemaHost != null && _sistemaHost.IsVisible)
                {
                    CloseSistema();
                    return;
                }
                if (_gpsDataHost != null && _gpsDataHost.IsVisible)
                {
                    CloseGpsData();
                    return;
                }
                if (_stormXHost != null && _stormXHost.IsVisible)
                {
                    CloseStormX();
                    return;
                }
                if (_flowXHost != null && _flowXHost.IsVisible)
                {
                    CloseFlowX();
                    return;
                }
                if (_sectionXHost != null && _sectionXHost.IsVisible)
                {
                    CloseSectionX();
                    return;
                }
                if (_quantiXHost != null && _quantiXHost.IsVisible)
                {
                    CloseQuantiX();
                    return;
                }
                if (_vistaXHost != null && _vistaXHost.IsVisible)
                {
                    CloseVistaX();
                    return;
                }
                if (_coreXEcuHost != null && _coreXEcuHost.IsVisible)
                {
                    CloseCoreXEcu();
                    return;
                }
                if (_hubHost != null && _hubHost.IsVisible)
                {
                    CloseHub();
                    return;
                }
                if (_nodosHost != null && _nodosHost.IsVisible)
                {
                    CloseNodos();
                    return;
                }
                if (_actualizarHost != null && _actualizarHost.IsVisible)
                {
                    CloseActualizar();
                    return;
                }
                if (_camarasHost != null && _camarasHost.IsVisible)
                {
                    CloseCamaras();
                    return;
                }
                if (_webView != null)
                {
                    CloseWebView();
                    return;
                }
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
        // Si algun overlay nativo estaba abierto, lo cierro: solo un overlay
        // a la vez para no apilar costos de input/render.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible)
            _fieldDataHost.IsVisible = false;
        if (_sistemaHost != null && _sistemaHost.IsVisible)
        {
            _sistemaHost.Reset();
            _sistemaHost.IsVisible = false;
        }
        if (_gpsDataHost != null && _gpsDataHost.IsVisible)
            _gpsDataHost.IsVisible = false;
        if (_stormXHost != null && _stormXHost.IsVisible)
        {
            _stormXHost.Detach();
            _stormXHost.IsVisible = false;
        }
        if (_flowXHost != null && _flowXHost.IsVisible)
        {
            _flowXHost.Detach();
            _flowXHost.IsVisible = false;
        }
        if (_sectionXHost != null && _sectionXHost.IsVisible)
        {
            _sectionXHost.Detach();
            _sectionXHost.IsVisible = false;
        }
        if (_quantiXHost != null && _quantiXHost.IsVisible)
        {
            _quantiXHost.Detach();
            _quantiXHost.IsVisible = false;
        }
        if (_vistaXHost != null && _vistaXHost.IsVisible)
        {
            _vistaXHost.Detach();
            _vistaXHost.IsVisible = false;
        }
        if (_coreXEcuHost != null && _coreXEcuHost.IsVisible)
        {
            _coreXEcuHost.Detach();
            _coreXEcuHost.IsVisible = false;
        }
        if (_hubHost != null && _hubHost.IsVisible)
        {
            _hubHost.Detach();
            _hubHost.IsVisible = false;
        }
        if (_nodosHost != null && _nodosHost.IsVisible)
        {
            _nodosHost.Detach();
            _nodosHost.IsVisible = false;
        }
        if (_actualizarHost != null && _actualizarHost.IsVisible)
        {
            _actualizarHost.Detach();
            _actualizarHost.IsVisible = false;
        }
        if (_camarasHost != null && _camarasHost.IsVisible)
        {
            _camarasHost.Detach();
            _camarasHost.IsVisible = false;
        }
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

    /// <summary>
    /// Handler unificado del boton "&lt;-" (esquina sup. izq.). Cierra el
    /// overlay activo (FieldData o WebView) y vuelve al mapa nativo.
    /// </summary>
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) { CloseFieldData(); return; }
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { CloseSistema();   return; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   { CloseGpsData();   return; }
        if (_stormXHost    != null && _stormXHost.IsVisible)    { CloseStormX();    return; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { CloseFlowX();     return; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { CloseSectionX();  return; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { CloseQuantiX();   return; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { CloseVistaX();    return; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { CloseCoreXEcu();  return; }
        if (_hubHost       != null && _hubHost.IsVisible)       { CloseHub();       return; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { CloseNodos();     return; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ CloseActualizar();return; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { CloseCamaras();  return; }
        if (_webView != null) { CloseWebView(); return; }
    }

    // ---------- FieldData overlay nativo (sin WebView) -------------------

    private void ShowFieldData()
    {
        if (_fieldDataHost == null) return;
        // Si hay un WebView abierto, lo cierro: solo un overlay a la vez.
        if (_webView != null) CloseWebView();
        if (_sistemaHost != null && _sistemaHost.IsVisible) { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost != null && _gpsDataHost.IsVisible) _gpsDataHost.IsVisible = false;
        if (_stormXHost != null && _stormXHost.IsVisible) { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost != null && _flowXHost.IsVisible) { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost != null && _sectionXHost.IsVisible) { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost != null && _quantiXHost.IsVisible) { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost != null && _vistaXHost.IsVisible) { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost != null && _coreXEcuHost.IsVisible) { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        _fieldDataHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] FieldData open (nativo, no WebView)");
    }

    private void CloseFieldData()
    {
        if (_fieldDataHost == null) return;
        _fieldDataHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] FieldData closed -> back to native map");
    }

    // ---------- Sistema overlay nativo (sin WebView) ---------------------

    private void ShowSistema()
    {
        if (_sistemaHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_gpsDataHost != null && _gpsDataHost.IsVisible) _gpsDataHost.IsVisible = false;
        if (_stormXHost != null && _stormXHost.IsVisible) { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost != null && _flowXHost.IsVisible) { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost != null && _sectionXHost.IsVisible) { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost != null && _quantiXHost.IsVisible) { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost != null && _vistaXHost.IsVisible) { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost != null && _coreXEcuHost.IsVisible) { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Lazy init del cliente HTTP: solo se crea la primera vez que el
        // operario abre Sistema. Si nunca lo abre, cero costo de red extra.
        if (_sistemaClient == null)
            _sistemaClient = new SistemaClient(DeriveOrigin(App.TargetUrl));
        _sistemaHost.Attach(_sistemaClient);
        _sistemaHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Sistema open (nativo, no WebView)");
    }

    private void CloseSistema()
    {
        if (_sistemaHost == null) return;
        _sistemaHost.Reset();
        _sistemaHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Sistema closed -> back to native map");
    }

    // ---------- Datos GPS overlay nativo (sin WebView) -------------------

    private void ShowGpsData()
    {
        if (_gpsDataHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        _gpsDataHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] GpsData open (nativo, no WebView)");
    }

    private void CloseGpsData()
    {
        if (_gpsDataHost == null) return;
        _gpsDataHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] GpsData closed -> back to native map");
    }

    // ---------- StormX overlay nativo (sin WebView) ----------------------

    private void ShowStormX()
    {
        if (_stormXHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_stormXClient == null)
            _stormXClient = new StormXClient(DeriveOrigin(App.TargetUrl));
        _stormXHost.Attach(_stormXClient);
        _stormXHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] StormX open (nativo, no WebView)");
    }

    private void CloseStormX()
    {
        if (_stormXHost == null) return;
        // Detach apaga el polling: cero costo de red cuando esta cerrado.
        _stormXHost.Detach();
        _stormXHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] StormX closed -> back to native map");
    }

    // ---------- FlowX overlay nativo (live-only, sin WebView) ------------
    //
    // Strangler fig: el live (caudal/PWM/PID + KPIs combinados con HUD) va
    // nativo en cabina; el editor de productos/cables/PID sigue siendo
    // pages/flowx.html y se abre vía OnRequestConfigurar (WebView lazy).

    private void ShowFlowX()
    {
        if (_flowXHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_flowXClient == null)
            _flowXClient = new FlowXClient(DeriveOrigin(App.TargetUrl));
        _flowXHost.Attach(_flowXClient);
        _flowXHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] FlowX open (nativo live-only, no WebView)");
    }

    private void CloseFlowX()
    {
        if (_flowXHost == null) return;
        // Detach apaga el polling: cero costo de red cuando esta cerrado.
        _flowXHost.Detach();
        _flowXHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] FlowX closed -> back to native map");
    }

    // ---------- SectionX overlay nativo (live-only, sin WebView) ---------
    //
    // Strangler fig: chip de estado del bridge + grilla de secciones live
    // (consume HudSnapshot). El editor de mapeo + test de reles + debug
    // MQTT siguen en pages/sectionx.html (via OnRequestConfigurar).

    private void ShowSectionX()
    {
        if (_sectionXHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_sectionXClient == null)
            _sectionXClient = new SectionXClient(DeriveOrigin(App.TargetUrl));
        _sectionXHost.Attach(_sectionXClient);
        _sectionXHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] SectionX open (nativo live-only, no WebView)");
    }

    private void CloseSectionX()
    {
        if (_sectionXHost == null) return;
        _sectionXHost.Detach();
        _sectionXHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] SectionX closed -> back to native map");
    }

    // ---------- QuantiX overlay nativo (Monitor live-only, sin WebView) -----
    //
    // Strangler fig: la tab Monitor (ver dosis real/target + PWM + estado
    // PID por motor) es la unica cabin-critical. CRUD de motores, upload de
    // shape, PID live-tune, calibracion y prueba siguen en pages/quantix.html
    // (via OnRequestConfigurar -> WebView lazy).

    private void ShowQuantiX()
    {
        if (_quantiXHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_quantiXClient == null)
            _quantiXClient = new QuantiXClient(DeriveOrigin(App.TargetUrl));
        _quantiXHost.Attach(_quantiXClient);
        _quantiXHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] QuantiX open (nativo Monitor, no WebView)");
    }

    private void CloseQuantiX()
    {
        if (_quantiXHost == null) return;
        _quantiXHost.Detach();
        _quantiXHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] QuantiX closed -> back to native map");
    }

    // ---------- VistaX overlay nativo (Monitor live-only, sin WebView) ------
    //
    // Strangler fig: la tab Monitor (SPM por surco + badges + trenes con
    // tubitos semilla/ferti y barras de otros sensores) es la cabin-critical.
    // Insumo & calibracion, Implemento, Nodos, Config siguen en HTML
    // (pages/vistax.html via OnRequestConfigurar -> WebView lazy).

    private void ShowVistaX()
    {
        if (_vistaXHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_vistaXClient == null)
            _vistaXClient = new VistaXClient(DeriveOrigin(App.TargetUrl));
        _vistaXHost.Attach(_vistaXClient);
        _vistaXHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] VistaX open (nativo Monitor, no WebView)");
    }

    private void CloseVistaX()
    {
        if (_vistaXHost == null) return;
        _vistaXHost.Detach();
        _vistaXHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] VistaX closed -> back to native map");
    }

    // ----- CoreX-ECU (port #9): solo Live tab nativa (telemetria Teensy).
    // Estado / Calibracion / Conexion siguen en HTML detras de "Configurar"
    // (pages/corex-ecu.html via OnRequestConfigurar -> WebView lazy).

    private void ShowCoreXEcu()
    {
        if (_coreXEcuHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_coreXEcuClient == null)
            _coreXEcuClient = new CoreXEcuClient(DeriveOrigin(App.TargetUrl));
        _coreXEcuHost.Attach(_coreXEcuClient);
        _coreXEcuHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoreX-ECU open (nativo Live, no WebView)");
    }

    private void CloseCoreXEcu()
    {
        if (_coreXEcuHost == null) return;
        _coreXEcuHost.Detach();
        _coreXEcuHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoreX-ECU closed -> back to native map");
    }

    // ----- Hub home (port #11): reemplazo nativo de pages/hub.html.
    // KPIs vienen del HudSnapshot; lista de nodos via NodosClient @3s;
    // toggles QX/VX/FX via OverlaysClient. NO se usa WebView.

    private void ShowHub()
    {
        if (_hubHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Lazy init: clientes solo la primera vez. NodosClient se reusa con
        // el del overlay cabina-alarmas si ya esta inicializado.
        if (_nodosClient == null)
            _nodosClient = new NodosClient(DeriveOrigin(App.TargetUrl));
        if (_overlaysClient == null)
            _overlaysClient = new OverlaysClient(DeriveOrigin(App.TargetUrl));
        _hubHost.Attach(_nodosClient, _overlaysClient);
        _hubHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Hub open (nativo home, no WebView)");
    }

    private void CloseHub()
    {
        if (_hubHost == null) return;
        _hubHost.Detach();
        _hubHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Hub closed -> back to native map");
    }

    // ----- Nodos overlay (port #12): reemplazo nativo (parcial) de pages/nodos.html.
    // Live monitor: tabs + tabla + banner alarma offline-del-implemento. Las acciones
    // de curado (aceptar/ignorar/renombrar/restaurar) y el diag MQTT (wildcard +
    // msg log) siguen en HTML via WebView lazy.

    private void ShowNodos()
    {
        if (_nodosHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        // Reutiliza el NodosClient si ya esta inicializado (cabina-alarmas/Hub).
        if (_nodosClient == null)
            _nodosClient = new NodosClient(DeriveOrigin(App.TargetUrl));
        _nodosHost.Attach(_nodosClient);
        _nodosHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Nodos open (nativo, no WebView)");
    }

    private void CloseNodos()
    {
        if (_nodosHost == null) return;
        _nodosHost.Detach();
        _nodosHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Nodos closed -> back to native map");
    }

    // ----- Actualizar overlay (port #13): reemplazo nativo de pages/actualizar.html.
    // Self-update PilotX via OrbitX OTA. Polling 1s a /api/pilotx/update/status;
    // acciones POST check/download/apply. Cuando Aplicar dispara Updater.exe,
    // PilotX se cierra y vuelve a abrir con la nueva version.

    private void ShowActualizar()
    {
        if (_actualizarHost == null) return;
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        if (_updateClient == null)
            _updateClient = new UpdateClient(DeriveOrigin(App.TargetUrl));
        _actualizarHost.Attach(_updateClient);
        _actualizarHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Actualizar open (nativo, no WebView)");
    }

    private void CloseActualizar()
    {
        if (_actualizarHost == null) return;
        _actualizarHost.Detach();
        _actualizarHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Actualizar closed -> back to native map");
    }

    // ----- Camaras overlay (port #14): reemplazo PARCIAL de pages/camaras.html.
    // Tab Monitor nativo (snapshots JPEG @refrescoMs en grilla 1x1/2x1/2x2).
    // Tab Configuracion sigue en HTML porque el formulario (IP/usuario/clave)
    // necesita teclado virtual que aun no esta portado nativo - desde el panel
    // se abre con el boton "Configurar" (callback OnRequestConfigurar -> WebView).

    private void ShowCamaras()
    {
        if (_camarasHost == null) return;
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_webView != null) CloseWebView();
        if (_camarasClient == null)
            _camarasClient = new CamarasClient(DeriveOrigin(App.TargetUrl));
        _camarasHost.Attach(_camarasClient);
        _camarasHost.IsVisible = true;
        if (_mapHost != null) _mapHost.IsVisible = false;
        if (_webViewBack != null) _webViewBack.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Camaras open (nativo, no WebView)");
    }

    private void CloseCamaras()
    {
        if (_camarasHost == null) return;
        _camarasHost.Detach();
        _camarasHost.IsVisible = false;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Camaras closed -> back to native map");
    }

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
            // Push tambien al overlay nativo si esta abierto: refresca KPIs.
            if (_fieldDataHost != null && _fieldDataHost.IsVisible)
                _fieldDataHost.OnSnapshot(s);
            if (_gpsDataHost != null && _gpsDataHost.IsVisible)
                _gpsDataHost.OnSnapshot(s);
            // FlowX combina HUD (vel + secciones + area) con su propia
            // telemetria MQTT — solo cuando el overlay esta visible.
            if (_flowXHost != null && _flowXHost.IsVisible)
                _flowXHost.OnSnapshot(s);
            // SectionX combina el chip de status del bridge con la grilla
            // de secciones que viene en el HUD (NumSections + SectionOnRequest).
            if (_sectionXHost != null && _sectionXHost.IsVisible)
                _sectionXHost.OnSnapshot(s);
            // Hub home: KPIs (velocidad/rumbo/dosis/secciones/posicion/lote)
            // se alimentan del HUD sin red propia.
            if (_hubHost != null && _hubHost.IsVisible)
                _hubHost.OnSnapshot(s);

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

    // Settings ahora abre el overlay NATIVO (SistemaPanel) en lugar de
    // instanciar un WebView. Segundo port del flujo cockpit a nativo.
    private void OnSettingsClick(object? sender, RoutedEventArgs e)   => ShowSistema();
    // FieldTools ahora abre el overlay NATIVO (FieldDataPanel). Primer
    // port real del flujo de cabina: zero WebView. La pagina HTML
    // pages/datos-lote.html queda solo como referencia historica.
    private void OnFieldToolsClick(object? sender, RoutedEventArgs e) => ShowFieldData();

    // Hub home ahora es nativo (HubPanel). NO se instancia WebView. Los
    // KPIs vienen del HudSnapshot y los nodos via NodosClient @3s.
    private void OnNavHub      (object? s, RoutedEventArgs e) => ShowHub();
    // Datos GPS abre el overlay NATIVO (GpsDataPanel) en lugar de
    // instanciar un WebView para pages/datos-gps.html.
    private void OnNavDatosGps (object? s, RoutedEventArgs e) => ShowGpsData();
    private void OnNavCamaras  (object? s, RoutedEventArgs e) => ShowCamaras();
    // VistaX abre el overlay NATIVO (VistaXPanel, Monitor live-only). Las
    // otras tabs (Insumo & calibracion, Implemento, Nodos, Config) siguen en
    // HTML via callback OnRequestConfigurar.
    private void OnNavVistaX   (object? s, RoutedEventArgs e) => ShowVistaX();
    // QuantiX abre el overlay NATIVO (QuantiXPanel, Monitor live-only). Las
    // otras tabs (Motores, Shape, PID-tune, Calibracion, Prueba) siguen en
    // HTML via callback OnRequestConfigurar.
    private void OnNavQuantiX  (object? s, RoutedEventArgs e) => ShowQuantiX();
    // SectionX abre el overlay NATIVO (SectionXPanel, live-only). El editor
    // de mapeo surcos->secciones + test de reles + debug MQTT siguen en HTML
    // via callback OnRequestConfigurar (wired en el constructor).
    private void OnNavSectionX (object? s, RoutedEventArgs e) => ShowSectionX();
    // FlowX abre el overlay NATIVO (FlowXPanel, live-only). El editor de
    // config sigue en HTML y se abre desde el boton "Configurar" del propio
    // overlay (callback OnRequestConfigurar wired en el constructor).
    private void OnNavFlowX    (object? s, RoutedEventArgs e) => ShowFlowX();
    // StormX abre el overlay NATIVO (StormXPanel) en lugar de instanciar
    // un WebView para pages/stormx.html.
    private void OnNavStormX   (object? s, RoutedEventArgs e) => ShowStormX();
    private void OnNavCoreX    (object? s, RoutedEventArgs e) => ShowCoreXEcu();
    private void OnNavNodos    (object? s, RoutedEventArgs e) => ShowNodos();
    private void OnNavActualizar(object? s, RoutedEventArgs e) => ShowActualizar();
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
