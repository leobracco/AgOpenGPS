// ============================================================================
// HubBootstrap.cs
// Réplica Android del AgpWebHostBootstrap (que es net48/Shell): levanta el
// broker MQTT embebido + NodoRegistry + AgpWebHost:5180 con los servicios
// reales netstandard (VistaX/QuantiX/FlowX/StormX/LineX/OrbitX/nodos/OTA) y
// stubs Fase 1 para lo FormGPS-backed. Idempotente.
// ============================================================================

using System;
using System.IO;
using AgroParallel.FlowX;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.WebHost;
using PilotXCore = global::AgOpenGPS;

namespace PilotX.Droid
{
    internal static class HubBootstrap
    {
        private static readonly object s_lock = new object();
        private static AgpWebHost s_host;
        private static MqttBrokerService s_broker;
        private static NodoRegistryService s_nodos;
        private static FlowXBridge s_flowxBridge;
        private static PilotXCore.GuidanceEngineHost s_guidance;

        public static bool IsRunning { get { lock (s_lock) return s_host != null; } }
        public static string Url { get { lock (s_lock) return s_host?.Url; } }

        /// <summary>
        /// dataDir: files internos (settings/configs). wwwroot: carpeta ya
        /// extraída de assets. externalDataDir: raíz para Fields/Vehicles/Logs.
        /// </summary>
        public static void Start(string dataDir, string wwwroot, string externalDataDir)
        {
            lock (s_lock)
            {
                if (s_host != null) return;

                // Paths de plataforma ANTES de que nadie lea Settings ni
                // instancie servicios (el BaseDirectory del APK es read-only).
                AgroParallel.Common.AgpPaths.ConfigRoot = dataDir;
                PilotXCore.RegistrySettings.AppBasePath = dataDir;
                PilotXCore.RegistrySettings.DataRootOverride = externalDataDir;
                PilotXCore.RegistrySettings.Load();

                // Broker MQTT embebido (en Windows vive en CoreX; acá es
                // proceso único — bloque 14 de la matriz).
                s_broker = new MqttBrokerService();
                s_broker.StartAsync(1883).GetAwaiter().GetResult();

                s_nodos = new NodoRegistryService();
                try { s_nodos.Start("127.0.0.1", 1883); }
                catch (Exception ex) { Android.Util.Log.Warn("PilotX", "NodoRegistry: " + ex.Message); }

                // Guidance engine headless (bloque 14) — reemplaza los stubs de
                // guiado/lotes/estado/cobertura/secciones/vehiculo-tool/QuantiX
                // runtime por implementaciones reales (GuidanceEngineServices.cs +
                // GuidanceEngineStateServices.cs). Sin fix GPS real todavia
                // (necesita CoreX/serial por USB-OTG, bloque 8 pendiente de
                // hardware): Start() solo deja el loopback UDP escuchando, sin
                // nada que le mande PGN por ahora.
                var guidanceBaseDir = new DirectoryInfo(Path.Combine(dataDir, "GuidanceEngine"));
                if (!guidanceBaseDir.Exists) guidanceBaseDir.Create();
                s_guidance = new PilotXCore.GuidanceEngineHost(guidanceBaseDir);
                s_guidance.Start();
                var guidanceCalc = new GuidanceEngineGuidanceCalculator(s_guidance);
                var lotes = new GuidanceEngineLotesService(s_guidance);
                var state = new GuidanceEngineStateProvider(s_guidance);
                var sectionsCore = new GuidanceEngineSectionControlService(s_guidance);
                var vehicleTool = new GuidanceEngineVehicleToolService(s_guidance);
                var coverage = new GuidanceEngineCoverageService(s_guidance);
                var quantixRuntime = new GuidanceEngineQuantiXRuntimeService(state);

                var vistaxCfg = new VistaXConfigService();
                var insumosCat = new InsumoCatalogService();
                var sectionxCfg = new SectionXConfigService();
                var orbitxCfg = new OrbitXConfigService();
                var quantixCfg = new QuantiXConfigService(s_nodos);
                var implemento = new ImplementoService(vistaxCfg, vehicleTool, quantixCfg, sectionxCfg);
                var vistaxLive = new VistaXLiveService(s_nodos, vistaxCfg, insumosCat, state, sectionsCore, implemento);
                var flowxCfg = new FlowXConfigService();
                var flowxLive = new FlowXLiveService(s_nodos, flowxCfg);
                var stormxCfg = new StormXConfigService();
                var stormxLive = new StormXLiveService(s_nodos, stormxCfg);
                var linexCfg = new LineXConfigService();
                var linexLive = new LineXLiveService(s_nodos, linexCfg);

                var host = new AgpWebHost(
                    state,
                    new StubSistemaService(),
                    s_nodos,
                    orbitxCfg,
                    sectionxCfg,
                    new CamarasConfigService(),
                    quantixCfg,
                    vistaxCfg,
                    vistaxLive,
                    new DebugLogService(),
                    lotes,
                    vehicleTool,
                    new StubShapefileService(),
                    coverage,
                    sectionsCore,
                    quantixRuntime,
                    guidanceCalc,
                    new StubPilotXUpdateService(),
                    flowxCfg,
                    flowxLive,
                    stormxCfg,
                    stormxLive,
                    linexCfg,
                    linexLive,
                    wwwroot,
                    5180,
                    insumos: null,          // igual que el bootstrap Windows
                    implemento: implemento);
                host.Start();
                s_host = host;

                try
                {
                    s_flowxBridge = new FlowXBridge(state, FlowXConfig.Load());
                    _ = s_flowxBridge.StartAsync();
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("PilotX", "FlowXBridge: " + ex.Message);
                }
            }
        }

        public static void Stop()
        {
            lock (s_lock)
            {
                try { s_flowxBridge?.Stop(); s_flowxBridge?.Dispose(); } catch { }
                try { s_host?.Stop(); } catch { }
                try { s_guidance?.Stop(); } catch { }
                try { s_nodos?.Stop(); } catch { }
                try { s_broker?.StopAsync().GetAwaiter().GetResult(); } catch { }
                s_flowxBridge = null;
                s_guidance = null;
                s_host = null;
                s_nodos = null;
                s_broker = null;
            }
        }

        /// <summary>
        /// Extrae los assets wwwroot/** a destDir. Se rehace si cambió la
        /// versión de la app (marker con el versionCode).
        /// </summary>
        public static string ExtractWwwroot(Android.Content.Context ctx)
        {
            string dest = Path.Combine(ctx.FilesDir.AbsolutePath, "wwwroot");
            string marker = Path.Combine(dest, ".version");
            string ver = ctx.PackageManager.GetPackageInfo(ctx.PackageName, 0).LongVersionCode.ToString();

            if (File.Exists(marker) && File.ReadAllText(marker) == ver) return dest;

            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            CopyAssetDir(ctx.Assets, "wwwroot", dest);
            File.WriteAllText(marker, ver);
            return dest;
        }

        private static void CopyAssetDir(Android.Content.Res.AssetManager assets, string src, string dest)
        {
            string[] entries = assets.List(src);
            if (entries == null || entries.Length == 0)
            {
                // src es archivo
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using (var input = assets.Open(src))
                using (var output = File.Create(dest))
                {
                    input.CopyTo(output);
                }
                return;
            }

            Directory.CreateDirectory(dest);
            foreach (string e in entries)
            {
                CopyAssetDir(assets, src + "/" + e, Path.Combine(dest, e));
            }
        }
    }
}
