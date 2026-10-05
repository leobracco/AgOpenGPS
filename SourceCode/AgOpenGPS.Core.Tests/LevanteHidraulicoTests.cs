// ============================================================================
// LevanteHidraulicoTests.cs — cuándo el motor manda SUBIR o BAJAR el levante.
//
// En AOG la decisión salía del scan de píxeles del dibujo (oglBack); el motor
// headless no dibuja y SetHydPosition no se llamaba nunca: el levante se
// configuraba pero la placa de máquina recibía siempre 0. Ahora es
// geométrico (CBoundary.DecidirLevante):
//   · BAJA apenas la punta anticipada de cualquiera de las dos esquinas del
//     implemento entra al área de trabajo (anticipación = tiempo × velocidad);
//   · SUBE cuando las dos esquinas Y sus puntas anticipadas están en cabecera.
// ============================================================================

using System;
using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class LevanteHidraulicoTests
    {
        [Test]
        public void TodoEnCabecera_Sube()
        {
            Assert.That(CBoundary.ImplementoEnCabecera(true, false, false), Is.True);
        }

        [Test]
        public void UnaPuntaYaEntraAlLote_Baja()
        {
            // Saliendo del giro: las esquinas siguen en cabecera, pero la punta
            // anticipada de un lado ya está sobre el área de trabajo → bajar ya,
            // para que el implemento llegue abajo justo al borde.
            Assert.That(CBoundary.ImplementoEnCabecera(true, true, false), Is.False);
            Assert.That(CBoundary.ImplementoEnCabecera(true, false, true), Is.False);
        }

        [Test]
        public void UnaEsquinaTodaviaEnElLote_NoSube()
        {
            // Entrando a la cabecera: mientras una esquina siga trabajando, no
            // se levanta (quedaría un pedazo sin sembrar).
            Assert.That(CBoundary.ImplementoEnCabecera(false, false, false), Is.False);
        }

        [Test]
        public void PuntaAnticipada_SeProyectaHaciaAdelanteEnElRumbo()
        {
            // Rumbo 0 = norte: 5 m adelante es +5 en el northing.
            var p = CBoundary.PuntaAnticipada(new vec2(10, 20), 0.0, 5.0);
            Assert.That(p.easting, Is.EqualTo(10).Within(1e-9));
            Assert.That(p.northing, Is.EqualTo(25).Within(1e-9));

            // Rumbo 90° = este.
            var q = CBoundary.PuntaAnticipada(new vec2(10, 20), Math.PI / 2, 5.0);
            Assert.That(q.easting, Is.EqualTo(15).Within(1e-9));
            Assert.That(q.northing, Is.EqualTo(20).Within(1e-9));
        }
    }
}
