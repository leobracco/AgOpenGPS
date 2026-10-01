// ============================================================================
// VigiaDesacople.cs — desenganche automático del piloto (ISO 10975, idea del
// T-Wave de Sensor): sin posición por demasiado tiempo, o lejos de la guía.
//
// Lo corre el motor por RELOJ, no por fix: si el GPS se corta del todo no
// llegan fixes y el pipeline no corre. El módulo frena el volante por su
// watchdog, pero el piloto queda "puesto" y al volver la señal engancharía
// solo, de golpe. Este vigía lo desengancha.
//
// Sin reloj propio (recibe "ahora" en segundos) para poder testearlo.
// Límites en 0 = apagado.
// ============================================================================

using System;
using System.Globalization;

namespace AgOpenGPS
{
    public sealed class VigiaDesacople
    {
        /// <summary>Valor que usa el guiado para "no hay distancia a la línea".</summary>
        private const double SinDistancia = 32000;

        private static readonly CultureInfo Es = new CultureInfo("es-AR");

        /// <summary>Segundos sin fix tolerados con el piloto puesto. 0 = no vigila.</summary>
        public double MaxSinPosicionSeg { get; set; }

        /// <summary>Distancia a la guía tolerada con el piloto puesto (m). 0 = no vigila.</summary>
        public double MaxDistanciaM { get; set; }

        /// <summary>Cuánto tiempo seguido tiene que estar lejos para cortar: un
        /// pico de GPS o un bache no desengancha.</summary>
        public double GraciaDistanciaSeg { get; set; } = 1.0;

        private double _lejosDesdeSeg = double.NaN;
        private bool _yaCorto;

        /// <summary>
        /// Evalúa el estado actual. Devuelve el motivo para desenganchar (una sola
        /// vez por enganche) o null.
        /// </summary>
        /// <param name="ahoraSeg">Reloj monótono, en segundos.</param>
        /// <param name="ultimoFixSeg">Momento del último fix en el mismo reloj; NaN = nunca hubo.</param>
        /// <param name="distanciaM">Desvío a la guía (m, con signo).</param>
        public string Evaluar(double ahoraSeg, bool pilotoPuesto, double ultimoFixSeg, double distanciaM)
        {
            if (!pilotoPuesto)
            {
                _yaCorto = false;
                _lejosDesdeSeg = double.NaN;
                return null;
            }

            if (_yaCorto) return null;

            if (MaxSinPosicionSeg > 0 && !double.IsNaN(ultimoFixSeg))
            {
                double sinFix = ahoraSeg - ultimoFixSeg;
                if (sinFix > MaxSinPosicionSeg)
                    return Cortar("Piloto desenganchado: sin señal de GPS hace " +
                                  sinFix.ToString("0", Es) + " s.");
            }

            bool hayDistancia = !double.IsNaN(distanciaM) && Math.Abs(distanciaM) < SinDistancia;
            if (MaxDistanciaM > 0 && hayDistancia && Math.Abs(distanciaM) > MaxDistanciaM)
            {
                if (double.IsNaN(_lejosDesdeSeg)) _lejosDesdeSeg = ahoraSeg;
                if (ahoraSeg - _lejosDesdeSeg >= GraciaDistanciaSeg)
                    return Cortar("Piloto desenganchado: el tractor se fue " +
                                  Math.Abs(distanciaM).ToString("0.0", Es) + " m de la guía (máximo " +
                                  MaxDistanciaM.ToString("0.0", Es) + " m).");
            }
            else
            {
                _lejosDesdeSeg = double.NaN;
            }

            return null;
        }

        private string Cortar(string motivo)
        {
            _yaCorto = true;
            _lejosDesdeSeg = double.NaN;
            return motivo;
        }
    }
}
