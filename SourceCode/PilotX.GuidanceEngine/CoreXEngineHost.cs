// ============================================================================
// CoreXEngineHost.cs — lado "CoreX" del proceso único (bloque 14). Reemplaza
// a AgIO/FormLoop.cs + SerialComm.Designer.cs + UDP.designer.cs: dueño de los
// 6 puertos serie reales (GPS/GPS2/RTCM/IMU/Steer/Machine), el parser NMEA
// (CNmeaParser, ya portable), el framing PGN de los módulos serie
// (PgnFrameParser, ya portable) y los 3 servicios de CoreX (MqttBrokerService/
// UdpBridgeService/NtripClientService, ya portables desde bloque 8).
//
// Mismo protocolo loopback que el AgIO real: escucha en :17777, contesta a
// :15555 — así convive en el mismo proceso que GuidanceEngineHost (que
// escucha :15555 y contesta a :17777) sin que ninguno de los dos necesite
// saber que el otro ya no es un .exe aparte.
// ============================================================================

using System;
using System.Net;
using System.Text;
using AgLibrary.Logging;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using PilotX.GuidanceEngine;

namespace AgIO
{
    public sealed class CoreXEngineHost : INmeaParserHost, IDisposable
    {
        public readonly ISerialPortService SpGPS = new Net9SerialPortService();
        public readonly ISerialPortService SpGPS2 = new Net9SerialPortService();
        public readonly ISerialPortService SpRtcm = new Net9SerialPortService();
        public readonly ISerialPortService SpIMU = new Net9SerialPortService();
        public readonly ISerialPortService SpSteerModule = new Net9SerialPortService();
        public readonly ISerialPortService SpMachineModule = new Net9SerialPortService();

        private readonly PgnFrameParser _pgnParserSteer = new PgnFrameParser();
        private readonly PgnFrameParser _pgnParserMachine = new PgnFrameParser();
        private readonly PgnFrameParser _pgnParserIMU = new PgnFrameParser();

        public readonly CNmeaParser Nmea;
        public readonly IMqttBrokerService MqttBroker = new MqttBrokerService();
        public readonly IUdpBridgeService UdpBridge = new UdpBridgeService();
        public readonly INtripClientService Ntrip = new NtripClientService();

        // OJO: 255.255.255.255 (broadcast LIMITADO) acá era una trampa: Windows
        // lo despacha por UNA sola interfaz elegida por la tabla de rutas, y en
        // una PC con adaptadores virtuales (QEMU/emulador, VPN) puede salir por
        // el equivocado — los módulos de la LAN no reciben NADA, sin ningún
        // error. AgIO nativo siempre usó broadcast DE SUBRED (x.y.z.255), que
        // se enruta por la interfaz correcta; por eso "el otro andaba". Este
        // endpoint queda de fallback si no se puede enumerar ninguna interfaz.
        public IPEndPoint EpModule = new IPEndPoint(IPAddress.Parse("255.255.255.255"), 8888);

