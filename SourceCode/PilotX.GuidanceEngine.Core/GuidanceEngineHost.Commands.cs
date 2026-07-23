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
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;

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

        // Mismo vocabulario que FormGPS.ExecuteGuidanceCommand — "autosteer",
        // "uturn" (btnAutoYouTurn), "job_start_<lote>"/"job_close" por ahora
        // (el resto de esa función depende de controles WinForms que acá no
        // existen, ej. sec_auto/sec_manual mezclan lógica pura con manejo de
        // color de ~24 botones que Sections.Designer.cs ya no aísla más
        // — ver auditoría de bloque 9). Devuelve false para comando vacío/
        // desconocido, igual que la implementación de FormGPS. "job_start_"
        // preserva el nombre del lote tal cual vino (no lowercase) por si el
        // nombre de carpeta es case-sensitive (Linux) — mismo criterio que
        // "idioma_" en GUI.FloatingMenu.cs.
        public bool ExecuteCommand(string command)
        {
            string raw = (command ?? "").Trim();
            string cmd = raw.ToLowerInvariant();

            if (cmd.StartsWith("job_start_"))
            {
                return OpenField(raw.Substring("job_start_".Length));
            }

            // Crear + activar una AB en la posición actual del tractor. Equivale
            // a FormBuildTracks.btnEnter_AB (agrega un CTrk AB, setea Trk.idx,
            // nudge a la referencia por ancho/2 + offset). El loop de autosteer
            // (CAutoSteerUpdater) valida la línea solo (BuildCurrentABLineList).
            // "track_new_ab" es el comando que manda el menú Guías; "track_ab_here"
            // permite un heading explícito ("track_ab_here_<grados>").
            // STOPGAP: android reemplaza esto con el ITrackBuilderService completo.
            if (cmd == "track_new_ab" || cmd == "track_ab_here" || cmd.StartsWith("track_ab_here_"))
            {
                double headingRad = fixHeading;
                if (cmd.StartsWith("track_ab_here_") &&
                    double.TryParse(cmd.Substring("track_ab_here_".Length), NumberStyles.Any, CultureInfo.InvariantCulture, out double deg))
                {
                    headingRad = deg * Math.PI / 180.0;
                }
                return CreateAbAtPivot(headingRad);
            }

            switch (cmd)
            {
                case "autosteer":
                    ((IAutoSteerHost)this).PerformAutoSteerClick();
                    return true;
                case "uturn":
                    ToggleYouTurn();
                    return true;
                case "pick":
                    SelectTrack();
                    return true;
                case "job_close":
                    CloseField();
                    return true;
                default:
                    return false;
            }
        }

        // Crea una guía AB anclada en el pivote actual con el rumbo dado y la
        // deja activa (Trk.idx). El loop de guiado la valida en el próximo fix.
        private bool CreateAbAtPivot(double headingRad)
        {
            var t = new CTrk
            {
                mode = TrackMode.AB,
                heading = headingRad,
                isVisible = true,
                name = "AB " + Math.Round(headingRad * 180.0 / Math.PI, 1).ToString(CultureInfo.InvariantCulture) + "°",
                ptA = new vec2(pivotAxlePos.easting, pivotAxlePos.northing),
                ptB = new vec2(
                    pivotAxlePos.easting + Math.Sin(headingRad) * 100.0,
                    pivotAxlePos.northing + Math.Cos(headingRad) * 100.0)
            };
            Trk.gArr.Add(t);
            Trk.idx = Trk.gArr.Count - 1;

            // Nudge a la línea de referencia = ancho/2 - solape/2 + offset,
            // mismo criterio que btnEnter_AB (lado derecho por defecto).
            double dist = (Tool.width - Tool.overlap) * 0.5 + Tool.offset;
            Trk.NudgeRefABLine(dist);

            ABLineField.isABValid = false; // fuerza rebuild en el próximo fix
            Log.EventWriter($"GuidanceEngine: AB creada en pivote, heading={Math.Round(headingRad * 180 / Math.PI, 1)}°, idx={Trk.idx}");
            return true;
        }
    }
}
