using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace AgIO
{
    // Alimenta CoreXState desde el hilo UI (oneSecondLoopTimer). Extracción
    // mecánica: refleja lo mismo que hoy muestran los labels de FormLoop.
    public partial class FormLoop
    {
        private void UpdateCoreXSnapshot()
        {
            List<string> topics;
            lock (_mqttLock)
            {
                topics = _mqttRecentTopics.Take(20).ToList();
            }

            CoreXState.Instance.Publish(new CoreXStatusDto
            {
                Version = Program.Version,
                Profile = RegistrySettings.profileName,
                Gps = new CoreXGpsDto
                {
                    Alive = lastHelloGPS,
                    Latitude = latitude,
                    Longitude = longitude,
                },
                Ntrip = new CoreXNtripDto
                {
                    RequiredOn = isNTRIP_RequiredOn,
                    Connected = isNTRIP_Connected,
                    Connecting = isNTRIP_Connecting,
                    KbTotal = (long)(tripBytes >> 10),
                    CasterIp = broadCasterIP ?? "",
                },
                Mqtt = new CoreXMqttDto
                {
                    Running = _mqttRunning,
                    Port = _mqttPort,
                    Clients = _mqttClientsConnected,
                    Messages = _mqttMessagesTotal,
                    UptimeSec = _mqttRunning
                        ? (long)(DateTime.Now - _mqttStartTime).TotalSeconds : 0,
                    RecentTopics = topics,
                },
                Modules = new CoreXModulesDto
                {
                    SteerConfigured = isConnectedSteer,
                    SteerHello = traffic.helloFromAutoSteer < 3,
                    MachineConfigured = isConnectedMachine,
                    MachineHello = traffic.helloFromMachine < 3,
                    ImuConfigured = isConnectedIMU,
                    ImuHello = traffic.helloFromIMU < 3,
                },
            });
        }

        // Puentes para el web host (Task 4): reusan los handlers de los
        // botones WinForms para que web y UI vieja hagan exactamente lo mismo.
        public void ToggleMqttBrokerFromWeb() => btnMQTT_Click(null, EventArgs.Empty);
        public void ToggleNtripFromWeb() => btnStartStopNtrip_Click(null, EventArgs.Empty);

        // ── Puentes de hilos para el web host ────────────────────────────────
        // EmbedIO despacha requests en pool threads; los SerialPort, labels y
        // Settings de FormLoop NO son thread-safe. Todo acceso pasa por acá.

        /// <summary>
        /// Ejecuta <paramref name="fn"/> en el hilo UI y devuelve el resultado
        /// al endpoint web. Nunca bloquear el hilo UI desde este awaitable.
        /// </summary>
        public System.Threading.Tasks.Task<T> RunOnUiAsync<T>(Func<T> fn)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
            BeginInvoke((MethodInvoker)(() =>
            {
                try   { tcs.SetResult(fn()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));
            return tcs.Task;
        }

        /// <summary>
        /// Abre el canal serie indicado con el puerto y baud elegidos desde la
        /// web. Escribe los campos static antes de llamar al OpenXPort original
        /// (que también persiste y actualiza el label). Devuelve true si quedó
        /// abierto.
        /// </summary>
        public bool OpenSerialFromWeb(string channel, string port, int baud)
        {
            switch (channel)
            {
                case "gps":
                    portNameGPS = port;
                    baudRateGPS = baud;
                    OpenGPSPort();
                    return spGPS.IsOpen;

                case "gps2":
                    portNameGPS2 = port;
                    baudRateGPS2 = baud;
                    OpenGPS2Port();
                    return spGPS2.IsOpen;

                case "rtcm":
                    portNameRtcm = port;
                    baudRateRtcm = baud;
                    OpenRtcmPort();
                    return spRtcm.IsOpen;

                case "imu":
                    // IMU tiene baud fijo (38400); ignoramos el parámetro baud.
                    portNameIMU = port;
                    OpenIMUPort();
                    return spIMU.IsOpen;

                case "steer":
                    // Steer tiene baud fijo (38400); ignoramos el parámetro baud.
                    portNameSteerModule = port;
                    OpenSteerModulePort();
                    return spSteerModule.IsOpen;

                case "machine":
                    // Machine tiene baud fijo (38400); ignoramos el parámetro baud.
                    portNameMachineModule = port;
                    OpenMachineModulePort();
                    return spMachineModule.IsOpen;

                default:
                    throw new ArgumentException("canal desconocido: " + channel);
            }
        }

        /// <summary>
        /// Cierra el canal serie indicado desde la web.
        /// </summary>
        public void CloseSerialFromWeb(string channel)
        {
            switch (channel)
            {
                case "gps":     CloseGPSPort();            break;
                case "gps2":    CloseGPS2Port();           break;
                case "rtcm":    CloseRtcmPort();           break;
                case "imu":     CloseIMUPort();            break;
                case "steer":   CloseSteerModulePort();    break;
                case "machine": CloseMachineModulePort();  break;
                default: throw new ArgumentException("canal desconocido: " + channel);
            }
        }

        // ── Puentes NTRIP para el web host ────────────────────────────────────
        // Replica exactamente lo que hace FormNtrip.btnSerialOK_Click, pero
        // sin acceso a la UI del form. El criterio de reinicio es el mismo que
        // ntripStatusChanged en el form: cambió is_on o el destino serial/UDP.

        /// <summary>
        /// Guarda la configuración NTRIP desde la web y devuelve true si CoreX
        /// necesita reiniciarse (cambió is_on o el destino serial/UDP).
        /// </summary>
        public bool SaveNtripConfigFromWeb(CoreXConfigController.NtripConfigDto d)
        {
            var s = Properties.Settings.Default;

            // Calcular si hay cambio que requiere reinicio ANTES de pisar settings.
            bool restart = (d.IsOn != s.setNTRIP_isOn)
                || (d.SendToSerial != s.setNTRIP_sendToSerial)
                || (d.SendToUdp   != s.setNTRIP_sendToUDP);

            s.setNTRIP_isOn = d.IsOn;
            if (d.IsOn)
            {
                // Mismo comportamiento que cboxIsNTRIPOn_Click y btnSerialOK_Click.
                s.setRadio_isOn          = isRadio_RequiredOn       = false;
                s.setPass_isOn           = isSerialPass_RequiredOn  = false;
            }

            s.setNTRIP_casterURL        = d.CasterUrl      ?? "";
            s.setNTRIP_casterIP         = d.CasterIp       ?? "";
            s.setNTRIP_casterPort       = d.CasterPort;
            s.setNTRIP_mount            = d.Mount           ?? "";
            s.setNTRIP_userName         = d.UserName        ?? "";
            s.setNTRIP_userPassword     = d.UserPassword    ?? "";
            s.setNTRIP_sendGGAInterval  = d.SendGgaInterval;
            s.setNTRIP_isGGAManual      = d.IsGgaManual;
            s.setNTRIP_manualLat        = d.ManualLat;
            s.setNTRIP_manualLon        = d.ManualLon;
            s.setNTRIP_isTCP            = d.IsTcp;
            s.setNTRIP_isHTTP10         = d.IsHttp10;
            s.setNTRIP_packetSize       = d.PacketSize;
            s.setNTRIP_sendToSerial     = isSendToSerial    = d.SendToSerial;
            s.setNTRIP_sendToUDP        = isSendToUDP       = d.SendToUdp;
            s.setNTRIP_sendToUDPPort    = d.SendToUdpPort;
            packetSizeNTRIP             = d.PacketSize;

            s.Save();

            // Aplica en caliente solo si no hay cambio que requiera reinicio
            // (igual que el form: ConfigureNTRIP() solo si !ntripStatusChanged).
            if (!restart) ConfigureNTRIP();

            return restart;
        }

        // Timer único de reinicio: si el operario guarda dos veces rápido, el
        // segundo pedido reemplaza al primero (no dos Program.Restart()).
        private System.Windows.Forms.Timer restartWebTimer;

        /// <summary>
        /// Inicia un reinicio diferido de CoreX (800 ms) para que la respuesta
        /// HTTP pueda salir antes de que el proceso termine.
        /// </summary>
        public void RestartFromWeb()
        {
            restartWebTimer?.Stop();
            restartWebTimer = new System.Windows.Forms.Timer { Interval = 800 };
            restartWebTimer.Tick += (s2, e2) => { restartWebTimer.Stop(); Program.Restart(); };
            restartWebTimer.Start();
        }

        // ── Puentes de red UDP para el web host ───────────────────────────────

        /// <summary>
        /// Guarda el estado UDP on/off y reinicia CoreX (igual que FormEthernet y
        /// FormUDP.btnUDPOff_Click: SIEMPRE reinicia para aplicar el cambio).
        /// </summary>
        public void SetUdpOnOffFromWeb(bool on)
        {
            var s = Properties.Settings.Default;
            s.setUDP_isOn = on;
            if (!on) s.setUDP_isSendNMEAToUDP = false;
            s.Save();
            AgLibrary.Logging.Log.EventWriter("Program Reset: UDP on/off desde la web");
            RestartFromWeb();
        }

        /// <summary>
        /// Envía el comando de cambio de subnet a todos los módulos de la LAN y
        /// actualiza epModule + Settings. Réplica de FormUDP.btnSendSubnet_Click
        /// (líneas 217-290), adaptada para recibir los octetos como parámetros.
        /// No reinicia CoreX.
        /// </summary>
        public void SendSubnetFromWeb(byte o1, byte o2, byte o3)
        {
            // PGN de cambio de subnet: mismo array que FormUDP.sendIPToModules.
            byte[] sendIPToModules = { 0x80, 0x81, 0x7F, 201, 5, 201, 201, 192, 168, 5, 0x47 };
            sendIPToModules[7] = o1;
            sendIPToModules[8] = o2;
            sendIPToModules[9] = o3;

            // Broadcast por cada NIC activa (igual que el form).
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4)
                    && nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    foreach (var info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !System.Net.IPAddress.IsLoopback(info.Address)
                            && info.IPv4Mask != null)
                        {
                            System.Net.Sockets.Socket scanSocket;
                            try
                            {
                                if (nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                    && info.IPv4Mask != null)
                                {
                                    scanSocket = new System.Net.Sockets.Socket(
                                        System.Net.Sockets.AddressFamily.InterNetwork,
                                        System.Net.Sockets.SocketType.Dgram,
                                        System.Net.Sockets.ProtocolType.Udp);
                                    scanSocket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
                                        System.Net.Sockets.SocketOptionName.Broadcast, true);
                                    scanSocket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
                                        System.Net.Sockets.SocketOptionName.ReuseAddress, true);
                                    scanSocket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
                                        System.Net.Sockets.SocketOptionName.DontRoute, true);
                                    try
                                    {
                                        scanSocket.Bind(new System.Net.IPEndPoint(info.Address, 9999));
                                        scanSocket.SendTo(sendIPToModules, 0, sendIPToModules.Length,
                                            System.Net.Sockets.SocketFlags.None, epModuleSet);
                                    }
                                    catch (Exception ex)
                                    {
                                        AgLibrary.Logging.Log.EventWriter(
                                            "Catch -> Send Subnet Bind and Send (web): " + ex.ToString());
                                    }
                                    scanSocket.Dispose();
                                }
                            }
                            catch (Exception ex)
                            {
                                AgLibrary.Logging.Log.EventWriter(
                                    "Catch -> Nic Loop Send Subnet (web): " + ex.ToString());
                            }
                        }
                    }
                }
            }

            // Persistir y actualizar epModule en caliente (igual que el form).
            Properties.Settings.Default.etIP_SubnetOne   = o1;
            Properties.Settings.Default.etIP_SubnetTwo   = o2;
            Properties.Settings.Default.etIP_SubnetThree = o3;
            Properties.Settings.Default.Save();

            epModule = new System.Net.IPEndPoint(
                System.Net.IPAddress.Parse(o1 + "." + o2 + "." + o3 + ".255"), 8888);

            AgLibrary.Logging.Log.EventWriter("Subnet enviada desde web: " + o1 + "." + o2 + "." + o3);
        }

        /// <summary>
        /// Devuelve las IPs locales que CoreX usa para la red UDP (lo que
        /// muestra lblIP en LoadUDPNetwork, sin los saltos de línea del label).
        /// Si UDP está apagado devuelve "Off".
        /// </summary>
        public string GetLocalIpForWeb()
        {
            if (!Properties.Settings.Default.setUDP_isOn) return "Off";

            var sb = new System.Text.StringBuilder();
            try
            {
                foreach (System.Net.IPAddress ipa in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                {
                    if (ipa.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(ipa.ToString().Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                AgLibrary.Logging.Log.EventWriter("GetLocalIpForWeb error: " + ex.Message);
            }
            return sb.Length > 0 ? sb.ToString() : "—";
        }

        // ── Fase 2 del spec ───────────────────────────────────────────────────
        // FormLoop queda como host invisible; la ventana
        // visible es FormWebShell. Hide() no frena los timers (el message
        // loop de Application.Run sigue vivo), así que el broker, el UDP y
        // el snapshot @1Hz siguen andando ocultos.
        private bool legacyUiHidden;

        public void HideLegacyUi()
        {
            legacyUiHidden = true;
            ShowInTaskbar = false;
            Hide();
        }

        // Escape de seguridad: si la ventana web se cierra (o WebView2
        // falla), la UI vieja vuelve para no dejar al operario sin nada.
        public void ShowLegacyUi()
        {
            legacyUiHidden = false;
            ShowInTaskbar = true;
            Show();
            WindowState = System.Windows.Forms.FormWindowState.Normal;
        }
    }
}
