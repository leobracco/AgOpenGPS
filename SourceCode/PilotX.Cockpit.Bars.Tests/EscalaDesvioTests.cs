// ============================================================================
// EscalaDesvioTests.cs — la escala de las luces de banderillero.
//
// Dos reglas que se rompen fácil y por eso están cubiertas una por una:
//  · el color se decide en CENTÍMETROS (5/15/25), no en cantidad de luces —
//    si el operario cambia los cm por luz, "naranja" tiene que seguir
//    significando "te fuiste 15-25 cm";
//  · las luces se prenden del lado al que hay que IR, que es el contrario al
//    lado al que te fuiste.
// ============================================================================

using NUnit.Framework;
using PilotX.Cockpit.Bars;

namespace PilotX.Cockpit.Bars.Tests
{
    public class EscalaDesvioTests
    {
        private const double Cm5 = 5.0;

        // --- cantidad de luces (default 5 cm por luz) ---

        [TestCase(0.00, 0)]
        [TestCase(0.04, 0)]
        [TestCase(0.05, 1)]
        [TestCase(0.09, 1)]
        [TestCase(0.10, 2)]
        [TestCase(0.20, 4)]
        [TestCase(0.34, 6)]
        [TestCase(0.35, 7)]
        public void CantidadDeLuces(double xte, int esperadas)
        {
            Assert.That(EscalaDesvio.Leer(xte, Cm5).LucesEncendidas, Is.EqualTo(esperadas));
        }

        [Test]
        public void MasAlladeLaEscala_TopeEnSiete()
        {
            Assert.That(EscalaDesvio.Leer(3.0, Cm5).LucesEncendidas, Is.EqualTo(7));
        }

        // --- color, por centímetros ---

        [TestCase(0.00, NivelDesvio.Verde)]
        [TestCase(0.049, NivelDesvio.Verde)]
        [TestCase(0.05, NivelDesvio.Amarillo)]
        [TestCase(0.149, NivelDesvio.Amarillo)]
        [TestCase(0.15, NivelDesvio.Naranja)]
        [TestCase(0.249, NivelDesvio.Naranja)]
        [TestCase(0.25, NivelDesvio.Rojo)]
        [TestCase(1.00, NivelDesvio.Rojo)]
        public void NivelPorCentimetros(double xte, NivelDesvio esperado)
        {
            Assert.That(EscalaDesvio.Leer(xte, Cm5).Nivel, Is.EqualTo(esperado));
        }

        // El color NO puede depender de la escala: con 10 cm por luz, 20 cm
        // son 2 luces en vez de 4, pero siguen siendo naranja.
        [Test]
        public void ElColorNoCambiaAlCambiarLosCmPorLuz()
        {
            var conCinco = EscalaDesvio.Leer(0.20, 5.0);
            var conDiez = EscalaDesvio.Leer(0.20, 10.0);

            Assert.That(conCinco.LucesEncendidas, Is.EqualTo(4));
            Assert.That(conDiez.LucesEncendidas, Is.EqualTo(2));
            Assert.That(conDiez.Nivel, Is.EqualTo(NivelDesvio.Naranja));
            Assert.That(conDiez.Nivel, Is.EqualTo(conCinco.Nivel));
        }

        // --- lado ---

        // Desviado a la DERECHA de la línea (xte > 0) => corregir a la izquierda.
        // Mismo criterio que la flecha del cluster (MainWindow.axaml.cs).
        [Test]
        public void DesviadoALaDerecha_PrendeLasDeLaIzquierda()
        {
            Assert.That(EscalaDesvio.Leer(0.20, Cm5).HaciaLaIzquierda, Is.True);
        }

        [Test]
        public void DesviadoALaIzquierda_PrendeLasDeLaDerecha()
        {
            Assert.That(EscalaDesvio.Leer(-0.20, Cm5).HaciaLaIzquierda, Is.False);
        }

        // --- centímetros informados ---

        [Test]
        public void CentimetrosSiempreEnPositivo()
        {
            Assert.That(EscalaDesvio.Leer(-0.23, Cm5).Centimetros, Is.EqualTo(23.0).Within(0.001));
        }

        // --- defensa ---

