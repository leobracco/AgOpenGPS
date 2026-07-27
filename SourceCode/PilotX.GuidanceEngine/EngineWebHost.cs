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
        private AgpWebHost _web;

        public EngineWebHost(GuidanceEngineHost host, int port = 5180)
        {
            _host = host;
            _port = port;
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
            // Config de dirección: implementación compartida con FormGPS (archivo
            // linkeado). El engine no tiene hilo de UI, así que SendSettings va
            // directo; el ángulo vivo del WAS sale del CModuleComm del host.
            var steerConfig = new AgroParallel.Adapters.SteerConfigService(
                _host.Vehicle,
                () => _host.Mc.actualSteerAngleDegrees,
                () => _host.SettingsSender.SendSettings());

            _web = new AgpWebHost(
                state,                 // requerido
                sistema: null,
                nodos: null,
                orbitxCfg: null,
                sectionxCfg: null,
                camarasCfg: null,
                quantixCfg: null,
                vistaxCfg: null,
                vistaxLive: null,
                debug: null,
                lotes: lotes,
                vehicleTool: vehicleTool,
                shapefile: null,
                coverage: coverage,
                sectionsCore: sectionsCore,
                quantixRuntime: null,
                guidance: guidance,
                pilotxUpdate: null,
                flowxCfg: null,
                flowxLive: null,
                stormxCfg: null,
                stormxLive: null,
                linexCfg: null,
                linexLive: null,
                wwwroot: ResolveWwwroot(),
                port: _port,
                toolGeometry: toolGeom,
                tram: tram,
                paths: paths,
                trackBuilder: trackBuilder,
                trackList: trackList,
                steerConfig: steerConfig);

            _web.Start();
        }

        public void Stop()
        {
            try { _web?.Stop(); } catch { }
            _web = null;
        }
    }
}
