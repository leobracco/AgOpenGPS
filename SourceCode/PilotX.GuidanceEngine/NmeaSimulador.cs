// ============================================================================
// NmeaSimulador.cs — GENERADOR de NMEA por RS232 (la contracara del receptor).
//
// Emite GGA/VTG/RMC/HDT por un puerto serie con valores y frecuencia
// configurables desde el panel (:5181, tarjeta en Datos GPS). Sirve para:
//   · probar la ENTRADA RS232 de esta misma PC (par virtual com0com)
//   · alimentar OTRO equipo/pantalla con un GPS simulado por cable real
//   · verificar baudios/cableado con un receptor conocido del otro lado
//
// La posición AVANZA sola con la velocidad y el rumbo configurados (como un
// tractor de verdad), así el guiado del otro lado ve movimiento coherente.
// Los valores se pueden pisar EN CALIENTE (Start con el sim corriendo
// re-aplica config sin cortar el chorro).
// ============================================================================

using System;
using System.Globalization;
using System.Text;

namespace AgIO
{
    public sealed class NmeaSimuladorConfig
    {
        public string Port { get; set; } = "";
        public int Baud { get; set; } = 115200;
        /// <summary>Frecuencia de emisión del grupo de sentencias (1..20 Hz).</summary>
        public double Hz { get; set; } = 10;
        public double Lat { get; set; } = 53.4360564;
        public double Lon { get; set; } = -111.160047;
        public double VelKmh { get; set; } = 3.5;
        /// <summary>Rumbo en grados (0 = norte, horario). La posición avanza hacia acá.</summary>
        public double Rumbo { get; set; }
        public int Fix { get; set; } = 4;
        public int Sats { get; set; } = 12;
        public double Hdop { get; set; } = 0.7;
        public double AltM { get; set; } = 700;
        public bool Gga { get; set; } = true;
        public bool Vtg { get; set; } = true;
        public bool Rmc { get; set; } = true;
        public bool Hdt { get; set; }
    }

    public sealed class NmeaSimulador : IDisposable
    {
        private readonly object _lock = new object();
        private System.IO.Ports.SerialPort _sp;
        private System.Threading.Timer _timer;
        private NmeaSimuladorConfig _cfg = new NmeaSimuladorConfig();
        private double _lat, _lon;
        private long _grupos;
        private string _error = "";

        public bool Corriendo { get; private set; }

