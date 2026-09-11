// ============================================================================
// SiembraStateMachineTests.cs — Máquina "¿estamos sembrando?" compartida (D#1).
// Cubre: arranque por cada método, confirmación sostenida, histéresis de
// parada (vel<0.3 >10s), apagado sin pintar (>2s) y modo manual.
// El tiempo es inyectado — ningún test duerme.
// ============================================================================

using System;
using AgroParallel.VistaX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class SiembraStateMachineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

        private static SiembraEntrada Entrada(double vel = 0, int secActivas = 0,
            bool secDisponibles = true, int sensores = 0)
        {
            return new SiembraEntrada
            {
                VelocidadKmh = vel,
                SeccionesActivas = secActivas,
                SeccionesDisponibles = secDisponibles,
                SensoresActivos = sensores
            };
        }

        // ---------------- Método "pintando" ----------------

        [Test]
        public void Pintando_ArrancaConSeccionYVelocidad()
        {
            var m = new SiembraStateMachine();
            m.Configurar("pintando", 3, 500);

            Assert.That(m.Evaluar(Entrada(vel: 0.4, secActivas: 1), T0), Is.False,
                "vel 0.4 <= 0.5 no debe arrancar");
            Assert.That(m.Evaluar(Entrada(vel: 0.6, secActivas: 0), T0), Is.False,
                "sin secciones pintando no debe arrancar");
            Assert.That(m.Evaluar(Entrada(vel: 0.6, secActivas: 1), T0), Is.True);
            Assert.That(m.MotivoDetenido, Is.Empty);
        }

        [Test]
        public void Pintando_SinFuenteDeSecciones_NoArranca()
        {
            var m = new SiembraStateMachine();
            m.Configurar("pintando", 3, 500);
            Assert.That(m.Evaluar(Entrada(vel: 5, secActivas: 0, secDisponibles: false), T0), Is.False);
            Assert.That(m.MotivoDetenido, Does.Contain("secciones"));
        }

        [Test]
        public void Pintando_SeApagaTras2sSinPintar()
        {
            var m = new SiembraStateMachine();
            m.Configurar("pintando", 3, 500);
            m.Evaluar(Entrada(vel: 6, secActivas: 2), T0);
            Assert.That(m.Activo, Is.True);

            // Deja de pintar pero sigue andando rápido: aguanta 2 s.
            m.Evaluar(Entrada(vel: 6, secActivas: 0), T0.AddSeconds(1));
            Assert.That(m.Activo, Is.True, "a 1 s sin pintar sigue activo");
            m.Evaluar(Entrada(vel: 6, secActivas: 0), T0.AddSeconds(3.5));
            Assert.That(m.Activo, Is.False, "a >2 s sin pintar se apaga");
            Assert.That(m.MotivoDetenido, Does.Contain("pintar"));
        }

        [Test]
        public void Pintando_VolverAPintarReseteaElTimerDeApagado()
        {
            var m = new SiembraStateMachine();
            m.Configurar("pintando", 3, 500);
            m.Evaluar(Entrada(vel: 6, secActivas: 2), T0);

            m.Evaluar(Entrada(vel: 6, secActivas: 0), T0.AddSeconds(1));
            m.Evaluar(Entrada(vel: 6, secActivas: 1), T0.AddSeconds(2)); // vuelve a pintar
            m.Evaluar(Entrada(vel: 6, secActivas: 0), T0.AddSeconds(3));
            m.Evaluar(Entrada(vel: 6, secActivas: 0), T0.AddSeconds(4.5));
            Assert.That(m.Activo, Is.True, "el corte de 2 s cuenta desde el último pintado");
        }

        // ---------------- Método "sensores" ----------------

        [Test]
        public void Sensores_RequiereConfirmacionSostenida()
        {
            var m = new SiembraStateMachine();
            m.Configurar("sensores", 3, 500);

            Assert.That(m.Evaluar(Entrada(vel: 5, sensores: 3), T0), Is.False,
                "primer tick solo abre la ventana de confirmación");
            Assert.That(m.MotivoDetenido, Does.Contain("Confirmando"));
            Assert.That(m.Evaluar(Entrada(vel: 5, sensores: 3), T0.AddMilliseconds(300)), Is.False,
                "300 ms < 500 ms de confirmación");
            Assert.That(m.Evaluar(Entrada(vel: 5, sensores: 3), T0.AddMilliseconds(600)), Is.True,
                "600 ms >= 500 ms → arranca");
        }

        [Test]
        public void Sensores_CondicionInterrumpidaReiniciaConfirmacion()
        {
            var m = new SiembraStateMachine();
            m.Configurar("sensores", 3, 500);

            m.Evaluar(Entrada(vel: 5, sensores: 3), T0);
            m.Evaluar(Entrada(vel: 5, sensores: 1), T0.AddMilliseconds(300)); // se cae la condición
            Assert.That(m.Evaluar(Entrada(vel: 5, sensores: 3), T0.AddMilliseconds(600)), Is.False,
                "la ventana se reinició — 600 ms ya no alcanza");
            Assert.That(m.Evaluar(Entrada(vel: 5, sensores: 3), T0.AddMilliseconds(1200)), Is.True);
        }

        [Test]
        public void Sensores_ConfirmacionCero_ArrancaInmediato()
        {
            var m = new SiembraStateMachine();
            m.Configurar("sensores", 2, 0);
            Assert.That(m.Evaluar(Entrada(vel: 5, sensores: 2), T0), Is.True);
        }

        [Test]
        public void Sensores_NoArrancaPorDebajoDelUmbralODeVelocidad()
        {
            var m = new SiembraStateMachine();
            m.Configurar("sensores", 3, 0);

            Assert.That(m.Evaluar(Entrada(vel: 5, sensores: 2), T0), Is.False);
            Assert.That(m.MotivoDetenido, Does.Contain("umbral 3"));
            Assert.That(m.Evaluar(Entrada(vel: 0.9, sensores: 5), T0), Is.False);
            Assert.That(m.MotivoDetenido, Does.Contain("Velocidad"));
        }

        // ---------------- Método "herramienta" ----------------

        [Test]
        public void Herramienta_ArrancaConSeccionActivaSinVelocidad()
        {
            var m = new SiembraStateMachine();
            m.Configurar("herramienta", 3, 500);

            Assert.That(m.Evaluar(Entrada(vel: 0, secActivas: 0), T0), Is.False);
            Assert.That(m.Evaluar(Entrada(vel: 0, secActivas: 1), T0), Is.True,
                "herramienta bajada arranca aun sin velocidad");
        }

        // ---------------- Método "manual" ----------------

        [Test]
        public void Manual_SoloArrancaPorForzarManual()
        {
            var m = new SiembraStateMachine();
            m.Configurar("manual", 3, 500);

            Assert.That(m.Evaluar(Entrada(vel: 10, secActivas: 5, sensores: 10), T0), Is.False,
                "en manual nada arranca solo");
            Assert.That(m.MotivoDetenido, Does.Contain("manual"));

            m.ForzarManual(true);
            Assert.That(m.Activo, Is.True);
            m.ForzarManual(false);
            Assert.That(m.Activo, Is.False);
        }

        // ---------------- Histéresis de parada (común) ----------------

        [Test]
        public void Activo_SeApagaConVelocidadBajaSostenida10s()
        {
            var m = new SiembraStateMachine();
            m.Configurar("pintando", 3, 500);
            m.Evaluar(Entrada(vel: 6, secActivas: 1), T0);

            m.Evaluar(Entrada(vel: 0.1, secActivas: 1), T0.AddSeconds(1));
            Assert.That(m.Activo, Is.True, "recién frenó — aguanta");
            m.Evaluar(Entrada(vel: 0.1, secActivas: 1), T0.AddSeconds(9));
            Assert.That(m.Activo, Is.True, "8 s frenado < 10 s");
            m.Evaluar(Entrada(vel: 0.1, secActivas: 1), T0.AddSeconds(12));
            Assert.That(m.Activo, Is.False, ">10 s frenado → se apaga");
            Assert.That(m.MotivoDetenido, Does.Contain("detenido"));
        }

        [Test]
        public void Activo_RetomarVelocidadReseteaLaParada()
        {
            var m = new SiembraStateMachine();
            m.Configurar("pintando", 3, 500);
            m.Evaluar(Entrada(vel: 6, secActivas: 1), T0);

            m.Evaluar(Entrada(vel: 0.1, secActivas: 1), T0.AddSeconds(1));
            m.Evaluar(Entrada(vel: 5, secActivas: 1), T0.AddSeconds(8));  // retoma
            m.Evaluar(Entrada(vel: 0.1, secActivas: 1), T0.AddSeconds(9));
            m.Evaluar(Entrada(vel: 0.1, secActivas: 1), T0.AddSeconds(15));
            Assert.That(m.Activo, Is.True, "los 10 s cuentan desde la última parada");
        }

        // ---------------- Configurar / Reset ----------------

        [Test]
        public void Configurar_NormalizaMetodoDesconocidoASensores()
        {
            var m = new SiembraStateMachine();
            m.Configurar("  PINTANDO ", 3, 500);
            Assert.That(m.Metodo, Is.EqualTo("pintando"));
            m.Configurar("cualquiera", 3, 500);
            Assert.That(m.Metodo, Is.EqualTo("sensores"));
            m.Configurar(null, 0, -5);
            Assert.That(m.Metodo, Is.EqualTo("sensores"));
            Assert.That(m.UmbralSensores, Is.EqualTo(1), "umbral mínimo 1");
        }

        [Test]
        public void Reset_VuelveAInactivoYPermiteRearmar()
        {
            var m = new SiembraStateMachine();
            m.Configurar("herramienta", 3, 500);
            m.Evaluar(Entrada(secActivas: 1), T0);
            Assert.That(m.Activo, Is.True);

            m.Reset();
            Assert.That(m.Activo, Is.False);
            Assert.That(m.Evaluar(Entrada(secActivas: 1), T0.AddSeconds(1)), Is.True);
        }
    }
}
