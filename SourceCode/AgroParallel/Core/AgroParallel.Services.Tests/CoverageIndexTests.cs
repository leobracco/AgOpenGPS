// El índice espacial existe por una sola razón: que la consulta entre en la CPU
// de la cabina con un lote entero encima. Pero una poda que se pierda un
// triángulo hace que la sección siembre dos veces sobre lo ya sembrado, y eso
// no se nota hasta que el lote está hecho.
//
// Por eso el test central de acá no es de performance sino de EQUIVALENCIA:
// el índice tiene que dar exactamente lo mismo que revisar todos los triángulos
// a lo bruto. La performance se mide aparte.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgroParallel.Coverage;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class CoverageIndexTests
    {
        private const double Tol = 1e-6;

        // ---- helpers -------------------------------------------------------

        /// <summary>
        /// Una pasada recta: banda de <paramref name="ancho"/> m centrada en
        /// <paramref name="ejeE"/>, desde northing 0 hasta <paramref name="largo"/>,
        /// como tira de triángulos (igual que la pinta AOG).
        /// </summary>
        private static void Pasada(CoverageIndex idx, double ejeE, double ancho, double largo, double paso = 3.0)
        {
            var es = new List<double>();
            var ns = new List<double>();
            double izq = ejeE - ancho / 2, der = ejeE + ancho / 2;
            for (double n = 0; n <= largo; n += paso)
            {
                es.Add(izq); ns.Add(n);
                es.Add(der); ns.Add(n);
            }
            idx.AgregarTira(es, ns);
        }

        /// <summary>Referencia a lo bruto: mismo cálculo sin índice.</summary>
        private static CoverageResult Fuerza(
            List<double[]> tris, double centroE, double centroN, double heading,
            double medioAncho, double umbralY)
        {
            double cos = Math.Cos(heading), sin = Math.Sin(heading);
            var tramos = new List<XInterval>();
            foreach (var t in tris)
            {
                double ax, ay, bx, by, cx, cy;
                CoverageGeometry.ATransformar(t[0], t[1], centroE, centroN, cos, sin, out ax, out ay);
                CoverageGeometry.ATransformar(t[2], t[3], centroE, centroN, cos, sin, out bx, out by);
                CoverageGeometry.ATransformar(t[4], t[5], centroE, centroN, cos, sin, out cx, out cy);
                XInterval iv;
                if (CoverageGeometry.TrianguloEnIntervalo(ax, ay, bx, by, cx, cy, umbralY, medioAncho, out iv))
                    tramos.Add(iv);
            }
            return CoverageGeometry.Resultado(tramos, medioAncho);
        }

        /// <summary>Genera triángulos y los carga en el índice y en la lista cruda.</summary>
        private static void PasadaDoble(
            CoverageIndex idx, List<double[]> crudo,
            double ejeE, double ancho, double largo, double paso = 3.0)
        {
            double izq = ejeE - ancho / 2, der = ejeE + ancho / 2;
            var es = new List<double>();
            var ns = new List<double>();
            for (double n = 0; n <= largo; n += paso)
            {
                es.Add(izq); ns.Add(n);
                es.Add(der); ns.Add(n);
            }
            idx.AgregarTira(es, ns);

            for (int i = 0; i + 2 < es.Count; i++)
            {
                double area2 = Math.Abs((es[i + 1] - es[i]) * (ns[i + 2] - ns[i])
                                      - (es[i + 2] - es[i]) * (ns[i + 1] - ns[i]));
                if (area2 < 1e-9) continue;
                crudo.Add(new[] { es[i], ns[i], es[i + 1], ns[i + 1], es[i + 2], ns[i + 2] });
            }
        }

        // ---- básicos -------------------------------------------------------

        [Test]
        public void IndiceVacio_NoHaySolape()
        {
            var idx = new CoverageIndex();
            var r = idx.Consultar(0, 0, 0, medioAncho: 2);

            Assert.That(r.HasAnyOverlap, Is.False);
            Assert.That(r.CoveragePercent, Is.EqualTo(0).Within(Tol));
        }

        [Test]
        public void SobreLaPasadaAnterior_DaCubierta()
        {
            var idx = new CoverageIndex();
            Pasada(idx, ejeE: 0, ancho: 4, largo: 100);

            // Sección de 4 m justo encima de la pasada, rumbo Norte.
            var r = idx.Consultar(0, 50, 0, medioAncho: 2);

            Assert.That(r.IsFullyCovered, Is.True, "va pisando exactamente lo ya sembrado");
            Assert.That(r.CoveragePercent, Is.EqualTo(1.0).Within(1e-3));
        }

        [Test]
        public void AlLadoDeLaPasada_NoHaySolape()
        {
            var idx = new CoverageIndex();
            Pasada(idx, ejeE: 0, ancho: 4, largo: 100);

            // Corrida 10 m al este: terreno virgen.
            var r = idx.Consultar(10, 50, 0, medioAncho: 2);

            Assert.That(r.HasAnyOverlap, Is.False);
            Assert.That(r.UncoveredLength, Is.EqualTo(4).Within(Tol));
        }

        [Test]
        public void MediaPasadaPisada_DaLaMitad()
        {
            var idx = new CoverageIndex();
            Pasada(idx, ejeE: 0, ancho: 4, largo: 100);

            // Desplazada 2 m: la mitad de la sección pisa lo viejo.
            var r = idx.Consultar(2, 50, 0, medioAncho: 2);

            Assert.That(r.CoveragePercent, Is.EqualTo(0.5).Within(1e-2));
            Assert.That(r.HasAnyOverlap, Is.True);
            Assert.That(r.IsFullyCovered, Is.False);
        }

        [Test]
        public void MasAllaDelFinal_NoHaySolape()
        {
            var idx = new CoverageIndex();
            Pasada(idx, ejeE: 0, ancho: 4, largo: 100);

            var r = idx.Consultar(0, 150, 0, medioAncho: 2);

            Assert.That(r.HasAnyOverlap, Is.False, "todavía no llegó a sembrar ahí");
        }

        [Test]
        public void LookAhead_VeAdelanteLoQueNoVeEnElLugar()
        {
            var idx = new CoverageIndex();
            // Pasada que arranca recién en northing 100.
            var es = new List<double>(); var ns = new List<double>();
            for (double n = 100; n <= 200; n += 3) { es.Add(-2); ns.Add(n); es.Add(2); ns.Add(n); }
            idx.AgregarTira(es, ns);

            // Parado en 90: acá no hay nada...
            var ahora = idx.Consultar(0, 90, 0, medioAncho: 2, umbralY: 0);
            Assert.That(ahora.HasAnyOverlap, Is.False);

            // ...pero 15 m adelante sí.
            var adelante = idx.Consultar(0, 90, 0, medioAncho: 2, umbralY: 15);
            Assert.That(adelante.IsFullyCovered, Is.True, "el look-ahead tiene que anticiparlo");
        }

        [Test]
        public void RumboEste_FuncionaIgual()
        {
            // La misma situación rotada 90°: pasada a lo largo del este.
            var idx = new CoverageIndex();
            var es = new List<double>(); var ns = new List<double>();
            for (double e = 0; e <= 100; e += 3) { es.Add(e); ns.Add(-2); es.Add(e); ns.Add(2); }
            idx.AgregarTira(es, ns);

            var r = idx.Consultar(50, 0, Math.PI / 2, medioAncho: 2);

            Assert.That(r.IsFullyCovered, Is.True, "rotar el mundo no puede cambiar el resultado");
        }

        // ---- equivalencia con fuerza bruta ---------------------------------

        [Test]
        public void LaPodaNoSePierdeNingunSolape()
        {
            var idx = new CoverageIndex(tamCeldaM: 4.0);
            var crudo = new List<double[]>();

            // Lote de 20 pasadas de 4 m, 200 m de largo.
            for (int p = 0; p < 20; p++)
                PasadaDoble(idx, crudo, ejeE: p * 4.0, ancho: 4, largo: 200);

            Assert.That(idx.CantidadTriangulos, Is.EqualTo(crudo.Count), "mismo set de triángulos");

            var rnd = new Random(12345);   // semilla fija: test reproducible
            int comparadas = 0;

            for (int i = 0; i < 400; i++)
            {
                double e = rnd.NextDouble() * 100 - 10;
                double n = rnd.NextDouble() * 220 - 10;
                double h = rnd.NextDouble() * Math.PI * 2;
                double hw = 1.0 + rnd.NextDouble() * 3.0;
                double look = rnd.NextDouble() < 0.3 ? rnd.NextDouble() * 20 : 0;

                var conIndice = idx.Consultar(e, n, h, hw, look);
                var aLoBruto = Fuerza(crudo, e, n, h, hw, look);

                Assert.That(conIndice.CoveragePercent, Is.EqualTo(aLoBruto.CoveragePercent).Within(1e-9),
                    string.Format("difieren en e={0:F2} n={1:F2} h={2:F3} hw={3:F2} look={4:F2}", e, n, h, hw, look));
                Assert.That(conIndice.HasAnyOverlap, Is.EqualTo(aLoBruto.HasAnyOverlap));
                comparadas++;
            }

            Assert.That(comparadas, Is.EqualTo(400));
        }

        [Test]
        public void TamanoDeCelda_NoCambiaElResultado()
        {
            var crudo = new List<double[]>();
            var chico = new CoverageIndex(tamCeldaM: 1.0);
            var grande = new CoverageIndex(tamCeldaM: 25.0);

            for (int p = 0; p < 8; p++)
            {
                PasadaDoble(chico, crudo, ejeE: p * 4.0, ancho: 4, largo: 120);
                Pasada(grande, ejeE: p * 4.0, ancho: 4, largo: 120);
            }

            var rnd = new Random(999);
            for (int i = 0; i < 120; i++)
            {
                double e = rnd.NextDouble() * 40 - 5;
                double n = rnd.NextDouble() * 130 - 5;
                double h = rnd.NextDouble() * Math.PI * 2;

                var a = chico.Consultar(e, n, h, 2.0);
                var b = grande.Consultar(e, n, h, 2.0);

                Assert.That(a.CoveragePercent, Is.EqualTo(b.CoveragePercent).Within(1e-9),
                    "el tamaño de celda es solo performance, no puede mover el resultado");
            }
        }

        [Test]
        public void Limpiar_DejaElIndiceVacio()
        {
            var idx = new CoverageIndex();
            Pasada(idx, 0, 4, 100);
            Assert.That(idx.CantidadTriangulos, Is.GreaterThan(0));

            idx.Limpiar();

            Assert.That(idx.CantidadTriangulos, Is.EqualTo(0));
            Assert.That(idx.Consultar(0, 50, 0, 2).HasAnyOverlap, Is.False);
        }

        [Test]
        public void TriangulosDegenerados_NoEntranAlIndice()
        {
            var idx = new CoverageIndex();
            // Tres puntos colineales: no tapan nada.
            idx.AgregarTriangulo(0, 0, 1, 0, 2, 0);

            Assert.That(idx.CantidadTriangulos, Is.EqualTo(0));
        }

        // ---- el caso que importa: no verse a uno mismo ---------------------

        [Test]
        public void PasadaRecta_LaSeccionNoDetectaSuPropiaPintura()
        {
            // Reproduce lo que hace el motor de verdad: en cada fix la sección
            // pinta un cuadrilátero DETRÁS suyo (de la posición anterior a la
            // actual) y acto seguido se pregunta si tiene que seguir encendida.
            //
            // Si la consulta llegara a ver esa pintura recién puesta, la sección
            // se apagaría sola en cuanto arranca — y como apagada ya no pinta,
            // quedaría trepidando. Este test fija que eso NO pase.
            var idx = new CoverageIndex();
            double ancho = 4.0, medioAncho = ancho / 2;
            double paso = 0.6;            // ~6,7 km/h a 10 Hz
            double lookAhead = 0.93;      // el mismo que usa el motor en cabina

            double n = 0;
            for (int fix = 0; fix < 200; fix++)
            {
                // 1) Pintar el tramo recién recorrido (de n a n+paso).
                idx.AgregarTriangulo(-medioAncho, n, medioAncho, n, -medioAncho, n + paso);
                idx.AgregarTriangulo(medioAncho, n, medioAncho, n + paso, -medioAncho, n + paso);
                n += paso;

                // 2) Decidir, desde la posición nueva, mirando hacia adelante.
                var r = idx.Consultar(0, n, 0, medioAncho, lookAhead);

                Assert.That(r.HasAnyOverlap, Is.False,
                    string.Format("fix {0}: la sección se está viendo a sí misma (solape {1:P0})",
                        fix, r.CoveragePercent));
            }
        }

        [Test]
        public void PasadaDeAlLado_SiSeDetecta()
        {
            // Contraparte del anterior: si la pintura es de una pasada VECINA,
            // tiene que detectarse. Si no, el test de arriba se podría estar
            // pasando simplemente porque nunca detecta nada.
            var idx = new CoverageIndex();
            Pasada(idx, ejeE: 0, ancho: 4, largo: 200);

            // Segunda pasada exactamente encima de la primera.
            var r = idx.Consultar(0, 100, 0, medioAncho: 2, umbralY: 0.93);

            Assert.That(r.IsFullyCovered, Is.True, "pisar la pasada anterior SÍ tiene que verse");
        }

        // ---- escala --------------------------------------------------------

        [Test]
        public void JornadaCompleta_ConsultaSigueSiendoBarata()
        {
            // ~8 h de trabajo: 50 pasadas de 4 m × 1000 m. Del orden de 100k
            // vértices, que es lo que dice el propio código de AOG que deja una
            // jornada larga.
            var idx = new CoverageIndex(tamCeldaM: 4.0);
            for (int p = 0; p < 50; p++)
                Pasada(idx, ejeE: p * 4.0, ancho: 4, largo: 1000, paso: 1.0);

            Assert.That(idx.CantidadTriangulos, Is.GreaterThan(50000),
                "si esto no es un lote grande, el test no prueba nada");

            // 16 secciones × 3 distancias (actual, look-on, look-off) × 10 Hz
            // = 480 consultas por segundo. Medimos ese segundo.
            const int consultas = 480;
            var sw = Stopwatch.StartNew();
            double acc = 0;
            for (int i = 0; i < consultas; i++)
            {
                double e = (i % 50) * 4.0;
                double n = 500 + (i % 17);
                var r = idx.Consultar(e, n, 0, medioAncho: 2, umbralY: (i % 3) * 5);
                acc += r.CoveragePercent;
            }
            sw.Stop();

            Assert.That(acc, Is.GreaterThan(0), "algo tiene que haber dado cobertura");

            // Presupuesto holgado: un segundo de consultas tiene que resolverse
            // en una fracción chica de ese segundo. Si esto falla, la poda dejó
            // de podar.
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(200),
                string.Format("{0} consultas tardaron {1} ms sobre {2} triángulos",
                    consultas, sw.ElapsedMilliseconds, idx.CantidadTriangulos));

            TestContext.Out.WriteLine(string.Format(
                "{0} triángulos, {1} celdas, {2} consultas en {3} ms ({4:F3} ms c/u)",
                idx.CantidadTriangulos, idx.CantidadCeldas, consultas,
                sw.ElapsedMilliseconds, (double)sw.ElapsedMilliseconds / consultas));
        }
    
    // El caso que inflo el equipo real a >4 GB: UN triangulo con un vertice
    // basura (bbox de km) registraba millones de celdas en la grilla. La
    // guardia lo descarta entero y el indice queda usable.
    [Test]
    public void TrianguloConBboxAbsurdo_SeDescartaYNoRevientaLaGrilla()
    {
        var idx = new CoverageIndex();
        // Triangulo veneno: 20 km de bbox (vertice sin origen / header).
        idx.AgregarTriangulo(0, 0, 20000, 0, 20000, 20000);
        Assert.That(idx.CantidadTriangulos, Is.EqualTo(0));

        // Y uno sano despues sigue funcionando normal.
        idx.AgregarTriangulo(0, 0, 3, 0, 0, 3);
        Assert.That(idx.CantidadTriangulos, Is.EqualTo(1));
        var r = idx.Consultar(1, 1, 0, 2);
        Assert.That(r.CoveragePercent, Is.GreaterThan(0));
    }
}
}
