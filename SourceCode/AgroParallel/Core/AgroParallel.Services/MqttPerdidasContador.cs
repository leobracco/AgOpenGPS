// ============================================================================
// MqttPerdidasContador.cs — Contabilidad de consignas MQTT que no salieron.
//
// Todos los bridges que comandan la máquina (FlowX: caudal; corte: secciones;
// QuantiX: pps de los motores) publican consignas con un tick de 200 ms. Si el
// enlace con el broker no está, esa consigna se DESCARTA — encolarla sería peor
// (una consigna vieja aplicada tarde es una dosis equivocada, y el firmware ya
// corta por comms-loss a los 3-4 s). Lo que no puede pasar es que se descarte
// en silencio, que es justo lo que venía pasando: un catch vacío, o una línea
// "publish error ... The MQTT client is not connected" por tick perdida entre
// miles de líneas de log.
//
// Esta clase resuelve las dos mitades del problema:
//   · cuenta TODO lo perdido (el total es lo que mira la UI / el status),
//   · y decide CUÁNDO avisar, para que el aviso se vea: un resumen cada 5 s en
//     vez de cinco líneas por segundo.
//
// Lógica pura (el "ahora" se puede inyectar) para poder testearla sin broker.
// ============================================================================

using System;

namespace AgroParallel.Services
{
    /// <summary>
    /// Cuenta consignas MQTT descartadas por falta de enlace y arma el aviso
    /// resumido que va al log del bridge.
    /// </summary>
    public sealed class MqttPerdidasContador
    {
        /// <summary>Cada cuánto se resume el goteo de pérdidas.</summary>
        public const int AvisoCadaMsDefault = 5000;

        private readonly object _lock = new object();
        private readonly int _avisoCadaMs;
        private long _total;
        private long _avisadas;
        private DateTime _ultimoAvisoUtc;
        private string _ultimoMotivo;

        public MqttPerdidasContador() : this(AvisoCadaMsDefault) { }

        public MqttPerdidasContador(int avisoCadaMs)
        {
            _avisoCadaMs = avisoCadaMs < 0 ? 0 : avisoCadaMs;
        }

        /// <summary>Total de consignas descartadas desde el arranque.</summary>
        public long Total { get { lock (_lock) return _total; } }

        /// <summary>Último motivo anotado, en castellano.</summary>
        public string UltimoMotivo { get { lock (_lock) return _ultimoMotivo; } }

        /// <summary>
        /// Anota una consigna perdida. Devuelve el texto a loguear, o null si
        /// todavía no toca avisar (se está resumiendo).
        /// </summary>
        public string Anotar(string motivo)
        {
            return Anotar(motivo, DateTime.UtcNow);
        }

        /// <summary>Igual que <see cref="Anotar(string)"/>, con el reloj inyectado (tests).</summary>
        public string Anotar(string motivo, DateTime ahoraUtc)
        {
            long nuevas;
            long total;
            lock (_lock)
            {
                _total++;
                _ultimoMotivo = motivo;
                total = _total;

                bool primera = _ultimoAvisoUtc == default(DateTime);
                bool vencido = !primera && (ahoraUtc - _ultimoAvisoUtc).TotalMilliseconds >= _avisoCadaMs;
                if (!primera && !vencido) return null;

                _ultimoAvisoUtc = ahoraUtc;
                nuevas = _total - _avisadas;
                _avisadas = _total;
            }

            return "CONSIGNA PERDIDA: " + nuevas + " publicacion(es) sin enviar"
                 + (string.IsNullOrEmpty(motivo) ? "" : " (" + motivo + ")")
                 + ". Total perdidas: " + total + ".";
        }

        /// <summary>Vuelve a cero (enlace nuevo).</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _total = 0;
                _avisadas = 0;
                _ultimoAvisoUtc = default(DateTime);
                _ultimoMotivo = null;
            }
        }
    }
}
