// ============================================================================
// EngineWebHost.cs — levanta el AgpWebHost (netstandard2.0, EmbedIO :5180)
// contra el GuidanceEngineHost en vez de FormGPS. Sirve exactamente los 6
// endpoints /api/aog/{state,coverage,tool,tram,paths,guidance} que PilotX.Desktop
// pollea para renderizar el mapa — o sea, el motor headless queda "detrás" de
// la MISMA API HTTP que hoy sirve FormGPS, sin WinForms/GL.
//
// No usa AgpWebHostBootstrap (net48, WinForms Shell): instancia AgpWebHost
// directo. Todos los servicios que el mapa no necesita van en null — sus
// controllers se registran solo `if (svc != null)`, así que quedan fuera; los
// controllers "siempre-on" se construyen lazy por request y PilotX.Desktop
// nunca los toca. wwwroot = null: PilotX.Desktop es nativo, no carga HTML.
// ============================================================================

using System;
using System.IO;
using AgroParallel.FlowX;
using AgroParallel.Services;
using AgroParallel.WebHost;
using PilotX.GuidanceEngine.Adapters;

namespace AgOpenGPS
{
    public sealed class EngineWebHost
    {
        // Ubica el wwwroot del Hub para servir las páginas HTML (config, colores,
        // gráficos, etc.) que las barras del cockpit abren en el WebView de
        // PilotX.Desktop. Prueba el Build empaquetado y el WebUI del source.
        private static string ResolveWwwroot()
        {
            var baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                // INSTALADO: el motor vive en <install>\Engine\ y el wwwroot lo
                // deja el paquete en <install>\AgroParallel\wwwroot (hermano de
                // Engine\). Va PRIMERO porque es el layout de la cabina; sin esto
                // las pantallas HTML del Hub daban 404 al abrirlas desde
                // PilotX.Desktop (Dirección, config, gráficos…).
                Path.GetFullPath(Path.Combine(baseDir, "..", "AgroParallel", "wwwroot")),
                // DESARROLLO: corriendo desde SourceCode\...\bin\<cfg>\net9.0.
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "AgroParallel", "Web", "AgroParallel.WebUI", "wwwroot")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "Build", "AgroParallel", "wwwroot")),
                Path.GetFullPath(Path.Combine(baseDir, "wwwroot")),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c))
                {
                    Console.WriteLine("wwwroot: " + c);
                    return c;
                }

            // Sin wwwroot la API sigue andando pero TODA página del Hub da 404 —
            // avisarlo fuerte, que es exactamente el síntoma que se ve en cabina.
            Console.Error.WriteLine("wwwroot NO ENCONTRADO — las páginas del Hub van a dar 404. Buscado en:");
            foreach (var c in candidates) Console.Error.WriteLine("  " + c);
            return null;
        }

        private readonly GuidanceEngineHost _host;
        private readonly int _port;
        private readonly string _brokerHost;
        private readonly int _brokerPort;
        private AgpWebHost _web;
        private NodoRegistryService _nodos;
        private FlowXBridge _flowxBridge;

        /// <summary>Registro de nodos MQTT compartido: lo usan los bridges que
        /// publican targets (QuantiX/SectionX) en vez de abrir otra conexión.</summary>
        public NodoRegistryService Nodos => _nodos;

        public EngineWebHost(GuidanceEngineHost host, int port = 5180,
            string brokerHost = "127.0.0.1", int brokerPort = 1883)
        {
            _host = host;
            _port = port;
            _brokerHost = brokerHost;
            _brokerPort = brokerPort;
        }

        public string Url => _web?.Url;

        public void Start()
        {
            if (_web != null) return;

            var state = new EngineStateProvider(_host);
            var coverage = new EngineCoverageService(_host);
            var guidance = new EngineGuidanceCalculator(_host);
            var toolGeom = new EngineToolGeometryCalculator(_host);
            var tram = new EngineTramCalculator(_host);
            var paths = new EnginePathsCalculator(_host);
            var lotes = new EngineLotesService(_host);
            var trackBuilder = new EngineTrackBuilderService(_host);
            var trackList = new EngineTrackListService(_host);
            var sectionsCore = new EngineSectionControlService(_host);
            // Config de vehículo/herramienta/IMU: es lo que hace andar la pantalla
            // de Configuración. Al guardar la herramienta recalcula la geometría
            // de secciones, si no la huella queda con el reparto viejo.
            var vehicleTool = new EngineVehicleToolService(_host);
            // Pantalla de Configuración (config.html) y calibración de IMU:
            // ambas daban 404 contra el motor.
            var configVehiculo = new EngineConfigVehiculoService(_host);
            var imuCalibracion = new EngineImuCalibracionService(_host);
            // Config de dirección: implementación compartida con FormGPS (archivo
            // linkeado). El engine no tiene hilo de UI, así que SendSettings va
            // directo; el ángulo vivo del WAS sale del CModuleComm del host.
            var steerConfig = new AgroParallel.Adapters.SteerConfigService(
                _host.Vehicle,
                () => _host.Mc.actualSteerAngleDegrees,
                () => _host.SettingsSender.SendSettings());

            // ── Productos X-* ────────────────────────────────────────────────
            // Sin esto el motor headless servía el mapa pero NADA de QuantiX,
            // VistaX, FlowX ni nodos: contra PilotX.Desktop esas pantallas daban
            // 404 y el operario veía paneles vacíos. Mismo bloque que arma el
            // host WinForms (AgpWebHostBootstrap) y el head Android.
            _nodos = new NodoRegistryService();
            try { _nodos.Start(_brokerHost, _brokerPort); }
            catch (Exception ex)
            {
                // El registro de nodos es por MQTT: si el broker no está, los
                // paneles quedan sin nodos pero el guiado tiene que seguir.
                Console.Error.WriteLine("[Engine] NodoRegistry: " + ex.Message);
            }

            var vistaxCfg = new VistaXConfigService();
            var insumosCat = new InsumoCatalogService();
            var sectionxCfg = new SectionXConfigService();
            var orbitxCfg = new OrbitXConfigService();
            var quantixCfg = new QuantiXConfigService(_nodos);
            // UNA sola instancia de implemento compartida: si el live de VistaX
            // arma la suya, el overlay muestra geometría vieja hasta reiniciar.
            var implemento = new ImplementoService(vistaxCfg, vehicleTool, quantixCfg, sectionxCfg);
            var vistaxLive = new VistaXLiveService(_nodos, vistaxCfg, insumosCat, state, sectionsCore, implemento);
            var quantixRuntime = new QuantiXRuntimeService(state);
            var flowxCfg = new FlowXConfigService();
            var flowxLive = new FlowXLiveService(_nodos, flowxCfg);
            var stormxCfg = new StormXConfigService();
            var stormxLive = new StormXLiveService(_nodos, stormxCfg);
            var linexCfg = new LineXConfigService();
            var linexLive = new LineXLiveService(_nodos, linexCfg);

            // sistema (brillo/apagado) queda en null: la implementación es net48
            // + WinForms (dxva2/WMI) y no porta al motor headless. Pendiente:
            // versión net9 para que la página Sistema del Hub ande contra él.
            _web = new AgpWebHost(
                state,                 // requerido
                sistema: null,
                nodos: _nodos,
                orbitxCfg: orbitxCfg,
                sectionxCfg: sectionxCfg,
                camarasCfg: new CamarasConfigService(),
                quantixCfg: quantixCfg,
                vistaxCfg: vistaxCfg,
                vistaxLive: vistaxLive,
                debug: new DebugLogService(),
                lotes: lotes,
                // Sin esto PerfilesController no se registra y /api/aog/perfiles
                // da 404: la pantalla de perfiles del Hub no lista nada.
                perfiles: new EnginePerfilService(_host),
                vehicleTool: vehicleTool,
                shapefile: null,
                coverage: coverage,
                sectionsCore: sectionsCore,
                quantixRuntime: quantixRuntime,
                guidance: guidance,
                pilotxUpdate: null,
                flowxCfg: flowxCfg,
                flowxLive: flowxLive,
                stormxCfg: stormxCfg,
                stormxLive: stormxLive,
                linexCfg: linexCfg,
                linexLive: linexLive,
                wwwroot: ResolveWwwroot(),
                port: _port,
                toolGeometry: toolGeom,
                tram: tram,
                implemento: implemento,
                paths: paths,
                trackBuilder: trackBuilder,
                trackList: trackList,
                steerConfig: steerConfig,
                configVehiculo: configVehiculo,
                imuCalibracion: imuCalibracion);

            _web.Start();

            // FlowX comanda la válvula de dosificación líquida: va atado al
            // ciclo de vida del host. Si flowX.json está vacío o deshabilitado,
            // sale en silencio.
            try
            {
                _flowxBridge = new FlowXBridge(state, FlowXConfig.Load());
                _ = _flowxBridge.StartAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] FlowXBridge: " + ex.Message);
            }
        }

        public void Stop()
        {
            try { _flowxBridge?.Stop(); _flowxBridge?.Dispose(); } catch { }
            _flowxBridge = null;
            try { _web?.Stop(); } catch { }
            _web = null;
            try { _nodos?.Dispose(); } catch { }
            _nodos = null;
        }
    }
}
