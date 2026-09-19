// ============================================================================
// BackoffReintentos.cs — espaciado exponencial de reintentos contra el cloud.
//
// Para qué: el heartbeat sale en cada tick del sync (cada 30 s por defecto).
// Cuando el server contesta 502 o se corta el SSL, insistir cada 30 s no
// arregla nada: llena orbitx_sync.log, gasta batería de la pantalla y suma
// timeouts de 30 s adentro del tick, que retrasan lo que sí importa (subir los
// lotes). Con backoff, la espera va creciendo 30 s → 1 min → 2 min → 4 min
// hasta un tope, y vuelve a cero apenas el cloud contesta bien.
//
// El tope existe a propósito: un tractor que quedó sin señal dos horas tiene
// que reengancharse rápido cuando vuelve, no esperar media hora más.
//
// Es una clase pura, sin timers ni relojes internos: el "ahora" entra por
// parámetro para poder testearla sin esperar tiempo real.
// ============================================================================

using System;

namespace AgroParallel.Services.OrbitX
{
    public sealed class BackoffReintentos
    {
        private readonly TimeSpan _espera0;
        private readonly TimeSpan _tope;
        private readonly object _candado = new object();
        private int _fallos;
        private DateTime _proximoIntentoUtc = DateTime.MinValue;
        private TimeSpan _esperaActual = TimeSpan.Zero;

        public BackoffReintentos(TimeSpan esperaInicial, TimeSpan tope)
        {
            if (esperaInicial <= TimeSpan.Zero) esperaInicial = TimeSpan.FromSeconds(30);
            if (tope < esperaInicial) tope = esperaInicial;
            _espera0 = esperaInicial;
            _tope = tope;
        }

        /// <summary>Fallos seguidos desde el último éxito.</summary>
        public int FallosConsecutivos
        {
            get { lock (_candado) return _fallos; }
        }

        /// <summary>Cuánto hay que esperar ahora mismo (cero si no hay backoff activo).</summary>
        public TimeSpan EsperaActual
        {
            get { lock (_candado) return _esperaActual; }
        }

        /// <summary>true si ya se puede volver a intentar.</summary>
        public bool PuedeIntentar(DateTime ahoraUtc)
        {
            lock (_candado) return ahoraUtc >= _proximoIntentoUtc;
        }

        /// <summary>El cloud contestó bien: se vuelve al ritmo normal enseguida.</summary>
        public void RegistrarExito()
        {
            lock (_candado)
            {
                _fallos = 0;
                _esperaActual = TimeSpan.Zero;
                _proximoIntentoUtc = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Falló el intento: duplica la espera (hasta el tope) y devuelve cuánto
        /// se va a esperar, para poder loguearlo una sola vez.
        /// </summary>
        public TimeSpan RegistrarFallo(DateTime ahoraUtc)
        {
            lock (_candado)
            {
                if (_fallos < 30) _fallos++; // tope al exponente: 2^30 ya se comió el tope hace rato
                double factor = Math.Pow(2, _fallos - 1);
                double ticks = _espera0.Ticks * factor;
                TimeSpan espera = ticks >= _tope.Ticks ? _tope : TimeSpan.FromTicks((long)ticks);
                _esperaActual = espera;
                _proximoIntentoUtc = ahoraUtc + espera;
                return espera;
            }
        }
    }
}
