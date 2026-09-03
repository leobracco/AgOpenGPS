// ============================================================================
// HubBootstrap.cs — bootstrap Android: GEMELO de EngineWebHost.Start() +
// CoreXEngineHost.StartServices() (PilotX.GuidanceEngine, Windows), en un solo
// proceso y sin puertos serie.
//
// Regla de mantenimiento: cada servicio que EngineWebHost cablea tiene que
// estar acá con la MISMA instancia de adaptador (los Engine* de
// PilotX.GuidanceEngine/Adapters se linkean por archivo en el csproj — antes
// había copias Android y derivaban: el APK dejó de compilar el 2026-09-03).
// Lo único propio de Android: paths, sistema/WiFi/audio de plataforma, el
// bridge LAN sin serie y el self-update por APK.
//
// Diferencias asumidas contra Windows (hardware):
//   · Sin System.IO.Ports: GPS/IMU/dirección sólo por WiFi/UDP (NMEA crudo o
//     PGN envuelto a :9999). USB-OTG queda para después.
//   · NTRIP: el RTCM sale por UDP :2233 a la subred (como el "NTRIP por UDP" de
//     CoreX hacia un AIO/CoreX-ECU); no hay UART donde volcarlo. La config se
//     lee del mismo corex-integrado.json (sección ntrip) que usa el panel
//     :5181 de Windows, ubicado en el dataDir de la app.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgroParallel.FlowX;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.WebHost;
using MQTTnet;
using MQTTnet.Client;
using PilotX.GuidanceEngine.Adapters;
using PilotXCore = global::AgOpenGPS;

namespace PilotX.Droid
{
    internal static class HubBootstrap
    {
        private static readonly object s_lock = new object();
        private static AgpWebHost s_host;
        private static MqttBrokerService s_broker;
        private static NodoRegistryService s_nodos;
        private static PilotXCore.GuidanceEngineHost s_guidance;
        private static AndroidPilotXUpdateService s_pilotxUpdate;

        // Bridge LAN (CoreX sin serie) + NMEA por WiFi.
        private static UdpBridgeService s_lanBridge;
        private static AgIO.CNmeaParser s_nmeaParser;
        private static System.Threading.Timer s_helloTimer;
        private static List<IPEndPoint> s_epsCache;
        private static int s_epsCachePort;
        private static DateTime s_epsCacheAt = DateTime.MinValue;

        // Procesos de fondo (mismos que EngineWebHost).
        private static FlowXBridge s_flowxBridge;
        private static AgroParallel.OrbitX.OrbitXSync s_orbitxSync;
        private static System.Threading.Timer s_orbitxRetry;
        private static SonidosAlarmService s_sonidos;
        private static AgroParallel.QuantiX.QuantiXMotorBridge s_quantixBridge;
        private static System.Threading.Timer s_quantixRetry;
        private static AgroParallel.Cut.CutDispatcher s_cutDispatcher;
        private static AgroParallel.SectionX.SectionsSpeedPublisher s_sectionsSpeed;
        private static System.Threading.Timer s_cutRetry;

        // NTRIP + comandos MQTT (CoreXEngineHost).
        private static NtripClientService s_ntrip;
        private static System.Threading.Timer s_ntripTick;
        private static IMqttClient s_cmdClient;

        /// <summary>Mismo frame que CoreXEngineHost.HelloAgIO: PGN 200 de descubrimiento,
        /// 1 Hz a :8888. Con él los módulos WiFi (CoreX-ECU, ToolX) aprenden la IP
        /// de la tablet y le mandan unicast en vez de broadcast.</summary>
        private static readonly byte[] HelloAgIO = { 0x80, 0x81, 0x7F, 200, 3, 56, 0, 0, 0x47 };

