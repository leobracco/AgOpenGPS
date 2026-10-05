// CorrectionGraphSample — muestra instantánea para el gráfico de chequeo de roll
// de PilotX (reemplazo HTML del WinForms FormCorrection). Read-only: distancia de
// corrección por roll, easting crudo del GPS y easting sin corregir, más el roll
// del IMU en grados. La página arma su propio buffer rodante. Sin persistencia.

namespace AgroParallel.Models
{
    public sealed class CorrectionGraphSample
    {
        /// <summary>Distancia de corrección aplicada por el roll del IMU, en metros.</summary>
        public double CorrectionDistance { get; set; }

        /// <summary>Easting actual del fix GPS (ya corregido), en metros.</summary>
        public double Easting { get; set; }

        /// <summary>Easting del fix GPS sin corregir por roll, en metros.</summary>
        public double UncorrectedEasting { get; set; }

        /// <summary>Roll del IMU en grados. Sólo válido si RollPresent es true.</summary>
        public double RollDegrees { get; set; }

        /// <summary>true si hay un IMU presente reportando roll.</summary>
        public bool RollPresent { get; set; }
    }
}
