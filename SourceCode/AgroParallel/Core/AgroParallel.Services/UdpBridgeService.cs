// ============================================================================
// UdpBridgeService.cs — Bridge UDP portable (netstandard2.0).
// Sockets puros sin WinForms. Los handlers de datos recibidos se disparan
// via events (no BeginInvoke). El host WinForms/Android registra callbacks.
// ============================================================================

using System;
using System.Net;
using System.Net.Sockets;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class UdpBridgeService : IUdpBridgeService
    {
        private Socket _loopbackSocket;
        private Socket _udpSocket;
        private EndPoint _epLoopback = new IPEndPoint(IPAddress.Any, 0);
        private EndPoint _epUdp = new IPEndPoint(IPAddress.Any, 0);
        private IPEndPoint _epSendLoopback;
        private readonly byte[] _bufferLoop = new byte[1024];
        private readonly byte[] _bufferUdp = new byte[1024];

        public bool IsLoopbackConnected { get; private set; }
        public bool IsUdpConnected { get; private set; }

        public event Action<byte[], IPEndPoint> OnLoopbackReceived;
        public event Action<byte[], IPEndPoint> OnUdpReceived;

        public void StartLoopback(string loopbackSendIp = "127.0.0.1",
                                  int listenPort = 17777, int sendPort = 15555)
        {
            _epSendLoopback = new IPEndPoint(IPAddress.Parse(loopbackSendIp), sendPort);
            try
            {
                _loopbackSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _loopbackSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                _loopbackSocket.Bind(new IPEndPoint(IPAddress.Loopback, listenPort));
                _loopbackSocket.BeginReceiveFrom(_bufferLoop, 0, _bufferLoop.Length, SocketFlags.None,
                    ref _epLoopback, LoopbackReceiveCallback, null);
                IsLoopbackConnected = true;
            }
            catch { IsLoopbackConnected = false; }
        }

        public void StartUdp(int listenPort = 9999)
        {
            try
            {
                _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                // Puerto COMPARTIDO a propósito: acá entra el broadcast de los
                // módulos (GPS/dirección/máquina) y más de un proceso de la PC
                // necesita esa misma copia — el caso concreto es el relay que
                // mete el GPS adentro del emulador Android (tools\
                // emulador-gps-relay.ps1), que está detrás del NAT de QEMU y no
                // ve el broadcast de la LAN. Sin esto Windows rechaza el
                // segundo bind con WSAEACCES y había que matar la cadena entera
                // para poder probar en el emulador.
                _udpSocket.ExclusiveAddressUse = false;
                _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpSocket.Bind(new IPEndPoint(IPAddress.Any, listenPort));
                _udpSocket.BeginReceiveFrom(_bufferUdp, 0, _bufferUdp.Length, SocketFlags.None,
                    ref _epUdp, UdpReceiveCallback, null);
                IsUdpConnected = true;
            }
            catch { IsUdpConnected = false; }
        }

        public void Stop()
        {
            try { _loopbackSocket?.Close(); } catch { }
            try { _udpSocket?.Close(); } catch { }
            _loopbackSocket = null;
            _udpSocket = null;
            IsLoopbackConnected = false;
            IsUdpConnected = false;
        }

        public void SendToLoopback(byte[] data)
        {
            if (!IsLoopbackConnected || _loopbackSocket == null || data == null || data.Length == 0) return;
            try
            {
                _loopbackSocket.BeginSendTo(data, 0, data.Length, SocketFlags.None,
                    _epSendLoopback, SendCallback, _loopbackSocket);
            }
            catch { }
        }

        public void SendUdpTo(byte[] data, IPEndPoint endPoint)
        {
            if (!IsUdpConnected || _udpSocket == null || data == null || data.Length == 0 || endPoint == null) return;
            try
            {
                _udpSocket.BeginSendTo(data, 0, data.Length, SocketFlags.None,
                    endPoint, SendCallback, _udpSocket);
            }
            catch { }
        }

        public void Dispose() => Stop();

        // ── Callbacks async ─────────────────────────────────────────────
        private void LoopbackReceiveCallback(IAsyncResult ar)
        {
            try
            {
                int len = _loopbackSocket.EndReceiveFrom(ar, ref _epLoopback);
                if (len > 0)
                {
                    var msg = new byte[len];
                    Array.Copy(_bufferLoop, msg, len);
                    OnLoopbackReceived?.Invoke(msg, _epLoopback as IPEndPoint);
                }
                _loopbackSocket.BeginReceiveFrom(_bufferLoop, 0, _bufferLoop.Length, SocketFlags.None,
                    ref _epLoopback, LoopbackReceiveCallback, null);
            }
            catch { }
        }

        private void UdpReceiveCallback(IAsyncResult ar)
        {
            try
            {
                int len = _udpSocket.EndReceiveFrom(ar, ref _epUdp);
                if (len > 0)
                {
                    var msg = new byte[len];
                    Array.Copy(_bufferUdp, msg, len);
                    OnUdpReceived?.Invoke(msg, _epUdp as IPEndPoint);
                }
                _udpSocket.BeginReceiveFrom(_bufferUdp, 0, _bufferUdp.Length, SocketFlags.None,
                    ref _epUdp, UdpReceiveCallback, null);
            }
            catch { }
        }

        private static void SendCallback(IAsyncResult ar)
        {
            try { ((Socket)ar.AsyncState).EndSend(ar); } catch { }
        }
    }
}
