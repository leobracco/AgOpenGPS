// ============================================================================
// TurnMarksTests.cs — geometría pura de "Marcar giro" (CTurnMarks).
//
// Dos marcas perpendiculares a la guía definen un rectángulo virtual que hace
// de línea de giro para el U-turn cuando NO hay lindero; con lindero, cada
// marca RECORTA la turnLine real con un semiplano. Acá se prueban las dos
// cuentas sin host ni estado.
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class TurnMarksTests
    {
        // Guía apuntando al norte (heading 0). Marca A en n=0, marca B en n=100.
        private static TurnMark MarkA() => new TurnMark { easting = 0, northing = 0, heading = 0 };
        private static TurnMark MarkB() => new TurnMark { easting = 0, northing = 100, heading = 0 };

        [Test]
        public void RectanguloVirtual_ContieneElCentro_YNoLosExtremos()
        {
            var ring = CTurnMarks.BuildVirtualFence(MarkA(), MarkB(), 500);
            Assert.That(ring.Count, Is.GreaterThanOrEqualTo(5)); // 4 esquinas + cierre
            Assert.That(ring.IsPointInPolygon(new vec3(0, 50, 0)), Is.True);   // centro adentro
            Assert.That(ring.IsPointInPolygon(new vec3(0, -10, 0)), Is.False); // atrás de A afuera
            Assert.That(ring.IsPointInPolygon(new vec3(0, 110, 0)), Is.False); // adelante de B afuera
            Assert.That(ring.IsPointInPolygon(new vec3(499, 50, 0)), Is.True); // ancho lateral
        }

        [Test]
        public void RecorteConSemiplano_AchicaElAnillo_DelLadoCorrecto()
        {
            // Cuadrado 0..100 y una marca horizontal en n=60: me quedo con n<60.
            var ring = new List<vec3> {
                new vec3(0,0,0), new vec3(100,0,0), new vec3(100,100,0),
                new vec3(0,100,0), new vec3(0,0,0) };
            var recortado = CTurnMarks.ClipRingWithHalfPlane(
                ring, new vec3(0, 60, 0), lineHeading: Math.PI / 2, // línea este-oeste
                keepSidePoint: new vec2(50, 10));
            Assert.That(recortado.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(recortado.IsPointInPolygon(new vec3(50, 50, 0)), Is.True);
            Assert.That(recortado.IsPointInPolygon(new vec3(50, 70, 0)), Is.False);
        }

        [Test]
        public void RecorteQueVaciaElPoligono_DevuelveVacio()
        {
            var ring = new List<vec3> {
                new vec3(0,0,0), new vec3(10,0,0), new vec3(10,10,0),
                new vec3(0,10,0), new vec3(0,0,0) };
            var r = CTurnMarks.ClipRingWithHalfPlane(
                ring, new vec3(0, -50, 0), Math.PI / 2, new vec2(5, -100));
            Assert.That(r, Is.Empty);
        }
    }
}
