// ============================================================================
// QxPidAnalisisTests.cs — el analizador de sintonia.
//
// Cada test arma una corrida con una forma concreta y verifica que el veredicto
// sea el correcto. Lo que mas importa NO es que acierte el diagnostico lindo,
// sino que SE CALLE cuando los datos no lo soportan: recomendar una ganancia
// sobre una corrida en curva o con el motor saturado es peor que no decir nada.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class QxPidAnalisisTests
    {
        private const double Kp = 5.8, Ki = 22.5, Kd = 0;

        /// <summary>Arma una corrida de n muestras a 5 Hz (200 ms entre muestras).</summary>
        private static List<QxPidSample> Corrida(int n, Func<int, double> rpmReal,
            double rpmTarget = 45, double velMotor = 6.0, int loadPct = 50)
        {
            var ms = new List<QxPidSample>(n);
            for (int i = 0; i < n; i++)
            {
                ms.Add(new QxPidSample
                {
                    TickMs = i * 200,
                    Uid = "A4CF12AB9E30",
                    MotorIdx = 0,
                    Nombre = "Cuerpo 1",
                    RpmReal = rpmReal(i),
                    RpmTarget = rpmTarget,
                    PpsTarget = 15,
                    PpsReal = 15,
                    Pwm = (int)(loadPct / 100.0 * 4095),
                    LoadPct = loadPct,
                    VelMotorKmh = velMotor,
                    VelGpsKmh = velMotor,
                    Dosis = 7,
                    SeccionOn = true,
                });
            }
            return ms;
        }

        // ── Los descartes: lo que NO tiene que recomendar ────────────────────

        [Test]
        public void Corrida_corta_no_recomienda_nada()
        {
            var d = QxPidAnalisis.Analizar(Corrida(20, i => 45), Kp, Ki, Kd);
            Assert.That(d.Veredicto, Is.EqualTo(QxVeredicto.SinDatos));
            Assert.That(d.HayRecomendacion, Is.False);
            Assert.That(d.Explicacion, Does.Contain("100 metros"));
        }

        [Test]
        public void Con_la_velocidad_despareja_se_calla()
        {
            // Una curva: la velocidad del motor se mueve mucho, asi que el target
            // cambia CON RAZON. Culpar al PID acá seria el error clasico.
            var ms = Corrida(300, i => 45 + Math.Sin(i / 5.0) * 8);
            for (int i = 0; i < ms.Count; i++)
            {
                var m = ms[i];
                m.VelMotorKmh = 6.0 + Math.Sin(i / 10.0) * 2.5;   // ±40%
                ms[i] = m;
            }

            var d = QxPidAnalisis.Analizar(ms, Kp, Ki, Kd);
            Assert.That(d.Veredicto, Is.EqualTo(QxVeredicto.VelocidadDespareja));
            Assert.That(d.HayRecomendacion, Is.False,
                "con el target moviendose no se puede juzgar el PID");
            Assert.That(d.Explicacion, Does.Contain("recta"));
        }

        [Test]
        public void Motor_saturado_no_se_arregla_con_ganancias()
        {
            // Oscila Y esta contra el techo: gana el techo, porque subir o bajar
            // ganancias no le devuelve fuerza al motor.
            var ms = Corrida(300, i => 45 + Math.Sin(i / 4.0) * 6, loadPct: 100);
            var d = QxPidAnalisis.Analizar(ms, Kp, Ki, Kd);

            Assert.That(d.Veredicto, Is.EqualTo(QxVeredicto.Saturado));
            Assert.That(d.HayRecomendacion, Is.False);
            Assert.That(d.Explicacion, Does.Contain("cadena").Or.Contain("rodamiento"));
        }

        // ── Los diagnosticos ────────────────────────────────────────────────

        [Test]
        public void Rpm_firmes_en_el_objetivo_es_que_anda()
        {
            var d = QxPidAnalisis.Analizar(Corrida(300, i => 45), Kp, Ki, Kd);
            Assert.That(d.Veredicto, Is.EqualTo(QxVeredicto.Anda));
            Assert.That(d.HayRecomendacion, Is.False, "lo que anda no se toca");
        }

        [Test]
        public void Oscilando_con_velocidad_pareja_manda_bajar_ki()
        {
            // El caso del pedido: velocidad estable, rpm subiendo y bajando.
            var ms = Corrida(300, i => 45 + Math.Sin(i / 3.0) * 7);
            var d = QxPidAnalisis.Analizar(ms, Kp, Ki, Kd);

            Assert.That(d.Veredicto, Is.EqualTo(QxVeredicto.Oscila));
            Assert.That(d.HayRecomendacion, Is.True);
            Assert.That(d.Parametro, Is.EqualTo("ki"));
            Assert.That(d.ValorSugerido, Is.LessThan(Ki), "oscila: hay que BAJAR, no subir");
            Assert.That(d.OscilacionHz, Is.GreaterThan(0), "tiene que medir la frecuencia");
        }

        [Test]
        public void El_ajuste_es_gradual_no_un_salto()
        {
            var ms = Corrida(300, i => 45 + Math.Sin(i / 3.0) * 7);
            var d = QxPidAnalisis.Analizar(ms, Kp, Ki, Kd);

            double cambio = Math.Abs(d.ValorSugerido - d.ValorActual) / d.ValorActual;
            Assert.That(cambio, Is.LessThan(0.45),
                "un salto grande arregla el sintoma y desarma el motor que andaba");
        }

        [Test]
        public void Quedandose_corto_sin_oscilar_manda_subir()
        {
            // Rpm planchadas por debajo del objetivo: no oscila, falta empuje.
            var ms = Corrida(300, i => 38, rpmTarget: 45);
            var d = QxPidAnalisis.Analizar(ms, Kp, Ki, Kd);

            Assert.That(d.Veredicto, Is.AnyOf(QxVeredicto.Lento, QxVeredicto.ErrorConstante));
            Assert.That(d.HayRecomendacion, Is.True);
            Assert.That(d.ValorSugerido, Is.GreaterThan(d.ValorActual), "falta empuje: SUBIR");
        }

        // ── Bordes ──────────────────────────────────────────────────────────

        [Test]
        public void Una_ganancia_apagada_no_se_enciende_sola()
        {
            // Con Kd o Ki en cero el termino esta apagado a proposito.
            // Multiplicar cero por lo que sea sigue siendo cero: no se sugiere.
            var ms = Corrida(300, i => 45 + Math.Sin(i / 3.0) * 7);
            var d = QxPidAnalisis.Analizar(ms, Kp, 0, Kd);
            Assert.That(d.HayRecomendacion, Is.False);
        }

        [Test]
        public void Sin_muestras_no_explota()
        {
            var d = QxPidAnalisis.Analizar(new List<QxPidSample>(), Kp, Ki, Kd);
            Assert.That(d.Veredicto, Is.EqualTo(QxVeredicto.SinDatos));
            Assert.That(d.HayRecomendacion, Is.False);

            var d2 = QxPidAnalisis.Analizar(null, Kp, Ki, Kd);
            Assert.That(d2.Veredicto, Is.EqualTo(QxVeredicto.SinDatos));
        }

        [Test]
        public void El_motor_que_no_giro_no_recibe_diagnostico()
        {
            var ms = Corrida(300, i => 0, rpmTarget: 0, loadPct: 0);
            var d = QxPidAnalisis.Analizar(ms, Kp, Ki, Kd);
            Assert.That(d.Veredicto, Is.EqualTo(QxVeredicto.SinDatos));
            Assert.That(d.HayRecomendacion, Is.False);
        }

        [Test]
        public void Las_muestras_sin_dato_del_nodo_no_cuentan_como_cero()
        {
            var ms = Corrida(300, i => 45);
            for (int i = 0; i < 40; i++) { var m = ms[i]; m.RpmReal = null; ms[i] = m; }

            var d = QxPidAnalisis.Analizar(ms, Kp, Ki, Kd);
            Assert.That(d.Veredicto, Is.EqualTo(QxVeredicto.Anda),
                "los huecos del nodo no tienen que leerse como una caida a cero");
        }
    }
}
