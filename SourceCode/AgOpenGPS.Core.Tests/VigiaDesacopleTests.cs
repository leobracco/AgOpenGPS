// ============================================================================
// VigiaDesacopleTests.cs — cuándo el piloto se DESENGANCHA solo.
//
// El motor solo corre cuando llega un fix: si el GPS se corta del todo, nadie
// apaga el piloto. El módulo deja de mover el volante por su watchdog, pero el
// piloto queda "puesto" y al volver la señal ENGANCHA SOLO, de golpe. El vigía
// corre aparte (por reloj) y lo desengancha si pasa demasiado sin posición, o
// si el tractor se va de la guía más de lo permitido.
// ============================================================================

using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class VigiaDesacopleTests
    {
        private static VigiaDesacople Vigia(double sinPosicionSeg = 3, double maxDistanciaM = 2)
            => new VigiaDesacople { MaxSinPosicionSeg = sinPosicionSeg, MaxDistanciaM = maxDistanciaM };

        [Test]
        public void ApagadoEnCero_NuncaDesengancha()
        {
            var v = Vigia(0, 0);
            Assert.That(v.Evaluar(ahoraSeg: 100, pilotoPuesto: true, ultimoFixSeg: 10, distanciaM: 50), Is.Null);
        }

        [Test]
        public void PilotoApagado_NoHayNadaQueCortar()
        {
            var v = Vigia();
            Assert.That(v.Evaluar(100, pilotoPuesto: false, ultimoFixSeg: 10, distanciaM: 50), Is.Null);
        }

        [Test]
        public void SinPosicionMasDelLimite_Desengancha()
        {
            var v = Vigia(sinPosicionSeg: 3);
            Assert.That(v.Evaluar(12.0, true, ultimoFixSeg: 10.0, distanciaM: 0), Is.Null, "2 s sin fix todavía no");
            string m = v.Evaluar(13.5, true, ultimoFixSeg: 10.0, distanciaM: 0);
            Assert.That(m, Does.Contain("GPS"));
        }

        [Test]
        public void NuncaHuboFix_NoSeCuentaComoPerdida()
        {
            // Sin fix desde el arranque el piloto ni siquiera pudo engancharse;
            // un ultimoFix "vacío" no es una pérdida de 3 horas.
            var v = Vigia();
            Assert.That(v.Evaluar(500, true, ultimoFixSeg: double.NaN, distanciaM: 0), Is.Null);
        }

        [Test]
        public void LejosDeLaGuia_DesenganchaRecienDespuesDeLaGracia()
        {
            var v = Vigia(maxDistanciaM: 2);
            Assert.That(v.Evaluar(10.0, true, 10.0, distanciaM: -2.5), Is.Null, "un pico no corta");
            Assert.That(v.Evaluar(10.5, true, 10.5, distanciaM: -2.6), Is.Null);
            string m = v.Evaluar(11.1, true, 11.1, distanciaM: -2.7);
            Assert.That(m, Does.Contain("2,7 m"));
        }

        [Test]
        public void VuelveALaGuia_ReiniciaLaGracia()
        {
            var v = Vigia(maxDistanciaM: 2);
            v.Evaluar(10.0, true, 10.0, 3.0);
            v.Evaluar(10.8, true, 10.8, 0.5);   // volvió
            Assert.That(v.Evaluar(11.2, true, 11.2, 3.0), Is.Null, "la gracia arranca de nuevo");
        }

        [Test]
        public void SinGuiaCalculada_LaDistanciaNoCuenta()
        {
            var v = Vigia(maxDistanciaM: 2);
            v.Evaluar(10.0, true, 10.0, 32000);
            Assert.That(v.Evaluar(15.0, true, 15.0, 32000), Is.Null);
        }

        [Test]
        public void DespuesDeCortar_NoRepiteHastaQueSeVuelvaAPrender()
        {
            var v = Vigia(sinPosicionSeg: 3);
            Assert.That(v.Evaluar(20, true, 10, 0), Is.Not.Null);
            // El motor tarda un ciclo en reflejar el corte: no se dispara dos veces.
            Assert.That(v.Evaluar(20.25, true, 10, 0), Is.Null);
            v.Evaluar(21, false, 21, 0);        // apagado
            Assert.That(v.Evaluar(30, true, 21, 0), Is.Not.Null, "prendido de nuevo, vuelve a vigilar");
        }
    }
}
