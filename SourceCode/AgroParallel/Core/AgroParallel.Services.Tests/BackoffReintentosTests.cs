// ============================================================================
// BackoffReintentosTests.cs — el espaciado de reintentos del heartbeat.
//
// Sin esto, con el cloud devolviendo 502 (o cortando el SSL) el heartbeat
// salía cada 30 s para siempre: llenaba orbitx_sync.log y cada intento se comía
// hasta 30 s de timeout adentro del tick de sync, retrasando la subida de lotes.
// ============================================================================

using System;
using AgroParallel.Services.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class BackoffReintentosTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 19, 5, 0, 0, DateTimeKind.Utc);

        private static BackoffReintentos Nuevo()
        {
            return new BackoffReintentos(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        }

        [Test]
        public void SinFallos_SePuedeIntentarSiempre()
        {
            var b = Nuevo();

            Assert.That(b.PuedeIntentar(T0), Is.True);
            Assert.That(b.FallosConsecutivos, Is.EqualTo(0));
        }

        [Test]
        public void UnFallo_BloqueaHastaQuePasaLaEspera()
        {
            var b = Nuevo();

            var espera = b.RegistrarFallo(T0);

            Assert.That(espera, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(b.PuedeIntentar(T0.AddSeconds(29)), Is.False);
            Assert.That(b.PuedeIntentar(T0.AddSeconds(30)), Is.True);
        }

        // 30s → 1min → 2min → 4min: el server caído no se arregla porque le
        // peguemos más seguido.
        [Test]
        public void FallosSeguidos_DuplicanLaEspera()
        {
            var b = Nuevo();

            Assert.That(b.RegistrarFallo(T0), Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(b.RegistrarFallo(T0), Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(b.RegistrarFallo(T0), Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(b.RegistrarFallo(T0), Is.EqualTo(TimeSpan.FromSeconds(240)));
        }

        // El tope importa: un tractor que estuvo dos horas sin señal tiene que
        // reengancharse en minutos, no en horas.
        [Test]
        public void LaEsperaNoPasaDelTope()
        {
            var b = Nuevo();

            for (int i = 0; i < 40; i++) b.RegistrarFallo(T0);

            Assert.That(b.EsperaActual, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(b.PuedeIntentar(T0.AddMinutes(5)), Is.True);
        }

        [Test]
        public void UnExito_VuelveAlRitmoNormal()
        {
            var b = Nuevo();
            b.RegistrarFallo(T0);
            b.RegistrarFallo(T0);

            b.RegistrarExito();

            Assert.That(b.PuedeIntentar(T0), Is.True);
            Assert.That(b.FallosConsecutivos, Is.EqualTo(0));
            Assert.That(b.EsperaActual, Is.EqualTo(TimeSpan.Zero));
            // Y el siguiente fallo arranca de nuevo desde la espera chica.
            Assert.That(b.RegistrarFallo(T0), Is.EqualTo(TimeSpan.FromSeconds(30)));
        }

        [Test]
        public void EsperaInicialInvalida_CaeEn30Segundos()
        {
            var b = new BackoffReintentos(TimeSpan.Zero, TimeSpan.FromMinutes(5));

            Assert.That(b.RegistrarFallo(T0), Is.EqualTo(TimeSpan.FromSeconds(30)));
        }

        [Test]
        public void TopeMenorQueLaEsperaInicial_NoRompe()
        {
            var b = new BackoffReintentos(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10));

            Assert.That(b.RegistrarFallo(T0), Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(b.RegistrarFallo(T0), Is.EqualTo(TimeSpan.FromSeconds(60)));
        }
    }
}
