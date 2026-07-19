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
    public sealed class UdpBridgeService : IUdpBridgeService, IDisposable
    {
        private Socket _loopbackSocket;
        private Socket _udpSocket;
        private EndPoint _epLoopback = new IPEndPoint(IPAddress.Any, 0);
        private EndPoint _epUdp = new IPEndPoint(IPAddress.Any, 0);
        private IPEndPoint _epSendLoopback;
        private IPEndPoint _epSendModule;
        private readonly byte[] _bufferLoop = new byte[1024];
        private readonly byte[] _bufferUdp = new byte[1024];

        public bool IsLoopbackConnected { get; private set; }
        public bool IsUdpConnected { get; private set; }

        public event Action<byte[]> OnLoopbackReceived;
        public event Action<byte[]> OnUdpReceived;

        public void Start(string moduleSubnet, string loopbackIp = "127.0.0.1",
                          int loopbackListenPort = 17777, int loopbackSendPort = 15555,
                          int udpListenPort = 9999, int udpSendPort = 8888)
        {
            _epSendLoopback = new IPEndPoint(IPAddress.Parse(loopbackIp), loopbackSendPort);
            _epSendModule = new IPEndPoint(IPAddress.Parse(moduleSubnet + ".255"), udpSendPort);

            // Loopback socket
            try
            {
                _loopbackSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _loopbackSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                _loopbackSocket.Bind(new IPEndPoint(IPAddress.Loopback, loopbackListenPort));
                _loopbackSocket.BeginReceiveFrom(_bufferLoop, 0, _bufferLoop.Length, SocketFlags.None,
                    ref _epLoopback, LoopbackReceiveCallback, null);
                IsLoopbackConnected = true;
            }
            catch { IsLoopbackConnected = false; }

            // UDP LAN socket
            try
            {
                _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                _udpSocket.Bind(new IPEndPoint(IPAddress.Any, udpListenPort));
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

        public void SendToModules(byte[] data)
        {
            if (!IsUdpConnected || _udpSocket == null || data == null || data.Length == 0) return;
            try
            {
                _udpSocket.BeginSendTo(data, 0, data.Length, SocketFlags.None,
                    _epSendModule, SendCallback, _udpSocket);
            }
            catch { }
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

        // Enviar a un endpoint específico por UDP (para NTRIP broadcast).
        public void SendUdpTo(byte[] data, IPEndPoint ep)
        {
            if (!IsUdpConnected || _udpSocket == null || data == null || data.Length == 0) return;
            try
            {
                _udpSocket.BeginSendTo(data, 0, data.Length, SocketFlags.None,
                    ep, SendCallback, _udpSocket);
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
                    OnLoopbackReceived?.Invoke(msg);
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
                    OnUdpReceived?.Invoke(msg);
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
