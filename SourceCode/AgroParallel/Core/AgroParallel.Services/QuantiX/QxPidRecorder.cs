// ============================================================================
// QxPidRecorder.cs — registro de PID de QuantiX: un CSV por motor a 5 Hz.
//
// Para que se pueda contestar en el lote la pregunta que importa: si la
// velocidad se mantiene estable y las rpm suben y bajan, hay que corregir el
// PID. Con el detalle de que "velocidad estable" tiene que ser la del MOTOR
// (vel_motor), no la del tractor: en curva no son la misma y culpar al PID de
// un cambio de target legitimo es el error facil de cometer.
//
// REGLA QUE NO SE NEGOCIA: Registrar() NUNCA toca el disco. El tick de 200 ms
// que lo llama es el que comanda los motores; si se queda esperando un pendrive
// trabado, el motor se queda sin comando en el medio del lote. Encola en
// memoria y vuelve. El disco lo escribe el timer de flush, y si falla, se
// pierde el registro y el motor sigue andando.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Timers;

namespace AgroParallel.QuantiX
{
    public sealed class QxPidRecorder : IDisposable
    {
        public const string Cabecera =
            "t_s,rpm_real,rpm_target,pps_real,pps_target,pwm,load_pct,vel_motor,vel_gps,dosis,sec_on,marca";

        /// <summary>Instancia viva, para que el controller HTTP llegue al buffer
        /// sin sumarle otro parametro al constructor de AgpWebHost.</summary>
        public static QxPidRecorder Instance { get; set; }

        private readonly string _baseDir;
        private readonly object _lock = new object();
        private readonly Queue<QxPidSample> _cola = new Queue<QxPidSample>();
        private readonly Dictionary<string, StreamWriter> _writers =
            new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);

        private Timer _flushTimer;
        private DateTime _inicio;
        private bool _abierta;
        private bool _huboMuestras;
        private string _marcaPendiente;
        private bool _avisoFalloDisco;

        public string SesionDir { get; private set; }

        public QxPidRecorder(string baseDir)
        {
            _baseDir = baseDir;
        }

        public void AbrirSesion()
        {
            lock (_lock)
            {
                if (_abierta) return;
                _inicio = DateTime.Now;
                SesionDir = Path.Combine(_baseDir, "pid-quantix",
                    _inicio.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture));
                _huboMuestras = false;
                _abierta = true;
            }

            _flushTimer = new Timer { Interval = 1000, AutoReset = true };
            _flushTimer.Elapsed += (s, e) => FlushAhora();
            _flushTimer.Start();
        }

        /// <summary>Encola la muestra. No toca disco. No bloquea.</summary>
        public void Registrar(QxPidSample m)
        {
            lock (_lock)
            {
                if (!_abierta) return;
                _cola.Enqueue(m);
            }
        }

        /// <summary>Clava una marca en la proxima fila de TODOS los motores, para
        /// alinear "aca toque Kp" con lo que hicieron los 14 a la vez.</summary>
        public void Marcar(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return;
            lock (_lock) _marcaPendiente = texto.Replace(',', ' ').Replace('\n', ' ');
        }

        public void FlushAhora()
        {
            QxPidSample[] lote;
            string marca;
            lock (_lock)
            {
                if (_cola.Count == 0) return;
                lote = _cola.ToArray();
                _cola.Clear();
                marca = _marcaPendiente;
                _marcaPendiente = null;
            }

            try
            {
                for (int i = 0; i < lote.Length; i++)
                {
                    var w = WriterDe(lote[i].Uid, lote[i].MotorIdx);
                    if (w == null) return;
                    w.WriteLine(Fila(lote[i], marca));
                }
                _huboMuestras = true;
            }
            catch (Exception ex)
            {
                // Disco lleno o pendrive desconectado: se corta el registro y se
                // avisa UNA vez. Los motores siguen comandados.
                if (!_avisoFalloDisco)
                {
                    _avisoFalloDisco = true;
                    System.Diagnostics.Trace.WriteLine(
                        "[QuantiX-PID] registro detenido por error de disco: " + ex.Message);
                }
            }
        }

        private StreamWriter WriterDe(string uid, int motorIdx)
        {
            if (string.IsNullOrEmpty(uid)) uid = "sin-uid";
            string clave = uid + "_m" + motorIdx.ToString(CultureInfo.InvariantCulture);
            StreamWriter w;
            if (_writers.TryGetValue(clave, out w)) return w;

            if (!Directory.Exists(SesionDir)) Directory.CreateDirectory(SesionDir);
            string path = Path.Combine(SesionDir, clave + ".csv");
            bool nuevo = !File.Exists(path);
            // ReadWrite y no Read: con FileShare.Read, Windows le niega el archivo
            // a cualquiera que quiera abrirlo mientras PilotX graba — el operario
            // no podria mirar el CSV en Excel sin cerrar PilotX primero.
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            w = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            if (nuevo) w.WriteLine(Cabecera);
            _writers[clave] = w;
            return w;
        }

        private string Fila(QxPidSample m, string marca)
        {
            var inv = CultureInfo.InvariantCulture;
            double t = (DateTime.Now - _inicio).TotalSeconds;
            var sb = new StringBuilder(96);
            sb.Append(t.ToString("F1", inv)).Append(',');
            sb.Append(m.RpmReal.HasValue ? m.RpmReal.Value.ToString("F0", inv) : "").Append(',');
            sb.Append(m.RpmTarget.ToString("F0", inv)).Append(',');
            sb.Append(m.PpsReal.ToString("F2", inv)).Append(',');
            sb.Append(m.PpsTarget.ToString("F2", inv)).Append(',');
            sb.Append(m.Pwm.ToString(inv)).Append(',');
            sb.Append(m.LoadPct.ToString(inv)).Append(',');
            sb.Append(m.VelMotorKmh.ToString("F2", inv)).Append(',');
            sb.Append(m.VelGpsKmh.ToString("F2", inv)).Append(',');
            sb.Append(m.Dosis.ToString("F2", inv)).Append(',');
            sb.Append(m.SeccionOn ? '1' : '0').Append(',');
            sb.Append(marca ?? "");
            return sb.ToString();
        }

        public void CerrarSesion()
        {
            if (_flushTimer != null)
            {
                _flushTimer.Stop();
                _flushTimer.Dispose();
                _flushTimer = null;
            }
            FlushAhora();

            foreach (var kv in _writers)
            {
                try { kv.Value.Flush(); kv.Value.Dispose(); } catch { } // silencioso a proposito: fallback de I/O del propio logger
            }
            _writers.Clear();

            lock (_lock) _abierta = false;

            // Sesion sin una sola muestra: no dejar carpeta huerfana.
            if (!_huboMuestras && !string.IsNullOrEmpty(SesionDir))
            {
                try { if (Directory.Exists(SesionDir)) Directory.Delete(SesionDir, true); }
                catch { } // silencioso a proposito: fallback de I/O del propio logger
            }
        }

        public void Dispose() { CerrarSesion(); }
    }
}
