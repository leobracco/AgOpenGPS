// ============================================================================
// ToolXWorkSwitchTests — convivencia del switch de trabajo inalámbrico (ToolX,
// PGN 253 con origen 0x7C) con el módulo de dirección real (origen 0x7E).
//
// Reglas de PgnReceiver (caso 253) / CModuleComm que se prueban:
//   · un frame de ToolX sólo toca el bit de trabajo (nada de ángulo, heading,
//     roll, bit de dirección ni PWM) y marca a ToolX como vivo;
//   · la polaridad de ToolX se normaliza contra "Activo con contacto cerrado"
//     (ON ⇔ herramienta abajo, sea cual sea el flag);
//   · con isToolXWorkSwitch apagado el frame se descarta entero;
//   · mientras ToolX (habilitado y vivo) sea el dueño, el AIO no pisa el bit;
//   · si ToolX se PIERDE, el bit queda en su último valor y el AIO sólo lo
//     escribe por flanco PROPIO (un AIO sin switch manda un 1 fijo: copiarlo
//     crudo al expirar era un corte de secciones por un microcorte WiFi);
//   · ToolX nunca visto o deshabilitado → AOG puro, manda el AIO.
// ============================================================================

using System;
using System.Diagnostics;
using AgOpenGPS;
using AgOpenGPS.Core;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class ToolXWorkSwitchTests
    {
        private sealed class ModuleHostFalso : IModuleCommHost
        {
            public bool IsAutoSteerAuto => false;
            public bool IsBtnAutoSteerOn => false;
            public btnStates AutoBtnState => btnStates.Off;
            public btnStates ManualBtnState => btnStates.Off;
        }

        private sealed class PgnHostFalso : IPgnReceiveHost
        {
            public int TraficoDireccion;

            public ApplicationModel AppModel => null;
            public CNMEA Pn => null;
            public CAHRS Ahrs { get; } = new CAHRS();
            public CModuleComm Mc { get; } = new CModuleComm(new ModuleHostFalso());
            public CTrack Trk => null;
            public CISOBUS Isobus => null;
            public bool IsSimEnabled => false;
            public void DisableSim() { }
            public void UpdateFixPosition() { }
            public bool IsHardwareMessages => false;
            public void ShowHardwareMessage(string text, bool isAlert, int secondsToDisplay) { }
            public void HideHardwareMessage() { }
            public void CycleLineForward() { }
            public void CycleLineBackward() { }
            public void DoRemoteSwitches() { }
            public void OnGpsSentenceReceived() { }
            public void OnSteerModuleTraffic() { TraficoDireccion++; }
        }

        private const byte OrigenDireccion = 0x7E; // módulo de dirección (AIO)

        /// <summary>Arma un PGN 253 completo (14 bytes, CRC incluido). Heading y
        /// roll van en sentinela (9999 / 8888) para no ensuciar el IMU.</summary>
        private static byte[] Pgn253(byte origen, short anguloX100, bool workHigh, bool steerHigh, byte pwm)
        {
            var f = new byte[14];
            f[0] = 0x80; f[1] = 0x81; f[2] = origen; f[3] = 253; f[4] = 8;
            f[5] = (byte)(anguloX100 & 0xFF); f[6] = (byte)((anguloX100 >> 8) & 0xFF);
            const short heading = 9999; const short roll = 8888;
            f[7] = (byte)(heading & 0xFF); f[8] = (byte)((heading >> 8) & 0xFF);
            f[9] = (byte)(roll & 0xFF); f[10] = (byte)((roll >> 8) & 0xFF);
            f[11] = (byte)((workHigh ? 1 : 0) | (steerHigh ? 2 : 0) | 4);
            f[12] = pwm;
            byte ck = 0;
            for (int i = 2; i < 13; i++) ck += f[i];
            f[13] = ck;
            return f;
        }

        private static byte[] ToolX(bool abajo) =>
            Pgn253(CModuleComm.ToolXSource, 0, workHigh: !abajo, steerHigh: true, pwm: 0);

        private static byte[] Aio(bool workHigh, short angulo = 0, bool steerHigh = true, byte pwm = 0) =>
            Pgn253(OrigenDireccion, angulo, workHigh, steerHigh, pwm);

        /// <summary>Simula que pasó más del timeout sin frames de ToolX (reloj monotónico).</summary>
        private static void ExpirarToolX(CModuleComm mc) =>
            mc.lastToolXTicks = Stopwatch.GetTimestamp()
                - (long)((CModuleComm.ToolXTimeoutSec + 1.0) * Stopwatch.Frequency);

        private PgnHostFalso _host;
        private PgnReceiver _rx;
        private CModuleComm _mc;

        [SetUp]
        public void SetUp()
        {
            _host = new PgnHostFalso();
            _rx = new PgnReceiver(_host);
            _mc = _host.Mc;
        }

        // -------------------------------------------------------------------
        //  Frame de ToolX
        // -------------------------------------------------------------------

        [Test]
        public void FrameDeToolX_SoloTocaElBitDeTrabajo_YMarcaVivo()
        {
            _mc.isToolXWorkSwitch = true;
            _mc.workSwitchHigh = true;
            _mc.steerSwitchHigh = true;
            _mc.actualSteerAngleChart = 1234;
            _mc.actualSteerAngleDegrees = 12.34;
            _mc.pwmDisplay = 77;

            _rx.ReceiveFromAgIO(ToolX(abajo: true));

            Assert.That(_mc.workSwitchHigh, Is.False, "el bit de trabajo debe venir de ToolX (abajo → ON con activeLow=true)");
            Assert.That(_mc.actualSteerAngleChart, Is.EqualTo(1234), "el ángulo NO debe pisarse con el 0 de relleno");
            Assert.That(_mc.actualSteerAngleDegrees, Is.EqualTo(12.34).Within(1e-9));
            Assert.That(_mc.pwmDisplay, Is.EqualTo(77), "el PWM NO debe pisarse");
            Assert.That(_mc.steerSwitchHigh, Is.True, "el bit de dirección NO debe tocarse");
            Assert.That(_host.TraficoDireccion, Is.EqualTo(0), "ToolX no cuenta como tráfico del módulo de dirección");
            Assert.That(_mc.IsToolXAlive, Is.True);
            Assert.That(_mc.ToolXSeenEver, Is.True);
            Assert.That(_mc.IsToolXOwningWorkSwitch, Is.True);
        }

        [Test]
        public void PolaridadDeToolX_NoDependeDelFlagDelSwitchCableado()
        {
            // CheckWorkAndSteerSwitch prende con `workSwitchHigh != isWorkSwitchActiveLow`.
            // Con el flag en true (default) ON es workSwitchHigh=false; con el flag en
            // false ON es workSwitchHigh=true. ToolX tiene que dar ON ⇔ abajo en ambos.
            _mc.isToolXWorkSwitch = true;

            _mc.isWorkSwitchActiveLow = true;
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            Assert.That(_mc.workSwitchHigh != _mc.isWorkSwitchActiveLow, Is.True, "activeLow=true, abajo → ON");
            _rx.ReceiveFromAgIO(ToolX(abajo: false));
            Assert.That(_mc.workSwitchHigh != _mc.isWorkSwitchActiveLow, Is.False, "activeLow=true, arriba → OFF");

            _mc.isWorkSwitchActiveLow = false;
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            Assert.That(_mc.workSwitchHigh != _mc.isWorkSwitchActiveLow, Is.True, "activeLow=false, abajo → ON");
            _rx.ReceiveFromAgIO(ToolX(abajo: false));
            Assert.That(_mc.workSwitchHigh != _mc.isWorkSwitchActiveLow, Is.False, "activeLow=false, arriba → OFF");
        }

        [Test]
        public void ToolXDeshabilitadoEnElPerfil_DescartaElFrameEntero()
        {
            _mc.isToolXWorkSwitch = false;
            _mc.workSwitchHigh = true;

            _rx.ReceiveFromAgIO(ToolX(abajo: true));

            Assert.That(_mc.workSwitchHigh, Is.True, "sin habilitar, un ToolX ajeno no puede tocar las secciones");
            Assert.That(_mc.lastToolXTicks, Is.EqualTo(0));
            Assert.That(_mc.ToolXSeenEver, Is.False);
            Assert.That(_mc.IsToolXAlive, Is.False);
            Assert.That(_host.TraficoDireccion, Is.EqualTo(0));
        }

        [Test]
        public void FrameDeToolXConLargoIncorrecto_SeIgnora()
        {
            _mc.isToolXWorkSwitch = true;
            _mc.workSwitchHigh = true;

            var corto = ToolX(abajo: true);
            Array.Resize(ref corto, 13); // sin CRC → ni siquiera pasa el chequeo de largo

            _rx.ReceiveFromAgIO(corto);

            Assert.That(_mc.workSwitchHigh, Is.True);
            Assert.That(_mc.ToolXSeenEver, Is.False);
        }

        // -------------------------------------------------------------------
        //  Convivencia con el módulo de dirección
        // -------------------------------------------------------------------

        [Test]
        public void ModuloDeDireccion_NoPisaElBitMientrasToolXEstaVivo()
        {
            _mc.isToolXWorkSwitch = true;

            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            Assert.That(_mc.workSwitchHigh, Is.False);

            // El AIO sigue mandando su bit de trabajo en 1 (switch sin cablear),
            // con ángulo real, bit de dirección enganchado (0) y PWM.
            _rx.ReceiveFromAgIO(Aio(workHigh: true, angulo: 550, steerHigh: false, pwm: 120));

            Assert.That(_mc.workSwitchHigh, Is.False, "ToolX es el dueño: el AIO no debe pisar el bit de trabajo");
            Assert.That(_mc.steerSwitchHigh, Is.False, "el bit de dirección SÍ viene del AIO");
            Assert.That(_mc.actualSteerAngleChart, Is.EqualTo(550), "el ángulo SÍ viene del AIO");
            Assert.That(_mc.pwmDisplay, Is.EqualTo(120));
            Assert.That(_host.TraficoDireccion, Is.EqualTo(1));
        }

        [Test]
        public void ToolXPerdido_ElBitQuedaEnSuUltimoValor_AunqueElAioMandeUnValorFijo()
        {
            // Escenario del campo: herramienta abajo, secciones pintando, y el WiFi
            // del ESP32 se corta unos segundos. El AIO sin switch cableado manda
            // bit0=1 FIJO: si se copiara crudo al expirar ToolX, sería un flanco
            // 0→1 y CheckWorkAndSteerSwitch cortaría las secciones en la pasada.
            _mc.isToolXWorkSwitch = true;
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            for (int i = 0; i < 20; i++) _rx.ReceiveFromAgIO(Aio(workHigh: true));
            Assert.That(_mc.workSwitchHigh, Is.False);

            ExpirarToolX(_mc);
            Assert.That(_mc.IsToolXAlive, Is.False);

            for (int i = 0; i < 20; i++) _rx.ReceiveFromAgIO(Aio(workHigh: true));

            Assert.That(_mc.workSwitchHigh, Is.False, "el 1 fijo del AIO NO es un flanco: el bit queda en su último valor");
            Assert.That(_mc.toolXLostLogged, Is.True, "la pérdida se loguea una vez");
        }

        [Test]
        public void ToolXPerdido_ElAioMandaSoloPorFlancoPropio()
        {
            // ToolX ARRIBA (no pinta) y el AIO con su 1 fijo. Se pierde ToolX. Si el
            // operario cierra el switch cableado del AIO (1→0), ESE flanco sí manda.
            _mc.isToolXWorkSwitch = true;
            _rx.ReceiveFromAgIO(ToolX(abajo: false));
            _rx.ReceiveFromAgIO(Aio(workHigh: true));
            Assert.That(_mc.workSwitchHigh, Is.True);

            ExpirarToolX(_mc);

            _rx.ReceiveFromAgIO(Aio(workHigh: true));
            Assert.That(_mc.workSwitchHigh, Is.True, "sin flanco propio, nada cambia");

            _rx.ReceiveFromAgIO(Aio(workHigh: false)); // el operario baja con el switch cableado
            Assert.That(_mc.workSwitchHigh, Is.False, "flanco propio del AIO 1→0 → manda");

            _rx.ReceiveFromAgIO(Aio(workHigh: true));
            Assert.That(_mc.workSwitchHigh, Is.True, "flanco propio del AIO 0→1 → manda");
        }

        [Test]
        public void ToolXVuelve_RetomaElDominio()
        {
            _mc.isToolXWorkSwitch = true;
            _rx.ReceiveFromAgIO(ToolX(abajo: false));
            _rx.ReceiveFromAgIO(Aio(workHigh: true));
            ExpirarToolX(_mc);
            _rx.ReceiveFromAgIO(Aio(workHigh: false)); // flanco propio con ToolX perdido
            Assert.That(_mc.workSwitchHigh, Is.False);
            Assert.That(_mc.toolXLostLogged, Is.True);

            _rx.ReceiveFromAgIO(ToolX(abajo: false)); // vuelve ToolX, arriba
            Assert.That(_mc.workSwitchHigh, Is.True);
            Assert.That(_mc.IsToolXAlive, Is.True);
            Assert.That(_mc.toolXLostLogged, Is.False, "se rearma el log para la próxima pérdida");

            _rx.ReceiveFromAgIO(Aio(workHigh: true)); // flanco propio del AIO 0→1, pero ToolX vivo
            Assert.That(_mc.workSwitchHigh, Is.True, "con ToolX vivo el AIO no toca el bit aunque tenga flanco");
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            Assert.That(_mc.workSwitchHigh, Is.False);
        }

        [Test]
        public void ToolXHabilitadoPeroNuncaVisto_ElModuloDeDireccionMandaComoSiempre()
        {
            _mc.isToolXWorkSwitch = true;
            _mc.workSwitchHigh = true;

            // AOG puro: el valor del AIO se copia crudo aunque no haya flanco.
            _rx.ReceiveFromAgIO(Aio(workHigh: false));
            Assert.That(_mc.workSwitchHigh, Is.False);
            _rx.ReceiveFromAgIO(Aio(workHigh: false));
            Assert.That(_mc.workSwitchHigh, Is.False);
            _rx.ReceiveFromAgIO(Aio(workHigh: true));
            Assert.That(_mc.workSwitchHigh, Is.True);
            Assert.That(_mc.IsToolXAlive, Is.False);
            Assert.That(_mc.toolXLostLogged, Is.False, "nunca visto ≠ perdido: no se loguea nada");
        }

        [Test]
        public void ToolXDeshabilitadoDespuesDeVisto_VuelveAAogPuro()
        {
            _mc.isToolXWorkSwitch = true;
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            _rx.ReceiveFromAgIO(Aio(workHigh: true));
            Assert.That(_mc.workSwitchHigh, Is.False);

            // El operario apaga la fila ToolX en el perfil: el AIO manda de nuevo
            // aunque su valor no cambie y aunque ToolX siga vivo en la LAN.
            _mc.isToolXWorkSwitch = false;
            _rx.ReceiveFromAgIO(Aio(workHigh: true));
            Assert.That(_mc.workSwitchHigh, Is.True);

            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            Assert.That(_mc.workSwitchHigh, Is.True, "deshabilitado, el frame de ToolX se descarta");
        }

        // -------------------------------------------------------------------
        //  Primer contacto, polaridad en caliente, reset de la fila
        // -------------------------------------------------------------------

        [Test]
        public void PrimerFrameConHerramientaAbajo_FuerzaFlancoParaAplicarElNivel()
        {
            // workSwitchHigh y oldWorkSwitchHigh nacen en false; con activeLow=true
            // "abajo" también es false → sin flanco no pintaba hasta subir y bajar.
            _mc.isToolXWorkSwitch = true;
            Assert.That(_mc.workSwitchHigh, Is.False);
            Assert.That(_mc.oldWorkSwitchHigh, Is.False);

            _rx.ReceiveFromAgIO(ToolX(abajo: true));

            Assert.That(_mc.workSwitchHigh, Is.False, "abajo con activeLow=true → bit 0");
            Assert.That(_mc.oldWorkSwitchHigh, Is.Not.EqualTo(_mc.workSwitchHigh),
                "el primer frame deja old != nuevo para que CheckWorkAndSteerSwitch aplique el nivel");

            // Los frames siguientes no vuelven a forzar nada (old lo maneja
            // CheckWorkAndSteerSwitch; acá se simula que ya lo sincronizó).
            _mc.oldWorkSwitchHigh = _mc.workSwitchHigh;
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            Assert.That(_mc.oldWorkSwitchHigh, Is.EqualTo(_mc.workSwitchHigh), "sin cambio de estado no hay flanco");
        }

        [Test]
        public void PrimerFrameConHerramientaArriba_TambienAplicaElNivel()
        {
            _mc.isToolXWorkSwitch = true;
            _rx.ReceiveFromAgIO(ToolX(abajo: false));
            Assert.That(_mc.workSwitchHigh, Is.True);
            Assert.That(_mc.oldWorkSwitchHigh, Is.False, "old != nuevo → CheckWorkAndSteerSwitch evalúa (y apaga si estaba prendido)");
        }

        [Test]
        public void ResetToolX_ElProximoFrameVuelveASerElPrimero()
        {
            _mc.isToolXWorkSwitch = true;
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            _mc.oldWorkSwitchHigh = _mc.workSwitchHigh; // CheckWorkAndSteerSwitch ya lo aplicó

            // Se apaga y prende la fila en el perfil (GuardarSwitches llama ResetToolX).
            _mc.ResetToolX();
            Assert.That(_mc.ToolXSeenEver, Is.False);
            Assert.That(_mc.IsToolXAlive, Is.False);
            Assert.That(_mc.toolXLostLogged, Is.False);

            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            Assert.That(_mc.oldWorkSwitchHigh, Is.Not.EqualTo(_mc.workSwitchHigh), "vuelve a aplicar el nivel");
            Assert.That(_mc.IsToolXAlive, Is.True);
        }

        [Test]
        public void CambiarPolaridadEnCalienteConToolXVivo_NoGeneraFlanco()
        {
            _mc.isToolXWorkSwitch = true;
            _mc.isWorkSwitchActiveLow = true;
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            _mc.oldWorkSwitchHigh = _mc.workSwitchHigh; // ya aplicado
            bool onAntes = _mc.workSwitchHigh != _mc.isWorkSwitchActiveLow;
            Assert.That(onAntes, Is.True);

            // El operario toca "Activo con contacto cerrado" con ToolX mandando.
            _mc.SetWorkSwitchActiveLow(false);

            Assert.That(_mc.isWorkSwitchActiveLow, Is.False);
            Assert.That(_mc.workSwitchHigh, Is.True, "bit invertido junto con el flag");
            Assert.That(_mc.oldWorkSwitchHigh, Is.EqualTo(_mc.workSwitchHigh), "sin flanco: no re-aplica ni pisa un apagado manual");
            Assert.That(_mc.workSwitchHigh != _mc.isWorkSwitchActiveLow, Is.True, "el estado físico (ON) no cambió");

            // El próximo frame de ToolX, ya normalizado con el flag nuevo, coincide.
            _rx.ReceiveFromAgIO(ToolX(abajo: true));
            Assert.That(_mc.workSwitchHigh, Is.True);
            Assert.That(_mc.oldWorkSwitchHigh, Is.EqualTo(_mc.workSwitchHigh));
        }

        [Test]
        public void CambiarPolaridadSinToolX_NoTocaElBit()
        {
            // Con el switch cableado el bit es crudo: sólo cambia el flag.
            _mc.isToolXWorkSwitch = false;
            _mc.workSwitchHigh = true;
            _mc.oldWorkSwitchHigh = true;
            _mc.SetWorkSwitchActiveLow(false);
            Assert.That(_mc.isWorkSwitchActiveLow, Is.False);
            Assert.That(_mc.workSwitchHigh, Is.True);
            Assert.That(_mc.oldWorkSwitchHigh, Is.True);

            _mc.SetWorkSwitchActiveLow(false); // sin cambio → no-op
            Assert.That(_mc.workSwitchHigh, Is.True);
        }
    }
}
