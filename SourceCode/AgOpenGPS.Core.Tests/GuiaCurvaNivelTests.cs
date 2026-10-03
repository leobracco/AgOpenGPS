// ============================================================================
// GuiaCurvaNivelTests.cs — de la curva de nivel a la guía curva.
//
// El test de punta a punta usa el lote sintético: calcula el mapa, toma la
// cota bajo un punto, arma la guía y verifica contra la SUPERFICIE VERDADERA
// que cada punto de la guía está a esa altura (±8 cm, la misma tolerancia que
// las curvas en Node) — o sea, que la guía de verdad sigue el contorno.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class GuiaCurvaNivelTests
    {
        private static List<vec2> Recta(double x0, double y0, double x1, double y1, int n)
        {
            var l = new List<vec2>();
            for (int i = 0; i <= n; i++) l.Add(new vec2(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n));
            return l;
        }

        private static List<vec2> Circulo(double cx, double cy, double r, int n)
        {
            var l = new List<vec2>();
            for (int i = 0; i < n; i++) l.Add(new vec2(cx + r * Math.Cos(2 * Math.PI * i / n), cy + r * Math.Sin(2 * Math.PI * i / n)));
            l.Add(l[0]);
            return l;
        }

        [Test]
        public void ElegirLinea_LaMasCercanaALaReferencia_DescartaLasCortas()
        {
            var lejos = Recta(0, 100, 200, 100, 50);
            var cerca = Recta(0, 10, 200, 10, 50);
            var corta = Recta(0, 1, 5, 1, 5);            // 5 m: no sirve como guía
            var lineas = new List<List<vec2>> { lejos, corta, cerca };
            Assert.That(GuiaCurvaNivel.ElegirLinea(lineas, 50, 0), Is.EqualTo(2));
            Assert.That(GuiaCurvaNivel.ElegirLinea(new List<List<vec2>> { corta }, 0, 0), Is.EqualTo(-1));
            Assert.That(GuiaCurvaNivel.Preparar(new List<List<vec2>> { corta }, 0, 0), Is.Null);
        }

        [Test]
        public void Remuestrear_PasoFijoYConservaLosExtremos()
        {
            var r = GuiaCurvaNivel.Remuestrear(Recta(0, 0, 10, 0, 3), 1.5);
            Assert.That(r.First().easting, Is.EqualTo(0));
            Assert.That(r.Last().easting, Is.EqualTo(10).Within(1e-9));
            for (int i = 1; i < r.Count - 1; i++)
                Assert.That(r[i].easting - r[i - 1].easting, Is.EqualTo(1.5).Within(1e-9));
        }

        [Test]
        public void Anillo_SeAbreEnElPuntoMasCercanoAlTractor()
        {
            var anillo = Circulo(0, 0, 50, 72);
            var pts = GuiaCurvaNivel.Preparar(new List<List<vec2>> { anillo }, 0, -80);
            Assert.That(pts, Is.Not.Null);
            // arranca abajo (0, −50), que es lo más cercano a (0, −80)
            Assert.That(pts[0].easting, Is.EqualTo(0).Within(1.0));
            Assert.That(pts[0].northing, Is.EqualTo(-50).Within(1.0));
            // da la vuelta entera (~314 m) a paso ~1,5 m y no repite el inicio
            Assert.That(pts.Count, Is.InRange(200, 215));
            double cierre = Math.Sqrt(Math.Pow(pts.Last().easting - pts[0].easting, 2) + Math.Pow(pts.Last().northing - pts[0].northing, 2));
            Assert.That(cierre, Is.InRange(0.5, 2.5));
        }

        [Test]
        public void Suavizar_LimaLosQuiebresSinCorrerLosExtremos()
        {
            // serrucho de ±0,5 m sobre una recta
            var s = new List<vec2>();
            for (int i = 0; i <= 40; i++) s.Add(new vec2(i * 1.5, (i % 2 == 0 ? 0.5 : -0.5)));
            var f = GuiaCurvaNivel.Suavizar(s, 2, false);
            Assert.That(f[0].northing, Is.EqualTo(0.5));
            Assert.That(f[40].northing, Is.EqualTo(0.5));
            for (int i = 2; i < 39; i++) Assert.That(Math.Abs(f[i].northing), Is.LessThanOrEqualTo(0.11));
        }

        [Test]
        public void LoteSintetico_LaGuiaSigueLaCurvaDeNivelVerdadera()
        {
            var L = PlanimetriaSintetico.Generar();
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3 });
            // tractor en (120, 150): la cota del mapa bajo el tractor
            double x = 120, y = 150;
            double cota = PlanimetriaCabina.CotaEn(R, LatDe(L, x, y), LonDe(L, x, y));
            Assert.That(double.IsNaN(cota), Is.False);
            Assert.That(cota, Is.EqualTo(PlanimetriaSintetico.Terreno(x, y)).Within(0.05));

            var nivel = Planimetria.CurvaDeCota(R.Grilla, cota);
            // "plano local" del test = la proyección del lote (x, y del generador)
            var lineas = nivel.Lineas.Select(l => l.Select(p =>
            {
                double la, lo;
                R.CeldaALatLon(p.C, p.F, out la, out lo);
                return new vec2(L.Pr.X(lo), L.Pr.Y(la));
            }).ToList()).ToList();
            var pts = GuiaCurvaNivel.Preparar(lineas, x, y);
            Assert.That(pts, Is.Not.Null);
            Assert.That(pts.Count, Is.GreaterThan(30));
            double peor = 0;
            int n = 0;
            foreach (var p in pts)
            {
                if (p.easting < 0 || p.easting > 400 || p.northing < 0 || p.northing > 300) continue;
                peor = Math.Max(peor, Math.Abs(PlanimetriaSintetico.Terreno(p.easting, p.northing) - cota));
                n++;
            }
            TestContext.Out.WriteLine("cota " + cota.ToString("0.000") + " m, " + pts.Count + " puntos, peor " + (peor * 100).ToString("0.0") + " cm");
            Assert.That(n, Is.GreaterThan(30));
            Assert.That(peor, Is.LessThan(0.08), "la guía se aparta de la curva de nivel");
            // y pasa por el tractor (la cota se eligió ahí)
            double dmin = pts.Min(p => Math.Sqrt((p.easting - x) * (p.easting - x) + (p.northing - y) * (p.northing - y)));
            Assert.That(dmin, Is.LessThan(3.0));
        }

        private static double LatDe(LoteSintetico L, double x, double y)
        {
            double la, lo;
            L.ALatLon(x, y, out la, out lo);
            return la;
        }

        private static double LonDe(LoteSintetico L, double x, double y)
        {
            double la, lo;
            L.ALatLon(x, y, out la, out lo);
            return lo;
        }
    }
}
