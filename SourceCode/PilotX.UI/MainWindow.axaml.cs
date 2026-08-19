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

    // FlowX nativo. El monitor (FlowXPanel) reemplaza la parte cabin-critical
    // de pages/flowx.html y el EDITOR (FlowXEditorPanel) reemplaza el resto:
    // desde 2026-08-16 FlowX no abre WebView para nada.
    private FlowXPanel? _flowXHost;
    private FlowXEditorPanel? _flowXEditorHost;
    private FlowXClient? _flowXClient;

    // SectionX nativo (live-only). Chip de estado del bridge + grilla de
    // secciones (consume HudSnapshot). Editor de mapeo + test de reles +
    // debug MQTT siguen en HTML (lazy WebView via boton "Configurar").
    private SectionXPanel? _sectionXHost;
    private SectionXClient? _sectionXClient;

    // QuantiX nativo (Monitor tab live-only). Ver dosis real/target + PWM +
    // estado PID por motor.
    private QuantiXPanel? _quantiXHost;
    private QuantiXClient? _quantiXClient;

    // EDITOR de QuantiX, NATIVO desde 2026-08-15 (18vo port): Siembra,
    // Motores, Shape, PID live, Calibracion y Prueba. Antes "Configurar"
    // navegaba a pages/quantix.html y eso despertaba Chromium entero.
    private QuantiXEditorPanel? _quantiXEditorHost;
    private QuantiXEditorClient? _quantiXEditorClient;

    // Widgets SOBRE el mapa (los que el operario prende desde el Hub). Viven
    // en un Canvas encima del mapa y se arrastran a mano; la posición se
    // guarda en overlayPrefs.json, el mismo archivo que usa la app WinForms.
    // (_overlaysClient se comparte con el Hub nativo — es el mismo /api/overlays)
    private Canvas? _mapOverlaysHost;
    private QuantiXMapOverlay? _qxMapOverlay;
    private FlowXMapOverlay? _fxMapOverlay;
    private HttpClient? _fxOverlayHttp;
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

    // EDITOR de VistaX, NATIVO desde 2026-08-16 (29no port): Insumo &
    // calibracion, Implemento y Config. Antes "Configurar" navegaba a
    // pages/vistax.html y era el ultimo motivo por el que VistaX levantaba
    // Chromium. Reusa el MISMO VistaXClient del monitor (es stateless).
    private VistaXEditorPanel? _vistaXEditorHost;

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

    // Nodos overlay (12vo port, cerrado). Reemplazo nativo de pages/nodos.html:
    // tabs + tabla + banner + acciones de curado (aceptar/ignorar/renombrar/
    // eliminar/pin) + diagnostico MQTT. Ya no abre la pagina en WebView.
    private NodosPanel? _nodosHost;

    // Actualizar overlay (13vo port). Reemplazo nativo de pages/actualizar.html:
    // self-update PilotX via OrbitX OTA. Polling 1s; acciones POST check/download/apply.
    private ActualizarPanel? _actualizarHost;
    private UpdateClient? _updateClient;

    // Camaras overlay (14vo port). Reemplazo nativo (parcial) de pages/camaras.html:
    // Tab Monitor con snapshot JPEG polling. Config (forms) sigue en HTML.
    private CamarasPanel? _camarasHost;
    private CamarasClient? _camarasClient;

    // Sonidos nativo (port de pages/sonidos.html, COMPLETO). Config de las
    // alarmas sonoras + activas + probar + subir un .wav propio: ya no queda
    // nada de esta pantalla en HTML, asi que Sonidos no despierta Chromium.
    // El que SUENA sigue siendo SoundAlarmPoller, aparte.
    private SonidosPanel? _sonidosHost;
    private SonidosClient? _sonidosClient;

    // Guías nativo (15vo port). Reemplaza tracks.html en el flujo de labor:
    // el único diálogo que despertaba Chromium MANEJANDO. La página HTML
    // sigue para el Hub remoto/celular.
    private GuiasPanel? _guiasHost;

    // Lote nativo (16vo port, ex lote.html). Mismo criterio.
    private LotePanel? _loteHost;
    private DireccionPanel? _direccionHost;

    // Contorno (lindero) nativo, ex contorno.html: la ÚNICA página que se abría
    // con el mapa vivo detrás. Card chica sobre el mapa; la página HTML queda
    // para el Hub remoto/celular/Android.
    private ContornoPanel? _contornoHost;
    private ContornoClient? _contornoClient;

    // Tramlines nativo, ex tramline.html (FormTram): la vista previa de las
    // huellas se dibuja sobre el MAPA, así que la ventana HTML tapaba justo lo
    // único que hay para mirar. Card chica sobre el mapa vivo; la página HTML
    // queda para el Hub remoto/celular/Android.
    private TramSimplePanel? _tramSimpleHost;
    private TramSimpleClient? _tramSimpleClient;

    // Suavizar AB nativo, ex suavizar-ab.html (FormSmoothAB): la vista previa
    // de la curva suavizada también se dibuja sobre el MAPA (curve.smooList),
    // así que la ventana HTML tapaba lo único que hay para mirar mientras se
    // sube el nivel. Card chica sobre el mapa vivo; la página HTML queda para
    // el Hub remoto/celular/Android. Sin client propio: es un solo POST al
    // /api/aog/guidance/command de siempre, va por el HttpClient compartido.
    private SuavizarAbPanel? _suavizarAbHost;

    // Corregir posición nativa, ex corregir-posicion.html (FormShiftPos): mueve
    // la posición percibida de la máquina (corrimiento de deriva GPS), o sea
    // que el operario la usa MIRANDO el mapa para ver si la pasada quedó donde
    // va. La ventana HTML tapaba justo eso. Card chica sobre el mapa vivo; la
    // página HTML queda para el Hub remoto/celular/Android.
    private CorregirPosicionPanel? _corregirPosHost;
    private ShiftPosClient? _shiftPosClient;

    // Rutas grabadas nativo, ex recpath.html (FormRecordPicker + FormRecordName):
    // el operario elige el camino que la máquina repite sola, y si es el que
    // quería se ve EN EL MAPA — que era justo lo que tapaba la ventana HTML.
    // Card chica sobre el mapa vivo; la página HTML queda para el Hub
    // remoto/celular/Android.
    private RecPathPanel? _recPathHost;
    private RecPathClient? _recPathClient;

    // Cabecera nativa, ex cabecera.html: el diálogo HTML tapaba y APAGABA el
    // mapa justo donde el operario quiere ver la franja dibujándose. Card chica
    // sobre el mapa vivo; la página HTML queda para el Hub remoto/celular.
    private CabeceraPanel? _cabeceraHost;
    private HeadlandClient? _cabeceraClient;

    // Cabecera por líneas nativa, ex cabecera-lineas.html (FormHeadAche): el
    // último diálogo del flujo de labor que despertaba Chromium encima del
    // mapa. Card con lienzo propio; la página HTML queda para el Hub remoto/
    // celular/Android.
    private CabeceraLineasPanel? _cabLineasHost;
    private CabeceraLineasClient? _cabLineasClient;

    // Tramlines (multi) nativo, ex tramlines.html (FormTramLine): el
    // constructor de huellas por guía, con el corte de 3 toques que se marca
    // con el dedo sobre el dibujo. Card con lienzo propio; la página HTML
    // queda para el Hub remoto/celular/Android.
    private TramMultiPanel? _tramMultiHost;
    private TramMultiClient? _tramMultiClient;

    // CONFIGURACIÓN nativa: shell del porteo de pages/config.html (menú lateral
    // por grupos + pestañas + footer). Hoy trae "Resumen"; las pestañas que
    // faltan y los módulos embebidos siguen abriéndose por WebView desde el
    // propio menú del panel. La página HTML queda para la PWA del celular.
    private ConfigPanel? _configHost;
    private ConfigVehiculoClient? _configClient;

    // Visor de eventos nativo, ex eventos.html (FormEventViewer): el registro
    // de la sesión + el histórico. Sin polling — carga al abrir y con el botón
    // "Actualizar". La página HTML queda para el Hub remoto/celular.
    private EventosPanel? _eventosHost;
    private EventosClient? _eventosClient;

    // Calculadora de siembra nativa, ex calculadora-siembra.html: las cuatro
    // cuentas de gruesa (densidad, prueba de campo, PMS y motor). La página
    // HTML queda para el Hub remoto/celular.
    private CalculadoraSiembraPanel? _calculadoraHost;
    private CalculadoraSiembraClient? _calculadoraClient;

    // Perfiles nativo, ex perfiles.html: un perfil = TODAS las configuraciones
    // del vehículo, así que la pantalla es DESTRUCTIVA (crear/cargar/copiar/
    // proteger/borrar). Polling 5 s que corta el Detach. La página HTML queda
    // para el Hub remoto/celular.
    private PerfilesPanel? _perfilesHost;
    private PerfilesClient? _perfilesClient;

    // Firmwares nativo, ex firmwares.html: catálogo local de .bin para cargar
    // desde USB sin internet. La página HTML queda para el Hub remoto/celular.
    private FirmwaresPanel? _firmwaresHost;
    private FirmwaresClient? _firmwaresClient;

    // Banderas nativo, ex banderas.html (FormFlags + FormEnterFlag): se marca
    // una piedra o un pozo MANEJANDO y la lista da la distancia en vivo, o sea
    // que lo que hay que ver es el mapa — que la ventana HTML tapaba. Poll de
    // 500 ms; el ciclo de vida es Abrir()/Cerrar(), no Attach/IsVisible.
    private BanderasPanel? _banderasHost;
    private BanderasClient? _banderasClient;

    // Los CUATRO gráficos nativos, ex grafico-direccion.html / grafico-rumbo.html
    // / grafico-xte.html / grafico-correccion.html (ex FormGraphSteer y
    // hermanas): se miran mientras la máquina anda, así que van sobre el mapa
    // vivo. Comparten UN SOLO GraficosClient — es stateless (solo GETs) y no
    // tiene sentido tener cuatro pools de conexiones contra el mismo host. Las
    // páginas HTML quedan para el Hub remoto/celular.
    private GraficoDireccionPanel?  _grafDireccionHost;
    private GraficoRumboPanel?      _grafRumboHost;
    private GraficoXtePanel?        _grafXteHost;
    private GraficoCorreccionPanel? _grafCorreccionHost;
    private GraficosClient?         _graficosClient;

    // OrbitX nativo, ex orbitx.html: vinculación del tractor con el cloud
    // (código de pareo, estado del sync). La página HTML queda para el Hub
    // remoto/celular.
    private OrbitXPanel? _orbitXHost;
    private OrbitXPanelClient? _orbitXClient;

    // Red WiFi nativa, ex wifi.html: el WiFi PROPIO de la pantalla (escanear,
    // conectar, olvidar). La página HTML queda para el Hub remoto/celular.
    private WifiPanel? _wifiHost;
    private RedWifiClient? _wifiClient;

    // Debug nativo, ex debug.html: el log unificado de todos los servicios.
    // La página HTML queda para el Hub remoto/celular.
    private DebugPanel? _debugHost;
    private DebugClient? _debugClient;

    // Catálogo de insumos nativo, ex insumos.html: lo comparten VistaX y los
    // demás productos. La página HTML queda para el Hub remoto/celular.
    private InsumosPanel? _insumosHost;
    private InsumosClient? _insumosClient;

    // Mapas nativo, ex mapas.html: preview y exportación de los mapas del lote.
    // La página HTML queda para el Hub remoto/celular.
    private MapasPanel? _mapasHost;
    private MapasClient? _mapasClient;

    // Detalle de nodo nativo, ex nodo-detalle.html: la matriz wifi/mqtt/target,
    // el OTA y los comandos de UN nodo. Se entra SIEMPRE desde una fila de
    // Nodos (el UID sale del announcement MQTT, nunca se escribe a mano). OJO:
    // su Detach() apaga el polling y NO cancela un OTA en curso — el flasheo lo
    // manejan el nodo y el coordinator, no esta pantalla.
    private NodoDetallePanel? _nodoDetalleHost;
    private NodoDetalleClient? _nodoDetalleClient;

    public MainWindow()
    {
        InitializeComponent();

        Title = App.WindowTitle;

        // Modo kiosko (PILOTX_KIOSKO=1, lo setea la sesión de cabina Linux):
        // la ventana NO se puede cerrar — ni por el window manager (Alt-F4 de
        // un WM ajeno, wmctrl), ni por Escape, ni por ningún Close() de UI.
        // La única salida es apagar/reiniciar desde SISTEMA, que termina el
        // proceso por ExecutePowerAction sin pasar por Closing.
        if (Environment.GetEnvironmentVariable("PILOTX_KIOSKO") == "1")
            Closing += (_, e) => e.Cancel = true;

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
        // ✕ propio de cada card (pedido 2026-08-18). La flecha "←" de la
        // esquina sigue funcionando; esto agrega la salida donde el operario
        // ya está mirando, sin cruzar la pantalla.
        if (_fieldDataHost != null) _fieldDataHost.OnRequestCerrar = () => CloseFieldData();
        if (_gpsDataHost   != null) _gpsDataHost.OnRequestCerrar   = () => CloseGpsData();
        // Y se pueden CORRER agarrandolas del header (pedido 2026-08-18): la
        // card queda centrada justo arriba del tractor. Doble toque en el
        // header la devuelve al centro.
        PanelArrastrable.Habilitar(_fieldDataHost);
        PanelArrastrable.Habilitar(_gpsDataHost);
        _stormXHost      = this.FindControl<StormXPanel>("StormXHost");
        _flowXHost       = this.FindControl<FlowXPanel>("FlowXHost");
        _flowXEditorHost = this.FindControl<FlowXEditorPanel>("FlowXEditorHost");
        _sectionXHost    = this.FindControl<SectionXPanel>("SectionXHost");
        _quantiXHost     = this.FindControl<QuantiXPanel>("QuantiXHost");
        _quantiXEditorHost = this.FindControl<QuantiXEditorPanel>("QuantiXEditorHost");
        _vistaXHost      = this.FindControl<VistaXPanel>("VistaXHost");
        _vistaXEditorHost = this.FindControl<VistaXEditorPanel>("VistaXEditorHost");
        _coreXEcuHost    = this.FindControl<CoreXEcuPanel>("CoreXEcuHost");
        _cabinaAlarmasHost = this.FindControl<CabinaAlarmasOverlay>("CabinaAlarmasHost");
        _hubHost           = this.FindControl<HubPanel>("HubHost");
        _nodosHost         = this.FindControl<NodosPanel>("NodosHost");
        _actualizarHost    = this.FindControl<ActualizarPanel>("ActualizarHost");
        _camarasHost       = this.FindControl<CamarasPanel>("CamarasHost");
        _sonidosHost       = this.FindControl<SonidosPanel>("SonidosHost");
        _eventosHost       = this.FindControl<EventosPanel>("EventosHost");
        _calculadoraHost   = this.FindControl<CalculadoraSiembraPanel>("CalculadoraHost");
        _perfilesHost      = this.FindControl<PerfilesPanel>("PerfilesHost");
        _firmwaresHost     = this.FindControl<FirmwaresPanel>("FirmwaresHost");
        _banderasHost      = this.FindControl<BanderasPanel>("BanderasHost");
        _grafDireccionHost = this.FindControl<GraficoDireccionPanel>("GrafDireccionHost");
        _grafRumboHost     = this.FindControl<GraficoRumboPanel>("GrafRumboHost");
        _grafXteHost       = this.FindControl<GraficoXtePanel>("GrafXteHost");
        _grafCorreccionHost= this.FindControl<GraficoCorreccionPanel>("GrafCorreccionHost");
        _orbitXHost        = this.FindControl<OrbitXPanel>("OrbitXHost");
        _wifiHost          = this.FindControl<WifiPanel>("WifiHost");
        _debugHost         = this.FindControl<DebugPanel>("DebugHost");
        _insumosHost       = this.FindControl<InsumosPanel>("InsumosHost");
        _mapasHost         = this.FindControl<MapasPanel>("MapasHost");
        _nodoDetalleHost   = this.FindControl<NodoDetallePanel>("NodoDetalleHost");
        _configHost        = this.FindControl<ConfigPanel>("ConfigHost");
        if (_configHost != null)
        {
            _configHost.OnRequestCerrar = () => CloseConfig();
            _configHost.Aviso += MostrarToast;
            // Pestaña sin portar / módulos: se cierra el panel y se abre la
            // página del Hub en el WebView (NavigateTo ya agrega ?widget=1).
            _configHost.OnRequestHtml = ruta =>
            {
                CloseConfig();
                NavigateTo(ruta);
            };
            // Grilla de módulos de la Configuración → PANEL NATIVO.
            // Antes esa lista era el menú HTML de config.html y abría la
            // versión web de pantallas que ya existen en Avalonia: dos puertas
            // a la misma cosa, y la que veías dependía de por dónde entraras
            // (reporte 2026-08-16). Acá vive el único mapeo clave→panel; si
            // una clave no está, el módulo NO se ofrece nativo en la grilla.
            // No se reusa RouteCockpitCommand a propósito: ese router decide
            // qué comandos NO viajan al motor de guiado, y meterle claves de
            // producto ("stormx", "sectionx") sería cambiarle el contrato.
            _configHost.OnRequestPanelNativo = clave =>
            {
                switch (clave)
                {
                    // Los Show* de acá abajo YA cierran la Configuración
                    // (todos la esconden en su bloque "solo un overlay a la
                    // vez"), por eso no hace falta un CloseConfig previo.
                    case "hub":            ShowHub();                  break;
                    case "quantix":        ShowQuantiXEditor();        break;
                    case "vistax":         ShowVistaXEditor();         break;
                    case "flowx":          ShowFlowXEditor();          break;
                    case "sectionx":       ShowSectionX();             break;
                    case "stormx":         ShowStormX();               break;
                    case "corex_ecu":      ShowCoreXEcu();             break;
                    case "nodos":          ShowNodos();                break;
                    case "prescripciones": ShowQuantiXEditor("shape"); break;
                    case "actualizar":     ShowActualizar();           break;
                    case "sistema":        ShowSistema();              break;
                    case "sonidos":        ShowSonidos();              break;
                    // Cámaras es VENTANA propia, no overlay: no esconde la
                    // Configuración sola y quedaría abierta atrás.
                    case "camaras":        CloseConfig(); ShowCamaras(); break;
                    default:
                        MostrarToast("Ese módulo todavía no tiene pantalla propia");
                        break;
                }
            };
            // Cierre de la ventana con la Config abierta en el QuantiX
            // EMBEBIDO: mismo motivo que DetenerEditorQuantiXAlApagar (más
            // abajo) para el overlay suelto — el STOP a los motores tiene que
            // salir al cable ANTES de que el proceso muera, así que se espera.
            Closed += (_, _) => _configHost.DetenerModulosAlApagar();
        }
        // Guías nativo (14vo port): AB delega en el flujo del mapa que ya
        // existía; curva y lista van contra /api/tracks igual que la página.
        _guiasHost = this.FindControl<GuiasPanel>("GuiasHost");
        if (_guiasHost != null)
        {
            _guiasHost.CrearAbPedido += StartAbCreate;
            _guiasHost.Aviso += MostrarToast;
            _guiasHost.Cerrado += () =>
            {
                // Igual que al cerrar el diálogo HTML: replegar el menú.
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
        }
        _loteHost = this.FindControl<LotePanel>("LoteHost");
        if (_loteHost != null)
            _loteHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };

        // Dirección nativa (17vo port): panel chico sobre el mapa vivo con
        // TODO el FormSteer (6 tabs). La página HTML queda solo para el Hub
        // remoto — acá ya no se abre ningún WebView.
        _direccionHost = this.FindControl<DireccionPanel>("DireccionHost");
        if (_direccionHost != null)
        {
            _direccionHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
        }

        // Contorno nativo (ex contorno.html). El Aviso avisa por toast cuando
        // el panel se cierra con la grabación del lindero todavía prendida: el
        // pill "REC" vive adentro de la card y cerrada no se ve más.
        _contornoHost = this.FindControl<ContornoPanel>("ContornoHost");
        if (_contornoHost != null)
        {
            _contornoHost.Aviso += MostrarToast;
            _contornoHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
        }

        // Tramlines nativo (ex tramline.html). El Closed manda el commit de
        // descarte aunque la app se apague con el panel abierto: el motor es
        // OTRO proceso y sin ese POST las huellas quedan dibujadas para siempre.
        _tramSimpleHost = this.FindControl<TramSimplePanel>("TramSimpleHost");
        if (_tramSimpleHost != null)
        {
            _tramSimpleHost.Aviso += MostrarToast;
            _tramSimpleHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
            Closed += (_, _) => _tramSimpleHost.DetachEnCierreDeApp();
        }

        // Suavizar AB nativo (ex suavizar-ab.html). Mismo cuidado que Tramlines
        // con el cierre: si la app se apaga con el panel abierto y sin aplicar,
        // el cancel se espera acotado — el motor es OTRO proceso y sin ese POST
        // la curva suavizada queda dibujada en el mapa para siempre.
        _suavizarAbHost = this.FindControl<SuavizarAbPanel>("SuavizarAbHost");
        if (_suavizarAbHost != null)
        {
            _suavizarAbHost.Aviso += MostrarToast;
            _suavizarAbHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
            Closed += (_, _) => _suavizarAbHost.DetachEnCierreDeApp();
        }

        // Corregir posición nativa (ex corregir-posicion.html). Acá NO hay nada
        // que mandar en la bajada: el corrimiento aplicado tiene que QUEDAR
        // aplicado (es una corrección de deriva, no una vista previa).
        _corregirPosHost = this.FindControl<CorregirPosicionPanel>("CorregirPosHost");
        if (_corregirPosHost != null)
        {
            _corregirPosHost.Aviso += MostrarToast;
            _corregirPosHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
        }

        // Rutas grabadas nativo (ex recpath.html). Acá tampoco hay nada que
        // mandar en la bajada: cerrar NO descarta la grabación en memoria (igual
        // que cerrar la ventana HTML). El único cuidado es el teclado nativo, y
        // ese lo cierra el propio Cerrar() del panel.
        _recPathHost = this.FindControl<RecPathPanel>("RecPathHost");
        if (_recPathHost != null)
        {
            _recPathHost.Aviso += MostrarToast;
            _recPathHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
        }

        // Cabecera nativa (ex cabecera.html). El Closed manda el /close aunque
        // la app se apague con el panel abierto: ese POST no es cosmética —
        // suaviza y PERSISTE la cabecera recién construida.
        _cabeceraHost = this.FindControl<CabeceraPanel>("CabeceraHost");
        if (_cabeceraHost != null)
        {
            _cabeceraHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
            // En la bajada el /close se ESPERA (acotado): un fire-and-forget acá
            // sale con el proceso ya muriendo y la cabecera recién construida no
            // llega a guardarse.
            Closed += (_, _) => _cabeceraHost.DetachEnCierreDeApp();
        }

        // Cabecera por líneas nativa (ex cabecera-lineas.html). Mismo cuidado
        // con el cierre: su /close guarda las líneas dibujadas y recalcula si
        // la cabecera queda prendida — si la app se apaga con el panel abierto,
        // ese POST se espera acotado en la bajada.
        _cabLineasHost = this.FindControl<CabeceraLineasPanel>("CabeceraLineasHost");
        if (_cabLineasHost != null)
        {
            _cabLineasHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
            Closed += (_, _) => _cabLineasHost.DetachEnCierreDeApp();
        }

        // Tramlines (multi) nativo (ex tramlines.html). Mismo cuidado con el
        // cierre: su /close persiste Tram.txt y la opacidad, y además CIERRA la
        // sesión temporal del editor del motor — si la app se apaga con el
        // panel abierto, ese POST se espera acotado en la bajada.
        _tramMultiHost = this.FindControl<TramMultiPanel>("TramMultiHost");
        if (_tramMultiHost != null)
        {
            _tramMultiHost.Aviso += MostrarToast;
            _tramMultiHost.Cerrado += () =>
            {
                if (_vmIzq != null) { _vmIzq.OpenSubmenu = null; _vmIzq.IsCollapsed = true; }
            };
            Closed += (_, _) => _tramMultiHost.DetachEnCierreDeApp();
        }

        // El visor de IMU (rumbo/rolido/cabeceo, arriba a la izquierda) se
        // sacó de la pantalla (pedido 2026-08-11). La telemetría completa del
        // ECU queda en Herram. › CoreX-ECU; la clase ImuOverlay sigue en el
        // repo por si se re-engancha.

        // Tractor de rolido (abajo a la derecha): mismo ciclo de vida que el
        // visor IMU — arranca con la app y muere con la ventana.
        var tractorRolido = this.FindControl<TractorRolidoOverlay>("TractorRolidoHost");
        if (tractorRolido != null && App.WindowMode != "float")
        {
            tractorRolido.Attach(
                new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) },
                DeriveOrigin(App.TargetUrl));
            Closed += (_, _) => tractorRolido.Detach();
        }
        _mapOverlaysHost   = this.FindControl<Canvas>("MapOverlaysHost");
        _qxMapOverlay      = this.FindControl<QuantiXMapOverlay>("QxMapOverlay");
        _vxMapStrip        = this.FindControl<VistaXMapStrip>("VxMapStrip");
        _fxMapOverlay      = this.FindControl<FlowXMapOverlay>("FxMapOverlay");
        _nudgeOverlay      = this.FindControl<Border>("NudgeOverlay");
        // Los tres de corrección lateral mandan el mismo comando que mandaban
        // desde la barra; lo único que cambió es dónde están.
        // Menú SISTEMA (panel propio, ver MainWindow.axaml): los dos ítems
        // mandan los mismos comandos que mandaba el flyout que no abría.
        _sistemaMenu = this.FindControl<Border>("SistemaMenu");
        var bSisPerf = this.FindControl<Button>("BtnSisPerfiles");
        var bSisAyuda = this.FindControl<Button>("BtnSisAyuda");
        if (bSisPerf != null) bSisPerf.Click += (_, __) =>
        {
            if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
            RouteCockpitCommand("perfil_gestion");
        };
        if (bSisAyuda != null) bSisAyuda.Click += (_, __) =>
        {
            if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
            RouteCockpitCommand("ayuda");
        };

        // Idioma: despliega los tres in-place dentro del mismo panel.
        _sisIdiomaLista = this.FindControl<StackPanel>("SisIdiomaLista");
        var bSisIdioma = this.FindControl<Button>("BtnSisIdioma");
        if (bSisIdioma != null) bSisIdioma.Click += (_, __) =>
        {
            if (_sisIdiomaLista != null) _sisIdiomaLista.IsVisible = !_sisIdiomaLista.IsVisible;
        };
        void Idi(string ctrl, string codigo)
        {
            var b = this.FindControl<Button>(ctrl);
            if (b == null) return;
            b.Click += async (_, __) =>
            {
                if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
                if (_sisIdiomaLista != null) _sisIdiomaLista.IsVisible = false;
                await CambiarIdiomaAsync(codigo);
            };
        }
        Idi("BtnIdiomaEs", "es");
        Idi("BtnIdiomaEn", "en");
        Idi("BtnIdiomaPt", "pt");

        // Panel de Herramientas de la barra de la pasada: cada botón manda su
        // comando y cierra el panel.
        _herramientasMenu = this.FindControl<Border>("HerramientasMenu");
        void Herr(string ctrl, string cmd)
        {
            var b = this.FindControl<Button>(ctrl);
            if (b == null) return;
            b.Click += (_, __) =>
            {
                if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
                RouteCockpitCommand(cmd);
            };
        }
        Herr("BtnHrConteo",   "conteo_semillas");
        Herr("BtnHrCalc",     "calculadora");
        Herr("BtnHrWebcam",   "webcam");
        Herr("BtnHrCorregir", "corregir_pos");
        Herr("BtnHrSuavizar", "suavizar_ab");
        Herr("BtnHrEventos",  "visor_eventos");
        Herr("BtnHrGrafDir",  "grafico_direccion");
        Herr("BtnHrGrafXte",  "grafico_xte");
        // El botón Hub se sacó del menú (pedido 2026-08-11); el comando "hub"
        // y ShowHub() quedan por si se re-engancha desde otro lado.
        Herr("BtnHrCorexEcu", "corex_ecu");
        // Sonidos: antes se llegaba por Configuración › Otros › 🔔 Sonidos
        // (pagina HTML dentro del WebView). Ahora es panel nativo y tiene su
        // propio boton acá, que es donde el operario busca las herramientas.
        Herr("BtnHrSonidos",  "sonidos");

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
            // resuelve config.js clickeando el botón real del menú. EMBEBIDO
            // en la tarjeta de Configuración, no en ventana-diálogo suelta
            // (pedido 2026-08-17: "que todo se abra dentro de la ventana
            // config principal").
            _camarasHost.OnRequestConfigurar = () =>
                ShowConfigModulo("pages/config.html?mod=camaras.html", "Cámaras — Configurar");
            _camarasHost.OnRequestCerrar = CloseCamaras;
        }

        if (_nodosHost != null)
        {
            // El NodosPanel ya no abre nodos.html: las acciones de curado
            // (aceptar/ignorar/renombrar/eliminar/pin del implemento) y el
            // diagnostico MQTT (wildcard + log) son NATIVAS. El detalle del
            // nodo TAMBIÉN es nativo desde 2026-08-18 (NodoDetallePanel): la
            // fila abre el panel, no la página. Lo único que sigue saliendo
            // por WebView es el asistente de primera vez — EMBEBIDO en la
            // tarjeta de Configuración, no en ventana-diálogo suelta (pedido
            // 2026-08-17: "que todo se abra dentro de la ventana config
            // principal"). El panel de Nodos queda oculto al abrir Config;
            // el operario vuelve por la grilla de Módulos.
            _nodosHost.OnRequestCerrar = () => CloseNodos();
            _nodosHost.OnRequestDetalle = uid => ShowNodoDetalle(uid);
            _nodosHost.OnRequestAsistente = () =>
                ShowConfigModulo("pages/setup.html", "Nodos — Asistente de primera vez");
            // "Configurar" de una fila abre el PANEL NATIVO del producto, no la
            // pagina: si el nodo es un QuantiX, va al QuantiX de siempre.
            _nodosHost.OnRequestConfigurarProducto = producto =>
            {
                switch (producto)
                {
                    case "quantix":  ShowQuantiX();  break;
                    case "vistax":   ShowVistaX();   break;
                    case "sectionx": ShowSectionX(); break;
                    case "flowx":    ShowFlowX();    break;
                    case "stormx":   ShowStormX();   break;
                    default: MostrarToast("Todavía no hay pantalla para ese tipo de nodo"); break;
                }
            };
            _nodosHost.Aviso += MostrarToast;
        }

        if (_sonidosHost != null)
        {
            // El panel de Sonidos NO abre ninguna pagina: la subida del .wav
            // propio usa el file-picker del sistema, asi que no hay boton
            // "Configurar" que despierte el WebView.
            _sonidosHost.OnRequestCerrar = () => CloseSonidos();
            _sonidosHost.Aviso += MostrarToast;
        }

        if (_eventosHost != null)
        {
            _eventosHost.OnRequestCerrar = () => CloseEventos();
            // Header con Name="DragHandle": la card se puede CORRER para ver el
            // mapa de atras (doble toque la devuelve al centro).
            PanelArrastrable.Habilitar(_eventosHost);
        }

        if (_calculadoraHost != null)
            _calculadoraHost.OnRequestCerrar = () => CloseCalculadora();

        if (_perfilesHost != null)
            _perfilesHost.OnRequestCerrar = () => ClosePerfiles();

        if (_firmwaresHost != null)
        {
            _firmwaresHost.OnRequestCerrar = () => CloseFirmwares();
            _firmwaresHost.Aviso += MostrarToast;
        }

        // Los cuatro gráficos: solo ✕ y arrastre. Su header trae
        // Name="DragHandle", así que la card se puede correr para ver el mapa
        // de atrás (doble toque la devuelve al centro).
        if (_grafDireccionHost != null)
        {
            _grafDireccionHost.OnRequestCerrar = () => CloseGrafDireccion();
            PanelArrastrable.Habilitar(_grafDireccionHost);
        }
        if (_grafRumboHost != null)
        {
            _grafRumboHost.OnRequestCerrar = () => CloseGrafRumbo();
            PanelArrastrable.Habilitar(_grafRumboHost);
        }
        if (_grafXteHost != null)
        {
            _grafXteHost.OnRequestCerrar = () => CloseGrafXte();
            PanelArrastrable.Habilitar(_grafXteHost);
        }
        if (_grafCorreccionHost != null)
        {
            _grafCorreccionHost.OnRequestCerrar = () => CloseGrafCorreccion();
            PanelArrastrable.Habilitar(_grafCorreccionHost);
        }

        if (_orbitXHost != null)
        {
            _orbitXHost.OnRequestCerrar = () => CloseOrbitX();
            // "Abrir Prescripciones" (en el HTML era un <a> a
            // quantix.html?tab=shape): acá lleva al EDITOR NATIVO de QuantiX
            // parado en Shape, que es la misma pantalla.
            _orbitXHost.OnRequestPrescripciones = () => ShowQuantiXEditor("shape");
            PanelArrastrable.Habilitar(_orbitXHost);
        }

        // Red WiFi y Debug no tienen Name="DragHandle" en su header: quedan
        // fijas, como estaban las páginas.
        if (_wifiHost != null)
            _wifiHost.OnRequestCerrar = () => CloseWifi();

        if (_debugHost != null)
        {
            _debugHost.OnRequestCerrar = () => CloseDebug();
            _debugHost.Aviso += MostrarToast;
        }

        if (_insumosHost != null)
        {
            _insumosHost.OnRequestCerrar = () => CloseInsumos();
            _insumosHost.Aviso += MostrarToast;
            PanelArrastrable.Habilitar(_insumosHost);
        }

        if (_mapasHost != null)
        {
            _mapasHost.OnRequestCerrar = () => CloseMapas();
            PanelArrastrable.Habilitar(_mapasHost);
        }

        if (_nodoDetalleHost != null)
        {
            _nodoDetalleHost.OnRequestCerrar = () => CloseNodoDetalle();
            // "‹ Nodos" vuelve a la lista sin pasar por el mapa (el detalle
            // siempre se abre DESDE una fila de Nodos).
            _nodoDetalleHost.OnRequestVolver = () => ShowNodos();
        }

        if (_banderasHost != null)
        {
            // Banderas no usa OnRequestCerrar: su ✕ llama Cerrar() (que manda
            // el POST /close al motor) y avisa por el evento Cerrado.
            _banderasHost.Cerrado += () => CloseBanderas();
            // Salir de PilotX con el panel abierto TAMBIÉN tiene que mandar el
            // /close: la página lo cubría con el pagehide (sendBeacon), que se
            // dispara igual cuando se cierra la app. Sin esto queda una bandera
            // seleccionada y sin guardar. Detach() = Cerrar(), y es idempotente:
            // si el operario ya lo había cerrado, no repite nada.
            Closed += (_, _) => _banderasHost?.Detach();
            PanelArrastrable.Habilitar(_banderasHost);
        }

        if (_hubHost != null)
        {
            // Acciones rapidas del Hub: el callback abre el overlay nativo
            // correspondiente (QuantiX/VistaX) o el WebView lazy para
            // pantallas todavia no portadas (Nodos).
            _hubHost.OnRequestQuantix = () => ShowQuantiX();
            _hubHost.OnRequestVistax  = () => ShowVistaX();
            _hubHost.OnRequestNodos   = () => ShowNodos();
            _hubHost.OnRequestCorexEcu = () => ShowCoreXEcu();
        }

        if (_flowXHost != null)
        {
            // Boton Configurar: abre el EDITOR NATIVO (Nodo activo, Reguladoras,
            // Cortes, Electrovalvulas, Firmware, Nodos en red). Antes navegaba a
            // pages/flowx.html y era el ultimo motivo por el que FlowX levantaba
            // Chromium.
            _flowXHost.OnRequestConfigurar = () => ShowFlowXEditor();
        }
        if (_flowXEditorHost != null)
        {
            _flowXEditorHost.OnRequestCerrar  = () => CloseFlowXEditor();
            // "‹ Monitor" vuelve al panel live sin pasar por el mapa.
            _flowXEditorHost.OnRequestMonitor = () => { CloseFlowXEditor(); ShowFlowX(); };
            _flowXEditorHost.Aviso += MostrarToast;
        }
        if (_sectionXHost != null)
        {
            // El editor (mapeo surcos->secciones, test reles, debug MQTT)
            // sigue en HTML, pero EMBEBIDO en la tarjeta de Configuración —
            // ni pantalla completa ("toco Configurar y se abre una ventana
            // que ocupa toda la pantalla", reporte 2026-08-17) ni la
            // ventana-diálogo suelta que la reemplazó un día ("que todo se
            // abra dentro de la ventana config principal", mismo día).
            _sectionXHost.OnRequestConfigurar = () =>
                ShowConfigModulo("pages/sectionx.html", "SectionX — Configurar");
        }
        if (_quantiXHost != null)
        {
            // Boton Configurar: abre el EDITOR NATIVO (Siembra, Motores,
            // Shape, PID live, Calibracion, Prueba). Antes navegaba a
            // pages/quantix.html y era el ultimo motivo por el que QuantiX
            // levantaba Chromium.
            _quantiXHost.OnRequestConfigurar = () => ShowQuantiXEditor();
        }
        if (_quantiXEditorHost != null)
        {
            _quantiXEditorHost.OnRequestCerrar = () => CloseQuantiXEditor();
            _quantiXEditorHost.Aviso += MostrarToast;
            // Apagar la pantalla NO es una salida limpia por si sola: verb=test
            // no tiene meta, asi que un motor en plena rampa (Medir tope, Max
            // Hz, Prueba) se quedaba girando con PilotX cerrado. El cierre
            // manda el STOP igual que el boton Cerrar del editor.
            Closed += (_, _) => DetenerEditorQuantiXAlApagar();
        }
        if (_vistaXHost != null)
        {
            // Boton Configurar: abre el EDITOR NATIVO (Insumo & calibracion,
            // Implemento, Config). Antes navegaba a pages/vistax.html y era el
            // ultimo motivo por el que VistaX levantaba Chromium.
            _vistaXHost.OnRequestConfigurar = () => ShowVistaXEditor();
        }
        if (_vistaXEditorHost != null)
        {
            _vistaXEditorHost.OnRequestCerrar  = () => CloseVistaXEditor();
            // "‹ Monitor" vuelve al panel live sin pasar por el mapa.
            _vistaXEditorHost.OnRequestMonitor = () => { CloseVistaXEditor(); ShowVistaX(); };
            // Los paneles no navegan solos: el catalogo de insumos es una
            // pantalla propia, y desde 2026-08-18 es NATIVA (InsumosPanel) —
            // ya no hace falta pasar por la Configuración ni por Chromium.
            // Como antes, el editor de VistaX se cierra al abrirla (solo un
            // overlay a la vez): se vuelve reabriendo VistaX.
            _vistaXEditorHost.OnRequestAbrirInsumos       = () => ShowInsumos();
            // Secciones ES nativa desde la ola 3b: mandarla al WebView dejaba dos
            // pantallas distintas para la MISMA config segun por donde se entrara
            // (menu -> nativa, VistaX -> Chromium), con el riesgo de que una
            // muestre lo que la otra ya cambio.
            _vistaXEditorHost.OnRequestAbrirConfigCentral = () => { CloseVistaXEditor(); ShowConfig("tsections"); };
            _vistaXEditorHost.Aviso += MostrarToast;
        }
        if (_coreXEcuHost != null)
        {
            // Pestaña Configurar DENTRO de la misma card (pedido 2026-08-07,
            // "unifica"): un WebView propio embebido en el slot del panel con
            // corex-ecu.html?widget=1 (sin sidebar del Hub). Se crea al entrar
            // a la pestaña y se destruye al salir — ver AbrirEcuConfig.
            _coreXEcuHost.OnConfigOpen  = slot => AbrirEcuConfig(slot);
            _coreXEcuHost.OnConfigClose = () => CerrarEcuConfig();
            // La ✕ de la card flotante cierra el panel (el mapa ya esta vivo).
            _coreXEcuHost.OnRequestCerrar = () => CloseCoreXEcu();
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
            // …y el reloj que lo vuelve a bajar si nadie lo usa (ver
            // LiberarWebViewSiEstaOcioso: son ~260 MB de Chromium ocioso).
            ArmarLiberacionWebView();

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
                // El mismo banner también avisa fallas de siembra por surco
                // (VistaX): tapado / dosis no alcanzada / sin datos / tolva
                // vacía (pedido 2026-08-07).
                _cabinaAlarmasHost.Attach(_nodosClient,
                    new VistaXClient(DeriveOrigin(App.TargetUrl)));
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
                if (_flowXEditorHost != null && _flowXEditorHost.IsVisible)
                {
                    CloseFlowXEditor();
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
                if (_sonidosHost != null && _sonidosHost.IsVisible)
                {
                    CloseSonidos();
                    return;
                }
                if (_eventosHost != null && _eventosHost.IsVisible)
                {
                    CloseEventos();
                    return;
                }
                if (_calculadoraHost != null && _calculadoraHost.IsVisible)
                {
                    CloseCalculadora();
                    return;
                }
                if (_perfilesHost != null && _perfilesHost.IsVisible)
                {
                    ClosePerfiles();
                    return;
                }
                if (_firmwaresHost != null && _firmwaresHost.IsVisible)
                {
                    CloseFirmwares();
                    return;
                }
                if (_grafDireccionHost != null && _grafDireccionHost.IsVisible)
                {
                    CloseGrafDireccion();
                    return;
                }
                if (_grafRumboHost != null && _grafRumboHost.IsVisible)
                {
                    CloseGrafRumbo();
                    return;
                }
                if (_grafXteHost != null && _grafXteHost.IsVisible)
                {
                    CloseGrafXte();
                    return;
                }
                if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible)
                {
                    CloseGrafCorreccion();
                    return;
                }
                if (_orbitXHost != null && _orbitXHost.IsVisible)
                {
                    CloseOrbitX();
                    return;
                }
                if (_wifiHost != null && _wifiHost.IsVisible)
                {
                    CloseWifi();
                    return;
                }
                if (_debugHost != null && _debugHost.IsVisible)
                {
                    CloseDebug();
                    return;
                }
                if (_insumosHost != null && _insumosHost.IsVisible)
                {
                    CloseInsumos();
                    return;
                }
                if (_mapasHost != null && _mapasHost.IsVisible)
                {
                    CloseMapas();
                    return;
                }
                if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible)
                {
                    CloseNodoDetalle();
                    return;
                }
                if (_banderasHost != null && _banderasHost.IsVisible)
                {
                    CloseBanderas();
                    return;
                }
                if (_configHost != null && _configHost.IsVisible)
                {
                    CloseConfig();
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
        // Desde acá empieza a correr el reloj de inactividad: el prewarm dejó
        // el motor levantado y nadie lo pidió todavía.
        _webViewUsadoUtc = DateTime.UtcNow;
    }

    // ---- liberación del WebView por inactividad ---------------------------
    //
    // Al cerrar una pantalla el WebView se RECICLA (about:blank) en vez de
    // destruirse, para que la próxima apertura sea inmediata. Eso está bien
    // mientras el operario entra y sale del Hub — pero el uso real de la
    // jornada es: configura al principio y después maneja horas con todo
    // cerrado. Medido acá: 262 MB en 6 procesos de Chromium al 0% de CPU,
    // sosteniendo una página en blanco.
    //
    // Así que se recicla mientras se lo está usando, y recién después de unos
    // minutos quietos se baja del todo. La próxima apertura vuelve a pagar el
    // arranque del motor, pero a esa altura ya no es "el operario esperando
    // por algo que acaba de cerrar".
    //
    // El timer corre a 30 s: no hace falta más fino para un plazo de minutos,
    // y en Background no compite con el render del mapa.
    private DateTime _webViewUsadoUtc = DateTime.UtcNow;
    private DispatcherTimer? _webViewOciosoTimer;

    // Traza al archivo y NO Debug.WriteLine: en Release el compilador saca las
    // llamadas a Debug.*, así que todo lo que se "loguea" así no existe
    // justamente en la build que corre en la cabina.
    private static void TrazaWebView(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pilotx-webview.log"),
                DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    private void ArmarLiberacionWebView()
    {
        if (App.WebViewSiempre) { TrazaWebView("liberacion DESACTIVADA (--webview-siempre)"); return; }
        TrazaWebView("liberacion armada, plazo " + App.WebViewOcioso.TotalMinutes.ToString("0.##") + " min");
        _webViewOciosoTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _webViewOciosoTimer.Tick += (_, __) => LiberarWebViewSiEstaOcioso();
        _webViewOciosoTimer.Start();
        Closed += (_, _) => { try { _webViewOciosoTimer?.Stop(); } catch { } };
    }

    private void LiberarWebViewSiEstaOcioso()
    {
        try
        {
            if (_webView == null || _webViewSlot == null)
            {
                TrazaWebView("tick: nada que liberar (webView=" + (_webView == null ? "null" : "ok")
                             + " slot=" + (_webViewSlot == null ? "null" : "ok") + ")");
                return;
            }

            // Cualquiera de estas es "lo están usando": el reloj se reinicia y
            // no se toca nada. Sobre todo la del diálogo — hay pantallas que
            // abren en ventana aparte y el slot principal queda oculto, que
            // desde acá se vería igual que "no lo usa nadie".
            if (_webViewSlot.IsVisible || _dialogWebView != null || _dialogWin != null)
            {
                TrazaWebView("tick: en uso (slotVisible=" + _webViewSlot.IsVisible
                             + " dlgWv=" + (_dialogWebView != null)
                             + " dlgWin=" + (_dialogWin != null) + ")");
                _webViewUsadoUtc = DateTime.UtcNow;
                return;
            }

            var ocio = DateTime.UtcNow - _webViewUsadoUtc;
            if (ocio < App.WebViewOcioso)
            {
                TrazaWebView("tick: ocioso hace " + ocio.TotalSeconds.ToString("0") + " s, falta");
                return;
            }
            TrazaWebView("LIBERANDO tras " + ocio.TotalSeconds.ToString("0") + " s de ocio");

            var wv = _webView;
            _webView = null;                       // ShowWebView lo recrea solo
            _webViewSlot.Children.Remove(wv.Control);
            wv.Destroy();
            TrazaWebView("WebView liberado (destruido y sacado del arbol)");
        }
        catch (Exception ex)
        {
            // Que no se pueda liberar no es motivo para voltear la pantalla:
            // en el peor caso queda la memoria ocupada, como antes.
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] liberar WebView: " + ex.Message);
        }
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
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible)
        {
            _flowXEditorHost.Detach();
            _flowXEditorHost.IsVisible = false;
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
        if (_eventosHost != null && _eventosHost.IsVisible)
        {
            _eventosHost.Detach();
            _eventosHost.IsVisible = false;
        }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible)
        {
            _calculadoraHost.Detach();
            _calculadoraHost.IsVisible = false;
        }
        if (_perfilesHost != null && _perfilesHost.IsVisible)
        {
            _perfilesHost.Detach();
            _perfilesHost.IsVisible = false;
        }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible)
        {
            _firmwaresHost.Detach();
            _firmwaresHost.IsVisible = false;
        }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible)
        {
            _grafDireccionHost.Detach();
            _grafDireccionHost.IsVisible = false;
        }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible)
        {
            _grafRumboHost.Detach();
            _grafRumboHost.IsVisible = false;
        }
        if (_grafXteHost != null && _grafXteHost.IsVisible)
        {
            _grafXteHost.Detach();
            _grafXteHost.IsVisible = false;
        }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible)
        {
            _grafCorreccionHost.Detach();
            _grafCorreccionHost.IsVisible = false;
        }
        if (_orbitXHost != null && _orbitXHost.IsVisible)
        {
            _orbitXHost.Detach();
            _orbitXHost.IsVisible = false;
        }
        if (_wifiHost != null && _wifiHost.IsVisible)
        {
            _wifiHost.Detach();
            _wifiHost.IsVisible = false;
        }
        if (_debugHost != null && _debugHost.IsVisible)
        {
            _debugHost.Detach();
            _debugHost.IsVisible = false;
        }
        if (_insumosHost != null && _insumosHost.IsVisible)
        {
            _insumosHost.Detach();
            _insumosHost.IsVisible = false;
        }
        if (_mapasHost != null && _mapasHost.IsVisible)
        {
            _mapasHost.Detach();
            _mapasHost.IsVisible = false;
        }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible)
        {
            _nodoDetalleHost.Detach();
            _nodoDetalleHost.IsVisible = false;
        }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost != null && _banderasHost.IsVisible)
            _banderasHost.Cerrar();
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
            _webViewUsadoUtc = DateTime.UtcNow;   // reinicia el reloj de inactividad
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
                // Recién acá arranca a contar la inactividad: mientras la
                // pantalla estuvo abierta se lo estaba usando.
                _webViewUsadoUtc = DateTime.UtcNow;
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
            // mapa, así que cada píxel suyo es mapa tapado. (En esta pantalla ya
            // no se abre como ventana — es panel nativo; la medida queda por si
            // algún camino vuelve a pedir la página.)
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

            // Guías abre en el MENÚ (3 botones): ventana chica. Cada paso
            // pide su tamaño por /api/ventana (ver AtenderPedidoDeVentana).
            case "tracks.html":
                return (360, 300);

            // Conteo de semillas: tabla de 7 columnas (semillas, esperadas,
            // logrado, objetivo, desvío, estado) con una fila por surco —
            // necesita ancho para que no se apilen las columnas, y alto para
            // ver varias líneas de siembra juntas.
            case "vistax-prueba.html":
                return (760, 560);

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
            // `mapaVivo` quedó de parámetro por compatibilidad, pero desde que
            // Contorno es panel nativo NINGÚN call site lo pasa en true (era el
            // único) y no hay rama que apague nada. TODO: borrar el parámetro
            // de OpenDialogUrl/OpenDialogPage cuando no queden diálogos HTML.
            if (_mapHost != null) _mapHost.IsVisible = true;
            ReanudarMapa();

            // Estas flags valen también cuando el diálogo ya está abierto y
            // solo se navega (el early-return de abajo): sin esto, pasar de
            // otra página a Guías dejaba el cierre-por-guía-nueva apagado.
            _dialogEsTracks = full.IndexOf("tracks.html", StringComparison.OrdinalIgnoreCase) >= 0;
            _tracksAlAbrirDialogo = -1;                  // el próximo HUD fija el piso
            _trackIdxAlAbrirDialogo = int.MinValue;      // ídem para la guía activa

            // ÍDEM para el lote, y por la misma razón: si la ventana ya estaba
            // abierta en OTRA página (ej. Guías) y desde el menú se navega a
            // lote.html, el early-return de abajo se saltaba estas dos líneas
            // — el diálogo dejaba de estar marcado como "de lote" y abrir un
            // lote ya no lo cerraba (reporte 2026-08-06: "hoy se cerraba,
            // ahora queda abierta"). Marcar ANTES del early-return.
            _dialogEsLote = full.IndexOf("lote.html", StringComparison.OrdinalIgnoreCase) >= 0;
            _loteAlAbrirDialogo = _lastFieldDir;

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

    // ---- canal página → host (VentanaController) ---------------------------
    //
    // La página POSTea a /api/ventana lo que quiere de SU ventana y llega acá
    // con el snapshot del HUD. Es el canal que faltaba: el WebView del diálogo
    // no entrega las navegaciones que inicia la página, así que el centinela
    // "pilotx-close" nunca cerró nada (dejaba la ventana en blanco abierta) y
    // cada pantalla se venía tapando con una señal indirecta distinta.
    private long _ventanaSeqVista = -1;

    // Estado vivo de secciones (auto o manual prendido). Lo refresca el HUD a
    // 10 Hz; lo consulta el guard de "Borrar pintado".
    private bool _seccionesActivas;

    // Cantidad de marcas de giro del snapshot anterior: si SUBE es que el
    // operario acaba de marcar → toast de confirmación (el botón solo no da
    // feedback y el operario duda si tocó bien).
    private int _marcasGiroPrev;

    // Contador monótono del engine de marcas descartadas por ser de otra
    // guía. -1 = todavía sin snapshot: el primer valor solo sincroniza, no
    // avisa (el descarte pudo ser de una sesión anterior de la UI).
    private int _marcasGiroDescartadasPrev = -1;

    // ---- idioma de la interfaz ---------------------------------------------
    //
    // El idioma se elige en el menú SISTEMA y también desde el Hub. Viaja en el
    // HUD (idioma + contador) para que un cambio hecho en CUALQUIER ventana
    // llegue acá: sin eso, cambiar el idioma en una pantalla dejaba la otra en
    // castellano hasta reiniciar.
    //
    // La traducción no reemplaza los textos del XAML: el Traductor recorre el
    // árbol de controles y los cambia al vuelo, guardando el castellano
    // original de cada uno. Ver PilotX.Cockpit.Bars/Traductor.cs.
    private StackPanel? _sisIdiomaLista;
    private long _idiomaSeqVisto = -1;

    /// <summary>Guarda el idioma en el engine y lo aplica en pantalla.</summary>
    private async Task CambiarIdiomaAsync(string codigo)
    {
        try
        {
            var http = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = DeriveOrigin(App.TargetUrl).TrimEnd('/');
            using var body = new System.Net.Http.StringContent(
                "{\"idioma\":\"" + codigo + "\"}", System.Text.Encoding.UTF8, "application/json");
            using var _ = await http.PostAsync(url + "/api/idioma", body);
        }
        catch (Exception ex) { Console.Error.WriteLine("[Idioma] guardar: " + ex.Message); }

        await AplicarIdiomaAsync(codigo);
    }

    /// <summary>Traduce la pantalla al idioma pedido (sin tocar el engine).</summary>
    private async Task AplicarIdiomaAsync(string codigo)
    {
        try
        {
            if (!PilotX.Cockpit.Bars.Traductor.HayDiccionario)
            {
                var http = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                await PilotX.Cockpit.Bars.Traductor.CargarDiccionarioAsync(
                    http, DeriveOrigin(App.TargetUrl));
            }
            if (PilotX.Cockpit.Bars.Traductor.CambiarIdioma(codigo))
            {
                PilotX.Cockpit.Bars.Traductor.Aplicar(this);
                MarcarIdiomaActivo();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[Idioma] aplicar: " + ex.Message); }
    }

    // El idioma activo se marca con FONDO, no agregándole un tilde al texto:
    // el Traductor guarda el Content original de cada botón y en la próxima
    // pasada lo restauraría, borrando la marca.
    private void MarcarIdiomaActivo()
    {
        void Pintar(string ctrl, string codigo)
        {
            var b = this.FindControl<Button>(ctrl);
            if (b == null) return;
            bool activo = PilotX.Cockpit.Bars.Traductor.Idioma == codigo;
            b.Background = activo
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DCEFD8"))
                : Avalonia.Media.Brushes.Transparent;
            b.FontWeight = activo ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;
        }
        Pintar("BtnIdiomaEs", "es");
        Pintar("BtnIdiomaEn", "en");
        Pintar("BtnIdiomaPt", "pt");
    }

    /// <summary>
    /// Aplica el idioma que venga en el HUD. Cubre dos casos con el mismo
    /// código: el arranque (la pantalla toma el idioma que quedó guardado) y
    /// el cambio hecho desde otra ventana.
    /// </summary>
    private void AtenderIdioma(HudSnapshot s)
    {
        if (s == null) return;
        string quiere = string.IsNullOrWhiteSpace(s.Idioma) ? "es" : s.Idioma;
        if (_idiomaSeqVisto == s.IdiomaSeq && quiere == PilotX.Cockpit.Bars.Traductor.Idioma) return;
        _idiomaSeqVisto = s.IdiomaSeq;
        if (quiere != PilotX.Cockpit.Bars.Traductor.Idioma) _ = AplicarIdiomaAsync(quiere);
    }

    private void AtenderPedidoDeVentana(HudSnapshot s)
    {
        if (s == null) return;
        if (_ventanaSeqVista < 0) { _ventanaSeqVista = s.VentanaSeq; return; }  // piso al arrancar
        if (s.VentanaSeq == _ventanaSeqVista) return;
        _ventanaSeqVista = s.VentanaSeq;

        if (s.VentanaCerrar)
        {
            CerrarDialogo();
            return;
        }

        if (s.VentanaAncho > 0 && s.VentanaAlto > 0 && _dialogWin != null)
        {
            var (w, h) = AjustarAPantalla(s.VentanaAncho, s.VentanaAlto);
            _dialogWin.Width = w;
            _dialogWin.Height = h;
        }
    }

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
        if (_guiasHost != null && _guiasHost.IsVisible) { _guiasHost.Cerrar(); return; }
        if (_loteHost != null && _loteHost.IsVisible) { _loteHost.Cerrar(); return; }
        if (_contornoHost != null && _contornoHost.IsVisible) { _contornoHost.Cerrar(); return; }
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) { _cabeceraHost.Cerrar(); return; }
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) { _cabLineasHost.Cerrar(); return; }
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) { _tramSimpleHost.Cerrar(); return; }
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) { _tramMultiHost.Cerrar(); return; }
        // Suavizar AB: sin esta línea la flecha "←" no cerraba la card (el
        // operario la veía muerta) y encima quedaba la curva suavizada pintada
        // en el mapa, porque el cancel sale recién en Cerrar().
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) { _suavizarAbHost.Cerrar(); return; }
        // Corregir posición: sin esta línea la flecha "←" no cerraba la card y el
        // operario la veía muerta.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) { _corregirPosHost.Cerrar(); return; }
        // Rutas grabadas: mismo motivo — sin esta línea la flecha "←" no cerraba
        // la card y el operario la veía muerta con la lista de .rec parada
        // arriba del mapa.
        if (_recPathHost != null && _recPathHost.IsVisible) { _recPathHost.Cerrar(); return; }
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) { CloseFieldData(); return; }
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { CloseSistema();   return; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   { CloseGpsData();   return; }
        if (_stormXHost    != null && _stormXHost.IsVisible)    { CloseStormX();    return; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { CloseFlowX();     return; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { CloseFlowXEditor(); return; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { CloseSectionX();  return; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { CloseQuantiX();   return; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { CloseQuantiXEditor(); return; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { CloseVistaXEditor(); return; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { CloseVistaX();    return; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { CloseCoreXEcu();  return; }
        if (_hubHost       != null && _hubHost.IsVisible)       { CloseHub();       return; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { CloseNodos();     return; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ CloseActualizar();return; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { CloseCamaras();  return; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { CloseSonidos();  return; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { CloseEventos();  return; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { CloseCalculadora(); return; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { ClosePerfiles(); return; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { CloseFirmwares();return; }
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { CloseGrafDireccion(); return; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { CloseGrafRumbo(); return; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { CloseGrafXte(); return; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { CloseGrafCorreccion(); return; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { CloseOrbitX(); return; }
        if (_wifiHost != null && _wifiHost.IsVisible) { CloseWifi(); return; }
        if (_debugHost != null && _debugHost.IsVisible) { CloseDebug(); return; }
        if (_insumosHost != null && _insumosHost.IsVisible) { CloseInsumos(); return; }
        if (_mapasHost != null && _mapasHost.IsVisible) { CloseMapas(); return; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { CloseNodoDetalle(); return; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  { CloseBanderas(); return; }
        if (_configHost    != null && _configHost.IsVisible)    { CloseConfig();   return; }
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
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost != null && _sectionXHost.IsVisible) { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost != null && _quantiXHost.IsVisible) { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost != null && _vistaXHost.IsVisible) { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost != null && _coreXEcuHost.IsVisible) { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_configHost != null && _configHost.IsVisible) { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost != null && _eventosHost.IsVisible) { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost != null && _perfilesHost.IsVisible) { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        _fieldDataHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        // SIN la flecha "←" de la esquina: esta card tiene su propio ✕ en el
        // header (2026-08-18). Dos salidas para lo mismo, una de ellas del otro
        // lado de la pantalla, solo confunde — y encima la flecha se comia el
        // rincon del mapa.
        if (_webViewBack != null) _webViewBack.IsVisible = false;
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
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost != null && _sectionXHost.IsVisible) { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost != null && _quantiXHost.IsVisible) { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost != null && _vistaXHost.IsVisible) { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost != null && _coreXEcuHost.IsVisible) { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_configHost != null && _configHost.IsVisible) { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost != null && _eventosHost.IsVisible) { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost != null && _perfilesHost.IsVisible) { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init del cliente HTTP: solo se crea la primera vez que el
        // operario abre Sistema. Si nunca lo abre, cero costo de red extra.
        if (_sistemaClient == null)
            _sistemaClient = new SistemaClient(DeriveOrigin(App.TargetUrl));
        _sistemaHost.Attach(_sistemaClient);
        _sistemaHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
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
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        _gpsDataHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        // SIN la flecha "←" de la esquina: esta card tiene su propio ✕ en el
        // header (2026-08-18). Mismo criterio que FieldData.
        if (_webViewBack != null) _webViewBack.IsVisible = false;
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
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_stormXClient == null)
            _stormXClient = new StormXClient(DeriveOrigin(App.TargetUrl));
        _stormXHost.Attach(_stormXClient);
        _stormXHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
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

    // ---------- FlowX overlay nativo (Monitor, sin WebView) --------------
    //
    // El live (caudal/PWM/PID + KPIs combinados con el HUD) es lo cabin-
    // critical. El resto (reguladoras, PID, cortes, firmware) tambien es
    // nativo desde 2026-08-16: el boton Configurar abre FlowXEditorPanel.

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
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_flowXClient == null)
            _flowXClient = new FlowXClient(DeriveOrigin(App.TargetUrl));
        _flowXHost.Attach(_flowXClient);
        _flowXHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
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

    // ---------- FlowX EDITOR nativo (30mo port, sin WebView) ---------------
    //
    // Las pantallas que antes vivian en pages/flowx.html: Nodo activo (datos,
    // ancho de barra, PID y actuador, calibrar / auto-tune / barrido / PWM
    // manual), Reguladoras, Cortes y secciones, Electrovalvulas + sync al
    // firmware, Firmware (OTA) y Nodos en red. Card clara flotante con el mapa
    // VIVO detras.

    private void ShowFlowXEditor()
    {
        if (_flowXEditorHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();

        // Lazy init: es el MISMO cliente del monitor (stateless), se reusa.
        _flowXClient ??= new FlowXClient(DeriveOrigin(App.TargetUrl));
        _flowXEditorHost.Attach(_flowXClient);
        _flowXEditorHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        // La flecha "volver" es del WebView: si venimos del monitor quedaba
        // colgada sobre el mapa sin nada atras a lo que volver.
        bool hayWebView = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !hayWebView) _webViewBack.IsVisible = false;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] FlowX editor open (nativo, no WebView)");
    }

    private void CloseFlowXEditor()
    {
        if (_flowXEditorHost == null) return;
        // Detach manda parar la valvula si quedo girando con el buscador de
        // PWM minimo abierto (el failsafe del firmware tarda 4 s).
        _flowXEditorHost.Detach();
        _flowXEditorHost.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] FlowX editor closed -> back to native map");
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
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_sectionXClient == null)
            _sectionXClient = new SectionXClient(DeriveOrigin(App.TargetUrl));
        _sectionXHost.Attach(_sectionXClient);
        _sectionXHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
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
    // La tab Monitor (dosis real/target + PWM + estado PID por motor) es la
    // cabin-critical. El resto (Siembra, Motores, Shape, PID live-tune,
    // calibracion y prueba) tambien es nativo desde 2026-08-15: el boton
    // Configurar abre QuantiXEditorPanel, no el WebView.

    private void ShowQuantiX()
    {
        if (_quantiXHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_quantiXClient == null)
            _quantiXClient = new QuantiXClient(DeriveOrigin(App.TargetUrl));
        _quantiXHost.Attach(_quantiXClient);
        _quantiXHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
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

    // ---------- QuantiX EDITOR nativo (18vo port, sin WebView) -------------
    //
    // Las 6 tabs que antes vivian en pages/quantix.html: Siembra (planter +
    // reparto de surcos), Motores (sensor/PWM/PID), Shape (prescripciones),
    // PID live, Calibracion y Prueba. Card clara flotante con el mapa VIVO
    // detras. `tab` cubre los deep-links que en HTML eran ?tab=shape.

    private void ShowQuantiXEditor(string? tab = null)
    {
        if (_quantiXEditorHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();

        // Lazy init: el cliente se crea la primera vez y se reusa.
        _quantiXEditorClient ??= new QuantiXEditorClient(DeriveOrigin(App.TargetUrl));
        _quantiXEditorHost.Attach(_quantiXEditorClient, tab);
        _quantiXEditorHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] QuantiX editor open (nativo, no WebView)");
    }

    // Cierre de la ventana con el editor de QuantiX abierto. Esperamos hasta
    // 1,5 s a que el STOP salga de verdad: si lo disparamos y soltamos, el
    // proceso se apaga antes y el motor queda girando. Los awaits del camino de
    // stop van con ConfigureAwait(false), asi que esperar desde el hilo de UI
    // no traba nada.
    private void DetenerEditorQuantiXAlApagar()
    {
        var h = _quantiXEditorHost;
        if (h == null) return;
        try { h.DetachAsync().Wait(1500); } catch { }
    }

    private void CloseQuantiXEditor()
    {
        if (_quantiXEditorHost == null) return;
        // Detach manda stop a cualquier motor girando (rampa / Max Hz /
        // corrida de calibracion): verb=test no tiene meta propia.
        _quantiXEditorHost.Detach();
        _quantiXEditorHost.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] QuantiX editor closed -> back to native map");
    }

    // ---------- VistaX overlay nativo (Monitor live-only, sin WebView) ------
    //
    // La tab Monitor (SPM por surco + badges + trenes con tubitos semilla/ferti
    // y barras de otros sensores) es la cabin-critical. El resto (Insumo &
    // calibracion, Implemento, Config) tambien es nativo desde 2026-08-16: el
    // boton Configurar abre VistaXEditorPanel, no el WebView.

    private void ShowVistaX()
    {
        if (_vistaXHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_vistaXClient == null)
            _vistaXClient = new VistaXClient(DeriveOrigin(App.TargetUrl));
        _vistaXHost.Attach(_vistaXClient);
        _vistaXHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
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

    // ---------- VistaX EDITOR nativo (29no port, sin WebView) --------------
    //
    // Las pantallas que antes vivian en pages/vistax.html: Insumo &
    // calibracion (catalogo + ventana de captura de 5 s), Implemento (objetivo,
    // parametros y mapeo de sensores) y Config (comportamiento del monitoreo +
    // ZIP de sesiones del lote). Card clara flotante con el mapa VIVO detras.
    //
    // El tab "Nodos" de la pagina NO se replica: esa grilla ya esta en el
    // monitor (VistaXPanel) y en el editor alcanza el desplegable de UID.

    private void ShowVistaXEditor(string? tab = null)
    {
        if (_vistaXEditorHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();

        // Lazy init: es el MISMO cliente del monitor (stateless), se reusa.
        _vistaXClient ??= new VistaXClient(DeriveOrigin(App.TargetUrl));
        _vistaXEditorHost.Attach(_vistaXClient, tab);
        _vistaXEditorHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        // La flecha "volver" es del WebView: si venimos del monitor quedaba
        // colgada sobre el mapa sin nada atras a lo que volver.
        bool hayWebView = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !hayWebView) _webViewBack.IsVisible = false;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] VistaX editor open (nativo, no WebView)");
    }

    private void CloseVistaXEditor()
    {
        if (_vistaXEditorHost == null) return;
        // Detach cancela una ventana de calibracion abierta: si no, el backend
        // queda con la captura colgada y el proximo start puede fallar.
        _vistaXEditorHost.Detach();
        _vistaXEditorHost.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] VistaX editor closed -> back to native map");
    }

    // ----- CoreX-ECU (port #9): solo Live tab nativa (telemetria del ECU).
    // Estado / Calibracion / Conexion siguen en HTML detras de "Configurar"
    // (pages/corex-ecu.html via OnRequestConfigurar -> WebView lazy).
    // Card flotante sobre el mapa VIVO (mismo patron que Guias/Lote): antes
    // era takeover pantalla completa oscuro y en cabina se veia como "una
    // ventana gigante negra" (reporte 2026-08-07). El mapa nunca se apaga.

    private void ShowCoreXEcu()
    {
        if (_coreXEcuHost == null) return;
        // Solo un overlay a la vez (y se restaura el mapa si otro lo tapaba).
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        // La Configuración es una card del MISMO ZIndex: si no se cierra queda
        // abajo de la de CoreX-ECU (dos cards pisadas) y además sigue pidiendo
        // el snapshot cada 3 s sin que nadie lo mire.
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa: uno a la vez, mismo criterio que AbrirGuias.
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // El mapa queda VIVO detras de la card (y se reenciende si un
        // takeover previo lo habia apagado).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        bool webViewVisible = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisible) _webViewBack.IsVisible = false;
        // Lazy init: el cliente solo se crea la primera vez que se abre.
        if (_coreXEcuClient == null)
            _coreXEcuClient = new CoreXEcuClient(DeriveOrigin(App.TargetUrl));
        _coreXEcuHost.Attach(_coreXEcuClient);
        _coreXEcuHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoreX-ECU open (card flotante, mapa vivo)");
    }

    private void CloseCoreXEcu()
    {
        if (_coreXEcuHost == null) return;
        _coreXEcuHost.Detach();
        _coreXEcuHost.IsVisible = false;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoreX-ECU closed");
    }

    // WebView propio de la pestaña Configurar de CoreX-ECU. Vive DENTRO de
    // la card (slot ConfigHost). NO se re-parenta el _webView global: sacar
    // un NativeControlHost del arbol visual mata los procesos de Chromium.
    // Se crea al entrar a la pestaña y se DESTRUYE al salir: la config del
    // ECU es flujo de galpon, no de labor — no vale la pena tenerlo vivo.
    private PilotX.Desktop.Services.IWebViewHandle? _ecuConfigWebView;

    private void AbrirEcuConfig(Avalonia.Controls.Panel slot)
    {
        if (App.WebViewHost == null) return;
        try
        {
            if (_ecuConfigWebView == null)
            {
                _ecuConfigWebView = App.WebViewHost.Create(_ => { });
                slot.Children.Add(_ecuConfigWebView.Control);
            }
            var origin = DeriveOrigin(App.TargetUrl);
            _ecuConfigWebView.Navigate(origin + "pages/corex-ecu.html?widget=1");
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoreX-ECU config tab (WebView embebido)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoreX-ECU config open error: " + ex.Message);
        }
    }

    private void CerrarEcuConfig()
    {
        if (_ecuConfigWebView == null) return;
        try
        {
            var wv = _ecuConfigWebView;
            _ecuConfigWebView = null;
            if (wv.Control.Parent is Avalonia.Controls.Panel p) p.Children.Remove(wv.Control);
            wv.Destroy();
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoreX-ECU config tab cerrada (WebView destruido)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoreX-ECU config close error: " + ex.Message);
        }
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
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: clientes solo la primera vez. NodosClient se reusa con
        // el del overlay cabina-alarmas si ya esta inicializado.
        if (_nodosClient == null)
            _nodosClient = new NodosClient(DeriveOrigin(App.TargetUrl));
        if (_overlaysClient == null)
            _overlaysClient = new OverlaysClient(DeriveOrigin(App.TargetUrl));
        _hubHost.Attach(_nodosClient, _overlaysClient);
        _hubHost.IsVisible = true;
        // Tarjeta flotante sobre el mapa VIVO — misma cura que CoreX-ECU
        // (reporte 2026-08-07 "ventana gigante negra"). Antes apagaba el mapa
        // y prendía el fondo de respaldo: el operario veía "una página negra"
        // (reporte 2026-08-11). Regla de la casa: el mapa siempre se ve.
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Hub open (card nativa sobre mapa vivo)");
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

    // ----- Nodos overlay (port #12, CERRADO): reemplazo nativo de pages/nodos.html.
    // Lista (tabs + tabla + banner + acciones de curado) y Diagnostico MQTT
    // (wildcard + reconectar + log) son nativos. Solo salen por WebView las OTRAS
    // paginas: detalle del nodo y asistente de primera vez.

    private void ShowNodos()
    {
        if (_nodosHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Reutiliza el NodosClient si ya esta inicializado (cabina-alarmas/Hub).
        if (_nodosClient == null)
            _nodosClient = new NodosClient(DeriveOrigin(App.TargetUrl));
        _nodosHost.Attach(_nodosClient);
        _nodosHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga (apagarlo dejaba "una pagina
        // negra" detras del panel — reporte 2026-08-11, misma cura que el Hub).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
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
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        // Actualizar APAGA el mapa: si la Configuración quedaba abierta, su card
        // se dibujaba encima de la pantalla de actualización y seguía sondeando.
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        if (_webView != null) CloseWebView();
        if (_updateClient == null)
            _updateClient = new UpdateClient(DeriveOrigin(App.TargetUrl));
        _actualizarHost.Attach(_updateClient);
        _actualizarHost.IsVisible = true;
        // El mapa se queda VIVO detras de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
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
            // pantalla completa "gigante"). EMBEBIDO en la tarjeta de
            // Configuración (pedido 2026-08-17), con la ventana de Cámaras
            // ya cerrada arriba para no dejar dos superficies peleando.
            ShowConfigModulo("pages/config.html?mod=camaras.html", "Cámaras — Configurar");
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

    // ----- Sonidos overlay: reemplazo nativo COMPLETO de pages/sonidos.html.
    // Config de eventos (habilitado/sonido/umbral/sostenido/repetir), mute,
    // alarmas activas @2s, probar el wav por el sink de audio del head y subir
    // un .wav propio con el file-picker del sistema. No queda ningun camino a
    // HTML: esta pantalla ya no instancia Chromium.

    private void ShowSonidos()
    {
        if (_sonidosHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // Sonidos se dibuja ENCIMA de Guias/Lote/Direccion y quedan dos cards
        // pisadas (mismo criterio que ShowCoreXEcu).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        if (_sonidosClient == null)
            _sonidosClient = new SonidosClient(DeriveOrigin(App.TargetUrl));
        _sonidosHost.Attach(_sonidosClient);
        _sonidosHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Sonidos open (nativo, no WebView)");
    }

    // ----- CONFIGURACIÓN nativa: shell del porteo de pages/config.html.
    // Menú lateral por grupos + pestañas + footer (perfil / ancho / unidades).
    // Desde la ola 3c las 16 filas del menú son NATIVAS: ninguna cae al WebView.
    // Lo único que sigue abriendo HTML desde acá es "Módulos y más…", que lleva
    // a la config completa para llegar a los módulos embebidos.
    // Ver docs/MIGRACION-OLA3C.md.

    private void ShowConfig(string? tab = null)
    {
        if (_configHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Banderas cierra con Cerrar() (no con IsVisible=false): ese es el que
        // manda el POST /close — deselecciona la bandera y guarda.
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Cards que flotan con el mismo ZIndex: si no se cierran quedan dos
        // pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();

        // Lazy init: el cliente se crea una sola vez y se reusa.
        _configClient ??= new ConfigVehiculoClient(DeriveOrigin(App.TargetUrl));
        _configHost.Attach(_configClient, tab);
        _configHost.IsVisible = true;
        // El mapa se queda VIVO detrás de la card (doctrina: nunca se apaga).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        bool hayWebViewCfg = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !hayWebViewCfg) _webViewBack.IsVisible = false;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Config open (nativo, tab=" + (tab ?? "summary") + ")");
    }

    /// <summary>
    /// Abre la Configuración PARADA en un módulo HTML embebido (pedido usuario
    /// 2026-08-17: "en vez de que abra otra ventana, armá tabs y que todo se
    /// abra dentro de la ventana config principal"). Reemplaza a las
    /// ventanas-diálogo sueltas que abrían los "Configurar" de los paneles:
    /// ahora todo vive adentro de la tarjeta de Configuración, con su menú,
    /// sus pestañas y su ✕. El orden importa: ShowConfig hace el Attach y
    /// AbrirModuloHtml sabe esperar al arranque si hace falta.
    /// </summary>
    private void ShowConfigModulo(string ruta, string titulo)
    {
        ShowConfig();
        _configHost?.AbrirModuloHtml(ruta, titulo);
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Config modulo embebido: " + ruta);
    }

    private void CloseConfig()
    {
        if (_configHost == null) return;
        // Detach apaga el refresco y deja que la pestaña activa guarde lo que
        // tenga pendiente (igual que el visibilitychange de la página HTML).
        _configHost.Detach();
        _configHost.IsVisible = false;
        bool webViewVisibleCfg = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleCfg)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Config closed -> back to native map");
    }

    private void CloseSonidos()
    {
        if (_sonidosHost == null) return;
        _sonidosHost.Detach();
        _sonidosHost.IsVisible = false;
        bool webViewVisibleSn = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleSn)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Sonidos closed -> back to native map");
    }

    // ----- Visor de EVENTOS: reemplazo nativo de pages/eventos.html (ex
    // FormEventViewer). El registro de la sesión + el histórico, con el mismo
    // botón "Actualizar" y sin polling. Card clara flotante, mapa VIVO detrás;
    // la página HTML queda intacta para la PWA del celular.

    private void ShowEventos()
    {
        if (_eventosHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _eventosClient ??= new EventosClient(DeriveOrigin(App.TargetUrl));
        _eventosHost.Attach(_eventosClient);
        _eventosHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Eventos open (nativo, no WebView)");
    }

    private void CloseEventos()
    {
        if (_eventosHost == null) return;
        // Detach cancela la lectura del log en vuelo (puede ser de 256 KB).
        _eventosHost.Detach();
        _eventosHost.IsVisible = false;
        bool webViewVisibleEv = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleEv)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Eventos closed -> back to native map");
    }

    // ----- CALCULADORA DE SIEMBRA: reemplazo nativo de
    // pages/calculadora-siembra.html. Las cuatro cuentas de gruesa (densidad,
    // prueba de campo, PMS y motor), precargadas con la geometría real de la
    // máquina. Card clara flotante, mapa VIVO detrás; la página HTML queda
    // intacta para la PWA del celular.

    private void ShowCalculadora()
    {
        if (_calculadoraHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _calculadoraClient ??= new CalculadoraSiembraClient(DeriveOrigin(App.TargetUrl));
        _calculadoraHost.Attach(_calculadoraClient);
        _calculadoraHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Calculadora open (nativo, no WebView)");
    }

    private void CloseCalculadora()
    {
        if (_calculadoraHost == null) return;
        // Detach cancela la precarga en vuelo y baja el teclado nativo: si no,
        // quedaria abierto arriba del mapa.
        _calculadoraHost.Detach();
        _calculadoraHost.IsVisible = false;
        bool webViewVisibleCa = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleCa)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Calculadora closed -> back to native map");
    }

    // ----- PERFILES: reemplazo nativo de pages/perfiles.html. Un perfil son
    // TODAS las configuraciones del vehículo, así que la pantalla es
    // destructiva (crear / cargar / copiar / proteger / borrar) y los guards
    // son los mismos que los del JS. Polling 5 s que corta el Detach.

    private void ShowPerfiles()
    {
        if (_perfilesHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _perfilesClient ??= new PerfilesClient(DeriveOrigin(App.TargetUrl));
        _perfilesHost.Attach(_perfilesClient);
        _perfilesHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Perfiles open (nativo, no WebView)");
    }

    private void ClosePerfiles()
    {
        if (_perfilesHost == null) return;
        // Detach corta el polling de 5 s, desarma el diálogo abierto y baja el
        // teclado nativo: cerrado no se hace red ni queda nada arriba del mapa.
        _perfilesHost.Detach();
        _perfilesHost.IsVisible = false;
        bool webViewVisiblePf = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisiblePf)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Perfiles closed -> back to native map");
    }

    // ----- FIRMWARES: reemplazo nativo de pages/firmwares.html. Catálogo local
    // de .bin para cargar desde USB sin internet (cap 8 MB) y borrarlos. Card
    // clara flotante, mapa VIVO detrás; la página HTML queda intacta para la
    // PWA del celular.

    private void ShowFirmwares()
    {
        if (_firmwaresHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _firmwaresClient ??= new FirmwaresClient(DeriveOrigin(App.TargetUrl));
        _firmwaresHost.Attach(_firmwaresClient);
        _firmwaresHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Firmwares open (nativo, no WebView)");
    }

    private void CloseFirmwares()
    {
        if (_firmwaresHost == null) return;
        // Detach cancela la request en vuelo, cierra modal/picker y baja el
        // teclado nativo.
        _firmwaresHost.Detach();
        _firmwaresHost.IsVisible = false;
        bool webViewVisibleFw = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleFw)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Firmwares closed -> back to native map");
    }

    // ----- BANDERAS: reemplazo nativo de pages/banderas.html (ex FormFlags +
    // FormEnterFlag). Se marca una piedra o un pozo MANEJANDO y la lista da la
    // distancia en vivo (poll 500 ms), o sea que lo que hay que ver es el mapa.
    // El ciclo de vida NO es Attach/IsVisible: Abrir(enAlta) prende el panel y
    // arranca el poll, Cerrar() lo apaga y manda el POST /close.

    private void ShowBanderas(bool enAlta)
    {
        if (_banderasHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos). Contorno,
        // Cabecera, Suavizar AB y Corregir posición están ANCLADOS EN EL MISMO
        // lugar que esta card (derecha, margen 82).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa. El Attach es
        // solo inyección — el que prende el panel y el poll es Abrir().
        _banderasClient ??= new BanderasClient(DeriveOrigin(App.TargetUrl));
        _banderasHost.Attach(_banderasClient);
        _banderasHost.Abrir(enAlta);
        // Card flotante: el mapa NUNCA se apaga (acá menos que nunca — la
        // bandera recién marcada se ve ahí).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        bool hayWebViewBd = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !hayWebViewBd) _webViewBack.IsVisible = false;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Banderas open (nativo, alta=" + enAlta + ")");
    }

    private void CloseBanderas()
    {
        if (_banderasHost == null) return;
        // Cerrar() apaga el poll, esconde la card y manda el POST /close
        // (deselecciona + guarda). Es idempotente: si el panel se cerró solo
        // (✕ propio), esta segunda llamada no repite nada.
        _banderasHost.Cerrar();
        bool webViewVisibleBd = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleBd)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Banderas closed -> back to native map");
    }

    // ----- GRÁFICO DE DIRECCIÓN: reemplazo nativo de
    // pages/grafico-direccion.html (ex FormGraphSteer). Muestra el ángulo
    // real contra el pedido a 5 Hz: se mira MANEJANDO, así que va como card
    // flotante con el mapa vivo detrás. La página HTML queda intacta para la
    // PWA del celular.

    private void ShowGrafDireccion()
    {
        if (_grafDireccionHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: UN SOLO cliente para los cuatro gráficos (es stateless).
        _graficosClient ??= new GraficosClient(DeriveOrigin(App.TargetUrl));
        _grafDireccionHost.Attach(_graficosClient);
        _grafDireccionHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Grafico direccion open (nativo, no WebView)");
    }

    private void CloseGrafDireccion()
    {
        if (_grafDireccionHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _grafDireccionHost.Detach();
        _grafDireccionHost.IsVisible = false;
        bool webViewVisibleGd = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleGd)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Grafico direccion closed -> back to native map");
    }

    // ----- GRÁFICO DE RUMBO: reemplazo nativo de pages/grafico-rumbo.html.
    // Compara las fuentes de rumbo (GPS / IMU / fusión) mientras la máquina
    // anda. Card flotante, mapa VIVO detrás; la página HTML queda intacta
    // para la PWA del celular.

    private void ShowGrafRumbo()
    {
        if (_grafRumboHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: UN SOLO cliente para los cuatro gráficos (es stateless).
        _graficosClient ??= new GraficosClient(DeriveOrigin(App.TargetUrl));
        _grafRumboHost.Attach(_graficosClient);
        _grafRumboHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Grafico rumbo open (nativo, no WebView)");
    }

    private void CloseGrafRumbo()
    {
        if (_grafRumboHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _grafRumboHost.Detach();
        _grafRumboHost.IsVisible = false;
        bool webViewVisibleGr = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleGr)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Grafico rumbo closed -> back to native map");
    }

    // ----- GRÁFICO DE XTE: reemplazo nativo de pages/grafico-xte.html. El
    // error a la línea en el tiempo — se mira con el tractor andando, así que
    // el mapa tiene que seguir vivo detrás. La página HTML queda intacta para
    // la PWA del celular.

    private void ShowGrafXte()
    {
        if (_grafXteHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: UN SOLO cliente para los cuatro gráficos (es stateless).
        _graficosClient ??= new GraficosClient(DeriveOrigin(App.TargetUrl));
        _grafXteHost.Attach(_graficosClient);
        _grafXteHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Grafico XTE open (nativo, no WebView)");
    }

    private void CloseGrafXte()
    {
        if (_grafXteHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _grafXteHost.Detach();
        _grafXteHost.IsVisible = false;
        bool webViewVisibleGx = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleGx)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Grafico XTE closed -> back to native map");
    }

    // ----- CHEQUEO DE ROLL (corrección): reemplazo nativo de
    // pages/grafico-correccion.html. Es el comando "chequeo_roll" del menú:
    // se mira el efecto de la corrección de rolido en vivo. Card flotante,
    // mapa VIVO detrás; la página HTML queda intacta para la PWA del celular.

    private void ShowGrafCorreccion()
    {
        if (_grafCorreccionHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: UN SOLO cliente para los cuatro gráficos (es stateless).
        _graficosClient ??= new GraficosClient(DeriveOrigin(App.TargetUrl));
        _grafCorreccionHost.Attach(_graficosClient);
        _grafCorreccionHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Chequeo de roll open (nativo, no WebView)");
    }

    private void CloseGrafCorreccion()
    {
        if (_grafCorreccionHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _grafCorreccionHost.Detach();
        _grafCorreccionHost.IsVisible = false;
        bool webViewVisibleGc = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleGc)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Chequeo de roll closed -> back to native map");
    }

    // ----- ORBITX CLOUD: reemplazo nativo de pages/orbitx.html. Vinculación
    // del tractor con el cloud (código de pareo cada 4 s, estado del sync
    // cada 10 s) y el atajo a Prescripciones. Card clara flotante, mapa VIVO
    // detrás; la página HTML queda intacta para la PWA del celular.

    private void ShowOrbitX()
    {
        if (_orbitXHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _orbitXClient ??= new OrbitXPanelClient(DeriveOrigin(App.TargetUrl));
        _orbitXHost.Attach(_orbitXClient);
        _orbitXHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] OrbitX open (nativo, no WebView)");
    }

    private void CloseOrbitX()
    {
        if (_orbitXHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _orbitXHost.Detach();
        _orbitXHost.IsVisible = false;
        bool webViewVisibleOx = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleOx)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] OrbitX closed -> back to native map");
    }

    // ----- RED WIFI: reemplazo nativo de pages/wifi.html. El WiFi PROPIO de
    // la pantalla (escanear, conectar, olvidar), que es de lo que depende que
    // los nodos y el cloud lleguen. Card clara flotante, mapa VIVO detrás; la
    // página HTML queda intacta para la PWA del celular.

    private void ShowWifi()
    {
        if (_wifiHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _wifiClient ??= new RedWifiClient(DeriveOrigin(App.TargetUrl));
        _wifiHost.Attach(_wifiClient);
        _wifiHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Red WiFi open (nativo, no WebView)");
    }

    private void CloseWifi()
    {
        if (_wifiHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _wifiHost.Detach();
        _wifiHost.IsVisible = false;
        bool webViewVisibleWf = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleWf)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Red WiFi closed -> back to native map");
    }

    // ----- DEBUG: reemplazo nativo de pages/debug.html. El log unificado de
    // todos los servicios, con sus filtros. Card clara flotante, mapa VIVO
    // detrás; la página HTML queda intacta para la PWA del celular.

    private void ShowDebug()
    {
        if (_debugHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _debugClient ??= new DebugClient(DeriveOrigin(App.TargetUrl));
        _debugHost.Attach(_debugClient);
        _debugHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Debug open (nativo, no WebView)");
    }

    private void CloseDebug()
    {
        if (_debugHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _debugHost.Detach();
        _debugHost.IsVisible = false;
        bool webViewVisibleDbg = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleDbg)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Debug closed -> back to native map");
    }

    // ----- CATÁLOGO DE INSUMOS: reemplazo nativo de pages/insumos.html. Es
    // el catálogo COMPARTIDO (VistaX lo abre desde su editor). Card clara
    // flotante, mapa VIVO detrás; la página HTML queda intacta para la PWA
    // del celular.

    private void ShowInsumos()
    {
        if (_insumosHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _insumosClient ??= new InsumosClient(DeriveOrigin(App.TargetUrl));
        _insumosHost.Attach(_insumosClient);
        _insumosHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Insumos open (nativo, no WebView)");
    }

    private void CloseInsumos()
    {
        if (_insumosHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _insumosHost.Detach();
        _insumosHost.IsVisible = false;
        bool webViewVisibleIns = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleIns)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Insumos closed -> back to native map");
    }

    // ----- MAPAS: reemplazo nativo de pages/mapas.html. Preview y
    // exportación de los mapas del lote. Card clara flotante, mapa VIVO
    // detrás; la página HTML queda intacta para la PWA del celular.

    private void ShowMapas()
    {
        if (_mapasHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_nodoDetalleHost != null && _nodoDetalleHost.IsVisible) { _nodoDetalleHost.Detach(); _nodoDetalleHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _mapasClient ??= new MapasClient(DeriveOrigin(App.TargetUrl));
        _mapasHost.Attach(_mapasClient);
        _mapasHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Mapas open (nativo, no WebView)");
    }

    private void CloseMapas()
    {
        if (_mapasHost == null) return;
        // Detach corta el polling y baja lo que el panel haya dejado arriba
        // (diálogo, teclado nativo): cerrado no se hace red ni queda nada
        // flotando sobre el mapa.
        _mapasHost.Detach();
        _mapasHost.IsVisible = false;
        bool webViewVisibleMp = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleMp)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Mapas closed -> back to native map");
    }

    // ----- DETALLE DE NODO: reemplazo nativo de pages/nodo-detalle.html.
    // Se entra SIEMPRE tocando una fila de Nodos: el UID sale del
    // announcement MQTT, acá no se escribe a mano ni se da de alta nada.
    // Abrir(uid) y Attach son conmutativos, así que el orden no importa.
    // OJO: el Detach de este panel apaga el POLLING y NADA MÁS — un OTA en
    // curso lo siguen manejando el nodo y el coordinator, y cerrar la
    // pantalla no lo cancela (ni tiene que cancelarlo).

    private void ShowNodoDetalle(string? uid)
    {
        if (_nodoDetalleHost == null) return;
        // Solo un overlay a la vez.
        if (_fieldDataHost != null && _fieldDataHost.IsVisible) _fieldDataHost.IsVisible = false;
        if (_sistemaHost   != null && _sistemaHost.IsVisible)   { _sistemaHost.Reset(); _sistemaHost.IsVisible = false; }
        if (_gpsDataHost   != null && _gpsDataHost.IsVisible)   _gpsDataHost.IsVisible = false;
        if (_stormXHost    != null && _stormXHost.IsVisible)    { _stormXHost.Detach(); _stormXHost.IsVisible = false; }
        if (_flowXHost     != null && _flowXHost.IsVisible)     { _flowXHost.Detach(); _flowXHost.IsVisible = false; }
        if (_flowXEditorHost != null && _flowXEditorHost.IsVisible) { _flowXEditorHost.Detach(); _flowXEditorHost.IsVisible = false; }
        if (_sectionXHost  != null && _sectionXHost.IsVisible)  { _sectionXHost.Detach(); _sectionXHost.IsVisible = false; }
        if (_quantiXHost   != null && _quantiXHost.IsVisible)   { _quantiXHost.Detach(); _quantiXHost.IsVisible = false; }
        if (_quantiXEditorHost != null && _quantiXEditorHost.IsVisible) { _quantiXEditorHost.Detach(); _quantiXEditorHost.IsVisible = false; }
        if (_vistaXEditorHost != null && _vistaXEditorHost.IsVisible) { _vistaXEditorHost.Detach(); _vistaXEditorHost.IsVisible = false; }
        if (_vistaXHost    != null && _vistaXHost.IsVisible)    { _vistaXHost.Detach(); _vistaXHost.IsVisible = false; }
        if (_coreXEcuHost  != null && _coreXEcuHost.IsVisible)  { _coreXEcuHost.Detach(); _coreXEcuHost.IsVisible = false; }
        if (_hubHost       != null && _hubHost.IsVisible)       { _hubHost.Detach(); _hubHost.IsVisible = false; }
        if (_nodosHost     != null && _nodosHost.IsVisible)     { _nodosHost.Detach(); _nodosHost.IsVisible = false; }
        if (_actualizarHost!= null && _actualizarHost.IsVisible){ _actualizarHost.Detach(); _actualizarHost.IsVisible = false; }
        if (_camarasHost   != null && _camarasHost.IsVisible)   { _camarasHost.Detach(); _camarasHost.IsVisible = false; }
        if (_sonidosHost   != null && _sonidosHost.IsVisible)   { _sonidosHost.Detach(); _sonidosHost.IsVisible = false; }
        if (_configHost    != null && _configHost.IsVisible)    { _configHost.Detach(); _configHost.IsVisible = false; }
        if (_eventosHost   != null && _eventosHost.IsVisible)   { _eventosHost.Detach(); _eventosHost.IsVisible = false; }
        if (_calculadoraHost != null && _calculadoraHost.IsVisible) { _calculadoraHost.Detach(); _calculadoraHost.IsVisible = false; }
        if (_perfilesHost  != null && _perfilesHost.IsVisible)  { _perfilesHost.Detach(); _perfilesHost.IsVisible = false; }
        if (_firmwaresHost != null && _firmwaresHost.IsVisible) { _firmwaresHost.Detach(); _firmwaresHost.IsVisible = false; }
        // Paneles nativos de la ola 2026-08-18 (los 4 graficos, OrbitX,
        // Red WiFi, Debug, Insumos, Mapas y el detalle de nodo): mismo
        // ZIndex que estas cards, asi que si no se cierran quedan dos
        // pisadas sobre el mapa. Detach() apaga sus polls.
        if (_grafDireccionHost != null && _grafDireccionHost.IsVisible) { _grafDireccionHost.Detach(); _grafDireccionHost.IsVisible = false; }
        if (_grafRumboHost != null && _grafRumboHost.IsVisible) { _grafRumboHost.Detach(); _grafRumboHost.IsVisible = false; }
        if (_grafXteHost != null && _grafXteHost.IsVisible) { _grafXteHost.Detach(); _grafXteHost.IsVisible = false; }
        if (_grafCorreccionHost != null && _grafCorreccionHost.IsVisible) { _grafCorreccionHost.Detach(); _grafCorreccionHost.IsVisible = false; }
        if (_orbitXHost != null && _orbitXHost.IsVisible) { _orbitXHost.Detach(); _orbitXHost.IsVisible = false; }
        if (_wifiHost != null && _wifiHost.IsVisible) { _wifiHost.Detach(); _wifiHost.IsVisible = false; }
        if (_debugHost != null && _debugHost.IsVisible) { _debugHost.Detach(); _debugHost.IsVisible = false; }
        if (_insumosHost != null && _insumosHost.IsVisible) { _insumosHost.Detach(); _insumosHost.IsVisible = false; }
        if (_mapasHost != null && _mapasHost.IsVisible) { _mapasHost.Detach(); _mapasHost.IsVisible = false; }
        if (_banderasHost  != null && _banderasHost.IsVisible)  _banderasHost.Cerrar();
        // Paneles que flotan sobre el mapa con el mismo ZIndex: si no se cierran,
        // quedan dos cards pisadas (mismo criterio que ShowSonidos).
        if (_guiasHost     != null && _guiasHost.IsVisible)     _guiasHost.Cerrar();
        if (_loteHost      != null && _loteHost.IsVisible)      _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost  != null && _contornoHost.IsVisible)  _contornoHost.Cerrar();
        if (_cabeceraHost  != null && _cabeceraHost.IsVisible)  _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _nodoDetalleClient ??= new NodoDetalleClient(DeriveOrigin(App.TargetUrl));
        _nodoDetalleHost.Attach(_nodoDetalleClient);
        _nodoDetalleHost.Abrir(uid ?? string.Empty);
        _nodoDetalleHost.IsVisible = true;
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Nodo detalle open (nativo, no WebView)");
    }

    private void CloseNodoDetalle()
    {
        if (_nodoDetalleHost == null) return;
        // Apaga el polling, cierra el modal y el toast. NO toca el OTA.
        _nodoDetalleHost.Detach();
        _nodoDetalleHost.IsVisible = false;
        bool webViewVisibleNd = _webView != null && (_webViewSlot?.IsVisible ?? false);
        if (_webViewBack != null && !webViewVisibleNd)
            _webViewBack.IsVisible = false;
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Nodo detalle closed -> back to native map");
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
            // El texto se ARMA acá, así que el recorrido del árbol no lo puede
            // traducir (nunca coincide entero con una entrada del diccionario):
            // se traduce la parte de palabras y los números quedan como están.
            s = hasT
                ? $"T {_lastTractorHeadingDeg:0}°  ·  {PilotX.Cockpit.Bars.Traductor.T("sin guía")}"
                : "";
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

        // La franja de VistaX se apila SOBRE las secciones: cuando esa barra
        // aparece, cambia de tamaño o se oculta (según haya lote), hay que
        // recolocarla o queda tapada.
        var seccionesFloat = this.FindControl<Border>("SeccionesFloat");
        if (seccionesFloat != null)
        {
            seccionesFloat.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty || e.Property == IsVisibleProperty)
                {
                    UbicarVxStrip();
                    UbicarQxBar();
                }
            };
        }

        if (_vxMapStrip != null)
        {
            // Tocar la franja FUERA de una barra: NADA (pedido 2026-08-07 —
            // abría el panel VistaX a pantalla completa por un toque al gris,
            // desproporcionado igual que lo del 2026-08-06 con las barras).
            // El panel completo se abre desde el Hub o el menú, a propósito.
            _vxMapStrip.OnTap = null;
            // Tocar UNA barra: ficha chica de ESE sensor (pedido 2026-08-06 —
            // abrir el panel entero para mirar un surco era desproporcionado).
            _vxMapStrip.OnTapSurco = s => MostrarFichaSurco(s);
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
                if (e.Property == BoundsProperty) ReclampOverlayQx();
                if (e.Property == BoundsProperty) ReclampOverlayFx();
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

        // Ídem FlowX: al soltarlo se guarda dónde quedó (merge, no pisa lo demás).
        if (_fxMapOverlay != null)
            _fxMapOverlay.OnMovido = pt =>
            {
                var c = _overlaysClient;
                if (c == null) return;
                _ = c.SavePosFxAsync((int)Math.Round(pt.X), (int)Math.Round(pt.Y));
            };

        _overlayPrefsCts = new CancellationTokenSource();
        _ = SeguirPreferenciasOverlaysAsync(_overlayPrefsCts.Token);
    }

    private void UbicarOverlayQx(int x, int y)
    {
        if (_qxMapOverlay == null || _mapOverlaysHost == null) return;
        if (x >= 0 && y >= 0)
        {
            // La preferencia pudo guardarse en una pantalla MÁS GRANDE (o la
            // ventana achicarse): sin clamp el widget quedaba fuera de la
            // vista y el operario lo daba por desaparecido.
            var (cx, cy) = ClampOverlayAlHost(_qxMapOverlay, x, y);
            Canvas.SetLeft(_qxMapOverlay, cx);
            Canvas.SetTop(_qxMapOverlay, cy);
            return;
        }
        // Default: abajo a la izquierda, al lado del menú lateral (140 px) y
        // arriba de la barra inferior. El centro queda libre para el tractor.
        // Ese rincón lo ocupaba el mini-mapa, que se sacó.
        Canvas.SetLeft(_qxMapOverlay, 155);
        double alto = _mapOverlaysHost.Bounds.Height;
        Canvas.SetTop(_qxMapOverlay, alto > 260 ? alto - 235 : 40);
    }

    /// <summary>Deja un widget flotante ENTERO adentro del canvas del mapa.
    /// Si el host todavía no midió (arranque) devuelve la posición tal cual;
    /// el re-clamp llega con el próximo cambio de Bounds del host.</summary>
    private (double X, double Y) ClampOverlayAlHost(Control c, double x, double y)
    {
        double hostW = _mapOverlaysHost?.Bounds.Width ?? 0;
        double hostH = _mapOverlaysHost?.Bounds.Height ?? 0;
        if (hostW < 200 || hostH < 150) return (x, y);
        double w = c.Bounds.Width > 0 ? c.Bounds.Width : 220;
        double h = c.Bounds.Height > 0 ? c.Bounds.Height : 120;
        return (Math.Max(0, Math.Min(x, hostW - w)),
                Math.Max(0, Math.Min(y, hostH - h)));
    }

    /// <summary>Re-clamp del widget QuantiX cuando el canvas cambia de tamaño
    /// (ventana más chica, pantalla distinta): lo trae de vuelta a la vista.</summary>
    private void ReclampOverlayQx()
    {
        if (_qxMapOverlay == null || !_qxMapOverlay.IsVisible) return;
        double x = Canvas.GetLeft(_qxMapOverlay), y = Canvas.GetTop(_qxMapOverlay);
        if (double.IsNaN(x) || double.IsNaN(y)) return;
        var (cx, cy) = ClampOverlayAlHost(_qxMapOverlay, x, y);
        if (Math.Abs(cx - x) > 0.5 || Math.Abs(cy - y) > 0.5)
        {
            Canvas.SetLeft(_qxMapOverlay, cx);
            Canvas.SetTop(_qxMapOverlay, cy);
        }
    }

    // Overlay de corrección lateral: flotante y centrado abajo, despegado del
    // borde para no tapar la barra de secciones.
    private Border? _nudgeOverlay;
    private Button? _btnNudgeCentro;
    private Border? _sistemaMenu;
    private Border? _herramientasMenu;

    /// <summary>Deja el panel de Herramientas centrado sobre su botón de la
    /// barra de la pasada (que se corre según qué botones apliquen).</summary>
    private void UbicarHerramientasMenu()
    {
        if (_herramientasMenu == null) return;
        try
        {
            var btn = this.FindControl<Button>("BtnOvHerramientas");
            if (btn == null || btn.Bounds.Width <= 0) return;
            var p = btn.TranslatePoint(new Point(btn.Bounds.Width / 2, 0), this);
            if (!p.HasValue) return;

            double ancho = _herramientasMenu.Bounds.Width > 0 ? _herramientasMenu.Bounds.Width : 460;
            double x = p.Value.X - ancho / 2;
            double maxX = Math.Max(0, Bounds.Width - ancho - 8);
            x = Math.Max(8, Math.Min(x, maxX));

            // Justo ARRIBA de la barra de la pasada, con aire.
            double desdeAbajo = Math.Max(0, Bounds.Height - p.Value.Y) + 10;
            _herramientasMenu.Margin = new Thickness(x, 0, 0, desdeAbajo);
        }
        catch { }
    }

    // ---- ficha chica de UN surco de VistaX --------------------------------
    //
    // Tocar una barra de la franja abría el panel VistaX entero ("una ventana
    // gigante" para mirar un sensor). Esto muestra SOLO ese surco, en una
    // ventana del tamaño de una tarjeta, y se refresca en vivo mientras esté
    // abierta. Cerrar: el botón o la X.
    private Window? _fichaSurcoWin;
    private System.Threading.CancellationTokenSource? _fichaSurcoCts;

    private void MostrarFichaSurco(Services.VistaXSurcoLive surco)
    {
        if (surco == null) return;
        try
        {
            _fichaSurcoWin?.Close();

            int bajada = surco.Bajada;
            var titulo = new TextBlock
            {
                Text = "Surco " + bajada,
                FontSize = 20,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Avalonia.Media.Brushes.Black,
            };
            var estado = new TextBlock { FontSize = 14, Foreground = Avalonia.Media.Brushes.Black };
            var spm = new TextBlock { FontSize = 30, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = Avalonia.Media.Brushes.Black };
            var obj = new TextBlock { FontSize = 13, Foreground = Avalonia.Media.Brush.Parse("#535E54") };
            var extra = new TextBlock { FontSize = 12, Foreground = Avalonia.Media.Brush.Parse("#535E54"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var cerrar = new Button
            {
                Content = "Cerrar",
                Height = 42,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };

            var panel = new StackPanel { Spacing = 6, Margin = new Thickness(14) };
            panel.Children.Add(titulo);
            panel.Children.Add(estado);
            panel.Children.Add(spm);
            panel.Children.Add(obj);
            panel.Children.Add(extra);
            panel.Children.Add(cerrar);

            var win = new Window
            {
                Title = "Surco " + bajada,
                Width = 250,
                Height = 260,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.Full,
                ShowInTaskbar = false,
                Background = Avalonia.Media.Brush.Parse("#F5F7F4"),
                Content = panel,
            };
            _fichaSurcoWin = win;
            cerrar.Click += (_, __) => { try { win.Close(); } catch { } };

            // Todo en SEMILLAS POR METRO, que es como se piensa la siembra
            // (regla del producto: unidades agronómicas al operario). El
            // backend entrega sem/MINUTO, así que se divide por los metros por
            // minuto de la velocidad actual. Mostrar objetivo en sem/min y
            // medido en sem/m era comparar peras con manzanas (2026-08-06).
            void Pintar(Services.VistaXSurcoLive s, double mMin)
            {
                string e = (s.Estado ?? "sin datos").ToLowerInvariant();
                estado.Text = e switch
                {
                    "ok" => "Sembrando bien",
                    "bajo" => "Por debajo del objetivo",
                    "exceso" => "Por encima del objetivo",
                    "tapado" => "TAPADO — no pasa semilla",
                    "falla" => "FALLA",
                    "seccion-off" => "Sección cortada",
                    "no-data" or "idle" => "Sin datos",
                    _ => e,
                };

                if (mMin > 1)
                {
                    spm.Text = (s.Spm / mMin).ToString("N1", CultureInfo.InvariantCulture) + " sem/m";
                    obj.Text = s.Objetivo > 0
                        ? "Objetivo: " + (s.Objetivo / mMin).ToString("N1", CultureInfo.InvariantCulture) + " sem/m"
                        : "Objetivo: sin definir";
                }
                else
                {
                    // Parado no hay sem/m que valga (dividiría por cero): se
                    // muestra el ritmo instantáneo, que es lo único real.
                    spm.Text = s.Spm.ToString("N0", CultureInfo.InvariantCulture) + " sem/min";
                    obj.Text = "detenido — sin sem/m";
                }

                // Sin sem/min: al operario no le dice nada (pedido 2026-08-06).
                extra.Text = "Cable " + s.Cable + " · tren " + s.Tren
                           + (string.IsNullOrEmpty(s.Tipo) ? "" : " · " + s.Tipo)
                           + (s.Muted ? " · SILENCIADO" : "")
                           + "\n" + (s.Uid ?? "");
            }
            Pintar(surco, 0);

            // Refresco en vivo mientras la ficha esté abierta.
            var cts = new System.Threading.CancellationTokenSource();
            _fichaSurcoCts = cts;
            win.Closed += (_, __) =>
            {
                try { cts.Cancel(); } catch { }
                if (ReferenceEquals(_fichaSurcoWin, win)) _fichaSurcoWin = null;
            };
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                // El mismo cliente que alimenta la franja (ya apunta al origen
                // correcto); si por lo que sea no está, se crea uno propio.
                var cli = _vxStripClient ?? new VistaXClient(DeriveOrigin(App.TargetUrl));
                while (!cts.IsCancellationRequested && cli != null)
                {
                    try
                    {
                        var live = await cli.GetLiveAsync(cts.Token).ConfigureAwait(false);
                        Services.VistaXSurcoLive? act = null;
                        if (live?.Trenes != null)
                            foreach (var t in live.Trenes)
                                if (t?.Surcos != null)
                                    foreach (var s in t.Surcos)
                                        if (s != null && s.Bajada == bajada) { act = s; break; }
                        if (act != null)
                        {
                            // Metros por minuto de la velocidad actual: con eso
                            // se pasa de sem/min (lo que da el backend) a sem/m.
                            double mMin = (live?.Velocidad ?? 0) * 1000.0 / 60.0;
                            var actual = act;
                            await Dispatcher.UIThread.InvokeAsync(() => Pintar(actual, mMin));
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch { }
                    try { await System.Threading.Tasks.Task.Delay(500, cts.Token).ConfigureAwait(false); }
                    catch { return; }
                }
            });

            win.Show(this);
        }
        catch { /* la ficha nunca puede voltear la pantalla principal */ }
    }

    /// <summary>Alinea el panel SISTEMA con el borde izquierdo de su botón.</summary>
    private void UbicarSistemaMenu()
    {
        if (_sistemaMenu == null) return;
        try
        {
            var barra = this.FindControl<PilotX.Cockpit.Bars.Views.BarraSuperior>("BarSuperior");
            var btn = barra?.FindControl<Button>("BtnSistema");
            if (btn == null || btn.Bounds.Width <= 0) return;
            var p = btn.TranslatePoint(new Point(0, 0), this);
            if (!p.HasValue) return;
            double x = Math.Max(0, p.Value.X);
            _sistemaMenu.Margin = new Thickness(x, 6, 0, 0);
        }
        catch { /* si no se puede medir, queda donde estaba */ }
    }

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
        // Pantalla angosta (taller 1024x768): si la barra no entra, se ESCALA
        // hacia abajo y se ARRIMA al borde izquierdo, pegada al riel plegado
        // (~36 px + el chevrón) — así queda aire a la derecha para los toasts
        // y el riel derecho. En pantallas grandes el factor es 1, la barra
        // sigue centrada sobre el eje del tractor y nada cambia.
        //
        // El 0.88 EXTRA (pedido 2026-08-14, "un poco más chica") multiplicaba
        // encima del factor que ya hace entrar la barra, y en 1024 el resultado
        // era 0,85: botones de 47,7 px y TÍTULOS DE 8,5 px (con las marcas de
        // giro puestas, 44,6 y 7,9). Con sol de frente y la máquina andando eso
        // no se lee ni se acierta con guante. Se conserva la intención —la barra
        // sigue más chica de lo que entra— pero con un piso: el factor nunca
        // baja del que deja el título en 9,5 px, y nunca sube del que hace que
        // la barra entre. En 1024 queda 0,95 ⇒ botones de 53 px y títulos de
        // 9,5. Ningún botón se mueve de lugar: es la misma barra, escalada.
        const double margenAngosto = 56;
        const double pisoLectura = 0.95;    // 9,5 px de título sobre los 10 nominales
        double disponible = hostW - margenAngosto - 6;
        bool angosta = disponible > 0 && w > disponible;
        double entra = angosta ? disponible / w : 1.0;
        double f = angosta ? Math.Min(entra, Math.Max(entra * 0.88, pisoLectura)) : 1.0;
        if (f < 1.0)
        {
            _nudgeOverlay.RenderTransformOrigin =
                new RelativePoint(0, 0, RelativeUnit.Relative);
            _nudgeOverlay.RenderTransform = new Avalonia.Media.ScaleTransform(f, f);
        }
        else
        {
            _nudgeOverlay.RenderTransform = null;
        }
        double wf = w * f, hf = h * f;

        double x;
        if (angosta)
        {
            // Angosta: alineada a la izquierda; centrar sobre el eje acá no
            // aplica porque la barra ocupa casi todo el ancho igual.
            x = margenAngosto;
        }
        else
        {
            x = (hostW - wf) / 2;
            var cen = _btnNudgeCentro;
            if (cen != null && cen.Bounds.Width > 0)
            {
                var p = cen.TranslatePoint(new Point(cen.Bounds.Width / 2, 0), _nudgeOverlay);
                if (p.HasValue) x = hostW / 2 - p.Value.X * f;
            }
            x = Math.Max(150, Math.Min(x, hostW - wf - 6));
        }
        Canvas.SetLeft(_nudgeOverlay, x);
        // Pegada al borde inferior (intercambio 2026-08-05): la pasada ocupa
        // el lugar que tenía la barra de secciones, y las secciones flotan
        // arriba (SeccionesFloat). El 6 es solo aire contra el borde. Con
        // escala, la altura VISUAL es h*f (RenderTransform no cambia Bounds).
        Canvas.SetTop(_nudgeOverlay, Math.Max(0, hostH - hf - 6));
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
        // La barra de secciones NO vive en este canvas (está en el grid de las
        // barras del cockpit, que arranca debajo de la barra superior), así que
        // calcular su tope con constantes del XAML daba un valor corrido y la
        // franja de VistaX terminaba ENTERRADA abajo de las secciones (reporte
        // 2026-08-06). Se mide su posición real y se traduce a coordenadas de
        // este canvas.
        var secciones = this.FindControl<Border>("SeccionesFloat");
        if (secciones != null && secciones.IsVisible && secciones.Bounds.Height > 0)
        {
            try
            {
                var p = secciones.TranslatePoint(new Point(0, 0), _mapOverlaysHost!);
                if (p.HasValue) tope = Math.Min(tope, p.Value.Y);
                else tope = Math.Min(tope, hostH - 96 - secciones.Bounds.Height);
            }
            catch { tope = Math.Min(tope, hostH - 96 - secciones.Bounds.Height); }
        }
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

    private void UbicarFxOverlay(int x = -1, int y = -1)
    {
        if (_fxMapOverlay == null || _mapOverlaysHost == null) return;
        double hostW = _mapOverlaysHost.Bounds.Width;
        if (hostW < 300) return;
        // Posición guardada (el operario lo arrastró): respetarla, con clamp
        // por si se guardó en una pantalla más grande. Igual que QuantiX.
        if (x >= 0 && y >= 0)
        {
            var (cx, cy) = ClampOverlayAlHost(_fxMapOverlay, x, y);
            Canvas.SetLeft(_fxMapOverlay, cx);
            Canvas.SetTop(_fxMapOverlay, cy);
            return;
        }
        double w = double.IsNaN(_fxMapOverlay.Width) ? 236 : _fxMapOverlay.Width;
        // Default: arriba a la derecha, debajo de los botones de zoom
        // (50+50+margenes): el centro del mapa (tractor) y la barra derecha
        // quedan libres.
        Canvas.SetLeft(_fxMapOverlay, Math.Max(150, hostW - w - 64));
        Canvas.SetTop(_fxMapOverlay, 130);
    }

    /// <summary>Re-clamp del overlay FlowX cuando el canvas cambia de tamaño.</summary>
    private void ReclampOverlayFx()
    {
        if (_fxMapOverlay == null || !_fxMapOverlay.IsVisible) return;
        double x = Canvas.GetLeft(_fxMapOverlay), y = Canvas.GetTop(_fxMapOverlay);
        if (double.IsNaN(x) || double.IsNaN(y)) return;
        var (cx, cy) = ClampOverlayAlHost(_fxMapOverlay, x, y);
        if (Math.Abs(cx - x) > 0.5 || Math.Abs(cy - y) > 0.5)
        {
            Canvas.SetLeft(_fxMapOverlay, cx);
            Canvas.SetTop(_fxMapOverlay, cy);
        }
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

                    // Overlay FlowX (pulverización + StormX): mismo criterio.
                    if (_fxMapOverlay != null)
                    {
                        bool mostrarFx = prefs.FxOverlay;
                        if (mostrarFx != _fxMapOverlay.IsVisible)
                        {
                            _fxMapOverlay.IsVisible = mostrarFx;
                            if (mostrarFx)
                            {
                                _fxOverlayHttp ??= new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                                _fxMapOverlay.Attach(_fxOverlayHttp, DeriveOrigin(App.TargetUrl));
                                UbicarFxOverlay(prefs.FxX, prefs.FxY);
                            }
                            else _fxMapOverlay.Detach();
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
            // El ✕ apagaba PilotX A UN TOQUE, y vive a 4 px del de maximizar:
            // un dedo enguantado que erra por medio centímetro dejaba al
            // operario sin guiado, sin mapa y sin secciones en pleno lote.
            // Ahora pregunta antes (mismo diálogo que el reset de fábrica).
            case "apagar": _ = ConfirmarSalidaAsync(); return true;

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

            // ---- Info de lote/GPS → overlays NATIVOS ----
            // Antes abrían el HTML por WebView y quedaba una incoherencia: el
            // mismo dato se veía nativo entrando por la barra (OnNavDatosGps /
            // OnFieldToolsClick) y por Chromium entrando por acá. Las páginas
            // quedan en wwwroot para la PWA del celular.
            case "datos_gps":  ShowGpsData();   return true;
            case "lote_datos": ShowFieldData(); return true;
            // ---- Paneles nativos grandes (Hub / Cámaras) ----
            case "hub":        ShowHub();      return true;
            case "webcam":     ShowCamaras();  return true;
            // Nodos nativo (lista + curado + diagnostico MQTT). Hoy la entrada
            // llega por el HubPanel; el comando queda para engancharlo desde
            // una barra/menu sin volver a tocar el router.
            case "nodos":      ShowNodos();    return true;
            // Sonidos nativo (alarmas de cabina): que avisa, con que sonido y
            // cuando. Reemplaza pages/sonidos.html, que seguia siendo la unica
            // via de configurarlas y levantaba Chromium para hacerlo.
            case "sonidos":    ShowSonidos();  return true;
            // Editor de QuantiX nativo. "prescripciones" entra directo a la
            // tab Shape (el equivalente del viejo quantix.html?tab=shape).
            case "quantix_editor":  ShowQuantiXEditor();        return true;
            case "prescripciones":  ShowQuantiXEditor("shape"); return true;
            // Editor de VistaX nativo (Insumo & calibracion / Implemento /
            // Config). El operario llega normalmente por VistaX → Configurar.
            case "vistax_editor":   ShowVistaXEditor();         return true;
            // Editor de FlowX nativo (Nodo activo / Reguladoras / Cortes /
            // Electrovalvulas / Firmware / Nodos). El operario llega
            // normalmente por FlowX → Configurar.
            case "flowx_editor":    ShowFlowXEditor();          return true;
            // CoreX del menú izquierdo → dashboard de CoreX (config del sistema:
            // Serial / NTRIP / Red-IP / Módulos). Vive en :5181, servido por el
            // panel integrado del motor (CoreXEnginePanel), NO en el Hub :5180.
            // El ECU de autosteer queda en 'corex_ecu'.
            case "corex":      _ = AbrirCoreXAsync(); return true;
            case "corex_ecu":  ShowCoreXEcu(); return true;

            // ---- CONFIGURACIÓN → shell nativo (aterriza en Resumen) ----
            // Strangler fig: el panel abre nativo lo que ya está portado y las
            // pestañas que faltan las manda al HTML (?tab=…) desde su propio
            // menú, así que el operario no pierde ningún acceso. Si algún día
            // se pide una pestaña que todavía no es nativa, se va derecho al
            // WebView con la página de siempre.
            case "config_form":
                ShowConfig();
                return true;
            case "config_resumen":
                ShowConfig("summary");
                return true;

            // ---- Nueva A/B → flujo en el mapa (toco A, manejo, toco B) ----
            case "track_new_ab":
                StartAbCreate();
                return true;

            // ---- Guías → panel NATIVO (15vo port). El diálogo HTML era el
            // único de la labor que despertaba Chromium manejando; el panel
            // además deja el mapa vivo detrás (se ve la curva grabándose).
            case "pick":
            case "importar_guias":
            case "track_new_curve":
            case "track_new_a":
                AbrirGuias();
                return true;

            // ---- Contorno (lindero) → panel NATIVO. Era el único diálogo que
            // se abría con el mapa vivo detrás justamente porque se abre PARA
            // mirarlo; ahora es una card chica sobre el mapa, sin Chromium.
            // "lindero" = barra de la pasada; "herr_limites" = menú izquierda.
            case "lindero":
            case "herr_limites":
                AbrirContorno();
                return true;

            // ---- Cabecera → panel NATIVO. El diálogo HTML no solo despertaba
            // Chromium en pleno lote: TAPABA el mapa, que es el único preview
            // que sirve (el canvas de la página era una maqueta del mismo lote).
            // OJO: "cabecera_onoff" (prender/apagar el corte) sigue yendo al
            // motor — no abre pantalla.
            case "cabecera":
                AbrirCabecera();
                return true;

            // ---- Cabecera por líneas (la "avanzada", ex FormHeadAche) →
            // panel NATIVO. Era el último diálogo del flujo de labor que abría
            // Chromium sobre el mapa, y el que más área de dibujo necesitaba.
            case "cabecera_avanzada":
                AbrirCabeceraLineas();
                return true;

            // ---- Tramlines (huellas de rueda) → panel NATIVO. La vista previa
            // NO vive en la pantalla: se dibuja sobre el MAPA, o sea que la
            // ventana HTML de 350x340 tapaba justo lo único que hay para mirar
            // mientras se ajustan las pasadas. OJO: este case tiene que quedar
            // ANTES del switch de páginas — si "tram_crear" volviera a mapear a
            // tramline.html, el comando abriría Chromium otra vez.
            case "tram_crear":
                AbrirTramSimple();
                return true;

            // ---- Tramlines (multi) → panel NATIVO. El corte de las huellas se
            // marca CON EL DEDO sobre el dibujo (3 toques: A, B y el lado a
            // eliminar), y la ventana HTML de 460x470 dejaba ese lienzo del
            // tamaño de un sello encima del mapa. Mismo cuidado que "tram_crear":
            // este case va ANTES del switch de páginas, si no "tram_multi"
            // volvería a abrir Chromium.
            case "tram_multi":
                AbrirTramMulti();
                return true;

            // ---- Suavizar AB → panel NATIVO. Misma historia que Tramlines: la
            // vista previa de la curva suavizada se dibuja sobre el MAPA
            // (curve.smooList), o sea que el diálogo HTML tapaba justo lo único
            // que hay para mirar mientras se ajusta el nivel. Este case tiene
            // que quedar ANTES del switch de páginas — si no, "suavizar_ab"
            // volvería a abrir Chromium.
            case "suavizar_ab":
                AbrirSuavizarAb();
                return true;

            // ---- Corregir posición → panel NATIVO. Mueve la posición
            // percibida de la máquina (deriva GPS): lo que hay que mirar para
            // saber si quedó bien es EL MAPA, y la ventana HTML se paraba justo
            // encima. Este case tiene que quedar ANTES del switch de páginas —
            // si no, "corregir_pos" volvería a abrir Chromium.
            case "corregir_pos":
                AbrirCorregirPos();
                return true;

            // ---- Rutas grabadas → panel NATIVO. El operario elige el camino
            // que la máquina va a repetir SOLA, y lo único que dice si eligió
            // bien es verlo dibujado en el mapa: la ventana HTML se paraba
            // encima. Este case tiene que quedar ANTES del switch de páginas —
            // si no, "ruta_grabada" volvería a abrir Chromium.
            case "ruta_grabada":
                AbrirRutaGrabada();
                return true;

            // ---- Visor de eventos → panel NATIVO. Este case tiene que quedar
            // ANTES del switch de páginas: si "visor_eventos" volviera a mapear
            // a eventos.html, el comando abriría Chromium otra vez.
            case "visor_eventos":
                ShowEventos();
                return true;

            // ---- Calculadora de siembra → panel NATIVO. Mismo cuidado: va
            // ANTES del switch de páginas.
            case "calculadora":
                ShowCalculadora();
                return true;

            // ---- Perfiles → panel NATIVO. Las tres entradas (nuevo, cargar y
            // gestión) abrían la MISMA página perfiles.html, así que las tres
            // van al mismo panel — igual que antes. Va ANTES del switch de
            // páginas para que ninguna vuelva a abrir Chromium.
            case "perfil_nuevo":
            case "perfil_cargar":
            case "perfil_gestion":
                ShowPerfiles();
                return true;

            // ---- Los CUATRO gráficos → paneles NATIVOS. Se miran con la
            // máquina andando (ángulo real vs. pedido, rumbo, XTE, corrección
            // de rolido), o sea que la ventana HTML tapaba justo el mapa que
            // hay que mirar al mismo tiempo. Estos cases van ANTES del switch
            // de páginas: si "grafico_direccion" volviera a mapear a
            // grafico-direccion.html, el comando abriría Chromium otra vez.
            case "grafico_direccion":
                ShowGrafDireccion();
                return true;
            case "grafico_rumbo":
                ShowGrafRumbo();
                return true;
            case "grafico_xte":
                ShowGrafXte();
                return true;
            // "chequeo_roll" es el rótulo del menú ("Corrección roll"); la
            // pantalla es la de grafico-correccion.html.
            case "chequeo_roll":
                ShowGrafCorreccion();
                return true;

            // ---- Banderas → panel NATIVO. Se marca la piedra o el pozo
            // MANEJANDO y la lista da la distancia en vivo: lo que hay que ver
            // es el mapa, que la ventana HTML tapaba. "bandera" abre la LISTA;
            // "bandera_latlon" abre directo el ALTA por lat/lon (el ?add=1 de
            // la página). Va ANTES del switch de páginas.
            case "bandera":
                ShowBanderas(false);
                return true;
            case "bandera_latlon":
                ShowBanderas(true);
                return true;

            // Menú de lote (FormJob) → panel NATIVO (16vo port). El submenú
            // LOTE de la barra izquierda salta directo a su pantalla, igual
            // que hacían los deep-links ?do= de lote.js.
            case "lote_menu":
                AbrirLote("menu"); return true;
            // Continuar NO abre panel: acción directa (pedido del usuario —
            // acá no hay nada que elegir: es "abrí el último y listo"). El
            // backend resuelve __resume__.
            case "lote_continuar":
                ContinuarUltimoLote(); return true;
            case "lote_abrir":
                AbrirLote("abrir"); return true;
            case "lote_nuevo":
                AbrirLote("nuevo"); return true;
            case "lote_kml":
                OpenDialogPage("pages/lote.html?do=kml", "Lote desde KML", 670, 610); return true;

            // Cerrar lote. Sin este case el comando caía al motor de guiado, que
            // espera "job_close" y no conoce "lote_cerrar": el botón no hacía
            // absolutamente nada y no quedaba ni un error en ningún lado.
            case "lote_cerrar":
                CerrarLote(); return true;

            // Borrar pintado: el guard del motor (secciones apagadas) devolvía
            // false MUDO — el operario tocaba el botón con secciones activas y
            // no pasaba nada, sin explicación (circuito de pruebas 2026-08-07).
            // El aviso va acá, que es donde hay pantalla; el motor conserva su
            // guard como última defensa.
            case "borrar_aplicado":
                if (_seccionesActivas)
                {
                    // Toast, NO modal: el aviso con ShowDialog trababa todos
                    // los botones hasta tocar "Entendido" (reporte 2026-08-07).
                    MostrarToast(PilotX.Cockpit.Bars.Traductor.T(
                        "Apagá las secciones primero para poder borrar el pintado."));
                    return true;
                }
                // Con las secciones apagadas ANTES caía derecho al motor, que
                // borraba la cobertura del lote entero sin preguntar nada: un
                // toque en un botón que se ve igual que "Continuar" y "Abrir",
                // y toda la jornada pintada se iba. Ahora la confirmación es
                // la que manda el comando (por eso devuelve true acá: el
                // comando NO tiene que seguir de largo al motor).
                _ = ConfirmarBorrarPintadoAsync();
                return true;

            // Dirección (FormSteer) → ventana propia más grande. ?v= evita que
            // el WebView2 sirva una versión cacheada vieja de la página.
            case "direccion":
                // Panel NATIVO chico (17vo port). La página HTML completa
                // (dos columnas v=10) queda detrás del botón "Todo…".
                AbrirDireccion(); return true;

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

            // Panel de HERRAMIENTAS desde la barra de la pasada.
            case "herramientas_menu":
                if (_herramientasMenu != null)
                {
                    bool ver = !_herramientasMenu.IsVisible;
                    if (ver) UbicarHerramientasMenu();
                    _herramientasMenu.IsVisible = ver;
                }
                return true;

            // Menú SISTEMA: panel propio en vez de Flyout (ver el XAML). Se
            // coloca DEBAJO DEL BOTÓN, no en un lugar fijo: el botón se
            // corre cuando cambia el nombre del lote (la pestaña de al lado
            // mide distinto), y con margen clavado el panel aparecía lejos
            // — "abajo de la hora y las Ha" (reporte 2026-08-06).
            case "sistema_menu":
                if (_sistemaMenu != null)
                {
                    bool mostrar = !_sistemaMenu.IsVisible;
                    if (mostrar) UbicarSistemaMenu();
                    _sistemaMenu.IsVisible = mostrar;
                }
                return true;
        }

        // ---- Comandos que abren una página HTML del Hub en el WebView ----
        string page = cmd switch
        {
            // "config_form" ya NO mapea acá: lo agarra el case de arriba y
            // abre el panel nativo. config.html sigue viva para la PWA y para
            // las pestañas sin portar (las abre el propio panel).
            "todos_ajustes"     => "pages/ajustes-todos.html",
            "colores"           => "pages/colores.html",
            "colores_sec"       => "pages/colores-secciones.html",
            "mapeo_color"       => "pages/colores-secciones.html",
            // "perfil_nuevo"/"perfil_cargar"/"perfil_gestion" ya NO mapean acá:
            // Perfiles es panel nativo (el case de arriba los agarra antes).
            // perfiles.html queda para el Hub remoto/celular/Android, que no
            // pasan por este switch.
            "directorios"       => "pages/config.html",
            // Ayuda abre CONFIGURACIÓN parada en su módulo, no la página
            // suelta (pedido 2026-08-06, mismo criterio que Cámaras).
            "ayuda"             => "pages/config.html?mod=ayuda.html",
            // "grafico_direccion"/"grafico_rumbo"/"grafico_xte"/"chequeo_roll"
            // ya NO mapean acá: los cuatro son paneles nativos (los cases de
            // arriba los agarran antes). Las páginas grafico-*.html quedan para
            // el Hub remoto/celular/Android, que no pasan por este switch.
            // "suavizar_ab" ya NO mapea acá: es panel nativo (el case de arriba
            // lo agarra antes). suavizar-ab.html queda para el Hub remoto/
            // celular/Android, que no pasan por este switch.
            // "corregir_pos" ya NO mapea acá: es panel nativo (el case de
            // arriba lo agarra antes). corregir-posicion.html queda para el Hub
            // remoto/celular/Android, que no pasan por este switch.
            // "visor_eventos" ya NO mapea acá: es panel nativo (el case de
            // arriba lo agarra antes). eventos.html queda para el Hub remoto/
            // celular/Android, que no pasan por este switch.
            "conteo_semillas"   => "pages/vistax-prueba.html",
            // "calculadora" ya NO mapea acá: es panel nativo (el case de arriba
            // lo agarra antes). calculadora-siembra.html queda para el Hub
            // remoto/celular/Android, que no pasan por este switch.
            // "bandera"/"bandera_latlon" ya NO mapean acá: Banderas es panel
            // nativo (el case de arriba los agarra antes). banderas.html queda
            // para el Hub remoto/celular/Android, que no pasan por este switch.
            // "lindero"/"herr_limites" ya NO mapean acá: Contorno es nativo (el
            // case de arriba los agarra antes). contorno.html queda para el Hub
            // remoto/celular/Android, que no pasan por este switch.
            // "cabecera" ya NO mapea acá: es panel nativo (el case de arriba lo
            // agarra antes). cabecera.html queda para el Hub remoto/celular/
            // Android, que no pasan por este switch.
            // "cabecera_avanzada" ya NO mapea acá: es panel nativo (el case de
            // arriba lo agarra antes). cabecera-lineas.html queda para el Hub
            // remoto/celular/Android, que no pasan por este switch.
            // "tram_crear" ya NO mapea acá: Tramlines es panel nativo (el case
            // de arriba lo agarra antes). tramline.html queda para el Hub
            // remoto/celular/Android, que no pasan por este switch.
            // "pick"/"importar_guias" ya NO mapean acá: Guías es nativo (el
            // case de arriba los agarra antes). tracks.html queda para el Hub
            // remoto/celular, que no pasa por este switch.
            "sim_coords"        => "pages/sim-coords.html",
            "asistente_direccion" => "pages/config.html",
            // "ruta_grabada" ya NO mapea acá: Rutas grabadas es panel nativo (el
            // case de arriba lo agarra antes). recpath.html queda para el Hub
            // remoto/celular/Android, que no pasan por este switch.
            // "tram_multi" ya NO mapea acá: Tramlines (multi) es panel nativo
            // (el case de arriba lo agarra antes). tramlines.html queda para el
            // Hub remoto/celular/Android, que no pasan por este switch.
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
            // El `mapaVivo` que pasaba Contorno se fue con el port nativo: hoy
            // NINGÚN call site lo pasa en true (ver la nota de OpenDialogUrl).
            OpenDialogPage(page, TitleForCommand(cmd), 820, 600);
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
    // El dashboard de CoreX (:5181) lo sirve el CoreXEnginePanel del motor.
    // Se chequea el puerto ANTES de abrir la ventana (el motor pudo no haber
    // levantado todavía) y si no contesta se explica qué pasa en vez de
    // mostrar el error crudo del WebView.
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
            "El panel de CoreX lo sirve el motor de PilotX y ahora mismo no " +
            "contesta. Si el motor está arrancando, esperá unos segundos y " +
            "volvé a intentar.");
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

    // ---- Borrar pintado ----------------------------------------------------
    //
    // Lo que se pierde no tiene vuelta atrás: la cobertura trabajada del lote
    // entero (lo verde del mapa) es el registro de por dónde ya pasó la
    // máquina, y sin él el anti-solape deja de saber qué está sembrado. Por
    // eso el primer toque dice QUÉ se pierde, con el nombre del lote adelante.
    private async Task ConfirmarBorrarPintadoAsync()
    {
        string lote = (_vmSup?.LoteText ?? "").Trim();
        bool hayNombre = lote.Length > 0 &&
                         !lote.Equals("SIN LOTE", StringComparison.OrdinalIgnoreCase);

        bool ok = await MostrarConfirmacionAsync(
            "Borrar pintado",
            "Se borra TODO lo trabajado" + (hayNombre ? " del lote " + lote : " del lote abierto") +
            ": lo verde del mapa y las hectáreas hechas.\n\n" +
            "El lote, las guías y el lindero NO se tocan. Lo pintado no se puede recuperar.",
            "Borrar lo trabajado");
        if (!ok) return;

        // El comando va DERECHO al motor: mandarlo por _cockpitCmd volvería a
        // entrar en RouteCockpitCommand y pediría confirmación otra vez.
        await EnviarComandoAlMotorAsync("borrar_aplicado");
    }

    // ---- Cerrar PilotX -----------------------------------------------------
    //
    // Fuera del modo kiosco (taller y cualquier PC de escritorio) el ✕ de la
    // barra superior mide 38x40 y está a 4 px del de maximizar: errarle es
    // apagar el guiado en medio de la pasada. Se confirma; el mapa sigue
    // vivo detrás del diálogo.
    private async Task ConfirmarSalidaAsync()
    {
        bool ok = await MostrarConfirmacionAsync(
            "Cerrar PilotX",
            "Se apaga el guiado: se van el mapa, el piloto y el control de secciones.\n\n" +
            "Si estás trabajando un lote, cerralo primero desde LOTE › Cerrar.",
            "Cerrar PilotX");
        if (ok) Close();
    }

    /// <summary>POST directo del comando al motor de guiado, salteando el
    /// LocalHandler (que es quien nos trajo hasta acá).</summary>
    private async Task EnviarComandoAlMotorAsync(string cmd)
    {
        try
        {
            var http = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = DeriveOrigin(App.TargetUrl).TrimEnd('/');
            using var body = new System.Net.Http.StringContent(
                "{\"cmd\":\"" + cmd + "\"}", System.Text.Encoding.UTF8, "application/json");
            using var _ = await http.PostAsync(url + "/api/aog/guidance/command", body);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Comando] " + cmd + ": " + ex.Message);
            MostrarToast(PilotX.Cockpit.Bars.Traductor.T(
                "Sin conexión con el motor de PilotX: no se pudo hacer."));
        }
    }

    /// <summary>Diálogo nativo de confirmación (dos botones grandes, para
    /// guantes). Devuelve true solo si el operario tocó el botón rojo.</summary>
    private async Task<bool> MostrarConfirmacionAsync(string titulo, string mensaje,
                                                      string textoSi = "Borrar todo")
    {
        var tcs = new TaskCompletionSource<bool>();
        var win = ArmarDialogoBase(titulo, mensaje, out var fila);

        var btnNo = BotonDialogo("Cancelar", "#FFFFFF", "#101612");
        btnNo.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        // #C8332D y no #ED4848: blanco sobre aquel rojo daba 3,74:1 y este es
        // el botón que ejecuta lo que no tiene vuelta atrás — tiene que leerse
        // con sol de frente. Con #C8332D son 5,29:1 y sigue siendo el mismo
        // rojo de peligro. (El #ED4848 se queda donde es relleno, no texto.)
        var btnSi = BotonDialogo(textoSi, "#C8332D", "#FFFFFF");
        btnSi.Click += (_, _) => { tcs.TrySetResult(true); win.Close(); };
        fila.Children.Add(btnNo);
        fila.Children.Add(btnSi);

        win.Closed += (_, _) => tcs.TrySetResult(false);
        await win.ShowDialog(this);
        return await tcs.Task;
    }

    // ---- toast transitorio (no bloquea) ------------------------------------
    //
    // Para avisos que no piden decisión. IsHitTestVisible=false en el XAML:
    // ni siquiera el propio toast intercepta toques. Se va solo a los 4 s;
    // un aviso nuevo pisa al anterior y reinicia el reloj.
    private DispatcherTimer? _toastTimer;

    private void MostrarToast(string mensaje)
    {
        var borde = this.FindControl<Border>("ToastAviso");
        var texto = this.FindControl<TextBlock>("ToastAvisoText");
        if (borde == null || texto == null) return;
        texto.Text = mensaje;
        borde.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, __) =>
        {
            _toastTimer?.Stop();
            borde.IsVisible = false;
        };
        _toastTimer.Start();
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
        // Foreground EXPLÍCITO: el tema del cockpit pinta los TextBlock claros
        // (para leerse sobre el mapa oscuro) y esta ventana es blanca — sin
        // esto el aviso salía con el texto invisible: "salta una ventana pero
        // no dice nada, solo el título y el botón" (reporte 2026-08-07).
        var tinta = new global::Avalonia.Media.SolidColorBrush(
            global::Avalonia.Media.Color.Parse("#101612"));
        var stack = new global::Avalonia.Controls.StackPanel { Spacing = 12 };
        stack.Children.Add(new global::Avalonia.Controls.TextBlock
        {
            Text = titulo,
            FontSize = 22,
            FontWeight = global::Avalonia.Media.FontWeight.Bold,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = tinta,
        });
        stack.Children.Add(new global::Avalonia.Controls.TextBlock
        {
            Text = mensaje,
            FontSize = 15,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = tinta,
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
            // 48 explícito: con el padding solo quedaba en ~43 px y estos dos
            // botones deciden si se borra el trabajo o se apaga el guiado.
            MinHeight = 48,
            MinWidth = 120,
            HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
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

    /// <summary>
    /// Abre el gestor de Guías nativo. El guard de lote es el mismo que tenía
    /// el diálogo HTML: sin lote no hay dónde guardar una guía.
    /// </summary>
    /// <summary>Abre el menú de lote nativo en la pantalla pedida.</summary>
    private void AbrirLote(string pantalla)
    {
        if (_loteHost == null) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        if (_guiasHttp == null)
            _guiasHttp = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _loteHost.Attach(_guiasHttp, DeriveOrigin(App.TargetUrl));
        _loteHost.Abrir(pantalla);
    }

    // Dirección nativa: panel chico sobre el mapa vivo (mismo patrón Guías/Lote).
    private void AbrirDireccion()
    {
        if (_direccionHost == null) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        if (_guiasHttp == null)
        {
            _guiasHttp = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        }
        _direccionHost.Attach(_guiasHttp, DeriveOrigin(App.TargetUrl));
        _direccionHost.Abrir();
    }

    // Contorno (lindero) nativo: card chica sobre el mapa vivo. OJO — cerrarlo
    // NO cancela la grabación del lindero (ver la nota de ContornoPanel): si
    // abrir otro panel la cancelara, el operario perdería la vuelta entera que
    // acaba de manejar. Al reabrirlo, `state.recording` lo devuelve directo a
    // la grabación.
    private void AbrirContorno()
    {
        if (_contornoHost == null) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        // Cabecera está anclada en el MISMO lugar que esta card: sin cerrarla se
        // dibujaban una encima de la otra, y además su /close (que persiste la
        // cabecera) no salía nunca.
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _contornoClient ??= new ContornoClient(DeriveOrigin(App.TargetUrl));
        _contornoHost.Attach(_contornoClient);
        _contornoHost.Abrir();
        // Card flotante: el mapa NUNCA se apaga (y acá menos que nunca — esta
        // pantalla se abre para mirarlo).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
    }

    // Tramlines nativo: card chica sobre el mapa vivo. Abrir esta pantalla YA
    // prende la vista previa de las huellas en el mapa (el GET /state ejecuta
    // el Open del motor), así que el mapa tiene que quedar SÍ o SÍ encendido —
    // acá menos que nunca se apaga. Cerrar el panel sin commit descarta la
    // preview (lo hace el propio panel en Cerrar()).
    private void AbrirTramSimple()
    {
        if (_tramSimpleHost == null) return;
        // Ya abierto: no se re-abre. Un segundo /state serían otro Open y otro
        // rebuild de la geometría sobre lo que el operario ya venía ajustando.
        if (_tramSimpleHost.IsVisible) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        // Contorno y Cabecera están ancladas en el MISMO lugar que esta card:
        // sin cerrarlas se dibujarían una encima de la otra.
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        // El editor multi trabaja sobre el MISMO tram del lote: con los dos
        // abiertos habría dos sesiones peleando por el editor del motor (que no
        // es thread-safe). Su cierre manda /close, o sea guarda lo hecho.
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        // Este comando hoy nace en el menú del Hub (menu-izquierda.js), o sea
        // con el WebView ocupando la pantalla: sin cerrarlo, la card nativa
        // quedaría abajo y el operario vería que "no pasó nada".
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _tramSimpleClient ??= new TramSimpleClient(DeriveOrigin(App.TargetUrl));
        _tramSimpleHost.Attach(_tramSimpleClient);
        _tramSimpleHost.Abrir();
        // Card flotante: el mapa NUNCA se apaga (y acá es la vista previa misma).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
    }

    // Suavizar AB nativo: card chica sobre el mapa vivo. Igual que Tramlines,
    // abrir esta pantalla YA prende la vista previa en el mapa (el
    // smooth_ab_open del motor calcula la primera curva suavizada), así que el
    // mapa tiene que quedar SÍ o SÍ encendido — es lo único que hay para mirar
    // mientras se sube y baja el nivel. Cerrar el panel sin aplicar descarta la
    // preview (lo hace el propio panel en Cerrar()).
    private void AbrirSuavizarAb()
    {
        if (_suavizarAbHost == null) return;
        // Ya abierto: no se re-abre. Un segundo smooth_ab_open resetearía el
        // nivel a 20 y recalcularía la preview sobre lo que el operario ya venía
        // ajustando (el diálogo HTML tampoco se reabría: OpenDialogPage reusaba
        // la ventana).
        if (_suavizarAbHost.IsVisible) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        // Contorno, Cabecera y Tramlines están anclados en el MISMO lugar que
        // esta card: sin cerrarlos se dibujarían uno encima del otro, y las
        // preview de ellos taparían la curva suavizada.
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        // Este comando también nace en el menú del Hub (menu-izquierda.js), o
        // sea con el WebView ocupando la pantalla: sin cerrarlo, la card nativa
        // quedaría abajo y el operario vería que "no pasó nada".
        if (_webView != null) CloseWebView();
        // Sin client propio: un solo POST al /api/aog/guidance/command de
        // siempre, por el HttpClient compartido con Guías/Lote/Dirección.
        if (_guiasHttp == null)
        {
            _guiasHttp = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        }
        _suavizarAbHost.Attach(_guiasHttp, DeriveOrigin(App.TargetUrl));
        _suavizarAbHost.Abrir();
        // Card flotante: el mapa NUNCA se apaga (y acá es la vista previa misma).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
    }

    // Corregir posición nativa: card chica sobre el mapa vivo. Esta pantalla
    // MUEVE la posición percibida de la máquina, y la única forma de ver si la
    // corrección quedó bien es mirando el mapa — la ventana HTML de 350x340 se
    // paraba justo encima. Nada de reabrir: es idempotente, pero un segundo
    // Abrir() volvería a mostrar 0/0 hasta que conteste el GET.
    private void AbrirCorregirPos()
    {
        if (_corregirPosHost == null) return;
        if (_corregirPosHost.IsVisible) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        // Todas estas cards están ancladas en el MISMO lugar que esta: sin
        // cerrarlas se dibujarían una encima de la otra.
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB además deja la curva suavizada dibujada en el mapa: su
        // Cerrar() manda el cancel, o sea que sin esto la preview quedaría
        // colgada abajo de esta card.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        // Este comando también nace en el menú del Hub (menu-izquierda.js), o
        // sea con el WebView ocupando la pantalla: sin cerrarlo, la card nativa
        // quedaría abajo y el operario vería que "no pasó nada".
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _shiftPosClient ??= new ShiftPosClient(DeriveOrigin(App.TargetUrl));
        _corregirPosHost.Attach(_shiftPosClient);
        _corregirPosHost.Abrir();
        // Card flotante: el mapa NUNCA se apaga (y acá menos que nunca — es la
        // única forma de ver si el corrimiento quedó donde tenía que quedar).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
    }

    // Rutas grabadas nativo (ex recpath.html): la lista de .rec del lote — los
    // caminos que el tractor puede repetir solo. La ventana HTML de 460x470
    // despertaba Chromium en pleno lote y tapaba el mapa, que es donde se ve si
    // la ruta cargada es la que se quería. Nada de reabrir: un segundo Abrir()
    // volvería a pedir la lista y perdería la selección que el operario ya hizo.
    //
    // modoSalvar queda listo para cuando se recupere el botón de parar
    // grabación (el ex btnPathRecordStop): hoy nadie lo dispara — el flujo
    // "parar → nombrar" quedó huérfano desde que murieron las WinForms — pero
    // el back (/api/recpath/save|discard) está vivo y probado.
    private void AbrirRutaGrabada(bool modoSalvar = false)
    {
        if (_recPathHost == null) return;
        if (_recPathHost.IsVisible) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        // Un solo overlay a la vez: si queda una card vieja visible, las dos se
        // pisan sobre el mapa.
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB deja la curva suavizada dibujada en el mapa: su Cerrar()
        // manda el cancel, si no la preview queda colgada abajo de esta card.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        // Este comando también nace en el menú del Hub (menu-izquierda.js), o
        // sea con el WebView ocupando la pantalla: sin cerrarlo, la card nativa
        // quedaría abajo y el operario vería que "no pasó nada".
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _recPathClient ??= new RecPathClient(DeriveOrigin(App.TargetUrl));
        _recPathHost.Attach(_recPathClient);
        _recPathHost.Abrir(modoSalvar);
        // Card flotante: el mapa NUNCA se apaga — la ruta que se carga se ve
        // dibujada ahí y no en ningún otro lado.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
    }

    // Tramlines (multi) nativo, ex tramlines.html (FormTramLine): el
    // constructor de huellas por guía con el corte de 3 toques. Es una card con
    // lienzo propio (720x560, centrada) — el operario TOCA el dibujo para
    // cortar, y en la ventana HTML de 460x470 ese lienzo quedaba del tamaño de
    // un sello. El mapa sigue vivo alrededor.
    private void AbrirTramMulti()
    {
        if (_tramMultiHost == null) return;
        // Ya abierta: NO se vuelve a abrir. Un segundo POST /open del lado del
        // motor rehace la sesión desde cero (vuelve a copiar las guías, resetea
        // passes/start y reconstruye el preview): el operario perdería el corte
        // que venía marcando. El diálogo HTML tampoco se reabría — OpenDialogPage
        // reusaba la ventana.
        if (_tramMultiHost.IsVisible) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        // Tramlines (simple) edita EL MISMO tram del lote y encima deja la
        // preview dibujada en el mapa: sin cerrarla habría dos sesiones sobre
        // el editor del motor y huellas colgadas encima del preview de acá.
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        // Suavizar AB deja la curva suavizada dibujada en el mapa: sin su
        // Cerrar() (que manda el cancel) la preview quedaría colgada abajo del
        // lienzo de huellas, y encima quedarían dos cards abiertas a la vez.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        // Este comando hoy nace en el menú del Hub (menu-izquierda.js), o sea
        // con el WebView ocupando la pantalla: sin cerrarlo, la card nativa
        // quedaría abajo y el operario vería que "no pasó nada".
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _tramMultiClient ??= new TramMultiClient(DeriveOrigin(App.TargetUrl));
        _tramMultiHost.Attach(_tramMultiClient);
        _tramMultiHost.Abrir();
        // Card flotante: el mapa NUNCA se apaga. Y acá se actualiza solo cuando
        // se Guarda: TramGeometryPoller (1 Hz, cache por revisión) ya trae
        // /api/aog/tram — no hay que empujarle nada.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
    }

    // Cabecera nativa: card chica sobre el mapa vivo. El diálogo HTML de 350x340
    // tapaba y apagaba el mapa justo en la pantalla donde el operario quiere ver
    // la franja verde dibujándose contra el lindero.
    private void AbrirCabecera()
    {
        if (_cabeceraHost == null) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        // La cabecera por líneas edita LO MISMO que esta card: con las dos
        // abiertas quedarían dos sesiones sobre el mismo editor del motor.
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _cabeceraClient ??= new HeadlandClient(DeriveOrigin(App.TargetUrl));
        _cabeceraHost.Attach(_cabeceraClient);
        _cabeceraHost.Abrir();
        // Card flotante: el mapa NUNCA se apaga (acá menos que nunca — es el
        // preview real de la cabecera que se está construyendo).
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
    }

    // Cabecera por líneas (la "avanzada", ex FormHeadAche) → panel NATIVO. Es
    // la cabecera de los lotes que no son un rectángulo: se marcan líneas A/B
    // sobre el contorno y se arma la cabecera con los cruces. La ventana HTML
    // de 460x470 despertaba Chromium en pleno lote y encima dejaba el lienzo
    // del tamaño de un sello; la card nativa tiene más área para el dedo y el
    // mapa sigue vivo detrás.
    private void AbrirCabeceraLineas()
    {
        if (_cabLineasHost == null) return;
        // Ya abierta: NO se vuelve a abrir. Un segundo POST /open del lado del
        // motor hace tracksArr.Clear() y recarga Headlines.txt del disco, así
        // que TODAS las líneas que el operario marcó en esta sesión (todavía
        // sin /close, o sea sin guardar) se pierden. Volver a tocar el comando
        // con la card a la vista no puede costar el trabajo hecho — el diálogo
        // HTML tampoco se reabría, OpenDialogPage reusaba la ventana.
        if (_cabLineasHost.IsVisible) return;
        if (_guiasHost != null && _guiasHost.IsVisible) _guiasHost.Cerrar();
        if (_loteHost  != null && _loteHost.IsVisible)  _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        // Tramlines está anclada en el MISMO lugar que las demás cards y, sobre
        // todo, deja la vista previa de las huellas DIBUJADA en el mapa mientras
        // siga abierta: sin cerrarla acá el operario armaba la cabecera con las
        // huellas colgadas encima y el editor de tram vivo en el motor.
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        // Este comando hoy nace en el menú del Hub (menu-izquierda.js), o sea
        // con el WebView ocupando la pantalla: sin cerrarlo, la card nativa
        // quedaría abajo y el operario vería que "no pasó nada".
        if (_webView != null) CloseWebView();
        // Lazy init: el cliente se crea una sola vez y se reusa.
        _cabLineasClient ??= new CabeceraLineasClient(DeriveOrigin(App.TargetUrl));
        _cabLineasHost.Attach(_cabLineasClient);
        _cabLineasHost.Abrir();
        // Card flotante: el mapa NUNCA se apaga.
        if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true;
    }

    private void AbrirGuias()
    {
        if (_guiasHost == null) return;
        // Solo un panel a la vez sobre el mapa (mismo criterio que ShowSistema).
        if (_loteHost != null && _loteHost.IsVisible) _loteHost.Cerrar();
        if (_direccionHost != null && _direccionHost.IsVisible) _direccionHost.Cerrar();
        if (_contornoHost != null && _contornoHost.IsVisible) _contornoHost.Cerrar();
        if (_cabeceraHost != null && _cabeceraHost.IsVisible) _cabeceraHost.Cerrar();
        if (_cabLineasHost != null && _cabLineasHost.IsVisible) _cabLineasHost.Cerrar();
        if (_tramSimpleHost != null && _tramSimpleHost.IsVisible) _tramSimpleHost.Cerrar();
        if (_tramMultiHost != null && _tramMultiHost.IsVisible) _tramMultiHost.Cerrar();
        // Suavizar AB está anclado en el MISMO lugar que estas cards y encima
        // deja la curva suavizada dibujada en el mapa: su Cerrar() manda el
        // cancel, o sea que sin esto la preview quedaría colgada abajo de la
        // pantalla nueva.
        if (_suavizarAbHost != null && _suavizarAbHost.IsVisible) _suavizarAbHost.Cerrar();
        // Corregir posición está anclada en el MISMO lugar que estas cards:
        // sin cerrarla quedarían dos pisadas sobre el mapa.
        if (_corregirPosHost != null && _corregirPosHost.IsVisible) _corregirPosHost.Cerrar();
        if (_recPathHost != null && _recPathHost.IsVisible) _recPathHost.Cerrar();
        // Banderas está anclada en el MISMO lugar que estas cards (derecha,
        // margen 82): sin cerrarla quedarían dos pisadas sobre el mapa. Su
        // Cerrar() manda además el POST /close (deselecciona + guarda).
        if (_banderasHost != null && _banderasHost.IsVisible) _banderasHost.Cerrar();
        if (_herramientasMenu != null) _herramientasMenu.IsVisible = false;
        if (_sistemaMenu != null) _sistemaMenu.IsVisible = false;
        if (_guiasHttp == null)
        {
            _guiasHttp = _trackHttp ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            _guiasHost.Attach(_guiasHttp, DeriveOrigin(App.TargetUrl));
        }
        // Directo al LISTADO cuando el lote ya tiene guías guardadas (pedido
        // 2026-08-10: "las guías guardadas deberían aparecer en un listado
        // cuando las hay"). Sin guías cae al menú de crear, como siempre; y
        // desde la lista se vuelve al menú con "Nueva guía".
        _guiasHost.Abrir(directoALista: true);
    }

    private System.Net.Http.HttpClient? _guiasHttp;

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
        // "suavizar_ab" ya no llega acá: es panel nativo y no abre ninguna
        // ventana con título (igual que "tram_multi"/"cabecera").
        // "corregir_pos" ya no llega acá: es panel nativo y no abre ninguna
        // ventana con título (igual que "suavizar_ab"/"tram_multi").
        "visor_eventos"     => "Eventos",
        "conteo_semillas"   => "Conteo de semillas",
        "calculadora"       => "Calculadora de siembra",
        "bandera" or "bandera_latlon" => "Banderas",
        // "lindero"/"herr_limites" ya no llegan acá: Contorno es panel nativo y
        // no abre ninguna ventana con título.
        // "cabecera" ya no llega acá: es panel nativo y no abre ninguna ventana
        // con título (igual que "lindero"/"herr_limites").
        "cabecera_avanzada" => "Cabecera avanzada",
        "tram_crear"        => "Tramline",
        "sim_coords"        => "Coordenadas simulador",
        // "ruta_grabada" ya no llega acá: es panel nativo y no abre ninguna
        // ventana con título (igual que "corregir_pos"/"suavizar_ab").
        // "tram_multi" ya no llega acá: es panel nativo y no abre ninguna
        // ventana con título (igual que "tram_crear"/"cabecera").
        _ => "PilotX"
    };

    // ---------- HUD --------------------------------------------------------

    private void OnHudSnapshot(HudSnapshot s)
    {
        _lastPivotE = s.PivotEasting;
        _lastPivotN = s.PivotNorthing;
        // Para el guard de "Borrar pintado" (se consulta al tocar el botón).
        _seccionesActivas = s.IsSectionAutoOn || s.IsSectionManualOn;
        Dispatcher.UIThread.Post(() =>
        {
            // El HUD llega siempre (nunca se pausa), así que es el mejor lugar
            // para reafirmar el estado del mapa y que no quede trabado.
            ReconciliarMapa();
            // Tramlines nativo: el editor trabaja sobre la guía y el tram del
            // lote ACTIVO. Si el lote cambia con el panel abierto, lo que
            // muestra dejó de existir — se cierra, y el cierre descarta la
            // preview que quedaría colgada en el mapa del lote nuevo. (El
            // centinela pilotx-close no sirve acá: nunca fue un diálogo.)
            if (_tramSimpleHost != null && _tramSimpleHost.IsVisible
                && !string.Equals(_lastFieldDir ?? "", s.CurrentFieldDirectory ?? "",
                                  StringComparison.OrdinalIgnoreCase))
                _tramSimpleHost.Cerrar();
            // Mismo motivo para el editor multi: sus guías, su contorno y sus
            // huellas son las del lote que se acaba de cerrar. Su Cerrar() manda
            // /close, o sea GUARDA lo hecho en el lote viejo antes de irse — no
            // se le tira el trabajo al operario por cambiar de lote.
            if (_tramMultiHost != null && _tramMultiHost.IsVisible
                && !string.Equals(_lastFieldDir ?? "", s.CurrentFieldDirectory ?? "",
                                  StringComparison.OrdinalIgnoreCase))
                _tramMultiHost.Cerrar();
            // Suavizar AB trabaja sobre la curva AB del lote ACTIVO. Si el lote
            // cambia con el panel abierto, la preview que quedó prendida es de
            // una curva que ya no existe y "A archivo" escribiría sobre el lote
            // nuevo. Se cierra, y el cierre manda el cancel.
            if (_suavizarAbHost != null && _suavizarAbHost.IsVisible
                && !string.Equals(_lastFieldDir ?? "", s.CurrentFieldDirectory ?? "",
                                  StringComparison.OrdinalIgnoreCase))
                _suavizarAbHost.Cerrar();
            // El corrimiento de deriva vive en las propiedades del lote ACTIVO
            // (SharedFieldProperties.DriftCompensation). Si el lote cambia con
            // el panel abierto, los cm que muestra son de un lote que ya no
            // está: se cierra, así el próximo Abrir() relee del motor.
            if (_corregirPosHost != null && _corregirPosHost.IsVisible
                && !string.Equals(_lastFieldDir ?? "", s.CurrentFieldDirectory ?? "",
                                  StringComparison.OrdinalIgnoreCase))
                _corregirPosHost.Cerrar();
            // Las rutas grabadas son .rec DEL LOTE, y el motor resuelve el nombre
            // contra la carpeta del lote ACTIVO en el momento del POST
            // (EngineRecPathService.DirLote()). Si el lote cambia con el panel
            // abierto (lo puede cambiar el Hub del celular), la lista en pantalla
            // es la del lote viejo pero "Usar" cargaría —y "Borrar" BORRARÍA— el
            // homónimo del lote nuevo. Se cierra: la lista se vuelve a pedir en
            // el próximo Abrir().
            if (_recPathHost != null && _recPathHost.IsVisible
                && !string.Equals(_lastFieldDir ?? "", s.CurrentFieldDirectory ?? "",
                                  StringComparison.OrdinalIgnoreCase))
                _recPathHost.Cerrar();
            _lastFieldDir = s.CurrentFieldDirectory;
            CerrarDialogoSiCambioElLote(s.CurrentFieldDirectory);
            CerrarDialogoSiHayGuiaNueva(s.TracksTotal, s.TrackIdx);
            AtenderPedidoDeVentana(s);
            AtenderIdioma(s);
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

            // Marcas de "Marcar giro": visibilidad del botón Borrar + toast de
            // confirmación cuando el count sube (el operario acaba de marcar).
            // Si el engine además descartó una marca de OTRA guía (rumbo
            // incompatible al re-marcar), el aviso lo dice explícito: nada se
            // borra en silencio.
            int marcasGiro = s.TurnMarks?.Count ?? 0;
            if (_vmDer != null) _vmDer.HayMarcasGiro = marcasGiro > 0;
            bool descartoAjena = _marcasGiroDescartadasPrev >= 0
                && s.TurnMarksDescartadas > _marcasGiroDescartadasPrev;
            if (descartoAjena)
                MostrarToast(PilotX.Cockpit.Bars.Traductor.T("Marca de giro puesta · se borró una marca de otra guía"));
            else if (marcasGiro > _marcasGiroPrev)
                MostrarToast(PilotX.Cockpit.Bars.Traductor.T("Marca de giro puesta"));
            _marcasGiroDescartadasPrev = s.TurnMarksDescartadas;
            _marcasGiroPrev = marcasGiro;

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
            // Config › Módulos: los paneles embebidos que viven de datos
            // empujados (grilla de SectionX, KPIs del Hub) son instancias
            // propias del shell de la Config — sin este reenvío quedaban en
            // blanco para siempre. ConfigPanel filtra por el módulo activo.
            if (_configHost != null && _configHost.IsVisible)
                _configHost.OnSnapshot(s);

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
    // Firmwares abre el panel NATIVO (FirmwaresPanel): el catálogo local de
    // .bin ya no despierta Chromium. firmwares.html queda para la PWA.
    private void OnNavFirmwares(object? s, RoutedEventArgs e) => ShowFirmwares();
    // OrbitX y Debug abren los paneles NATIVOS (OrbitXPanel / DebugPanel):
    // ninguno de los dos despierta Chromium ya. orbitx.html y debug.html
    // quedan para la PWA del celular.
    private void OnNavOrbitX   (object? s, RoutedEventArgs e) => ShowOrbitX();
    private void OnNavDebug    (object? s, RoutedEventArgs e) => ShowDebug();

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
