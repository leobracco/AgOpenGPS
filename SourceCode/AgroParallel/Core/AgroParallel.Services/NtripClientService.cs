// ============================================================================
// NtripClientService.cs — Cliente NTRIP portable (netstandard2.0).
// Socket TCP puro sin WinForms. Los datos RTCM se disparan via event.
// El host llama SecondTick() desde su loop para manejar reconexión y
// envío periódico de GGA.
// ============================================================================

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class NtripClientService : INtripClientService, IDisposable
    {
        private Socket _socket;
        private readonly byte[] _recvBuffer = new byte[2800];
        private NtripConfig _config;
        private Func<NtripGpsData> _gpsFeedback;

        private int _tickCounter;
        private int _watchdog;
        private bool _starting;
        private int _ggaIntervalSec;
        private int _ggaTickCounter;

        public bool IsConnected { get; private set; }
        public bool IsConnecting { get; private set; }
        public long TotalBytes { get; private set; }
        public string CasterIp => _config?.CasterIp ?? "";

        public event Action<byte[]> OnRtcmData;

        public void Connect(NtripConfig config, Func<NtripGpsData> gpsFeedback)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _gpsFeedback = gpsFeedback;
            _tickCounter = 0;
            _watchdog = 0;
            _starting = false;
            _ggaIntervalSec = config.SendGgaIntervalSec;
            _ggaTickCounter = 0;
            TotalBytes = 0;
            IsConnected = false;
            IsConnecting = false;
        }

        public void Disconnect()
        {
            try
            {
                if (_socket != null && _socket.Connected)
                {
                    _socket.Shutdown(SocketShutdown.Both);
                    _socket.Close();
                }
            }
            catch { }
            _socket = null;
            IsConnected = false;
            IsConnecting = false;
            _starting = false;
        }

        public void SecondTick()
        {
            if (_config == null) return;

            _tickCounter++;

            // Watchdog: si conectado pero sin datos por >30s, reconectar.
            if (_watchdog++ > 30 && IsConnected)
                RequestReconnect();

            // Intentar conectar si no estamos conectados ni conectando.
            if (!IsConnected && !IsConnecting && !_starting && _tickCounter > 20)
                DoConnect();

            // Si conectando y timeout >29s, reconectar.
            if (IsConnecting && _tickCounter > 29)
                RequestReconnect();

            // Si conectando y socket conectado, enviar autorización.
            if (IsConnecting && _socket != null && _socket.Connected)
                SendAuthorization();

            // Envío periódico de GGA.
            if (IsConnected && _ggaIntervalSec > 0)
            {
                _ggaTickCounter++;
                if (_ggaTickCounter >= _ggaIntervalSec)
                {
                    _ggaTickCounter = 0;
                    SendGga();
                }
            }
        }

        public void Dispose() => Disconnect();

        // ── Internals ───────────────────────────────────────────────────
        private void DoConnect()
        {
            try
            {
                if (_socket != null && _socket.Connected)
                {
                    _socket.Shutdown(SocketShutdown.Both);
                    System.Threading.Thread.Sleep(100);
                    _socket.Close();
                }

                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.NoDelay = true;
                _socket.Blocking = false;
                _socket.BeginConnect(
                    new IPEndPoint(IPAddress.Parse(_config.CasterIp), _config.CasterPort),
                    OnConnect, null);

                IsConnecting = true;
            }
            catch
            {
                RequestReconnect();
            }
        }

        private void OnConnect(IAsyncResult ar)
        {
            try
            {
                if (_socket != null && _socket.Connected)
                    _socket.BeginReceive(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None,
                        OnData, null);
            }
            catch { }
        }

        private void OnData(IAsyncResult ar)
        {
            try
            {
                int n = _socket.EndReceive(ar);
                if (n > 0)
                {
                    var data = new byte[n];
                    Array.Copy(_recvBuffer, data, n);

                    TotalBytes += n;
                    _watchdog = 0;
                    OnRtcmData?.Invoke(data);

                    _socket.BeginReceive(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None,
                        OnData, null);
                }
                else
                {
                    _socket.Shutdown(SocketShutdown.Both);
                    _socket.Close();
                    RequestReconnect();
                }
            }
            catch { }
        }

        private void SendAuthorization()
        {
            if (_socket == null || !_socket.Connected) { RequestReconnect(); return; }

            try
            {
                if (!_config.IsTcp)
                {
                    string auth = Convert.ToBase64String(
                        Encoding.ASCII.GetBytes(_config.Username + ":" + _config.Password));

                    string gga = BuildGga();
                    string htt = _config.IsHttp10 ? "1.0" : "1.1";

                    string req = "GET /" + _config.Mount + " HTTP/" + htt + "\r\n";
                    req += "User-Agent: NTRIP AgOpenGPSClient/6.4\r\n";
                    req += "Authorization: Basic " + auth + "\r\n";
                    req += "Accept: */*\r\nConnection: close\r\n\r\n";

                    byte[] bytes = Encoding.ASCII.GetBytes(req);
                    _socket.Send(bytes, bytes.Length, 0);
                }

                IsConnected = true;
                IsConnecting = false;
                _starting = false;
            }
            catch
            {
                RequestReconnect();
            }
        }

        private void SendGga()
        {
            if (!IsConnected || _socket == null || !_socket.Connected) return;
            try
            {
                string gga = BuildGga();
                byte[] bytes = Encoding.ASCII.GetBytes(gga);
                _socket.Send(bytes, bytes.Length, 0);
            }
            catch { RequestReconnect(); }
        }

        private void RequestReconnect()
        {
            _tickCounter = 15;
            IsConnected = false;
            _starting = false;
            IsConnecting = false;
        }

        private string BuildGga()
        {
            var gps = _gpsFeedback?.Invoke();
            double lat = _config.IsGgaManual ? _config.ManualLat : (gps?.Latitude ?? 0);
            double lon = _config.IsGgaManual ? _config.ManualLon : (gps?.Longitude ?? 0);

            double latDeg = (int)lat;
            double lonDeg = (int)lon;
            double latMin = Math.Round((lat - latDeg) * 60.0, 7);
            double lonMin = Math.Round((lon - lonDeg) * 60.0, 7);

            char ns = lat >= 0 ? 'N' : 'S';
            char ew = lon >= 0 ? 'E' : 'W';

            var sb = new StringBuilder();
            sb.Append("$GPGGA,");
            sb.Append(DateTime.UtcNow.ToString("HHmmss.00,", CultureInfo.InvariantCulture));
            sb.Append(Math.Abs(latDeg * 100 + latMin).ToString("0000.000", CultureInfo.InvariantCulture))
              .Append(',').Append(ns).Append(',');
            sb.Append(Math.Abs(lonDeg * 100 + lonMin).ToString("00000.000", CultureInfo.InvariantCulture))
              .Append(',').Append(ew).Append(',');
            sb.Append(gps?.FixQuality ?? 1).Append(',');
            sb.Append(gps?.Satellites ?? 10).Append(',');
            sb.Append((gps?.Hdop > 0 ? gps.Hdop : 1).ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
            sb.Append((gps?.Altitude ?? 0).ToString("0.###", CultureInfo.InvariantCulture)).Append(",M,46.4,M,");
            sb.Append((gps?.Age ?? 0).ToString("0.#", CultureInfo.InvariantCulture)).Append(",0*");

            // Checksum
            int sum = 0;
            for (int i = 1; i < sb.Length; i++)
            {
                if (sb[i] == '*') break;
                sum ^= sb[i];
            }
            sb.Append(sum.ToString("X2"));
            sb.Append("\r\n");
            return sb.ToString();
        }
    }
}