        /// <summary>Arranca (o re-configura en caliente si ya corre). Con otro
        /// puerto/baud reabre; con el mismo, solo pisa valores y frecuencia.</summary>
        public bool Start(NmeaSimuladorConfig cfg)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.Port)) { _error = "falta puerto"; return false; }
            cfg.Hz = Math.Max(1, Math.Min(20, cfg.Hz));
            lock (_lock)
            {
                try
                {
                    bool mismoPuerto = Corriendo && _sp != null &&
                        string.Equals(_sp.PortName, cfg.Port, StringComparison.OrdinalIgnoreCase) &&
                        _sp.BaudRate == cfg.Baud;
                    if (!mismoPuerto)
                    {
                        CerrarPuerto();
                        _sp = new System.IO.Ports.SerialPort(cfg.Port, cfg.Baud,
                            System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One)
                        { WriteTimeout = 500 };
                        _sp.Open();
                    }

                    // Posición: si cambia la config arranca de la configurada;
                    // el avance la va moviendo desde ahí.
                    _lat = cfg.Lat;
                    _lon = cfg.Lon;
                    _cfg = cfg;
                    _error = "";
                    int periodo = (int)Math.Round(1000.0 / cfg.Hz);
                    if (_timer == null) _timer = new System.Threading.Timer(_ => Tick(), null, periodo, periodo);
                    else _timer.Change(periodo, periodo);
                    Corriendo = true;
                    return true;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    Stop();
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                try { _timer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                CerrarPuerto();
                Corriendo = false;
            }
        }

        private void CerrarPuerto()
        {
            try { if (_sp != null && _sp.IsOpen) _sp.Close(); } catch { }
            try { _sp?.Dispose(); } catch { }
            _sp = null;
        }

        public object Estado()
        {
            lock (_lock)
            {
                return new
                {
                    Corriendo,
                    Error = _error,
                    Grupos = _grupos,
                    LatActual = Math.Round(_lat, 7),
                    LonActual = Math.Round(_lon, 7),
                    Config = _cfg
                };
            }
        }

        private void Tick()
        {
            NmeaSimuladorConfig cfg;
            System.IO.Ports.SerialPort sp;
            double lat, lon;
            lock (_lock)
            {
                if (!Corriendo || _sp == null || !_sp.IsOpen) return;
                cfg = _cfg;
                sp = _sp;

                // Avance por tick: v en m/s repartida según el rumbo.
                double vms = cfg.VelKmh / 3.6;
                double paso = vms / cfg.Hz;
                double rumboRad = cfg.Rumbo * Math.PI / 180.0;
                _lat += paso * Math.Cos(rumboRad) / 111320.0;
                _lon += paso * Math.Sin(rumboRad) / (111320.0 * Math.Cos(_lat * Math.PI / 180.0));
                lat = _lat;
                lon = _lon;
                _grupos++;
            }

            try
            {
                var sb = new StringBuilder();
                string hora = DateTime.UtcNow.ToString("HHmmss.ff", CultureInfo.InvariantCulture);
                string fecha = DateTime.UtcNow.ToString("ddMMyy", CultureInfo.InvariantCulture);
                string latN = GradosAMinutos(Math.Abs(lat), 2);
                string ns = lat >= 0 ? "N" : "S";
                string lonN = GradosAMinutos(Math.Abs(lon), 3);
                string ew = lon >= 0 ? "E" : "W";
                string velKt = (cfg.VelKmh / 1.852).ToString("F2", CultureInfo.InvariantCulture);
                string velKm = cfg.VelKmh.ToString("F2", CultureInfo.InvariantCulture);
                string rumbo = cfg.Rumbo.ToString("F1", CultureInfo.InvariantCulture);

                if (cfg.Gga)
                    sb.Append(ConChecksum(string.Format(CultureInfo.InvariantCulture,
                        "GPGGA,{0},{1},{2},{3},{4},{5},{6},{7:F1},{8:F1},M,0.0,M,,",
                        hora, latN, ns, lonN, ew, cfg.Fix, cfg.Sats, cfg.Hdop, cfg.AltM)));
                if (cfg.Vtg)
                    sb.Append(ConChecksum(string.Format(CultureInfo.InvariantCulture,
                        "GPVTG,{0},T,,M,{1},N,{2},K,A", rumbo, velKt, velKm)));
                if (cfg.Rmc)
                    sb.Append(ConChecksum(string.Format(CultureInfo.InvariantCulture,
                        "GPRMC,{0},A,{1},{2},{3},{4},{5},{6},{7},,,A",
                        hora, latN, ns, lonN, ew, velKt, rumbo, fecha)));
                if (cfg.Hdt)
                    sb.Append(ConChecksum(string.Format(CultureInfo.InvariantCulture,
                        "GPHDT,{0},T", rumbo)));

                var datos = Encoding.ASCII.GetBytes(sb.ToString());
                sp.Write(datos, 0, datos.Length);
            }
            catch (Exception ex)
            {
                // Puerto desenchufado a mitad de emisión: parar limpio y dejar
                // el motivo visible en el panel.
                _error = ex.Message;
                Stop();
            }
        }

        private static string GradosAMinutos(double grados, int digitosGrado)
        {
            int d = (int)grados;
            double m = (grados - d) * 60.0;
            string fmtG = digitosGrado == 3 ? "000" : "00";
            return d.ToString(fmtG, CultureInfo.InvariantCulture) +
                   m.ToString("00.000000", CultureInfo.InvariantCulture);
        }

        private static string ConChecksum(string cuerpo)
        {
            int c = 0;
            foreach (char ch in cuerpo) c ^= ch;
            return "$" + cuerpo + "*" + c.ToString("X2") + "\r\n";
        }

        public void Dispose()
        {
            Stop();
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }
    }
}
