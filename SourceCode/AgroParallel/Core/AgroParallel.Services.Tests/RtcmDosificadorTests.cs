// ============================================================================
// RtcmDosificadorTests.cs — el RTCM hacia el AiO (UDP 2233) sale dosificado.
//
// Antes se cortaba en pedazos de 256 B pero se mandaban TODOS seguidos: cuando
// el caster vuelve después de un corte de internet descarga de golpe lo que
// tenía acumulado, y la Teensy (buffer NTRIP de 1023 B) se ahoga y el
// receptor queda sin correcciones unos segundos (mismo problema que arregló
// AgOpenWeb, RtcmPacer, issue #169). Ahora: cola con tope, 256 B por tick.
// ============================================================================

using System.Linq;
using AgroParallel.CoreXBridge;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class RtcmDosificadorTests
    {
        private static byte[] Bytes(int n, byte inicio = 0)
            => Enumerable.Range(0, n).Select(i => (byte)(inicio + i)).ToArray();

        [Test]
        public void SinDatos_NoHayNadaQueMandar()
        {
            var d = new RtcmDosificador();
            Assert.That(d.Siguiente(), Is.Null);
        }

        [Test]
        public void UnTickManda_ComoMucho_UnPedazo()
        {
            var d = new RtcmDosificador(tamPaquete: 256, topeBytes: 10240);
            d.Encolar(Bytes(600));

            Assert.That(d.Siguiente().Length, Is.EqualTo(256));
            Assert.That(d.Siguiente().Length, Is.EqualTo(256));
            Assert.That(d.Siguiente().Length, Is.EqualTo(88));
            Assert.That(d.Siguiente(), Is.Null);
        }

        [Test]
        public void MantieneElOrden()
        {
            var d = new RtcmDosificador(tamPaquete: 4, topeBytes: 1000);
            d.Encolar(new byte[] { 1, 2, 3 });
            d.Encolar(new byte[] { 4, 5, 6 });
            Assert.That(d.Siguiente(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(d.Siguiente(), Is.EqualTo(new byte[] { 5, 6 }));
        }

        [Test]
        public void SiSePasaDelTope_TiraLoMasViejo()
        {
            // Una corrección vieja no sirve: si se acumula de más (vuelve el
            // caster con una ráfaga), se descarta lo más viejo y queda lo nuevo.
            var d = new RtcmDosificador(tamPaquete: 256, topeBytes: 1000);
            d.Encolar(Bytes(800, 0));
            d.Encolar(Bytes(600, 100));

            Assert.That(d.Pendientes, Is.EqualTo(1000));
            Assert.That(d.Descartados, Is.EqualTo(400));
            // El primer byte que sale es el 400 del primer bloque (los 0..399 se tiraron).
            Assert.That(d.Siguiente()[0], Is.EqualTo((byte)144)); // (0 + 400) % 256
        }

        [Test]
        public void UnBloqueMasGrandeQueElTope_SeQuedaConElFinal()
        {
            var d = new RtcmDosificador(tamPaquete: 256, topeBytes: 300);
            d.Encolar(Bytes(1000));
            Assert.That(d.Pendientes, Is.EqualTo(300));
            Assert.That(d.Descartados, Is.EqualTo(700));
        }
    }
}
