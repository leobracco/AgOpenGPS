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
//            (comportamiento ya validado antes). Con este flag TAMBIÉN se
//            suscribe el comando de guiado (GuidanceEngineHost.ExecuteCommand)
//            al tópico MQTT "agp/aog/guidance/command" — el canal real,
//            además del TCP de prueba en :15556.
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
            bool useWebHost = Array.IndexOf(args, "--webhost") >= 0;

            var baseDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "GuidanceEngineData"));
            if (!baseDir.Exists) baseDir.Create();

            // Mismo fieldsDirectory que usa PilotX real (MyDocuments\AgOpenGPS\Fields,
            // o DataRootOverride en Android) — necesario para poder abrir un lote real
            // por nombre (comando "job_start_<lote>"). No toca Windows Registry en
            // net9.0 puro (guard #if NETFRAMEWORK || WINDOWS en RegistrySettings.cs).
            // En el paquete el motor vive en <install>\Engine\, pero la config de
            // arranque (aog_settings.json: carpeta de trabajo, perfil de vehículo,
            // idioma) es de la INSTALACIÓN y vive un nivel arriba — el ZIP de
            // release no la incluye a propósito, para no pisar la del cliente al
            // actualizar. Sin esto el motor arrancaría sin perfil y correría con
            // la geometría por defecto (antena/ancho/ganancias), que es un error
            // silencioso y difícil de diagnosticar en cabina.
            var parentCfg = Path.Combine(
                Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? "",
                "aog_settings.json");
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "aog_settings.json"))
                && File.Exists(parentCfg))
            {
                var raizInstalacion = Path.GetDirectoryName(parentCfg);
                RegistrySettings.AppBasePath = raizInstalacion;
                Console.WriteLine("Config de arranque heredada de la instalación: " + parentCfg);

                // MISMO razonamiento para las configs de los productos X-*
                // (vistaX.json, quantiX_motores.json, nodos.json, implementos\…):
                // por defecto se guardan junto al exe, o sea en <install>\Engine\,
                // mientras PilotX las lee en <install>\. Quedaban DOS juegos de
                // configuración: el operario configuraba un implemento desde el
                // motor y PilotX seguía con el viejo, sin ningún error visible.
                AgroParallel.Common.AgpPaths.ConfigRoot = raizInstalacion;
                Console.WriteLine("Configs de productos X-*: " + raizInstalacion);
            }

            RegistrySettings.Load();

            // El perfil de vehículo (Vehicles\<nombre>.XML) trae TODA la config
            // real del operario: geometría, antena, ganancias de dirección,
            // secciones. Sin este Load el motor headless corría siempre con los
            // valores por defecto del código y ningún Save() persistía
            // (vehicleFileName vacío → CSettings.Save() es no-op). Mismo Load que
            // hace PilotX/FormGPS al arrancar.
            var vehLoad = AgOpenGPS.Properties.Settings.Default.Load();
            Console.WriteLine("Perfil de vehículo: "
                + (string.IsNullOrEmpty(RegistrySettings.vehicleFileName) ? "(ninguno)" : RegistrySettings.vehicleFileName)
                + " → " + vehLoad);

            Console.WriteLine("PilotX.GuidanceEngine — bloque 14, guidance engine headless");
            Console.WriteLine("Base directory: " + baseDir.FullName);
            Console.WriteLine("Fields directory: " + RegistrySettings.fieldsDirectory);

            var host = new GuidanceEngineHost(baseDir);
            host.Start();
            Console.WriteLine("Escuchando PGN en 127.0.0.1:15555, respondiendo a 127.255.255.255:17777.");

            host.StartCommandServer();
            Console.WriteLine("Comandos por TCP en 127.0.0.1:15556 (linea de texto, ej. \"autosteer\").");

            EngineWebHost webHost = null;
            if (useWebHost)
            {
                webHost = new EngineWebHost(host, 5180);
                webHost.Start();
                Console.WriteLine("Modo --webhost: API HTTP /api/aog/* arriba en " + webHost.Url
                    + " (state/coverage/tool/tram/paths/guidance) — PilotX.Desktop puede renderizar el mapa contra este motor.");
            }

            CoreXEngineHost coreX = null;
            if (useCoreX)
            {
                coreX = new CoreXEngineHost();
                coreX.StartServices();
                Console.WriteLine("Modo --corex: broker MQTT (:1883), bridge UDP LAN (:9999) y puertos serie arriba, mismo proceso.");

                coreX.SubscribeCommands(cmd =>
                {
                    bool ok = host.ExecuteCommand(cmd);
                    Console.WriteLine($"MQTT cmd \"{cmd}\" -> {(ok ? "ok" : "unknown")}");
                });
                Console.WriteLine("Comandos por MQTT en topico agp/aog/guidance/command.");
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
            webHost?.Stop();
            coreX?.Stop();
            host.StopCommandServer();
            host.Stop();
        }
    }
}
