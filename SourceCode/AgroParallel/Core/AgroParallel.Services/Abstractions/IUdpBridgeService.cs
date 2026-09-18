// ============================================================================
// IUdpBridgeService.cs — Bridge UDP portable (netstandard2.0).
// Extraído del UDP.designer.cs de CoreX. Maneja:
//   - Loopback socket (PilotX ↔ CoreX, :17777 ↔ :15555)
//   - UDP LAN socket (CoreX ↔ módulos hardware, :9999 ↔ :8888)
// Sin WinForms: los callbacks de datos usan Action en vez de BeginInvoke.
// Los sockets arrancan por separado (CoreX levanta loopback siempre y UDP
// solo si está habilitado en settings). Los endpoints de destino los maneja
// el host (epModule es mutable en caliente), por eso los Send reciben el
// endpoint explícito.
// ============================================================================

using System;
using System.Net;

namespace AgroParallel.Services.Abstractions
{
    public interface IUdpBridgeService : IDisposable
    {
        bool IsLoopbackConnected { get; }
        bool IsUdpConnected { get; }

        // Loopback PilotX↔CoreX. loopbackSendIp/Port = destino de SendToLoopback.
        void StartLoopback(string loopbackSendIp = "127.0.0.1",
                           int listenPort = 17777, int sendPort = 15555);

        // UDP LAN CoreX↔módulos. Escucha broadcast en listenPort.
        void StartUdp(int listenPort = 9999);

        void Stop();

        // Enviar datos por loopback a PilotX (destino fijado en StartLoopback).
        void SendToLoopback(byte[] data);

        // Enviar por UDP LAN a un endpoint arbitrario (módulos .255:8888, NTRIP, etc.).
        void SendUdpTo(byte[] data, IPEndPoint endPoint);

        // Callbacks: el host registra handlers para recibir datos.
        // OnLoopbackReceived: datos de PilotX → CoreX (para reenviar a módulos/serial).
        // OnUdpReceived: datos de módulos → CoreX (para reenviar a PilotX).
        // El segundo parámetro es el endpoint remoto (para logs/monitor).
        event Action<byte[], IPEndPoint> OnLoopbackReceived;
        event Action<byte[], IPEndPoint> OnUdpReceived;
    }
}
