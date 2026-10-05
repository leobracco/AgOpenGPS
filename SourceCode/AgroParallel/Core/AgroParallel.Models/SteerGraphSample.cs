// SteerGraphSample — muestra instantánea para el gráfico de dirección de PilotX
// (reemplazo HTML del WinForms FormGraphSteer). Read-only: ángulo de dirección
// real (WAS) vs ángulo seteado por el guiado, en grados. La página arma su
// propio buffer rodante. Sin persistencia.

namespace AgroParallel.Models
{
    public sealed class SteerGraphSample
    {
        /// <summary>Ángulo de dirección real medido (WAS), en grados.</summary>
        public double ActualSteerDeg { get; set; }

        /// <summary>Ángulo de dirección que ordena el guiado, en grados.</summary>
        public double SetSteerDeg { get; set; }
    }
}
