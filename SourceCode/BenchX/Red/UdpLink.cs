using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace BenchX.Red;

// Socket UDP del simulador: escucha :8888 y manda broadcast a <subred>.255:9999.
//
// Dos lecciones heredadas que esta clase NO puede perder:
// 1) La escucha es INMORTAL. En el ModSim original una sola excepción (el
//    clásico WSAECONNRESET 10054 — ICMP port-unreachable cuando PilotX está
//    caído) mataba el BeginReceiveFrom y ModSim quedaba sordo para siempre
//    (2026-08-05). Acá el while re-arma pase lo que pase; solo termina con
//    el socket dispuesto de verdad.
// 2) Nada de BeginReceiveFrom en net9: el callback que completa sincrónico
//    recurre y vuela el stack (lección 0e90d4ca en el engine). Bucle async.
public sealed class UdpLink : IDisposable
{
    private readonly Socket? _socket;
    private readonly IPEndPoint _destino = new(IPAddress.None, 0);

    public event Action<byte[]>? DatagramaRecibido;   // hilo de red: el consumidor marshalea
    public long Tx, Rx;
    public string? ErrorBind { get; }
    public bool Conectado => ErrorBind == null;

    public UdpLink(byte s1, byte s2, byte s3, int puertoEscucha = 8888, int puertoDestino = 9999)
    {
        try
        {
            _destino = new IPEndPoint(IPAddress.Parse($"{s1}.{s2}.{s3}.255"), puertoDestino);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _socket.Bind(new IPEndPoint(IPAddress.Any, puertoEscucha));
            _ = Task.Run(BucleRecepcion);
        }
        catch (Exception ex)
        {
            ErrorBind = ex.Message;
            _socket?.Dispose();
            _socket = null;
        }
    }

    private async Task BucleRecepcion()
    {
        var buffer = new byte[1024];
        EndPoint remoto = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            try
            {
                var r = await _socket!.ReceiveFromAsync(buffer, SocketFlags.None, remoto);
                if (r.ReceivedBytes > 0)
                {
                    var datos = new byte[r.ReceivedBytes];
                    Array.Copy(buffer, datos, r.ReceivedBytes);
                    Rx++;
                    DatagramaRecibido?.Invoke(datos);
                }
            }
            catch (ObjectDisposedException) { return; }          // Dispose real: fin
            catch (SocketException) { /* 10054 y afines: seguir escuchando */ }
            catch (Exception) { await Task.Delay(200); }         // raro: respirar y seguir
        }
    }

    public void Enviar(byte[] datos)
    {
        if (_socket == null || datos.Length == 0) return;
        try { _socket.SendTo(datos, _destino); Tx++; }
        catch { /* red caída: el próximo tick reintenta solo */ }
    }

    public void Enviar(string texto) => Enviar(Encoding.ASCII.GetBytes(texto));

    public void Dispose() => _socket?.Dispose();
}
