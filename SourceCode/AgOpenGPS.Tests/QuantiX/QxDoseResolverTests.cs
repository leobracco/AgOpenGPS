using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgOpenGPS.Tests.QuantiX
{
    public class QxDoseResolverTests
    {
        // Helper: resuelve con CampoDosis vacío y un lookup que nunca aplica.
        private static double Resolve(
            bool manualMode, double manualDosis, double dosisFija,
            string campoDosis, double mapaGlobal,
            double campoLookup = 0)
        {
            return QxDoseResolver.Resolve(
                manualMode, manualDosis, dosisFija, campoDosis, mapaGlobal,
                _ => campoLookup);
        }

        [Test]
        public void ManualMode_overrides_everything()
        {
            double d = Resolve(true, 99, 60, "", 70);
            Assert.That(d, Is.EqualTo(99));
        }

        [Test]
        public void MapaGlobal_manda_sobre_DosisFija()
        {
            double d = Resolve(false, 0, 60, "", 70);
            Assert.That(d, Is.EqualTo(70));
        }

        [Test]
        public void Cae_a_DosisFija_cuando_no_hay_mapa()
        {
            double d = Resolve(false, 0, 60, "", 0);
            Assert.That(d, Is.EqualTo(60));
        }

        [Test]
        public void CampoDosis_especifico_manda_sobre_fija()
        {
            double d = Resolve(false, 0, 60, "DOSIS_A", 0, campoLookup: 55);
            Assert.That(d, Is.EqualTo(55));
        }

        [Test]
        public void Sin_mapa_ni_fija_da_cero()
        {
            double d = Resolve(false, 0, 0, "", 0);
            Assert.That(d, Is.EqualTo(0));
        }

        [Test]
        public void ManualDosisCero_para_el_motor()
        {
            // Operario pone el widget manual en 0 = parar el motor: NO cae al mapa ni a la fija.
            double d = Resolve(true, 0, 60, "", 70);
            Assert.That(d, Is.EqualTo(0));
        }

        [Test]
        public void CampoDosis_sin_valor_cae_a_fija()
        {
            // El campo DBF existe pero la zona no tiene dosis (lookup=0) → cae a la fija.
            double d = Resolve(false, 0, 60, "DOSIS_A", 0, campoLookup: 0);
            Assert.That(d, Is.EqualTo(60));
        }
    }
}
