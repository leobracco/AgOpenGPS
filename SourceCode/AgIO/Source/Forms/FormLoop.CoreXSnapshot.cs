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

        /// <summary>
        /// Inicia un reinicio diferido de CoreX (800 ms) para que la respuesta
        /// HTTP pueda salir antes de que el proceso termine.
        /// </summary>
        public void RestartFromWeb()
        {
            var t = new System.Windows.Forms.Timer { Interval = 800 };
            t.Tick += (s2, e2) => { t.Stop(); Program.Restart(); };
            t.Start();
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
