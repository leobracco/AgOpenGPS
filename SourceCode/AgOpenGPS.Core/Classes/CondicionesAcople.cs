// ============================================================================
// CondicionesAcople.cs — por qué el piloto no se deja acoplar (distancia y
// ángulo respecto de la guía).
//
// La velocidad mínima/máxima ya la controla el módulo (setAS_min/maxSteerSpeed).
// Esto agrega lo que pide la ISO 10975 y traen los pilotos de la competencia:
// no enganchar lejos de la línea ni cruzado a ella, que es cuando la dirección
// pega el volantazo. Función PURA: la usa el motor (que decide) y su resultado
// viaja en el state para que la cabina diga el motivo.
//
// Los dos límites en 0 = apagado. Así vienen por defecto hasta validarlo en lote.
// ============================================================================

using System;
using System.Globalization;

namespace AgOpenGPS
{
    public static class CondicionesAcople
    {
        /// <summary>Valor que usa el guiado para "no hay distancia a la línea".</summary>
        private const double SinDistancia = 32000;

        private static readonly NumberFormatInfo Es = new NumberFormatInfo
        {
            // Coma decimal y punto de miles SIN depender de la cultura "es-AR":
            // PilotX.Desktop corre con InvariantGlobalization (crear es-AR ahí
            // tira CultureNotFoundException y voltea la pantalla). Mismo patrón
            // que TareaFormato.
            NumberDecimalSeparator = ",",
            NumberGroupSeparator = ".",
            NumberGroupSizes = new[] { 3 },
        };

        /// <summary>
        /// null = se puede acoplar. Si no, el motivo en castellano, con el valor
        /// medido y el límite, para que el operario sepa cuánto le falta.
        /// </summary>
        /// <param name="distanciaM">Desvío a la guía (m, con signo).</param>
        /// <param name="errorRumboDeg">Ángulo entre el tractor y la guía (°, con signo).</param>
        /// <param name="maxDistanciaM">Límite de distancia; 0 o menos = sin límite.</param>
        /// <param name="maxAnguloDeg">Límite de ángulo; 0 o menos = sin límite.</param>
        public static string PorQueNoAcopla(double distanciaM, double errorRumboDeg,
                                            double maxDistanciaM, double maxAnguloDeg)
        {
            // Sin guía calculada no hay nada que medir: ese caso lo informa el
            // requisito "no hay ninguna guía", no esta regla.
            if (double.IsNaN(distanciaM) || Math.Abs(distanciaM) >= SinDistancia) return null;

            // Primero la distancia: acercándose a la guía el ángulo suele
            // corregirse en la misma maniobra.
            double d = Math.Abs(distanciaM);
            if (maxDistanciaM > 0 && d > maxDistanciaM)
                return "Estás muy lejos de la guía para acoplar: " + Metros(d) +
                       " (máximo " + Metros(maxDistanciaM) + "). Acercate a la línea.";

            if (maxAnguloDeg > 0 && !double.IsNaN(errorRumboDeg))
            {
                double a = Math.Abs(errorRumboDeg);
                if (a > maxAnguloDeg)
                    return "Estás muy cruzado a la guía para acoplar: " + Grados(a) +
                           " (máximo " + Grados(maxAnguloDeg) + "). Enderezá el tractor.";
            }

            return null;
        }

        private static string Metros(double m) => m.ToString("0.0", Es) + " m";

        private static string Grados(double g) => Math.Round(g).ToString("0", Es) + "°";
    }
}
