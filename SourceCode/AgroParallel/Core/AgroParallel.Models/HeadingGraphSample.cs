// HeadingGraphSample — muestra instantánea para el gráfico de rumbo de PilotX
// (reemplazo HTML del WinForms FormGraphHeading). Read-only: rumbo GPS vs rumbo
// IMU corregido, en grados. La diferencia la calcula la página del lado cliente.
// La página arma su propio buffer rodante. Sin persistencia.

namespace AgroParallel.Models
{
    public sealed class HeadingGraphSample
    {
        /// <summary>Rumbo GPS actual, en grados.</summary>
        public double GpsHeadingDeg { get; set; }

        /// <summary>Rumbo del IMU corregido (fusión), en grados.</summary>
        public double ImuHeadingDeg { get; set; }
    }
}
