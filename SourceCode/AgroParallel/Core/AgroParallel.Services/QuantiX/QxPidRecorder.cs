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

        /// <summary>Muestras acumuladas por motor, para el resumen del sidecar.</summary>
        private readonly Dictionary<string, List<QxPidSample>> _porMotor =
            new Dictionary<string, List<QxPidSample>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Muestras que ve el panel en vivo: 300 a 5 Hz son 60 segundos,
        /// suficiente para ver un ciclo de oscilacion lento.</summary>
        public const int MaxBuffer = 300;

        private readonly Dictionary<string, Queue<QxPidSample>> _buffer =
            new Dictionary<string, Queue<QxPidSample>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Config con la que corrio esta sesion. Sin las ganancias no se
        /// puede comparar una corrida con otra, que es para lo que se registra.</summary>
        private MotoresConfig _cfgSesion;

        /// <summary>Cuantas sesiones se conservan. Una sesion es 1 hora (ver
        /// CorteHoraMs), asi que son las ultimas 20 horas de registro.</summary>
        public const int MaxSesiones = 20;

        private const double CorteHoraMs = 60 * 60 * 1000;

        private Timer _flushTimer;
        private Timer _corteTimer;
        private bool _marcada;
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

        /// <summary>Igual que AbrirSesion(), guardando ademas la config con la que
        /// corrio esta sesion: es lo que permite decir "esta curva salio con Kp=2"
        /// cuando se la mira semanas despues.</summary>
        public void AbrirSesion(MotoresConfig cfg)
        {
            _cfgSesion = cfg;
            AbrirSesion();
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

            Purgar();

            _corteTimer = new Timer { Interval = CorteHoraMs, AutoReset = false };
            _corteTimer.Elapsed += (s, e) => Rotar();
            _corteTimer.Start();

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
            lock (_lock)
            {
                _marcaPendiente = texto.Replace(',', ' ').Replace('\n', ' ');
                _marcada = true;
            }
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

                    string clave = (lote[i].Uid ?? "sin-uid") + "_m" +
                        lote[i].MotorIdx.ToString(CultureInfo.InvariantCulture);

                    List<QxPidSample> acc;
                    if (!_porMotor.TryGetValue(clave, out acc))
                    {
                        acc = new List<QxPidSample>();
                        _porMotor[clave] = acc;
                    }
                    acc.Add(lote[i]);

                    // Bajo lock: FlushAhora corre en el timer de flush y BufferDe en
                    // el hilo del servidor HTTP cuando el panel pide datos. Sin el
                    // lock, una peticion que caiga justo durante un flush revienta
                    // el Queue. Los otros diccionarios los toca solo este hilo.
                    lock (_lock)
                    {
                        Queue<QxPidSample> q;
                        if (!_buffer.TryGetValue(clave, out q))
                        {
                            q = new Queue<QxPidSample>(MaxBuffer);
                            _buffer[clave] = q;
                        }
                        q.Enqueue(lote[i]);
                        while (q.Count > MaxBuffer) q.Dequeue();
                    }
                }
                _huboMuestras = true;

                if (_marcada && !string.IsNullOrEmpty(SesionDir)
                    && !File.Exists(Path.Combine(SesionDir, ".marcada")))
                    File.WriteAllText(Path.Combine(SesionDir, ".marcada"), marca ?? "1");
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
            if (_corteTimer != null)
            {
                _corteTimer.Stop();
                _corteTimer.Dispose();
                _corteTimer = null;
            }
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

            if (_huboMuestras) EscribirSidecar();

            _porMotor.Clear();
            lock (_lock) { _buffer.Clear(); _abierta = false; _marcada = false; }

            // Sesion sin una sola muestra: no dejar carpeta huerfana.
            if (!_huboMuestras && !string.IsNullOrEmpty(SesionDir))
            {
                try { if (Directory.Exists(SesionDir)) Directory.Delete(SesionDir, true); }
                catch { } // silencioso a proposito: fallback de I/O del propio logger
            }
        }

        /// <summary>Ultimas muestras de un motor, de la mas vieja a la mas nueva.</summary>
        public List<QxPidSample> BufferDe(string uid, int motorIdx)
        {
            string clave = (uid ?? "sin-uid") + "_m" + motorIdx.ToString(CultureInfo.InvariantCulture);
            lock (_lock)
            {
                Queue<QxPidSample> q;
                if (!_buffer.TryGetValue(clave, out q)) return new List<QxPidSample>();
                return new List<QxPidSample>(q);
            }
        }

        /// <summary>Motores vistos en esta sesion, para el selector del panel.</summary>
        public List<QxPidSample> Motores()
        {
            var res = new List<QxPidSample>();
            lock (_lock)
            {
                foreach (var kv in _buffer)
                    if (kv.Value.Count > 0) res.Add(kv.Value.Peek());
            }
            return res;
        }

        /// <summary>sesion.json: cuando arranco, con que ganancias, y el resumen por
        /// motor. El resumen dice COMO se porto cada motor; la config dice CON QUE.
        /// Sin las dos mitades no se puede comparar una corrida contra otra, que es
        /// exactamente para lo que se registra.</summary>
        private void EscribirSidecar()
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"inicio\": \"").Append(_inicio.ToString("o", inv)).Append("\",\n");
                sb.Append("  \"fin\": \"").Append(DateTime.Now.ToString("o", inv)).Append("\",\n");
                sb.Append("  \"hz\": 5,\n");

                sb.Append("  \"config\": [\n");
                bool primeroCfg = true;
                if (_cfgSesion != null && _cfgSesion.Nodos != null)
                {
                    foreach (var nodo in _cfgSesion.Nodos)
                    {
                        if (nodo == null || nodo.Motores == null) continue;
                        for (int i = 0; i < nodo.Motores.Length; i++)
                        {
                            var mc = nodo.Motores[i];
                            if (mc == null) continue;
                            if (!primeroCfg) sb.Append(",\n");
                            primeroCfg = false;
                            sb.Append("    { \"uid\": \"").Append(nodo.Uid ?? "").Append("\"");
                            sb.Append(", \"m\": ").Append(i.ToString(inv));
                            sb.Append(", \"nombre\": \"").Append(mc.Nombre ?? "").Append("\"");
                            sb.Append(", \"kp\": ").Append(mc.Kp.ToString("F2", inv));
                            sb.Append(", \"ki\": ").Append(mc.Ki.ToString("F2", inv));
                            sb.Append(", \"kd\": ").Append(mc.Kd.ToString("F2", inv));
                            sb.Append(", \"pwm_min\": ").Append(mc.PwmMin.ToString(inv));
                            sb.Append(", \"pwm_max\": ").Append(mc.PwmMax.ToString(inv));
                            sb.Append(", \"deadband\": ").Append(mc.Deadband.ToString(inv));
                            sb.Append(", \"slew_rate\": ").Append(mc.SlewRate.ToString(inv));
                            sb.Append(", \"ff_gain\": ").Append(mc.FFGain.ToString("F2", inv));
                            sb.Append(", \"alpha\": ").Append(mc.Alpha.ToString("F2", inv));
                            sb.Append(", \"max_integral\": ").Append(mc.MaxIntegral.ToString("F2", inv));
                            sb.Append(", \"target_slew_hz_s\": ").Append(mc.TargetSlewHzPerSec.ToString("F0", inv));
                            sb.Append(", \"pid_time\": ").Append(mc.PIDTime.ToString(inv));
                            sb.Append(", \"dientes\": ").Append(mc.DientesEngranaje.ToString(inv));
                            sb.Append(", \"sem_vuelta\": ").Append(mc.SemillasVuelta.ToString("F1", inv));
                            sb.Append(", \"meter_cal\": ").Append(mc.MeterCal.ToString("F3", inv));
                            sb.Append(" }");
                        }
                    }
                }
                sb.Append("\n  ],\n");

                sb.Append("  \"motores\": [\n");
                bool primero = true;
                foreach (var kv in _porMotor)
                {
                    var r = QxPidResumen.Calcular(kv.Value);
                    if (!primero) sb.Append(",\n");
                    primero = false;
                    sb.Append("    { \"uid\": \"").Append(r.Uid ?? "").Append("\"");
                    sb.Append(", \"m\": ").Append(r.MotorIdx.ToString(inv));
                    sb.Append(", \"nombre\": \"").Append(r.Nombre ?? "").Append("\"");
                    sb.Append(", \"rpm_prom\": ").Append(r.RpmProm.ToString("F1", inv));
                    sb.Append(", \"rpm_desvio\": ").Append(r.RpmDesvio.ToString("F1", inv));
                    sb.Append(", \"error_medio_pct\": ").Append(r.ErrorMedioPct.ToString("F1", inv));
                    sb.Append(", \"load_prom_pct\": ").Append(r.LoadPromPct.ToString("F0", inv));
                    sb.Append(", \"tiempo_saturado_pct\": ").Append(r.TiempoSaturadoPct.ToString("F0", inv));
                    sb.Append(" }");
                }
                sb.Append("\n  ]\n}\n");

                File.WriteAllText(Path.Combine(SesionDir, "sesion.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { } // silencioso a proposito: fallback de I/O del propio logger
        }

        /// <summary>Cierra la sesion en curso y abre la siguiente. Las corridas
        /// dentro de una sesion se separan con marcas, no con archivos: el corte
        /// horario existe para que el techo de retencion sea predecible. Sin el,
        /// una jornada de 8 h seria una sola sesion de ~56 MB y veinte de esas
        /// 1,1 GB en vez de los 140 MB que se prometen.</summary>
        private void Rotar()
        {
            CerrarSesion();
            AbrirSesion();
        }

        /// <summary>Deja las ultimas MaxSesiones. Las marcadas no cuentan para el
        /// tope y no se borran nunca — si no, el tope terminaria borrando justo
        /// lo que se quiso guardar.</summary>
        public void Purgar()
        {
            try
            {
                string raiz = Path.Combine(_baseDir, "pid-quantix");
                if (!Directory.Exists(raiz)) return;

                var candidatas = new List<string>();
                foreach (string d in Directory.GetDirectories(raiz))
                    if (!File.Exists(Path.Combine(d, ".marcada"))) candidatas.Add(d);

                candidatas.Sort(StringComparer.OrdinalIgnoreCase);
                int sobran = candidatas.Count - MaxSesiones;
                for (int i = 0; i < sobran; i++)
                {
                    try { Directory.Delete(candidatas[i], true); }
                    catch { } // silencioso a proposito: fallback de I/O del propio logger
                }
            }
            catch { } // silencioso a proposito: fallback de I/O del propio logger
        }

        public void Dispose() { CerrarSesion(); }
    }
}
