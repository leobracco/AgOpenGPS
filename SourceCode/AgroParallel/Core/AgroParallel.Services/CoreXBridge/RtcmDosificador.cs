// ============================================================================
// RtcmDosificador.cs — cola del RTCM que va al AiO por UDP 2233.
//
// El caster NTRIP manda el RTCM en ráfagas: cuando vuelve después de un corte
// de internet descarga de golpe todo lo acumulado. Mandarlo entero, aunque sea
// rebanado en 256 B, ahoga a la Teensy (buffer NTRIP de 1023 B) y el receptor
// queda sin correcciones varios segundos: en el lote es perder el RTK cada vez
// que se corta el 4G. Idea de AgOpenWeb (RtcmPacer.cs, issue #169).
//
// Esta clase es solo la cola (pura, thread-safe): el que llama saca un pedazo
// por tick (50 ms → 256 B = ~5 kB/s, holgado para 1-3 kB/s de RTCM). Si se
// acumula más del tope, se tira lo MÁS VIEJO: una corrección vieja no sirve.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.CoreXBridge
{
    public sealed class RtcmDosificador
    {
        private readonly int _tamPaquete;
        private readonly int _topeBytes;
        private readonly LinkedList<byte[]> _bloques = new LinkedList<byte[]>();
        private int _offsetPrimero;   // bytes ya enviados del primer bloque
        private int _pendientes;
        private long _descartados;
        private readonly object _lock = new object();

        public RtcmDosificador(int tamPaquete = 256, int topeBytes = 10240)
        {
            _tamPaquete = Math.Max(1, tamPaquete);
            _topeBytes = Math.Max(_tamPaquete, topeBytes);
        }

        /// <summary>Bytes esperando salir.</summary>
        public int Pendientes { get { lock (_lock) return _pendientes; } }

        /// <summary>Bytes tirados por pasarse del tope (diagnóstico).</summary>
        public long Descartados { get { lock (_lock) return _descartados; } }

        public void Encolar(byte[] datos)
        {
            if (datos == null || datos.Length == 0) return;
            lock (_lock)
            {
                // Copia: el que llama puede reusar su buffer.
                var copia = new byte[datos.Length];
                Buffer.BlockCopy(datos, 0, copia, 0, datos.Length);
                _bloques.AddLast(copia);
                _pendientes += copia.Length;
                Recortar();
            }
        }

        /// <summary>El próximo pedazo a mandar (≤ tamPaquete), o null si no hay.</summary>
        public byte[] Siguiente()
        {
            lock (_lock)
            {
                if (_pendientes == 0) return null;
                int n = Math.Min(_tamPaquete, _pendientes);
                var salida = new byte[n];
                int escrito = 0;
                while (escrito < n)
                {
                    var b = _bloques.First.Value;
                    int disp = b.Length - _offsetPrimero;
                    int tomar = Math.Min(disp, n - escrito);
                    Buffer.BlockCopy(b, _offsetPrimero, salida, escrito, tomar);
                    escrito += tomar;
                    _offsetPrimero += tomar;
                    if (_offsetPrimero >= b.Length) { _bloques.RemoveFirst(); _offsetPrimero = 0; }
                }
                _pendientes -= n;
                return salida;
            }
        }

        /// <summary>Tira lo más viejo hasta quedar dentro del tope.</summary>
        private void Recortar()
        {
            int sobra = _pendientes - _topeBytes;
            while (sobra > 0)
            {
                var b = _bloques.First.Value;
                int disp = b.Length - _offsetPrimero;
                if (disp <= sobra)
                {
                    _bloques.RemoveFirst();
                    _offsetPrimero = 0;
                    sobra -= disp;
                    _pendientes -= disp;
                    _descartados += disp;
                }
                else
                {
                    _offsetPrimero += sobra;
                    _pendientes -= sobra;
                    _descartados += sobra;
                    sobra = 0;
                }
            }
        }
    }
}
