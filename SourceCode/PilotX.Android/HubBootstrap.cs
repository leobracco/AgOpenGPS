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
        private static AndroidPilotXUpdateService s_pilotxUpdate;
        private static UdpBridgeService s_lanBridge;
        private static AgIO.CNmeaParser s_nmeaParser;
        private static readonly System.Net.IPEndPoint s_epModule =
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("255.255.255.255"), 8888);

        // Host mínimo de CNmeaParser (AgIO) para el bridge LAN: arma el PGN
        // 0xD6 a partir de $GPGGA/$GPVTG/$PANDA crudo que llegue por WiFi
        // (mismo formato que CoreXEngineHost.SpGPS.OnDataReceived en Windows,
        // pero la fuente acá es un receptor GPS en red en vez de serie) y lo
        // manda al loopback donde ya escucha GuidanceEngineHost.
        private sealed class LanNmeaHost : AgIO.INmeaParserHost
        {
            public bool IsGpsSentencesOn => false;
            public bool IsLogMonitorOn => false;
            public void AppendLogMonitor(string text) { }
            public void SendNmeaPgn(byte[] pgn) => s_lanBridge?.SendToLoopback(pgn);
        }

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
                // GuidanceEngineStateServices.cs). Start() deja el loopback UDP
                // escuchando (:15555); el bridge LAN de abajo es lo que le puede
                // mandar PGN real sin serie/USB-OTG.
                var guidanceBaseDir = new DirectoryInfo(Path.Combine(dataDir, "GuidanceEngine"));
                if (!guidanceBaseDir.Exists) guidanceBaseDir.Create();
                s_guidance = new PilotXCore.GuidanceEngineHost(guidanceBaseDir);
                s_guidance.Start();

                // Bridge LAN de "CoreX" sin serie/USB-OTG: mismo patrón que
                // CoreXEngineHost.StartServices()/ReceiveFromLoopBack/ReceiveFromUdp
                // (PilotX.GuidanceEngine, Windows) pero sin los 6 puertos serie —
                // esos necesitan System.IO.Ports (NETSDK1047 en net9.0-android, ver
                // header de PilotX.GuidanceEngine.Core). Dos tipos de tráfico por
                // :9999: (a) PGN ya envuelto (0x80 0x81...) de módulos WiFi tipo
                // AutoSteer ECU, se reenvía tal cual; (b) NMEA crudo ($GPGGA/$GPVTG/
                // $PANDA) de un receptor GPS que saca NMEA por WiFi en vez de serie
                // — se parsea con CNmeaParser (mismo que usa CoreXEngineHost con
                // SpGPS.OnDataReceived) para armar el PGN 0xD6 que espera el motor.
                s_nmeaParser = new AgIO.CNmeaParser(new LanNmeaHost());
                s_lanBridge = new UdpBridgeService();
                s_lanBridge.OnLoopbackReceived += (data, ep) => s_lanBridge.SendUdpTo(data, s_epModule);
                s_lanBridge.OnUdpReceived += (data, ep) =>
                {
                    if (data == null || data.Length < 4) return;

                    if (data[0] == 0x80 && data[1] == 0x81)
                    {
                        s_lanBridge.SendToLoopback(data);
                    }
                    else if (data[0] == (byte)'$')
                    {
                        try { s_nmeaParser.ParseIncoming(System.Text.Encoding.ASCII.GetString(data)); }
                        catch (Exception ex) { Android.Util.Log.Warn("PilotX", "LAN NMEA parse: " + ex.Message); }
                    }
                };
                s_lanBridge.StartLoopback("127.0.0.1", 17777, 15555);
                s_lanBridge.StartUdp(9999);

                var guidanceCalc = new GuidanceEngineGuidanceCalculator(s_guidance);
                var lotes = new GuidanceEngineLotesService(s_guidance);
                var state = new GuidanceEngineStateProvider(s_guidance);
                var sectionsCore = new GuidanceEngineSectionControlService(s_guidance);
                var vehicleTool = new GuidanceEngineVehicleToolService(s_guidance);
                var coverage = new GuidanceEngineCoverageService(s_guidance);
                var quantixRuntime = new GuidanceEngineQuantiXRuntimeService(state);
                var pilotxUpdate = new AndroidPilotXUpdateService(dataDir);
                s_pilotxUpdate = pilotxUpdate;

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
                    pilotxUpdate,
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
                try { s_lanBridge?.Stop(); } catch { }
                s_lanBridge = null;
                s_nmeaParser = null;
                try { s_guidance?.Stop(); } catch { }
                try { s_pilotxUpdate?.Dispose(); } catch { }
                try { s_nodos?.Stop(); } catch { }
                try { s_broker?.StopAsync().GetAwaiter().GetResult(); } catch { }
                s_flowxBridge = null;
                s_guidance = null;
                s_pilotxUpdate = null;
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
