// Tests del monitor de "fuente muda" (GPS, PGN 253, máquina, IMU, nodos).
// La regla que se fija acá: UNA línea cuando la fuente se calla y UNA cuando
// vuelve, con la duración del corte. Nunca una línea por segundo, y nada de
// avisos por fuentes que nunca hablaron (un equipo sin módulo de máquina no
// tiene que llenar el registro de "máquina muda").

using System;
using System.Linq;
using AgroParallel.Diagnostico;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class MonitorFuentesMudasTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        private static MonitorFuentesMudas NuevoConGps()
        {
            var m = new MonitorFuentesMudas();
            m.Registrar("gps", "GPS (NMEA)", TimeSpan.FromSeconds(2));
            return m;
        }

        [Test]
        public void FuenteNuncaVista_NoAvisa()
        {
            var m = NuevoConGps();
            for (int s = 0; s < 60; s++)
                Assert.That(m.Observar("gps", null, T0.AddSeconds(s)), Is.Null);
            Assert.That(m.EventosRecientes(), Is.Empty);
            Assert.That(m.Estado().Single().Vista, Is.False);
        }

        [Test]
        public void FuenteViva_NoAvisa()
        {
            var m = NuevoConGps();
            for (int s = 0; s < 30; s++)
            {
                var t = T0.AddSeconds(s);
                Assert.That(m.Observar("gps", t, t), Is.Null);
            }
            Assert.That(m.EventosRecientes(), Is.Empty);
        }

        [Test]
        public void SeCalla_AvisaUnaSolaVez()
        {
            var m = NuevoConGps();
            m.Observar("gps", T0, T0);

            // 1,5 s sin datos: todavía dentro del umbral.
            Assert.That(m.Observar("gps", T0, T0.AddSeconds(1.5)), Is.Null);

            var ev = m.Observar("gps", T0, T0.AddSeconds(3));
            Assert.That(ev, Is.Not.Null);
            Assert.That(ev.Volvio, Is.False);
            Assert.That(ev.Clave, Is.EqualTo("gps"));
            Assert.That(ev.Desde, Is.EqualTo(T0));
            Assert.That(ev.Mensaje, Does.Contain("GPS (NMEA)"));
            Assert.That(ev.Mensaje, Does.Contain("muda"));

            // Sigue callada un minuto más: ni una línea extra.
            for (int s = 4; s < 60; s++)
                Assert.That(m.Observar("gps", T0, T0.AddSeconds(s)), Is.Null);
            Assert.That(m.EventosRecientes().Count, Is.EqualTo(1));
        }

        [Test]
        public void Vuelve_AvisaConDuracionDelCorte()
        {
            var m = NuevoConGps();
            m.Observar("gps", T0, T0);
            m.Observar("gps", T0, T0.AddSeconds(5));          // se calla

            var vuelta = T0.AddMilliseconds(37400);
            var ev = m.Observar("gps", vuelta, vuelta);
            Assert.That(ev, Is.Not.Null);
            Assert.That(ev.Volvio, Is.True);
            Assert.That(ev.Duracion, Is.EqualTo(TimeSpan.FromMilliseconds(37400)));
            Assert.That(ev.Mensaje, Does.Contain("volvió"));
            Assert.That(ev.Mensaje, Does.Contain("37 s"));

            // Viva otra vez: no repite.
            var t = vuelta.AddSeconds(1);
            Assert.That(m.Observar("gps", t, t), Is.Null);

            var est = m.Estado().Single();
            Assert.That(est.Muda, Is.False);
            Assert.That(est.Cortes, Is.EqualTo(1));
            Assert.That(est.TiempoMudo, Is.EqualTo(TimeSpan.FromMilliseconds(37400)));
        }

        [Test]
        public void DosCortes_SeCuentanPorSeparado()
        {
            var m = NuevoConGps();
            m.Observar("gps", T0, T0);
            m.Observar("gps", T0, T0.AddSeconds(3));
            m.Observar("gps", T0.AddSeconds(10), T0.AddSeconds(10));
            m.Observar("gps", T0.AddSeconds(10), T0.AddSeconds(20));
            m.Observar("gps", T0.AddSeconds(25), T0.AddSeconds(25));

            var est = m.Estado().Single();
            Assert.That(est.Cortes, Is.EqualTo(2));
            Assert.That(est.TiempoMudo, Is.EqualTo(TimeSpan.FromSeconds(25)));
            Assert.That(m.EventosRecientes().Count, Is.EqualTo(4));
        }

        [Test]
        public void ClaveNoRegistrada_SeIgnora()
        {
            var m = NuevoConGps();
            Assert.That(m.Observar("otra", T0, T0.AddSeconds(30)), Is.Null);
            Assert.That(m.Estado().Count, Is.EqualTo(1));
        }

        [Test]
        public void ObservarEstado_ParaNodosSinTimestampUtil()
        {
            // Los nodos MQTT traen Online (LWT del broker): se vigila el bool.
            var m = new MonitorFuentesMudas();
            Assert.That(m.ObservarEstado("nodo:A1", "Nodo QuantiX A1", true, T0, T0), Is.Null);

            var callada = m.ObservarEstado("nodo:A1", "Nodo QuantiX A1", false, T0.AddSeconds(2), T0.AddSeconds(16));
            Assert.That(callada, Is.Not.Null);
            Assert.That(callada.Volvio, Is.False);

            var vuelve = m.ObservarEstado("nodo:A1", "Nodo QuantiX A1", true, T0.AddSeconds(62), T0.AddSeconds(62));
            Assert.That(vuelve, Is.Not.Null);
            Assert.That(vuelve.Volvio, Is.True);
            // Desde el último visto vivo (T0) hasta que volvió (T0+62).
            Assert.That(vuelve.Duracion, Is.EqualTo(TimeSpan.FromSeconds(62)));
        }

        [Test]
        public void EventosRecientes_TieneTope()
        {
            var m = new MonitorFuentesMudas(maxEventos: 4);
            m.Registrar("gps", "GPS", TimeSpan.FromSeconds(1));
            var t = T0;
            m.Observar("gps", t, t);
            for (int i = 0; i < 10; i++)
            {
                var visto = t;
                t = t.AddSeconds(5);
                m.Observar("gps", visto, t);   // se calla
                m.Observar("gps", t, t);       // vuelve
            }
            Assert.That(m.EventosRecientes().Count, Is.EqualTo(4));
            Assert.That(m.Estado().Single().Cortes, Is.EqualTo(10));
        }

        [Test]
        public void Resumen_ListaFuentesYEventos()
        {
            var m = NuevoConGps();
            m.Registrar("maquina", "Módulo de máquina", TimeSpan.FromSeconds(5));
            m.Observar("gps", T0, T0);
            m.Observar("gps", T0, T0.AddSeconds(4));

            string r = m.Resumen(T0.AddSeconds(4));
            Assert.That(r, Does.Contain("GPS (NMEA)"));
            Assert.That(r, Does.Contain("MUDA"));
            Assert.That(r, Does.Contain("Módulo de máquina"));
            Assert.That(r, Does.Contain("nunca habló"));
        }

        [TestCase(2.26, "2.3 s")]
        [TestCase(37.4, "37 s")]
        [TestCase(125, "2 min 05 s")]
        [TestCase(3725, "1 h 02 min")]
        public void FormatearDuracion_Legible(double seg, string esperado)
        {
            Assert.That(MonitorFuentesMudas.FormatearDuracion(TimeSpan.FromSeconds(seg)), Is.EqualTo(esperado));
        }
    }
}
