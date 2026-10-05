using System;
using AgOpenGPS.SteerCal;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    // Cero automático del WAS: la clase MIDE y PROPONE, nunca aplica. Estos
    // tests fijan qué muestras cuentan, que la mediana recupera el sesgo y el
    // signo del offset propuesto (el mismo que el cero manual de
    // SteerConfigService.ZeroWas: offset += cuentas/grado × (−ángulo)).
    [TestFixture]
    public class CeroWasEstadisticoTests
    {
        private const double Hz = 10.0;

        private static ConfigWasCeroAuto Cfg(int offset = -177, int cpd = 110, int ack = 100, bool inv = false)
            => new ConfigWasCeroAuto { WasOffset = offset, CuentasPorGrado = cpd, AckermanPct = ack, InvertWas = inv };

        /// <summary>Andar derecho, enganchado, sobre la línea, a 8 km/h.</summary>
        private static EntradaCeroWas Buena(double t, double angulo)
            => new EntradaCeroWas
            {
                T = t,
                AnguloReal = angulo,
                PilotoEnganchado = true,
                GuiaRecta = true,
                VelKmh = 8,
                ErrorLateralM = 0.03,
                RumboRad = 1.0,
                RolidoGrados = CeroWasEstadistico.CentinelaSinImu,
            };

        /// <summary>Alimenta <paramref name="segundos"/> de muestras a 10 Hz con
        /// ruido gaussiano y una corrección lenta (el piloto serpentea apenas).</summary>
        private static double Alimentar(CeroWasEstadistico c, double t0, double segundos, double sesgo,
            ConfigWasCeroAuto cfg, Func<EntradaCeroWas, EntradaCeroWas> mod = null, int semilla = 7)
        {
            var rnd = new Random(semilla);
            int n = (int)(segundos * Hz);
            double t = t0;
            for (int i = 0; i < n; i++)
            {
                t = t0 + i / Hz;
                double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
                double gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                double ang = sesgo + 0.4 * gauss + 0.3 * Math.Sin(t * 0.7);
                var e = Buena(t, ang);
                if (mod != null) e = mod(e);
                c.Evaluar(e, cfg);
            }
            return t + 1 / Hz;
        }

        [TestCase(2.0)]
        [TestCase(-1.5)]
        [TestCase(0.8)]
        public void RecuperaElSesgoConocido(double sesgo)
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, sesgo, Cfg());
            var r = c.Resultado();
            Assert.That(r.SesgoGrados, Is.EqualTo(sesgo).Within(0.2));
            Assert.That(r.Estado, Is.EqualTo(EstadoCeroWas.Propuesta));
            Assert.That(r.HayPropuesta, Is.True);
            Assert.That(r.Confianza, Is.GreaterThanOrEqualTo(50));
        }

        [Test]
        public void SignoIgualAlCeroManual_SesgoPositivoBajaElOffset()
        {
            // ZeroWas: offset += cpd × (−ángulo). Con la rueda derecha leyendo
            // +2° el cero manual llevaría el offset de −177 a −177 − 220 = −397.
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg(offset: -177, cpd: 110));
            var r = c.Resultado();
            int zeroWasManual = -177 + (int)Math.Round(110 * -r.SesgoGrados);
            Assert.That(r.OffsetPropuesto, Is.EqualTo(zeroWasManual));
            Assert.That(r.OffsetPropuesto, Is.LessThan(-177));
            Assert.That(r.OffsetActual, Is.EqualTo(-177));
        }

        [Test]
        public void ElSignoNoDependeDeInvertirWas()
        {
            // El módulo ya invierte el ángulo que reporta y su fórmula
            // (crudo−centro∓offset)/±cpd hace que d(ángulo)/d(offset) = +1/cpd
            // en las dos ramas: la propuesta es la misma con o sin invertir.
            var a = new CeroWasEstadistico();
            var b = new CeroWasEstadistico();
            Alimentar(a, 0, 90, 1.7, Cfg(inv: false));
            Alimentar(b, 0, 90, 1.7, Cfg(inv: true));
            Assert.That(b.Resultado().OffsetPropuesto, Is.EqualTo(a.Resultado().OffsetPropuesto));
        }

        [Test]
        public void FormulaDelFirmware_ConLaPropuestaElAnguloQuedaEnCero_ConYSinInvertir()
        {
            // Port de la conversión de Autosteer.ino (AiO) para verificar el
            // signo contra la placa y no contra nosotros mismos.
            foreach (bool inv in new[] { false, true })
            {
                const int centro = 6805, cpd = 110, offset = 40;
                int crudo = inv ? centro - 180 : centro + 180;   // rueda derecha, sensor corrido
                Func<int, double> ecu = off => inv
                    ? (crudo - centro - off) / -(double)cpd
                    : (crudo - centro + off) / (double)cpd;
                double leido = ecu(offset);
                int propuesto = CeroWasEstadistico.CalcularOffsetPropuesto(offset, leido, cpd, 100);
                Assert.That(ecu(propuesto), Is.EqualTo(0).Within(0.01), "invertido=" + inv);
            }
        }

        [Test]
        public void Ackermann_SesgoIzquierdoSeEscalaALasCuentasCrudas()
        {
            // El firmware multiplica los ángulos negativos por ack/100 DESPUÉS
            // del offset: −1,6° leídos con ack 80 % son −2° de cuentas.
            int p = CeroWasEstadistico.CalcularOffsetPropuesto(0, -1.6, 100, 80);
            Assert.That(p, Is.EqualTo(200));
            // Del lado derecho no se toca.
            Assert.That(CeroWasEstadistico.CalcularOffsetPropuesto(0, 1.6, 100, 80), Is.EqualTo(-160));
        }

        [Test]
        public void CeroBien_NoPropone()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 0.1, Cfg());
            var r = c.Resultado();
            Assert.That(r.Estado, Is.EqualTo(EstadoCeroWas.CeroBien));
            Assert.That(r.HayPropuesta, Is.False);
            Assert.That(r.OffsetPropuesto, Is.EqualTo(r.OffsetActual));
        }

        [Test]
        public void SinTiempoSuficiente_NoPropone()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 40, 2.0, Cfg());
            var r = c.Resultado();
            Assert.That(r.Estado, Is.EqualTo(EstadoCeroWas.Juntando));
            Assert.That(r.HayPropuesta, Is.False);
            Assert.That(r.SegundosEfectivos, Is.LessThan(r.SegundosRequeridos));
        }

        [Test]
        public void PilotoSuelto_NoSumaMuestras()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg(), e => { e.PilotoEnganchado = false; return e; });
            var r = c.Resultado();
            Assert.That(r.Muestras, Is.EqualTo(0));
            Assert.That(r.CondicionesOk, Is.False);
            Assert.That(r.Motivo, Is.EqualTo("piloto_suelto"));
        }

        [Test]
        public void GuiaCurva_NoSumaMuestras()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg(), e => { e.GuiaRecta = false; return e; });
            Assert.That(c.Resultado().Muestras, Is.EqualTo(0));
            Assert.That(c.Resultado().Motivo, Is.EqualTo("guia_curva"));
        }

        [Test]
        public void LejosDeLaLinea_NoSumaMuestras()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg(), e => { e.ErrorLateralM = 0.40; return e; });
            Assert.That(c.Resultado().Muestras, Is.EqualTo(0));
            Assert.That(c.Resultado().Motivo, Is.EqualTo("lejos_de_la_linea"));
        }

        [Test]
        public void SinGuia_NoSumaMuestras()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg(), e => { e.ErrorLateralM = double.NaN; return e; });
            Assert.That(c.Resultado().Muestras, Is.EqualTo(0));
        }

        [TestCase(2.0)]
        [TestCase(25.0)]
        public void VelocidadFueraDeRango_NoSumaMuestras(double kmh)
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg(), e => { e.VelKmh = kmh; return e; });
            Assert.That(c.Resultado().Muestras, Is.EqualTo(0));
            Assert.That(c.Resultado().Motivo, Is.EqualTo("velocidad"));
        }

        [Test]
        public void MarchaAtrasYUTurn_NoSumanMuestras()
        {
            var a = new CeroWasEstadistico();
            Alimentar(a, 0, 90, 2.0, Cfg(), e => { e.MarchaAtras = true; return e; });
            Assert.That(a.Resultado().Muestras, Is.EqualTo(0));
            var b = new CeroWasEstadistico();
            Alimentar(b, 0, 90, 2.0, Cfg(), e => { e.UTurn = true; return e; });
            Assert.That(b.Resultado().Muestras, Is.EqualTo(0));
        }

        [Test]
        public void Girando_NoSumaMuestras()
        {
            // Rumbo que cambia 3°/s: no es andar derecho aunque la guía sea AB.
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg(), e => { e.RumboRad = 0.5 + e.T * 3.0 * Math.PI / 180.0; return e; });
            Assert.That(c.Resultado().Muestras, Is.EqualTo(0));
            Assert.That(c.Resultado().Motivo, Is.EqualTo("girando"));
        }

        [Test]
        public void RolidoGrande_NoSuma_PeroSinImuSiSuma()
        {
            var a = new CeroWasEstadistico();
            Alimentar(a, 0, 90, 2.0, Cfg(), e => { e.RolidoGrados = 9; return e; });
            Assert.That(a.Resultado().Muestras, Is.EqualTo(0));
            Assert.That(a.Resultado().Motivo, Is.EqualTo("rolido"));

            var b = new CeroWasEstadistico();
            Alimentar(b, 0, 90, 2.0, Cfg(), e => { e.RolidoGrados = 2; return e; });
            Assert.That(b.Resultado().Muestras, Is.GreaterThan(0));
        }

        [Test]
        public void EsperaEstabilizarseAntesDeContar()
        {
            // Las primeras muestras buenas (3 s) no cuentan: recién enganchado
            // el piloto todavía está entrando a la línea.
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 2.5, 2.0, Cfg());
            Assert.That(c.Resultado().Muestras, Is.EqualTo(0));
            Assert.That(c.Resultado().Motivo, Is.EqualTo("estabilizando"));
        }

        [Test]
        public void SiCambiaLaConfigDelWas_ReiniciaLaMedicion()
        {
            // Las muestras viejas se tomaron con otro offset/escala: no valen.
            var c = new CeroWasEstadistico();
            double t = Alimentar(c, 0, 90, 2.0, Cfg(offset: -177));
            Assert.That(c.Resultado().Muestras, Is.GreaterThan(0));
            c.Evaluar(Buena(t, 0.0), Cfg(offset: -397));
            Assert.That(c.Resultado().Muestras, Is.EqualTo(0));
            Assert.That(c.Resultado().OffsetActual, Is.EqualTo(-397));
        }

        [Test]
        public void Reiniciar_BorraTodo()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg());
            c.Reiniciar();
            var r = c.Resultado();
            Assert.That(r.Muestras, Is.EqualTo(0));
            Assert.That(r.Estado, Is.EqualTo(EstadoCeroWas.Juntando));
            Assert.That(r.HayPropuesta, Is.False);
        }

        [Test]
        public void DatosDispersos_NoProponeConConfianzaBaja()
        {
            // Mitad del tiempo +3°, mitad −1°: no hay un cero claro.
            var c = new CeroWasEstadistico();
            double t = Alimentar(c, 0, 50, 3.0, Cfg(), semilla: 1);
            Alimentar(c, t, 50, -1.0, Cfg(), semilla: 2);
            var r = c.Resultado();
            Assert.That(r.HayPropuesta, Is.False);
            Assert.That(r.Estado, Is.EqualTo(EstadoCeroWas.Inestable));
        }

        [Test]
        public void OffsetFueraDeRango_NoPropone()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 2.0, Cfg(offset: -3800));
            var r = c.Resultado();
            Assert.That(r.Estado, Is.EqualTo(EstadoCeroWas.FueraDeRango));
            Assert.That(r.HayPropuesta, Is.False);
        }

        [Test]
        public void AnguloGrande_NoCuenta()
        {
            var c = new CeroWasEstadistico();
            Alimentar(c, 0, 90, 12.0, Cfg());
            Assert.That(c.Resultado().Muestras, Is.EqualTo(0));
        }

        [Test]
        public void HuecoDeDatos_NoSumaTiempoEfectivo()
        {
            // Dos muestras separadas 30 s no son 30 s de recta.
            var c = new CeroWasEstadistico();
            double t = Alimentar(c, 0, 10, 1.0, Cfg());
            double antes = c.Resultado().SegundosEfectivos;
            c.Evaluar(Buena(t + 30, 1.0), Cfg());
            Assert.That(c.Resultado().SegundosEfectivos - antes, Is.LessThanOrEqualTo(0.5));
        }
    }
}
