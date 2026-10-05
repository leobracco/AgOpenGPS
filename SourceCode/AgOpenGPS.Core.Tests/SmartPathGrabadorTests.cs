// ============================================================================
// SmartPathGrabadorTests.cs — guía por última pasada ("SmartPath").
//
// Idea de la competencia (Ag Leader SmartPath, Sensor "línea guía por última
// pasada"): sin configurar nada, el operario hace la primera pasada a mano y
// al girar en la cabecera la pasada recién hecha se vuelve la guía de la
// siguiente. Estos tests fijan la parte pura: cuándo termina una pasada (giro
// de ~180° en pocos metros), qué puntos son "la pasada" (sin el giro) y que
// una curva suave del lote NO se confunda con un giro de cabecera.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class SmartPathGrabadorTests
    {
        // ---- trayectorias sintéticas (un punto cada 0,5 m) ------------------

        private sealed class Recorrido
        {
            public double E, N;
            public double Rumbo; // radianes, 0 = norte, horario
            public readonly List<vec3> Puntos = new List<vec3>();

            public Recorrido(double e, double n, double rumboDeg)
            {
                E = e; N = n; Rumbo = rumboDeg * Math.PI / 180.0;
                Puntos.Add(new vec3(E, N, Rumbo));
            }

            public Recorrido Recto(double metros, double paso = 0.5)
            {
                int n = (int)Math.Round(metros / paso);
                for (int i = 0; i < n; i++)
                {
                    E += Math.Sin(Rumbo) * paso;
                    N += Math.Cos(Rumbo) * paso;
                    Puntos.Add(new vec3(E, N, Rumbo));
                }
                return this;
            }

            /// <summary>Arco de <paramref name="grados"/> (positivo = a la derecha) con radio dado.</summary>
            public Recorrido Arco(double grados, double radio, double paso = 0.5)
            {
                double largo = Math.Abs(grados) * Math.PI / 180.0 * radio;
                int n = Math.Max(1, (int)Math.Round(largo / paso));
                double dRumbo = (grados * Math.PI / 180.0) / n;
                for (int i = 0; i < n; i++)
                {
                    Rumbo += dRumbo * 0.5;
                    E += Math.Sin(Rumbo) * (largo / n);
                    N += Math.Cos(Rumbo) * (largo / n);
                    Rumbo += dRumbo * 0.5;
                    Puntos.Add(new vec3(E, N, Rumbo));
                }
                return this;
            }
        }

        /// <summary>Alimenta el grabador y devuelve las pasadas que entregó, en orden.</summary>
        private static List<List<vec3>> Manejar(SmartPathGrabador g, IEnumerable<vec3> puntos)
        {
            var entregadas = new List<List<vec3>>();
            foreach (var p in puntos)
            {
                var pasada = g.Agregar(p.easting, p.northing);
                if (pasada != null) entregadas.Add(pasada);
            }
            return entregadas;
        }

        private static double Largo(List<vec3> pts)
        {
            double d = 0;
            for (int i = 1; i < pts.Count; i++)
                d += Math.Sqrt(Math.Pow(pts[i].easting - pts[i - 1].easting, 2)
                             + Math.Pow(pts[i].northing - pts[i - 1].northing, 2));
            return d;
        }

        // ---- tests -----------------------------------------------------------

        [Test]
        public void Recta_SinGiro_NoTerminaNingunaPasada()
        {
            var g = new SmartPathGrabador();
            var r = new Recorrido(0, 0, 0).Recto(300);

            Assert.That(Manejar(g, r.Puntos), Is.Empty);
            Assert.That(g.PasadasTerminadas, Is.EqualTo(0));
        }

        [Test]
        public void GiroEnU_EntregaLaPasadaSinElGiro_RecienAlSalirDelGiro()
        {
            var g = new SmartPathGrabador();
            // 100 m al norte, giro en U a la derecha (ancho 6 m), y vuelta al sur.
            var r = new Recorrido(0, 0, 0).Recto(100).Arco(180, 3);
            int finGiro = r.Puntos.Count;
            r.Recto(40);

            // Durante el giro no se entrega nada: la guía nueva se instala
            // cuando el tractor ya encaró la pasada siguiente.
            Assert.That(Manejar(g, r.Puntos.Take(finGiro)), Is.Empty);

            var entregadas = Manejar(g, r.Puntos.Skip(finGiro));
            Assert.That(entregadas, Has.Count.EqualTo(1));
            Assert.That(g.PasadasTerminadas, Is.EqualTo(1));

            var pasada = entregadas[0];
            // Es la pasada recta: nada del giro (que se va hasta 6 m al este).
            Assert.That(pasada.Max(p => Math.Abs(p.easting)), Is.LessThan(0.3));
            Assert.That(Largo(pasada), Is.GreaterThan(90).And.LessThan(101));
            // En el sentido en que se manejó (de sur a norte).
            Assert.That(pasada[0].northing, Is.LessThan(pasada[pasada.Count - 1].northing));
        }

        [Test]
        public void SegundaPasada_EsLaRecienHecha_SinRestosDelGiroAnterior()
        {
            var g = new SmartPathGrabador();
            var r = new Recorrido(0, 0, 0)
                .Recto(100).Arco(180, 3)      // pasada 1 al norte, gira a la derecha
                .Recto(100).Arco(-180, 3)     // pasada 2 al sur (en E=6), gira a la izquierda
                .Recto(40);                   // encara la pasada 3 al norte (E=12)

            var entregadas = Manejar(g, r.Puntos);
            Assert.That(entregadas, Has.Count.EqualTo(2));

            var segunda = entregadas[1];
            Assert.That(segunda.Min(p => p.easting), Is.GreaterThan(5.7));
            Assert.That(segunda.Max(p => p.easting), Is.LessThan(6.3));
            Assert.That(Largo(segunda), Is.GreaterThan(80));
            // Va al sur: del norte del lote hacia el sur.
            Assert.That(segunda[0].northing, Is.GreaterThan(segunda[segunda.Count - 1].northing));
        }

        [Test]
        public void GiroConFondoPlano_NoMeteElTramoDeLaCabeceraEnLaPasada()
        {
            // Implemento ancho: 90°, cruza la cabecera derecho, 90°. El tramo
            // derecho del medio NO es el fin de la pasada.
            var g = new SmartPathGrabador();
            var r = new Recorrido(0, 0, 0).Recto(100).Arco(90, 4).Recto(8).Arco(90, 4).Recto(40);

            var entregadas = Manejar(g, r.Puntos);
            Assert.That(entregadas, Has.Count.EqualTo(1));
            Assert.That(entregadas[0].Max(p => Math.Abs(p.easting)), Is.LessThan(0.3));
            Assert.That(Largo(entregadas[0]), Is.GreaterThan(90));
        }

        [Test]
        public void PasadaMuyCorta_NoGeneraGuia()
        {
            // Maniobrar en la cabecera (10 m y vuelta) no es una pasada.
            var g = new SmartPathGrabador();
            var r = new Recorrido(0, 0, 0).Recto(10).Arco(180, 3).Recto(40);

            Assert.That(Manejar(g, r.Puntos), Is.Empty);
            Assert.That(g.PasadasTerminadas, Is.EqualTo(0));
        }

        [Test]
        public void CurvaSuaveDelLote_NoSeConfundeConGiroDeCabecera()
        {
            // Una curva de nivel: 160° pero a lo largo de 280 m (radio 100 m).
            var g = new SmartPathGrabador();
            var r = new Recorrido(0, 0, 0).Recto(50).Arco(160, 100).Recto(50);

            Assert.That(Manejar(g, r.Puntos), Is.Empty);
        }

        [Test]
        public void TractorParadoConRuidoDeGps_NoInventaGiros()
        {
            var g = new SmartPathGrabador();
            var rnd = new Random(7);
            var pts = new Recorrido(0, 0, 0).Recto(60).Puntos;
            // Parado al final: 500 fixes con ±5 cm de ruido.
            for (int i = 0; i < 500; i++)
                pts.Add(new vec3(rnd.NextDouble() * 0.1 - 0.05, 60 + rnd.NextDouble() * 0.1 - 0.05, 0));
            pts.AddRange(new Recorrido(0, 60, 0).Recto(60).Puntos);

            Assert.That(Manejar(g, pts), Is.Empty);
        }

        [Test]
        public void SiNuncaSeAlinea_EntregaIgualDespuesDeUnTrecho()
        {
            // Gira 155° y sigue cruzado (25° fuera de la paralela) (no encara una pasada paralela): la
            // pasada hecha sigue siendo válida y se entrega igual.
            var g = new SmartPathGrabador();
            var r = new Recorrido(0, 0, 0).Recto(100).Arco(155, 3).Recto(100);

            var entregadas = Manejar(g, r.Puntos);
            Assert.That(entregadas, Has.Count.EqualTo(1));
            Assert.That(entregadas[0].Max(p => Math.Abs(p.easting)), Is.LessThan(0.3));
        }

        [Test]
        public void Reiniciar_DescartaLoGrabado()
        {
            var g = new SmartPathGrabador();
            var r = new Recorrido(0, 0, 0).Recto(100).Arco(180, 3);
            Manejar(g, r.Puntos);

            g.Reiniciar();
            var vuelta = new Recorrido(r.E, r.N, 180).Recto(40);

            // Lo de antes de reiniciar no existe: no hay pasada que entregar.
            Assert.That(Manejar(g, vuelta.Puntos), Is.Empty);
            Assert.That(g.PasadasTerminadas, Is.EqualTo(0));
            Assert.That(g.PuntosPasadaActual, Is.GreaterThan(0));
        }
    }
}
