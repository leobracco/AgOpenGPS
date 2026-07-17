// ============================================================================
// FormGps.NmeaHost.cs
// Implementación de INmeaHost sobre FormGPS. CNMEA vive en AgOpenGPS.Core
// y consume el host a través de esta interfaz (inversión de dependencia,
// traspaso de portabilidad 2026-07-17).
// ============================================================================

using AgOpenGPS.Core;

namespace AgOpenGPS
{
    public partial class FormGPS : INmeaHost
    {
        double INmeaHost.AvgSpeed
        {
            get => avgSpeed;
            set => avgSpeed = value;
        }

        bool INmeaHost.IsSimTimerEnabled => timerSim.Enabled;

        CSim INmeaHost.Sim => sim;

        WorldGrid INmeaHost.WorldGrid => worldGrid;
    }
}
