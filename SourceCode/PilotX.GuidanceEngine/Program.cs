// ============================================================================
// Program.cs — entry point del guidance engine headless (bloque 14).
//
// Modos, no excluyentes:
//   --sim    arranca el simulador interno (CSim.DoSimTick, el mismo que usa
//            FormGPS en su modo simulador) para probar el loop completo
//            heading -> posición corregida -> autosteer -> youturn en
//            proceso, sin CoreX ni hardware. Loguea cada tick.
//   --corex  arranca TAMBIÉN CoreXEngineHost en este mismo proceso: broker
//            MQTT, bridge UDP (loopback + LAN), NTRIP y los 6 puertos serie
//            reales (GPS/GPS2/RTCM/IMU/Steer/Machine) — el "otro lado" que
//            hoy es AgIO/CoreX.exe aparte. Sin este flag, GuidanceEngineHost
//            sigue escuchando en :15555 esperando un CoreX externo real
//            (comportamiento ya validado antes).
//
// Ctrl+C para salir.
// ============================================================================

using System;
using System.IO;
using System.Threading;
using AgIO;
using AgLibrary.Logging;

namespace AgOpenGPS
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            bool useSim = Array.IndexOf(args, "--sim") >= 0;
            bool useCoreX = Array.IndexOf(args, "--corex") >= 0;

            var baseDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "GuidanceEngineData"));
            if (!baseDir.Exists) baseDir.Create();

            Console.WriteLine("PilotX.GuidanceEngine — bloque 14, guidance engine headless");
            Console.WriteLine("Base directory: " + baseDir.FullName);

            var host = new GuidanceEngineHost(baseDir);
            host.Start();
            Console.WriteLine("Escuchando PGN en 127.0.0.1:15555, respondiendo a 127.255.255.255:17777.");

            host.StartCommandServer();
            Console.WriteLine("Comandos por TCP en 127.0.0.1:15556 (linea de texto, ej. \"autosteer\").");

            CoreXEngineHost coreX = null;
            if (useCoreX)
            {
                coreX = new CoreXEngineHost();
                coreX.StartServices();
                Console.WriteLine("Modo --corex: broker MQTT (:1883), bridge UDP LAN (:9999) y puertos serie arriba, mismo proceso.");
            }

            Timer simTimer = null;
            if (useSim)
            {
                Console.WriteLine("Modo --sim: simulador interno (CSim.DoSimTick) a 10 Hz, línea recta ~4 km/h.");
                host.isGPSPositionInitialized = false;
                host.isFirstHeadingSet = false;
                host.isSimTimerEnabled = true;
                host.Sim.stepDistance = 0.11; // ~4 km/h a 10 Hz

                int tick = 0;
                simTimer = new Timer(_ =>
                {
                    host.Sim.DoSimTick(0);
                    tick++;
                    if (tick % 10 == 0)
                    {
                        Console.WriteLine(
                            $"tick={tick,5}  fix=({host.Pn.fix.easting:F2},{host.Pn.fix.northing:F2})  " +
                            $"fixHeading={glm.toDegrees(host.fixHeading):F1}deg  " +
                            $"avgSpeed={host.avgSpeed:F1}  isFirstHeadingSet={host.isFirstHeadingSet}  " +
                            $"crossTrackError={host.crossTrackError}");
                    }
                }, null, 0, 100);
            }

            Console.WriteLine("Ctrl+C para salir.");

            var exit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exit.Set();
            };

            exit.Wait();

            simTimer?.Dispose();
            Log.EventWriter("GuidanceEngine: cerrando");
            coreX?.Stop();
            host.StopCommandServer();
            host.Stop();
        }
    }
}
