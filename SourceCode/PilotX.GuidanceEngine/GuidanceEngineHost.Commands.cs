// ============================================================================
// GuidanceEngineHost.Commands.cs — origen de comando real para btnStates
// (bloque 14, pendiente desde el primer paso). Sin FormGPS no hay botón
// físico que clickear: esto es el equivalente headless de
// FormGPS.ExecuteGuidanceCommand (GUI.FloatingMenu.cs) /
// IGuidanceCalculator.ExecuteCommand (AgroParallel.Services.Abstractions) —
// mismo vocabulario de comandos ("autosteer" hoy), para que el día que este
// proceso reciba comandos de una UI real (Android, MQTT, o el Hub vía
// AgroParallel.WebHost) el punto de entrada ya exista.
//
// Transporte: TCP plano (line-based, un comando por línea/conexión) en vez
// de HTTP — HttpListener no es portable a Linux/Android sin Kestrel, y este
// proceso debe seguir siendo net9.0 puro. Mismo criterio "sin dependencias
// nuevas" que el resto del proyecto.
// ============================================================================

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using AgLibrary.Logging;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        private TcpListener _cmdListener;
        private bool _cmdRunning;

        public void StartCommandServer(int port = 15556)
        {
            if (_cmdRunning) return;
            _cmdListener = new TcpListener(IPAddress.Loopback, port);
            _cmdListener.Start();
            _cmdRunning = true;
            Log.EventWriter("GuidanceEngine: comandos por TCP en 127.0.0.1:" + port);
            AcceptLoop();
        }

        public void StopCommandServer()
        {
            _cmdRunning = false;
            try { _cmdListener?.Stop(); } catch { }
            _cmdListener = null;
        }

        private async void AcceptLoop()
        {
            while (_cmdRunning)
            {
                TcpClient client;
                try { client = await _cmdListener.AcceptTcpClientAsync(); }
                catch { break; } // listener parado (Stop()) -> sale del loop

                _ = HandleClient(client);
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream))
            using (var writer = new StreamWriter(stream) { AutoFlush = true })
            {
                try
                {
                    string line = await reader.ReadLineAsync();
                    bool ok = ExecuteCommand(line);
                    await writer.WriteLineAsync(ok ? "ok" : "unknown");
                }
                catch (Exception ex)
                {
                    Log.EventWriter("GuidanceEngine: error en comando TCP: " + ex.Message);
                }
            }
        }

        // Mismo vocabulario que FormGPS.ExecuteGuidanceCommand — "autosteer"
        // es el único mapeado por ahora (el resto de esa función depende de
        // controles WinForms que acá no existen). Devuelve false para
        // comando vacío/desconocido, igual que la implementación de FormGPS.
        public bool ExecuteCommand(string command)
        {
            string cmd = (command ?? "").Trim().ToLowerInvariant();
            switch (cmd)
            {
                case "autosteer":
                    ((IAutoSteerHost)this).PerformAutoSteerClick();
                    return true;
                default:
                    return false;
            }
        }
    }
}
