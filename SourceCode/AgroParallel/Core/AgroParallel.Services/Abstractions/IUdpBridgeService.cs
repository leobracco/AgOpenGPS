// ============================================================================
// IUdpBridgeService.cs — Bridge UDP portable (netstandard2.0).
// Extraído del UDP.designer.cs de CoreX. Maneja:
//   - Loopback socket (PilotX ↔ CoreX, :17777 ↔ :15555)
//   - UDP LAN socket (CoreX ↔ módulos hardware, :9999 ↔ :8888)
// Sin WinForms: los callbacks de datos usan Action en vez de BeginInvoke.
// ============================================================================

using System;
using System.Net;

namespace AgroParallel.Services.Abstractions
{
    public interface IUdpBridgeService
    {
        bool IsLoopbackConnected { get; }
        bool IsUdpConnected { get; }

        // Arranca los sockets. moduleSubnet = "192.168.5" (los 3 primeros octetos).
        // loopbackIp = la IP loopback del host (127.0.0.1 por default, configurable).
        void Start(string moduleSubnet, string loopbackIp = "127.0.0.1",
                   int loopbackListenPort = 17777, int loopbackSendPort = 15555,
                   int udpListenPort = 9999, int udpSendPort = 8888);

        void Stop();

        // Enviar datos por UDP LAN a módulos (broadcast .255:8888).
        void SendToModules(byte[] data);

        // Enviar datos por loopback a PilotX.
        void SendToLoopback(byte[] data);

        // Callbacks: el host registra handlers para recibir datos.
        // OnLoopbackReceived: datos de PilotX → CoreX (para reenviar a módulos/serial).
        // OnUdpReceived: datos de módulos → CoreX (para reenviar a PilotX).
        event Action<byte[]> OnLoopbackReceived;
        event Action<byte[]> OnUdpReceived;
    }
}
