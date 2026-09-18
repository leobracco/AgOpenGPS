namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CTrack necesita del host (FormGPS en WinForms). Hereda de
    /// IGuidanceHost (ABLine/Curve/Tool/Tracks) y agrega solo el eje
    /// directriz. Inversión de dependencia para el traspaso de
    /// portabilidad (2026-07-17).
    /// </summary>
    public interface ITrackHost : IGuidanceHost
    {
        /// <summary>steerAxlePos — posición del eje directriz.</summary>
        vec3 SteerAxlePos { get; }
    }
}
