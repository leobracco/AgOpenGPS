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

        public IPEndPoint EpModule = new IPEndPoint(IPAddress.Parse("255.255.255.255"), 8888);

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
            SpGPS.OnDataReceived += bytes => Nmea.ParseIncoming(System.Text.Encoding.ASCII.GetString(bytes));
        }

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
        }

        // NTRIP: instanciado pero NO conectado hasta que haya credenciales de
        // caster reales (no hay ninguna disponible en este entorno de prueba).
        public void ConnectNtrip(NtripConfig config, Func<NtripGpsData> gpsFeedback)
        {
            Ntrip.OnRtcmData += rtcm =>
            {
                if (SpRtcm.IsOpen) SpRtcm.Write(rtcm, 0, rtcm.Length);
                else if (SpGPS.IsOpen) SpGPS.Write(rtcm, 0, rtcm.Length);
            };
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
            UdpBridge.SendUdpTo(data, EpModule);

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

        // módulos por red (LAN) en vez de serie: llega un PGN de vuelta -> va al loopback.
        private void ReceiveFromUdp(byte[] data, IPEndPoint remoteEp)
        {
            if (data.Length < 4 || data[0] != 0x80 || data[1] != 0x81) return;
            UdpBridge.SendToLoopback(data);
        }

        // ---- INmeaParserHost ----
        bool INmeaParserHost.IsGpsSentencesOn => false;
        bool INmeaParserHost.IsLogMonitorOn => false;
        void INmeaParserHost.AppendLogMonitor(string text) { }
        void INmeaParserHost.SendNmeaPgn(byte[] pgn) => UdpBridge.SendToLoopback(pgn);

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
