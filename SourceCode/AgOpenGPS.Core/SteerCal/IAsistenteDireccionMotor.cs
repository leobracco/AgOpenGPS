// ============================================================================
// IAsistenteDireccionMotor.cs — el asistente de calibración de la dirección
// pidiendo el PGN 254.
//
// CAutoSteerUpdater lo consulta en CADA PGN de dirección (al ritmo del GPS).
// Sin asistente conectado (null, el caso normal) el PGN sale exactamente como
// siempre. Con asistente, solo mientras QuiereMotor devuelve true el 254 lleva
// status=1, el setpoint del asistente y SU velocidad (0,5 km/h falsos en los
// pasos parados, la real andando) — nunca los 8 km/h del manejo libre.
// La implementación vive en el motor (EngineSteerCalService): acá solo el
// contrato, para no atar AgOpenGPS.Core al host.
// ============================================================================

namespace AgOpenGPS.SteerCal
{
    public interface IAsistenteDireccionMotor
    {
        /// <summary>
        /// Avanza el asistente con la foto actual y dice si tiene que mover el
        /// motor. Se llama en cada PGN 254; tiene que ser rápido y no tirar.
        /// </summary>
        bool QuiereMotor(out double setpointGrados, out double velocidadKmh);
    }
}
