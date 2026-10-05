// ============================================================================
// MqttBackoff.cs — Escalera de reintentos para conectar al broker MQTT.
//
// El broker es EMBEBIDO: lo levanta el propio Engine (MqttBrokerService, vía
// CoreXEngineHost.StartServices) en el mismo proceso que los bridges. Pero el
// arranque NO está ordenado: Program.cs hace webHost.Start() — que dispara
// FlowXBridge/CutDispatcher/SectionsSpeedPublisher — ANTES de
// coreX.StartServices(), que es donde el broker empieza a escuchar en :1883.
// O sea: el primer intento de conexión de un bridge falla por diseño, no por
// falla real. Por eso la escalera arranca corta (medio segundo) y sólo se
// estira si el broker de verdad no está.
//
// Lógica pura, sin timers ni I/O: quien la usa decide cómo esperar. Así se
// puede testear la escalera sin broker ni hardware.
// ============================================================================

using System;

namespace AgroParallel.Services
{
    /// <summary>
    /// Escalera de espera entre reintentos de conexión MQTT: duplica desde
    /// <see cref="EsperaInicialMsDefault"/> hasta toparse en
    /// <see cref="EsperaMaximaMsDefault"/>, y vuelve a cero al conectar.
    /// </summary>
    public sealed class MqttBackoff
    {
        /// <summary>Primera espera: el caso normal es "el broker todavía no
        /// terminó de levantar", que se resuelve en menos de un segundo.</summary>
        public const int EsperaInicialMsDefault = 500;

        /// <summary>Tope de la escalera. Con el broker caído de verdad no tiene
        /// sentido machacar, pero tampoco esperar minutos: el operario puede
        /// estar reiniciando el nodo o la pantalla en pleno lote.</summary>
        public const int EsperaMaximaMsDefault = 15000;

        private readonly int _inicialMs;
        private readonly int _maximaMs;
        private int _fallos;

        public MqttBackoff() : this(EsperaInicialMsDefault, EsperaMaximaMsDefault) { }

        public MqttBackoff(int inicialMs, int maximaMs)
        {
            if (inicialMs < 1) inicialMs = 1;
            if (maximaMs < inicialMs) maximaMs = inicialMs;
            _inicialMs = inicialMs;
            _maximaMs = maximaMs;
        }

        /// <summary>Fallos consecutivos desde el último <see cref="Reset"/>.</summary>
        public int Fallos { get { return _fallos; } }

        /// <summary>true mientras no haya habido ningún fallo (o después de conectar).</summary>
        public bool Limpio { get { return _fallos == 0; } }

        /// <summary>true cuando la escalera ya llegó al tope.</summary>
        public bool EnTope { get { return EsperaActualMs >= _maximaMs; } }

        /// <summary>
        /// Cuánto esperar antes del próximo intento, según los fallos ya
        /// anotados. Sin fallos devuelve la espera inicial (no se usa: al
        /// arranque se intenta directo).
        /// </summary>
        public int EsperaActualMs
        {
            get
            {
                if (_fallos <= 0) return _inicialMs;
                long espera = _inicialMs;
                for (int i = 1; i < _fallos && espera < _maximaMs; i++) espera *= 2;
                if (espera > _maximaMs) espera = _maximaMs;
                return (int)espera;
            }
        }

        /// <summary>
        /// Cuánto se va a esperar si el intento en curso falla, SIN anotarlo.
        /// Sirve para que el mensaje de log prometa la espera real.
        /// </summary>
        public int EsperaTrasOtroFalloMs
        {
            get
            {
                long espera = _inicialMs;
                int fallos = _fallos < int.MaxValue ? _fallos + 1 : _fallos;
                for (int i = 1; i < fallos && espera < _maximaMs; i++) espera *= 2;
                if (espera > _maximaMs) espera = _maximaMs;
                return (int)espera;
            }
        }

        /// <summary>
        /// Anota un intento fallido y devuelve cuánto esperar antes del
        /// siguiente. No satura ni desborda: el contador se clava en int.MaxValue.
        /// </summary>
        public int RegistrarFallo()
        {
            if (_fallos < int.MaxValue) _fallos++;
            return EsperaActualMs;
        }

        /// <summary>Conexión lograda: la escalera vuelve a cero.</summary>
        public void Reset()
        {
            _fallos = 0;
        }

        /// <summary>
        /// Regla de ruido para el log: un bridge que arranca antes que el
        /// broker falla siempre la primera vez, y loguear cada reintento llena
        /// el archivo de líneas idénticas (fue exactamente lo que pasó en
        /// fx_bridge.log / cut_dispatcher.log). Se loguea el primer fallo, cada
        /// vez que cambia el motivo, y después uno cada <paramref name="cada"/>
        /// fallos para dejar rastro de que sigue caído.
        /// </summary>
        public static bool DebeLoguearFallo(int fallos, string motivoAnterior, string motivoNuevo, int cada = 10)
        {
            if (fallos <= 1) return true;
            if (!string.Equals(motivoAnterior, motivoNuevo, StringComparison.Ordinal)) return true;
            if (cada < 1) cada = 1;
            return (fallos % cada) == 0;
        }
    }
}
