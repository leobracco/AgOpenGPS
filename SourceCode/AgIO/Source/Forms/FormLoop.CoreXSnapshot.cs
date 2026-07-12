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

            // Capturas controladas por la web (sentencias NMEA, monitor UDP,
            // monitor GPS crudo): mientras la página correspondiente pollee su
            // endpoint mantenemos la captura viva; a los 5 s sin requests la
            // apagamos (solo si la prendimos nosotros, para no pisar a los
            // forms de la UI vieja).
            bool webWants = WebMonitorWants(ref _gpsSentencesWebReqTicks);
            if (webWants && !isGPSSentencesOn)
            {
                isGPSSentencesOn = true;
                _gpsSentencesWebOwned = true;
            }
            else if (!webWants && _gpsSentencesWebOwned)
            {
                isGPSSentencesOn = false;
                _gpsSentencesWebOwned = false;
            }

            bool udpMonWants = WebMonitorWants(ref _udpMonWebReqTicks);
            if (udpMonWants && !isUDPMonitorOn)
            {
                isUDPMonitorOn = true;
                _udpMonWebOwned = true;
            }
            else if (!udpMonWants && _udpMonWebOwned)
            {
                isUDPMonitorOn = false;
                _udpMonWebOwned = false;
                logUDPSentence.Clear();
            }

            bool rawMonWants = WebMonitorWants(ref _rawMonWebReqTicks);
            if (rawMonWants && !isLogMonitorOn)
            {
                isLogMonitorOn = true;
                _rawMonWebOwned = true;
            }
            else if (!rawMonWants && _rawMonWebOwned)
            {
                isLogMonitorOn = false;
                _rawMonWebOwned = false;
                logMonitorSentence.Clear();
            }

            // Tope de seguridad: si un monitor quedó prendido sin que nadie
            // drene (p. ej. la UI vieja abierta pero congelada), que el buffer
            // no crezca sin límite.
            if (logUDPSentence.Length > 200000) logUDPSentence.Clear();
            if (logMonitorSentence.Length > 200000) logMonitorSentence.Clear();

            CoreXState.Instance.Publish(new CoreXStatusDto
            {
                Version = Program.Version,
                Profile = RegistrySettings.profileName,
                Gps = new CoreXGpsDto
                {
                    Alive = lastHelloGPS,
                    Latitude = latitude,
                    Longitude = longitude,

                    // Port de FormGPSData: mismos campos que sus labels.
                    FixQuality = FixQuality.TrimEnd(' ', ':'),
                    Sats = satellitesData,
                    Hdop = hdopData,
                    SpeedKmh = speedData,
                    AltitudeM = altitudeData,
                    AgeSec = ageData,
                    RollDeg = rollData,
                    HeadingTrue = headingTrueData,
                    HeadingDual = headingTrueDualData,
                    ImuHeading = imuHeadingData,
                    ImuRoll = imuRollData,
                    ImuPitch = imuPitchData,
                    ImuYawRate = imuYawRateData,
                    Nmea = isGPSSentencesOn
                        ? new CoreXNmeaDto
                        {
                            Gga = ggaSentence ?? "",
                            Vtg = vtgSentence ?? "",
                            Panda = pandaSentence ?? "",
                            Paogi = paogiSentence ?? "",
                            Hdt = hdtSentence ?? "",
                            Avr = avrSentence ?? "",
                            Hpd = hpdSentence ?? "",
                            Ksxt = ksxtSentence ?? "",
                        }
                        : null,
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

        // ── Capturas keep-alive para las páginas de la web ───────────────────
        // Cada GET del endpoint correspondiente renueva su timestamp desde el
        // pool thread de EmbedIO; UpdateCoreXSnapshot (hilo UI, 1 Hz) lo lee
        // con Interlocked y decide encender/apagar el flag de captura.
        private long _gpsSentencesWebReqTicks;
        private bool _gpsSentencesWebOwned;
        private long _udpMonWebReqTicks;
        private bool _udpMonWebOwned;
        private long _rawMonWebReqTicks;
        private bool _rawMonWebOwned;

        private static bool WebMonitorWants(ref long ticksField) =>
            (DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref ticksField))
                < 5 * TimeSpan.TicksPerSecond;

        private static void RenewWebMonitor(ref long ticksField) =>
            System.Threading.Interlocked.Exchange(ref ticksField, DateTime.UtcNow.Ticks);

        public void KeepGpsSentencesAliveFromWeb() => RenewWebMonitor(ref _gpsSentencesWebReqTicks);
        public void KeepUdpMonitorAliveFromWeb() => RenewWebMonitor(ref _udpMonWebReqTicks);
        public void KeepRawMonitorAliveFromWeb() => RenewWebMonitor(ref _rawMonWebReqTicks);

        // ── Puentes del monitor de tráfico (SOLO hilo UI, vía RunOnUiAsync) ──
        // Port de FormUDPMonitor / FormSerialMonitor: drenan el StringBuilder
        // igual que los timer1_Tick de esos forms (leer y limpiar).

        /// <summary>Drena el log de tráfico UDP/PGN y devuelve flags de filtro.</summary>
        public CoreXDiagController.UdpMonitorDrainDto DrainUdpMonitorFromWeb()
        {
            string data = logUDPSentence.ToString();
            logUDPSentence.Clear();
            return new CoreXDiagController.UdpMonitorDrainDto
            {
                Data = data,
                LogNmea = isGPSLogOn,
                LogNtrip = isNTRIPLogOn,
            };
        }

        /// <summary>Aplica los filtros del monitor UDP (NMEA y NTRIP).</summary>
        public void SetUdpMonitorFlagsFromWeb(bool logNmea, bool logNtrip)
        {
            isGPSLogOn = logNmea;
            isNTRIPLogOn = logNtrip;
        }

        /// <summary>Drena el log crudo de sentencias GPS (monitor serial/NMEA).</summary>
        public string DrainRawMonitorFromWeb()
        {
            string data = logMonitorSentence.ToString();
            logMonitorSentence.Clear();
            return data;
        }

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
                try { tcs.SetResult(fn()); }
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
                case "gps": CloseGPSPort(); break;
                case "gps2": CloseGPS2Port(); break;
                case "rtcm": CloseRtcmPort(); break;
                case "imu": CloseIMUPort(); break;
                case "steer": CloseSteerModulePort(); break;
                case "machine": CloseMachineModulePort(); break;
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
                || (d.SendToUdp != s.setNTRIP_sendToUDP);

            s.setNTRIP_isOn = d.IsOn;
            if (d.IsOn)
            {
                // Mismo comportamiento que cboxIsNTRIPOn_Click y btnSerialOK_Click.
                s.setRadio_isOn = isRadio_RequiredOn = false;
                s.setPass_isOn = isSerialPass_RequiredOn = false;
            }

            s.setNTRIP_casterURL = d.CasterUrl ?? "";
            s.setNTRIP_casterIP = d.CasterIp ?? "";
            s.setNTRIP_casterPort = d.CasterPort;
            s.setNTRIP_mount = d.Mount ?? "";
            s.setNTRIP_userName = d.UserName ?? "";
            s.setNTRIP_userPassword = d.UserPassword ?? "";
            s.setNTRIP_sendGGAInterval = d.SendGgaInterval;
            s.setNTRIP_isGGAManual = d.IsGgaManual;
            s.setNTRIP_manualLat = d.ManualLat;
            s.setNTRIP_manualLon = d.ManualLon;
            s.setNTRIP_isTCP = d.IsTcp;
            s.setNTRIP_isHTTP10 = d.IsHttp10;
            s.setNTRIP_packetSize = d.PacketSize;
            s.setNTRIP_sendToSerial = isSendToSerial = d.SendToSerial;
            s.setNTRIP_sendToUDP = isSendToUDP = d.SendToUdp;
            s.setNTRIP_sendToUDPPort = d.SendToUdpPort;
            packetSizeNTRIP = d.PacketSize;

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
            Properties.Settings.Default.etIP_SubnetOne = o1;
            Properties.Settings.Default.etIP_SubnetTwo = o2;
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

            // Enumeración de NICs en memoria (sin Dns.GetHostAddresses: la
            // resolución DNS puede tardar segundos y esto corre en el hilo UI).
            var sb = new System.Text.StringBuilder();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                        || !nic.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4))
                        continue;

                    foreach (var info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !System.Net.IPAddress.IsLoopback(info.Address))
                        {
                            if (sb.Length > 0) sb.Append(", ");
                            sb.Append(info.Address.ToString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AgLibrary.Logging.Log.EventWriter("GetLocalIpForWeb error: " + ex.Message);
            }
            return sb.Length > 0 ? sb.ToString() : "—";
        }

        // ── Puentes de módulos para el web host ──────────────────────────────
        // Los handlers de los cbox en Controls.Designer.cs son eventos Click
        // (NO CheckedChanged), por lo que asignar cbox.Checked desde acá NO
        // dispara ningún evento y no hay doble ejecución de SetModulesOnOff().
        // El puente replica exactamente lo que hace cada cbox_Click: primero
        // actualiza el campo isConnected*, luego llama SetModulesOnOff() una vez.

        /// <summary>
        /// Aplica los tres módulos on/off en caliente desde la web.
        /// SetModulesOnOff() persiste si hubo cambio (igual que los checkboxes del form).
        /// </summary>
        public void SetModulesFromWeb(bool imu, bool steer, bool machine)
        {
            isConnectedIMU = cboxIsIMUModule.Checked = imu;
            isConnectedSteer = cboxIsSteerModule.Checked = steer;
            isConnectedMachine = cboxIsMachineModule.Checked = machine;
            SetModulesOnOff();
        }

        // ── Puentes de perfiles para el web host ─────────────────────────────
        // Réplica de FormProfiles: guardar = Save() del perfil activo;
        // cargar = cambiar el nombre en Registry y reiniciar (al arrancar,
        // RegistrySettings.Load() lee el perfil nuevo); crear = nombre nuevo
        // en Registry + copia de la config actual (sin reinicio) o valores
        // de fábrica vía Reset() (con reinicio).

        /// <summary>
        /// Guarda la configuración actual en el XML del perfil activo.
        /// </summary>
        public void SaveProfileFromWeb()
        {
            Properties.Settings.Default.Save();
            AgLibrary.Logging.Log.EventWriter(
                "Perfil guardado desde web: " + RegistrySettings.profileName);
        }

        /// <summary>
        /// Cambia el perfil activo y reinicia CoreX para aplicarlo completo
        /// (puertos serie, UDP, NTRIP y broker arrancan con el perfil nuevo).
        /// </summary>
        public void LoadProfileFromWeb(string nombre)
        {
            RegistrySettings.Save(RegKeys.profileName, nombre);
            AgLibrary.Logging.Log.EventWriter(
                "Program Reset: cargar perfil desde web: " + nombre);
            RestartFromWeb();
        }

        /// <summary>
        /// Crea un perfil nuevo. desdeActual=true copia la config vigente al
        /// XML nuevo (sin reinicio: nada cambia en memoria). desdeActual=false
        /// resetea a valores de fábrica (Settings.Reset ya persiste) y
        /// reinicia. Devuelve true si CoreX se reinicia.
        /// </summary>
        public bool CreateProfileFromWeb(string nombre, bool desdeActual)
        {
            RegistrySettings.Save(RegKeys.profileName, nombre);

            if (desdeActual)
            {
                Properties.Settings.Default.Save();
                AgLibrary.Logging.Log.EventWriter(
                    "Perfil creado desde web (copia de la config actual): " + nombre);
                return false;
            }

            Properties.Settings.Default.Reset();
            AgLibrary.Logging.Log.EventWriter(
                "Program Reset: perfil nuevo de fábrica desde web: " + nombre);
            RestartFromWeb();
            return true;
        }

        // ── Puentes de radio RTCM para el web host (SOLO hilo UI) ────────────
        // Port de FormRadio/FormRadioChannel. La radio comparte el pipeline
        // RTCM con NTRIP y serial-pass: guardar reconfigura vía ConfigureNTRIP()
        // (que relee los tres flags de Settings), igual que btnRadioOK_Click.

        /// <summary>Config de radio + canales con distancia a la posición actual.</summary>
        public CoreXRadioController.RadioConfigDto GetRadioConfigForWeb()
        {
            var s = Properties.Settings.Default;
            var channels = new List<CoreXRadioController.RadioChannelDto>();

            foreach (var c in s.setRadio_Channels ?? new List<CRadioChannel>())
            {
                // Distancia al canal si tiene ubicación "lat lon". El form viejo
                // exigía lat>0 && lon>0 (nunca cierto en el hemisferio sur);
                // acá alcanza con tener fix (!=0).
                double distanciaKm = -1;
                if (!string.IsNullOrEmpty(c.Location) && latitude != 0 && longitude != 0)
                {
                    var loc = c.Location.Split(' ');
                    if (loc.Length >= 2
                        && double.TryParse(loc[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double lat)
                        && double.TryParse(loc[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double lon))
                    {
                        distanciaKm = glm.DistanceLonLat(lon, lat, longitude, latitude);
                    }
                }

                channels.Add(new CoreXRadioController.RadioChannelDto
                {
                    Id = c.Id,
                    Name = c.Name ?? "",
                    Frequency = c.Frequency ?? "",
                    Location = c.Location ?? "",
                    DistanceKm = distanciaKm,
                });
            }

            return new CoreXRadioController.RadioConfigDto
            {
                IsOn = s.setRadio_isOn,
                Port = s.setPort_portNameRadio ?? "",
                Baud = s.setPort_baudRateRadio ?? "9600",
                Channel = s.setPort_radioChannel ?? "",
                PortOpen = spRadio != null && spRadio.IsOpen,
                AvailablePorts = System.IO.Ports.SerialPort.GetPortNames()
                    .Distinct().OrderBy(p => p).ToList(),
                Channels = channels,
            };
        }

        /// <summary>
        /// Guarda la config de radio (y la lista de canales completa). Devuelve
        /// null si está OK o el mensaje de error de validación. Radio encendida
        /// apaga NTRIP y serial-pass (excluyentes, igual que el form).
        /// </summary>
        public string SaveRadioConfigFromWeb(CoreXRadioController.RadioConfigSaveRequest r)
        {
            if (r.IsOn && string.IsNullOrEmpty(r.Channel))
                return "La radio está encendida pero no hay canal seleccionado.";

            var s = Properties.Settings.Default;
            s.setPort_portNameRadio = r.Port ?? "";
            s.setPort_baudRateRadio = string.IsNullOrEmpty(r.Baud) ? "9600" : r.Baud;
            s.setPort_radioChannel = r.Channel ?? "";
            s.setRadio_isOn = r.IsOn;

            if (r.IsOn)
            {
                s.setNTRIP_isOn = false;
                s.setPass_isOn = false;
            }

            if (r.Channels != null)
            {
                s.setRadio_Channels = r.Channels.Select(c => new CRadioChannel
                {
                    Id = c.Id,
                    Name = c.Name ?? "",
                    Frequency = c.Frequency ?? "",
                    Location = c.Location ?? "",
                }).ToList();
            }

            s.Save();
            ConfigureNTRIP();
            AgLibrary.Logging.Log.EventWriter("Radio config guardada desde web (on="
                + r.IsOn + ", canal=" + (r.Channel ?? "") + ")");
            return null;
        }

        /// <summary>
        /// Manda un comando de texto a la radio (p. ej. "SL&amp;F=439.000" para
        /// sintonizar). Si el puerto no está abierto lo abre con la config
        /// guardada solo durante el comando. Devuelve la respuesta de la radio
        /// (puede ser vacía) o lanza con mensaje amigable.
        /// </summary>
        public string SendRadioCommandFromWeb(string comando)
        {
            bool abiertoTemporal = false;

            if (spRadio == null || !spRadio.IsOpen)
            {
                var s = Properties.Settings.Default;
                if (string.IsNullOrEmpty(s.setPort_portNameRadio))
                    throw new InvalidOperationException("No hay puerto de radio configurado.");

                spRadio = new System.IO.Ports.SerialPort(
                    s.setPort_portNameRadio, int.Parse(s.setPort_baudRateRadio))
                { NewLine = "\r\n" };
                spRadio.Open();
                abiertoTemporal = true;
            }

            try
            {
                spRadio.WriteLine(comando);
                // La radio contesta enseguida; margen corto como el form viejo.
                System.Threading.Thread.Sleep(150);

                int n = spRadio.BytesToRead;
                if (n == 0) return "";
                byte[] buffer = new byte[n];
                spRadio.Read(buffer, 0, n);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, n);
            }
            finally
            {
                if (abiertoTemporal && spRadio != null)
                {
                    try { spRadio.Close(); spRadio.Dispose(); } catch { /* puerto ya caído */ }
                    spRadio = null;
                }
            }
        }

        // ── Puente de paso serial RTCM (port de FormSerialPass) ──────────────
        // Comparte puerto/baud con la radio (mismos settings setPort_*Radio).
        // Igual que btnSerialOK_Click del form: pass ON apaga NTRIP y radio,
        // y SIEMPRE reinicia CoreX para aplicar.
        public void SaveSerialPassFromWeb(bool isOn, string port, string baud,
            bool toSerial, bool toUdp, int udpPort)
        {
            var s = Properties.Settings.Default;
            s.setPass_isOn = isOn;

            if (isOn)
            {
                s.setNTRIP_isOn = isNTRIP_RequiredOn = false;
                s.setRadio_isOn = isRadio_RequiredOn = false;
            }

            s.setNTRIP_sendToUDPPort = udpPort;
            s.setNTRIP_sendToSerial = isSendToSerial = toSerial;
            s.setNTRIP_sendToUDP = isSendToUDP = toUdp;
            s.setPort_portNameRadio = port ?? "";
            s.setPort_baudRateRadio = string.IsNullOrEmpty(baud) ? "9600" : baud;
            s.Save();

            AgLibrary.Logging.Log.EventWriter("Program Reset: paso serial desde web (on="
                + isOn + ")");
            RestartFromWeb();
        }

        // ── Puente de IP de PilotX (port de FormEthernet) ────────────────────
        // eth_loop* es la IP a donde CoreX manda los datos de GPS/módulos
        // (puerto 15555, normalmente 127.0.0.1 con PilotX en la misma pantalla).
        // El form reinicia siempre al guardar; acá igual.
        public void SavePilotxIpFromWeb(byte o1, byte o2, byte o3, byte o4)
        {
            var s = Properties.Settings.Default;
            s.eth_loopOne = o1;
            s.eth_loopTwo = o2;
            s.eth_loopThree = o3;
            s.eth_loopFour = o4;
            s.Save();

            AgLibrary.Logging.Log.EventWriter("Program Reset: IP de PilotX desde web: "
                + o1 + "." + o2 + "." + o3 + "." + o4);
            RestartFromWeb();
        }

        // ── Modo demonio ──────────────────────────────────────────────────────
        // CoreX no tiene ventana: FormLoop es solo el host invisible del
        // message loop. Hide() no frena los timers (Application.Run sigue
        // vivo), así que el broker, el UDP, los puertos y el snapshot @1Hz
        // siguen andando ocultos. La única interfaz es la web en :5181.
        private bool legacyUiHidden;

        public void HideLegacyUi()
        {
            legacyUiHidden = true;
            ShowInTaskbar = false;
            Hide();
        }

        /// <summary>
        /// Cierra CoreX prolijo desde la web (timer 800 ms para que la
        /// respuesta HTTP salga antes). FormClosing persiste settings y
        /// apaga puertos/broker.
        /// </summary>
        public void ShutdownFromWeb()
        {
            AgLibrary.Logging.Log.EventWriter("Apagado de CoreX pedido desde la web");
            restartWebTimer?.Stop();
            restartWebTimer = new System.Windows.Forms.Timer { Interval = 800 };
            restartWebTimer.Tick += (s2, e2) => { restartWebTimer.Stop(); Close(); };
            restartWebTimer.Start();
        }
    }
}
