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
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using System.Net.Http;
using PilotX.UI.Services;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;
using PilotX.Cockpit.Bars.Views;
using PilotX.Desktop.Controls;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views;

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

    // Creación de guía A/B en el mapa (toco A, manejo, toco B).
    private Border?    _abCreatePanel;
    private TextBlock? _abCreateHint;
    private Button?    _abCreateMark;
    private int    _abStep;             // 0 = esperando A, 1 = esperando B
    private double _abAe, _abAn;         // punto A capturado
    private double _lastPivotE, _lastPivotN; // última posición del tractor (del HUD)

    // Barras del cockpit (menús reutilizados de PilotX.Cockpit.Bars) — reemplazan
    // el HudBar + BottomToolbar placeholder por los menús reales.
    private Grid? _cockpitBarsHost;
    private BarraSuperior? _barSuperior;
    private BarraDerecha? _barDerecha;
    private BarraAbajo? _barAbajo;
    private MenuIzquierda? _menuIzq;
    private CockpitStateClient? _cockpitPoller;
    private GuidanceCommandClient? _cockpitCmd;
    private HttpClient? _cockpitHttp;
    private BarraSuperiorViewModel? _vmSup;
    private BarraDerechaViewModel? _vmDer;
    private BarraAbajoViewModel? _vmAba;
    private MenuIzquierdaViewModel? _vmIzq;
    private const double MenuIzqCollapsed = 40;
    private const double MenuIzqNarrow = 140;
    private const double MenuIzqExpanded = 316;

    // WebView lazy: se instancia on-demand y se dispone al cerrar la pantalla.
    private Panel?   _webViewSlot;
    private Button?  _webViewBack;
    private IWebViewHandle? _webView;
    private bool     _coldStartLogged;

    // Ventana-diálogo hija para páginas que el nativo abre como ventana chiquita
    // separada (ej. FormBuildTracks → tracks.html). Es su propia Window con barra
    // de título y X, así NO tapa el mapa ni sufre el airspace del WebView2.
    private Window?  _dialogWin;
    private IWebViewHandle? _dialogWebView;

    // HUD
    private TextBlock _hudSpeed;
    private TextBlock _hudHeading;
    private TextBlock? _hudTrack;

    // Cluster del piloto (giro / salteo / distancia a la línea, arriba-centro
    // del mapa con el piloto activo).
    private Border? _pilotoCluster;
    private Button? _pcGiroIzq, _pcGiroDer, _pcSkipMenos, _pcSkipMas;
    private TextBlock? _pcXte, _pcXteFlecha, _pcXteUnidad, _pcSkip;
    private int _pcSalteadas;          // lo que muestra el cluster (0..9)
    private TextBlock? _pcGiroInfo;
    private YouTurnPath? _lastYt;      // estado del U-turn (del poller de guidance)
    // Debug de rumbos: rumbo del tractor y de la guía activa (grados 0=N, CW),
    // para ver a qué guía apunta y cuánto desvía. NaN = sin dato.
    private double _lastTractorHeadingDeg = double.NaN;
    private double _lastGuideHeadingDeg = double.NaN;
    // Índice de la guía paralela respecto de la de referencia (howManyPathsAway):
    // 0 = la inicial, negativo = izquierda, positivo = derecha. NaN = sin guía.
    private double _lastPathsAway = double.NaN;
    // Distancia perpendicular a la guía (XTE) en metros, con signo. NaN = sin guía.
    private double _lastXteMeters = double.NaN;
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
    // Paths geometry poller (Stage 5 mapa GL): youturn (giro de cabecera) +
    // recorded path. Cadencia 1 Hz contra /api/aog/paths — solo cambia al
    // generar un giro o grabar. Revision-cache filtra snapshots iguales.
    // Solo con UseGl=on.
    private PathsGeometryPoller? _pathsPoller;
    // Banderas del operario (piedra, pozo, alambrado caído...). Cadencia baja
    // (2 s, ver FlagsPoller) — no hay urgencia de tiempo real como con la
    // posición del tractor. Solo con UseGl=on, igual que el resto de esta capa
    // (MapSkiaSurface no tiene DrawFlags).
    private FlagsPoller? _flagsPoller;
    // Prescripción (.shp): zonas con color por dosis sobre el mapa. 1 Hz
    // filtrado por source_token. Solo con UseGl=on.
    private ShapeGeometryPoller? _shapePoller;
    private SoundAlarmPoller? _soundPoller;

    // Toolbar inferior (state-aware).
    private Button? _btnSettings;
    private Button? _btnFieldTools;

    // El mini-mapa de la esquina inferior izquierda se sacó (2026-07-28): con el
    // mapa nativo a pantalla completa no aportaba y le comía lugar a los widgets
    // de los productos. MiniMapView sigue existiendo por si vuelve en otro lado.

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

    // Widgets SOBRE el mapa (los que el operario prende desde el Hub). Viven
    // en un Canvas encima del mapa y se arrastran a mano; la posición se
    // guarda en overlayPrefs.json, el mismo archivo que usa la app WinForms.
    // (_overlaysClient se comparte con el Hub nativo — es el mismo /api/overlays)
    private Canvas? _mapOverlaysHost;
    private QuantiXMapOverlay? _qxMapOverlay;
    private QuantiXControlBar? _qxControlBar;
    private WidgetQuantiXClient? _qxWidgetClient;
    private System.Threading.CancellationTokenSource? _overlayPrefsCts;

    // Franja mínima VistaX sobre el mapa (barras de nivel por surco, auto-mini
    // cerca de la cabecera). Comparte el toggle vx_overlay con WinForms.
    private VistaXMapStrip? _vxMapStrip;
    private VistaXClient? _vxStripClient;

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
        // Cualquier pantalla que tape el mapa (WebView del Hub o panel nativo)
        // lo oculta con IsVisible=false. Enganchándonos ahí frenamos también los
        // pollers, sin tener que tocar los ~14 lugares que lo ocultan.
        // El evento acelera la reacción; la corrección de fondo la hace
        // ReconciliarMapa desde el HUD, que no depende de que llegue.
        if (_mapHost != null)
            _mapHost.VisibilidadCambiada += _ => ReconciliarMapa();

        // Zoom táctil del mapa (+/−): la cabina no tiene rueda de mouse.
        var btnZoomIn  = this.FindControl<Button>("BtnZoomIn");
        var btnZoomOut = this.FindControl<Button>("BtnZoomOut");
        if (btnZoomIn  != null) btnZoomIn.Click  += (_, _) => _mapHost?.ZoomIn();
        if (btnZoomOut != null) btnZoomOut.Click += (_, _) => _mapHost?.ZoomOut();
        _abCreatePanel   = this.FindControl<Border>("AbCreatePanel");
        _abCreateHint    = this.FindControl<TextBlock>("AbCreateHint");
        _abCreateMark    = this.FindControl<Button>("AbCreateMark");
        _cockpitBarsHost = this.FindControl<Grid>("CockpitBarsHost");
        _barSuperior     = this.FindControl<BarraSuperior>("BarSuperior");
        _barDerecha      = this.FindControl<BarraDerecha>("BarDerecha");
        _barAbajo        = this.FindControl<BarraAbajo>("BarAbajo");
        _menuIzq         = this.FindControl<MenuIzquierda>("MenuIzq");
        _webViewSlot     = this.FindControl<Panel>("WebViewSlot");
        _webViewBack     = this.FindControl<Button>("WebViewBack");

        _hudSpeed        = this.FindControl<TextBlock>("HudSpeed");
        _hudHeading      = this.FindControl<TextBlock>("HudHeading");
        _hudTrack        = this.FindControl<TextBlock>("HudTrack");

        // Cluster del piloto: giro / salteo / distancia a la línea.
        _pilotoCluster = this.FindControl<Border>("PilotoCluster");
        _pcGiroIzq     = this.FindControl<Button>("PcGiroIzq");
        _pcGiroDer     = this.FindControl<Button>("PcGiroDer");
        _pcSkipMenos   = this.FindControl<Button>("PcSkipMenos");
        _pcSkipMas     = this.FindControl<Button>("PcSkipMas");
        _pcXte         = this.FindControl<TextBlock>("PcXte");
        _pcXteFlecha   = this.FindControl<TextBlock>("PcXteFlecha");
        _pcXteUnidad   = this.FindControl<TextBlock>("PcXteUnidad");
        _pcSkip        = this.FindControl<TextBlock>("PcSkip");
        _pcGiroInfo    = this.FindControl<TextBlock>("PcGiroInfo");
        if (_pcGiroIzq != null) _pcGiroIzq.Click += (_, _) => _ = MandarComandoPiloto("uturn_manual_izq");
        if (_pcGiroDer != null) _pcGiroDer.Click += (_, _) => _ = MandarComandoPiloto("uturn_manual_der");
        // Tocar el aviso del giro ("giro ↱ en N m") invierte el lado del giro
        // armado; "GIRANDO" lo aborta. Réplica del SwapDirection nativo.
        if (_pcGiroInfo != null) _pcGiroInfo.PointerPressed += (_, _) => _ = MandarComandoPiloto("uturn_swap");
        if (_pcSkipMenos != null) _pcSkipMenos.Click += (_, _) => _ = CambiarSalteo(-1);
        if (_pcSkipMas != null) _pcSkipMas.Click += (_, _) => _ = CambiarSalteo(+1);
        _hudArea         = this.FindControl<TextBlock>("HudArea");
        _hudStatusText   = this.FindControl<TextBlock>("HudStatusText");
        _hudStatusDot    = this.FindControl<Ellipse>("HudStatusDot");
        _hudStatusChip   = this.FindControl<Border>("HudStatusChip");

        _btnSettings     = this.FindControl<Button>("BtnSettings");
        _btnFieldTools   = this.FindControl<Button>("BtnFieldTools");

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
        _mapOverlaysHost   = this.FindControl<Canvas>("MapOverlaysHost");
        _qxMapOverlay      = this.FindControl<QuantiXMapOverlay>("QxMapOverlay");
        _vxMapStrip        = this.FindControl<VistaXMapStrip>("VxMapStrip");
        _nudgeOverlay      = this.FindControl<Border>("NudgeOverlay");
        // Los tres de corrección lateral mandan el mismo comando que mandaban
        // desde la barra; lo único que cambió es dónde están.
        var bIzq = this.FindControl<Button>("BtnNudgeIzq");
        var bCen = this.FindControl<Button>("BtnNudgeCentro");
        var bDer = this.FindControl<Button>("BtnNudgeDer");
        if (bIzq != null) bIzq.Click += (_, __) => { _ = _cockpitCmd?.SendAsync("nudge_left"); };
        if (bCen != null) bCen.Click += (_, __) => { _ = _cockpitCmd?.SendAsync("center"); };
        if (bDer != null) bDer.Click += (_, __) => { _ = _cockpitCmd?.SendAsync("nudge_right"); };
        // UbicarNudgeOverlay alinea el botón Centrar con el eje del tractor.
        _btnNudgeCentro = bCen;

        if (_camarasHost != null)
        {
            // Boton "Configurar" del CamarasPanel: abre camaras.html en
            // WebView lazy para la tab Configuracion (formulario IP/usuario/
            // clave por camara — el live monitor ya esta nativo).
            // A Configuración parado en el módulo Cámaras (no a camaras.html
            // suelta: abría "otra ventana más grande" fuera del flujo de
            // config — reporte usuario 2026-08-06). El deep-link ?mod= lo
            // resuelve config.js clickeando el botón real del menú. MISMA
            // ventana-diálogo que el "Configuración" del menú (OpenDialogPage
            // 820x600), NO NavigateTo: el WebView a pantalla completa era la
            // "pantalla gigante" del segundo reporte.
            _camarasHost.OnRequestConfigurar = () =>
                OpenDialogPage("pages/config.html?mod=camaras.html", "Configuración", 820, 600);
            _camarasHost.OnRequestCerrar = CloseCamaras;
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
            // El cockpit tiene que ocupar TODA la pantalla de la cabina.
            // Ni Maximized ni FullScreen alcanzan con SystemDecorations.None:
            // Maximized respeta el área de trabajo (deja la barra de tareas de
            // Windows a la vista) y FullScreen, sin decoraciones, en Windows no
            // llega a cubrirla. Se dimensiona a mano contra los bounds FÍSICOS
            // de la pantalla (Screen.Bounds, no WorkingArea), convertidos a DIPs
            // con el factor de escala del monitor.
            WindowState = WindowState.Normal;
            AjustarAPantallaCompleta();
            if (_rootBorder != null)
            {
                _rootBorder.CornerRadius = new global::Avalonia.CornerRadius(0);
                _rootBorder.BorderThickness = new global::Avalonia.Thickness(0);
            }
            if (_headerBar     != null) _headerBar.IsVisible = false;
            // Las 4 barras del cockpit reemplazan el HudBar + BottomToolbar
            // placeholder (menús reales: crear A/B, curva, piloto, secciones…).
            if (_hudBar        != null) _hudBar.IsVisible = false;
            if (_bottomToolbar != null) _bottomToolbar.IsVisible = false;
            if (_cockpitBarsHost != null) _cockpitBarsHost.IsVisible = true;
            SetupCockpitBars();
            // Widgets sobre el mapa: solo en modo cockpit, que es cuando hay
            // labor. En modo ventana el mapa es chico y taparlo no sirve.
            SetupMapOverlays();
            // La FAB roja de cerrar app queda OCULTA: la barra superior ya trae
            // su ✕ (apagar). Esa X grande se confundía con "cerrar la pantalla"
            // y terminaba cerrando toda la app.
            var closeBtn = this.FindControl<Button>("CloseButton");
            if (closeBtn != null) closeBtn.IsVisible = false;
        }

        if (App.WindowMode != "float")
        {
            _hudPoller = new HudPoller(baseUrl: DeriveOrigin(App.TargetUrl), intervalMs: 100);
            _hudPoller.SnapshotReceived += OnHudSnapshot;
            _hudPoller.PollFailed       += OnHudPollFailed;
            _hudPoller.Start();
            Closed += (_, _) => _hudPoller?.Dispose();

            // Prewarm del WebView, en cuanto la UI queda ociosa: que el costo de
            // levantar Chromium lo pague el arranque y no el operario la primera
            // vez que abre Configuración. Prioridad Background para no competir
            // con el primer render del mapa.
            Dispatcher.UIThread.Post(PrecalentarWebView, DispatcherPriority.Background);

            // Al abrir un lote que ya tiene guías, activar la primera visible si
            // no hay ninguna activa: así las guías aparecen en el mapa apenas se
            // abre el lote y se cambian con los botones de ciclado ‹ ›.
            StartTrackAutoSelect(DeriveOrigin(App.TargetUrl));

            // Stage 2 mapa GL: poller dedicado para coverage (worked
            // area triangulado). Solo lo enchufamos si UseGl=on; el
            // render Skia legacy no pinta coverage (volumen de
            // triangulos no compite con DrawingContext).
            if (App.UseGl)
            {
                var cov = new CoverageClient(DeriveOrigin(App.TargetUrl));
                // 350ms (~3 Hz): la cobertura se pinta continuamente detrás del
                // tractor, así que a 1 Hz la huella aparecía con ~1s de retraso
                // ("arranca a pintar más tarde"). Revision-cache evita re-subir el
                // VBO si no cambió. (Incremental /coverage?since=<rev> queda para
                // futuro.)
                //
                // Protocolo INCREMENTAL (cursor "j:p:v"): cada poll baja solo lo
                // pintado desde el anterior — bytes, no el snapshot completo (que
                // llegaba a ~3 MB en jornada de 8 h y hacía tironear el mapa aun a
                // 350 ms por el GC). Con payloads chicos, 200 ms da pintado fluido.
                _coveragePoller = new CoveragePoller(cov, snap =>
                {
                    _mapHost?.OnCoverage(snap);
                }, periodMs: 200);
                _coveragePoller.Start();
                Closed += (_, _) => _coveragePoller?.Stop();

                // Paths (Stage 5): youturn (giro de cabecera) + recorded path.
                // 1 Hz con revision-cache — solo cambia al generar un giro o
                // grabar/cargar un camino. Especifico de GL (como coverage).
                var pc = new PathsGeometryClient(DeriveOrigin(App.TargetUrl));
                _pathsPoller = new PathsGeometryPoller(pc, snap =>
                {
                    _mapHost?.OnPaths(snap);
                }, periodMs: 1000);
                _pathsPoller.Start();
                Closed += (_, _) => _pathsPoller?.Stop();

                // Banderas: el widget banderas.html ya las crea/edita contra
                // el motor; sin esto el mapa nunca mostraba lo que se cargaba
                // ahí. 2 s de cadencia (ver FlagsPoller), especifico de GL
                // (como coverage/paths).
                var fc = new FlagsClient(DeriveOrigin(App.TargetUrl));
                _flagsPoller = new FlagsPoller(fc, flags =>
                {
                    _mapHost?.OnFlags(flags);
                }, periodMs: 2000);
                _flagsPoller.Start();
                Closed += (_, _) => _flagsPoller?.Stop();

                // Prescripción (.shp): zonas con color por dosis, base de
                // QuantiX/FlowX. 1 Hz filtrado por source_token — solo cambia
                // al subir otro shape o cambiar el campo de dosis. La
                // triangulación corre en el hilo del poller, no en el GL.
                // Alarmas sonoras: pollea /api/sonidos/estado y toca el WAV por
                // el sink que registró el head (Desktop: winmm). Sin sink, no
                // suena — la pantalla Sonidos muestra las alarmas igual.
                _soundPoller = new SoundAlarmPoller(DeriveOrigin(App.TargetUrl));
                Closed += (_, _) => _soundPoller?.Dispose();

                _shapePoller = new ShapeGeometryPoller(DeriveOrigin(App.TargetUrl), snap =>
                {
                    _mapHost?.OnShape(snap);
                });
                Closed += (_, _) => _shapePoller?.Dispose();
            }

            // Stages 3/4: pollers de guidance/tool/tram. Corren tanto
            // con UseGl=on como con UseGl=off — MapSkiaSurface tambien
            // pinta estas capas (paridad parcial para que el toggle no
            // pierda referencia visual). Coverage se queda solo en GL.
            var gg = new GuidanceGeometryClient(DeriveOrigin(App.TargetUrl));
            _guidancePoller = new GuidanceGeometryPoller(gg, snap =>
            {
                _mapHost?.OnGuidance(snap);
                // Rumbo de la guía activa = dirección A→B de la polyline (0=N, CW).
                _lastGuideHeadingDeg = ComputeGuideHeadingDeg(snap);
                _lastPathsAway = (snap.PathsAway == int.MinValue) ? double.NaN : snap.PathsAway;
                _lastXteMeters = snap.XteMeters; // NaN si no hay guía
                // Estado del U-turn (réplica 6.8.5) para el cluster del piloto.
                _lastYt = snap.YouTurn;
                UpdateHeadingDebug();
            // 250 ms (era 1000): el XTE de este poller alimenta la distancia a
            // la línea del cluster del piloto — a 1 Hz el número parecía
            // clavado. La geometría igual solo se re-sube si cambió (revision).
            }, periodMs: 250);
            _guidancePoller.Start();
            Closed += (_, _) => _guidancePoller?.Stop();

            // Tool / sections (Stage 4a). 4 Hz porque los puntos siguen
            // al tractor; sin revision-cache, cada poll va al render.
            // Sprite del vehículo elegido en Configuración: si hay uno, el mapa
            // dibuja el tractor en vez del triángulo. Se recarga cada 5 s para
            // que el cambio se vea sin reiniciar (es una acción poco frecuente,
            // no vale la pena un canal dedicado).
            _ = CargarSpriteVehiculoAsync(DeriveOrigin(App.TargetUrl));

            var tg = new ToolGeometryClient(DeriveOrigin(App.TargetUrl));
            _toolPoller = new ToolGeometryPoller(tg, snap =>
            {
                _mapHost?.OnTool(snap);
            }, periodMs: 100);
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
            try { _webView.OpenDevTools(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] DevTools error: " + ex.Message); }
        }
    }

    /// <summary>
    /// Trae el sprite del vehículo activo y se lo pasa al mapa. Reintenta cada
    /// 5 s: así, cuando el operario elige otro vehículo en Configuración, el
    /// mapa lo cambia solo. Si no hay sprite o falla, el mapa sigue con el
    /// triángulo — nunca queda sin marcador de posición.
    /// </summary>
    private async Task CargarSpriteVehiculoAsync(string origin)
    {
        var cli = new VehicleSpriteClient(origin);
        string? ultimo = null;
        bool ruedaLista = false;
        bool implementoListo = false;
        bool pisoListo = false;
        while (true)
        {
            try
            {
                // Piso del mapa (fondo texturado): una sola vez.
                if (!pisoListo)
                {
                    var piso = await cli.GetSueloAsync().ConfigureAwait(false);
                    if (piso != null)
                    {
                        _mapHost?.SetFloorTexture(piso.Rgba, piso.Width, piso.Height);
                        pisoListo = true;
                    }
                }
                // Rueda delantera e implemento: una sola vez, no cambian con el
                // vehículo elegido.
                if (!ruedaLista)
                {
                    var rueda = await cli.GetRuedaAsync().ConfigureAwait(false);
                    if (rueda != null)
                    {
                        _mapHost?.SetWheelSprite(rueda.Rgba, rueda.Width, rueda.Height);
                        ruedaLista = true;
                    }
                }
                if (!implementoListo)
                {
                    var impl = await cli.GetImplementoAsync().ConfigureAwait(false);
                    if (impl != null)
                    {
                        _mapHost?.SetImplementSprite(impl.Rgba, impl.Width, impl.Height);
                        implementoListo = true;
                    }
                }
                var sp = await cli.GetActivoAsync().ConfigureAwait(false);
                string? actual = sp?.Archivo;
                if (actual != ultimo)
                {
                    ultimo = actual;
                    var mapa = _mapHost;
                    if (mapa != null)
                    {
                        if (sp != null) mapa.SetVehicleSprite(sp.Rgba, sp.Width, sp.Height);
                        else mapa.SetVehicleSprite(null, 0, 0);
                    }
                }
            }
            catch { /* que el loop no muera nunca por un error puntual */ }

            try { await Task.Delay(5000).ConfigureAwait(false); }
            catch { return; }
        }
    }

    /// <summary>
    /// Deja la ventana cubriendo la pantalla entera, barra de tareas incluida.
    /// Se usa en vez de WindowState.FullScreen porque con SystemDecorations.None
    /// ese estado no cubre la barra de tareas en Windows.
    /// </summary>
    private void AjustarAPantallaCompleta()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;

            var b = screen.Bounds;                       // píxeles físicos
            double escala = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new global::Avalonia.PixelPoint(b.X, b.Y);
            Width  = b.Width  / escala;                  // Width/Height van en DIPs
            Height = b.Height / escala;
            Topmost = false;                             // que no tape diálogos del sistema
        }
        catch { /* si falla, queda el tamaño del XAML */ }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                // Doble clic en el header: alterna pantalla completa ↔ ventana.
                // Es la única salida para el operario, que no tiene teclado.
                if (Width >= (Screens.Primary?.Bounds.Width ?? 0) / (Screens.Primary?.Scaling ?? 1) - 1)
                {
                    Width = 1280; Height = 800;
                    Position = new global::Avalonia.PixelPoint(80, 60);
                }
                else AjustarAPantallaCompleta();
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

    // ---- pausa del mapa mientras otra pantalla lo tapa --------------------
    //
    // Abrir Configuración (o cualquier página del Hub) levanta WebView2, que es
    // un árbol de procesos Chromium entero. Si en ese mismo momento seguimos
    // pidiendo frames del mapa a 30 Hz y bajando el snapshot de cobertura —que
    // crece con el área trabajada, de 248 KB a ~3 MB— la máquina de la cabina se
    // arrastra justo en el arranque de la pantalla nueva.
    //
    // Se paran solo los pollers que alimentan ÚNICAMENTE al mapa. El HudPoller
    // sigue: es barato (2,5 KB) y lo consume también la barra de estado.
    // Estado deseado del mapa. Se compara contra la visibilidad real en cada
    // snapshot del HUD (ver ReconciliarMapa): si alguna transición se pierde, la
    // siguiente vuelta lo corrige sola.
    private bool _mapaCorriendo = true;

    /// <summary>
    /// Lleva el mapa al estado que le corresponde según esté tapado o no.
    ///
    /// Existe porque la primera versión de esto reaccionaba SOLO al evento de
    /// cambio de visibilidad. Con eso, perder un único evento dejaba el mapa
    /// pausado para siempre: sin frames, con los pollers frenados y sin nada que
    /// lo devolviera a la normalidad — el mapa quedaba congelado mostrando una
    /// escena vieja mientras la barra de arriba seguía actualizándose (el HUD
    /// nunca se pausa), que es justo el síntoma más confuso posible.
    ///
    /// Ahora el estado deseado se reafirma continuamente en vez de depender de
    /// los flancos. Es barato: se llama a 10 Hz y no hace nada si ya coincide.
    /// </summary>
    private void ReconciliarMapa()
    {
        bool debeCorrer = _mapHost != null && _mapHost.IsVisible;
        if (debeCorrer == _mapaCorriendo) return;

        _mapaCorriendo = debeCorrer;
        if (debeCorrer) ReanudarMapa();
        else PausarMapa();
    }

    private void PausarMapa()
    {
        _mapaCorriendo = false;
        _mapHost?.Pausar();
        _coveragePoller?.Stop();
        _toolPoller?.Stop();
        _guidancePoller?.Stop();
        _tramPoller?.Stop();
        _pathsPoller?.Stop();
        _flagsPoller?.Stop();
    }

    private void ReanudarMapa()
    {
        _mapaCorriendo = true;
        _coveragePoller?.Start();
        _toolPoller?.Start();
        _guidancePoller?.Start();
        _tramPoller?.Start();
        _pathsPoller?.Start();
        _flagsPoller?.Start();
        _mapHost?.Reanudar();
    }

    // ---- prewarm del WebView ---------------------------------------------
    //
    // Levantar el motor del web view (proceso hijo + entorno) es lo más caro del
    // ciclo, y con el reciclado ya solo se paga UNA vez por corrida — pero esa
    // vez le tocaba al operario, la primera que abría Configuración. Acá se paga
    // durante el arranque, que es cuando nadie está esperando.
    //
    // El control es un NativeControlHost: recién crea la ventana nativa cuando
    // está adjunto al árbol Y con un tamaño real. Con IsVisible=false eso no
    // pasa y el prewarm no calentaría nada. Y no sirve dejarlo visible con
    // Opacity=0, porque el web view pinta por airspace ignorando la opacidad y
    // taparía el mapa. Por eso se lo deja visible pero de 1×1 px en un rincón el
    // tiempo que tarda en levantar, y después se oculta.
    private bool _webViewPrecalentando;

    private void PrecalentarWebView()
    {
        if (_webView != null || _webViewSlot == null || App.WebViewHost == null) return;
        try
        {
            _webView = App.WebViewHost.Create(OnWebViewNavigated);
            _webViewSlot.Children.Add(_webView.Control);

            _webViewPrecalentando = true;
            _webViewSlot.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            _webViewSlot.VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Top;
            _webViewSlot.Width  = 1;
            _webViewSlot.Height = 1;
            _webViewSlot.IsHitTestVisible = false;
            _webViewSlot.IsVisible = true;
            _webView.Navigate("about:blank");

            // Plazo generoso: no sabemos cuánto tarda en levantar en la máquina
            // de la cabina, y quedarse 1×1 de más no molesta a nadie.
            DispatcherTimer.RunOnce(TerminarPrecalentado, TimeSpan.FromSeconds(6),
                                    DispatcherPriority.Background);
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView prewarm iniciado");
        }
        catch (Exception ex)
        {
            // Si falla, no se pierde nada: sigue el camino lazy de siempre.
            _webViewPrecalentando = false;
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView prewarm error: " + ex.Message);
        }
    }

    /// <summary>Devuelve el slot a tamaño completo. Se llama al terminar el
    /// prewarm y antes de mostrar cualquier pantalla real.</summary>
    private void RestaurarSlotWebView()
    {
        if (_webViewSlot == null) return;
        _webViewPrecalentando = false;
        _webViewSlot.Width  = double.NaN;
        _webViewSlot.Height = double.NaN;
        _webViewSlot.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        _webViewSlot.VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Stretch;
        _webViewSlot.IsHitTestVisible = true;
    }

    private void TerminarPrecalentado()
    {
        if (!_webViewPrecalentando) return;   // ya abrió una pantalla de verdad
        RestaurarSlotWebView();
        if (_webViewSlot != null) _webViewSlot.IsVisible = false;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView prewarm listo");
    }

    private void ShowWebView(string url, bool showBackButton)
    {
        if (_webViewSlot == null) return;
        // Si estábamos en pleno prewarm, el slot está en 1×1: devolverlo a
        // tamaño completo antes de mostrar nada.
        RestaurarSlotWebView();
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
                // Sin backend WebView inyectado (ej. build Android sin él aún)
                // no hay pantalla HTML que mostrar: se queda en el mapa nativo.
                if (App.WebViewHost == null) return;
                _webView = App.WebViewHost.Create(OnWebViewNavigated);
                _webViewSlot.Children.Add(_webView.Control);
            }
            // Pausar ANTES de navegar: la subida de WebView2 es el pico de costo
            // y es cuando más se nota tener el mapa compitiendo por CPU.
            PausarMapa();
            _webView.Navigate(url);
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
                // NO se destruye: se recicla. Antes se hacía Dispose en cada
                // cierre, así que CADA apertura de pantalla volvía a levantar el
                // árbol de procesos Chromium desde cero — que es el grueso de lo
                // que se sentía como "abrir Configuración tarda".
                //
                // Navegar a about:blank libera el contenido de la página (imágenes,
                // JS, DOM) y baja el working set del proceso hijo, que era el
                // objetivo real del Dispose. El proceso queda vivo y listo, así la
                // próxima apertura es inmediata. Blank() y NO Release(): este
                // último desengancha NavigationCompleted y dejaría el handle sordo
                // al centinela pilotx-close en la apertura siguiente.
                _webView.Blank();
            }
            if (_webViewSlot != null) _webViewSlot.IsVisible = false;
            if (_webViewBack != null) _webViewBack.IsVisible = false;
            if (_mapHost != null && App.WindowMode != "float")
            {
                _mapHost.IsVisible = true;
                ReanudarMapa();
            }
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView disposed -> back to native");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] WebView close error: " + ex.Message);
        }
    }

    private void OnWebViewBack(object? sender, RoutedEventArgs e) => CloseWebView();

    // Abre una página del Hub como VENTANA CHIQUITA separada (diálogo), igual
    // que el form nativo equivalente. Reusa la misma si ya está abierta.
    private void OpenDialogPage(string relativePath, string title, double w, double h,
                                bool mapaVivo = false)
    {
        string url = App.TargetUrl.TrimEnd('/');
        int api = url.IndexOf("/pages/", StringComparison.OrdinalIgnoreCase);
        string origin = api >= 0 ? url.Substring(0, api) : url;
        string full = origin + "/" + relativePath.TrimStart('/');

        // ?widget=1 — la misma página, pero SIN la navegación del Hub. Abierta
        // desde el menú de PilotX el operario no vino a navegar: vino a hacer
        // una cosa y volver al lote. Esa barra lateral se come entre 150 y 240
        // px de ancho de una ventana que queremos lo más chica posible, porque
        // el mapa tiene que seguir viéndose.
        full += (full.IndexOf('?') >= 0 ? "&" : "?") + "widget=1";

        var (aw, ah) = TamanoDialogo(relativePath, w, h);
        OpenDialogUrl(full, title, aw, ah, mapaVivo);
    }

    /// <summary>
    /// Recorta el tamaño pedido al área de trabajo real de la pantalla donde
    /// está PilotX. Devuelve como máximo el 85% del ancho y el 80% del alto:
    /// el diálogo tiene que entrar entero Y dejar mapa visible alrededor.
    /// Si por lo que sea no se puede leer la pantalla, se devuelve lo pedido.
    /// </summary>
    private (double W, double H) AjustarAPantalla(double w, double h)
    {
        try
        {
            var pantalla = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
            if (pantalla == null) return (w, h);

            // WorkingArea viene en píxeles físicos; las medidas de la Window son
            // unidades lógicas. Sin dividir por Scaling, en una pantalla a 150%
            // el techo quedaría un 50% más grande de lo que se ve.
            double esc = pantalla.Scaling <= 0 ? 1.0 : pantalla.Scaling;
            double maxW = pantalla.WorkingArea.Width / esc * 0.85;
            double maxH = pantalla.WorkingArea.Height / esc * 0.80;

            return (Math.Min(w, maxW), Math.Min(h, maxH));
        }
        catch
        {
            // Nunca impedir que se abra el diálogo por no poder medir la pantalla.
            return (w, h);
        }
    }

    /// <summary>
    /// Tamaño de la ventana-diálogo según la página. Centralizado acá y no en
    /// cada llamada: es UNA decisión de producto ("la ventana más chica que
    /// deje operar") y repartida por los call sites se desincroniza sola.
    ///
    /// Sin la barra lateral del Hub (ver ?widget=1) estas páginas necesitan
    /// bastante menos ancho del que tenían: el default histórico era 820x600
    /// para todo, midiera lo que midiera el contenido.
    ///
    /// Lo que NO está en la tabla conserva el tamaño que le pasa el llamador.
    /// Prefiero dejar grande algo que no medí antes que dejar al operario con
    /// una pantalla recortada en la cabina.
    /// </summary>
    private static (double W, double H) TamanoDialogo(string relativePath, double wDefault, double hDefault)
    {
        string p = relativePath ?? "";
        int q = p.IndexOf('?');
        if (q >= 0) p = p.Substring(0, q);
        int barra = p.LastIndexOf('/');
        if (barra >= 0) p = p.Substring(barra + 1);
        p = p.ToLowerInvariant();

        switch (p)
        {
            // Contorno es el más chico de todos: es el que se abre PARA mirar el
            // mapa, así que cada píxel suyo es mapa tapado.
            //
            // 290 de alto no aprieta nada. Todo el CSS de estas páginas está
            // clampeado contra vh, y los botones tocan su piso (28 px) en cuanto
            // la ventana baja de ~370 de alto — o sea que entre 370 y 290 no se
            // achica NADA, solo se saca aire muerto. La vista de grabación
            // necesita ~150 px de contenido (cabecera + puntos/ha + fila de
            // grabar + "Ajustes" plegado + pie); en 290 sobran ~120.
            case "contorno.html":
                return (330, 290);

            // Una sola acción y volver al lote. Son widgets, no pantallas.
            case "banderas.html":
            case "sim-coords.html":
            case "suavizar-ab.html":
            case "corregir-posicion.html":
            case "tramline.html":
            case "cabecera.html":
                return (350, 340);

            // Listas / selección: necesitan alto para ver varias filas, no ancho.
            case "colores.html":
            case "colores-secciones.html":
            case "perfiles.html":
            case "cabecera-lineas.html":
            case "ayuda.html":
            case "eventos.html":
            case "ajustes-todos.html":
            case "recpath.html":
            case "tramlines.html":
                return (460, 470);

            // Guías: tamaño ÚNICO que banca todas sus pantallas internas (el
            // wizard tkWin se acomoda adentro). No hay resize por paso: las
            // navegaciones de la página no llegan al host (ver
            // OnDialogNavigated).
            case "tracks.html":
                return (460, 470);

            // Gráficos: acá el ancho SÍ es información (es el eje del tiempo),
            // así que se les da ancho y se les saca alto.
            case "grafico-direccion.html":
            case "grafico-rumbo.html":
            case "grafico-xte.html":
            case "grafico-correccion.html":
                return (560, 370);

            default:
                return (wDefault, hDefault);
        }
    }

    /// <summary>
    /// Abre una URL ABSOLUTA (ej. el dashboard de CoreX en http://127.0.0.1:5181/)
    /// en una ventana chica cerrable, igual que OpenDialogPage pero sin componer
    /// la URL contra el origin del Hub (:5180).
    /// </summary>
    // ---- cierre del diálogo de LOTE por cambio de lote ---------------------
    //
    // La página cierra su ventana navegando a la URL centinela "pilotx-close",
    // pero en el WebView del diálogo esa navegación NO se produce (el log de
    // NavigationCompleted solo muestra lote.html, nunca el centinela), así que
    // la ventana quedaba abierta tapando el mapa después de abrir el lote.
    //
    // En vez de seguir peleando con la navegación, se usa una señal que el host
    // ya tiene y es la que de verdad importa: el lote activo del HUD. Cuando
    // cambia, el trabajo que motivó abrir esta ventana ya está hecho y la
    // ventana sobra. Funciona igual para abrir, continuar, crear y cerrar lote.
    private bool _dialogEsLote;
    private string? _loteAlAbrirDialogo;
    private string? _lastFieldDir;

    private void CerrarDialogoSiCambioElLote(string? loteActual)
    {
        if (!_dialogEsLote || _dialogWin == null) return;
        if (string.Equals(loteActual ?? "", _loteAlAbrirDialogo ?? "", StringComparison.OrdinalIgnoreCase)) return;
        _loteAlAbrirDialogo = loteActual;
        CerrarDialogo();
    }

    private void OpenDialogUrl(string full, string title, double w, double h,
                               bool mapaVivo = false)
    {
        try
        {
            // REGLA DEL USUARIO (2026-07-30): "siempre importa ver el mapa".
            // Ningún diálogo puede apagarlo/ocultarlo/pausarlo para ganarle la
            // carrera de compositor al WebView2 — eso quedó descartado como
            // estrategia, aunque el diálogo corra riesgo de parpadear o quedar
            // en blanco mientras tanto (el bug de fondo, mismo síntoma que el
            // "SIN RESOLVER" del 2026-07-28, se resuelve de raíz sacando estos
            // diálogos a embebido en la MainWindow, no apagando el mapa).
            // `mapaVivo` queda de parámetro por compatibilidad con los call
            // sites existentes (contorno lo pasaba explícito) pero ya no hay
            // rama que apague nada: sacarlo del todo cuando el rediseño a
            // embebido esté hecho.
            if (_mapHost != null) _mapHost.IsVisible = true;
            ReanudarMapa();

            // Estas flags valen también cuando el diálogo ya está abierto y
            // solo se navega (el early-return de abajo): sin esto, pasar de
            // otra página a Guías dejaba el cierre-por-guía-nueva apagado.
            _dialogEsTracks = full.IndexOf("tracks.html", StringComparison.OrdinalIgnoreCase) >= 0;
            _tracksAlAbrirDialogo = -1;                  // el próximo HUD fija el piso
            _trackIdxAlAbrirDialogo = int.MinValue;      // ídem para la guía activa

            if (_dialogWin != null)
            {
                // ya abierta → traer al frente y navegar
                _dialogWebView?.Navigate(full);
                _dialogWin.Activate();
                return;
            }

            // Sin backend WebView no se puede abrir la página HTML en diálogo.
            if (App.WebViewHost == null)
            {
                if (_mapHost != null) _mapHost.IsVisible = true;
                ReanudarMapa();
                return;
            }
            _dialogEsLote = full.IndexOf("lote.html", StringComparison.OrdinalIgnoreCase) >= 0;
            _loteAlAbrirDialogo = _lastFieldDir;
            // Crear el control y montarlo (Content) ANTES de navegar: WebView.Avalonia
            // arma el CoreWebView2Controller contra el HWND del control ya adjunto al
            // árbol visual. Navegar antes de que la Window exista/se muestre le pedía
            // al control una navegación sin handle nativo todavía — quedaba en blanco
            // (a veces desde el primer frame, a veces a los pocos segundos, según
            // cuándo terminaba de inicializar el WebView2 de fondo). Mismo orden que
            // ya usa el WebView principal: Control attachado y visible ANTES de
            // Navigate (ver PrecalentarWebView + ShowWebView).
            _dialogWebView = App.WebViewHost.Create(OnDialogNavigated);

            // Los tamaños de TamanoDialogo son un TECHO medido contra el
            // contenido, no una promesa sobre la pantalla. La de la cabina es de
            // 10" (1080x720) y hay perfiles todavía más chicos: un diálogo más
            // grande que la pantalla deja botones fuera de alcance, sin barra de
            // título para moverlo con el dedo. Se recorta contra el área real de
            // trabajo, dejando un margen para que se siga viendo mapa alrededor
            // — que es la regla: el mapa siempre tiene que verse.
            var (w2, h2) = AjustarAPantalla(w, h);

            _dialogWin = new Window
            {
                Title = title,
                Width = w2,
                Height = h2,
                CanResize = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.Full,
                ShowInTaskbar = false,
                Content = _dialogWebView.Control
            };
            _dialogWin.Closed += (_, _) =>
            {
                try { _dialogWebView?.Release(); } catch { }
                _dialogWebView = null;
                _dialogWin = null;
                if (_mapHost != null) _mapHost.IsVisible = true;
                ReanudarMapa();
            };
            _dialogWin.Show(this);
            _dialogWebView.Navigate(full);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] OpenDialogPage error: " + ex.Message);
            if (_mapHost != null) _mapHost.IsVisible = true;
            ReanudarMapa();
        }
    }

    // La página del diálogo pide cerrar navegando a la URL centinela.
    //
    // OJO: en el WebView del diálogo esta navegación NO se produce — se verificó
    // con un log en NavigationCompleted y solo aparece la página en sí, nunca el
    // centinela. Por eso el cierre real lo maneja CerrarDialogoSiCambioElLote,
    // que mira el lote activo del HUD. Esto queda porque no molesta y sí anda en
    // el WebView principal, pero NO se puede confiar en ello acá.
    private void OnDialogNavigated(string url)
    {
        // OJO: en este WebView SOLO llegan las navegaciones iniciadas por el
        // host (Navigate). Las que inicia la página (location.href, el
        // centinela pilotx-close) NO disparan este callback — verificado con
        // el lote (2026-07) y de nuevo con Guías (2026-08-05). El cierre real
        // va por señales del HUD: CerrarDialogoSiCambioElLote y
        // CerrarDialogoSiHayGuiaNueva.
        if ((url ?? string.Empty).IndexOf("pilotx-close", StringComparison.OrdinalIgnoreCase) >= 0)
            CerrarDialogo();
    }

    // ---- cierre del diálogo de GUÍAS por guía nueva ------------------------
    //
    // Misma idea que el cierre por cambio de lote: la página no puede avisar
    // (ver OnDialogNavigated), pero el host YA ve tracks_total en el HUD.
    // Cuando sube con el diálogo de Guías abierto, la guía nueva quedó creada
    // y dibujada en el mapa: la ventana ya hizo su trabajo, se cierra sola.
    // Si baja (borraron una guía desde la lista), es el nuevo piso — sin esto,
    // borrar y crear en la misma sesión no cerraría nunca.
    private bool _dialogEsTracks;
    private int _tracksAlAbrirDialogo = -1;
    private int _trackIdxAlAbrirDialogo = int.MinValue;

    private void CerrarDialogoSiHayGuiaNueva(int tracksTotal, int trackIdx)
    {
        if (!_dialogEsTracks || _dialogWin == null) return;

        // Primer HUD con el diálogo abierto: fija el piso, no cierra nada.
        if (_trackIdxAlAbrirDialogo == int.MinValue) _trackIdxAlAbrirDialogo = trackIdx;
        if (_tracksAlAbrirDialogo < 0 || tracksTotal < _tracksAlAbrirDialogo)
        {
            _tracksAlAbrirDialogo = tracksTotal;
            return;
        }

        // Guía NUEVA creada (el total sube)…
        if (tracksTotal > _tracksAlAbrirDialogo)
        {
            _tracksAlAbrirDialogo = tracksTotal;
            CerrarDialogo();
            return;
        }

        // …o guía ELEGIDA desde la lista con el tilde verde (cambia la
        // activa). Mismo motivo que el resto: la página no puede avisar
        // (ver OnDialogNavigated), pero el host ya ve track_idx en el HUD.
        if (trackIdx != _trackIdxAlAbrirDialogo)
        {
            _trackIdxAlAbrirDialogo = trackIdx;
            CerrarDialogo();
        }
    }

    /// <summary>
    /// Cierra la ventana de diálogo y deja la barra izquierda plegada.
    ///
    /// No es cosmético: cerrar solo el submenú dejaba igual la barra abierta a
    /// 316 px tapando el mapa, justo cuando el operario quiere ver el lote que
    /// acaba de abrir. Se pliega entera (submenú + barra) para devolver la
    /// pantalla al mapa, que es a lo que se vino.
    /// </summary>
    private void CerrarDialogo()
    {
        try { _dialogWin?.Close(); } catch { }
        if (_vmIzq == null) return;
        _vmIzq.OpenSubmenu = null;
        _vmIzq.IsCollapsed = true;
    }

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

    // Brillo +/− del menú Navegación: mismo SistemaClient que usa el panel
    // Sistema, lazy-init igual que ShowSistema (cero costo si nunca se toca
    // ni brillo ni Sistema). cur=-1 puede ser hardware sin soporte (DDC/CI o
    // WMI no disponibles) O el backend sin ISistemaService cableado (hoy
    // PilotX.GuidanceEngine --webhost pasa sistema:null → api/sistema/brillo
    // responde siempre ok:false — PEDIDO a Leonardo en COORDINACION-SESIONES,
    // no es un bug de este wiring). El log deja rastro en vez de fallar mudo.
    private async void AdjustBrightness(int delta)
    {
        if (_sistemaClient == null)
            _sistemaClient = new SistemaClient(DeriveOrigin(App.TargetUrl));
        int cur = await _sistemaClient.GetBrightnessAsync().ConfigureAwait(true);
        if (cur < 0)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Brillo: sin soporte (backend o hardware) — api/sistema/brillo devolvió -1");
            return;
        }
        await _sistemaClient.SetBrightnessAsync(cur + delta).ConfigureAwait(true);
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

    // Ventana de Cámaras (nativa). Antes era overlay a PANTALLA COMPLETA que
    // apagaba el mapa — dos strikes: violaba la regla "el mapa siempre se ve",
    // y el día que el "<-" quedó enterrado bajo la barra del cockpit dejó al
    // operario atrapado (2026-08-05). Ahora es una ventana como Datos del
    // Lote: el mapa sigue vivo alrededor, y el cierre es la X nativa o el
    // botón Cerrar del propio panel.
    private Window? _camarasWin;

    private void ShowCamaras()
    {
        if (_camarasWin != null) { _camarasWin.Activate(); return; }
        if (_camarasClient == null)
            _camarasClient = new CamarasClient(DeriveOrigin(App.TargetUrl));

        // Instancia PROPIA para la ventana (no el host embebido _camarasHost,
        // que quedó sin usar en este flujo): reparentar un control vivo entre
        // el grid y una Window es frágil en Avalonia, y el panel es barato de
        // construir — lo caro (el cliente HTTP y su cache) se reusa.
        var panel = new CamarasPanel();
        panel.OnRequestConfigurar = () =>
        {
            try { _camarasWin?.Close(); } catch { }
            // A Configuración parado en el módulo Cámaras — este es el handler
            // que se usa de verdad (la ventana nativa arma su CamarasPanel
            // propio; quedó desincronizado dos veces el 2026-08-06: primero
            // apuntando a camaras.html suelta, después vía NavigateTo a
            // pantalla completa "gigante"). MISMA ventana-diálogo que el
            // "Configuración" del menú.
            OpenDialogPage("pages/config.html?mod=camaras.html", "Configuración", 820, 600);
        };
        panel.OnRequestCerrar = () => { try { _camarasWin?.Close(); } catch { } };
        panel.Attach(_camarasClient);

        var (w, h) = AjustarAPantalla(1040, 680);
        _camarasWin = new Window
        {
            Title = "Cámaras",
            Width = w,
            Height = h,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SystemDecorations = SystemDecorations.Full,
            ShowInTaskbar = false,
            Content = panel
        };
        _camarasWin.Closed += (_, _) =>
        {
            try { panel.Detach(); } catch { }
            _camarasWin = null;
        };
        _camarasWin.Show(this);
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Camaras open (ventana nativa, mapa vivo)");
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

    private void OnWebViewNavigated(string url)
    {
        // Puente de cierre: como el WebView2 nativo TAPA los controles Avalonia
        // (el botón "Atrás" queda cubierto — airspace), las páginas HTML cierran
        // el overlay navegando a una URL centinela que interceptamos acá. Es el
        // único canal disponible (este wrapper solo expone NavigationCompleted).
        var u = url ?? string.Empty;
        if (u.IndexOf("pilotx-close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            CloseWebView();
            return;
        }

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

    // ---------- Barras del cockpit (PilotX.Cockpit.Bars) -------------------

    // Cablea las 4 barras reutilizadas de la librería (mismo patrón que
    // PilotX.Bars.Host, pero embebidas): ViewModels + GuidanceCommandClient
    // (POST /api/aog/guidance/command) + CockpitStateClient (poll /api/aog/state
    // → Apply en cada barra). El menú izquierdo se ensancha al abrir un submenú.
    // ---- Auto-activar guía al abrir un lote ------------------------------
    private System.Net.Http.HttpClient? _trackHttp;
    private System.Threading.CancellationTokenSource? _trackCts;

    private void StartTrackAutoSelect(string baseUrl)
    {
        _trackHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        _trackCts = new System.Threading.CancellationTokenSource();
        var ct = _trackCts.Token;
        string url = baseUrl.TrimEnd('/');
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Estado: ¿AutoTrack prendido (botón de la barra derecha)?
                    // ¿piloto enganchado? ¿hay lote? El auto-seguimiento de la guía
                    // más cercana SOLO corre si el usuario prendió AutoTrack; y con
                    // el piloto puesto NO reelegimos (se sostiene la línea activa).
                    bool autoTrackOn = false, autoSteer = false, jobStarted = false;
                    try
                    {
                        var sjson = await _trackHttp.GetStringAsync(url + "/api/aog/state", ct).ConfigureAwait(false);
                        using var sdoc = System.Text.Json.JsonDocument.Parse(sjson);
                        var root = sdoc.RootElement;
                        autoTrackOn = root.TryGetProperty("is_auto_track_on", out var atv) && atv.GetBoolean();
                        autoSteer = root.TryGetProperty("is_auto_steer_on", out var asv) && asv.GetBoolean();
                        jobStarted = root.TryGetProperty("is_job_started", out var jv) && jv.GetBoolean();
                    }
                    catch { }

                    var json = await _trackHttp.GetStringAsync(url + "/api/aog/tracks", ct).ConfigureAwait(false);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("tracks", out var tracks) &&
                        tracks.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        int firstVisible = -1;
                        bool anyActive = false, anyVisible = false;
                        foreach (var t in tracks.EnumerateArray())
                        {
                            bool vis = t.TryGetProperty("is_visible", out var v) && v.GetBoolean();
                            bool act = t.TryGetProperty("is_active", out var a) && a.GetBoolean();
                            int idx = t.TryGetProperty("index", out var ix) ? ix.GetInt32() : -1;
                            if (act) anyActive = true;
                            if (vis) { anyVisible = true; if (firstVisible < 0) firstVisible = idx; }
                        }

                        if (!_suppressAutoSelect && jobStarted)
                        {
                            if (autoTrackOn && !autoSteer && anyVisible)
                            {
                                // AutoTrack prendido + piloto libre → seguir la guía
                                // más cercana (se actualiza al pasar cerca de otra).
                                var body = new System.Net.Http.StringContent(
                                    "{\"cmd\":\"track_nearest\"}",
                                    System.Text.Encoding.UTF8, "application/json");
                                await _trackHttp.PostAsync(url + "/api/aog/guidance/command", body, ct).ConfigureAwait(false);
                            }
                            else if (!anyActive && firstVisible >= 0)
                            {
                                // AutoTrack apagado (selección manual se fija): solo
                                // aseguramos que haya UNA guía activa si no hay ninguna.
                                var body = new System.Net.Http.StringContent(
                                    "{\"index\":" + firstVisible + "}",
                                    System.Text.Encoding.UTF8, "application/json");
                                await _trackHttp.PostAsync(url + "/api/aog/tracks/select", body, ct).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch { /* backend caído / sin lote — reintenta */ }
                try { await System.Threading.Tasks.Task.Delay(1200, ct).ConfigureAwait(false); }
                catch { break; }
            }
        }, ct);
        Closed += (_, _) => { try { _trackCts?.Cancel(); _trackHttp?.Dispose(); } catch { } };
    }

    // Rumbo de la guía activa a partir de la polyline A→B (grados, 0=N, CW).
    private static double ComputeGuideHeadingDeg(GuidanceGeometrySnapshot? snap)
    {
        var pts = snap?.Points;
        if (pts == null || pts.Count < 2) return double.NaN;
        var a = pts[0];
        var b = pts[pts.Count - 1];
        double dE = b.E - a.E, dN = b.N - a.N;
        if (Math.Abs(dE) < 1e-9 && Math.Abs(dN) < 1e-9) return double.NaN;
        double deg = Math.Atan2(dE, dN) * 180.0 / Math.PI; // atan2(E,N) => 0=N, CW
        if (deg < 0) deg += 360.0;
        return deg;
    }

    // Compone el debug de guiado y lo empuja a la BarraSuperior del cockpit (que
    // es la barra VISIBLE, al lado del km/h). El HudBar nativo está oculto en modo
    // full (lo reemplaza el cockpit), así que escribir en _hudTrack no se ve —
    // por eso el string va a _vmSup.DebugText.
    // Formato: "T 123°  G 125°  Δ +2°  ‖ -1 izq  35cm izq"
    private void UpdateHeadingDebug()
    {
        bool hasT = !double.IsNaN(_lastTractorHeadingDeg);
        bool hasG = !double.IsNaN(_lastGuideHeadingDeg);

        // El overlay de la pasada NO se prende y apaga con la guía: queda fijo
        // y cada botón se habilita solo (bindings del BarraDerechaViewModel).
        // Antes aparecía/desaparecía y el operario perdía de vista el
        // manual/auto de secciones, que aplica con o sin guía.

        string s;
        if (!hasG)
        {
            s = hasT ? $"T {_lastTractorHeadingDeg:0}°  ·  sin guía" : "";
        }
        else
        {
            s = hasT
                ? $"T {_lastTractorHeadingDeg:0}°  ·  G {_lastGuideHeadingDeg:0}°"
                : $"G {_lastGuideHeadingDeg:0}°";

            if (hasT)
            {
                double d = _lastGuideHeadingDeg - _lastTractorHeadingDeg;
                while (d > 180) d -= 360;
                while (d < -180) d += 360;
                s += $"  ·  Δ {d:+0;-0;0}°";
            }

            if (!double.IsNaN(_lastPathsAway))
            {
                int n = (int)_lastPathsAway;
                string lr = n < 0 ? " izq" : n > 0 ? " der" : "";
                s += $"  ·  ‖ {n}{lr}";
            }

            if (!double.IsNaN(_lastXteMeters))
            {
                int cm = (int)Math.Round(Math.Abs(_lastXteMeters) * 100.0);
                string lr = _lastXteMeters < 0 ? " izq" : _lastXteMeters > 0 ? " der" : "";
                s += $"  ·  {cm}cm{lr}";
            }
        }

        if (_hudTrack != null) _hudTrack.Text = s;      // HUD nativo (oculto, por si se muestra)
        if (_vmSup != null) _vmSup.DebugText = s;       // barra visible del cockpit
    }

    // ---------- Widgets sobre el mapa --------------------------------------
    //
    // El toggle vive en el Hub y se persiste en overlayPrefs.json. La app
    // WinForms relee ese archivo cada 250 ms; acá se consulta por HTTP con la
    // misma idea: el operario prende el widget desde el Hub y aparece sin
    // reiniciar nada.
    private void SetupMapOverlays()
    {
        if (_mapOverlaysHost == null || _qxMapOverlay == null) return;
        if (_overlayPrefsCts != null) return;   // ya montado

        string baseUrl = DeriveOrigin(App.TargetUrl);
        _qxWidgetClient = new WidgetQuantiXClient(baseUrl);
        _vxStripClient = new VistaXClient(baseUrl);
        // Mismo cliente que usa el Hub: /api/overlays es una sola preferencia.
        _overlaysClient ??= new OverlaysClient(baseUrl);

        _mapOverlaysHost.IsVisible = true;
        UbicarOverlayQx(-1, -1);   // rincón por defecto hasta que llegue la preferencia

        // Barra horizontal de control del motor QuantiX elegido (rediseño
        // 2026-08-05): vive centrada arriba del overlay de la pasada — la
        // zona de la mano — y la maneja el propio overlay (selección, datos y
        // visibilidad). Acá solo se la monta en el canvas y se la ubica.
        if (_qxControlBar == null)
        {
            _qxControlBar = new QuantiXControlBar();
            _mapOverlaysHost.Children.Add(_qxControlBar);
            _qxMapOverlay.Barra = _qxControlBar;
            _qxControlBar.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty || e.Property == IsVisibleProperty) UbicarQxBar();
            };
        }

        if (_vxMapStrip != null)
        {
            // Tocar la franja abre el panel VistaX completo.
            _vxMapStrip.OnTap = () => ShowVistaX();
            // Abajo al centro-izquierda, pegada al borde: reposicionar cuando
            // cambie el tamaño del canvas o el alto de la franja (auto-mini).
            _vxMapStrip.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty) UbicarVxStrip();
                if (e.Property == BoundsProperty) UbicarNudgeOverlay();
            };
            _mapOverlaysHost.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty) UbicarVxStrip();
                if (e.Property == BoundsProperty) UbicarNudgeOverlay();
            };
        }

        // El overlay de la pasada arranca visible, así que hay que centrarlo
        // apenas mide: su ancho depende de cuántos botones entraron y del
        // largo de los títulos, no es fijo.
        if (_nudgeOverlay != null)
        {
            _nudgeOverlay.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty) UbicarNudgeOverlay();
            };
            UbicarNudgeOverlay();
        }

        // Al soltarlo se guarda dónde quedó. El POST hace merge, así que esto
        // no pisa los toggles ni la posición de los otros widgets.
        _qxMapOverlay.OnMovido = pt =>
        {
            var c = _overlaysClient;
            if (c == null) return;
            _ = c.SavePosQxAsync((int)Math.Round(pt.X), (int)Math.Round(pt.Y));
        };

        _overlayPrefsCts = new CancellationTokenSource();
        _ = SeguirPreferenciasOverlaysAsync(_overlayPrefsCts.Token);
    }

    private void UbicarOverlayQx(int x, int y)
    {
        if (_qxMapOverlay == null || _mapOverlaysHost == null) return;
        if (x >= 0 && y >= 0)
        {
            Canvas.SetLeft(_qxMapOverlay, x);
            Canvas.SetTop(_qxMapOverlay, y);
            return;
        }
        // Default: abajo a la izquierda, al lado del menú lateral (140 px) y
        // arriba de la barra inferior. El centro queda libre para el tractor.
        // Ese rincón lo ocupaba el mini-mapa, que se sacó.
        Canvas.SetLeft(_qxMapOverlay, 155);
        double alto = _mapOverlaysHost.Bounds.Height;
        Canvas.SetTop(_qxMapOverlay, alto > 260 ? alto - 235 : 40);
    }

    // Overlay de corrección lateral: flotante y centrado abajo, despegado del
    // borde para no tapar la barra de secciones.
    private Border? _nudgeOverlay;
    private Button? _btnNudgeCentro;

    private void UbicarNudgeOverlay()
    {
        if (_nudgeOverlay == null || _mapOverlaysHost == null) return;
        double hostH = _mapOverlaysHost.Bounds.Height;
        double hostW = _mapOverlaysHost.Bounds.Width;
        if (hostH < 80 || hostW < 200) return;
        double w = _nudgeOverlay.Bounds.Width > 0 ? _nudgeOverlay.Bounds.Width : 220;
        double h = _nudgeOverlay.Bounds.Height > 0 ? _nudgeOverlay.Bounds.Height : 70;
        // La barra NO se centra por su caja: se corre para que el botón
        // CENTRAR quede en el eje del tractor (centro de pantalla, que es
        // donde la cámara clava al vehículo). Los grupos laterales son
        // asimétricos (la derecha con Giro/Auto/Manual/Piloto es más ancha),
        // así que centrar la caja dejaba "Centrar" corrido a la izquierda.
        // El clamp de 150 protege del riel izquierdo; el de la derecha evita
        // que la barra se salga en ventanas angostas (ahí se pierde la
        // alineación exacta, pero la barra entra entera).
        double x = (hostW - w) / 2;
        var cen = _btnNudgeCentro;
        if (cen != null && cen.Bounds.Width > 0)
        {
            var p = cen.TranslatePoint(new Point(cen.Bounds.Width / 2, 0), _nudgeOverlay);
            if (p.HasValue) x = hostW / 2 - p.Value.X;
        }
        x = Math.Max(150, Math.Min(x, hostW - w - 6));
        Canvas.SetLeft(_nudgeOverlay, x);
        // Pegada al borde inferior (intercambio 2026-08-05): la pasada ocupa
        // el lugar que tenía la barra de secciones, y las secciones flotan
        // arriba (SeccionesFloat). El 6 es solo aire contra el borde.
        Canvas.SetTop(_nudgeOverlay, Math.Max(0, hostH - h - 6));
        // El resto de la pila cuelga de esta posición: reubicar juntas.
        UbicarVxStrip();
        UbicarQxBar();
    }

    /// <summary>
    /// Barra de control del motor QuantiX elegido: centrada, apilada JUSTO
    /// arriba del overlay de la pasada (que ya está arriba de las secciones).
    /// Es la franja donde el operario ya opera mirando la línea.
    /// </summary>
    private void UbicarQxBar()
    {
        if (_qxControlBar == null || _mapOverlaysHost == null || !_qxControlBar.IsVisible) return;
        double hostH = _mapOverlaysHost.Bounds.Height;
        double hostW = _mapOverlaysHost.Bounds.Width;
        if (hostH < 80 || hostW < 200) return;
        double w = _qxControlBar.Bounds.Width > 0 ? _qxControlBar.Bounds.Width : 430;
        double h = _qxControlBar.Bounds.Height > 0 ? _qxControlBar.Bounds.Height : 58;

        // Pila de abajo hacia arriba: pasada (borde) → secciones flotantes →
        // franja VistaX (si está) → esta barra. Se apila sobre lo más alto.
        double baseTop = TopeDePilaInferior();
        if (_vxMapStrip != null && _vxMapStrip.IsVisible && _vxMapStrip.Bounds.Height > 0)
        {
            double vt = Canvas.GetTop(_vxMapStrip);
            if (!double.IsNaN(vt)) baseTop = Math.Min(baseTop, vt);
        }

        Canvas.SetLeft(_qxControlBar, Math.Max(150, (hostW - w) / 2));
        Canvas.SetTop(_qxControlBar, Math.Max(0, baseTop - h - 8));
    }

    /// <summary>
    /// Tope actual de la pila de abajo (coordenadas del canvas): lo más alto
    /// entre la barra de la pasada (pegada al borde desde el intercambio del
    /// 2026-08-05) y las secciones flotantes. Todo lo que flote en esa zona se
    /// apila SOBRE este valor — sin esto, la franja VistaX quedaba enterrada
    /// bajo la pasada asomando como "un pedazo de sección" (reporte usuario).
    /// </summary>
    private double TopeDePilaInferior()
    {
        double hostH = _mapOverlaysHost?.Bounds.Height ?? 0;
        double tope = hostH;
        if (_nudgeOverlay != null && _nudgeOverlay.Bounds.Height > 0)
        {
            double t = Canvas.GetTop(_nudgeOverlay);
            if (!double.IsNaN(t)) tope = Math.Min(tope, t);
        }
        var secciones = this.FindControl<Border>("SeccionesFloat");
        if (secciones != null && secciones.IsVisible && secciones.Bounds.Height > 0)
            tope = Math.Min(tope, hostH - 96 - secciones.Bounds.Height);   // 96 = Margin del XAML
        return tope;
    }

    private void UbicarVxStrip()
    {
        if (_vxMapStrip == null || _mapOverlaysHost == null) return;
        double hostH = _mapOverlaysHost.Bounds.Height;
        double hostW = _mapOverlaysHost.Bounds.Width;
        if (hostH < 60 || hostW < 200) return;
        double w = double.IsNaN(_vxMapStrip.Width) ? 300 : _vxMapStrip.Width;
        double h = _vxMapStrip.Bounds.Height > 0 ? _vxMapStrip.Bounds.Height
                 : (double.IsNaN(_vxMapStrip.Height) ? 40 : _vxMapStrip.Height);
        double x = Math.Max(150, (hostW - w) / 2);
        Canvas.SetLeft(_vxMapStrip, x);
        // Sobre la pila (pasada + secciones), no en el borde: ahí ahora vive
        // la barra de la pasada y la enterraba.
        Canvas.SetTop(_vxMapStrip, Math.Max(0, TopeDePilaInferior() - h - 6));
    }

    private async Task SeguirPreferenciasOverlaysAsync(CancellationToken ct)
    {
        bool primera = true;
        while (!ct.IsCancellationRequested)
        {
            OverlayPrefs? prefs = null;
            try { prefs = _overlaysClient == null ? null : await _overlaysClient.GetAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { prefs = null; }

            if (prefs != null)
            {
                bool posicionar = primera;   // la posición guardada se aplica al montar
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_qxMapOverlay == null) return;
                    if (posicionar) UbicarOverlayQx(prefs.QxX, prefs.QxY);

                    bool mostrar = prefs.QxOverlay;
                    if (mostrar != _qxMapOverlay.IsVisible)
                    {
                        _qxMapOverlay.IsVisible = mostrar;
                        // El polling del widget solo corre mientras se ve: si no,
                        // son dos requests por segundo por nada.
                        if (mostrar && _qxWidgetClient != null) _qxMapOverlay.Attach(_qxWidgetClient);
                        else _qxMapOverlay.Detach();
                    }

                    // Franja VistaX: mismo criterio (poll solo mientras se ve).
                    if (_vxMapStrip != null)
                    {
                        bool mostrarVx = prefs.VxOverlay;
                        if (mostrarVx != _vxMapStrip.IsVisible)
                        {
                            _vxMapStrip.IsVisible = mostrarVx;
                            if (mostrarVx && _vxStripClient != null)
                            {
                                _vxMapStrip.Attach(_vxStripClient, DeriveOrigin(App.TargetUrl));
                                UbicarVxStrip();
                            }
                            else _vxMapStrip.Detach();
                        }
                    }
                });
                primera = false;
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(1000), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetupCockpitBars()
    {
        if (_cockpitBarsHost == null) return;

        string baseUrl = DeriveOrigin(App.TargetUrl);
        _cockpitHttp = new HttpClient();
        _cockpitCmd = new GuidanceCommandClient(_cockpitHttp, baseUrl);
        // Ruteo local: los comandos de UI (config, colores, gráficos, banderas…)
        // abren su página HTML en el WebView de PilotX.Desktop o un panel nativo;
        // los de guiado (autosteer, crear guías, secciones, youturn…) siguen al
        // backend por HTTP. Sin esto, todos iban al backend y los de UI no hacían
        // nada (el engine solo implementa guiado).
        _cockpitCmd.LocalHandler = RouteCockpitCommand;

        _vmSup = new BarraSuperiorViewModel(_cockpitCmd);
        _vmDer = new BarraDerechaViewModel(_cockpitCmd);
        _vmAba = new BarraAbajoViewModel(_cockpitCmd);
        _vmIzq = new MenuIzquierdaViewModel(_cockpitCmd);

        if (_barSuperior != null) _barSuperior.DataContext = _vmSup;
        if (_barDerecha  != null) _barDerecha.DataContext  = _vmDer;
        // El wrapper flotante de las secciones necesita el MISMO ViewModel que
        // BarraAbajo: su IsVisible se ata a SeccionesVisible (sin lote no hay
        // secciones, y una tarjeta flotante vacía sería un pastillón fantasma).
        var seccionesFloat = this.FindControl<Border>("SeccionesFloat");
        if (seccionesFloat != null) seccionesFloat.DataContext = _vmAba;
        if (_barAbajo    != null) _barAbajo.DataContext    = _vmAba;
        if (_menuIzq     != null) _menuIzq.DataContext      = _vmIzq;
        // El overlay de la pasada muestra los mismos estados que la barra
        // derecha (piloto on/off, giro, manual/auto de secciones): comparte su
        // ViewModel en vez de duplicar el snapshot.
        if (_nudgeOverlay != null) _nudgeOverlay.DataContext = _vmDer;

        // Ensanchar/angostar el menú izquierdo al abrir/cerrar un submenú
        // (equivalente al resize de la ventana en el Host). Sin ancho extra el
        // submenú queda clippeado a la derecha de la columna principal.
        _vmIzq.PropertyChanged += (_, e) =>
        {
            if (_menuIzq == null) return;
            if (e.PropertyName == nameof(MenuIzquierdaViewModel.OpenSubmenu)
                || e.PropertyName == nameof(MenuIzquierdaViewModel.IsCollapsed))
            {
                // El ancho NO cambia al abrir un submenú, solo al plegar.
                //
                // Antes se ensanchaba de 140 a 316 px en el momento del toque, y
                // ese re-layout en medio del gesto hacía que el botón de plegado
                // quedara bajo el dedo y se comiera el "soltar": un solo toque
                // abría el submenú, lo cerraba y encima plegaba el menú. Para el
                // operario era "no puedo abrir el lote".
                // Ahora la columna del submenú ya está reservada y solo se
                // muestra u oculta su contenido: nada se mueve bajo el dedo.
                _menuIzq.Width = _vmIzq!.IsCollapsed ? MenuIzqCollapsed : MenuIzqExpanded;
            }
        };

        // Poll de estado dedicado para las barras (superior/derecha/abajo leen
        // IsJobStarted, autosteer, secciones, etc.). Mismo :5180 que el mapa.
        _cockpitPoller = new CockpitStateClient(baseUrl, intervalMs: 250);
        _cockpitPoller.SnapshotReceived += snap => Dispatcher.UIThread.Post(() =>
        {
            _vmSup?.Apply(snap);
            _vmDer?.Apply(snap);
            _vmAba?.Apply(snap);
        });
        _cockpitPoller.Start();

        Closed += (_, _) =>
        {
            try { _cockpitPoller?.Dispose(); } catch { }
            try { _cockpitHttp?.Dispose(); } catch { }
        };
    }

    // Ruteo de los comandos de las barras. Devuelve true si se manejó localmente
    // (navegación a página HTML / panel nativo / acción de ventana); false para
    // que el comando siga al backend de guiado (POST /api/aog/guidance/command).
    private bool RouteCockpitCommand(string cmd)
    {
        switch (cmd)
        {
            // ---- Acciones de ventana ----
            case "minimizar": WindowState = WindowState.Minimized; return true;
            case "maximizar":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return true;
            case "apagar": Close(); return true;

            // Modo kiosco ↔ ventana. El cockpit ya arranca a pantalla completa,
            // así que este toggle sirve para lo contrario: achicarlo a una
            // ventana con bordes cuando se trabaja en el taller o el escritorio,
            // y volver a la pantalla completa de cabina.
            case "kiosco": ToggleKiosco(); return true;

            // El "simulador" de este stack es ModSim.exe, un proceso EXTERNO que
            // manda NMEA por UDP (el sim interno del motor no aplica: la
            // posición viene de afuera). El toggle lo abre o lo cierra.
            case "simulador": ToggleModSim(); return true;

            // Reset de fábrica: confirmación nativa acá; el borrado real lo hace
            // el motor (comando reset_all) y después hay que reiniciar.
            case "reset_all": _ = ConfirmarResetAllAsync(); return true;

            // ---- Info de lote/GPS → ventana chica cerrable (HTML) ----
            case "datos_gps":  OpenDialogPage("pages/datos-gps.html",  "Datos GPS",       760, 560); return true;
            case "lote_datos": OpenDialogPage("pages/datos-lote.html", "Datos del lote",  760, 560); return true;
            // ---- Paneles nativos grandes (Hub / Cámaras) ----
            case "hub":        ShowHub();      return true;
            case "webcam":     ShowCamaras();  return true;
            // CoreX del menú izquierdo → dashboard de CoreX (config del sistema:
            // Serial / NTRIP / Red-IP / Módulos). Vive en :5181, servido por
            // CoreX.exe (CoreXWebHost), NO en el Hub :5180. Con CoreX EMBEBIDO
            // en el motor (--corex) ese dashboard no existe todavía: se avisa
            // en criollo en vez de abrir un WebView contra un puerto muerto.
            // El ECU de autosteer queda en 'corex_ecu'.
            case "corex":      _ = AbrirCoreXAsync(); return true;
            case "corex_ecu":  ShowCoreXEcu(); return true;

            // ---- Nueva A/B → flujo en el mapa (toco A, manejo, toco B) ----
            case "track_new_ab":
                StartAbCreate();
                return true;

            // ---- Guías (curva/A+/elegir/importar) → ventana-diálogo HTML ----
            case "pick":
            case "importar_guias":
            case "track_new_curve":
            case "track_new_a":
                OpenDialogPage("pages/tracks.html", "Guías", 680, 520);
                return true;

            // Menú de lote (FormJob) → ventana chica. El submenú LOTE de la barra
            // izquierda hace deep-link a la sub-pantalla vía ?do= (ver lote.js).
            case "lote_menu":
                OpenDialogPage("pages/lote.html", "Lote", 670, 610); return true;
            // Continuar NO abre ventana: acción directa (pedido del usuario —
            // la ventana del diálogo quedaba en blanco porque el centinela de
            // cierre no corre en diálogos, y encima acá no hay nada que elegir:
            // es "abrí el último y listo"). El backend resuelve __resume__.
            case "lote_continuar":
                ContinuarUltimoLote(); return true;
            // "Abrir" va DERECHO al listado de lotes. Antes mandaba
            // lote_menu, que abre el menú entero otra vez: el operario tocaba
            // Abrir y le aparecía una ventana con Abrir/Nuevo/Continuar de nuevo.
            case "lote_abrir":
                OpenDialogPage("pages/lote.html?do=abrir", "Abrir lote", 670, 610); return true;
            case "lote_nuevo":
                OpenDialogPage("pages/lote.html?do=nuevo", "Nuevo lote", 670, 610); return true;
            case "lote_kml":
                OpenDialogPage("pages/lote.html?do=kml", "Lote desde KML", 670, 610); return true;

            // Cerrar lote. Sin este case el comando caía al motor de guiado, que
            // espera "job_close" y no conoce "lote_cerrar": el botón no hacía
            // absolutamente nada y no quedaba ni un error en ningún lado.
            case "lote_cerrar":
                CerrarLote(); return true;

            // Dirección (FormSteer) → ventana propia más grande. ?v= evita que
            // el WebView2 sirva una versión cacheada vieja de la página.
            case "direccion":
                // v=10: layout FormSteer clásico (dos columnas, 2026-07-31).
                OpenDialogPage("pages/direccion.html?v=10", "Dirección — Autoguiado", 1040, 780); return true;

            // ---- Controles de cámara/vista (menú Navegación) — 100% cliente
            // (MapGlSurface), no tocan el motor. Equivalentes a
            // camera.PitchInDegrees/FollowDirectionHint del legacy. ----
            case "v2d":     _mapHost?.SetHeadingUp(true);  _mapHost?.SetPitchDeg(0);   return true;
            case "v3d":     _mapHost?.SetHeadingUp(true);  _mapHost?.SetPitchDeg(-65); return true;
            case "norte2d": _mapHost?.SetHeadingUp(false); _mapHost?.SetPitchDeg(0);   return true;
            case "tilt_up": _mapHost?.TiltBy(+5); return true;
            case "tilt_dn": _mapHost?.TiltBy(-5); return true;
            case "grilla":  _mapHost?.ToggleGrid(); return true;
            case "dia_noche": _mapHost?.ToggleDayNight(); return true;

            // Brillo de PANTALLA (no del render) — mismo mecanismo que el
            // panel Sistema (SistemaClient/api/sistema/brillo). No hay
            // control de brillo en el shader; esto es fiel al legacy
            // (displayBrightness/CBrightness también era de sistema, no del mapa).
            case "brillo_up": AdjustBrightness(+10); return true;
            case "brillo_dn": AdjustBrightness(-10); return true;
        }

        // ---- Comandos que abren una página HTML del Hub en el WebView ----
        string page = cmd switch
        {
            "config_form"       => "pages/config.html",
            "todos_ajustes"     => "pages/ajustes-todos.html",
            "colores"           => "pages/colores.html",
            "colores_sec"       => "pages/colores-secciones.html",
            "mapeo_color"       => "pages/colores-secciones.html",
            "perfil_nuevo"      => "pages/perfiles.html",
            "perfil_cargar"     => "pages/perfiles.html",
            "perfil_gestion"    => "pages/perfiles.html",
            "directorios"       => "pages/config.html",
            "ayuda"             => "pages/ayuda.html",
            "grafico_direccion" => "pages/grafico-direccion.html",
            "grafico_rumbo"     => "pages/grafico-rumbo.html",
            "grafico_xte"       => "pages/grafico-xte.html",
            "chequeo_roll"      => "pages/grafico-correccion.html",
            "suavizar_ab"       => "pages/suavizar-ab.html",
            "corregir_pos"      => "pages/corregir-posicion.html",
            "visor_eventos"     => "pages/eventos.html",
            "bandera"           => "pages/banderas.html",
            "bandera_latlon"    => "pages/banderas.html",
            "lindero"           => "pages/contorno.html",
            "cabecera"          => "pages/cabecera.html",
            "cabecera_avanzada" => "pages/cabecera-lineas.html",
            "tram_crear"        => "pages/tramline.html",
            "importar_guias"    => "pages/tracks.html",
            "pick"              => "pages/tracks.html",
            "sim_coords"        => "pages/sim-coords.html",
            "asistente_direccion" => "pages/config.html",
            "herr_limites"      => "pages/contorno.html",
            // Los dos servicios ya están portados al motor (EngineRecPathService
            // y EngineTramLineService): solo faltaba rutear el botón a su página.
            "ruta_grabada"      => "pages/recpath.html",
            "tram_multi"        => "pages/tramlines.html",
            _ => null
        };
        if (page != null)
        {
            // Todas las pantallas de config/info abren como VENTANA CHICA
            // cerrable (con barra de título + X), no a pantalla completa —
            // así ninguna se confunde con el cierre de la app.
            //
            // El tamaño lo decide TamanoDialogo por página: 820x600 para todo,
            // midiera lo que midiera el contenido, era media pantalla tapada.
            // El 820x600 queda solo como red para las páginas sin medir.
            //
            // Contorno además va con el mapa VIVO. Los demás diálogos lo apagan
            // para no pelear con el WebView2 por el compositor, pero éste se
            // abre justamente para ver los puntos del lindero mientras se
            // maneja: apagárselo lo deja en negro y lo vuelve inútil.
            bool esContorno = page == "pages/contorno.html";
            OpenDialogPage(page, TitleForCommand(cmd), 820, 600, mapaVivo: esContorno);
            return true;
        }

        // Resto → backend de guiado (autosteer, sec_*, uturn, contour, track_*,
        // tracks_off, lote_cerrar, borrar_*, cabecera_onoff, tram_vista, vistas…).
        return false;
    }

    /// <summary>
    /// Cierra el lote activo. Es la misma llamada que hace el botón "Cerrar" de
    /// la pantalla de lote; acá se expone para el submenú de la barra izquierda,
    /// que hasta ahora mandaba el comando al motor de guiado y se perdía.
    /// </summary>
    // Continuar = abrir el último lote usado, sin ventanas: se pliega el menú
    // y se dispara el open; el mapa reacciona solo vía el HUD (y el shape del
    // lote, si lo usaba, lo recarga EngineShapeService al cambiar el field).
    private async void ContinuarUltimoLote()
    {
        if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
        try
        {
            var http = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = DeriveOrigin(App.TargetUrl).TrimEnd('/');
            using var resp = await http.PostAsync(url + "/api/lotes/open?name=__resume__", null).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || body.Contains("\"ok\":false") || body.Contains("\"ok\": false"))
                Console.Error.WriteLine("[Lote] continuar: sin último lote para abrir (" + (int)resp.StatusCode + ")");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Lote] no se pudo continuar: " + ex.Message);
        }
    }

    private async void CerrarLote()
    {
        // Plegar primero: el cierre puede tardar (guarda cobertura y lote) y el
        // menú no tiene por qué quedarse abierto esperando.
        if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
        try
        {
            var http = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = DeriveOrigin(App.TargetUrl).TrimEnd('/');
            using var resp = await http.PostAsync(url + "/api/lotes/close", null).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                Console.Error.WriteLine("[Lote] cerrar devolvió " + (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Lote] no se pudo cerrar: " + ex.Message);
        }
    }

    // ---- Botón CoreX ------------------------------------------------------
    //
    // El dashboard de CoreX (:5181) lo sirve CoreX.exe. Con CoreX embebido en
    // el motor (--corex) los servicios corren pero el panel no existe: se
    // chequea el puerto ANTES de abrir la ventana, y si no contesta se explica
    // qué pasa en vez de mostrar el error crudo del WebView.
    private async Task AbrirCoreXAsync()
    {
        bool vivo = false;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            using var resp = await http.GetAsync("http://127.0.0.1:5181/");
            vivo = resp.IsSuccessStatusCode;
        }
        catch { /* nadie escucha en 5181 */ }

        if (vivo)
        {
            OpenDialogUrl("http://127.0.0.1:5181/", "CoreX", 1000, 720);
            return;
        }

        await MostrarAvisoAsync("Panel de CoreX no disponible",
            "CoreX está corriendo INTEGRADO en el motor de PilotX (broker MQTT, " +
            "bridge de red y puertos serie andan), pero en este modo el panel " +
            "todavía no existe.\n\n" +
            "El panel aparece cuando CoreX corre como programa aparte (CoreX.exe).");
    }

    // ---- Cluster del piloto ------------------------------------------------
    //
    // Comandos del cluster (giro manual / salteo). Van por el mismo canal que
    // las barras si está armado; si no (modo ventana sin cockpit), POST directo.
    private async Task MandarComandoPiloto(string cmd)
    {
        try
        {
            if (_cockpitCmd != null) { await _cockpitCmd.SendAsync(cmd); return; }
            var http = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = DeriveOrigin(App.TargetUrl).TrimEnd('/');
            using var body = new System.Net.Http.StringContent(
                "{\"cmd\":\"" + cmd + "\"}", System.Text.Encoding.UTF8, "application/json");
            using var _ = await http.PostAsync(url + "/api/aog/guidance/command", body);
        }
        catch (Exception ex) { Console.Error.WriteLine("[Piloto] " + cmd + ": " + ex.Message); }
    }

    private Task CambiarSalteo(int delta)
    {
        int n = Math.Clamp(_pcSalteadas + delta, 0, 9);
        if (n == _pcSalteadas) return Task.CompletedTask;
        // Optimista: el snapshot lo confirma en el próximo poll (100 ms).
        _pcSalteadas = n;
        if (_pcSkip != null) _pcSkip.Text = n.ToString(CultureInfo.InvariantCulture);
        return MandarComandoPiloto("uturn_skip_" + n);
    }

    /// <summary>Refresca el cluster desde el HUD (10 Hz). La DISTANCIA se ve
    /// siempre que haya guía; giro y salteo aparecen con el piloto activo (y
    /// se habilitan con el U-turn prendido, que es lo que el motor exige).</summary>
    private void ActualizarClusterPiloto(HudSnapshot s)
    {
        if (_pilotoCluster == null) return;

        // Hay guía = el poller de guidance trae XTE (NaN sin guía activa).
        // Con eso ALCANZA: exigir además lote abierto escondía el cluster en
        // estados válidos (el motor mantiene la guía aunque el lote se cierre,
        // y el operario espera seguir viendo la distancia).
        bool hayGuia = !double.IsNaN(_lastXteMeters);
        bool visible = hayGuia;
        _pilotoCluster.IsVisible = visible;
        // El cluster tapa la franja del lightbar GL y muestra el mismo dato:
        // uno de los dos, nunca ambos.
        _mapHost?.SetLightbarVisible(!visible);
        if (!visible) return;

        // Giro manual y salteo: SIEMPRE junto a la distancia (pedido del
        // usuario 2026-07-31 — la versión condicionada al lindero los hacía
        // desaparecer en lotes sin contorno y quedaba solo el número). Las
        // flechas son el giro MANUAL, siempre operativas — no dependen del
        // U-turn automático, que tiene su propio botón en la barra derecha.
        // Los guards reales (guía, velocidad, lindero para armar el giro) los
        // valida el motor al recibir el comando.
        bool giroVisible = true;
        if (_pcGiroIzq != null) { _pcGiroIzq.IsVisible = giroVisible; _pcGiroIzq.IsEnabled = true; }
        if (_pcGiroDer != null) { _pcGiroDer.IsVisible = giroVisible; _pcGiroDer.IsEnabled = true; }
        var grupoSalteo = this.FindControl<StackPanel>("PcGrupoSalteo");
        if (grupoSalteo != null) grupoSalteo.IsVisible = giroVisible;
        if (_pcSkipMenos != null) _pcSkipMenos.IsEnabled = true;
        if (_pcSkipMas != null) _pcSkipMas.IsEnabled = true;

        // Estado del giro en cabecera (réplica 6.8.5: el número junto al botón
        // Turn del nativo). Armado y esperando → distancia al punto de giro en
        // verde (o rojo si el camino cae fuera del área de giro); girando →
        // "GIRANDO" violeta como el camino en el mapa.
        if (_pcGiroInfo != null)
        {
            var yt = _lastYt;
            if (yt == null || !giroVisible)
            {
                _pcGiroInfo.IsVisible = false;
            }
            else if (yt.Triggered)
            {
                _pcGiroInfo.IsVisible = true;
                _pcGiroInfo.Text = "GIRANDO " + (yt.TurnLeft ? "↰" : "↱");
                _pcGiroInfo.Foreground = new global::Avalonia.Media.SolidColorBrush(
                    global::Avalonia.Media.Color.Parse("#C24FC2"));
            }
            else if (yt.Phase == 10 && yt.OutOfBounds)
            {
                _pcGiroInfo.IsVisible = true;
                _pcGiroInfo.Text = "giro fuera del lote";
                _pcGiroInfo.Foreground = new global::Avalonia.Media.SolidColorBrush(
                    global::Avalonia.Media.Color.Parse("#ED4848"));
            }
            else if (yt.Phase == 10 && yt.DistanceM >= 0)
            {
                _pcGiroInfo.IsVisible = true;
                _pcGiroInfo.Text = "giro " + (yt.TurnLeft ? "↰" : "↱") + " en " +
                    yt.DistanceM.ToString("0", CultureInfo.InvariantCulture) + " m";
                _pcGiroInfo.Foreground = new global::Avalonia.Media.SolidColorBrush(
                    global::Avalonia.Media.Color.Parse("#2F8A27"));
            }
            else
            {
                _pcGiroInfo.IsVisible = false;
            }
        }

        // Salteo mostrado en guías SALTEADAS (0 = contigua); el motor habla en
        // ancho (width = salteadas + 1). Igual que el selector de la barra.
        int salteadas = Math.Clamp(s.YouTurnSkipWidth - 1, 0, 9);
        if (salteadas != _pcSalteadas)
        {
            _pcSalteadas = salteadas;
            if (_pcSkip != null) _pcSkip.Text = salteadas.ToString(CultureInfo.InvariantCulture);
        }

        // Distancia a la línea. El XTE fino viene del poller de guidance
        // (_lastXteMeters); el del HUD (CrossTrackErrorM) es respaldo.
        double xte = !double.IsNaN(_lastXteMeters) ? _lastXteMeters : s.CrossTrackErrorM;
        if (double.IsNaN(xte))
        {
            if (_pcXte != null) _pcXte.Text = "—";
            if (_pcXteFlecha != null) _pcXteFlecha.Text = "";
            return;
        }

        double cm = Math.Abs(xte) * 100.0;
        if (_pcXte != null && _pcXteUnidad != null)
        {
            if (cm < 100)
            {
                _pcXte.Text = cm.ToString("0", CultureInfo.InvariantCulture);
                _pcXteUnidad.Text = "cm";
            }
            else
            {
                _pcXte.Text = (cm / 100.0).ToString("0.0", CultureInfo.InvariantCulture);
                _pcXteUnidad.Text = "m";
            }
            // Mismos umbrales que el lightbar: verde centrado, amarillo, rojo.
            var brush = cm < 5 ? "#4ABA3E" : (cm < 20 ? "#D9A916" : "#ED4848");
            _pcXte.Foreground = new global::Avalonia.Media.SolidColorBrush(
                global::Avalonia.Media.Color.Parse(brush));
        }
        // Flecha hacia dónde corregir: lado OPUESTO al desvío (criterio lightbar).
        if (_pcXteFlecha != null)
            _pcXteFlecha.Text = cm < 2 ? "" : (xte > 0 ? "◀" : "▶");
    }

    // ---- Modo kiosco ↔ ventana --------------------------------------------
    //
    // El cockpit arranca borderless a pantalla completa (AjustarAPantallaCompleta).
    // Este toggle lo achica a una ventana normal CON decoraciones —para taller y
    // escritorio, donde vivir a pantalla completa molesta— y lo devuelve.
    private bool _modoVentana;

    private void ToggleKiosco()
    {
        try
        {
            if (_modoVentana)
            {
                // → volver a la pantalla completa de cabina
                SystemDecorations = SystemDecorations.None;
                WindowState = WindowState.Normal;
                AjustarAPantallaCompleta();
                _modoVentana = false;
            }
            else
            {
                // → ventana con bordes, movible y redimensionable
                SystemDecorations = SystemDecorations.Full;
                WindowState = WindowState.Normal;
                CanResize = true;
                Width = 1080;
                Height = 720;
                _modoVentana = true;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[Kiosco] " + ex.Message); }
    }

    // ---- Simulador (ModSim.exe) -------------------------------------------
    //
    // En este stack no hay sim interno: la posición viene de ModSim.exe, un
    // proceso aparte que manda NMEA por UDP (su config vive en %LOCALAPPDATA%\
    // ModSim). "Simulador" acá significa abrir o cerrar ESE proceso.
    private void ToggleModSim()
    {
        try
        {
            var corriendo = System.Diagnostics.Process.GetProcessesByName("ModSim");
            if (corriendo.Length > 0)
            {
                foreach (var p in corriendo)
                {
                    // Primero el cierre prolijo (es una app con ventana y guarda
                    // su config al salir); si no responde, abajo.
                    try { if (!p.CloseMainWindow()) p.Kill(); }
                    catch { try { p.Kill(); } catch { } }
                    finally { p.Dispose(); }
                }
                return;
            }

            string exe = BuscarModSim();
            if (exe == null)
            {
                Console.Error.WriteLine("[Sim] ModSim.exe no está junto al programa");
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? "",
            });
        }
        catch (Exception ex) { Console.Error.WriteLine("[Sim] " + ex.Message); }
    }

    private static string? BuscarModSim()
    {
        // Deploy real: Build\Desktop\PilotX.Desktop.exe con Build\ModSim.exe al
        // lado del árbol. En dev (bin\Debug) se prueban las mismas alturas.
        string baseDir = AppContext.BaseDirectory;
        foreach (string rel in new[] { "ModSim.exe", @"..\ModSim.exe", @"..\..\ModSim.exe" })
        {
            try
            {
                string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, rel));
                if (System.IO.File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    // ---- Reset de fábrica (reset_all) -------------------------------------
    //
    // La confirmación es nativa y DOBLE-paso implícito: el diálogo explica que
    // borra TODA la configuración y que la app se cierra. El borrado real lo
    // hace el motor (comando reset_all, que además rechaza con lote abierto);
    // acá solo se confirma, se manda y se cierra para que el reinicio traiga
    // los defaults.
    private async Task ConfirmarResetAllAsync()
    {
        bool ok = await MostrarConfirmacionAsync(
            "Restablecer TODO",
            "Se borra TODA la configuración (vehículo, implemento, dirección, " +
            "pantalla) y PilotX se cierra. Los lotes NO se tocan.\n\n" +
            "Al volver a abrir, arranca con los valores de fábrica.");
        if (!ok) return;

        try
        {
            var http = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = DeriveOrigin(App.TargetUrl).TrimEnd('/');
            using var body = new System.Net.Http.StringContent(
                "{\"cmd\":\"reset_all\"}", System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(url + "/api/aog/guidance/command", body);
            string json = await resp.Content.ReadAsStringAsync();

            if (!json.Contains("\"ok\":true"))
            {
                // Única causa esperable de rechazo: lote abierto (mismo guard
                // que el nativo). Avisar en vez de cerrar sin haber borrado.
                await MostrarAvisoAsync("No se pudo restablecer",
                    "Cerrá el lote primero y volvé a intentar.");
                return;
            }

            await MostrarAvisoAsync("Listo",
                "Configuración borrada. PilotX se cierra ahora: reiniciá la " +
                "pantalla (o volvé a abrir PilotX) y arranca con los valores " +
                "de fábrica.");
            Close();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[ResetAll] " + ex.Message);
            await MostrarAvisoAsync("No se pudo restablecer",
                "Sin conexión con el motor de PilotX. Detalle: " + ex.Message);
        }
    }

    /// <summary>Diálogo nativo de confirmación (dos botones grandes, para
    /// guantes). Devuelve true solo si el operario tocó el botón rojo.</summary>
    private async Task<bool> MostrarConfirmacionAsync(string titulo, string mensaje)
    {
        var tcs = new TaskCompletionSource<bool>();
        var win = ArmarDialogoBase(titulo, mensaje, out var fila);

        var btnNo = BotonDialogo("Cancelar", "#FFFFFF", "#101612");
        btnNo.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        var btnSi = BotonDialogo("Borrar todo", "#ED4848", "#FFFFFF");
        btnSi.Click += (_, _) => { tcs.TrySetResult(true); win.Close(); };
        fila.Children.Add(btnNo);
        fila.Children.Add(btnSi);

        win.Closed += (_, _) => tcs.TrySetResult(false);
        await win.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task MostrarAvisoAsync(string titulo, string mensaje)
    {
        var win = ArmarDialogoBase(titulo, mensaje, out var fila);
        var btn = BotonDialogo("Entendido", "#4ABA3E", "#FFFFFF");
        btn.Click += (_, _) => win.Close();
        fila.Children.Add(btn);
        await win.ShowDialog(this);
    }

    private static Window ArmarDialogoBase(string titulo, string mensaje,
                                           out global::Avalonia.Controls.StackPanel filaBotones)
    {
        var stack = new global::Avalonia.Controls.StackPanel { Spacing = 12 };
        stack.Children.Add(new global::Avalonia.Controls.TextBlock
        {
            Text = titulo,
            FontSize = 22,
            FontWeight = global::Avalonia.Media.FontWeight.Bold,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        });
        stack.Children.Add(new global::Avalonia.Controls.TextBlock
        {
            Text = mensaje,
            FontSize = 15,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        });
        filaBotones = new global::Avalonia.Controls.StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new global::Avalonia.Thickness(0, 8, 0, 0),
        };
        stack.Children.Add(filaBotones);

        return new Window
        {
            Title = titulo,
            SizeToContent = global::Avalonia.Controls.SizeToContent.Height,
            Width = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = global::Avalonia.Media.Brushes.White,
            Content = new global::Avalonia.Controls.Border
            {
                Padding = new global::Avalonia.Thickness(20),
                Child = stack,
            },
        };
    }

    private static global::Avalonia.Controls.Button BotonDialogo(string texto, string fondo, string texto2)
    {
        return new global::Avalonia.Controls.Button
        {
            Content = texto,
            FontSize = 16,
            Padding = new global::Avalonia.Thickness(22, 12),
            Background = new global::Avalonia.Media.SolidColorBrush(
                global::Avalonia.Media.Color.Parse(fondo)),
            Foreground = new global::Avalonia.Media.SolidColorBrush(
                global::Avalonia.Media.Color.Parse(texto2)),
            CornerRadius = new global::Avalonia.CornerRadius(8),
        };
    }

    // ---- Creación de guía A/B en el mapa (toco A, manejo, toco B) ----
    private bool _suppressAutoSelect;

    private void StartAbCreate()
    {
        if (_abCreatePanel == null) return;
        _abStep = 0;
        _suppressAutoSelect = true;   // que el auto-select no pise la guía nueva
        _mapHost?.BeginAbCreation();
        if (_abCreateHint != null) _abCreateHint.Text = "Ubicá el tractor en el inicio y tocá A";
        if (_abCreateMark != null) _abCreateMark.Content = "Marcar A";
        _abCreatePanel.IsVisible = true;
    }

    private async void OnAbCreateMark(object? sender, RoutedEventArgs e)
    {
        var (pe, pn, ok) = await GetPivotAsync();
        if (!ok) return;
        if (_abStep == 0)
        {
            // Marcar A: fija el punto A en la posición actual del tractor.
            _abAe = pe; _abAn = pn;
            _mapHost?.SetAbPointA(pe, pn);
            _abStep = 1;
            if (_abCreateHint != null) _abCreateHint.Text = "Manejá hasta el final y tocá B";
            if (_abCreateMark != null) _abCreateMark.Content = "Marcar B";
        }
        else
        {
            // Marcar B: fija B, calcula el rumbo A→B y crea la guía.
            double dx = pe - _abAe, dy = pn - _abAn;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 1.0)
            {
                if (_abCreateHint != null) _abCreateHint.Text = "Manejá un poco más y tocá B";
                return;
            }
            double headingDeg = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            if (headingDeg < 0) headingDeg += 360.0;
            if (_cockpitCmd != null)
                await _cockpitCmd.SendAsync("track_ab_here_" +
                    headingDeg.ToString("0.####", CultureInfo.InvariantCulture));
            FinishAbCreate();
        }
    }

    private void OnAbCreateCancel(object? sender, RoutedEventArgs e) => FinishAbCreate();

    private void FinishAbCreate()
    {
        _abStep = 0;
        _mapHost?.EndAbCreation();
        if (_abCreatePanel != null) _abCreatePanel.IsVisible = false;
        // Reactivar el auto-select recién después de un ratito, para no pisar
        // la guía que acabamos de crear (el backend tarda en reflejarla).
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2500);
            _suppressAutoSelect = false;
        });
    }

    // Lee la posición actual del tractor (pivote) directo del backend — robusto,
    // no depende del timing del snapshot del HUD.
    private async System.Threading.Tasks.Task<(double e, double n, bool ok)> GetPivotAsync()
    {
        try
        {
            var http = _trackHttp ?? new System.Net.Http.HttpClient();
            string url = DeriveOrigin(App.TargetUrl).TrimEnd('/') + "/api/aog/state";
            var json = await http.GetStringAsync(url);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            double e = doc.RootElement.TryGetProperty("pivot_easting", out var pe) ? pe.GetDouble() : 0;
            double n = doc.RootElement.TryGetProperty("pivot_northing", out var pn) ? pn.GetDouble() : 0;
            return (e, n, true);
        }
        catch { return (0, 0, false); }
    }

    // Título humano para la ventana-diálogo de cada comando de pantalla.
    private static string TitleForCommand(string cmd) => cmd switch
    {
        "config_form" or "direccion" or "directorios" or "asistente_direccion" => "Configuración",
        "todos_ajustes"     => "Todos los ajustes",
        "colores"           => "Colores",
        "colores_sec" or "mapeo_color" => "Colores de sección",
        "perfil_nuevo" or "perfil_cargar" or "perfil_gestion" => "Perfiles",
        "ayuda"             => "Ayuda",
        "grafico_direccion" => "Gráfico dirección",
        "grafico_rumbo"     => "Gráfico rumbo",
        "grafico_xte"       => "Gráfico XTE",
        "chequeo_roll"      => "Chequeo de roll",
        "suavizar_ab"       => "Suavizar AB",
        "corregir_pos"      => "Corregir posición",
        "visor_eventos"     => "Eventos",
        "bandera" or "bandera_latlon" => "Banderas",
        "lindero" or "herr_limites" => "Contorno",
        "cabecera"          => "Cabecera",
        "cabecera_avanzada" => "Cabecera avanzada",
        "tram_crear"        => "Tramline",
        "sim_coords"        => "Coordenadas simulador",
        "ruta_grabada"      => "Rutas grabadas",
        "tram_multi"        => "Tramlines",
        _ => "PilotX"
    };

    // ---------- HUD --------------------------------------------------------

    private void OnHudSnapshot(HudSnapshot s)
    {
        _lastPivotE = s.PivotEasting;
        _lastPivotN = s.PivotNorthing;
        Dispatcher.UIThread.Post(() =>
        {
            // El HUD llega siempre (nunca se pausa), así que es el mejor lugar
            // para reafirmar el estado del mapa y que no quede trabado.
            ReconciliarMapa();
            _lastFieldDir = s.CurrentFieldDirectory;
            CerrarDialogoSiCambioElLote(s.CurrentFieldDirectory);
            CerrarDialogoSiHayGuiaNueva(s.TracksTotal, s.TrackIdx);
            if (_hudSpeed   != null) _hudSpeed.Text   = s.AvgSpeed.ToString("0.0", CultureInfo.InvariantCulture);
            ActualizarClusterPiloto(s);
            double deg = (s.Heading * 180.0 / Math.PI) % 360.0;
            if (deg < 0) deg += 360.0;
            if (_hudHeading != null) _hudHeading.Text = deg.ToString("0", CultureInfo.InvariantCulture);
            _lastTractorHeadingDeg = deg;
            UpdateHeadingDebug();
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

            // Push al render nativo del mapa principal.
            _mapHost?.OnSnapshot(s);
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
        // ?widget=1, igual que OpenDialogPage y por la misma doctrina: abierto
        // desde PilotX el operario no vino a NAVEGAR el Hub, vino a hacer una
        // cosa y volver al lote con la flecha "<-". Sin esto, cada "Configurar"
        // de un panel nativo cargaba la página con la barra lateral del Hub
        // entera — "se abrió el Hub como una ventana" (reporte 2026-08-05) —
        // y desde esa barra se puede terminar en cualquier página, lejos del
        // camino de vuelta.
        full += (full.IndexOf('?') >= 0 ? "&" : "?") + "widget=1";
        // Lazy: si no hay WebView, lo crea; si ya hay uno abierto, solo cambia
        // la URL. Show con back button = true: el operario ve la flecha "<-"
        // arriba a la izq. para volver al mapa.
        ShowWebView(full, showBackButton: true);
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
