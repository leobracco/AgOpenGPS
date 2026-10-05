// ============================================================================
// CondicionesAcopleTests.cs — cuándo el piloto NO se deja acoplar.
//
// Idea tomada de la competencia (T-Wave de Sensor): además de la velocidad,
// el piloto exige estar cerca de la guía y más o menos alineado. Acoplar a
// 4 m de la línea o cruzado a 70° hace que la dirección pegue un volantazo.
// Los dos límites vienen APAGADOS (0) por defecto: es un cambio que restringe
// cuándo engancha el piloto y se valida en lote antes de prenderlo.
// ============================================================================

using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class CondicionesAcopleTests
    {
        [Test]
        public void ApagadasPorDefecto_SiempreAcopla()
        {
            Assert.That(CondicionesAcople.PorQueNoAcopla(25.0, 80.0, maxDistanciaM: 0, maxAnguloDeg: 0), Is.Null);
        }

        [Test]
        public void DentroDeLosLimites_Acopla()
        {
            Assert.That(CondicionesAcople.PorQueNoAcopla(0.8, 12.0, maxDistanciaM: 1.5, maxAnguloDeg: 30), Is.Null);
        }

        [Test]
        public void LejosDeLaGuia_NoAcoplaYDiceCuanto()
        {
            string m = CondicionesAcople.PorQueNoAcopla(-3.2, 5.0, maxDistanciaM: 1.5, maxAnguloDeg: 30);
            Assert.That(m, Does.Contain("3,2 m"));
            Assert.That(m, Does.Contain("1,5 m"));
        }

        [Test]
        public void CruzadoALaGuia_NoAcoplaYDiceElAngulo()
        {
            string m = CondicionesAcople.PorQueNoAcopla(0.2, -48.0, maxDistanciaM: 1.5, maxAnguloDeg: 30);
            Assert.That(m, Does.Contain("48°"));
            Assert.That(m, Does.Contain("30°"));
        }

        [Test]
        public void LasDosFallan_InformaPrimeroLaDistancia()
        {
            // La distancia se arregla primero: acercándose a la guía el ángulo
            // suele corregirse solo en la misma maniobra.
            string m = CondicionesAcople.PorQueNoAcopla(5.0, 60.0, maxDistanciaM: 1.5, maxAnguloDeg: 30);
            Assert.That(m, Does.Contain("lejos"));
        }

        [Test]
        public void SinGuiaCalculada_NoBloquea()
        {
            // 32000 es el "no hay distancia" que usa el guiado (CABCurve).
            Assert.That(CondicionesAcople.PorQueNoAcopla(32000, 0, maxDistanciaM: 1.5, maxAnguloDeg: 30), Is.Null);
            Assert.That(CondicionesAcople.PorQueNoAcopla(double.NaN, double.NaN, maxDistanciaM: 1.5, maxAnguloDeg: 30), Is.Null);
        }
    }
}
