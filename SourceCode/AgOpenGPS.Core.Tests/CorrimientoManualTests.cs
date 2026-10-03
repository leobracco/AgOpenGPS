// ============================================================================
// CorrimientoManualTests.cs — candado de "Corregir posición" a mano.
//
// Correr la posición con el piloto enganchado mueve la línea bajo el piloto.
// Un toque de ±1/±10 cm es un nudge y está bien; "Poner en cero" con 2 m
// cargados es un salto de 2 m y un volantazo. Por eso: con el piloto puesto,
// nada de saltos de más de 50 cm de una vez.
// ============================================================================

using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class CorrimientoManualTests
    {
        [Test]
        public void PilotoSuelto_TodoSePuede()
        {
            Assert.That(CorrimientoManual.PorQueNoSePuede(5.0, 0.0, pilotoEnganchado: false), Is.Null);
        }

        [Test]
        public void PilotoPuesto_ToqueChico_SePuede()
        {
            Assert.That(CorrimientoManual.PorQueNoSePuede(0.10, 0.0, pilotoEnganchado: true), Is.Null);
            Assert.That(CorrimientoManual.PorQueNoSePuede(0.0, -0.50, pilotoEnganchado: true), Is.Null, "50 cm justos entra");
        }

        [Test]
        public void PilotoPuesto_SaltoGrande_NoSePuedeYDiceCuanto()
        {
            // Ej.: "Poner en cero" con 1,2 m al norte y 1,6 m al este cargados → 2 m.
            string m = CorrimientoManual.PorQueNoSePuede(-1.2, -1.6, pilotoEnganchado: true);
            Assert.That(m, Does.Contain("Desenganchá el piloto"));
            Assert.That(m, Does.Contain("2,0 m"));
        }
    }
}