        [Test]
        public void SinDato_NoExplotaYNoPrendeNada()
        {
            var r = EscalaDesvio.Leer(double.NaN, Cm5);

            Assert.That(r.HayDato, Is.False);
            Assert.That(r.LucesEncendidas, Is.EqualTo(0));
            Assert.That(r.Nivel, Is.EqualTo(NivelDesvio.Verde));
        }

        [Test]
        public void ConDato_HayDatoEsVerdadero()
        {
            Assert.That(EscalaDesvio.Leer(0.10, Cm5).HayDato, Is.True);
        }

        // --- rango físico (centinela / valor colgado) ---
        //
        // CABCurve.cs manda distanceFromCurrentLinePivot = 32000 (mm, del lado
        // del host) cuando la curva quedó vacía pero sigue "válida"; acá el
        // XTE llega en metros, así que usamos un valor bien fuera de rango
        // (320 m) en vez del centinela real convertido, para no atarnos a la
        // conversión de unidades del host.

        [Test]
        public void Xte320m_FueraDeRangoFisico_SinDatoYCeroLuces()
        {
            var r = EscalaDesvio.Leer(320.0, Cm5);

            Assert.That(r.HayDato, Is.False);
            Assert.That(r.LucesEncendidas, Is.EqualTo(0));
        }

        // El centinela REAL, sin convertir: CABCurve.cs (línea ~1064) asigna
        // distanceFromCurrentLinePivot = 32000 cuando la curva quedó vacía
        // pero sigue "válida", y EngineGuidanceCalculator.cs (líneas 65-67) lo
        // copia crudo a XteMeters, en la misma escala que los metros (no hay
        // conversión de unidades de por medio). Este es el valor que de verdad
        // llega a Leer() en producción, a diferencia del test de 320 m de
        // arriba, que usa un valor arbitrario fuera de rango.
        [Test]
        public void Xte32000_CentinelaDeCurvaInvalida_SinDatoYCeroLuces()
        {
            var r = EscalaDesvio.Leer(32000.0, Cm5);

            Assert.That(r.HayDato, Is.False);
            Assert.That(r.LucesEncendidas, Is.EqualTo(0));
        }

        // El umbral es "más de 100 m" (estrictamente mayor): 100 m exactos
        // todavía se procesa como un dato normal (y satura a 7 luces).
        [Test]
        public void Xte100mExacto_EnElLimite_SigueSiendoDatoValido()
        {
            Assert.That(EscalaDesvio.Leer(100.0, Cm5).HayDato, Is.True);
        }

        [Test]
        public void XteUnPocoMenosDe100m_EsDatoValido()
        {
            Assert.That(EscalaDesvio.Leer(99.9, Cm5).HayDato, Is.True);
        }

        [Test]
        public void XteUnPocoMasDe100m_YaEsSinDato()
        {
            Assert.That(EscalaDesvio.Leer(100.1, Cm5).HayDato, Is.False);
        }

        [Test]
        public void XteInfinito_SinDato()
        {
            var r = EscalaDesvio.Leer(double.PositiveInfinity, Cm5);

            Assert.That(r.HayDato, Is.False);
            Assert.That(r.LucesEncendidas, Is.EqualTo(0));
        }

        [Test]
        public void CmPorLuzNaN_CaeAlDefault()
        {
            Assert.That(EscalaDesvio.Leer(0.20, double.NaN).LucesEncendidas, Is.EqualTo(4));
        }

        // Un cm-por-luz inválido guardado en Settings no puede dividir por cero
        // ni dejar la barra muerta: cae al default.
        [TestCase(0.0)]
        [TestCase(-3.0)]
        public void CmPorLuzInvalido_CaeAlDefault(double cmPorLuz)
        {
            var r = EscalaDesvio.Leer(0.20, cmPorLuz);
            Assert.That(r.LucesEncendidas, Is.EqualTo(4));
        }

        // --- colores ---

        [Test]
        public void CadaNivelTieneSuColor()
        {
            Assert.That(EscalaDesvio.ColorHex(NivelDesvio.Verde), Is.EqualTo("#4ABA3E"));
            Assert.That(EscalaDesvio.ColorHex(NivelDesvio.Amarillo), Is.EqualTo("#E8C81E"));
            Assert.That(EscalaDesvio.ColorHex(NivelDesvio.Naranja), Is.EqualTo("#F07E12"));
            Assert.That(EscalaDesvio.ColorHex(NivelDesvio.Rojo), Is.EqualTo("#ED4848"));
        }
    }
}
