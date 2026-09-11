// ============================================================================
// GuidanceEngineHost.PgnReceive.cs — IPgnReceiveHost, consumido por
// PgnReceiver (parseo de PGNs entrantes desde CoreX, ya 100% Core-portable).
// Mismo patrón que FormGps.PgnReceiveHost.cs, sin los mensajes de hardware
// en pantalla (log en su lugar).
// ============================================================================

using AgLibrary.Logging;
using AgOpenGPS.Core;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost : IPgnReceiveHost
    {
        ApplicationModel IPgnReceiveHost.AppModel => AppModelField;
        CNMEA IPgnReceiveHost.Pn => Pn;
        CAHRS IPgnReceiveHost.Ahrs => Ahrs;
        CModuleComm IPgnReceiveHost.Mc => Mc;
        CTrack IPgnReceiveHost.Trk => Trk;
        CISOBUS IPgnReceiveHost.Isobus => Isobus;

        bool IPgnReceiveHost.IsSimEnabled => isSimTimerEnabled;

        void IPgnReceiveHost.DisableSim()
        {
            isFirstFixPositionSet = false;
            isGPSPositionInitialized = false;
            isFirstHeadingSet = false;
            startCounter = 0;
            isSimTimerEnabled = false;
        }

        void IPgnReceiveHost.UpdateFixPosition() => UpdateFixPosition();

        bool IPgnReceiveHost.IsHardwareMessages => false;

        void IPgnReceiveHost.ShowHardwareMessage(string text, bool isAlert, int secondsToDisplay)
            => Log.EventWriter($"GuidanceEngine [hardware{(isAlert ? " ALERTA" : "")}] {text}");

        void IPgnReceiveHost.HideHardwareMessage() { }

        void IPgnReceiveHost.CycleLineForward() { }
        void IPgnReceiveHost.CycleLineBackward() { }

        void IPgnReceiveHost.DoRemoteSwitches() { }

        void IPgnReceiveHost.OnGpsSentenceReceived() => sentenceCounter = 0;
        void IPgnReceiveHost.OnSteerModuleTraffic() { }
    }
}
