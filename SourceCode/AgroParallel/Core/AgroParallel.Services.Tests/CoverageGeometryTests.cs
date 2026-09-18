// Casos de borde del solape por segmentos. De acá va a salir la decisión de
// cortar o no una sección sobre área ya sembrada: un falso "cubierto" deja un
// salteo sin sembrar, y un falso "libre" siembra dos veces encima. Los dos
// cuestan plata real, así que conviene que estén clavados con tests.
//
// Buena parte de estos tests fijan la CONVENCIÓN DE EJES (rumbo 0 = Norte,
// horario; X lateral positivo a la derecha; Y hacia adelante). Es lo que más
// fácil se rompe al portar geometría de otro proyecto.

using System;
using System.Collections.Generic;
using AgroParallel.Coverage;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class CoverageGeometryTests
    {
        private const double Tol = 1e-6;

        // ---- convención de ejes -------------------------------------------

        [Test]
        public void RumboNorte_PuntoAlEste_QuedaALaDerecha()
        {
            double cos = Math.Cos(0.0), sin = Math.Sin(0.0);
            double x, y;
            CoverageGeometry.ATransformar(10, 0, 0, 0, cos, sin, out x, out y);

            Assert.That(x, Is.EqualTo(10).Within(Tol), "al este del tractor = +X (derecha)");
            Assert.That(y, Is.EqualTo(0).Within(Tol), "no está ni adelante ni atrás");
        }

        [Test]
        public void RumboNorte_PuntoAlNorte_QuedaAdelante()
        {
            double cos = Math.Cos(0.0), sin = Math.Sin(0.0);
            double x, y;
            CoverageGeometry.ATransformar(0, 10, 0, 0, cos, sin, out x, out y);

            Assert.That(x, Is.EqualTo(0).Within(Tol));
            Assert.That(y, Is.EqualTo(10).Within(Tol), "al norte con rumbo norte = adelante");
        }

        [Test]
        public void RumboEste_PuntoAlEste_QuedaAdelante_NoAtras()
        {
            // Este es EL test que atrapa el error de signo al portar: con la
            // fórmula de AgOpenWeb tal cual, este punto daba Y negativo.
            double h = Math.PI / 2;                 // rumbo Este
            double cos = Math.Cos(h), sin = Math.Sin(h);
            double x, y;
            CoverageGeometry.ATransformar(10, 0, 0, 0, cos, sin, out x, out y);

            Assert.That(y, Is.EqualTo(10).Within(Tol), "yendo al este, lo que está al este está ADELANTE");
            Assert.That(x, Is.EqualTo(0).Within(Tol));
        }

        [Test]
        public void RumboEste_PuntoAlSur_QuedaALaDerecha()
        {
            double h = Math.PI / 2;
            double cos = Math.Cos(h), sin = Math.Sin(h);
            double x, y;
            CoverageGeometry.ATransformar(0, -10, 0, 0, cos, sin, out x, out y);

            Assert.That(x, Is.EqualTo(10).Within(Tol), "yendo al este, el sur queda a la derecha");
            Assert.That(y, Is.EqualTo(0).Within(Tol));
        }

        // ---- cruce de arista ----------------------------------------------

        [Test]
        public void Arista_QueCruza_DaElPuntoMedio()
        {
            double x;
            bool cruza = CoverageGeometry.CruceEnY(2, -1, 2, 1, 0, out x);

            Assert.That(cruza, Is.True);
            Assert.That(x, Is.EqualTo(2).Within(Tol));
        }

        [Test]
        public void Arista_QueNoCruza_DevuelveFalse()
        {
            double x;
            Assert.That(CoverageGeometry.CruceEnY(0, 1, 5, 3, 0, out x), Is.False, "ambos arriba");
            Assert.That(CoverageGeometry.CruceEnY(0, -1, 5, -3, 0, out x), Is.False, "ambos abajo");
        }

        [Test]
        public void Arista_ConUmbralDesplazado_EsElLookAhead()
        {
            // La MISMA arista, mirada a distintas distancias: es así como sale
            // el look-ahead sin volver a transformar nada.
            double x;

            Assert.That(CoverageGeometry.CruceEnY(0, 0, 0, 10, 5, out x), Is.True, "a 5 m la cruza");
            Assert.That(x, Is.EqualTo(0).Within(Tol));

            Assert.That(CoverageGeometry.CruceEnY(0, 0, 0, 10, 20, out x), Is.False, "a 20 m ya no llega");
        }

        [Test]
        public void Arista_QueArrancaJustoEnLaLinea_CuentaComoQueLaToca()
        {
            // Decisión deliberada: tocar en un extremo CUENTA como cruce.
            // Si se descartara, un triángulo con un vértice justo sobre la línea
            // y los otros dos a cada lado quedaría con un solo cruce detectado y
            // se perdería un solape real. El caso degenerado (tramo de largo
            // cero) lo filtra TrianguloEnIntervalo, no esta función.
            double x;
            Assert.That(CoverageGeometry.CruceEnY(3, 0, 3, 10, 0, out x), Is.True);
            Assert.That(x, Is.EqualTo(3).Within(Tol));
        }

        [Test]
        public void Triangulo_ConVerticeJustoEnLaLinea_YLosOtrosACadaLado_SeDetecta()
        {
            // El caso que justifica la decisión de arriba.
            XInterval iv;
            bool hay = CoverageGeometry.TrianguloEnIntervalo(
                0, 0,      // vértice EXACTAMENTE sobre la línea
                4, 5,      // arriba
                4, -5,     // abajo
                0, medioAncho: 10, intervalo: out iv);

            Assert.That(hay, Is.True, "cruza de verdad, no puede perderse");
            Assert.That(iv.Length, Is.GreaterThan(0));
        }

        [Test]
        public void Triangulo_ApenasTocandoConUnVertice_NoAportaAncho()
        {
            // Un vértice sobre la línea y los otros dos del MISMO lado: toca en
            // un punto, no tapa nada. Tiene que dar tramo vacío.
            XInterval iv;
            bool hay = CoverageGeometry.TrianguloEnIntervalo(
                0, 0,
                2, 5,
                -2, 5,
                0, medioAncho: 10, intervalo: out iv);

            Assert.That(hay, Is.False, "tocar en un punto no es cubrir");
        }

        // ---- triángulo -> tramo -------------------------------------------

        // Triángulo que cruza Y=0 entre x=-1 y x=+1.
        private static bool TrianguloCentrado(double medioAncho, out XInterval iv)
        {
            return CoverageGeometry.TrianguloEnIntervalo(
                -1, -1,    // a
                 1, -1,    // b
                 0,  2,    // c
                0, medioAncho, out iv);
        }

        [Test]
        public void Triangulo_QueCruza_DaSuTramo()
        {
            XInterval iv;
            Assert.That(TrianguloCentrado(5, out iv), Is.True);
            Assert.That(iv.Length, Is.GreaterThan(0));
            Assert.That(iv.Start, Is.LessThan(0));
            Assert.That(iv.End, Is.GreaterThan(0));
        }

        [Test]
        public void Triangulo_TodoArriba_NoCuenta()
        {
            XInterval iv;
            bool hay = CoverageGeometry.TrianguloEnIntervalo(
                -1, 5, 1, 5, 0, 8, 0, 5, out iv);

            Assert.That(hay, Is.False, "el triángulo está adelante de la sección, no la toca");
        }

        [Test]
        public void Triangulo_TodoAbajo_NoCuenta()
        {
            XInterval iv;
            bool hay = CoverageGeometry.TrianguloEnIntervalo(
                -1, -5, 1, -5, 0, -8, 0, 5, out iv);

            Assert.That(hay, Is.False, "ya quedó atrás");
        }

        [Test]
        public void Triangulo_MasAnchoQueLaSeccion_SeRecorta()
        {
            // Triángulo que cruza de -100 a +100: no puede aportar más que el
            // ancho de la sección.
            XInterval iv;
            bool hay = CoverageGeometry.TrianguloEnIntervalo(
                -100, -1, 100, -1, 0, 50, 0, medioAncho: 2, intervalo: out iv);

            Assert.That(hay, Is.True);
            Assert.That(iv.Start, Is.EqualTo(-2).Within(Tol));
            Assert.That(iv.End, Is.EqualTo(2).Within(Tol));
        }

        [Test]
        public void Triangulo_FueraDelAncho_NoCuenta()
        {
            // Cruza Y=0 pero muy a la derecha, fuera de la sección.
            XInterval iv;
            bool hay = CoverageGeometry.TrianguloEnIntervalo(
                50, -1, 52, -1, 51, 2, 0, medioAncho: 2, intervalo: out iv);

            Assert.That(hay, Is.False);
        }

        // ---- unión de tramos ----------------------------------------------

        [Test]
        public void Tramos_QueSePisan_NoSeCuentanDosVeces()
        {
            var lista = new List<XInterval>
            {
                new XInterval(-2, 1),
                new XInterval(0, 2),   // pisa al anterior
            };

            var unidos = CoverageGeometry.Unir(lista);

            Assert.That(unidos.Count, Is.EqualTo(1));
            Assert.That(unidos[0].Start, Is.EqualTo(-2).Within(Tol));
            Assert.That(unidos[0].End, Is.EqualTo(2).Within(Tol));
        }

        [Test]
        public void Tramos_Separados_QuedanSeparados()
        {
            var lista = new List<XInterval>
            {
                new XInterval(-2, -1),
                new XInterval(1, 2),
            };

            var unidos = CoverageGeometry.Unir(lista);

            Assert.That(unidos.Count, Is.EqualTo(2));
        }

        [Test]
        public void Tramos_Desordenados_SeUnenIgual()
        {
            var lista = new List<XInterval>
            {
                new XInterval(1, 2),
                new XInterval(-2, -1),
                new XInterval(-1.5, 1.5),   // los une a los dos
            };

            var unidos = CoverageGeometry.Unir(lista);

            Assert.That(unidos.Count, Is.EqualTo(1));
            Assert.That(unidos[0].Start, Is.EqualTo(-2).Within(Tol));
            Assert.That(unidos[0].End, Is.EqualTo(2).Within(Tol));
        }

        // ---- resultado final ----------------------------------------------

        [Test]
        public void SinTramos_SeccionLibre()
        {
            var r = CoverageGeometry.Resultado(new List<XInterval>(), medioAncho: 2);

            Assert.That(r.CoveragePercent, Is.EqualTo(0).Within(Tol));
            Assert.That(r.HasAnyOverlap, Is.False);
            Assert.That(r.IsFullyCovered, Is.False);
            Assert.That(r.UncoveredLength, Is.EqualTo(4).Within(Tol), "los 4 m enteros sin sembrar");
        }

        [Test]
        public void TramoCompleto_SeccionCubierta()
        {
            var r = CoverageGeometry.Resultado(
                new List<XInterval> { new XInterval(-2, 2) }, medioAncho: 2);

            Assert.That(r.CoveragePercent, Is.EqualTo(1.0).Within(Tol));
            Assert.That(r.HasAnyOverlap, Is.True);
            Assert.That(r.IsFullyCovered, Is.True);
            Assert.That(r.UncoveredLength, Is.EqualTo(0).Within(Tol));
        }

        [Test]
        public void MitadCubierta_DaCincuentaPorCiento()
        {
            var r = CoverageGeometry.Resultado(
                new List<XInterval> { new XInterval(-2, 0) }, medioAncho: 2);

            Assert.That(r.CoveragePercent, Is.EqualTo(0.5).Within(Tol));
            Assert.That(r.HasAnyOverlap, Is.True);
            Assert.That(r.IsFullyCovered, Is.False);
            Assert.That(r.UncoveredLength, Is.EqualTo(2).Within(Tol));
        }

        [Test]
        public void HuecoEnElMedio_NoCuentaComoCubierta()
        {
            // Este es el caso que el método por UN PUNTO se perdía: el centro
            // cae en el hueco (o en lo pintado) y decide por toda la sección.
            var r = CoverageGeometry.Resultado(
                new List<XInterval>
                {
                    new XInterval(-2, -0.5),
                    new XInterval(0.5, 2),
                },
                medioAncho: 2);

            Assert.That(r.CoveragePercent, Is.EqualTo(0.75).Within(Tol));
            Assert.That(r.HasAnyOverlap, Is.True);
            Assert.That(r.IsFullyCovered, Is.False, "queda 1 m sin sembrar en el medio");
            Assert.That(r.UncoveredLength, Is.EqualTo(1).Within(Tol));
        }

        [Test]
        public void TramosSuperpuestos_NoPasanDeCienPorCiento()
        {
            var r = CoverageGeometry.Resultado(
                new List<XInterval>
                {
                    new XInterval(-2, 2),
                    new XInterval(-2, 2),   // la misma pasada dos veces
                    new XInterval(-1, 1),
                },
                medioAncho: 2);

            Assert.That(r.CoveragePercent, Is.EqualTo(1.0).Within(Tol));
            Assert.That(r.UncoveredLength, Is.EqualTo(0).Within(Tol));
        }

        // ---- integración: mundo -> resultado -------------------------------

        [Test]
        public void PasadaAnterior_AlLado_CubreMediaSeccion()
        {
            // Sección de 4 m de ancho centrada en el origen, rumbo Norte.
            // Una pasada anterior dejó pintado todo el semiplano x >= 0.
            double medioAncho = 2.0;
            double cos = Math.Cos(0.0), sin = Math.Sin(0.0);

            // Dos triángulos que forman una banda que cruza Y=0 desde x=0 a x=50.
            var intervalos = new List<XInterval>();
            AgregarTriangulo(intervalos, cos, sin, medioAncho,
                0, -10, 50, -10, 0, 10);
            AgregarTriangulo(intervalos, cos, sin, medioAncho,
                50, -10, 50, 10, 0, 10);

            var r = CoverageGeometry.Resultado(intervalos, medioAncho);

            Assert.That(r.CoveragePercent, Is.EqualTo(0.5).Within(1e-3),
                "media sección pisando la pasada de al lado");
            Assert.That(r.IsFullyCovered, Is.False);
        }

        private static void AgregarTriangulo(
            List<XInterval> destino, double cos, double sin, double medioAncho,
            double ae, double an, double be, double bn, double ce, double cn)
        {
            double ax, ay, bx, by, cx, cy;
            CoverageGeometry.ATransformar(ae, an, 0, 0, cos, sin, out ax, out ay);
            CoverageGeometry.ATransformar(be, bn, 0, 0, cos, sin, out bx, out by);
            CoverageGeometry.ATransformar(ce, cn, 0, 0, cos, sin, out cx, out cy);

            XInterval iv;
            if (CoverageGeometry.TrianguloEnIntervalo(ax, ay, bx, by, cx, cy, 0, medioAncho, out iv))
                destino.Add(iv);
        }
    }
}
