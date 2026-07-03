// ============================================================================
// FirmwareOtaTests — pinnea el comportamiento de los guards anti-downgrade
// del FirmwareOtaCoordinator (IsDowngrade / TryParseLooseVersion).
//
// El override allow_downgrade se testea a nivel IsDowngrade: el bypass vive
// en SendOtaAsync, que necesita MQTT real y queda fuera de alcance acá.
// ============================================================================

using AgroParallel.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class FirmwareOtaTests
    {
        [Test]
        public void Downgrade_SeRechaza_SinOverride()
        {
            // requested (2.3.0) estrictamente menor que current (2.3.1) → es downgrade
            Assert.That(FirmwareOtaCoordinator.IsDowngrade("2.3.1", "2.3.0"), Is.True);
        }

        [Test]
        public void Upgrade_NoEsDowngrade()
        {
            Assert.That(FirmwareOtaCoordinator.IsDowngrade("2.3.0", "2.3.1"), Is.False);
        }

        [Test]
        public void MismaVersion_NoEsDowngrade()
        {
            // OJO: el plan original decía "MismaVersion_SeRechaza", pero el código
            // real (comentario H1 en FirmwareOtaCoordinator) permite re-flashear la
            // misma versión por diseño: IsDowngrade solo es true si requested es
            // ESTRICTAMENTE menor. Pinneamos el comportamiento real.
            Assert.That(FirmwareOtaCoordinator.IsDowngrade("2.3.0", "2.3.0"), Is.False);
        }

        [Test]
        public void VersionInvalida_NoExplota()
        {
            // El código tolera basura: si cualquiera de las dos versiones no parsea,
            // devuelve false (no bloquea el OTA — el caller ya logueó el error).
            Assert.DoesNotThrow(() => FirmwareOtaCoordinator.IsDowngrade("garbage", "2.3.0"));
            Assert.That(FirmwareOtaCoordinator.IsDowngrade("garbage", "2.3.0"), Is.False);
            Assert.That(FirmwareOtaCoordinator.IsDowngrade("2.3.0", "garbage"), Is.False);

            // Null/empty en cualquiera → false, sin excepción.
            Assert.That(FirmwareOtaCoordinator.IsDowngrade(null, "2.3.0"), Is.False);
            Assert.That(FirmwareOtaCoordinator.IsDowngrade("2.3.0", ""), Is.False);
        }

        [Test]
        public void TryParseLooseVersion_AceptaPrefijoVYSufijoRc()
        {
            Assert.That(FirmwareOtaCoordinator.TryParseLooseVersion("v1.2.3", out var v1), Is.True);
            Assert.That(v1, Is.EqualTo(new System.Version(1, 2, 3)));

            Assert.That(FirmwareOtaCoordinator.TryParseLooseVersion("1.2.3-rc1", out var v2), Is.True);
            Assert.That(v2, Is.EqualTo(new System.Version(1, 2, 3)));

            // Forma simple sin decoraciones
            Assert.That(FirmwareOtaCoordinator.TryParseLooseVersion("1.11.0", out var v3), Is.True);
            Assert.That(v3, Is.EqualTo(new System.Version(1, 11, 0)));
        }
    }
}
