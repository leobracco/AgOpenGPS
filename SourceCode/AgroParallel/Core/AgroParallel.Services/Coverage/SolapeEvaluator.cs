// ============================================================================
// SolapeEvaluator.cs — decidir si una sección corta sobre área ya trabajada.
//
// Fase 3a del anti-overlap. CoverageGeometry dice cuánto solapa y CoverageIndex
// lo responde rápido sobre el lote entero; acá está la DECISIÓN: con ese número,
// ¿la sección tiene que quedar encendida o apagada?
//
// Está aparte y como función pura a propósito. Es la regla que decide si se
// vuelve a sembrar sobre lo sembrado, y equivocarse cuesta plata en las dos
// direcciones:
//   · apagar de más  -> salteos, franjas sin sembrar
//   · apagar de menos -> doble siembra, semilla tirada
// Poder fijarla con tests vale más que ahorrarse una clase.
//
// Dos ideas que no son obvias:
//
// 1) HISTÉRESIS. Con un solo umbral, una sección que va bordeando el límite
//    prende y apaga muchas veces por segundo (el famoso "chattering"): castiga
//    los solenoides y deja el borde sucio. Por eso hay DOS umbrales, y entre
//    ambos se mantiene el estado actual.
//
// 2) DOS DISTANCIAS. No se mira solo dónde está la sección ahora: se mira
//    adelante. Para APAGAR se usa la distancia de apagado (lo que la máquina
//    tarda en cerrar), y para ENCENDER la de encendido. Así el corte cae donde
//    corresponde y no unos metros tarde. Las dos consultas salen del mismo
//    transform (ver CoverageIndex.Consultar con umbralY).
// ============================================================================

using System;

namespace AgroParallel.Coverage
{
    /// <summary>Qué se sabe de una sección al momento de decidir.</summary>
    public struct SolapeInput
    {
        /// <summary>Fracción ya trabajada donde estaría la sección al APAGAR
        /// (look-ahead de apagado). 0..1.</summary>
        public double SolapeApagado;

        /// <summary>Fracción ya trabajada donde estaría la sección al ENCENDER
        /// (look-ahead de encendido). 0..1.</summary>
        public double SolapeEncendido;

        /// <summary>Estado actual, para la histéresis.</summary>
        public bool EstabaEncendida;

        /// <summary>Por encima de esto se considera "ya trabajado" y se apaga.</summary>
        public double UmbralApagar;

        /// <summary>Por debajo de esto se considera "sin trabajar" y se enciende.</summary>
        public double UmbralEncender;
    }

    /// <summary>
    /// Regla de encendido/apagado por solape. Pura: mismas entradas, misma
    /// salida, sin estado propio.
    /// </summary>
    public static class SolapeEvaluator
    {
        /// <summary>
        /// Cubierta al 90% o más = ya trabajada. No se usa 100% porque el borde
        /// de los triángulos nunca calza exacto con el borde de la sección y
        /// siempre queda algún centímetro sin contar.
        /// </summary>
        public const double UmbralApagarPorDefecto = 0.90;

        /// <summary>
        /// Por debajo del 70% se considera que hay bastante sin sembrar como
        /// para justificar encender. Entre 70% y 90% manda la histéresis.
        /// </summary>
        public const double UmbralEncenderPorDefecto = 0.70;

        /// <summary>Input con los umbrales por defecto ya puestos.</summary>
        public static SolapeInput ConDefaults(
            double solapeApagado, double solapeEncendido, bool estabaEncendida)
        {
            return new SolapeInput
            {
                SolapeApagado   = solapeApagado,
                SolapeEncendido = solapeEncendido,
                EstabaEncendida = estabaEncendida,
                UmbralApagar    = UmbralApagarPorDefecto,
                UmbralEncender  = UmbralEncenderPorDefecto,
            };
        }

        /// <summary>
        /// ¿La sección tiene que estar encendida?
        ///
        /// Esta función decide SOLO por solape. El resto de las condiciones
        /// (botón en Off/Manual, velocidad mínima, marcha atrás, fuera de
        /// lindero) las resuelve el llamador ANTES y ni siquiera pregunta acá.
        /// </summary>
        public static bool RequeridaOn(SolapeInput e)
        {
            // Umbrales sanos aunque vengan mal cargados: si alguien los deja en
            // cero o invertidos, la regla tiene que seguir siendo determinista.
            double apagar = Clamp01(e.UmbralApagar);
            double encender = Clamp01(e.UmbralEncender);
            if (encender > apagar) encender = apagar;

            double sApagado = Clamp01(e.SolapeApagado);
            double sEncendido = Clamp01(e.SolapeEncendido);

            // Zona ya trabajada por delante -> apagar.
            if (sApagado >= apagar) return false;

            // Zona limpia por delante -> encender.
            if (sEncendido <= encender) return true;

            // Zona gris: no tocar nada. Es lo que evita el chattering en el
            // borde de la pasada anterior.
            return e.EstabaEncendida;
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v)) return 0;
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}
