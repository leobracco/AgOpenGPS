// ============================================================================
// PintadoAlAbrirValvulaTests.cs — el mapa pinta cuando la valvula ABRE.
//
// El reporte de campo: "abre las secciones pero no pinta, y deja el mapa sin
// pintar". La maquina aplicaba bien; lo que mentia era el mapa.
//
// POR QUE PASABA. En el bucle de secciones:
//   · la valvula abre en el INSTANTE del pedido;
//   · pero el pedido se levanta ANTICIPADO, porque el anti-solape consulta un
//     punto proyectado lookAheadOn segundos adelante;
//   · y el mapa esperaba ese mismo lookAheadOn para empezar a pintar.
// O sea que el mapa pintaba cuando el implemento LLEGABA al punto proyectado,
// bastante despues de que la maquina hubiera empezado a aplicar.
//
// Y lo perverso: los dos coinciden SOLO si lookAheadOn es exactamente el
// retardo fisico de la maquina. Como el look-ahead se sube de mas a proposito
// —para no dejar huecos de aplicacion, que es el ajuste prudente— resulta que
// CUANTO MEJOR se configura la valvula, MAS TARDE pinta el mapa.
//
// El arreglo del 2026-09-17 separo el retardo del mapa del de la valvula, pero
// lo dejo en -1 ("seguir el look-ahead") para no cambiarle la conducta a nadie
// al actualizar. Efecto real: el arreglo quedaba apagado de fabrica y el
// problema seguia, porque nadie sabia que habia que prenderlo. El 2026-09-22 el
// default paso a 0.
//
// Estos tests fijan esa decision. Si alguien vuelve a poner -1 de fabrica, el
// reporte de campo vuelve con el.
// ============================================================================

using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class PintadoAlAbrirValvulaTests
    {
        /// <summary>La regla del bucle de secciones, tal cual esta en
        /// GuidanceEngineHost.SectionsRuntime.cs: con retardo propio &gt;= 0 se usa
        /// ese; con -1 se cae al look-ahead de la valvula.</summary>
        private static double SegundosDePintado(double paintDelay, double lookAheadOn)
        {
            return paintDelay >= 0 ? paintDelay : lookAheadOn;
        }

        [Test]
        public void De_fabrica_el_mapa_pinta_cuando_abre_la_valvula()
        {
            var s = new AgOpenGPS.Properties.Settings();
            Assert.That(s.setVehicle_toolPaintDelay, Is.EqualTo(0),
                "De fabrica el mapa tiene que pintar al abrir la valvula. Con -1 seguia al " +
                "look-ahead y el mapa arrancaba tarde — el reporte de campo de Francisco Barbero.");
        }

        [Test]
        public void Con_el_default_el_look_ahead_de_la_valvula_ya_no_atrasa_el_mapa()
        {
            // Caso real: look-ahead de 1 s subido para no dejar huecos.
            double lookAheadOn = 1.0;
            var s = new AgOpenGPS.Properties.Settings();

            double seg = SegundosDePintado(s.setVehicle_toolPaintDelay, lookAheadOn);

            Assert.That(seg, Is.EqualTo(0),
                "el mapa no tiene que esperar el look-ahead de la valvula");
        }

        [Test]
        public void Subir_el_look_ahead_para_no_dejar_huecos_ya_no_empeora_el_mapa()
        {
            // El nucleo del problema: antes, cuanto MEJOR se configuraba la
            // valvula (look-ahead mas grande, sin huecos de aplicacion), PEOR
            // pintaba el mapa. Ahora las dos cosas son independientes.
            var s = new AgOpenGPS.Properties.Settings();
            double conPoco  = SegundosDePintado(s.setVehicle_toolPaintDelay, 0.5);
            double conMucho = SegundosDePintado(s.setVehicle_toolPaintDelay, 3.0);

            Assert.That(conPoco, Is.EqualTo(conMucho),
                "el retardo del mapa no puede depender de como este ajustada la valvula");
        }

        [Test]
        public void Menos_uno_sigue_sirviendo_para_volver_al_comportamiento_viejo()
        {
            // La salida de emergencia se conserva: quien haya ajustado su equipo
            // contando con la conducta historica puede volver a ella.
            Assert.That(SegundosDePintado(-1, 2.5), Is.EqualTo(2.5));
        }

        [Test]
        public void Un_retardo_propio_se_respeta_tal_cual()
        {
            // Maquinas con un retardo fisico real (la valvula abre y el producto
            // tarda en salir) pueden declararlo y el mapa lo espera.
            Assert.That(SegundosDePintado(0.4, 2.5), Is.EqualTo(0.4));
        }
    }
}
