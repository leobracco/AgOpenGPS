// ============================================================================
// EscalaDesvio.cs — cuántas luces se prenden, de qué lado y de qué color, para
// un desvío dado. Es la escala de las luces de banderillero.
//
// Vive acá y no en PilotX.UI porque PilotX.UI no tiene proyecto de tests y este
// proyecto sí: la regla del color y la del lado son fáciles de romper sin que se
// note en pantalla.
//
// Dos decisiones que NO son obvias leyendo el código:
//  · El color se decide en CENTÍMETROS (5/15/25) y no en cantidad de luces. Si
//    el operario cambia los cm por luz, "naranja" tiene que seguir queriendo
//    decir "te fuiste entre 15 y 25 cm", no "prendiste la tercera luz".
//  · Las luces se prenden del lado al que hay que IR, no del lado al que te
//    fuiste. Es el mismo criterio de la flecha del cluster y el de las barras
//    de banderillero de toda la vida.
// ============================================================================

using System;

namespace PilotX.Cockpit.Bars
{
    public enum NivelDesvio { Verde, Amarillo, Naranja, Rojo }

    public readonly struct LecturaDesvio
    {
        public int LucesEncendidas { get; }
        public bool HaciaLaIzquierda { get; }
        public NivelDesvio Nivel { get; }
        public double Centimetros { get; }
        /// <summary>false cuando no hay guía (XTE NaN): no se dibuja nada.</summary>
        public bool HayDato { get; }

        public LecturaDesvio(int luces, bool haciaLaIzquierda, NivelDesvio nivel,
                             double centimetros, bool hayDato)
        {
            LucesEncendidas = luces;
            HaciaLaIzquierda = haciaLaIzquierda;
            Nivel = nivel;
            Centimetros = centimetros;
            HayDato = hayDato;
        }
    }

    public static class EscalaDesvio
    {
        public const int LucesPorLado = 7;
        public const double CmPorLuzPorDefecto = 5.0;

        // Cortes en centímetros. Ver la nota de arriba: son cm, no luces.
        private const double CmAmarillo = 5.0;
        private const double CmNaranja  = 15.0;
        private const double CmRojo     = 25.0;

        public static LecturaDesvio Leer(double xteMetros, double cmPorLuz)
        {
            if (double.IsNaN(xteMetros) || double.IsInfinity(xteMetros))
                return new LecturaDesvio(0, false, NivelDesvio.Verde, 0.0, false);

            // Un valor inválido guardado en Settings no puede dividir por cero
            // ni dejar la barra muerta.
            if (cmPorLuz <= 0.0 || double.IsNaN(cmPorLuz)) cmPorLuz = CmPorLuzPorDefecto;

            double cm = Math.Abs(xteMetros) * 100.0;

            int luces = (int)Math.Floor(cm / cmPorLuz);
            if (luces > LucesPorLado) luces = LucesPorLado;

            NivelDesvio nivel = cm < CmAmarillo ? NivelDesvio.Verde
                              : cm < CmNaranja  ? NivelDesvio.Amarillo
                              : cm < CmRojo     ? NivelDesvio.Naranja
                                                : NivelDesvio.Rojo;

            // xte > 0 = el tractor está a la derecha de la línea => ir a la izquierda.
            bool haciaLaIzquierda = xteMetros > 0;

            return new LecturaDesvio(luces, haciaLaIzquierda, nivel, cm, true);
        }

        public static string ColorHex(NivelDesvio nivel)
        {
            switch (nivel)
            {
                case NivelDesvio.Verde:    return "#4ABA3E";
                case NivelDesvio.Amarillo: return "#E8C81E";
                case NivelDesvio.Naranja:  return "#F07E12";
                default:                   return "#ED4848";
            }
        }
    }
}
