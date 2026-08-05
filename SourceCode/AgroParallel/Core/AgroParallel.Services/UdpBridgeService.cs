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
                IsLoopbackConnected = true;
                ArmarRecepcion(esLoopback: true);
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
                IsUdpConnected = true;
                ArmarRecepcion(esLoopback: false);
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
        //
        // OJO — patrón anti-recursión, no simplificar al clásico
        // "EndReceiveFrom + BeginReceiveFrom adentro del callback":
        // en .NET moderno (net8/net9) BeginReceiveFrom completa
        // SINCRÓNICAMENTE cuando ya hay un datagrama encolado, y en ese caso
        // invoca el callback INLINE. Con tráfico sostenido (los PGN de guiado
        // a 10 Hz apenas se activa el piloto) cada datagrama pendiente apila
        // un frame más: LoopbackReceiveCallback → BeginReceiveFrom →
        // LoopbackReceiveCallback → ... hasta StackOverflowException y proceso
        // muerto sin log (así se moría el engine al activar el piloto,
        // 2026-08-05). En net48 (AgIO original) el completado sincrónico casi
        // no ocurre y por eso el patrón viejo sobrevivió años.
        //
        // La regla acá: el callback SOLO procesa completados asíncronos; los
        // sincrónicos los drena el while de ArmarRecepcion en un stack plano.
        private void LoopbackReceiveCallback(IAsyncResult ar)
        {
            if (ar.CompletedSynchronously) return;   // lo drena ArmarRecepcion
            if (ProcesarRecepcion(ar, esLoopback: true)) ArmarRecepcion(esLoopback: true);
        }

        private void UdpReceiveCallback(IAsyncResult ar)
        {
            if (ar.CompletedSynchronously) return;   // lo drena ArmarRecepcion
            if (ProcesarRecepcion(ar, esLoopback: false)) ArmarRecepcion(esLoopback: false);
        }

        /// <summary>
        /// Arma el BeginReceiveFrom y drena en un while todos los completados
        /// sincrónicos (stack plano). Sale cuando la operación queda pendiente
        /// (la sigue el callback) o el socket se cerró.
        /// </summary>
        private void ArmarRecepcion(bool esLoopback)
        {
            while (true)
            {
                Socket s = esLoopback ? _loopbackSocket : _udpSocket;
                if (s == null) return;
                IAsyncResult ar;
                try
                {
                    ar = esLoopback
                        ? s.BeginReceiveFrom(_bufferLoop, 0, _bufferLoop.Length, SocketFlags.None,
                            ref _epLoopback, LoopbackReceiveCallback, null)
                        : s.BeginReceiveFrom(_bufferUdp, 0, _bufferUdp.Length, SocketFlags.None,
                            ref _epUdp, UdpReceiveCallback, null);
                }
                catch (ObjectDisposedException) { return; }
                catch
                {
                    // Socket en mal estado transitorio: reintentar en frío para
                    // no quedar girando caliente ni matar la escucha para
                    // siempre (el bug viejo: catch{} sin re-armar = bridge
                    // sordo silencioso hasta reiniciar).
                    bool capturado = esLoopback;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        System.Threading.Thread.Sleep(100);
                        ArmarRecepcion(capturado);
                    });
                    return;
                }
                if (!ar.CompletedSynchronously) return;  // sigue el callback
                if (!ProcesarRecepcion(ar, esLoopback)) return;
            }
        }

        /// <summary>Devuelve false solo si hay que dejar de escuchar (socket cerrado).</summary>
        private bool ProcesarRecepcion(IAsyncResult ar, bool esLoopback)
        {
            Socket s = esLoopback ? _loopbackSocket : _udpSocket;
            if (s == null) return false;
            try
            {
                int len = esLoopback
                    ? s.EndReceiveFrom(ar, ref _epLoopback)
                    : s.EndReceiveFrom(ar, ref _epUdp);
                if (len > 0)
                {
                    byte[] msg;
                    IPEndPoint origen;
                    if (esLoopback)
                    {
                        msg = new byte[len];
                        Array.Copy(_bufferLoop, msg, len);
                        origen = _epLoopback as IPEndPoint;
                    }
                    else
                    {
                        msg = new byte[len];
                        Array.Copy(_bufferUdp, msg, len);
                        origen = _epUdp as IPEndPoint;
                    }
                    try
                    {
                        if (esLoopback) OnLoopbackReceived?.Invoke(msg, origen);
                        else OnUdpReceived?.Invoke(msg, origen);
                    }
                    catch { /* un handler roto no debe matar la escucha */ }
                }
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            // SocketException acá es casi siempre WSAECONNRESET (10054): un
            // ICMP port-unreachable de algún SendUdpTo a un destino apagado.
            // Es ruido — hay que SEGUIR escuchando (el bug viejo moría acá).
            catch (SocketException) { return true; }
            catch { return true; }
        }

        private static void SendCallback(IAsyncResult ar)
        {
            try { ((Socket)ar.AsyncState).EndSend(ar); } catch { }
        }
    }
}
