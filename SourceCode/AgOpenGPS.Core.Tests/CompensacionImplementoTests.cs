// ============================================================================
// CompensacionImplementoTests.cs — guiado del implemento "nivel A" (pasivo,
// solo modelo, sin sensores). Fija la geometría en régimen estacionario, el
// signo para arrastre y para 3 puntos, el tope, el límite de variación y que
// con el flag apagado el pivote que va a la guía queda intacto.
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class CompensacionImplementoTests
    {
        private static GeometriaImplemento Arrastre(double lanza, double enganche = 0)
            => new GeometriaImplemento(arrastre: true, engancheM: enganche, tanqueM: 0, lanzaM: lanza, herramientaAlEjeM: 0);

        private static GeometriaImplemento Rigido(double enganche)
            => new GeometriaImplemento(arrastre: false, engancheM: enganche, tanqueM: 0, lanzaM: 0, herramientaAlEjeM: 0);

        // ---- geometría en régimen estacionario --------------------------------

        [Test]
        public void Arrastre_R30_L6_CorrigeUnos60cmHaciaAfuera()
        {
            double c = CompensacionImplemento.CorreccionHaciaAfuera(30, Arrastre(6));

            // El implemento va ~L²/(2R) = 0,60 m por ADENTRO: el tractor tiene
            // que abrirse eso mismo hacia afuera (valor exacto: 30 − √(900−36)).
            Assert.That(c, Is.EqualTo(30 - Math.Sqrt(900 - 36)).Within(1e-9));
            Assert.That(c, Is.EqualTo(6.0 * 6.0 / (2 * 30)).Within(0.02));
            Assert.That(c, Is.GreaterThan(0), "arrastre: el tractor se abre hacia AFUERA");
        }

        [Test]
        public void TresPuntos_CorrigeHaciaAdentroConMagnitudSimilar()
        {
            // Rígido 3 m detrás del eje trasero: va por AFUERA ≈ d²/(2R).
            double c = CompensacionImplemento.CorreccionHaciaAfuera(30, Rigido(-3));

            Assert.That(c, Is.LessThan(0), "3 puntos: el tractor se cierra hacia ADENTRO");
            Assert.That(-c, Is.EqualTo(3.0 * 3.0 / (2 * 30)).Within(0.01));
        }

        [Test]
        public void Arrastre_ElEngancheRigidoDescuentaDeLaLanza()
        {
            // El enganche (barra) va por afuera y la lanza por adentro: el
            // modelo real de PilotX da (lanza² − enganche²)/(2R), no (suma)².
            double c = CompensacionImplemento.CorreccionHaciaAfuera(30, Arrastre(5, enganche: -1));

            Assert.That(c, Is.EqualTo((25.0 - 1.0) / (2 * 30)).Within(0.01));
        }

        [Test]
        public void Arrastre_ConTanqueTbt_SumaLosTramos()
        {
            var geo = new GeometriaImplemento(arrastre: true, engancheM: 0, tanqueM: 3, lanzaM: 4, herramientaAlEjeM: 0);
            double c = CompensacionImplemento.CorreccionHaciaAfuera(40, geo);

            Assert.That(c, Is.EqualTo((9.0 + 16.0) / (2 * 40)).Within(0.01));
        }

        // ---- objetivo con signo, recta, tope -------------------------------

        [Test]
        public void Recta_DaCero()
        {
            Assert.That(CompensacionImplemento.Objetivo(0, Arrastre(6), 0.5), Is.EqualTo(0));
            Assert.That(CompensacionImplemento.Objetivo(0, Rigido(-3), 0.5), Is.EqualTo(0));
        }

        [Test]
        public void RadioMuyGrande_DaCero()
        {
            Assert.That(CompensacionImplemento.Objetivo(1.0 / 2000, Arrastre(6), 0.5), Is.EqualTo(0));
        }

        [Test]
        public void Signo_CurvaALaDerecha_ArrastreCorrigeALaIzquierda()
        {
            // curvatura > 0 = rumbo creciendo = dobla a la DERECHA (rumbo AOG
            // horario). Afuera es la izquierda: corrección "a la derecha" < 0.
            double der = CompensacionImplemento.Objetivo(1.0 / 30, Arrastre(4), 0.5);
            double izq = CompensacionImplemento.Objetivo(-1.0 / 30, Arrastre(4), 0.5);

            Assert.That(der, Is.LessThan(0));
            Assert.That(izq, Is.EqualTo(-der).Within(1e-12));
        }

        [Test]
        public void Signo_CurvaALaDerecha_TresPuntosCorrigeALaDerecha()
        {
            Assert.That(CompensacionImplemento.Objetivo(1.0 / 30, Rigido(-3), 0.5), Is.GreaterThan(0));
        }

        [Test]
        public void Tope_LimitaElObjetivo()
        {
            // R=10, L=6 (curva a la izquierda): el modelo pide 2 m; el tope lo deja en 0,5.
            double obj = CompensacionImplemento.Objetivo(-1.0 / 10, Arrastre(6), 0.5);
            Assert.That(obj, Is.EqualTo(0.5).Within(1e-12));

            // Radio imposible (lanza más larga que el radio): no revienta, va al tope.
            double imposible = CompensacionImplemento.Objetivo(-1.0 / 5, Arrastre(8), 0.5);
            Assert.That(imposible, Is.EqualTo(0.5).Within(1e-12));
        }

        // ---- límite de variación y flag ------------------------------------

        [Test]
        public void LimiteDeVariacion_MaximoCincoCentimetrosPorSegundo()
        {
            var comp = new CompensacionImplemento();

            // Un segundo de GPS a 10 Hz: 5 cm, ni uno más.
            for (int i = 0; i < 10; i++)
                comp.Paso(habilitado: true, anular: false, curvatura: -1.0 / 10, Arrastre(6), dt: 0.1);
            Assert.That(comp.CorreccionDerecha, Is.EqualTo(0.05).Within(1e-9));

            for (int i = 0; i < 200; i++)
                comp.Paso(true, false, -1.0 / 10, Arrastre(6), 0.1);
            Assert.That(comp.CorreccionDerecha, Is.EqualTo(0.5).Within(1e-9), "llega al tope y se queda");

            // Sale de la curva: vuelve a 0 con la misma rampa, no de golpe.
            for (int i = 0; i < 10; i++)
                comp.Paso(true, false, 0, Arrastre(6), 0.1);
            Assert.That(comp.CorreccionDerecha, Is.EqualTo(0.45).Within(1e-9));
        }

        [Test]
        public void HuecoDeGps_NoDaUnSaltoGrande()
        {
            var comp = new CompensacionImplemento();
            comp.Paso(true, false, -1.0 / 10, Arrastre(6), dt: 30);
            Assert.That(comp.CorreccionDerecha, Is.LessThanOrEqualTo(0.05 * CompensacionImplemento.DtMaximoS + 1e-12));
        }

        [Test]
        public void Anular_UTurn_VuelveACeroEnElActo()
        {
            var comp = new CompensacionImplemento();
            for (int i = 0; i < 40; i++) comp.Paso(true, false, -1.0 / 10, Arrastre(6), 0.5);

            comp.Paso(true, anular: true, curvatura: -1.0 / 10, Arrastre(6), dt: 0.1);

            Assert.That(comp.CorreccionDerecha, Is.EqualTo(0));
        }

        [Test]
        public void FlagApagado_PivoteSinTocar()
        {
            var comp = new CompensacionImplemento();
            var pivote = new vec3(1234.5, -678.9, 0.7);

            for (int i = 0; i < 20; i++)
                comp.Paso(habilitado: false, anular: false, curvatura: 1.0 / 10, Arrastre(6), dt: 1.0);

            vec3 p = comp.Aplicar(pivote);
            Assert.That(comp.CorreccionDerecha, Is.EqualTo(0));
            Assert.That(p.easting, Is.EqualTo(pivote.easting));
            Assert.That(p.northing, Is.EqualTo(pivote.northing));
            Assert.That(p.heading, Is.EqualTo(pivote.heading));
        }

        [Test]
        public void FlagQueSeApaga_CortaEnElActo()
        {
            var comp = new CompensacionImplemento();
            for (int i = 0; i < 40; i++) comp.Paso(true, false, -1.0 / 10, Arrastre(6), 0.5);

            comp.Paso(false, false, -1.0 / 10, Arrastre(6), 0.1);

            Assert.That(comp.CorreccionDerecha, Is.EqualTo(0));
        }

        [Test]
        public void Desplazar_PivoteVirtualVaDelLadoDelImplemento()
        {
            // Rumbo norte, curva a la derecha, arrastre: corrección a la derecha
            // negativa (afuera = izquierda) → el pivote virtual queda corrido a
            // la DERECHA, adentro de la curva, donde va el implemento. La guía
            // lleva ese punto a la línea y el tractor queda abierto.
            var p = CompensacionImplemento.Desplazar(new vec3(0, 0, 0), correccionDerecha: -0.4);

            Assert.That(p.easting, Is.EqualTo(0.4).Within(1e-12));
            Assert.That(p.northing, Is.EqualTo(0).Within(1e-12));
            Assert.That(p.heading, Is.EqualTo(0));

            // Rumbo este (π/2): la derecha es el sur.
            var q = CompensacionImplemento.Desplazar(new vec3(0, 0, Math.PI / 2), correccionDerecha: -0.4);
            Assert.That(q.easting, Is.EqualTo(0).Within(1e-12));
            Assert.That(q.northing, Is.EqualTo(-0.4).Within(1e-12));
        }

        // ---- curvatura desde la curva activa --------------------------------

        /// <summary>Círculo recorrido en sentido horario (dobla a la derecha) arrancando en (0,0) rumbo norte.</summary>
        private static List<vec3> CirculoHorario(double radio, double paso, int puntos)
        {
            var l = new List<vec3>();
            for (int i = 0; i < puntos; i++)
            {
                double th = i * paso / radio;
                l.Add(new vec3(radio - radio * Math.Cos(th), radio * Math.Sin(th), th));
            }
            return l;
        }

        [Test]
        public void Curvatura_CirculoALaDerecha_EsPositiva()
        {
            var l = CirculoHorario(30, 1, 120);
            var pivote = new vec3(l[20].easting, l[20].northing, l[20].heading);

            double k = CompensacionImplemento.CurvaturaEnMira(l, pivote, sentidoLista: true);

            Assert.That(k, Is.EqualTo(1.0 / 30).Within(0.001));
        }

        [Test]
        public void Curvatura_RecorridaAlReves_CambiaDeSigno()
        {
            var l = CirculoHorario(30, 1, 120);
            var pivote = new vec3(l[60].easting, l[60].northing, l[60].heading + Math.PI);

            double k = CompensacionImplemento.CurvaturaEnMira(l, pivote, sentidoLista: false);

            Assert.That(k, Is.EqualTo(-1.0 / 30).Within(0.001));
        }

        [Test]
        public void Curvatura_Recta_EsCero()
        {
            var l = new List<vec3>();
            for (int i = 0; i < 50; i++) l.Add(new vec3(i * 0.3, i * 0.9, Math.Atan2(0.3, 0.9)));

            double k = CompensacionImplemento.CurvaturaEnMira(l, l[10], sentidoLista: true);

            Assert.That(k, Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void Curvatura_ListaCortaONula_EsCero()
        {
            Assert.That(CompensacionImplemento.CurvaturaEnMira(null, new vec3(), true), Is.EqualTo(0));
            Assert.That(CompensacionImplemento.CurvaturaEnMira(new List<vec3> { new vec3(), new vec3(1, 1, 0) }, new vec3(), true), Is.EqualTo(0));
        }

        [Test]
        public void Curvatura_AlFinalDeLaLista_NoRevienta()
        {
            var l = CirculoHorario(30, 1, 40);
            var ultimo = l[l.Count - 1];

            double k = CompensacionImplemento.CurvaturaEnMira(l, ultimo, sentidoLista: true);

            Assert.That(double.IsNaN(k), Is.False);
        }

        // ---- validación del perfil ------------------------------------------

        [Test]
        public void ValidarPerfil_RigidoConEngancheDe5m_Avisa()
        {
            string aviso = CompensacionImplemento.ValidarPerfil(arrastre: false, hitchLength: -5,
                trailingHitchLength: -3, tankTrailingHitchLength: 0, tbt: false);

            Assert.That(aviso, Is.Not.Null);
            Assert.That(aviso, Does.Contain("5"));
        }

        [Test]
        public void ValidarPerfil_TresPuntosNormal_NoAvisa()
        {
            Assert.That(CompensacionImplemento.ValidarPerfil(false, -1.2, 0, 0, false), Is.Null);
        }

        [Test]
        public void ValidarPerfil_ArrastreSinLanza_Avisa()
        {
            Assert.That(CompensacionImplemento.ValidarPerfil(true, -1, 0, 0, false), Is.Not.Null);
            Assert.That(CompensacionImplemento.ValidarPerfil(true, -1, -5, 0, false), Is.Null);
        }

        [Test]
        public void DesdeTool_ToleraSignosDeAog()
        {
            // AOG guarda las distancias hacia atrás en negativo; el modelo usa
            // cuadrados, el signo no cambia nada.
            var tool = new CTool(null)
            {
                isToolTrailing = true, isToolTBT = true,
                hitchLength = -1, tankTrailingHitchLength = -3, trailingHitchLength = -4,
                trailingToolToPivotLength = 0,
            };
            var geo = GeometriaImplemento.DesdeTool(tool, engancheDesdePivote: tool.hitchLength);
            var esperado = new GeometriaImplemento(true, 1, 3, 4, 0);

            Assert.That(CompensacionImplemento.CorreccionHaciaAfuera(40, geo),
                Is.EqualTo(CompensacionImplemento.CorreccionHaciaAfuera(40, esperado)).Within(1e-12));
        }
    }
}
