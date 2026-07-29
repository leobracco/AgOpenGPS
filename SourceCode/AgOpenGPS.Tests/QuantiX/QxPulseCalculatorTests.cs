// Casos de borde de la cadena de dosis de QuantiX — los que rompen EN CAMPO,
// no los felices. Cada uno fija una decisión de seguridad: ante duda, motor
// quieto. Es preferible no sembrar a sembrar cualquier cosa.

using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgOpenGPS.Tests.QuantiX
{
    public class QxPulseCalculatorTests
    {
        // Sembradora típica: 12 surcos, plato de 100 semillas/vuelta, 24 dientes.
        private static QxPulseInput Semillas(double dosis, double velKmh, bool seccionOn = true) =>
            new QxPulseInput
            {
                Dosis = dosis,
                VelocidadKmh = velKmh,
                SeccionOn = seccionOn,
                EsSemillas = true,
                Surcos = 12,
                SemillasVuelta = 100,
                DientesEngranaje = 24,
            };

        // Fertilizadora: 28 m de ancho, 2 gramos por pulso.
        private static QxPulseInput Kilos(double dosis, double velKmh, bool seccionOn = true) =>
            new QxPulseInput
            {
                Dosis = dosis,
                VelocidadKmh = velKmh,
                SeccionOn = seccionOn,
                EsSemillas = false,
                AnchoM = 28,
                MeterCal = 2.0,
            };

        // ---- Tractor parado -------------------------------------------------

        [Test]
        public void Parado_no_gira_el_motor()
        {
            Assert.That(QxPulseCalculator.Pps(Semillas(6, 0)), Is.Zero,
                "con el tractor parado el motor tiene que estar quieto");
            Assert.That(QxPulseCalculator.Pps(Kilos(150, 0)), Is.Zero);
        }

        [Test]
        public void Casi_parado_tampoco_gira()
        {
            // El piso existe porque la dosis por hectarea tiende a infinito
            // cuando la velocidad tiende a cero: sin el, el motor se embala.
            Assert.That(QxPulseCalculator.Pps(Kilos(150, 0.4)), Is.Zero);
            Assert.That(QxPulseCalculator.Pps(Kilos(150, 0.6)), Is.GreaterThan(0),
                "apenas arranca a moverse ya tiene que dosificar");
        }

        // ---- Seccion cerrada ------------------------------------------------

        [Test]
        public void Seccion_cerrada_para_el_motor()
        {
            Assert.That(QxPulseCalculator.Pps(Semillas(6, 8, seccionOn: false)), Is.Zero,
                "si el cuerpo esta levantado no se siembra aunque el tractor avance");
        }

        // ---- Falta de calibracion (el caso silencioso) -----------------------

        [Test]
        public void Sin_calibracion_de_producto_no_gira()
        {
            var e = Kilos(150, 8);
            e.MeterCal = 0;                       // nunca se calibro
            Assert.That(QxPulseCalculator.Pps(e), Is.Zero,
                "sin gramos-por-pulso no hay forma de saber cuanto entrega: motor quieto");
        }

        [Test]
        public void Sin_semillas_por_vuelta_no_gira()
        {
            var e = Semillas(6, 8);
            e.SemillasVuelta = 0;
            Assert.That(QxPulseCalculator.Pps(e), Is.Zero);
        }

        // ---- Que la cuenta sea la correcta ----------------------------------

        [Test]
        public void Semillas_la_cuenta_cierra()
        {
            // 6 sem/m, 12 surcos, 7,2 km/h = 2 m/s  ->  6*2*12 = 144 sem/s
            // plato 100 sem/vuelta con 24 dientes   ->  100/24 = 4,1667 sem/pulso
            // pps = 144 / 4,1667 = 34,56
            double pps = QxPulseCalculator.Pps(Semillas(6, 7.2));
            Assert.That(pps, Is.EqualTo(34.56).Within(0.01));
        }

        [Test]
        public void Kilos_la_cuenta_cierra()
        {
            // 150 kg/ha, 28 m, 7,2 km/h = 2 m/s
            // g/s = 150*1000*28*2/10000 = 840 g/s ; a 2 g/pulso -> 420 pps
            double pps = QxPulseCalculator.Pps(Kilos(150, 7.2));
            Assert.That(pps, Is.EqualTo(420).Within(0.01));
        }

        [Test]
        public void Al_doble_de_velocidad_el_doble_de_pulsos()
        {
            // La dosis por hectarea se mantiene: el motor compensa la velocidad.
            double lento = QxPulseCalculator.Pps(Kilos(150, 6));
            double rapido = QxPulseCalculator.Pps(Kilos(150, 12));
            Assert.That(rapido, Is.EqualTo(lento * 2).Within(0.01));
        }

        [Test]
        public void Cambiar_el_insumo_cambia_los_pulsos()
        {
            // Mismo pedido de dosis, calibracion distinta (producto mas denso):
            // la mitad de gramos por pulso = el doble de pulsos.
            var a = Kilos(150, 8);
            var b = Kilos(150, 8);
            b.MeterCal = 1.0;
            Assert.That(QxPulseCalculator.Pps(b),
                Is.EqualTo(QxPulseCalculator.Pps(a) * 2).Within(0.01));
        }

        // ---- Lo que ve el operario ------------------------------------------

        [Test]
        public void Rpm_es_lo_que_se_muestra_no_el_pps()
        {
            // 34,56 pps con 24 dientes -> 86,4 rpm
            double rpm = QxPulseCalculator.Rpm(34.56, 24);
            Assert.That(rpm, Is.EqualTo(86.4).Within(0.01));
        }

        [Test]
        public void Sin_dientes_configurados_asume_el_default()
        {
            Assert.That(QxPulseCalculator.Rpm(24, 0),
                Is.EqualTo(QxPulseCalculator.Rpm(24, QxPulseCalculator.DientesPorDefecto)));
        }
    }
}
