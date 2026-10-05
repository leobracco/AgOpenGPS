// ============================================================================
// LocalPlaneDerivaTests.cs — dónde se aplica DriftCompensation.
//
// La deriva se suma a la posición del GPS al ENTRAR al plano local (el fix),
// no a la geometría: linderos, guías y lo pintado quedan donde están y es el
// tractor el que se corre sobre ellos. La conversión inversa (plano → lat/lon)
// es geometría pura: si también sumara la deriva, el PGN de posición
// corregida y las exportaciones la contarían dos veces.
// ============================================================================

using AgOpenGPS.Core.Models;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests.Models
{
    [TestFixture]
    public class LocalPlaneDerivaTests
    {
        private static readonly Wgs84 Origen = new Wgs84(-33.0, -61.0);

        [Test]
        public void ElFixIncluyeLaDeriva()
        {
            var props = new SharedFieldProperties();
            var plano = new LocalPlane(Origen, props);
            var punto = new Wgs84(-33.001, -61.002);

            GeoCoord sin = plano.ConvertWgs84ToGeoCoord(punto);
            props.DriftCompensation = new GeoDelta(0.40, -0.25);
            GeoCoord fix = plano.ConvertWgs84ToFixGeoCoord(punto);

            Assert.That(fix.Northing, Is.EqualTo(sin.Northing + 0.40).Within(1e-9));
            Assert.That(fix.Easting, Is.EqualTo(sin.Easting - 0.25).Within(1e-9));
        }

        [Test]
        public void LaGeometriaNoSeCorre()
        {
            // Importar un lindero (KML/ISOXML) es geometría: no lleva deriva.
            var props = new SharedFieldProperties { DriftCompensation = new GeoDelta(3, 3) };
            var plano = new LocalPlane(Origen, props);
            var punto = new Wgs84(-33.001, -61.002);
            var conDeriva = plano.ConvertWgs84ToGeoCoord(punto);

            props.DriftCompensation = new GeoDelta(0, 0);
            var sinDeriva = plano.ConvertWgs84ToGeoCoord(punto);

            Assert.That(conDeriva.Northing, Is.EqualTo(sinDeriva.Northing));
            Assert.That(conDeriva.Easting, Is.EqualTo(sinDeriva.Easting));
        }

        [Test]
        public void IdaYVueltaEsIdentidadConDeriva()
        {
            var props = new SharedFieldProperties { DriftCompensation = new GeoDelta(1.5, -2.0) };
            var plano = new LocalPlane(Origen, props);
            var punto = new Wgs84(-33.0123, -61.0456);

            Wgs84 vuelta = plano.ConvertGeoCoordToWgs84(plano.ConvertWgs84ToGeoCoord(punto));

            Assert.That(vuelta.Latitude, Is.EqualTo(punto.Latitude).Within(1e-9));
            Assert.That(vuelta.Longitude, Is.EqualTo(punto.Longitude).Within(1e-9));
        }
    }
}
