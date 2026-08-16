// ============================================================================
// CoreXEnginePanel.cs — el panel web de CoreX (:5181) para el MODO INTEGRADO.
//
// Con CoreX embebido en el motor (--corex) los servicios corren
// (broker/bridge/serial/NTRIP) pero el dashboard vivía solo en CoreX.exe: sus
// controllers reciben FormLoop y el boton CoreX de PilotX abría un WebView
// contra un puerto muerto. Este archivo reimplementa el panel contra
// CoreXEngineHost, sirviendo el MISMO wwwroot-corex y el MISMO wire
// (/api/corex/*, snake_case) que el panel de CoreX.exe — las páginas no
// distinguen contra cuál de los dos hablan.
//
// Tres piezas:
//   · CoreXEngineConfig  — la config persistida del CoreX integrado
//     (corex-integrado.json junto al exe): puertos serie, NTRIP, subred.
//     CoreX.exe guarda en sus Properties.Settings; acá no existen.
//   · CoreXEnginePanel   — web server :5181 (static + API) y el publicador
//     del snapshot @1 Hz hacia CoreXState (el mismo singleton que lee el
//     CoreXStatusController linkeado de CoreX.Shared).
//   · Controllers        — serial/ntrip/red/mqtt implementados de verdad;
//     lo que no aplica al modo integrado (perfiles, radio, pass, avanzado,
//     monitor UDP, reiniciar/apagar) contesta "no-disponible-en-integrado"
//     para que las páginas muestren su error amable en vez de colgarse.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AgLibrary.Logging;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Files;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using System.Text.Json.Serialization;

namespace AgIO
{
    // ── DTOs del wire (mismo shape que CoreXConfigController de CoreX.exe;
    //    ese archivo no es linkeable porque depende de FormLoop) ─────────────
    /// <summary>Mountpoint de la sourcetable del caster (mismo shape que el
    /// MountDto de CoreX.exe; ntrip.js consume mount/identifier/format/
    /// nav_system/country/distance_km vía snake_case de AgpJson).</summary>
    public sealed class PanelMountDto
    {
        public string Mount { get; set; }
        public string Identifier { get; set; }
        public string Format { get; set; }
        public string NavSystem { get; set; }
        public string Country { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double DistanceKm { get; set; }
    }

    public sealed class PanelNtripDto
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
        [JsonPropertyName("packet_size")] public int PacketSize { get; set; }
        [JsonPropertyName("send_to_serial")] public bool SendToSerial { get; set; }
        [JsonPropertyName("send_to_udp")] public bool SendToUdp { get; set; }
        [JsonPropertyName("send_to_udp_port")] public int SendToUdpPort { get; set; }
    }

    internal sealed class PanelSerialOpenRequest
    {
        [JsonPropertyName("channel")] public string Channel { get; set; }
        [JsonPropertyName("port")] public string Port { get; set; }
        [JsonPropertyName("baud")] public int Baud { get; set; }
    }

    internal sealed class PanelSerialCloseRequest
    {
        [JsonPropertyName("channel")] public string Channel { get; set; }
    }

    internal sealed class PanelSubnetRequest
    {
        [JsonPropertyName("o1")] public int O1 { get; set; }
        [JsonPropertyName("o2")] public int O2 { get; set; }
        [JsonPropertyName("o3")] public int O3 { get; set; }
    }

    // ------------------------------------------------------------------------
    // Config persistida del CoreX integrado
    // ------------------------------------------------------------------------
    public sealed class CoreXSerialChannelConfig
    {
        public string Port { get; set; } = "";
        public int Baud { get; set; }
        public bool Enabled { get; set; }
    }

    public sealed class CoreXEngineConfig
    {
        public CoreXSerialChannelConfig Gps { get; set; } = new CoreXSerialChannelConfig { Baud = 115200 };
        public CoreXSerialChannelConfig Gps2 { get; set; } = new CoreXSerialChannelConfig { Baud = 115200 };
        public CoreXSerialChannelConfig Rtcm { get; set; } = new CoreXSerialChannelConfig { Baud = 115200 };
        public CoreXSerialChannelConfig Imu { get; set; } = new CoreXSerialChannelConfig { Baud = 38400 };
        public CoreXSerialChannelConfig Steer { get; set; } = new CoreXSerialChannelConfig { Baud = 38400 };
        public CoreXSerialChannelConfig Machine { get; set; } = new CoreXSerialChannelConfig { Baud = 38400 };

