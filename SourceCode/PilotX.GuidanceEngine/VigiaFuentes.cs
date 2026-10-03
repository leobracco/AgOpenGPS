// ============================================================================
// VigiaFuentes.cs — alimenta al MonitorFuentesMudas una vez por segundo con el
// "último visto" de cada fuente y escribe en el registro de eventos UNA línea
// cuando se calla y UNA cuando vuelve (con la duración del corte).
//
// Fuentes y de dónde sale su marca (todas ya existían; acá solo se leen):
//
//   gps        posición GPS que llega al motor   GuidanceEngineHost.lastFixUtc
//   direccion  módulo de dirección (PGN 253)     GuidanceEngineHost.ultimoPgn253Ticks
//   maquina    módulo de máquina (hello 123)     CoreXEngineHost.LastMachineHelloUtc
//   imu        IMU por red (hello 121)           CoreXEngineHost.LastImuHelloUtc
//   nodo:<uid> nodos MQTT (QuantiX, VistaX, …)    NodoStatus.Online (LWT / barrido 15 s)
//
// Umbrales: GPS y PGN 253 llegan a 5-10 Hz → 3 s es un corte de verdad, no un
// paquete perdido. Los hello de CoreX salen cada 1 s → 5 s. Ojo: el módulo de
// dirección contesta el 253 cuando el motor le manda el 254, que sale con cada
// fix; si se calla el GPS, la dirección se calla detrás. El registro muestra
// las dos líneas y el orden dice cuál fue primero.
//
// Lo de "nunca habló no se reporta" vive en el monitor: un equipo sin módulo
// de máquina o sin IMU de red no escribe nada.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AgIO;
using AgLibrary.Logging;
using AgroParallel.Diagnostico;

namespace AgOpenGPS
{
    internal sealed class VigiaFuentes : IDisposable
    {
        private readonly MonitorFuentesMudas _monitor;
        private readonly GuidanceEngineHost _host;
        private readonly CoreXEngineHost _corex;
        private readonly Func<IReadOnlyList<AgroParallel.Models.NodoStatus>> _nodos;
        private Timer _timer;
        private int _enCurso;

        public VigiaFuentes(MonitorFuentesMudas monitor, GuidanceEngineHost host, CoreXEngineHost corex,
            Func<IReadOnlyList<AgroParallel.Models.NodoStatus>> nodos)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            _host = host;
            _corex = corex;
            _nodos = nodos;

            _monitor.Registrar("gps", "GPS (posición en el motor)", TimeSpan.FromSeconds(3));
            _monitor.Registrar("direccion", "Módulo de dirección (PGN 253)", TimeSpan.FromSeconds(3));
            if (_corex != null)
            {
                _monitor.Registrar("maquina", "Módulo de máquina (CoreX)", TimeSpan.FromSeconds(5));
                _monitor.Registrar("imu", "IMU por red (CoreX)", TimeSpan.FromSeconds(5));
            }
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(_ => Tick(), null, 1000, 1000);
            Log.EventWriter("Monitor de fuente muda: ACTIVO (GPS, dirección PGN 253"
                + (_corex != null ? ", máquina, IMU" : "") + ", nodos MQTT)");
        }

        private void Tick()
        {
            // Un tick a la vez: si uno se demora (lock del registro de nodos), el
            // siguiente no se apila.
            if (Interlocked.Exchange(ref _enCurso, 1) == 1) return;
            try
            {
                DateTime ahora = DateTime.UtcNow;

                if (_host != null)
                {
                    Emitir(_monitor.Observar("gps", Marca(_host.lastFixUtc), ahora));

                    long t253 = _host.ultimoPgn253Ticks;
                    DateTime? visto253 = null;
                    if (t253 != 0)
                    {
                        double seg = (Stopwatch.GetTimestamp() - t253) / (double)Stopwatch.Frequency;
                        visto253 = ahora - TimeSpan.FromSeconds(Math.Max(0, seg));
                    }
                    Emitir(_monitor.Observar("direccion", visto253, ahora));
                }

                if (_corex != null)
                {
                    Emitir(_monitor.Observar("maquina", Marca(_corex.LastMachineHelloUtc), ahora));
                    Emitir(_monitor.Observar("imu", Marca(_corex.LastImuHelloUtc), ahora));
                }

                var nodos = _nodos?.Invoke();
                if (nodos != null)
                {
                    foreach (var n in nodos)
                    {
                        if (n == null || string.IsNullOrEmpty(n.Uid)) continue;
                        string nombre = "Nodo " + (string.IsNullOrEmpty(n.Type) ? "MQTT" : n.Type) + " " + n.Uid;
                        Emitir(_monitor.ObservarEstado("nodo:" + n.Uid, nombre, n.Online, Marca(n.LastSeenUtc), ahora));
                    }
                }
            }
            catch (Exception ex)
            {
                // Nunca tirar el timer por un dato raro; avisar y seguir.
                try { Console.Error.WriteLine("[Engine] VigiaFuentes: " + ex.Message); } catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _enCurso, 0);
            }
        }

        private static DateTime? Marca(DateTime t)
        {
            if (t == default(DateTime) || t == DateTime.MinValue) return null;
            return t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;
        }

        private static void Emitir(EventoFuente ev)
        {
            if (ev == null) return;
            Log.EventWriter(ev.Mensaje);
            try { Console.WriteLine("[Engine] " + ev.Mensaje); } catch { }
        }

        public void Dispose()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }
    }
}
