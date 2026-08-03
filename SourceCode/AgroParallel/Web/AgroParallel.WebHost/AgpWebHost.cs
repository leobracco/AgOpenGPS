// ============================================================================
// AgpWebHost.cs
// Wrapper sobre EmbedIO.WebServer: bind a 127.0.0.1:5180, registra controllers
// + WebSocket + estáticos de wwwroot. Vida del server gobernada por Start/Stop.
// ============================================================================

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.OrbitX;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.FieldMaps;
using AgroParallel.WebHost.Controllers;
// (controllers en sub-namespace)
using AgroParallel.WebHost.WebSockets;
using EmbedIO;
using EmbedIO.Files;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost
{
    public sealed class AgpWebHost : IDisposable
    {
        private readonly IAogStateProvider _state;
        private readonly ISistemaService _sistema;
        private readonly INodoRegistryService _nodos;
        private readonly IOrbitXConfigService _orbitxCfg;
        private readonly ISectionXConfigService _sectionxCfg;
        private readonly ICamarasConfigService _camarasCfg;
        private readonly IQuantiXConfigService _quantixCfg;
        private readonly IVistaXConfigService _vistaxCfg;
        private readonly IVistaXLiveService _vistaxLive;
        private readonly IDebugLogService _debug;
        private readonly ILotesService _lotes;
        private readonly IVehicleToolService _vehicleTool;
        private readonly IShapefileService _shapefile;
        private readonly ICoverageService _coverage;
        private readonly ISectionControlService _sectionsCore;
        private readonly IQuantiXRuntimeService _quantixRuntime;
        private readonly IGuidanceCalculator _guidance;
        // Calibración de roll del IMU interno (CAHRS/FormGPS.ahrs). Igual que
        // guidance/toolGeometry/tram: inyectado por FormGPS, no auto-instanciado
        // (necesita la referencia real al form vivo).
        private readonly IImuCalibracionService _imuCalibracion;
        private readonly ISteerConfigService _steerConfig;
        // Lista de guías (AB/curvas) del lote activo (FormGPS.trk.gArr). Mismo
        // criterio: inyectado por FormGPS, no auto-instanciado.
        private readonly ITrackListService _trackList;
        // Perfiles de vehículo (Vehicles/*.xml): cargar/sumar/copiar/proteger
        // desde pages/perfiles.html. Inyectado por FormGPS (necesita el form vivo).
        private readonly IPerfilVehiculoService _perfiles;
        // Réplica HTML de FormConfig (pages/config.html). Inyectado por FormGPS
        // (todas las acciones tocan Settings + estado vivo del form).
        private readonly IConfigVehiculoService _configVehiculo;
        private readonly IHeadlandEditService _headlandEdit;
        // Editor "Tramlines simples" (tramline.html, reemplazo de FormTram).
        // Inyectado por FormGPS (toca trk/ABLine/curve/tram del form vivo).
        private readonly ITramSimpleService _tramSimple;
        // Widget "Mover guía" (mover-guia.html, reemplazo de FormNudge/FormRefNudge).
        private readonly INudgeService _nudge;
        // Widget "AB rápido" (ab-rapido.html, reemplazo de FormQuickAB).
        private readonly IQuickAbService _quickAb;
        // Widget "Banderas" (banderas.html, reemplazo de FormFlags/FormEnterFlag).
        private readonly IFlagsService _flags;
        // Página "Contorno" (contorno.html, reemplazo de FormBoundary/Player).
        private readonly IContornoService _contorno;
        // Cabecera por líneas (cabecera-lineas.html, reemplazo de FormHeadAche).
        private readonly ICabeceraLineasService _cabeceraLineas;
        // Constructor de tramlines (tramlines.html, reemplazo de FormTramLine).
        private readonly ITramLineService _tramLine;
        // Gestor de tracks (tracks.html, reemplazo de FormBuildTracks).
        private readonly ITrackBuilderService _trackBuilder;
        // Recorded paths (recpath.html, reemplazo de FormRecordName/Picker).
        private readonly IRecPathService _recPath;
        private readonly IToolGeometryCalculator _toolGeometry;
        private readonly ITramCalculator _tram;
        // Caminos: youturn (giro de cabecera) + recorded path (Stage 5 render
        // OpenGL). Igual que tram: inyectado por FormGPS, no auto-instanciado.
        private readonly IPathsGeometryCalculator _paths;
        private readonly IPilotXUpdateService _pilotxUpdate;
        private readonly IFlowXConfigService _flowxCfg;
        private readonly IFlowXLiveService _flowxLive;
        private readonly IStormXConfigService _stormxCfg;
        private readonly IStormXLiveService _stormxLive;
        private readonly ILineXConfigService _linexCfg;
        private readonly ILineXLiveService _linexLive;
        private readonly IInsumoCatalogService _insumos;
        private readonly IVistaXCalibracionService _vistaxCalib;
        private readonly IFieldMapsService _fieldMaps;
        // CoreX-ECU: proxy HTTP al firmware Teensy de autosteer. Auto-instanciado
        // (file-based, sin dependencias). Permite diagnóstico live + step-by-step
        // de boot + selector WAS (encoder Keya vs analógico) desde el Hub.
        private readonly ICoreXEcuService _corexEcu;
        // CoreX (AgIO): puente de solo-lectura hacia el sidecar que corre en
        // 127.0.0.1:5181 y habla con GPS/IMU/Machine/Steer. Auto-instanciado
        // (puerto fijo, sin config). Alimenta la tira de estado de hub.html.
        private readonly ICoreXBridgeService _corexBridge;
        // Capa de identidad curada sobre el registry MQTT: aceptados, ignorados y
        // alias humano persistidos en nodos.json. Auto-instanciado.
        private readonly INodosCuratedService _nodosCurated;
        // Estado del asistente de primera vez del Hub (setup.json). Auto-instanciado.
        private readonly ISetupStateService _setupState;
        // Prescripciones variable-rate (Gap #5). Estático-compartido vía
        // PrescripcionService; el bridge QuantiX usa otra instancia y comparten
        // el _active. Auto-instanciado si nadie lo inyecta.
        private readonly IPrescripcionService _prescripciones;
        // Implemento central — fuente única de verdad de la geometría física.
        // Migra desde formato legacy (VistaX/Quantix/SectionX) en el primer GET.
        // Auto-instanciado: el shell no necesita conocerlo.
        private readonly IImplementoService _implemento;
        private readonly string _wwwroot;

        /// <summary>Detección de alarmas sonoras (opcional): lo setea el host
        /// que lo tenga (Engine). Null = endpoints degradan a defaults.</summary>
        public AgroParallel.Services.SonidosAlarmService Sonidos { get; set; }
        private readonly int _port;
        private WebServer _server;
        private CancellationTokenSource _cts;
        private TelemetryHub _telemetry;
        private DebugHub _debugHub;
        private QuantiXLiveHub _quantixHub;
        private MdnsResponder _mdns;
        // Coordinador único de OTA hacia nodos ESP32 (todos los productos X-*).
        // Reusa la conexión MQTT del NodoRegistryService; sin él los endpoints
        // /api/nodos/{uid}/ota|firmwares|cmd contestan service-unavailable.
        private FirmwareOtaCoordinator _otaCoord;

        public string Url { get; }
        public bool IsRunning { get; private set; }

        public AgpWebHost(IAogStateProvider state,
                          ISistemaService sistema,
                          INodoRegistryService nodos,
                          IOrbitXConfigService orbitxCfg,
                          ISectionXConfigService sectionxCfg,
                          ICamarasConfigService camarasCfg,
                          IQuantiXConfigService quantixCfg,
                          IVistaXConfigService vistaxCfg,
                          IVistaXLiveService vistaxLive,
                          IDebugLogService debug,
                          ILotesService lotes,
                          IVehicleToolService vehicleTool,
                          IShapefileService shapefile,
                          ICoverageService coverage,
                          ISectionControlService sectionsCore,
                          IQuantiXRuntimeService quantixRuntime,
                          IGuidanceCalculator guidance,
                          IPilotXUpdateService pilotxUpdate,
                          IFlowXConfigService flowxCfg,
                          IFlowXLiveService flowxLive,
                          IStormXConfigService stormxCfg,
                          IStormXLiveService stormxLive,
                          ILineXConfigService linexCfg,
                          ILineXLiveService linexLive,
                          string wwwroot,
                          int port = 5180,
                          IInsumoCatalogService insumos = null,
                          IToolGeometryCalculator toolGeometry = null,
                          ITramCalculator tram = null,
                          IImplementoService implemento = null,
                          IImuCalibracionService imuCalibracion = null,
                          ITrackListService trackList = null,
                          IPerfilVehiculoService perfiles = null,
                          IConfigVehiculoService configVehiculo = null,
                          IHeadlandEditService headlandEdit = null,
                          ITramSimpleService tramSimple = null,
                          INudgeService nudge = null,
                          IQuickAbService quickAb = null,
                          IFlagsService flags = null,
                          IContornoService contorno = null,
                          ICabeceraLineasService cabeceraLineas = null,
                          ITramLineService tramLine = null,
                          ITrackBuilderService trackBuilder = null,
                          IRecPathService recPath = null,
                          IPathsGeometryCalculator paths = null,
                          ISteerConfigService steerConfig = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _sistema = sistema;         // nullable
            _nodos = nodos;             // nullable
            _orbitxCfg = orbitxCfg;     // nullable
            _sectionxCfg = sectionxCfg; // nullable
            _camarasCfg = camarasCfg;   // nullable
            _quantixCfg = quantixCfg;   // nullable
            _vistaxCfg = vistaxCfg;     // nullable
            _vistaxLive = vistaxLive;   // nullable
            _debug = debug;             // nullable
            _lotes = lotes;             // nullable
            _vehicleTool = vehicleTool; // nullable
            _shapefile = shapefile;     // nullable
            _coverage = coverage;             // nullable
            _sectionsCore = sectionsCore;     // nullable
            _quantixRuntime = quantixRuntime; // nullable
            _guidance = guidance;             // nullable
            _imuCalibracion = imuCalibracion; // nullable
            _steerConfig = steerConfig;       // nullable
            _trackList = trackList;           // nullable
            _perfiles = perfiles;             // nullable
            _configVehiculo = configVehiculo; // nullable
            _headlandEdit = headlandEdit;     // nullable
            _tramSimple = tramSimple;         // nullable
            _nudge = nudge;                   // nullable
            _quickAb = quickAb;               // nullable
            _flags = flags;                   // nullable
            _contorno = contorno;             // nullable
            _cabeceraLineas = cabeceraLineas;   // nullable
            _tramLine = tramLine;               // nullable
            _trackBuilder = trackBuilder;       // nullable
            _recPath = recPath;                 // nullable
            _toolGeometry = toolGeometry;     // nullable (Stage 4a render OpenGL)
            _tram = tram;                     // nullable (Stage 4b render OpenGL)
            _paths = paths;                   // nullable (Stage 5 render OpenGL)
            _pilotxUpdate = pilotxUpdate;     // nullable
            _flowxCfg = flowxCfg;             // nullable
            _flowxLive = flowxLive;           // nullable
            _stormxCfg = stormxCfg;           // nullable
            _stormxLive = stormxLive;         // nullable
            _linexCfg = linexCfg;             // nullable
            _linexLive = linexLive;           // nullable
            // Catálogo de insumos: si nadie lo pasa, el host instancia uno
            // default (file-based, sin dependencias) para que la página
            // /pages/insumos.html y los endpoints /api/insumos funcionen
            // incluso si el caller no se enteró del feature todavía.
            _insumos = insumos ?? new InsumoCatalogService();
            _prescripciones = new PrescripcionService();
            // Calibración VistaX: solo tiene sentido si hay live + config + catálogo.
            // Si alguno falta, dejamos null y el controller responde service-unavailable.
            _vistaxCalib = (_vistaxLive != null && _vistaxCfg != null && _insumos != null)
                ? new VistaXCalibracionService(_vistaxLive, _vistaxCfg, _insumos)
                : null;
            // FieldMapsService: provee /api/mapas/* para la página Mapas del Hub.
            // No requiere config — solo lee el snapshot PilotX y los .shp existentes.
            _fieldMaps = new FieldMapsService(_state);
            // CoreX-ECU: bridge HTTP al firmware Teensy. Auto-instanciado para que
            // /pages/corex-ecu.html funcione aunque el shell no se entere del módulo.
            _corexEcu = new CoreXEcuService();
            _corexBridge = new CoreXBridgeService();
            // Nodos curados + estado del wizard: archivos pequeños, sin dependencias.
            // Auto-instanciados para que /pages/nodos.html y /pages/setup.html
            // funcionen aunque el shell no se entere de los nuevos servicios.
            _nodosCurated = new NodosCuratedService();
            _setupState = new SetupStateService();
            // ImplementoService: source-of-truth de geometría física del Hub.
            // VistaX/VehicleTool son opcionales — sólo se usan para sembrar un
            // "default" en la primera ejecución si no hay implementos/ todavía.
            // El bootstrap del shell puede inyectar UNA instancia compartida para
            // que VistaXLiveService y el WebHost lean el mismo cache (sin dos copias
            // desincronizadas). Si nadie la pasa, se auto-instancia.
            _implemento = implemento ?? new ImplementoService(_vistaxCfg, _vehicleTool, _quantixCfg, _sectionxCfg);
            _wwwroot = wwwroot;
            // Publicado para los controllers que leen archivos servidos (catálogo
            // de sprites): armar esa ruta desde BaseDirectory falla cuando el
            // motor corre desde <install>\Engine\.
            AgroParallel.Common.AgpPaths.WwwRoot = wwwroot;
            _port = port;
            // Url publica: la usa el WebView2 del Hub WinForms (loopback, no requiere LAN).
            Url = "http://127.0.0.1:" + port + "/";
            // Prefijo de listener: escucha en TODAS las interfaces para que el celular
            // del operario pueda acceder a /m/ (PWA Field) desde el WiFi del tractor.
            // EmbedIO HttpListenerMode.EmbedIO usa Sockets, no requiere URLACL en Windows.
            ListenerPrefix = "http://*:" + port + "/";
        }

        // Prefijo real usado para el bind (puede ser "*" para LAN; Url sigue siendo 127.0.0.1).
        private string ListenerPrefix { get; set; }

        public void Start()
        {
            if (IsRunning) return;

            _telemetry = new TelemetryHub(_state);
            _debugHub = _debug != null ? new DebugHub(_debug) : null;
            _quantixHub = _nodos != null ? new QuantiXLiveHub(_nodos) : null;

            // OTA coordinator: necesita el registry MQTT vivo. Si no hay
            // registry, los endpoints de OTA quedan en service-unavailable y
            // el resto del Hub sigue andando.
            if (_nodos != null)
            {
                _otaCoord = new FirmwareOtaCoordinator(_nodos, OrbitXConfig.Load());
                _ = _otaCoord.StartAsync();
            }

            _server = new WebServer(o => o
                    .WithUrlPrefix(ListenerPrefix)
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithLocalSessionManager()
                .WithModule(_telemetry);

            if (_debugHub != null) _server = _server.WithModule(_debugHub);
            if (_quantixHub != null) _server = _server.WithModule(_quantixHub);

            _server = _server.WithWebApi("/api", m =>
            {
                m.WithController(() => new AogStateController(_state))
                 .WithController(() => new SistemaController(_sistema, _port))
                 .WithController(() => new NodosController(_nodos, _nodosCurated, _otaCoord, _orbitxCfg, _implemento))
                 .WithController(() => new SetupController(_setupState, _nodos, _nodosCurated, _orbitxCfg))
                 .WithController(() => new QuantiXController(_nodos, _quantixCfg))
                 .WithController(() => new SonidosController(Sonidos, _wwwroot))
                 .WithController(() => new OrbitXController(_orbitxCfg))
                 .WithController(() => new FirmwaresController())
                 .WithController(() => new TecladoController())
                 .WithController(() => new BotoneraController())
                 .WithController(() => new ConfiguracionController())
                 .WithController(() => new SteerConfigController(_steerConfig))
                 .WithController(() => new SectionXController(_sectionxCfg))
                 .WithController(() => new CamarasController(_camarasCfg));
                if (_vistaxCfg != null || _vistaxLive != null)
                    m.WithController(() => new VistaXController(_vistaxCfg, _vistaxLive, _vistaxCalib, _implemento));
                if (_debug != null) m.WithController(() => new DebugController(_debug));
                if (_lotes != null) m.WithController(() => new LotesController(_lotes));
                if (_vehicleTool != null) m.WithController(() => new VehicleToolController(_vehicleTool));
                if (_shapefile != null) m.WithController(() => new ShapefileController(_shapefile));
                if (_coverage != null) m.WithController(() => new CoverageController(_coverage));
                if (_sectionsCore != null) m.WithController(() => new SectionControlController(_sectionsCore));
                if (_quantixRuntime != null) m.WithController(() => new QuantiXRuntimeController(_quantixRuntime));
                // Widget QuantiX HTML overlay sobre el OpenGL de PilotX.
                // Requiere runtime para el objetivo efectivo + registry para
                // online/pps_real + state para velocidad/ancho. Si falta runtime,
                // omitimos el controller (la página se ve "sin nodos").
                if (_quantixRuntime != null)
                    m.WithController(() => new WidgetQuantiXController(_quantixRuntime, _nodos, _state));
                if (_guidance != null) m.WithController(() => new GuidanceController(_guidance));
                if (_toolGeometry != null) m.WithController(() => new ToolGeometryController(_toolGeometry));
                if (_tram != null) m.WithController(() => new TramController(_tram));
                if (_paths != null) m.WithController(() => new PathsController(_paths));
                if (_pilotxUpdate != null) m.WithController(() => new PilotXUpdateController(_pilotxUpdate));
                if (_flowxCfg != null) m.WithController(() => new FlowXController(_flowxCfg, _nodos, _flowxLive));
                if (_stormxCfg != null) m.WithController(() => new StormXController(_stormxCfg, _nodos, _stormxLive));
                if (_linexCfg != null) m.WithController(() => new LineXController(_linexCfg, _nodos, _linexLive));
                if (_insumos != null) m.WithController(() => new InsumoCatalogController(_insumos));
                if (_fieldMaps != null) m.WithController(() => new MapasController(_fieldMaps));
                if (_prescripciones != null) m.WithController(() => new PrescripcionesController(_prescripciones));
                if (_implemento != null) m.WithController(() => new ImplementoController(_implemento, _nodosCurated));
                // Preferencias de overlays (widgets on/off del mapa de PilotX).
                // Controller stateless: lee/escribe el singleton OverlayPrefsService.Instance.
                m.WithController(() => new OverlayPrefsController());
                // CoreX-ECU: proxy al firmware Teensy de autosteer.
                if (_corexEcu != null) m.WithController(() => new CoreXEcuController(_corexEcu));
                // CoreX (AgIO): estado resumido de GPS/IMU/Machine/Steer para el Hub.
                if (_corexBridge != null) m.WithController(() => new CoreXBridgeController(_corexBridge));
                // Calibración de roll del IMU interno de PilotX (CAHRS/FormGPS.ahrs).
                if (_imuCalibracion != null) m.WithController(() => new ImuCalibracionController(_imuCalibracion));
                // Lista de guías (AB/curvas) del lote activo.
                if (_trackList != null) m.WithController(() => new TrackListController(_trackList));
                // Perfiles de vehículo (pages/perfiles.html).
                if (_perfiles != null) m.WithController(() => new PerfilesController(_perfiles));
                // Configuración completa vehículo/implemento (pages/config.html).
                if (_configVehiculo != null) m.WithController(() => new ConfigVehiculoController(_configVehiculo));
                // Editor de cabecera HTML (pages/cabecera.html) — flujo Build Around.
                if (_headlandEdit != null) m.WithController(() => new HeadlandController(_headlandEdit));
                if (_tramSimple != null) m.WithController(() => new TramSimpleController(_tramSimple));
                // Mover guía (pages/mover-guia.html) — FormNudge/FormRefNudge.
                if (_nudge != null) m.WithController(() => new NudgeController(_nudge));
                // AB rápido (pages/ab-rapido.html) — FormQuickAB.
                if (_quickAb != null) m.WithController(() => new QuickAbController(_quickAb));
                // Banderas (pages/banderas.html) — FormFlags/FormEnterFlag.
                if (_flags != null) m.WithController(() => new FlagsController(_flags));

                // Contorno (pages/contorno.html) — FormBoundary/FormBoundaryPlayer.
                if (_contorno != null) m.WithController(() => new ContornoController(_contorno));

                // Cabecera por líneas (pages/cabecera-lineas.html) — FormHeadAche.
                if (_cabeceraLineas != null) m.WithController(() => new CabeceraLineasController(_cabeceraLineas));
                if (_tramLine != null) m.WithController(() => new TramLineController(_tramLine));
                if (_trackBuilder != null) m.WithController(() => new TrackBuilderController(_trackBuilder));
                if (_recPath != null) m.WithController(() => new RecPathController(_recPath));
            });

            if (!string.IsNullOrEmpty(_wwwroot) && Directory.Exists(_wwwroot))
            {
                // Forzar REVALIDACIÓN de estáticos en el cliente: sin max-age el
                // WebView2 cachea .html/.js por heurística y quedaba corriendo
                // UI VIEJA después de un rebuild ("los botones no hacen nada",
                // "no se muestra nada"). Con no-cache el cliente revalida cada
                // vez (ETag → 304 si no cambió, sigue siendo rápido) y toma los
                // archivos nuevos apenas se reinicia PilotX.
                _server = _server.WithModule(new NoClientCacheModule());
#if DEBUG
                // DEV: sin cache. Cambios en wwwroot se ven sin recompilar.
                _server = _server.WithStaticFolder("/", _wwwroot, false, m =>
                {
                    m.WithContentCaching(false);
                });
#else
                // RELEASE: cache de estáticos en memoria DEL SERVIDOR (rápido);
                // el cliente revalida por el módulo no-cache de arriba.
                _server = _server.WithStaticFolder("/", _wwwroot, true, m =>
                {
                    m.WithContentCaching(true);
                });
#endif
            }

            _cts = new CancellationTokenSource();
            _ = _server.RunAsync(_cts.Token);
            _telemetry.Start();
            _debugHub?.Start();
            _quantixHub?.Start();
            _vistaxLive?.Start();
            _flowxLive?.Start();
            _stormxLive?.Start();
            _linexLive?.Start();

            // mDNS responder: publica "agroparallel.local" -> IPs LAN del tractor.
            // Asi el operario puede tipear http://agroparallel.local:5180/m/ desde
            // su celular sin tener que adivinar la IP del dia. Si falla (sin permisos
            // de socket multicast, firewall, etc.), seguimos sin mDNS pero el resto
            // del Hub anda igual.
            try { _mdns = new MdnsResponder("agroparallel"); _mdns.Start(); }
            catch { _mdns = null; }

            IsRunning = true;
        }

        // Módulo passthrough: setea Cache-Control: no-cache en TODAS las
        // respuestas y deja seguir el pipeline (IsFinalHandler = false). Los
        // controllers que quieren no-store lo pisan después sin problema.
        private sealed class NoClientCacheModule : EmbedIO.WebModuleBase
        {
            public NoClientCacheModule() : base("/") { }
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context)
            {
                context.Response.Headers["Cache-Control"] = "no-cache";
                return Task.CompletedTask;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _vistaxLive?.Stop(); } catch { }
            try { _flowxLive?.Stop(); } catch { }
            try { _stormxLive?.Stop(); } catch { }
            try { _linexLive?.Stop(); } catch { }
            try { _quantixHub?.Stop(); } catch { }
            try { _debugHub?.Stop(); } catch { }
            try { _telemetry?.Stop(); } catch { }
            try { _otaCoord?.Dispose(); _otaCoord = null; } catch { }
            try { _mdns?.Stop(); _mdns?.Dispose(); _mdns = null; } catch { }
            try { _cts?.Cancel(); } catch { }
            try { _server?.Dispose(); } catch { }
            _server = null;
            _cts = null;
        }

        public void Dispose() => Stop();
    }
}
