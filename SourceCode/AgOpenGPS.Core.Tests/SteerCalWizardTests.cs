// ============================================================================
// SteerCalWizardTests.cs — ASISTENTE DE CALIBRACIÓN DE LA DIRECCIÓN.
//
// La máquina de estados es pura: acá se la maneja con una planta simulada
// chica (placa AiO Keya.hex: WAS analógico + lazo P del firmware + motor con
// fricción, retardo e inercia + corriente). La prueba "de verdad" contra el
// actuador de BenchX está en BenchX.Tests/AsistenteDireccionBenchTests.cs.
//
// Qué se prueba:
//   · candados: hombre muerto, latido vencido, velocidad (parado / andando),
//     sin velocidad y sin fix fallan cerrado, setpoint acotado, escape por
//     tope, motor invertido, piloto/secciones/U-turn/switch;
//   · cero promedio en recta, cuentas/grado y Ackermann con un círculo de
//     radio conocido, umbral de corriente, Kp elegido contra la planta;
//   · cancelar / abortar → la placa vuelve a la config previa; tope de
//     grabaciones por paso; aplicar y deshacer.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenGPS.SteerCal;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class SteerCalWizardTests
    {
        // ---------------------------------------------------------------------
        // Planta: placa AiO (Keya.hex) + WAS + motor. Mismo criterio que el
        // ActuadorDireccion de BenchX, en chico.
        // ---------------------------------------------------------------------
        private sealed class Planta
        {
            public SteerCalConfig Placa;

            // sensor físico (lo que la calibración tiene que deshacer)
            public double CuentasReales = 100;   // por grado
            public double OffsetGrados;          // lee esto con la rueda derecha
            public double AckermannReal = 1.0;   // lado izquierdo: fracción de cuentas
            public bool SensorInvertido;

            // motor
            public bool MotorInvertido;
            public double PwmMinimoReal = 22;
            public double VelMaxGradS = 35;
            public double RetardoS = 0.05;
            public double Tau = 0.08;
            public bool Agarrado;
            public double CorrientePorPwm = 0.1;

            public double AnguloFisico;
            public double Velocidad;
            public double Corriente;
            public int Pwm;
            public bool SwitchAbierto;
            private readonly Queue<int> _cola = new Queue<int>();

            public double AnguloWas
            {
                get
                {
                    double ang = AnguloFisico + OffsetGrados;
                    double crudo = ang * CuentasReales;
                    if (ang < 0) crudo *= AckermannReal;
                    if (SensorInvertido) crudo = -crudo;
                    double a = Placa.InvertWas
                        ? (crudo - Placa.WasOffset) / -Placa.CountsPerDegree
                        : (crudo + Placa.WasOffset) / Placa.CountsPerDegree;
                    if (a < 0) a *= Placa.Ackerman * 0.01;
                    return a;
                }
            }

            public void Avanzar(double dt, bool enganchado, double sp, double velKmh)
            {
                const double h = 0.01;
                int n = (int)Math.Round(dt / h);
                for (int i = 0; i < n; i++) Paso(h, enganchado && !SwitchAbierto, sp, velKmh);
            }

            private void Paso(double h, bool enganchado, double sp, double velKmh)
            {
                int pwm = 0;
                if (enganchado)
                {
                    double error = AnguloWas - sp;
                    if (velKmh < 0.2) error = 0;
                    pwm = (int)(Placa.Kp * error);
                    double low = Placa.HighPwm / 3, high = Placa.HighPwm;
                    double ea = Math.Abs(error);
                    int max = ea < 1.5 ? (int)(ea * ((high - low) / 1.5) + low) : (int)high;
                    if (pwm < 0) pwm -= Placa.MinPwm; else if (pwm > 0) pwm += Placa.MinPwm;
                    if (pwm > max) pwm = max;
                    if (pwm < -max) pwm = -max;
                    if (Placa.InvertSteer) pwm = -pwm;
                }
                Pwm = pwm;

                int pasos = (int)Math.Round(RetardoS / h);
                _cola.Enqueue(pwm);
                while (_cola.Count > pasos + 1) _cola.Dequeue();
                int aplicado = _cola.Count > pasos ? _cola.Peek() : 0;

                double objetivo = 0;
                int mag = Math.Abs(aplicado);
                if (mag > PwmMinimoReal)
                {
                    objetivo = Math.Min(VelMaxGradS, (mag - PwmMinimoReal) / (255 - PwmMinimoReal) * VelMaxGradS);
                    objetivo *= -Math.Sign(aplicado);
                    if (MotorInvertido) objetivo = -objetivo;
                }
                Velocidad += (objetivo - Velocidad) * Math.Min(1, h / Tau);
                if (Agarrado) Velocidad = 0;
                AnguloFisico = Math.Max(-45, Math.Min(45, AnguloFisico + Velocidad * h));

                double corr = CorrientePorPwm * Math.Abs(aplicado) + (Agarrado && aplicado != 0 ? 150 : 0);
                Corriente = Math.Min(255, Corriente * 0.7 + corr * 0.3);
                if (Placa.CurrentSensor && Corriente >= Placa.SensorLimit) SwitchAbierto = true;
            }
        }

        // ---------------------------------------------------------------------
        // Banco: planta + entradas + reloj
        // ---------------------------------------------------------------------
        private SteerCalWizard _w;
        private Planta _p;
        private double _t;
        private bool _apretado;
        private int _grabaciones;
        private int _restauraciones;
        private readonly List<SteerCalPedido> _pedidos = new List<SteerCalPedido>();

        // tractor
        private double _velKmh;
        private double _rumbo, _este, _norte;
        private bool _fix = true, _hayVel = true, _piloto, _secciones, _uturn, _manejoLibre;
        private double? _edad253 = 0.05;
        private double _distEjes = 3.0;

        private static SteerCalConfig ConfigBase()
        {
            return new SteerCalConfig
            {
                Kp = 50,
                MinPwm = 22,
                HighPwm = 180,
                WasOffset = 0,
                CountsPerDegree = 100,
                Ackerman = 100,
                InvertWas = false,
                InvertSteer = false,
                CurrentSensor = true,
                SensorLimit = 120,
                MaxSteerAngle = 35,
                WheelbaseM = 3.0,
                StanleyUsed = false,
                HoldLookAhead = 30,
                LookAheadMult = 14,
                AcquireFactor = 90,
                IntegralPp = 0,
            };
        }

        [SetUp]
        public void SetUp()
        {
            _w = new SteerCalWizard();
            _p = new Planta { Placa = ConfigBase() };
            _t = 100;
            _apretado = false;
            _grabaciones = _restauraciones = 0;
            _pedidos.Clear();
            _velKmh = 0;
            _rumbo = _este = _norte = 0;
            _fix = _hayVel = true;
            _piloto = _secciones = _uturn = _manejoLibre = false;
            _edad253 = 0.05;
        }

        private SteerCalEntrada Entrada()
        {
            return new SteerCalEntrada
            {
                T = _t,
                Edad253 = _edad253,
                AnguloWas = _p.AnguloWas,
                Pwm = _p.Pwm,
                Corriente = (int)Math.Round(_p.Corriente),
                VelKmh = _velKmh,
                HayVelocidad = _hayVel,
                FixValido = _fix,
                RumboRad = _rumbo,
                Este = _este,
                Norte = _norte,
                SeccionesPintando = _secciones,
                UTurn = _uturn,
                SwitchAbierto = _p.SwitchAbierto,
                PilotoPuesto = _piloto,
                ManejoLibre = _manejoLibre,
            };
        }

        /// <summary>Corre la simulación a 10 Hz. El tractor avanza en bicicleta con la rueda física.</summary>
        private void Correr(double segundos, Action cadaTick = null)
        {
            int n = (int)Math.Round(segundos / 0.1);
            for (int i = 0; i < n; i++)
            {
                _t += 0.1;
                _p.Avanzar(0.1, _w.MotorActivo, _w.Setpoint, _w.VelocidadPgnKmh);

                double v = _velKmh / 3.6;
                double delta = _p.AnguloFisico * Math.PI / 180;
                _rumbo += v / _distEjes * Math.Tan(delta) * 0.1;
                _este += v * Math.Sin(_rumbo) * 0.1;
                _norte += v * Math.Cos(_rumbo) * 0.1;

                cadaTick?.Invoke();
                if (_apretado) _w.Latido(true, _t);
                _w.Tick(Entrada());
                Drenar();
            }
        }

        private void Drenar()
        {
            foreach (var p in _w.TomarPedidos())
            {
                _pedidos.Add(p);
                if (p == SteerCalPedido.EscribirPlaca) { _p.Placa = _w.EnPlaca.Clone(); _grabaciones++; }
                if (p == SteerCalPedido.Restaurar) { _p.Placa = _w.Original.Clone(); _restauraciones++; }
            }
            _w.TomarEventos();
        }

        private void Iniciar(SteerCalConfig cfg = null)
        {
            cfg = cfg ?? _p.Placa.Clone();
            _p.Placa = cfg.Clone();
            Assert.That(_w.Iniciar(cfg, _t), Is.True);
            Correr(0.2);
        }

        private void Hacer(string accion)
        {
            Assert.That(_w.Accion(accion), Is.True, "acción '" + accion + "' en " + _w.Paso + "/" + _w.Fase + ": " + _w.Mensaje);
            Drenar();
        }

        /// <summary>Pasa P0 y P1 (sensor bien) y deja el asistente en P2.</summary>
        private void HastaSentidoMotor()
        {
            Iniciar();
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
            Hacer("siguiente");
            Assert.That(_w.Paso, Is.EqualTo(SteerCalPaso.SentidoWas));
            Hacer("empezar");
            Correr(0.3, () => _p.AnguloFisico = 0);
            Correr(1.0, () => _p.AnguloFisico = 10);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
            _p.AnguloFisico = 0;
            Correr(0.1);
            Hacer("siguiente");
            Assert.That(_w.Paso, Is.EqualTo(SteerCalPaso.SentidoMotor));
        }

        private void IrAlPaso(SteerCalPaso destino)
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _apretado = true;
            Correr(3);
            _apretado = false;
            Correr(0.2);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
            Hacer("siguiente");
            while (_w.Paso != destino)
            {
                if (_w.PuedeSaltar) Hacer("saltar");
                else Hacer("siguiente");
            }
        }

        // =====================================================================
        // P0 — chequeo
        // =====================================================================

        [Test]
        public void Chequeo_TodoBien_DejaSeguir()
        {
            Iniciar();
            Assert.That(_w.Paso, Is.EqualTo(SteerCalPaso.Chequeo));
            Assert.That(_w.PuedeSiguiente, Is.True, _w.Mensaje);
            Assert.That(_w.Chequeos.All(c => c.Ok), Is.True);
        }

        [Test]
        public void Chequeo_SinPgn253_NoDejaSeguir()
        {
            _edad253 = null;
            Iniciar();
            Assert.That(_w.PuedeSiguiente, Is.False);
            Assert.That(_w.Chequeos.Single(c => c.Texto.Contains("253")).Ok, Is.False);
        }

        [Test]
        public void Chequeo_SinCortePorCorriente_NoDejaSeguir()
        {
            var cfg = ConfigBase();
            cfg.CurrentSensor = false;
            Iniciar(cfg);
            Assert.That(_w.PuedeSiguiente, Is.False);
        }

        [Test]
        public void Chequeo_ConPilotoOSeccionesOSwitchAbierto_NoDejaSeguir()
        {
            _piloto = true;
            Iniciar();
            Assert.That(_w.PuedeSiguiente, Is.False);
            _piloto = false; _secciones = true;
            Correr(0.1);
            Assert.That(_w.PuedeSiguiente, Is.False);
            _secciones = false; _p.SwitchAbierto = true;
            Correr(0.1);
            Assert.That(_w.PuedeSiguiente, Is.False);
            _p.SwitchAbierto = false;
            Correr(0.1);
            Assert.That(_w.PuedeSiguiente, Is.True);
        }

        [Test]
        public void Iniciar_DosVeces_NoPisaElEnCurso()
        {
            Iniciar();
            Assert.That(_w.Iniciar(ConfigBase(), _t), Is.False);
        }

        // =====================================================================
        // P1 — sentido del WAS
        // =====================================================================

        [Test]
        public void SentidoWas_Bien_NoGrabaNada()
        {
            HastaSentidoMotor();
            Assert.That(_grabaciones, Is.EqualTo(0));
            Assert.That(_w.Trabajo.InvertWas, Is.False);
        }

        [Test]
        public void SentidoWas_AlReves_ProponeInvertir_YAlAceptarGrabaUnaVez()
        {
            _p.SensorInvertido = true;
            Iniciar();
            Hacer("siguiente");
            Hacer("empezar");
            Correr(1.0, () => _p.AnguloFisico = 10);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta));
            Assert.That(_w.Propuesta[0].Despues, Is.EqualTo("Sí"));

            Hacer("aceptar");
            Assert.That(_grabaciones, Is.EqualTo(1));
            Assert.That(_p.Placa.InvertWas, Is.True);

            // Confirmación: ahora marca bien.
            _p.AnguloFisico = 0;
            Correr(0.1);
            Hacer("empezar");
            Correr(1.0, () => _p.AnguloFisico = 10);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
        }

        [Test]
        public void SentidoWas_EnMovimiento_NoMide()
        {
            Iniciar();
            Hacer("siguiente");
            _velKmh = 3;
            Correr(0.1);
            Assert.That(_w.Accion("empezar"), Is.False, "andando no arranca la medida");
        }

        // =====================================================================
        // Candados del motor (P2)
        // =====================================================================

        [Test]
        public void Motor_SinHombreMuerto_NoSeMueve()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            Correr(2);
            Assert.That(_w.MotorActivo, Is.False);
            Assert.That(_p.AnguloFisico, Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void Motor_LatidoVencido_CortaEnMedioSegundo()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _w.Latido(true, _t);
            _w.Tick(Entrada());
            Assert.That(_w.MotorActivo, Is.True);

            // La pantalla deja de latir (sin soltar): a los 0,6 s ya no hay motor.
            _t += 0.6;
            _w.Tick(Entrada());
            Assert.That(_w.MotorActivo, Is.False);
        }

        [Test]
        public void Motor_SoltarElBoton_CortaEnElActo()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _w.Latido(true, _t);
            _w.Tick(Entrada());
            Assert.That(_w.MotorActivo, Is.True);
            _w.Latido(false, _t);
            Assert.That(_w.MotorActivo, Is.False);
        }

        [Test]
        public void Motor_EnPasoParado_LlevaVelocidadFalsaDeMedioKmh_NoOcho()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _w.Latido(true, _t);
            _w.Tick(Entrada());
            Assert.That(_w.MotorActivo, Is.True);
            Assert.That(_w.VelocidadPgnKmh, Is.EqualTo(0.5));
        }

        [Test]
        public void Motor_ParadoConElTractorAndando_NoSeMueve()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _velKmh = 1.0;
            _apretado = true;
            Correr(1);
            Assert.That(_w.MotorActivo, Is.False);
            Assert.That(_w.Mensaje, Does.Contain("Pará"));
        }

        [Test]
        public void Motor_SinVelocidad_FallaCerrado()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _hayVel = false;
            _apretado = true;
            Correr(1);
            Assert.That(_w.MotorActivo, Is.False);
        }

        [Test]
        public void Motor_SinFix_NoSeMueve()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _fix = false;
            _apretado = true;
            Correr(1);
            Assert.That(_w.MotorActivo, Is.False);
        }

        [Test]
        public void Motor_ConPilotoSeccionesUturnOManejoLibre_NoSeMueve()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _apretado = true;
            foreach (Action poner in new Action[]
            {
                () => _piloto = true, () => _secciones = true, () => _uturn = true, () => _manejoLibre = true,
                () => _edad253 = 2.0,
            })
            {
                _piloto = _secciones = _uturn = _manejoLibre = false; _edad253 = 0.05;
                poner();
                Correr(0.3);
                Assert.That(_w.MotorActivo, Is.False, _w.Mensaje);
            }
        }

        [Test]
        public void Motor_SetpointAcotadoACincoGrados()
        {
            HastaSentidoMotor();
            _p.AnguloFisico = 1.9;   // +3 serían 4,9; con las ruedas en 1,9 el tope no se pasa
            Correr(0.1);
            Hacer("empezar");
            _apretado = true;
            double maxSp = 0;
            Correr(3, () => maxSp = Math.Max(maxSp, Math.Abs(_w.Setpoint)));
            Assert.That(maxSp, Is.LessThanOrEqualTo(SteerCalWizard.SetpointMaxGrados));
        }

        [Test]
        public void Motor_Bien_LlegaATresGradosYTermina()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _apretado = true;
            Correr(3);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
            Assert.That(_p.AnguloFisico, Is.EqualTo(3).Within(1.0));
            Assert.That(_w.MotorActivo, Is.False);
        }

        [Test]
        public void Motor_Invertido_LoDetectaCortaYProponeInvertir()
        {
            _p.MotorInvertido = true;
            HastaSentidoMotor();
            Hacer("empezar");
            _apretado = true;
            double peor = 0;
            Correr(2, () => peor = Math.Min(peor, _p.AnguloFisico));
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
            Assert.That(_w.MotorActivo, Is.False, "corta en el acto");
            Assert.That(peor, Is.GreaterThan(-6), "no se puede ir lejos para el otro lado");
            Assert.That(_w.Propuesta[0].Campo, Does.Contain("motor"));

            Hacer("aceptar");
            Assert.That(_p.Placa.InvertSteer, Is.True);
            _apretado = false;
            Correr(0.2);
            _p.AnguloFisico = 0;
            Correr(0.1);
            Hacer("empezar");
            _apretado = true;
            Correr(3);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
        }

        [Test]
        public void Motor_PasaElTope_Aborta()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _apretado = true;
            Correr(0.1);
            Assert.That(_w.MotorActivo, Is.True);
            // Algo empuja la rueda más allá del tope (35 + 5).
            Correr(0.1, () => _p.AnguloFisico = 41);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Error));
            Assert.That(_w.MotorActivo, Is.False);
            Assert.That(_w.Mensaje, Does.Contain("tope"));
        }

        [Test]
        public void Motor_NoSePuedenSaltearLosPasosDeSentido()
        {
            HastaSentidoMotor();
            Assert.That(_w.PuedeSaltar, Is.False);
            Assert.That(_w.Accion("saltar"), Is.False);
        }

        // =====================================================================
        // P3 — cero promedio en recta
        // =====================================================================

        [Test]
        public void Cero_PromedioEnRecta_ProponeElOffsetYGrabaUnaSolaVez()
        {
            _p.OffsetGrados = 1.2;    // el sensor lee 1,2° con la rueda derecha
            IrAlPaso(SteerCalPaso.CeroWas);
            int antes = _grabaciones;
            Hacer("empezar");
            _velKmh = 4.5;
            // Rueda física derecha → el tractor va derecho; el WAS marca 1,2.
            Correr(60, () => _p.AnguloFisico = 0);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
            Assert.That(_w.Propuesta[0].Despues, Is.EqualTo("-120"));   // 0 − 100 × 1,2

            Hacer("aceptar");
            Assert.That(_grabaciones - antes, Is.EqualTo(1));
            Assert.That(_p.AnguloWas, Is.EqualTo(0).Within(0.02));
            Assert.That(_w.PuedeRepetir, Is.False, "el cero se graba UNA vez");
        }

        [Test]
        public void Cero_SiDobla_SeReiniciaLaRecta()
        {
            _p.OffsetGrados = 1.0;
            IrAlPaso(SteerCalPaso.CeroWas);
            Hacer("empezar");
            _velKmh = 4;
            Correr(20, () => _p.AnguloFisico = 0);           // ~22 m
            Correr(3, () => _p.AnguloFisico = 8);            // dobla
            Assert.That(_w.Progreso, Is.LessThan(0.5));
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Midiendo));
        }

        [Test]
        public void Cero_FueraDeVelocidad_NoMide()
        {
            _p.OffsetGrados = 1.0;
            IrAlPaso(SteerCalPaso.CeroWas);
            Hacer("empezar");
            _velKmh = 8;
            Correr(40, () => _p.AnguloFisico = 0);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Midiendo));
            Assert.That(_w.Progreso, Is.EqualTo(0));
        }

        [Test]
        public void Calibracion_OffsetParaCero_DeshaceElAckermannDelLadoIzquierdo()
        {
            var c = ConfigBase();
            c.Ackerman = 80;
            // Marca −0,8 con Ackermann 80: crudo −1,0 → +100 cuentas.
            Assert.That(Calibracion.OffsetParaCero(c, -0.8), Is.EqualTo(100));
            Assert.That(Calibracion.OffsetParaCero(c, 0.5), Is.EqualTo(-50));
        }

        // =====================================================================
        // P4 — cuentas/grado y Ackermann con un círculo conocido
        // =====================================================================

        [Test]
        public void Bicicleta_CirculoConocido_DaElAnguloReal()
        {
            // R = L / tan δ → ω = v / R
            double L = 3.0, delta = 15, v = 1.25;
            double r = L / Math.Tan(delta * Math.PI / 180);
            Assert.That(Calibracion.AnguloBicicletaGrados(L, v / r, v), Is.EqualTo(15).Within(1e-9));
        }

        [Test]
        public void Circulos_SensorQueExageraYLadoIzquierdoCorto_CorrigeCuentasYAckermann()
        {
            _p.CuentasReales = 110;      // la placa usa 100: marca 10 % de más
            _p.AckermannReal = 0.9;      // del lado izquierdo el sensor da 10 % menos cuentas
            IrAlPaso(SteerCalPaso.CuentasAckermann);

            Hacer("empezar");
            _velKmh = 4.5;
            Correr(7, () => _p.AnguloFisico = 15);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Instrucciones), _w.Mensaje);   // ahora la izquierda
            Hacer("empezar");
            Correr(7, () => _p.AnguloFisico = -15);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);

            int cuentas = int.Parse(_w.Propuesta.Single(x => x.Campo.StartsWith("Cuentas")).Despues);
            int ack = int.Parse(_w.Propuesta.Single(x => x.Campo.StartsWith("Ackermann")).Despues);
            Assert.That(cuentas, Is.InRange(109, 111));
            Assert.That(ack, Is.InRange(109, 113), "con 110 cuentas el izquierdo marca 13,5 donde son 15 → ~111 %");

            Hacer("aceptar");
            Assert.That(_p.AnguloWas, Is.EqualTo(-15).Within(0.4));
            Correr(0.1, () => _p.AnguloFisico = 15);
            Assert.That(_p.AnguloWas, Is.EqualTo(15).Within(0.3));
        }

        [Test]
        public void Calibracion_CuentasYAckermann_Formulas()
        {
            Assert.That(Calibracion.CuentasNuevas(100, 16.5, 15), Is.EqualTo(110));
            Assert.That(Calibracion.CuentasNuevas(100, 15, 15), Is.EqualTo(100));
            // Izquierda marca −13,5 donde son −15, con cuentas que no cambian → 111 %.
            Assert.That(Calibracion.AckermannNuevo(100, 100, 100, -13.5, -15), Is.EqualTo(111));
        }

        // =====================================================================
        // P5 — PWM mínimo
        // =====================================================================

        [Test]
        public void PwmMinimo_EnRango_NoSeToca()
        {
            IrAlPaso(SteerCalPaso.PwmMinimo);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho));
        }

        [Test]
        public void PwmMinimo_FueraDeRango_ProponeVeintidos()
        {
            var cfg = ConfigBase();
            cfg.MinPwm = 40;
            _p.Placa = cfg;
            IrAlPaso(SteerCalPaso.PwmMinimo);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta));
            Assert.That(_w.Propuesta[0].Despues, Is.EqualTo("22"));
            Hacer("aceptar");
            Assert.That(_p.Placa.MinPwm, Is.EqualTo(22));
        }

        // =====================================================================
        // P6 — Kp elegido contra la planta, andando a 1–2 km/h
        // =====================================================================

        private void CorrerGanancia()
        {
            IrAlPaso(SteerCalPaso.Ganancia);
            Hacer("empezar");
            _velKmh = 1.5;
            _apretado = true;
            for (int i = 0; i < 1200 && _w.Fase == SteerCalFase.Midiendo; i++)
                Correr(0.1, () => { _rumbo = 0; });   // el operario corrige: sigue derecho
            _apretado = false;
        }

        [Test]
        public void Ganancia_EligeLaMayorQueNoSePasaNiOscila()
        {
            CorrerGanancia();
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta).Or.EqualTo(SteerCalFase.Hecho), _w.Mensaje);

            int kp = _w.Fase == SteerCalFase.Propuesta
                ? int.Parse(_w.Propuesta.Single(x => x.Campo.StartsWith("Ganancia")).Despues)
                : _w.Trabajo.Kp;
            Assert.That(SteerCalWizard.CandidatosKp, Does.Contain(kp));
            TestContext.Out.WriteLine("Kp elegido " + kp + " — " + _w.Medicion);

            // Re-simular el elegido: sobrepico < 10 %.
            var r = Escalon(kp, _w.Fase == SteerCalFase.Propuesta && _w.Propuesta.Any(x => x.Campo == "PWM alto")
                ? int.Parse(_w.Propuesta.Single(x => x.Campo == "PWM alto").Despues) : 180);
            Assert.That(r.Bueno, Is.True, "sobrepico " + r.SobrepicoPct + " %, oscila " + r.Oscila);
        }

        [Test]
        public void Ganancia_MotorMasLento_EligeMasGanancia_YLaSiguienteSePasa()
        {
            _p.VelMaxGradS = 14;
            _p.Tau = 0.15;
            CorrerGanancia();
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
            int kp = int.Parse(_w.Propuesta.Single(x => x.Campo.StartsWith("Ganancia")).Despues);
            TestContext.Out.WriteLine("Kp elegido (motor lento) " + kp + " — " + _w.Medicion);
            Assert.That(kp, Is.GreaterThan(SteerCalWizard.CandidatosKp[0]));

            int i = Array.IndexOf(SteerCalWizard.CandidatosKp, kp);
            if (i < SteerCalWizard.CandidatosKp.Length - 1)
            {
                var pl = new Planta { Placa = ConfigBase(), VelMaxGradS = 14, Tau = 0.15 };
                var siguiente = EscalonEn(pl, SteerCalWizard.CandidatosKp[i + 1], 180);
                Assert.That(siguiente.Bueno, Is.False, "la siguiente ganancia se pasa u oscila");
            }
        }

        [Test]
        public void Ganancia_RespetaElTopeDeGrabacionesYRestauraAlRechazar()
        {
            CorrerGanancia();
            Assume.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta));
            Assert.That(_w.EscriturasPaso, Is.LessThanOrEqualTo(SteerCalWizard.TopeEscrituras(SteerCalPaso.Ganancia)));
            Hacer("rechazar");
            Assert.That(_p.Placa.Kp, Is.EqualTo(50), "rechazar vuelve la placa a la ganancia de antes");
        }

        [Test]
        public void Ganancia_FueraDeUnoADosKmh_SePausa()
        {
            IrAlPaso(SteerCalPaso.Ganancia);
            Hacer("empezar");
            _velKmh = 4;
            _apretado = true;
            Correr(2);
            Assert.That(_w.MotorActivo, Is.False);
            Assert.That(_w.Mensaje, Does.Contain("1 y 2 km/h"));
        }

        [Test]
        public void Ganancia_MotorQueSeVaAlReves_AbortaYRestaura()
        {
            IrAlPaso(SteerCalPaso.Ganancia);
            _p.MotorInvertido = true;   // se rompió algo después de P2
            Hacer("empezar");
            _velKmh = 1.5;
            _apretado = true;
            Correr(5);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Error), _w.Mensaje);
            Assert.That(_w.MotorActivo, Is.False);
            Assert.That(_p.Placa.Kp, Is.EqualTo(_w.Trabajo.Kp), "la placa vuelve a lo aceptado");
        }

        /// <summary>Escalón 0→5° en la planta con un Kp dado, analizado igual que el asistente.</summary>
        private RespuestaEscalon Escalon(int kp, int alto)
        {
            return EscalonEn(new Planta { Placa = ConfigBase() }, kp, alto);
        }

        private static RespuestaEscalon EscalonEn(Planta pl, int kp, int alto)
        {
            pl.Placa.Kp = kp;
            pl.Placa.HighPwm = alto;
            var t = new List<double>(); var a = new List<double>(); var pwm = new List<int>();
            for (int i = 1; i <= 25; i++)
            {
                pl.Avanzar(0.1, true, 5, 1.5);
                t.Add(i * 0.1); a.Add(pl.AnguloWas); pwm.Add(pl.Pwm);
            }
            return RespuestaEscalon.Analizar(t, a, pwm, 0, 5, alto);
        }

        [Test]
        public void RespuestaEscalon_DetectaSobrepicoYOscilacion()
        {
            var t = new List<double> { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8 };
            var limpio = new List<double> { 1, 3, 4.6, 4.9, 5.0, 5.0, 5.0, 5.0 };
            var r1 = RespuestaEscalon.Analizar(t, limpio, null, 0, 5, 180);
            Assert.That(r1.Bueno, Is.True);
            Assert.That(r1.SobrepicoPct, Is.EqualTo(0));

            var pasado = new List<double> { 2, 4.6, 6.0, 5.8, 5.2, 5.0, 5.0, 5.0 };
            var r2 = RespuestaEscalon.Analizar(t, pasado, null, 0, 5, 180);
            Assert.That(r2.SobrepicoPct, Is.EqualTo(20).Within(1e-6));
            Assert.That(r2.Bueno, Is.False);

            var oscila = new List<double> { 3, 4.6, 5.4, 4.6, 5.4, 4.6, 5.4, 4.6 };
            var r3 = RespuestaEscalon.Analizar(t, oscila, null, 0, 5, 180);
            Assert.That(r3.Oscila, Is.True);
        }

        // =====================================================================
        // P7 — umbral de corte por corriente
        // =====================================================================

        [Test]
        public void Calibracion_UmbralCorriente_ConMargen()
        {
            Assert.That(Calibracion.UmbralCorriente(80), Is.EqualTo(120));   // ×1,5
            Assert.That(Calibracion.UmbralCorriente(30), Is.EqualTo(55));    // +25
            Assert.That(Calibracion.UmbralCorriente(5), Is.EqualTo(30));     // piso
        }

        [Test]
        public void Corte_MideLaCorrienteNormal_ProponeUmbral_YAgarrarElVolanteCorta()
        {
            IrAlPaso(SteerCalPaso.CorteCorriente);
            Hacer("empezar");
            _velKmh = 1.5;
            _apretado = true;
            for (int i = 0; i < 600 && _w.Fase == SteerCalFase.Midiendo; i++) Correr(0.1, () => _rumbo = 0);
            _apretado = false;
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
            int umbral = int.Parse(_w.Propuesta[0].Despues);
            Assert.That(umbral, Is.GreaterThan(20).And.LessThanOrEqualTo(SteerCalWizard.UmbralCorrienteMax));

            Hacer("aceptar");
            Assert.That(_p.Placa.SensorLimit, Is.EqualTo(umbral));
            Assert.That(_p.SwitchAbierto, Is.False, "andando normal NO cortó");

            // Prueba de agarrar: parado.
            _velKmh = 0;
            Correr(0.2);
            Hacer("empezar");
            _apretado = true;
            Correr(1);
            _p.Agarrado = true;
            Correr(3);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
            Assert.That(_w.Mensaje, Does.Contain("Cortó"));
            Assert.That(_w.MotorActivo, Is.False);
        }

        [Test]
        public void Corte_SiCortaAndandoNormal_ProponeSubirElUmbral()
        {
            IrAlPaso(SteerCalPaso.CorteCorriente);
            _p.CorrientePorPwm = 1.0;   // motor duro: andando normal ya pasa el umbral 120
            Hacer("empezar");
            _velKmh = 1.5;
            _apretado = true;
            for (int i = 0; i < 200 && _w.Fase == SteerCalFase.Midiendo; i++) Correr(0.1, () => _rumbo = 0);
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
            Assert.That(int.Parse(_w.Propuesta[0].Despues), Is.GreaterThan(120));
        }

        // =====================================================================
        // P8 — ajuste fino
        // =====================================================================

        [Test]
        public void AjusteFino_Serpentea_ProponeMirarMasLejos_NoAplicaSolo()
        {
            IrAlPaso(SteerCalPaso.AjusteFino);
            Hacer("empezar");
            _velKmh = 8;
            _piloto = true;
            var cfgAntes = _w.Trabajo.Clone();
            // Simulación directa del error a la guía: oscila ±6 cm cada 6 s.
            double t0 = _t;
            for (int i = 0; i < 1600 && _w.Fase == SteerCalFase.Midiendo; i++)
            {
                _t += 0.1;
                _norte += 8 / 3.6 * 0.1;
                var e = Entrada();
                e.HayGuiaRecta = true;
                e.ErrorLineaM = 0.06 * Math.Sin(2 * Math.PI * (_t - t0) / 6.0);
                _w.Tick(e);
                Drenar();
            }
            Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
            Assert.That(_w.Trabajo.MismoTodo(cfgAntes), Is.True, "propone, no aplica");
            Hacer("aceptar");
            Assert.That(_w.Trabajo.HoldLookAhead, Is.GreaterThan(cfgAntes.HoldLookAhead));
        }

        [Test]
        public void AjusteFino_Sesgo_ProponeIntegral()
        {
            var a = new AnalisisPasadas { SesgoM = 0.05, DesvioM = 0.01, RmsM = 0.051 };
            string motivo;
            var n = Calibracion.ProponerAjusteFino(ConfigBase(), a, out motivo);
            Assert.That(n, Is.Not.Null);
            Assert.That(n.IntegralPp, Is.EqualTo(10));
        }

        [Test]
        public void AjusteFino_AndaBien_NoProponeNada()
        {
            var a = new AnalisisPasadas { SesgoM = 0.005, DesvioM = 0.012, RmsM = 0.013 };
            string motivo;
            Assert.That(Calibracion.ProponerAjusteFino(ConfigBase(), a, out motivo), Is.Null);
        }

        // =====================================================================
        // Cancelar / abortar / tope / aplicar / deshacer
        // =====================================================================

        [Test]
        public void Cancelar_DespuesDeGrabar_RestauraLaConfigPrevia()
        {
            _p.SensorInvertido = true;
            Iniciar();
            Hacer("siguiente");
            Hacer("empezar");
            Correr(1.0, () => _p.AnguloFisico = 10);
            Hacer("aceptar");
            Assert.That(_p.Placa.InvertWas, Is.True);

            Hacer("cancelar");
            Assert.That(_w.Paso, Is.EqualTo(SteerCalPaso.Cancelado));
            Assert.That(_restauraciones, Is.EqualTo(1));
            Assert.That(_p.Placa.MismoTodo(ConfigBase()), Is.True, "la placa vuelve a la config de antes");
            Assert.That(_w.MotorActivo, Is.False);
        }

        [Test]
        public void Cancelar_SinHaberGrabado_NoGastaUnaGrabacion()
        {
            Iniciar();
            Hacer("cancelar");
            Assert.That(_pedidos, Does.Not.Contain(SteerCalPedido.Restaurar));
        }

        [Test]
        public void Cancelar_ConElMotorAndando_LoCorta()
        {
            HastaSentidoMotor();
            Hacer("empezar");
            _w.Latido(true, _t);
            _w.Tick(Entrada());
            Assert.That(_w.MotorActivo, Is.True);
            Hacer("cancelar");
            Assert.That(_w.MotorActivo, Is.False);
            _w.Tick(Entrada());
            Assert.That(_w.MotorActivo, Is.False);
        }

        [Test]
        public void Aplicar_PideGuardar_YDeshacerVuelveAlOriginal()
        {
            var cfg = ConfigBase();
            cfg.MinPwm = 40;
            _p.Placa = cfg;
            IrAlPaso(SteerCalPaso.PwmMinimo);
            Hacer("aceptar");
            while (_w.Paso != SteerCalPaso.Resumen)
            {
                if (_w.PuedeSaltar) Hacer("saltar"); else Hacer("siguiente");
            }
            Assert.That(_w.Propuesta.Any(c => c.Campo == "PWM mínimo" && c.Antes == "40" && c.Despues == "22"), Is.True);

            Hacer("aplicar");
            Assert.That(_pedidos, Does.Contain(SteerCalPedido.AplicarFinal));
            Assert.That(_w.Paso, Is.EqualTo(SteerCalPaso.Aplicado));
            Assert.That(_w.PuedeDeshacer, Is.True);

            Hacer("deshacer");
            Assert.That(_pedidos, Does.Contain(SteerCalPedido.Deshacer));
            Assert.That(_w.EnPlaca.MinPwm, Is.EqualTo(40));
        }

        [Test]
        public void TopeDeGrabaciones_PorPaso_SeRespeta()
        {
            Assert.That(SteerCalWizard.TopeEscrituras(SteerCalPaso.CeroWas), Is.EqualTo(1));
            Assert.That(SteerCalWizard.TopeEscrituras(SteerCalPaso.AjusteFino), Is.EqualTo(0));
            Assert.That(SteerCalWizard.TopeEscrituras(SteerCalPaso.Ganancia),
                Is.GreaterThanOrEqualTo(SteerCalWizard.CandidatosKp.Length + 2));
        }

        [Test]
        public void Inactivo_NuncaPideMotor()
        {
            _w.Latido(true, _t);
            _w.Tick(Entrada());
            Assert.That(_w.MotorActivo, Is.False);
            Assert.That(_w.Activo, Is.False);
        }
    }
}
