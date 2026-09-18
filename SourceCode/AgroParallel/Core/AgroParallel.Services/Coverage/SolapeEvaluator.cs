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

        /// <summary>Separación entre el umbral de apagar y el de encender
        /// (la histéresis). Con 20 puntos una sección que bordea la pasada
        /// anterior no titila.</summary>
        public const double HisteresisPorDefecto = 0.20;

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
        /// Umbral de apagado a partir de "Cobertura mínima" (%) de la config de
        /// secciones (setVehicle_minCoverage): cuánto del ancho de la sección
        /// tiene que estar ya sembrado para que corte. Hasta la 1.0.67 el
        /// campo se guardaba pero nadie lo leía y el corte usaba 90 fijo.
        ///   · 100 (el default histórico, cuando el campo no hacía nada) se lee
        ///     como el 90 de siempre: no cambia el corte de nadie por el solo
        ///     hecho de actualizar.
        ///   · El resto se acota a 50..95: por debajo de 50 corta con media
        ///     sección limpia (salteos), y 100 exacto nunca se alcanza porque
        ///     el borde de los triángulos no calza con el de la sección.
        /// </summary>
        public static double UmbralApagarDesdeCobertura(int minCoveragePct)
        {
            if (minCoveragePct >= 100) return UmbralApagarPorDefecto;
            double apagar = minCoveragePct / 100.0;
            if (apagar < 0.50) apagar = 0.50;
            if (apagar > 0.95) apagar = 0.95;
            return apagar;
        }

        /// <summary>Input con los umbrales derivados de "Cobertura mínima" (%):
        /// apagar = cobertura, encender = cobertura − histéresis.</summary>
        public static SolapeInput ConCobertura(
            double solapeApagado, double solapeEncendido, bool estabaEncendida, int minCoveragePct)
        {
            double apagar = UmbralApagarDesdeCobertura(minCoveragePct);
            return new SolapeInput
            {
                SolapeApagado   = solapeApagado,
                SolapeEncendido = solapeEncendido,
                EstabaEncendida = estabaEncendida,
                UmbralApagar    = apagar,
                // Redondeado: 0.70 − 0.20 da 0.4999… y el "≤" del umbral fallaba justo en 0.50.
                UmbralEncender  = Math.Round(apagar - HisteresisPorDefecto, 4),
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

            // Cada estado mira SOLO la distancia que le corresponde, como el
            // rango de píxeles que elige upstream según isSectionOn:
            //   · encendida: mira a la distancia de APAGADO. Se apaga cuando lo
            //     que viene ahí ya está trabajado; si no, sigue.
            //   · apagada: mira a la distancia de ENCENDIDO. Prende cuando lo
            //     que viene ahí está limpio; entre umbrales se queda apagada.
            // Hasta la 1.0.67 se preguntaba primero por la distancia de apagado
            // sin importar el estado: como esa distancia es más corta que la de
            // encendido, una sección apagada no podía prender hasta que la línea
            // CORTA pisara terreno limpio y arrancaba (on − off) segundos tarde
            // en cada reentrada (≈1 m a 8 km/h con los defaults 1,0 / 0,5).
            if (e.EstabaEncendida) return sApagado < apagar;
            // Apagada: además de estar por debajo del umbral de encender, lo que
            // viene no puede contar ya como trabajado (con umbral de apagar 0,
            // TODO cuenta como trabajado y no prende nunca).
            return sEncendido <= encender && sEncendido < apagar;
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
