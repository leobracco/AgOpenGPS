// ============================================================================
// AgpWebHostBootstrap.cs
// Singleton estatico que levanta UN solo AgpWebHost (127.0.0.1:5180) y lo
// comparte entre FormGPS (que lo arranca eager en su Load) y los hosts que
// lo consumen (FormAgroParallelHubWebView2 y/o AgroParallel.Shell.Avalonia
// que apuntan a la URL via WebView2).
//
// Antes el host vivia adentro del Hub y solo se levantaba al click "AP".
// Eso impedia que widgets Avalonia standalone (--page=pages/camaras.html)
// pudieran conectar antes de abrir el Hub. Ahora arranca con PilotX.
// ============================================================================

using System;
using AgroParallel.FlowX;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.WebHost;

namespace AgroParallel.Shell
{
    public static class AgpWebHostBootstrap
    {
        private static readonly object s_lock = new object();
        private static AgpWebHost s_host;
        private static NodoRegistryService s_nodos;
        private static FlowXBridge s_flowxBridge;
        private static IFlowXLiveService s_flowxLive;
        private static IFlowXConfigService s_flowxCfg;
        private static ISectionXConfigService s_sectionxCfg;
        private static string s_url;

        public static AgpWebHost Host { get { lock (s_lock) return s_host; } }
        public static string Url { get { lock (s_lock) return s_url; } }
        public static bool IsRunning { get { lock (s_lock) return s_host != null; } }

        // Expuestos para que widgets fuera del WebHost (overlay PilotX) puedan
        // leer telemetría sin pasar por HTTP loopback.
        public static IFlowXLiveService FlowXLive { get { lock (s_lock) return s_flowxLive; } }
        public static IFlowXConfigService FlowXConfigSvc { get { lock (s_lock) return s_flowxCfg; } }

        // Expuesto para que FormGPS pueda suscribirse al evento ConfigSaved y
        // relanzar SectionXBridge cuando el operario guarda desde la UI Hub
        // (si no se relanza, /sections nunca se publica → relays no se activan).
        public static ISectionXConfigService SectionXConfigSvc { get { lock (s_lock) return s_sectionxCfg; } }

        /// <summary>
        /// Idempotente: si ya hay host corriendo, no hace nada.
        /// Lo invocan FormGPS_Load (arranque temprano) y el Hub WebView2 (defensive).
        /// </summary>
        public static AgpWebHost EnsureStarted(
            IAogStateProvider state,
            ILotesService lotes,
            IVehicleToolService vehicleTool,
            IShapefileService shapefile,
            ICoverageService coverage,
            ISectionControlService sectionsCore,
            IQuantiXRuntimeService quantixRuntime,
            IGuidanceCalculator guidance,
            IPilotXUpdateService pilotxUpdate,
            string wwwroot,
            int port = 5180,
            string brokerHost = "127.0.0.1",
            int brokerPort = 1883)
        {
            lock (s_lock)
            {
                if (s_host != null) return s_host;

                s_nodos = new NodoRegistryService();
                try { s_nodos.Start(brokerHost, brokerPort); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AgpBootstrap] NodoRegistry start: " + ex.Message); }

                var vistaxCfg = new VistaXConfigService();
                var insumosCat = new InsumoCatalogService();
                var vistaxLive = new VistaXLiveService(s_nodos, vistaxCfg, insumosCat);
                var flowxCfg = new FlowXConfigService();
                var flowxLive = new FlowXLiveService(s_nodos, flowxCfg);
                var stormxCfg = new StormXConfigService();
                var stormxLive = new StormXLiveService(s_nodos, stormxCfg);
                // Instancia única expuesta a FormGPS — sin esto el controller
                // construiría su propia y el evento ConfigSaved no llegaría al shell.
                var sectionxCfg = new SectionXConfigService();

                var host = new AgpWebHost(
                    state,
                    new SistemaService(),
                    s_nodos,
                    new OrbitXConfigService(),
                    sectionxCfg,
                    new CamarasConfigService(),
                    new QuantiXConfigService(s_nodos),
                    vistaxCfg,
                    vistaxLive,
                    new DebugLogService(),
                    lotes,
                    vehicleTool,
                    shapefile,
                    coverage,
                    sectionsCore,
                    quantixRuntime,
                    guidance,
                    pilotxUpdate,
                    flowxCfg,
                    flowxLive,
                    stormxCfg,
                    stormxLive,
                    wwwroot,
                    port);
                host.Start();
                s_host = host;
                s_url = host.Url;
                // host.Start() ya hace flowxLive.Start(); guardamos refs para
                // que widgets de PilotX puedan leer el snapshot in-process.
                s_flowxLive = flowxLive;
                s_flowxCfg = flowxCfg;
                s_sectionxCfg = sectionxCfg;

                // FlowXBridge: publica targets PC -> ESP. No depende del WebHost,
                // pero el ciclo de vida queda atado al bootstrap para que arranque
                // / pare junto con todo lo demás. Si flowX.json no tiene nodos o
                // está deshabilitado, StartAsync sale en silencio (es idempotente).
                try
                {
                    s_flowxBridge = new FlowXBridge(state, FlowXConfig.Load());
                    _ = s_flowxBridge.StartAsync(); // fire-and-forget; loguea internamente
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[AgpBootstrap] FlowXBridge start: " + ex.Message);
                }

                return s_host;
            }
        }

        public static void Stop()
        {
            lock (s_lock)
            {
                try { s_flowxBridge?.Stop(); s_flowxBridge?.Dispose(); } catch { }
                try { (s_flowxLive as IDisposable)?.Dispose(); } catch { }
                try { s_host?.Stop(); } catch { }
                try { s_nodos?.Stop(); } catch { }
                s_flowxBridge = null;
                s_flowxLive = null;
                s_flowxCfg = null;
                s_sectionxCfg = null;
                s_host = null;
                s_nodos = null;
                s_url = null;
            }
        }
    }
}
