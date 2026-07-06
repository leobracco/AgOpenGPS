using System;
using System.Collections.Generic;
using System.Linq;

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

        // Fase 2 del spec: FormLoop queda como host invisible; la ventana
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