        /// <summary>
        /// Endpoints de módulos: el broadcast dirigido (ip | ~máscara) de CADA
        /// interfaz IPv4 real y activa. Multi-NIC seguro: se manda a todas.
        /// Se recalcula por llamada — es barato (~µs) y la LAN del tractor
        /// puede cambiar en caliente (WiFi que se cae, cable que entra).
        /// </summary>
        private System.Collections.Generic.List<IPEndPoint> EndpointsDeModulos()
        {
            var eps = new System.Collections.Generic.List<IPEndPoint>();
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
                        var mask = ua.IPv4Mask?.GetAddressBytes();
                        if (mask == null) continue;
                        // 169.254.x.x (link-local sin DHCP) no lleva módulos.
                        if (ip[0] == 169 && ip[1] == 254) continue;
                        var bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        eps.Add(new IPEndPoint(new IPAddress(bc), 8888));
                    }
                }
            }
            catch { }
            if (eps.Count == 0) eps.Add(EpModule);   // fallback: mejor limitado que nada
            return eps;
        }

        /// <summary>Manda un PGN a los módulos por TODAS las subredes reales.</summary>
        private void EnviarAModulos(byte[] data)
        {
            foreach (var ep in EndpointsDeModulos())
                UdpBridge.SendUdpTo(data, ep);
        }

        private IMqttClient _cmdClient;
        private int _mqttPort;

        public CoreXEngineHost()
        {
            Nmea = new CNmeaParser(this);

            _pgnParserSteer.OnFrame += frame => UdpBridge.SendToLoopback(frame);
            _pgnParserMachine.OnFrame += frame => UdpBridge.SendToLoopback(frame);
            _pgnParserIMU.OnFrame += frame => UdpBridge.SendToLoopback(frame);

            SpIMU.OnDataReceived += bytes => ProcessPgnBytes(bytes, _pgnParserIMU);
            SpSteerModule.OnDataReceived += bytes => ProcessPgnBytes(bytes, _pgnParserSteer);
            SpMachineModule.OnDataReceived += bytes => ProcessPgnBytes(bytes, _pgnParserMachine);
            // RS232: los chunks llegan en el hilo del DataReceived del puerto.
            // El MISMO CNmeaParser también lo alimenta el bridge LAN (:9999) en
            // OTRO hilo — sin el lock, rawBuffer se corrompe si un GPS serie y
            // uno LAN publican a la vez (sentencias cortadas, fixes perdidos).
            SpGPS.OnDataReceived += bytes =>
            {
                lock (_nmeaLock) Nmea.ParseIncoming(System.Text.Encoding.ASCII.GetString(bytes));
            };
        }

        /// <summary>Serializa las DOS entradas NMEA (RS232 y LAN) sobre el
        /// mismo parser — ver comentario en el handler del SpGPS.</summary>
        private readonly object _nmeaLock = new object();

        private static void ProcessPgnBytes(byte[] bytes, PgnFrameParser parser)
        {
            if (bytes.Length > 100) { parser.Reset(); return; }
            for (int i = 0; i < bytes.Length; i++) parser.ProcessByte(bytes[i]);
        }

        // ---- arranque de los 3 servicios portables ----
        public void StartServices(int mqttPort = 1883, int lanPort = 9999, string loopbackIp = "127.0.0.1")
        {
            _mqttPort = mqttPort;
            MqttBroker.StartAsync(mqttPort).GetAwaiter().GetResult();
            Log.EventWriter("CoreXEngine: broker MQTT en :" + mqttPort);

            UdpBridge.OnLoopbackReceived += (data, ep) => ReceiveFromLoopBack(data);
            UdpBridge.StartLoopback(loopbackIp, 17777, 15555);
            Log.EventWriter("CoreXEngine: loopback escuchando en :17777, contestando a " + loopbackIp + ":15555");

            UdpBridge.OnUdpReceived += (data, ep) => ReceiveFromUdp(data, ep);
            UdpBridge.StartUdp(lanPort);
            Log.EventWriter("CoreXEngine: bridge LAN escuchando en :" + lanPort);

            // Hello periódico a los módulos (PGN 200, 1 Hz) — los MISMOS bytes
            // que manda AgIO nativo. Sin esto los módulos UDP (y ModSim, que
            // los simula) escuchan silencio: nunca "ven" a PilotX, no arranca
            // el handshake y no responden. Se notó al reemplazar el stack
            // AgIO/CoreX.exe por el bridge integrado (reporte 2026-08-05:
            // "el otro recibía mensajes de PilotX"): el hello estaba anotado
            // como no-portado y este es el pedazo que faltaba.
            _helloTimer = new System.Threading.Timer(_ =>
            {
                try { EnviarAModulos(HelloAgIO); }
                catch { /* la LAN puede parpadear; el próximo tick reintenta */ }
            }, null, 1000, 1000);
            var listaEps = string.Join(", ", EndpointsDeModulos());
            Log.EventWriter("CoreXEngine: hello a modulos (PGN 200) cada 1 s hacia [" + listaEps + "]");
        }

        /// <summary>Mismo frame que helloFromAgIO en AgIO/UDP.designer.cs.</summary>
        private static readonly byte[] HelloAgIO = { 0x80, 0x81, 0x7F, 200, 3, 56, 0, 0, 0x47 };
        private System.Threading.Timer _helloTimer;

        // NTRIP: instanciado pero NO conectado hasta que haya credenciales de
        // caster reales (no hay ninguna disponible en este entorno de prueba).
        private bool _rtcmHooked;

        public void ConnectNtrip(NtripConfig config, Func<NtripGpsData> gpsFeedback)
        {
            // Suscribir UNA sola vez: el panel puede reconectar cada vez que se
            // guarda la config y cada += duplicaría el RTCM hacia el GPS.
            if (!_rtcmHooked)
            {
                _rtcmHooked = true;
                Ntrip.OnRtcmData += rtcm =>
                {
                    if (SpRtcm.IsOpen) SpRtcm.Write(rtcm, 0, rtcm.Length);
                    else if (SpGPS.IsOpen) SpGPS.Write(rtcm, 0, rtcm.Length);
                };
            }
            Ntrip.Connect(config, gpsFeedback);
        }

        // Comando real de guiado (bloque 14) sobre el broker MQTT que ya
        // arranca StartServices(): el mismo canal que usan QuantiX/VistaX
        // para descubrimiento (topic "agp/..."). Reemplaza al TCP de prueba
        // de GuidanceEngineHost.Commands.cs por un transporte que ya es el
        // estándar del ecosistema (ver COORDINACION-SESIONES.md).
        // Payload esperado: texto plano con el comando (ej. "autosteer"),
        // mismo vocabulario que GuidanceEngineHost.ExecuteCommand.
        public void SubscribeCommands(Action<string> onCommand, string topic = "agp/aog/guidance/command")
        {
            var factory = new MqttFactory();
            _cmdClient = factory.CreateMqttClient();

            var opts = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", _mqttPort)
                .WithClientId("PilotX_GuidanceEngine_cmd")
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .Build();

            _cmdClient.ApplicationMessageReceivedAsync += e =>
            {
                var seg = e.ApplicationMessage.PayloadSegment;
                string cmd = Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);
                onCommand(cmd);
                return System.Threading.Tasks.Task.CompletedTask;
            };

            _cmdClient.ConnectAsync(opts).GetAwaiter().GetResult();
            _cmdClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic).Build()).GetAwaiter().GetResult();

            Log.EventWriter("CoreXEngine: comandos por MQTT en tópico " + topic);
        }

        // ---- ruteo PGN loopback -> puertos serie (equivalente a
        // CPgnRouter.RouteLoopbackPgn + ReceiveFromLoopBack de UDP.designer.cs) ----
        private void ReceiveFromLoopBack(byte[] data)
        {
            EnviarAModulos(data);

            if (data.Length < 4 || data[0] != 0x80 || data[1] != 0x81) return;

            RouteLoopbackPgn(data[3], out bool toSteer, out bool toMachine);
            if (toSteer && SpSteerModule.IsOpen) SpSteerModule.Write(data, 0, data.Length);
            if (toMachine && SpMachineModule.IsOpen) SpMachineModule.Write(data, 0, data.Length);
        }

        private static void RouteLoopbackPgn(byte pgn, out bool toSteer, out bool toMachine)
        {
            toSteer = false;
            toMachine = false;
            switch (pgn)
            {
                case 0xFE: toSteer = true; toMachine = true; break; // 254 AutoSteer Data
                case 0xEF: toMachine = true; toSteer = true; break; // 239 machine pgn
                case 0xE5: toMachine = true; break;                  // 229 sections/zones
                case 0xFC: toSteer = true; break;                    // 252 steer settings
                case 0xFB: toSteer = true; break;                    // 251 steer config
                case 0xEE: toMachine = true; toSteer = true; break; // 238 machine config
                case 0xEC: toMachine = true; toSteer = true; break; // 236 machine config
            }
        }

        // módulos por red (LAN) en vez de serie. Dos tipos de tráfico por :9999:
        //  (a) PGN ya envuelto (0x80 0x81...) de módulos WiFi tipo AutoSteer ECU
        //      -> se reenvía tal cual al loopback.
        //  (b) NMEA crudo ($GPGGA/$GPVTG/$PANDA) de un GPS/simulador (ModSim) que
        //      saca NMEA por UDP en vez de serie -> se parsea con el MISMO
        //      CNmeaParser del path serie (arma el PGN 0xD6 via
        //      INmeaParserHost.SendNmeaPgn -> loopback). Sin esto el motor recibe
        //      texto que no entiende y la velocidad/posición quedan en cero.
        //  [stopgap Leonardo 2026-07-24, avisado a Santi: mismo patrón que el
        //   bridge LAN de Android en HubBootstrap.OnUdpReceived]
        private void ReceiveFromUdp(byte[] data, IPEndPoint remoteEp)
        {
            if (data == null || data.Length < 4) return;

            if (data[0] == 0x80 && data[1] == 0x81)
            {
                // ANTI-ECO: los PGNs que ORIGINA el propio motor (posición
                // corregida, autosteer data, secciones, settings) jamás pueden
                // venir de un módulo — si llegan por la LAN son un eco (ModSim
                // u otro relay reflejando el broadcast). Reinyectarlos armaba
                // un lazo: eco de 0xD6 → UpdateFixPosition → 4 PGNs más →
                // más eco… hasta GB de RAM. Se descartan acá.
                byte pgn = data.Length > 3 ? data[3] : (byte)0;
                bool esNuestro = pgn == 0xD6 || pgn == 0xFE || pgn == 0xEF ||
                                 pgn == 0xE5 || pgn == 0xFC || pgn == 0xFB ||
                                 pgn == 0xEE || pgn == 0xEC || pgn == 0xEB;
                if (!esNuestro) UdpBridge.SendToLoopback(data);
            }
            else if (data[0] == (byte)'$')
            {
                try
                {
                    // TryEnter + descarte (ver GuidanceEngineHost): bajo flood
                    // NMEA lo único que importa es la sentencia más nueva.
                    if (System.Threading.Monitor.TryEnter(_nmeaLock))
                    {
                        try { Nmea.ParseIncoming(System.Text.Encoding.ASCII.GetString(data)); }
                        finally { System.Threading.Monitor.Exit(_nmeaLock); }
                    }
                }
                catch (Exception ex) { Log.EventWriter("CoreXEngine: LAN NMEA parse: " + ex.Message); }
            }
        }

        // ---- INmeaParserHost ----
        // true: sin esto el parser no guarda las sentencias crudas y la página
        // GPS del panel (:5181) muestra "—" en todas. Es solo retener el último
        // string de cada tipo — costo despreciable, siempre prendido.
        bool INmeaParserHost.IsGpsSentencesOn => true;
        bool INmeaParserHost.IsLogMonitorOn => false;
        void INmeaParserHost.AppendLogMonitor(string text) { }
        void INmeaParserHost.SendNmeaPgn(byte[] pgn)
        {
            // Latido del GPS para el panel (:5181): cada PGN de posición que
            // sale del parser es prueba de que está entrando NMEA.
            _lastNmeaUtc = DateTime.UtcNow;
            UdpBridge.SendToLoopback(pgn);
        }

        private DateTime _lastNmeaUtc = DateTime.MinValue;

        /// <summary>Segundos desde el último NMEA parseado; −1 si nunca llegó.
        /// El panel lo usa para el "GPS vivo" del dashboard.</summary>
        public double NmeaAliveSec =>
            _lastNmeaUtc == DateTime.MinValue ? -1 : (DateTime.UtcNow - _lastNmeaUtc).TotalSeconds;

        // ---- apertura de los 6 puertos (mismo patrón que SerialComm.Designer.cs) ----
        public void OpenGPSPort(string portName, int baudRate)
        {
            SpGPS.WriteTimeout = 1000;
            try { SpGPS.Open(portName, baudRate); SpGPS.DiscardInBuffer(); SpGPS.DiscardOutBuffer(); }
            catch (Exception ex) { Log.EventWriter("CoreXEngine: falla abriendo GPS " + portName + ": " + ex.Message); }
        }

        public void OpenIMUPort(string portName, int baudRate)
        {
            SpIMU.DtrEnable = true;
            SpIMU.RtsEnable = true;
            try
            {
                SpIMU.Open(portName, baudRate);
                System.Threading.Thread.Sleep(500);
                SpIMU.DiscardInBuffer();
                SpIMU.DiscardOutBuffer();
            }
            catch (Exception ex) { Log.EventWriter("CoreXEngine: falla abriendo IMU " + portName + ": " + ex.Message); }
        }

        public void OpenSteerModulePort(string portName, int baudRate)
        {
            SpSteerModule.DtrEnable = true;
            SpSteerModule.RtsEnable = true;
            try
            {
                SpSteerModule.Open(portName, baudRate);
                System.Threading.Thread.Sleep(1000);
                SpSteerModule.DiscardInBuffer();
                SpSteerModule.DiscardOutBuffer();
            }
            catch (Exception ex) { Log.EventWriter("CoreXEngine: falla abriendo Steer " + portName + ": " + ex.Message); }
        }

        public void OpenMachineModulePort(string portName, int baudRate)
        {
            SpMachineModule.DtrEnable = true;
            SpMachineModule.RtsEnable = true;
            try
            {
                SpMachineModule.Open(portName, baudRate);
                System.Threading.Thread.Sleep(1000);
                SpMachineModule.DiscardInBuffer();
                SpMachineModule.DiscardOutBuffer();
            }
            catch (Exception ex) { Log.EventWriter("CoreXEngine: falla abriendo Machine " + portName + ": " + ex.Message); }
        }

        public void Stop()
        {
            SpGPS.Close(); SpGPS2.Close(); SpRtcm.Close();
            SpIMU.Close(); SpSteerModule.Close(); SpMachineModule.Close();
            UdpBridge.Stop();
            if (_cmdClient != null)
            {
                try { _cmdClient.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
                _cmdClient.Dispose();
                _cmdClient = null;
            }
            MqttBroker.StopAsync().GetAwaiter().GetResult();
            Ntrip.Disconnect();
        }

        public void Dispose() => Stop();
    }
}
