// ============================================================================
// CorrimientoManual.cs — cuándo NO se deja correr la posición a mano
// ("Corregir posición": ±cm por eje y "Poner en cero").
//
// Correr la posición mueve el tractor en el mapa y, con él, su distancia a la
// guía. Con el piloto enganchado un salto grande se convierte en un volantazo.
// Los toques chicos (±1, ±10 cm) sí se dejan: corregir la deriva andando es un
// uso válido, como el nudge. "Volver a la referencia" tiene sus propios
// candados (ver GuidanceEngineHost.Deriva).
// ============================================================================

using System;
using System.Globalization;

namespace AgOpenGPS
{
    public static class CorrimientoManual
    {
        /// <summary>Salto máximo de una vez con el piloto enganchado (m).</summary>
        public const double MaxSaltoConPilotoM = 0.50;

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

        /// <summary>null = se puede. Si no, el motivo para la pantalla.</summary>
        /// <param name="saltoNorteM">Cambio pedido en el norte (m).</param>
        /// <param name="saltoEsteM">Cambio pedido en el este (m).</param>
        public static string PorQueNoSePuede(double saltoNorteM, double saltoEsteM, bool pilotoEnganchado)
        {
            if (!pilotoEnganchado) return null;
            double salto = Math.Sqrt(saltoNorteM * saltoNorteM + saltoEsteM * saltoEsteM);
            if (salto <= MaxSaltoConPilotoM + 1e-9) return null;
            return "Desenganchá el piloto antes de mover la posición " + salto.ToString("0.0", Es) +
                   " m de una vez: el piloto pegaría un volantazo. Con el piloto puesto se puede corregir de a " +
                   (MaxSaltoConPilotoM * 100).ToString("0", Es) + " cm.";
        }
    }
}
