// ============================================================================
// ReferenciaDerivaTests.cs — "Punto de referencia contra la deriva".
//
// Idea tomada del T-Wave de Sensor: con GPS sin corrección la posición se
// corre decenas de cm (o metros) entre una sesión y otra. El operario clava
// una bandera, toca "Marcar referencia"; al volver pone el tractor sobre la
// bandera y toca "Volver a la referencia". La cuenta que mueve TODO el mapa
// se fija acá, no arriba de una sembradora.
//
// Convención (la misma que DriftCompensation desde FormShiftPos): el
// corrimiento se SUMA a la posición del GPS. posición_mapa = gps + deriva.
// ============================================================================

using System;
using AgOpenGPS;
using AgOpenGPS.Core.Models;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class ReferenciaDerivaTests
    {
        private const double Tol = 1e-9;

        [Test]
        public void SinDerivaPrevia_LaCorreccionEsReferenciaMenosActual()
        {
            var r = ReferenciaDeriva.CalcularVolver(
                referencia: new GeoCoord(100.0, 200.0),
                posicionActual: new GeoCoord(100.30, 199.88),
                derivaActual: new GeoDelta(0, 0));

            Assert.That(r.Aplicable, Is.True);
            Assert.That(r.Motivo, Is.Null);
            Assert.That(r.Correccion.NorthingDelta, Is.EqualTo(-0.30).Within(Tol));
            Assert.That(r.Correccion.EastingDelta, Is.EqualTo(0.12).Within(Tol));
            Assert.That(r.NuevaDeriva.NorthingDelta, Is.EqualTo(-0.30).Within(Tol));
            Assert.That(r.NuevaDeriva.EastingDelta, Is.EqualTo(0.12).Within(Tol));
        }

        [Test]
        public void ConDerivaPrevia_SeSumaALaExistente()
        {
            // La posición actual YA trae aplicada la deriva previa (es la del
            // mapa): la corrección nueva se acumula, no la reemplaza.
            var r = ReferenciaDeriva.CalcularVolver(
                referencia: new GeoCoord(100.0, 200.0),
                posicionActual: new GeoCoord(100.30, 199.88),
                derivaActual: new GeoDelta(0.10, -0.05));

            Assert.That(r.NuevaDeriva.NorthingDelta, Is.EqualTo(0.10 - 0.30).Within(Tol));
            Assert.That(r.NuevaDeriva.EastingDelta, Is.EqualTo(-0.05 + 0.12).Within(Tol));
        }

        [Test]
        public void DespuesDeAplicar_ElPivoteQuedaSobreLaReferencia()
        {
            // posición_mapa = gps + deriva. Con la deriva nueva, el mismo GPS
            // tiene que caer exactamente sobre la referencia.
            var gps = new GeoCoord(5000.42, -1234.77);
            var derivaVieja = new GeoDelta(-0.8, 1.3);
            var referencia = new GeoCoord(5001.00, -1236.10);

            var r = ReferenciaDeriva.CalcularVolver(referencia, gps + derivaVieja, derivaVieja);
            var mapaNuevo = gps + r.NuevaDeriva;

            Assert.That(mapaNuevo.Northing, Is.EqualTo(referencia.Northing).Within(1e-9));
            Assert.That(mapaNuevo.Easting, Is.EqualTo(referencia.Easting).Within(1e-9));
        }

        [Test]
        public void VolverDosVeces_LaSegundaNoCorrigeNada()
        {
            var referencia = new GeoCoord(10, 10);
            var deriva0 = new GeoDelta(0, 0);
            var actual = new GeoCoord(10.5, 9.7);

            var r1 = ReferenciaDeriva.CalcularVolver(referencia, actual, deriva0);
            var r2 = ReferenciaDeriva.CalcularVolver(referencia, actual + r1.Correccion, r1.NuevaDeriva);

            Assert.That(r2.Correccion.Length, Is.EqualTo(0).Within(1e-12));
            Assert.That(r2.NuevaDeriva.NorthingDelta, Is.EqualTo(r1.NuevaDeriva.NorthingDelta).Within(Tol));
            Assert.That(r2.NuevaDeriva.EastingDelta, Is.EqualTo(r1.NuevaDeriva.EastingDelta).Within(Tol));
        }

        [Test]
        public void MasDe50m_NoSeAplicaYDiceCuanto()
        {
            var r = ReferenciaDeriva.CalcularVolver(
                new GeoCoord(0, 0), new GeoCoord(60, 0), new GeoDelta(0.2, 0.3));

            Assert.That(r.Aplicable, Is.False);
            Assert.That(r.Motivo, Does.Contain("60 m"));
            Assert.That(r.Motivo, Does.Contain("50 m"));
            // La deriva queda como estaba: no se toca nada.
            Assert.That(r.NuevaDeriva.NorthingDelta, Is.EqualTo(0.2).Within(Tol));
            Assert.That(r.NuevaDeriva.EastingDelta, Is.EqualTo(0.3).Within(Tol));
        }

        [Test]
        public void Justo50m_SeAplica()
        {
            var r = ReferenciaDeriva.CalcularVolver(
                new GeoCoord(0, 0), new GeoCoord(0, 50), new GeoDelta(0, 0));
            Assert.That(r.Aplicable, Is.True);
        }

        [Test]
        public void ValoresInvalidos_NoSeAplica()
        {
            var r = ReferenciaDeriva.CalcularVolver(
                new GeoCoord(double.NaN, 0), new GeoCoord(1, 1), new GeoDelta(0, 0));
            Assert.That(r.Aplicable, Is.False);
            Assert.That(r.Motivo, Is.Not.Null.And.Not.Empty);
        }

        // ── Cómo se le dice al operario ─────────────────────────────────────

        [Test]
        public void Describir_CmConDireccion()
        {
            Assert.That(ReferenciaDeriva.DescribirCorreccion(new GeoDelta(-0.30, 0.12)),
                Is.EqualTo("30 cm al sur y 12 cm al este"));
            Assert.That(ReferenciaDeriva.DescribirCorreccion(new GeoDelta(0.05, -0.40)),
                Is.EqualTo("5 cm al norte y 40 cm al oeste"));
        }

        [Test]
        public void Describir_UnSoloEje()
        {
            Assert.That(ReferenciaDeriva.DescribirCorreccion(new GeoDelta(0.0, -0.07)),
                Is.EqualTo("7 cm al oeste"));
        }

        [Test]
        public void Describir_MetrosDesde1m()
        {
            Assert.That(ReferenciaDeriva.DescribirCorreccion(new GeoDelta(1.254, 0.0)),
                Is.EqualTo("1,25 m al norte"));
        }

        [Test]
        public void Describir_Nada()
        {
            Assert.That(ReferenciaDeriva.DescribirCorreccion(new GeoDelta(0.002, -0.004)),
                Is.EqualTo("menos de 1 cm"));
        }

        // ── Archivo del lote ────────────────────────────────────────────────

        [Test]
        public void Archivo_IdaYVuelta()
        {
            var p = new PuntoReferencia(
                northing: 123.456789, easting: -987.654321,
                latitude: -33.1234567, longitude: -61.7654321,
                marcadoUtc: new DateTime(2026, 10, 1, 15, 30, 0, DateTimeKind.Utc));

            string txt = ReferenciaDeriva.Serializar(p);
            Assert.That(ReferenciaDeriva.TryParsear(txt, out var q), Is.True);
            Assert.That(q.Northing, Is.EqualTo(p.Northing).Within(1e-6));
            Assert.That(q.Easting, Is.EqualTo(p.Easting).Within(1e-6));
            Assert.That(q.Latitude, Is.EqualTo(p.Latitude).Within(1e-9));
            Assert.That(q.Longitude, Is.EqualTo(p.Longitude).Within(1e-9));
            Assert.That(q.MarcadoUtc, Is.EqualTo(p.MarcadoUtc));
            Assert.That(q.MarcadoUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
        }

        [Test]
        public void Archivo_ConComaDecimalNoSeRompe()
        {
            // El archivo siempre se escribe con punto, sea cual sea la cultura.
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-AR");
                var p = new PuntoReferencia(1.5, 2.5, -33.5, -61.5, DateTime.UtcNow);
                string txt = ReferenciaDeriva.Serializar(p);
                Assert.That(txt, Does.Contain("northing=1.5"));
                Assert.That(ReferenciaDeriva.TryParsear(txt, out var q), Is.True);
                Assert.That(q.Northing, Is.EqualTo(1.5));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("basura")]
        [TestCase("northing=1\neasting=abc")]
        [TestCase("easting=1")]
        public void Archivo_InvalidoNoSeCarga(string txt)
        {
            Assert.That(ReferenciaDeriva.TryParsear(txt, out var p), Is.False);
            Assert.That(p, Is.Null);
        }
    }
}
