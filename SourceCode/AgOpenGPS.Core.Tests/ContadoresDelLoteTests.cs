// ============================================================================
// ContadoresDelLoteTests.cs — al cerrar el lote (o abrir uno sin Sections.txt)
// el área y la distancia vuelven a cero.
//
// Bug: el motor no ponía en cero Fd.workedAreaTotal (ni el área neta, ni la
// distancia) al cerrar el lote. CargarCobertura solo los recalcula si el lote
// nuevo TIENE Sections.txt, así que un lote nuevo arrancaba mostrando las HA
// del anterior — y la grilla neta (CoberturaNeta) seguía llena: al primer
// cuadrilátero pintado el "Neta" volvía con las hectáreas del otro lote.
// AOG lo hacía en JobClose (fd.workedAreaTotal = 0 + UpdateFieldBoundaryGUIAreas).
//
// ContadoresDelLote vive en PilotX.GuidanceEngine.Core (net9) y se compila
// linkeado acá junto con CoberturaNeta: es lo mismo que llama el host.
// ============================================================================

using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class ContadoresDelLoteTests
    {
        private static CFieldData FdConTrabajo()
        {
            return new CFieldData(null)
            {
                workedAreaTotal = 123456,
                workedAreaTotalUser = 120000,
                distanceUser = 4321,
                actualAreaCovered = 110000,
                overlapPercent = 12.5,
                barPercent = 40,
                areaOuterBoundary = 500000,
                areaBoundaryOuterLessInner = 480000,
            };
        }

        private static CoberturaNeta NetaConTrabajo()
        {
            var neta = new CoberturaNeta(0.5);
            // Un cuadrado de 10 x 10 m pintado.
            neta.MarcarTriangulo(0, 0, 10, 0, 0, 10);
            neta.MarcarTriangulo(10, 0, 0, 10, 10, 10);
            Assert.That(neta.AreaM2, Is.GreaterThan(90), "precondición: la grilla tiene área");
            return neta;
        }

        [Test]
        public void Reiniciar_PoneEnCeroElAreaYLaDistancia()
        {
            var fd = FdConTrabajo();

            ContadoresDelLote.Reiniciar(fd, NetaConTrabajo());

            Assert.That(fd.workedAreaTotal, Is.EqualTo(0), "HA trabajadas (barra de arriba)");
            Assert.That(fd.workedAreaTotalUser, Is.EqualTo(0));
            Assert.That(fd.distanceUser, Is.EqualTo(0));
            Assert.That(fd.actualAreaCovered, Is.EqualTo(0), "área neta");
            Assert.That(fd.overlapPercent, Is.EqualTo(0));
            Assert.That(fd.barPercent, Is.EqualTo(0));
        }

        [Test]
        public void Reiniciar_PoneEnCeroLasAreasDelLindero()
        {
            // Sin lote no hay lindero: el "faltan X ha" no puede quedar con el
            // lindero del anterior. Al abrir, BuildTurnLines lo recalcula.
            var fd = FdConTrabajo();

            ContadoresDelLote.Reiniciar(fd, NetaConTrabajo());

            Assert.That(fd.areaOuterBoundary, Is.EqualTo(0));
            Assert.That(fd.areaBoundaryOuterLessInner, Is.EqualTo(0));
        }

        [Test]
        public void Reiniciar_VaciaLaGrillaNeta_ParaQueNoResucite()
        {
            var fd = FdConTrabajo();
            var neta = NetaConTrabajo();

            ContadoresDelLote.Reiniciar(fd, neta);

            Assert.That(neta.AreaM2, Is.EqualTo(0));
            Assert.That(neta.Bloques, Is.EqualTo(0));

            // Lo que pinta el lote nuevo cuenta solo lo suyo.
            neta.MarcarTriangulo(1000, 1000, 1002, 1000, 1000, 1002);
            Assert.That(neta.AreaM2, Is.LessThan(5));
        }

        [Test]
        public void Reiniciar_ToleraNulos()
        {
            // El cierre del lote nunca puede tirar por esto.
            Assert.DoesNotThrow(() => ContadoresDelLote.Reiniciar(null, null));
            Assert.DoesNotThrow(() => ContadoresDelLote.Reiniciar(FdConTrabajo(), null));
        }
    }
}