        public bool NtripOn { get; set; }
        public PanelNtripDto Ntrip { get; set; } = new PanelNtripDto();

        /// <summary>Subred donde viven los módulos LAN (broadcast del bridge).</summary>
        public int[] Subnet { get; set; } = { 192, 168, 5 };

        private static string RutaArchivo =>
            Path.Combine(AppContext.BaseDirectory, "corex-integrado.json");

        public static CoreXEngineConfig Load()
        {
            try
            {
                var cfg = AgroParallel.Common.AtomicJson.Read<CoreXEngineConfig>(RutaArchivo, AgpJsonOpts());
                return cfg ?? new CoreXEngineConfig();
            }
            catch (Exception ex)
            {
                Log.EventWriter("CoreXPanel: config ilegible, arranco con defaults: " + ex.Message);
                return new CoreXEngineConfig();
            }
        }

        public void Save()
        {
            try
            {
                AgroParallel.Common.AtomicJson.Write(RutaArchivo,
                    System.Text.Json.JsonSerializer.Serialize(this, AgpJsonOpts()));
            }
            catch (Exception ex)
            {
                Log.EventWriter("CoreXPanel: no pude guardar la config: " + ex.Message);
            }
        }

        private static System.Text.Json.JsonSerializerOptions AgpJsonOpts() =>
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
            };
    }

    // ------------------------------------------------------------------------
    // Panel: server + snapshot
    // ------------------------------------------------------------------------
    public sealed class CoreXEnginePanel : IDisposable
    {
        public const int Port = 5181;

        private readonly CoreXEngineHost _corex;
        private WebServer _server;
        private System.Threading.Timer _tick;

        public CoreXEngineConfig Config { get; private set; }

        public CoreXEnginePanel(CoreXEngineHost corex)
        {
            _corex = corex;
            Config = CoreXEngineConfig.Load();
        }

        /// <summary>Aplica la config guardada: abre los puertos habilitados y
        /// conecta NTRIP si estaba prendido. Se llama una vez al arrancar.</summary>
        public void ApplyConfig()
        {
            AbrirCanal("gps", Config.Gps);
            AbrirCanal("gps2", Config.Gps2);
            AbrirCanal("rtcm", Config.Rtcm);
            AbrirCanal("imu", Config.Imu);
            AbrirCanal("steer", Config.Steer);
            AbrirCanal("machine", Config.Machine);

            if (Config.NtripOn && NtripTieneCaster())
                ConectarNtrip();
        }

        public bool AbrirCanal(string canal, CoreXSerialChannelConfig c)
        {
            if (c == null || string.IsNullOrEmpty(c.Port)) return false;
            var sp = PuertoDe(canal);
            if (sp == null) return false;

            switch (canal)
            {
                case "gps": _corex.OpenGPSPort(c.Port, c.Baud); break;
                case "imu": _corex.OpenIMUPort(c.Port, c.Baud); break;
                case "steer": _corex.OpenSteerModulePort(c.Port, c.Baud); break;
                case "machine": _corex.OpenMachineModulePort(c.Port, c.Baud); break;
                default:
                    // gps2 y rtcm no tienen apertura especializada en el host.
                    try { sp.Open(c.Port, c.Baud); }
                    catch (Exception ex) { Log.EventWriter("CoreXPanel: " + canal + " " + c.Port + ": " + ex.Message); }
                    break;
            }
            return sp.IsOpen;
        }

        public ISerialPortService PuertoDe(string canal) => canal switch
        {
            "gps" => _corex.SpGPS,
            "gps2" => _corex.SpGPS2,
            "rtcm" => _corex.SpRtcm,
            "imu" => _corex.SpIMU,
            "steer" => _corex.SpSteerModule,
            "machine" => _corex.SpMachineModule,
            _ => null,
        };

        public CoreXSerialChannelConfig CanalDe(string canal) => canal switch
        {
            "gps" => Config.Gps,
            "gps2" => Config.Gps2,
            "rtcm" => Config.Rtcm,
            "imu" => Config.Imu,
            "steer" => Config.Steer,
            "machine" => Config.Machine,
            _ => null,
        };

        /// <summary>¿Hay caster cargado (IP o URL)? Los guards de conexión usan
        /// esto: antes pedían CasterIp y un caster cargado por hostname quedaba
        /// "esperando" para siempre sin intentar conectar (banco 2026-08-12,
        /// IGN por ntrip.ign.gob.ar).</summary>
        public bool NtripTieneCaster() =>
            !string.IsNullOrWhiteSpace(Config.Ntrip?.CasterIp)
            || !string.IsNullOrWhiteSpace(Config.Ntrip?.CasterUrl);

        public void ConectarNtrip()
        {
            var d = Config.Ntrip;

            // La página guarda lo que el operario tipeó: IP directa o hostname.
            // El cliente quiere IP: resolver acá, con log si falla (sin esto el
            // fallo era mudo y el estado quedaba en "esperando").
            string ip = (d.CasterIp ?? "").Trim();
            if (ip.Length == 0 && !string.IsNullOrWhiteSpace(d.CasterUrl))
            {
                try
                {
                    var addrs = System.Net.Dns.GetHostAddresses(d.CasterUrl.Trim());
                    ip = addrs.FirstOrDefault(a =>
                        a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "";
                }
                catch (Exception ex)
                {
                    Log.EventWriter("CoreXEngine: NTRIP no pudo resolver '" + d.CasterUrl + "': " + ex.Message);
                    return;
                }
            }
            if (ip.Length == 0)
            {
                Log.EventWriter("CoreXEngine: NTRIP sin caster (ni IP ni URL) — no se conecta");
                return;
            }

            Log.EventWriter("CoreXEngine: NTRIP conectando a " + ip + ":" +
                (d.CasterPort > 0 ? d.CasterPort : 2101) + " /" + d.Mount);

            var cfg = new NtripConfig
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
            // El feedback de posición para la GGA sale del parser NMEA vivo.
            _corex.ConnectNtrip(cfg, () => new NtripGpsData
            {
                Latitude = _corex.Nmea.latitude,
                Longitude = _corex.Nmea.longitude,
                Altitude = _corex.Nmea.altitudeData,
                FixQuality = _corex.Nmea.fixQualityData,
                Satellites = _corex.Nmea.satellitesData,
                Hdop = _corex.Nmea.hdopData,
            });
        }

        public void Start()
        {
            if (_server != null) return;

            string wwwroot = ResolveWwwrootCorex();
            try
            {
                _server = new WebServer(o => o
                        .WithUrlPrefix("http://127.0.0.1:" + Port + "/")
                        .WithMode(HttpListenerMode.EmbedIO))
                    .WithWebApi("/api", m =>
                    {
                        m.WithController(() => new CoreXStatusController());
                        m.WithController(() => new EnginePanelConfigController(this, _corex));
                        m.WithController(() => new EnginePanelCommandController(this, _corex));
                        m.WithController(() => new EnginePanelDiagController(_corex));
                        m.WithController(() => new EnginePanelNoDisponibleController());
                    });

                if (wwwroot != null)
                    _server = _server.WithStaticFolder("/", wwwroot, false,
                        m => m.WithContentCaching(false));

                _ = _server.RunAsync();
                Log.EventWriter("CoreXPanel: dashboard integrado en http://127.0.0.1:" + Port
                    + (wwwroot != null ? " (wwwroot-corex: " + wwwroot + ")" : " (SIN wwwroot-corex: solo API)"));
            }
            catch (Exception ex)
            {
                // Si :5181 está tomado (CoreX.exe corriendo a la vez), el panel
                // del exe gana y este no levanta: mejor que pelearse el puerto.
                Log.EventWriter("CoreXPanel: no pude levantar :5181 — " + ex.Message);
                _server = null;
                return;
            }

            // Snapshot @1 Hz para /api/corex/status + SecondTick del NTRIP
            // (reconexión y GGA periódica viven ahí).
            _tick = new System.Threading.Timer(_ =>
            {
                try
                {
                    _corex.Ntrip.SecondTick();
                    CoreXState.Instance.Publish(ArmarSnapshot());
                }
                catch { /* el latido no puede morir por un snapshot fallado */ }
            }, null, 1000, 1000);
        }

        /// <summary>Inventario NMEA para la checklist del panel GPS: toda
        /// sentencia válida vista, con su edad — el operario "tilda" qué manda
        /// el receptor (GGA sí, RMC no, etc.) sin adivinar.</summary>
        private static System.Collections.Generic.List<CoreXNmeaVistaDto> ArmarInventarioNmea(CNmeaParser n)
        {
            var lista = new System.Collections.Generic.List<CoreXNmeaVistaDto>();
            var ahora = DateTime.UtcNow;
            foreach (var kv in n.SentenciasVistas)
            {
                lista.Add(new CoreXNmeaVistaDto
                {
                    Tipo = kv.Key,
                    EdadSec = Math.Round((ahora - kv.Value.Item2).TotalSeconds, 1),
                    Cruda = kv.Value.Item1
                });
            }
            lista.Sort((a, b) => string.Compare(a.Tipo, b.Tipo, StringComparison.Ordinal));
            return lista;
        }

        private CoreXStatusDto ArmarSnapshot()
        {
            var n = _corex.Nmea;
            var broker = _corex.MqttBroker as MqttBrokerService;

            var dto = new CoreXStatusDto
            {
                Ok = true,
                Version = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
                Profile = "integrado",
                Gps = new CoreXGpsDto
                {
                    Alive = _corex.NmeaAliveSec >= 0 && _corex.NmeaAliveSec < 3,
                    Latitude = n.latitude,
                    Longitude = n.longitude,
                    FixQuality = n.FixQuality,
                    Sats = n.satellitesData,
                    Hdop = Math.Round(n.hdopData, 2),
                    SpeedKmh = Math.Round(n.speedData, 2),
                    AltitudeM = Math.Round(n.altitudeData, 2),
                    AgeSec = Math.Round(n.ageData, 1),
                    RollDeg = Math.Round(n.rollData, 2),
                    HeadingTrue = Math.Round(n.headingTrueData, 2),
                    HeadingDual = Math.Round(n.headingTrueDualData, 2),
                    ImuHeading = n.imuHeadingData,
                    ImuRoll = n.imuRollData,
                    ImuPitch = n.imuPitchData,
                    ImuYawRate = n.imuYawRateData,
                    Nmea = new CoreXNmeaDto
                    {
                        Gga = n.ggaSentence ?? "",
                        Vtg = n.vtgSentence ?? "",
                        Panda = n.pandaSentence ?? "",
                        Paogi = n.paogiSentence ?? "",
                        Hdt = n.hdtSentence ?? "",
                        Avr = n.avrSentence ?? "",
                        Hpd = n.hpdSentence ?? "",
                        Ksxt = n.ksxtSentence ?? "",
                        Vistas = ArmarInventarioNmea(n),
                    },
                },
                Ntrip = new CoreXNtripDto
                {
                    RequiredOn = Config.NtripOn,
                    Connected = _corex.Ntrip.IsConnected,
                    Connecting = _corex.Ntrip.IsConnecting,
                    KbTotal = _corex.Ntrip.TotalBytes / 1024,
                    CasterIp = _corex.Ntrip.CasterIp ?? "",
                    RtcmTypes = _corex.Ntrip.RtcmTypesSnapshot(),
                },
                Mqtt = new CoreXMqttDto
                {
                    Running = broker?.IsRunning ?? true,
                    Port = broker?.Port ?? 1883,
                    Clients = broker?.ClientsConnected ?? 0,
                    Messages = broker?.MessagesTotal ?? 0,
                    RecentTopics = broker?.GetRecentTopics(20) ?? new System.Collections.Generic.List<string>(),
                },
                // Vivo = hello en los últimos 5 s (los módulos lo mandan a 1 Hz
                // en respuesta al PGN 200). "Configured" en CoreX.exe sale de
                // sus canales serie; en integrado el canal es la LAN, así que
                // configurado == vivo.
                Modules = ArmarModulos(),
            };
            return dto;
        }

        private CoreXModulesDto ArmarModulos()
        {
            var ahora = DateTime.UtcNow;
            bool steer = (ahora - _corex.LastSteerHelloUtc).TotalSeconds < 5;
            bool machine = (ahora - _corex.LastMachineHelloUtc).TotalSeconds < 5;
            bool imu = (ahora - _corex.LastImuHelloUtc).TotalSeconds < 5;
            return new CoreXModulesDto
            {
                SteerConfigured = steer,
                SteerHello = steer,
                MachineConfigured = machine,
                MachineHello = machine,
                ImuConfigured = imu,
                ImuHello = imu,
            };
        }

        private static string ResolveWwwrootCorex()
        {
            var baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                // Junto al exe: el csproj lo copia como Content (bin en dev,
                // Engine\wwwroot-corex en el publish) — SIEMPRE el más nuevo.
                Path.GetFullPath(Path.Combine(baseDir, "wwwroot-corex")),
                // Fallback instalaciones viejas: Build\wwwroot-corex hermano
                // (lo dejaba build.ps1 cuando el dashboard era de CoreX.exe).
                Path.GetFullPath(Path.Combine(baseDir, "..", "wwwroot-corex")),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) return c;
            return null;
        }

        public void Dispose()
        {
            try { _tick?.Dispose(); } catch { }
            try { _server?.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------------------
    // Config: serial / ntrip / red — implementación real contra el host
    // ------------------------------------------------------------------------
    public sealed class EnginePanelConfigController : AgpControllerBase
    {
        private readonly CoreXEnginePanel _panel;
        private readonly CoreXEngineHost _corex;

        public EnginePanelConfigController(CoreXEnginePanel panel, CoreXEngineHost corex)
        {
            _panel = panel;
            _corex = corex;
        }

        // ── Simulador NMEA de SALIDA por RS232 ──────────────────────────────
        // Emite GGA/VTG/RMC/HDT con valores y frecuencia configurables — la
        // contracara del receptor, para probar entradas serie (esta PC u otro
        // equipo por cable). Instancia única por proceso.
        private static readonly NmeaSimulador _nmeaSim = new NmeaSimulador();

        [Route(HttpVerbs.Get, "/corex/nmea-sim")]
        public Task GetNmeaSim() => WriteJsonAsync(new { Ok = true, Estado = _nmeaSim.Estado() });

        [Route(HttpVerbs.Post, "/corex/nmea-sim/start")]
        public async Task StartNmeaSim()
        {
            NmeaSimuladorConfig cfg;
            try { cfg = await ReadJsonBodyAsync<NmeaSimuladorConfig>().ConfigureAwait(false); }
            catch { await WriteErrorAsync(400, "BAD_REQUEST", "JSON inválido").ConfigureAwait(false); return; }
            bool ok = _nmeaSim.Start(cfg);
            await WriteJsonAsync(new { Ok = ok, Estado = _nmeaSim.Estado() }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex/nmea-sim/stop")]
        public Task StopNmeaSim()
        {
            _nmeaSim.Stop();
            return WriteJsonAsync(new { Ok = true, Estado = _nmeaSim.Estado() });
        }

        [Route(HttpVerbs.Get, "/corex/config/serial")]
        public Task GetSerial()
        {
            object Canal(string nombre)
            {
                var c = _panel.CanalDe(nombre);
                var sp = _panel.PuertoDe(nombre);
                return new { Port = c.Port, Baud = c.Baud, IsOpen = sp.IsOpen };
            }

            return WriteJsonAsync(new
            {
                Ok = true,
                PortsAvailable = _corex.SpGPS.GetAvailablePorts(),
                Channels = new
                {
                    Gps = Canal("gps"),
                    Gps2 = Canal("gps2"),
                    Rtcm = Canal("rtcm"),
                    Imu = Canal("imu"),
                    Steer = Canal("steer"),
                    Machine = Canal("machine"),
                },
            });
        }

        [Route(HttpVerbs.Post, "/corex/serial/open")]
        public async Task OpenSerial()
        {
            var req = await ReadJsonBodyAsync<PanelSerialOpenRequest>().ConfigureAwait(false);
            if (req == null || string.IsNullOrEmpty(req.Channel))
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Falta channel, port o baud").ConfigureAwait(false);
                return;
            }

            var canal = _panel.CanalDe(req.Channel);
            if (canal == null)
            {
                await WriteErrorAsync(400, "BAD_CHANNEL", "Canal desconocido: " + req.Channel).ConfigureAwait(false);
                return;
            }

            canal.Port = req.Port ?? "";
            canal.Baud = req.Baud;
            bool opened = _panel.AbrirCanal(req.Channel, canal);
            canal.Enabled = opened;
            _panel.Config.Save();

            await WriteJsonAsync(new { Ok = opened }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex/serial/close")]
        public async Task CloseSerial()
        {
            var req = await ReadJsonBodyAsync<PanelSerialCloseRequest>().ConfigureAwait(false);
            if (req == null || string.IsNullOrEmpty(req.Channel))
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Falta channel").ConfigureAwait(false);
                return;
            }

            var sp = _panel.PuertoDe(req.Channel);
            var canal = _panel.CanalDe(req.Channel);
            if (sp == null)
            {
                await WriteErrorAsync(400, "BAD_CHANNEL", "Canal desconocido: " + req.Channel).ConfigureAwait(false);
                return;
            }

            try { sp.Close(); } catch { }
            canal.Enabled = false;
            _panel.Config.Save();

            await WriteJsonAsync(new { Ok = true }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/corex/config/ntrip")]
        public Task GetNtrip()
        {
            var d = _panel.Config.Ntrip ?? new PanelNtripDto();
            d.IsOn = _panel.Config.NtripOn;
            return WriteJsonAsync(d);
        }

        [Route(HttpVerbs.Post, "/corex/config/ntrip")]
        public async Task PostNtrip()
        {
            var dto = await ReadJsonBodyAsync<PanelNtripDto>().ConfigureAwait(false);
            if (dto == null)
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Body requerido").ConfigureAwait(false);
                return;
            }

            _panel.Config.Ntrip = dto;
            _panel.Config.NtripOn = dto.IsOn;
            _panel.Config.Save();

            // En integrado no hace falta reiniciar el proceso: se reconecta acá.
            _corex.Ntrip.Disconnect();
            if (dto.IsOn && (!string.IsNullOrWhiteSpace(dto.CasterIp)
                || !string.IsNullOrWhiteSpace(dto.CasterUrl))) _panel.ConectarNtrip();

            await WriteJsonAsync(new { Ok = true, Restart = false }).ConfigureAwait(false);
        }

        // ── GET /api/corex/ntrip/mounts?ip=..&port=.. ─────────────────────────
        // Port 1:1 del CoreXConfigController de CoreX.exe (que no es linkeable
        // por depender de FormLoop): baja la sourcetable del caster, filtra STR
        // y devuelve los mountpoints ordenados por distancia a la posición
        // actual. Era un stub "no-disponible-en-integrado" y la página NTRIP
        // del Hub no listaba nada (reporte 2026-08-12).
        [Route(HttpVerbs.Get, "/corex/ntrip/mounts")]
        public async Task GetNtripMounts()
        {
            string ipStr = HttpContext.Request.QueryString["ip"];
            string portStr = HttpContext.Request.QueryString["port"];

            if (string.IsNullOrWhiteSpace(ipStr) || !int.TryParse(portStr, out int port)
                || port < 1 || port > 65535)
            {
                await WriteErrorAsync(400, "BAD_REQUEST",
                    "Falta la IP/URL o el puerto del caster.").ConfigureAwait(false);
                return;
            }

            // Resolver hostname → IPv4 si no es una IP literal.
            IPAddress casterIp;
            if (!IPAddress.TryParse(ipStr.Trim(), out casterIp))
            {
                try
                {
                    var addrs = await System.Net.Dns.GetHostAddressesAsync(ipStr.Trim())
                        .ConfigureAwait(false);
                    casterIp = addrs.FirstOrDefault(a =>
                        a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                }
                catch { casterIp = null; }

                if (casterIp == null)
                {
                    await WriteErrorAsync(400, "DNS",
                        "No se pudo resolver la dirección del caster.").ConfigureAwait(false);
                    return;
                }
            }

            string page;
            try
            {
                page = await FetchSourceTableAsync(casterIp, port).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(502, "CASTER",
                    "No se pudo conectar al caster (revisá IP y puerto).", ex.Message)
                    .ConfigureAwait(false);
                return;
            }

            // Posición actual para la distancia (0,0 = sin fix → sin distancia).
            var gps = CoreXState.Instance.Snapshot().Gps;
            double lat = gps?.Latitude ?? 0, lon = gps?.Longitude ?? 0;
            bool hayFix = lat != 0 || lon != 0;

            var mounts = new List<PanelMountDto>();
            foreach (var line in page.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split(';');
                if (f.Length < 11 || f[0] != "STR") continue;

                double.TryParse(f[9].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double mLat);
                double.TryParse(f[10].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double mLon);

                double dist = -1;
                if (hayFix && (mLat != 0 || mLon != 0))
                    dist = glm.DistanceLonLat(mLon, mLat, lon, lat);

                mounts.Add(new PanelMountDto
                {
                    Mount = f[1].Trim(),
                    Identifier = f[2].Trim(),
                    Format = f[3].Trim(),
                    NavSystem = f[6].Trim(),
                    Country = f[8].Trim(),
                    Lat = mLat,
                    Lon = mLon,
                    DistanceKm = dist,
                });
            }

            // Más cercano primero; sin distancia al final, por nombre.
            mounts = mounts
                .OrderBy(m => m.DistanceKm < 0 ? double.MaxValue : m.DistanceKm)
                .ThenBy(m => m.Mount, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await WriteJsonAsync(new { Ok = true, Count = mounts.Count, Mounts = mounts })
                .ConfigureAwait(false);
        }

        // Baja la sourcetable cruda con timeouts (6 s conexión, 10 s total,
        // tope 2 MB). Mismo request que CoreX.exe y que el form viejo.
        private static async Task<string> FetchSourceTableAsync(IPAddress ip, int port)
        {
            using (var client = new System.Net.Sockets.TcpClient())
            using (var cts = new System.Threading.CancellationTokenSource(10000))
            {
                var connect = client.ConnectAsync(ip, port);
                if (await Task.WhenAny(connect, Task.Delay(6000)).ConfigureAwait(false) != connect)
                    throw new TimeoutException("Timeout de conexión al caster.");
                await connect.ConfigureAwait(false); // rethrow si falló

                var stream = client.GetStream();
                byte[] req = Encoding.ASCII.GetBytes(
                    "GET / HTTP/1.0\r\nUser-Agent: NTRIP CoreX\r\nAccept: */*\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(req, 0, req.Length, cts.Token).ConfigureAwait(false);

                var sb = new StringBuilder();
                byte[] buf = new byte[4096];
                int n;
                while ((n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false)) > 0)
                {
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (sb.Length > 2 * 1024 * 1024) break;
                }
                return sb.ToString();
            }
        }

        [Route(HttpVerbs.Get, "/corex/config/red")]
        public Task GetRed()
        {
            string ipLocal = "";
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(a.Address))
                        { ipLocal = a.Address.ToString(); break; }
                    if (ipLocal != "") break;
                }
            }
            catch { }

            return WriteJsonAsync(new
            {
                // El bridge UDP del integrado está siempre arriba.
                UdpIsOn = true,
                Subnet = _panel.Config.Subnet,
                IpActual = ipLocal,
                // En integrado PilotX ES este proceso: loopback fijo.
                PilotxIp = new[] { 127, 0, 0, 1 },
            });
        }

        [Route(HttpVerbs.Post, "/corex/config/red/subnet")]
        public async Task PostRedSubnet()
        {
            var req = await ReadJsonBodyAsync<PanelSubnetRequest>().ConfigureAwait(false);
            if (req == null)
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Body requerido").ConfigureAwait(false);
                return;
            }
            if (req.O1 < 0 || req.O1 > 255 || req.O2 < 0 || req.O2 > 255 || req.O3 < 0 || req.O3 > 255)
            {
                await WriteErrorAsync(400, "BAD_REQUEST", "Octetos fuera de rango 0-255").ConfigureAwait(false);
                return;
            }

            _panel.Config.Subnet = new[] { req.O1, req.O2, req.O3 };
            _panel.Config.Save();

            // Broadcast de subred a los módulos (PGN 201), igual que el nativo.
            byte[] pgn =
            {
                0x80, 0x81, 0x7F, 201, 5,
                201, 201, (byte)req.O1, (byte)req.O2, (byte)req.O3, 0,
            };
            _corex.UdpBridge.SendUdpTo(pgn, _corex.EpModule);

            await WriteJsonAsync(new { Ok = true, Restart = false }).ConfigureAwait(false);
        }

        // UDP on/off y la IP de PilotX no aplican: el bridge del integrado va
        // siempre prendido y "PilotX" es este mismo proceso.
        [Route(HttpVerbs.Post, "/corex/config/red/udp")]
        public Task PostRedUdp() => NoDisponible();

        [Route(HttpVerbs.Post, "/corex/config/red/pilotx")]
        public Task PostRedPilotx() => NoDisponible();

        private Task NoDisponible() =>
            WriteJsonAsync(new { ok = false, error = "no-disponible-en-integrado" });
    }

    // ------------------------------------------------------------------------
    // Comandos: mqtt / ntrip toggle
    // ------------------------------------------------------------------------
    public sealed class EnginePanelCommandController : AgpControllerBase
    {
        private readonly CoreXEnginePanel _panel;
        private readonly CoreXEngineHost _corex;

        public EnginePanelCommandController(CoreXEnginePanel panel, CoreXEngineHost corex)
        {
            _panel = panel;
            _corex = corex;
        }

        [Route(HttpVerbs.Post, "/corex/mqtt/toggle")]
        public async Task ToggleMqtt()
        {
            var broker = _corex.MqttBroker as MqttBrokerService;
            if (broker == null) { await WriteJsonAsync(new { ok = false }).ConfigureAwait(false); return; }

            if (broker.IsRunning) await broker.StopAsync().ConfigureAwait(false);
            else await broker.StartAsync(1883).ConfigureAwait(false);

            await WriteJsonAsync(new { Ok = true }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex/ntrip/toggle")]
        public Task ToggleNtrip()
        {
            if (_corex.Ntrip.IsConnected || _corex.Ntrip.IsConnecting)
            {
                _corex.Ntrip.Disconnect();
                _panel.Config.NtripOn = false;
            }
            else
            {
                _panel.Config.NtripOn = true;
                if (_panel.NtripTieneCaster()) _panel.ConectarNtrip();
            }
            _panel.Config.Save();
            return WriteJsonAsync(new { Ok = true });
        }

        // Reiniciar/apagar el proceso desde acá apagaría también el motor de
        // guiado: en integrado eso se maneja desde PilotX, no desde este panel.
        [Route(HttpVerbs.Post, "/corex/reiniciar")]
        public Task Reiniciar() => WriteJsonAsync(new { ok = false, error = "no-disponible-en-integrado" });

        [Route(HttpVerbs.Post, "/corex/apagar")]
        public Task Apagar() => WriteJsonAsync(new { ok = false, error = "no-disponible-en-integrado" });
    }

    // ------------------------------------------------------------------------
    // Diagnóstico: gps + eventos
    // ------------------------------------------------------------------------
    public sealed class EnginePanelDiagController : AgpControllerBase
    {
        private readonly CoreXEngineHost _corex;

        public EnginePanelDiagController(CoreXEngineHost corex) { _corex = corex; }

        // El detalle GPS ya viaja completo dentro del status (gps.nmea incluido):
        // se contesta el mismo snapshot para que la página GPS ande sin otro wire.
        [Route(HttpVerbs.Get, "/corex/gps")]
        public Task Gps() => WriteJsonAsync(CoreXState.Instance.Snapshot());

        [Route(HttpVerbs.Get, "/corex/eventos")]
        public Task Eventos()
        {
            try
            {
                // El log real del proceso (AgLibrary). Últimas ~200 líneas.
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgOpenGPS", "Logs");
                string archivo = Path.Combine(dir, "AgOpenGPS_Events_Log.txt");
                if (!File.Exists(archivo))
                    return WriteJsonAsync(new { Ok = true, Lines = Array.Empty<string>() });

                string[] todas;
                using (var fs = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    todas = sr.ReadToEnd().Split('\n');

                int desde = Math.Max(0, todas.Length - 200);
                var tail = new string[todas.Length - desde];
                Array.Copy(todas, desde, tail, 0, tail.Length);
                return WriteJsonAsync(new { Ok = true, Lines = tail });
            }
            catch (Exception ex)
            {
                return WriteJsonAsync(new { Ok = false, Error = ex.Message });
            }
        }

        [Route(HttpVerbs.Get, "/corex/monitor/gps")]
        public Task MonitorGps() => WriteJsonAsync(CoreXState.Instance.Snapshot());
    }

    // ------------------------------------------------------------------------
    // Lo que el modo integrado todavía no tiene: respuesta honesta, no 404.
    // ------------------------------------------------------------------------
    public sealed class EnginePanelNoDisponibleController : AgpControllerBase
    {
        private Task No() => WriteJsonAsync(new { ok = false, error = "no-disponible-en-integrado" });

        [Route(HttpVerbs.Get, "/corex/config/perfiles")] public Task A() => No();
        [Route(HttpVerbs.Post, "/corex/perfil/guardar")] public Task B() => No();
        [Route(HttpVerbs.Post, "/corex/perfil/cargar")] public Task C() => No();
        [Route(HttpVerbs.Post, "/corex/perfil/crear")] public Task D() => No();
        [Route(HttpVerbs.Get, "/corex/config/radio")] public Task E() => No();
        [Route(HttpVerbs.Post, "/corex/config/radio")] public Task F() => No();
        [Route(HttpVerbs.Post, "/corex/radio/comando")] public Task G() => No();
        [Route(HttpVerbs.Get, "/corex/config/pass")] public Task H() => No();
        [Route(HttpVerbs.Post, "/corex/config/pass")] public Task I() => No();
        [Route(HttpVerbs.Get, "/corex/config/avanzado")] public Task J() => No();
        [Route(HttpVerbs.Post, "/corex/config/avanzado")] public Task K() => No();
        [Route(HttpVerbs.Get, "/corex/config/modulos")] public Task L() => No();
        [Route(HttpVerbs.Post, "/corex/config/modulos")] public Task M() => No();
        [Route(HttpVerbs.Get, "/corex/monitor/udp")] public Task O() => No();
        [Route(HttpVerbs.Post, "/corex/monitor/udp/flags")] public Task P() => No();
    }
}