        // Host mínimo de CNmeaParser para el bridge LAN: arma el PGN 0xD6 a partir
        // de $GPGGA/$GPVTG/$PANDA crudo que llegue por WiFi y lo manda al loopback
        // donde escucha GuidanceEngineHost (igual que CoreXEngineHost.SpGPS).
        private sealed class LanNmeaHost : AgIO.INmeaParserHost
        {
            public bool IsGpsSentencesOn => false;
            public bool IsLogMonitorOn => false;
            public void AppendLogMonitor(string text) { }
            public void SendNmeaPgn(byte[] pgn) => s_lanBridge?.SendToLoopback(pgn);
        }

        public static bool IsRunning { get { lock (s_lock) return s_host != null; } }
        public static string Url { get { lock (s_lock) return s_host?.Url; } }

        /// <summary>Broadcasts dirigidos de cada interfaz IPv4 viva (+ el limitado y el
        /// de loopback), cacheados 2 s — port de CoreXEngineHost.EndpointsDeModulos.</summary>
        private static List<IPEndPoint> EndpointsDeModulos(int puerto)
        {
            var cache = s_epsCache;
            if (cache != null && s_epsCachePort == puerto && (DateTime.UtcNow - s_epsCacheAt).TotalSeconds < 2.0)
                return cache;

            var eps = new List<IPEndPoint>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        var ip = ua.Address.GetAddressBytes();
                        byte[] mask = null;
                        try { mask = ua.IPv4Mask?.GetAddressBytes(); } catch { }
                        // Android a veces no informa la máscara: /24 asumido.
                        if (mask == null) mask = new byte[] { 255, 255, 255, 0 };
                        if (ip[0] == 169 && ip[1] == 254) continue;
                        var bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        eps.Add(new IPEndPoint(new IPAddress(bc), puerto));
                    }
                }
            }
            catch { }
            eps.Add(new IPEndPoint(IPAddress.Broadcast, puerto));
            eps.Add(new IPEndPoint(IPAddress.Parse("127.255.255.255"), puerto));

            s_epsCache = eps;
            s_epsCachePort = puerto;
            s_epsCacheAt = DateTime.UtcNow;
            return eps;
        }

        private static void EnviarAModulos(byte[] data)
        {
            var bridge = s_lanBridge;
            if (bridge == null) return;
            foreach (var ep in EndpointsDeModulos(8888))
            {
                try { bridge.SendUdpTo(data, ep); } catch { }
            }
        }

        /// <summary>
        /// dataDir: files internos (settings/configs). wwwroot: carpeta ya
        /// extraída de assets. externalDataDir: raíz para Fields/Vehicles/Logs.
        /// </summary>
        public static void Start(string dataDir, string wwwroot, string externalDataDir)
        {
            lock (s_lock)
            {
                if (s_host != null) return;

                var ctx = Android.App.Application.Context;

                // Paths de plataforma ANTES de que nadie lea Settings ni
                // instancie servicios (el BaseDirectory del APK es read-only).
                AgroParallel.Common.AgpPaths.ConfigRoot = dataDir;
                PilotXCore.RegistrySettings.AppBasePath = dataDir;
                PilotXCore.RegistrySettings.DataRootOverride = externalDataDir;
                PilotXCore.RegistrySettings.Load();

                // ── CoreX embebido: broker + registro de nodos ─────────────
                s_broker = new MqttBrokerService();
                s_broker.StartAsync(1883).GetAwaiter().GetResult();

                s_nodos = new NodoRegistryService();
                try { s_nodos.Start("127.0.0.1", 1883); }
                catch (Exception ex) { Android.Util.Log.Warn("PilotX", "NodoRegistry: " + ex.Message); }

                // ── Motor de guiado headless ───────────────────────────────
                var guidanceBaseDir = new DirectoryInfo(Path.Combine(dataDir, "GuidanceEngine"));
                if (!guidanceBaseDir.Exists) guidanceBaseDir.Create();
                s_guidance = new PilotXCore.GuidanceEngineHost(guidanceBaseDir);
                // Anti-solape de secciones: Program.cs de Windows lo engancha
                // siempre (Habilitado salvo --sin-antisolape).
                s_guidance.AntiSolape = new PilotX.GuidanceEngine.AntiSolapeSecciones(s_guidance) { Habilitado = true };
                s_guidance.Start();

                // ── Bridge LAN (CoreX sin serie) ───────────────────────────
                // (a) PGN envuelto (0x80 0x81) de módulos WiFi → loopback tal cual
                //     (con el mismo anti-eco que CoreXEngineHost.ReceiveFromUdp:
                //     lo que ORIGINA el motor jamás viene de un módulo).
                // (b) NMEA crudo de un receptor GPS de red → CNmeaParser → PGN 0xD6.
                // Lo que el motor manda al loopback sale a los broadcasts :8888.
                s_nmeaParser = new AgIO.CNmeaParser(new LanNmeaHost());
                s_lanBridge = new UdpBridgeService();
                s_lanBridge.OnLoopbackReceived += (data, ep) => EnviarAModulos(data);
                s_lanBridge.OnUdpReceived += (data, ep) =>
                {
                    if (data == null || data.Length < 4) return;
                    if (data[0] == 0x80 && data[1] == 0x81)
                    {
                        byte pgn = data[3];
                        bool esNuestro = pgn == 0xD6 || pgn == 0xFE || pgn == 0xEF ||
                                         pgn == 0xE5 || pgn == 0xFC || pgn == 0xFB ||
                                         pgn == 0xEE || pgn == 0xEC || pgn == 0xEB;
                        if (!esNuestro) s_lanBridge.SendToLoopback(data);
                    }
                    else if (data[0] == (byte)'$')
                    {
                        try { s_nmeaParser.ParseIncoming(Encoding.ASCII.GetString(data)); }
                        catch (Exception ex) { Android.Util.Log.Warn("PilotX", "LAN NMEA parse: " + ex.Message); }
                    }
                };
                s_lanBridge.StartLoopback("127.0.0.1", 17777, 15555);
                s_lanBridge.StartUdp(9999);

                // Hello PGN 200 a 1 Hz (descubrimiento de módulos, como CoreXEngineHost).
                s_helloTimer = new System.Threading.Timer(_ =>
                {
                    try { EnviarAModulos(HelloAgIO); } catch { }
                }, null, 1000, 1000);

                // ── Adaptadores del engine (los MISMOS archivos que Windows) ──
                PrescripcionService.LoteActualProvider =
                    () => s_guidance.IsJobStarted ? s_guidance.currentFieldDirectory : "";

                var state = new EngineStateProvider(s_guidance);
                var shape = new EngineShapeService(s_guidance);
                state.Shape = shape;
                var coverage = new EngineCoverageService(s_guidance);
                var guidance = new EngineGuidanceCalculator(s_guidance);
                var toolGeom = new EngineToolGeometryCalculator(s_guidance);
                var tram = new EngineTramCalculator(s_guidance);
                var paths = new EnginePathsCalculator(s_guidance);
                var lotes = new EngineLotesService(s_guidance);
                var trackBuilder = new EngineTrackBuilderService(s_guidance);
                var trackList = new EngineTrackListService(s_guidance);
                var sectionsCore = new EngineSectionControlService(s_guidance);
                var vehicleTool = new EngineVehicleToolService(s_guidance);
                var configVehiculo = new EngineConfigVehiculoService(s_guidance);
                var imuCalibracion = new EngineImuCalibracionService(s_guidance);
                var steerConfig = new AgroParallel.Adapters.SteerConfigService(
                    s_guidance.Vehicle,
                    () => s_guidance.Mc.actualSteerAngleDegrees,
                    () => s_guidance.SettingsSender.SendSettings(),
                    applyLive: null,
                    avgSpeed: () => s_guidance.avgSpeed);

                var pilotxUpdate = new AndroidPilotXUpdateService(dataDir);
                s_pilotxUpdate = pilotxUpdate;

                // ── Productos X-* ──────────────────────────────────────────
                var vistaxCfg = new VistaXConfigService();
                var insumosCat = new InsumoCatalogService();
                var sectionxCfg = new SectionXConfigService();
                var orbitxCfg = new OrbitXConfigService();
                var quantixCfg = new QuantiXConfigService(s_nodos);
                var implemento = new ImplementoService(vistaxCfg, vehicleTool, quantixCfg, sectionxCfg);
                toolGeom.ImplementoProvider = () => implemento.GetImplemento();
                var vistaxLive = new VistaXLiveService(s_nodos, vistaxCfg, insumosCat, state, sectionsCore, implemento, quantixCfg);
                var quantixRuntime = new QuantiXRuntimeService(state,
                    cargarImplemento: () => implemento.GetImplemento());
                var flowxCfg = new FlowXConfigService();
                var flowxLive = new FlowXLiveService(s_nodos, flowxCfg);
                var stormxCfg = new StormXConfigService();
                var stormxLive = new StormXLiveService(s_nodos, stormxCfg);
                var linexCfg = new LineXConfigService();
                var linexLive = new LineXLiveService(s_nodos, linexCfg);

                // Plataforma Android (gemelos de EngineSistemaService / WifiServiceWindows).
                ISistemaService sistema = new AndroidSistemaService(ctx);
                IWifiService wifi = new AndroidWifiService(ctx);

                var host = new AgpWebHost(
                    state,
                    sistema: sistema,
                    wifi: wifi,
                    nodos: s_nodos,
                    orbitxCfg: orbitxCfg,
                    sectionxCfg: sectionxCfg,
                    camarasCfg: new CamarasConfigService(),
                    quantixCfg: quantixCfg,
                    vistaxCfg: vistaxCfg,
                    vistaxLive: vistaxLive,
                    debug: new DebugLogService(),
                    lotes: lotes,
                    perfiles: new EnginePerfilService(s_guidance),
                    flags: new EngineFlagsService(s_guidance),
                    contorno: new EngineContornoService(s_guidance),
                    headlandEdit: new EngineHeadlandEditService(s_guidance),
                    cabeceraLineas: new EngineCabeceraLineasService(s_guidance),
                    tramLine: new EngineTramLineService(s_guidance),
                    quickAb: new EngineQuickAbService(s_guidance),
                    tramSimple: new EngineTramSimpleService(s_guidance),
                    nudge: new EngineNudgeService(s_guidance),
                    recPath: new EngineRecPathService(s_guidance),
                    vehicleTool: vehicleTool,
                    shapefile: shape,
                    coverage: coverage,
                    sectionsCore: sectionsCore,
                    quantixRuntime: quantixRuntime,
                    guidance: guidance,
                    pilotxUpdate: pilotxUpdate,
                    flowxCfg: flowxCfg,
                    flowxLive: flowxLive,
                    stormxCfg: stormxCfg,
                    stormxLive: stormxLive,
                    linexCfg: linexCfg,
                    linexLive: linexLive,
                    wwwroot: wwwroot,
                    port: 5180,
                    insumos: null,          // igual que EngineWebHost
                    toolGeometry: toolGeom,
                    tram: tram,
                    implemento: implemento,
                    paths: paths,
                    trackBuilder: trackBuilder,
                    trackList: trackList,
                    steerConfig: steerConfig,
                    configVehiculo: configVehiculo,
                    imuCalibracion: imuCalibracion);

                // Alarmas sonoras de cabina (el sink de audio lo pone MainActivity).
                try
                {
                    s_sonidos = new SonidosAlarmService(state, s_nodos, vistaxLive);
                    s_sonidos.Start();
                    host.Sonidos = s_sonidos;
                }
                catch (Exception ex) { Android.Util.Log.Warn("PilotX", "SonidosAlarm: " + ex.Message); }

                host.Start();
                s_host = host;

                // ── Procesos de fondo (idénticos a EngineWebHost) ──────────
                try
                {
                    s_flowxBridge = new FlowXBridge(state, FlowXConfig.Load());
                    _ = s_flowxBridge.StartAsync();
                }
                catch (Exception ex) { Android.Util.Log.Warn("PilotX", "FlowXBridge: " + ex.Message); }

                try
                {
                    s_orbitxSync = new AgroParallel.OrbitX.OrbitXSync(state, AgroParallel.OrbitX.OrbitXConfig.Load());
                    s_orbitxSync.ImportarLoteDesdeKml = lotes.CrearLoteDesdeKmlSinAbrir;
                    s_orbitxSync.Start();
                }
                catch (Exception ex) { Android.Util.Log.Warn("PilotX", "OrbitXSync: " + ex.Message); }

                s_quantixRetry = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (s_quantixBridge != null && s_quantixBridge.IsRunning) return;
                        if (AgroParallel.QuantiX.MotoresConfig.Load().Nodos.Count == 0) return;
                        s_quantixBridge = new AgroParallel.QuantiX.QuantiXMotorBridge(state, s_nodos, new PrescripcionService());
                        s_quantixBridge.ImplementoProvider = () => implemento.GetImplemento();
                        _ = s_quantixBridge.StartAsync();
                        Android.Util.Log.Info("PilotX", "QuantiXMotorBridge arrancado: hay nodos configurados.");
                    }
                    catch (Exception ex) { Android.Util.Log.Warn("PilotX", "QuantiX bridge: " + ex.Message); }
                }, null, 2000, 30000);

                try
                {
                    var sxAdapter = new AgroParallel.Cut.SectionXCutAdapter
                    {
                        ImplementoProvider = () => implemento.GetImplemento()
                    };
                    s_cutDispatcher = new AgroParallel.Cut.CutDispatcher(
                        state,
                        new AgroParallel.Cut.ICutAdapter[] { sxAdapter, new AgroParallel.Cut.LineXCutAdapter() });
                    s_sectionsSpeed = new AgroParallel.SectionX.SectionsSpeedPublisher(state);
                }
                catch (Exception ex) { Android.Util.Log.Warn("PilotX", "CutDispatcher: " + ex.Message); }

                s_cutRetry = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (s_cutDispatcher != null)
                        {
                            if (s_cutDispatcher.IsRunning && !s_cutDispatcher.MqttConnected) s_cutDispatcher.Stop();
                            if (!s_cutDispatcher.IsRunning) _ = s_cutDispatcher.StartAsync();
                        }
                        if (s_sectionsSpeed != null && !s_sectionsSpeed.IsRunning) _ = s_sectionsSpeed.StartAsync();
                    }
                    catch (Exception ex) { Android.Util.Log.Warn("PilotX", "CutDispatcher retry: " + ex.Message); }
                }, null, 1000, 15000);

                s_orbitxRetry = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (s_orbitxSync != null && s_orbitxSync.IsRunning) return;
                        var cfg = AgroParallel.OrbitX.OrbitXConfig.Load();
                        if (!cfg.Enabled || string.IsNullOrEmpty(cfg.DeviceToken)) return;
                        try { s_orbitxSync?.Dispose(); } catch { }
                        s_orbitxSync = new AgroParallel.OrbitX.OrbitXSync(state, cfg);
                        s_orbitxSync.ImportarLoteDesdeKml = lotes.CrearLoteDesdeKmlSinAbrir;
                        s_orbitxSync.Start();
                        Android.Util.Log.Info("PilotX", "OrbitXSync (re)arrancado: la vinculación apareció en orbitX.json.");
                    }
                    catch (Exception ex) { Android.Util.Log.Warn("PilotX", "OrbitXSync retry: " + ex.Message); }
                }, null, 30000, 30000);

                // ── NTRIP (RTCM por UDP :2233 a la subred) ─────────────────
                IniciarNtrip(dataDir);

                // ── Comandos de guiado por MQTT (agp/aog/guidance/command) ─
                try { SuscribirComandos(); }
                catch (Exception ex) { Android.Util.Log.Warn("PilotX", "MQTT cmd: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------
        //  NTRIP — misma sección "ntrip" de corex-integrado.json que el panel
        //  CoreX de Windows (CoreXEnginePanel.PanelNtripDto), en el dataDir.
        // ------------------------------------------------------------------
        private sealed class NtripDto
        {
            [JsonPropertyName("is_on")] public bool IsOn { get; set; }
            [JsonPropertyName("caster_url")] public string CasterUrl { get; set; } = "";
            [JsonPropertyName("caster_ip")] public string CasterIp { get; set; } = "";
            [JsonPropertyName("caster_port")] public int CasterPort { get; set; } = 2101;
            [JsonPropertyName("mount")] public string Mount { get; set; } = "";
            [JsonPropertyName("user_name")] public string UserName { get; set; } = "";
            [JsonPropertyName("user_password")] public string UserPassword { get; set; } = "";
            [JsonPropertyName("send_gga_interval")] public int SendGgaInterval { get; set; }
            [JsonPropertyName("is_gga_manual")] public bool IsGgaManual { get; set; }
            [JsonPropertyName("manual_lat")] public double ManualLat { get; set; }
            [JsonPropertyName("manual_lon")] public double ManualLon { get; set; }
            [JsonPropertyName("is_tcp")] public bool IsTcp { get; set; }
            [JsonPropertyName("is_http10")] public bool IsHttp10 { get; set; }
        }

        private sealed class CoreXConfigDto
        {
            public bool NtripOn { get; set; }
            public NtripDto Ntrip { get; set; }
        }

        private static void IniciarNtrip(string dataDir)
        {
            try
            {
                var ruta = Path.Combine(dataDir, "corex-integrado.json");
                if (!File.Exists(ruta)) return;
                var cfg = JsonSerializer.Deserialize<CoreXConfigDto>(File.ReadAllText(ruta),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var d = cfg?.Ntrip;
                if (cfg == null || d == null || !(cfg.NtripOn || d.IsOn)) return;

                string ip = (d.CasterIp ?? "").Trim();
                if (ip.Length == 0 && !string.IsNullOrWhiteSpace(d.CasterUrl))
                {
                    foreach (var a in Dns.GetHostAddresses(d.CasterUrl.Trim()))
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { ip = a.ToString(); break; }
                }
                if (ip.Length == 0) { Android.Util.Log.Warn("PilotX", "NTRIP sin caster (ni IP ni URL)"); return; }

                s_ntrip = new NtripClientService();
                s_ntrip.OnRtcmData += rtcm =>
                {
                    // Rebanado a 256 bytes (buffer NTRIP de la Teensy = 1023 y MTU),
                    // mismo criterio que CoreXEngineHost.ConnectNtrip.
                    const int tam = 256;
                    var bridge = s_lanBridge;
                    if (bridge == null) return;
                    var eps = EndpointsDeModulos(2233);
                    for (int off = 0; off < rtcm.Length; off += tam)
                    {
                        int n = Math.Min(tam, rtcm.Length - off);
                        var pedazo = new byte[n];
                        Array.Copy(rtcm, off, pedazo, 0, n);
                        foreach (var ep in eps) { try { bridge.SendUdpTo(pedazo, ep); } catch { } }
                    }
                };
                var ncfg = new NtripConfig
                {
                    CasterIp = ip,
                    CasterPort = d.CasterPort > 0 ? d.CasterPort : 2101,
                    Mount = d.Mount,
                    Username = d.UserName,
                    Password = d.UserPassword,
                    SendGgaIntervalSec = d.SendGgaInterval,
                    IsHttp10 = d.IsHttp10,
                    IsTcp = d.IsTcp,
                    IsGgaManual = d.IsGgaManual,
                    ManualLat = d.ManualLat,
                    ManualLon = d.ManualLon,
                };
                // Feedback de posición para la GGA: el parser NMEA del bridge si el
                // GPS entra como NMEA; si entra como PGN, la posición del motor.
                s_ntrip.Connect(ncfg, () =>
                {
                    var p = s_nmeaParser;
                    if (p != null && p.latitude != 0)
                        return new NtripGpsData
                        {
                            Latitude = p.latitude, Longitude = p.longitude, Altitude = p.altitudeData,
                            FixQuality = p.fixQualityData, Satellites = p.satellitesData, Hdop = p.hdopData,
                        };
                    // Posición del motor (misma fuente que EngineStateProvider).
                    var g = s_guidance;
                    var ll = g?.AppModelField?.CurrentLatLon;
                    return new NtripGpsData
                    {
                        Latitude = ll?.Latitude ?? 0,
                        Longitude = ll?.Longitude ?? 0,
                        FixQuality = g?.Pn?.fixQuality ?? 0,
                    };
                });
                s_ntripTick = new System.Threading.Timer(_ => { try { s_ntrip?.SecondTick(); } catch { } }, null, 1000, 1000);
                Android.Util.Log.Info("PilotX", "NTRIP conectando a " + ip + ":" + ncfg.CasterPort + " /" + ncfg.Mount + " → RTCM por UDP :2233");
            }
            catch (Exception ex) { Android.Util.Log.Warn("PilotX", "NTRIP: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        //  Comandos de guiado por MQTT — port de CoreXEngineHost.SubscribeCommands.
        // ------------------------------------------------------------------
        private static void SuscribirComandos()
        {
            var factory = new MqttFactory();
            s_cmdClient = factory.CreateMqttClient();
            var opts = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", 1883)
                .WithClientId("PilotX_Android_cmd")
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .Build();
            s_cmdClient.ApplicationMessageReceivedAsync += e =>
            {
                try
                {
                    var seg = e.ApplicationMessage.PayloadSegment;
                    string cmd = Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);
                    bool ok = s_guidance?.ExecuteCommand(cmd) ?? false;
                    Android.Util.Log.Info("PilotX", "MQTT cmd \"" + cmd + "\" -> " + (ok ? "ok" : "unknown"));
                }
                catch (Exception ex) { Android.Util.Log.Warn("PilotX", "MQTT cmd: " + ex.Message); }
                return System.Threading.Tasks.Task.CompletedTask;
            };
            s_cmdClient.ConnectAsync(opts).GetAwaiter().GetResult();
            s_cmdClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter("agp/aog/guidance/command").Build()).GetAwaiter().GetResult();
        }

        public static void Stop()
        {
            lock (s_lock)
            {
                try { s_cmdClient?.Dispose(); } catch { }
                s_cmdClient = null;
                try { s_ntripTick?.Dispose(); } catch { }
                s_ntripTick = null;
                try { s_ntrip?.Disconnect(); s_ntrip?.Dispose(); } catch { }
                s_ntrip = null;
                try { s_helloTimer?.Dispose(); } catch { }
                s_helloTimer = null;

                try { s_flowxBridge?.Stop(); s_flowxBridge?.Dispose(); } catch { }
                s_flowxBridge = null;
                try { s_quantixRetry?.Dispose(); } catch { }
                s_quantixRetry = null;
                try { s_cutRetry?.Dispose(); } catch { }
                s_cutRetry = null;
                try { s_cutDispatcher?.Dispose(); } catch { }
                s_cutDispatcher = null;
                try { s_sectionsSpeed?.Dispose(); } catch { }
                s_sectionsSpeed = null;
                try { s_quantixBridge?.Stop(); } catch { }
                s_quantixBridge = null;
                try { s_orbitxRetry?.Dispose(); } catch { }
                s_orbitxRetry = null;
                try { s_orbitxSync?.Dispose(); } catch { }
                s_orbitxSync = null;
                try { s_sonidos?.Dispose(); } catch { }
                s_sonidos = null;

                try { s_host?.Stop(); } catch { }
                s_host = null;
                try { s_lanBridge?.Stop(); } catch { }
                s_lanBridge = null;
                s_nmeaParser = null;
                try { s_guidance?.Stop(); } catch { }
                s_guidance = null;
                try { s_pilotxUpdate?.Dispose(); } catch { }
                s_pilotxUpdate = null;
                try { s_nodos?.Stop(); } catch { }
                s_nodos = null;
                try { s_broker?.StopAsync().GetAwaiter().GetResult(); } catch { }
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
