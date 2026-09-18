// XteGraphSample — muestra instantánea de telemetría de guiado para el gráfico
// XTE de PilotX (reemplazo HTML del WinForms FormGraphXTE). Read-only: dos
// valores en vivo que la página grafica en un buffer rodante. Sin persistencia.

namespace AgroParallel.Models
{
    public sealed class XteGraphSample
    {
        /// <summary>Error de rumbo actual del modo de guiado, en grados.</summary>
        public double HeadingErrorDeg { get; set; }

        /// <summary>Error de seguimiento (cross-track) actual, en centímetros.</summary>
        public double XteCm { get; set; }
    }
}
