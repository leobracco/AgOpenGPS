// ============================================================================
// FormGps.TrackHost.cs
// Implementación de ITrackHost sobre FormGPS. CTrack vive en AgOpenGPS.Core
// y consume el host a través de esta interfaz (hereda de IGuidanceHost —
// inversión de dependencia, traspaso de portabilidad 2026-07-17).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : ITrackHost
    {
        vec3 ITrackHost.SteerAxlePos => steerAxlePos;
    }
}
