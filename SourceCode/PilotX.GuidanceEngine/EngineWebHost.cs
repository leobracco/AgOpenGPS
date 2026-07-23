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
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "AgroParallel", "Web", "AgroParallel.WebUI", "wwwroot")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "Build", "AgroParallel", "wwwroot")),
                Path.GetFullPath(Path.Combine(baseDir, "wwwroot")),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) return c;
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
                vehicleTool: null,
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
                trackList: trackList);

            _web.Start();
        }

        public void Stop()
        {
            try { _web?.Stop(); } catch { }
            _web = null;
        }
    }
}
