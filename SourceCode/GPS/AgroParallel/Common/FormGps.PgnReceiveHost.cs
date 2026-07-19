// ============================================================================
// FormGps.PgnReceiveHost.cs
// Implementación de IPgnReceiveHost sobre FormGPS. PgnReceiver (parser de
// PGNs entrantes desde CoreX) vive en AgOpenGPS.Core y consume el host a
// través de esta interfaz (inversión de dependencia — traspaso de
// portabilidad 2026-07-19).
// ============================================================================

using AgOpenGPS.Core;
using System.Drawing;

namespace AgOpenGPS
{
    public partial class FormGPS : IPgnReceiveHost
    {
        ApplicationModel IPgnReceiveHost.AppModel => AppModel;
        CNMEA IPgnReceiveHost.Pn => pn;
        CAHRS IPgnReceiveHost.Ahrs => ahrs;
        CModuleComm IPgnReceiveHost.Mc => mc;
        CTrack IPgnReceiveHost.Trk => trk;
        CISOBUS IPgnReceiveHost.Isobus => isobus;

        bool IPgnReceiveHost.IsSimEnabled => timerSim.Enabled;
        void IPgnReceiveHost.DisableSim() => DisableSim();
        void IPgnReceiveHost.UpdateFixPosition() => UpdateFixPosition();

        bool IPgnReceiveHost.IsHardwareMessages => isHardwareMessages;

        void IPgnReceiveHost.ShowHardwareMessage(string text, bool isAlert, int secondsToDisplay)
        {
            lblHardwareMessage.Text = text;
            lblHardwareMessage.Visible = true;
            hardwareLineCounter = secondsToDisplay * 10;

            lblHardwareMessage.BackColor = isAlert ? Color.Salmon : Color.Bisque;
            lblHardwareMessage.ForeColor = Color.Black;
        }

        void IPgnReceiveHost.HideHardwareMessage()
        {
            lblHardwareMessage.Visible = false;
            hardwareLineCounter = 0;
        }

        void IPgnReceiveHost.CycleLineForward() => btnCycleLines.PerformClick();
        void IPgnReceiveHost.CycleLineBackward() => btnCycleLinesBk.PerformClick();
        void IPgnReceiveHost.DoRemoteSwitches() => DoRemoteSwitches();
        void IPgnReceiveHost.OnGpsSentenceReceived() => sentenceCounter = 0;
        void IPgnReceiveHost.OnSteerModuleTraffic() => steerModuleConnectedCounter = 0;
    }
}
