// ============================================================================
// IAntiSolapeSecciones.cs — costura para el corte por área ya trabajada.
//
// Fase 3b del anti-overlap. El cálculo vive en AgroParallel.Services
// (CoverageGeometry / CoverageIndex / SolapeEvaluator), pero ESE proyecto
// arrastra MQTTnet, NetTopologySuite y System.Text.Json. Referenciarlo desde el
// core de guiado por un cálculo geométrico es cargarle al motor un montón de
// dependencias que no tienen nada que ver, y sacarlas después es mucho más
// difícil que no ponerlas.
//
// Por eso la dependencia va al revés: acá se declara QUÉ se necesita, sin
// depender de nada, y el proyecto de afuera (PilotX.GuidanceEngine, que ya
// referencia AgroParallel.Services para EngineCoverageService) inyecta la
// implementación.
//
// Si nadie la inyecta, GuidanceEngineHost.AntiSolape queda null y el
// comportamiento es exactamente el de antes: en Auto la sección va encendida.
// ============================================================================

namespace AgOpenGPS
{
    /// <summary>
    /// Decide si una sección tiene que seguir aplicando según cuánto de su ancho
    /// cae sobre área ya trabajada.
    /// </summary>
    public interface IAntiSolapeSecciones
    {
        /// <summary>
        /// Interruptor en caliente. En false el motor ni consulta: se comporta
        /// como antes de que esto existiera. Empieza apagado a propósito —
        /// esto decide si una sección siembra o no, y hasta validarlo en el lote
        /// el default seguro es no tocar nada.
        /// </summary>
        bool Habilitado { get; }

        /// <summary>
        /// Pone al día la cobertura conocida. Se llama una vez por fix, antes de
        /// decidir. La implementación lee las tiras del host y agrega SOLO lo
        /// nuevo: reconstruir el índice entero en cada fix no escala.
        /// </summary>
        void Sincronizar();

        /// <summary>
        /// ¿La sección tiene que quedar encendida?
        ///
        /// Los extremos van en coordenadas del mundo y <paramref name="heading"/>
        /// en radianes con la convención de AOG (0 = Norte, horario).
        /// <paramref name="estabaEncendida"/> es para la histéresis: sin eso, una
        /// sección que va rozando el borde de la pasada anterior prende y apaga
        /// varias veces por segundo.
        /// </summary>
        bool SeccionRequeridaOn(
            double izqE, double izqN,
            double derE, double derN,
            double heading,
            double velocidadKmh,
            bool estabaEncendida);

        /// <summary>
        /// Olvida todo lo conocido. Va al abrir o cerrar un lote y al resetear la
        /// cobertura: si no, el corte del lote nuevo arrastraría el área del
        /// anterior.
        /// </summary>
        void Reiniciar();
    }
}
