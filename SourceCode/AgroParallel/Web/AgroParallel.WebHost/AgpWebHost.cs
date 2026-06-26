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
        private readonly IToolGeometryCalculator _toolGeometry;
        private readonly ITramCalculator _tram;
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
        private readonly int _port;
        private WebServer _server;
        private CancellationTokenSource _cts;
        private TelemetryHub _telemetry;
        private DebugHub _debugHub;
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
                          ITramCalculator tram = null)
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
            _toolGeometry = toolGeometry;     // nullable (Stage 4a render OpenGL)
            _tram = tram;                     // nullable (Stage 4b render OpenGL)
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
            // Nodos curados + estado del wizard: archivos pequeños, sin dependencias.
            // Auto-instanciados para que /pages/nodos.html y /pages/setup.html
            // funcionen aunque el shell no se entere de los nuevos servicios.
            _nodosCurated = new NodosCuratedService();
            _setupState = new SetupStateService();
            // ImplementoService: source-of-truth de geometría física del Hub.
            // VistaX/VehicleTool son opcionales — sólo se usan para sembrar un
            // "default" en la primera ejecución si no hay implementos/ todavía.
            // El service NO escribe en la config nativa AOG (sin SyncToVehicleTool):
            // el Hub edita su propio catálogo y AgValoniaGPS/AOG mantiene su Tool aparte.
            _implemento = new ImplementoService(_vistaxCfg, _vehicleTool, _quantixCfg, _sectionxCfg);
            _wwwroot = wwwroot;
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

            _server = _server.WithWebApi("/api", m =>
            {
                m.WithController(() => new AogStateController(_state))
                 .WithController(() => new SistemaController(_sistema, _port))
                 .WithController(() => new NodosController(_nodos, _nodosCurated, _otaCoord, _orbitxCfg, _implemento))
                 .WithController(() => new SetupController(_setupState, _nodos, _nodosCurated, _orbitxCfg))
                 .WithController(() => new QuantiXController(_nodos, _quantixCfg))
                 .WithController(() => new OrbitXController(_orbitxCfg))
                 .WithController(() => new FirmwaresController())
                 .WithController(() => new SectionXController(_sectionxCfg))
                 .WithController(() => new CamarasController(_camarasCfg));
                if (_vistaxCfg != null || _vistaxLive != null)
                    m.WithController(() => new VistaXController(_vistaxCfg, _vistaxLive, _vistaxCalib));
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
            });

            if (!string.IsNullOrEmpty(_wwwroot) && Directory.Exists(_wwwroot))
            {
#if DEBUG
                // DEV: sin cache. Cambios en wwwroot se ven sin recompilar.
                _server = _server.WithStaticFolder("/", _wwwroot, false, m =>
                {
                    m.WithContentCaching(false);
                });
#else
                // RELEASE: cache de estáticos en memoria + Cache-Control immutable.
                // El rebuild dispara invalidación natural (los .js/.css se copian
                // nuevos al output), así que es seguro. Mejora el "second open"
                // del Hub y la navegación inter-páginas en la PC del tractor.
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

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _vistaxLive?.Stop(); } catch { }
            try { _flowxLive?.Stop(); } catch { }
            try { _stormxLive?.Stop(); } catch { }
            try { _linexLive?.Stop(); } catch { }
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
