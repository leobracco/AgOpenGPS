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
//            hoy es CoreX.exe aparte. Sin este flag, GuidanceEngineHost
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
using PilotX.GuidanceEngine;
using PilotX.GuidanceEngine.Adapters;

namespace AgOpenGPS
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            bool useSim = Array.IndexOf(args, "--sim") >= 0;
            bool useCoreX = Array.IndexOf(args, "--corex") >= 0;
            bool useWebHost = Array.IndexOf(args, "--webhost") >= 0;
            // Corte por área ya trabajada: ENCENDIDO por defecto. No sembrar dos
            // veces lo mismo es el comportamiento normal de la máquina, no una
            // opción de arranque — si el operario quiere aplicar sobre lo
            // trabajado, para eso están los botones de sección (Off/Auto/On) en
            // la pantalla. Nació apagado hasta validarlo en lote; validado en
            // campo el 2026-08-01, pasa a default.
            // `--sin-antisolape` queda como salida de emergencia para poder
            // apagarlo en cabina sin recompilar si algún día se porta mal.
            bool useAntiSolape = Array.IndexOf(args, "--sin-antisolape") < 0;
            // Guiado del implemento (nivel A, CompensacionImplemento): lo prende
            // el setting setAS_guiadoImplemento (APAGADO de fábrica hasta
            // validarlo en lote). `--sin-guiado-implemento` es la salida de
            // emergencia: pisa el setting sin recompilar ni tocar el perfil.
            bool bloquearGuiadoImplemento = Array.IndexOf(args, "--sin-guiado-implemento") >= 0;

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

            // Despues de Load: es Load quien define la ruta del archivo de log
            // (via Log.CheckLogSize). Antes de esto el motor no tenia manejador
            // de errores NI bajaba su log a disco: todo lo que logueaba quedaba
            // en un StringBuilder que moria con el proceso.
            CrashLog.Instalar();

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

            // SIN PERFIL, NADA PERSISTE: Settings.Save() es un no-op con
            // vehicleFileName vacío (guard en Settings.cs), y el motor headless
            // arranca sin perfil porque el diálogo de elegirlo es de FormGPS.
            // Consecuencia real: ganancias de dirección, antena, IMU, U-turn,
            // relés y tram "se guardaban" y volvían a fábrica en cada arranque
            // (a las secciones ya las salvaba tool.json). Acá el motor se crea
            // su perfil por defecto — con nombre y activo, TODOS los Save()
            // existentes empiezan a escribir el XML completo, que además queda
            // exportable a otro tractor.
            //
            // Convivencia con la app WinForms (PilotX.exe): comparte el mismo
            // aog_settings.json de la instalación, así que al abrir va a cargar
            // este MISMO perfil — consistencia, no conflicto. Si algún día
            // corren a la vez y ambos guardan, gana el último (igual que
            // siempre fue entre pantallas); no corren a la vez en operación.
            if (string.IsNullOrEmpty(RegistrySettings.vehicleFileName))
            {
                const string perfilDefault = "PilotX";
                RegistrySettings.Save(RegKeys.vehicleFileName, perfilDefault);
                string xmlPerfil = Path.Combine(RegistrySettings.vehiclesDirectory, perfilDefault + ".XML");
                if (File.Exists(xmlPerfil))
                {
                    // Había un perfil PilotX de una corrida anterior (o de la
                    // app WinForms): cargarlo — es la config real del operario.
                    var r2 = AgOpenGPS.Properties.Settings.Default.Load();
                    Console.WriteLine("Perfil por defecto ya existía: " + perfilDefault + " → " + r2);
                }
                else
                {
                    // Crearlo con la config actual (defaults + lo que ya haya
                    // cargado tool.json más abajo lo pisa igual).
                    AgOpenGPS.Properties.Settings.Default.Save();
                    Console.WriteLine("Perfil por defecto creado: " + xmlPerfil);
                }
            }

            // Geometría del implemento guardada por el propio motor. VA ACÁ:
            // después del Load del perfil (para pisarlo con lo último que
            // configuró el operario) y ANTES de construir el host, que arma
            // CTool/CVehicle y reparte el ancho entre las secciones leyendo
            // estos mismos campos de Settings.
            //
            // Sin esto la config de secciones se perdía en cada arranque: el
            // Save() de Settings es un no-op cuando no hay perfil de vehículo
            // elegido (vehicle_file_name vacío, que es el caso normal del motor
            // headless — el diálogo de perfiles es de FormGPS). El operario
            // configuraba 14 secciones de 0,52 m, andaba en caliente, y al
            // reiniciar el proceso volvía a los defaults del código: 3 × 4 m.
            ToolGeometryStore.UsarCarpeta(baseDir.FullName);
            bool geomCargada = ToolGeometryStore.Cargar();
            Console.WriteLine("Geometría del implemento: "
                + (geomCargada ? "de " + ToolGeometryStore.Ruta : "sin archivo propio, se usa el perfil/defaults")
                + " → " + ToolGeometryStore.Resumen());

            Console.WriteLine("PilotX.GuidanceEngine — bloque 14, guidance engine headless");
            Console.WriteLine("Base directory: " + baseDir.FullName);
            Console.WriteLine("Fields directory: " + RegistrySettings.fieldsDirectory);

            var host = new GuidanceEngineHost(baseDir);

            // Se engancha siempre; Habilitado decide si interviene.
            var antiSolape = new AntiSolapeSecciones(host)
            {
                Habilitado = useAntiSolape,
                Diagnostico = Array.IndexOf(args, "--antisolape-debug") >= 0,
            };
            host.AntiSolape = antiSolape;
            Console.WriteLine("Anti-solape de secciones: " +
                (useAntiSolape ? "ACTIVO" : "APAGADO por --sin-antisolape"));

            // Registro de alturas (planimetría): lo prende el setting
            // setDisplay_isLogElevation y graba SOLO con RTK fijo (calidad 4).
            // `--elevacion-sim` acepta también el simulador (calidad 8) — solo
            // para probar el archivo en banco; la altitud del sim es constante.
            host.ElevacionAceptaSimulador = Array.IndexOf(args, "--elevacion-sim") >= 0;
            if (host.ElevacionAceptaSimulador)
                Console.WriteLine("Registro de alturas: acepta el simulador (--elevacion-sim) — SOLO pruebas");

            host.AutoSteerUpdater.GuiadoImplementoBloqueado = bloquearGuiadoImplemento;
            Console.WriteLine("Guiado del implemento: " +
                (bloquearGuiadoImplemento ? "APAGADO por --sin-guiado-implemento"
                 : host.AutoSteerUpdater.GuiadoImplementoActivo ? "ACTIVO (nivel A)"
                 : "APAGADO (se prende en Menú izquierdo › Dirección › Guiado › Implemento)"));
            if (host.Tool != null)
            {
                string avisoPerfil = CompensacionImplemento.ValidarPerfil(host.Tool.isToolTrailing,
                    host.Tool.hitchLength, host.Tool.trailingHitchLength,
                    host.Tool.tankTrailingHitchLength, host.Tool.isToolTBT);
                if (avisoPerfil != null)
                {
                    Console.WriteLine("Guiado del implemento — revisar perfil: " + avisoPerfil);
                    Log.EventWriter("Guiado del implemento — revisar perfil: " + avisoPerfil);
                }
            }

            host.Start();
            Console.WriteLine("Escuchando PGN en 127.0.0.1:15555, respondiendo a 127.255.255.255:17777.");

            host.StartCommandServer();
            Console.WriteLine("Comandos por TCP en 127.0.0.1:15556 (linea de texto, ej. \"autosteer\").");

            // Monitor de fuente muda: UNA línea en el registro de eventos cuando
            // se calla el GPS / la dirección (PGN 253) / la máquina / la IMU / un
            // nodo MQTT, y otra cuando vuelve con la duración del corte. Lo lee
            // también el reporte de falla (diagnostico/fuentes.txt).
            var fuentesMudas = new AgroParallel.Diagnostico.MonitorFuentesMudas();

            EngineWebHost webHost = null;
            if (useWebHost)
            {
                webHost = new EngineWebHost(host, 5180)
                {
                    FuentesMudas = fuentesMudas,
                    // Salida de emergencia del asistente de calibración de la
                    // dirección: sin él, Dirección › Asistente dice "no disponible".
                    AsistenteDireccionBloqueado = Array.IndexOf(args, "--sin-asistente-direccion") >= 0,
                    // Autoridad de control: APAGADA por defecto (solo registra
                    // quién acciona desde la red). Se exige el día que se
                    // habilite el celular/tablet como segunda pantalla.
                    AutoridadControlExigida = Array.IndexOf(args, "--autoridad-control") >= 0,
                };
                webHost.Start();
                Console.WriteLine("Asistente de calibración de la dirección: " +
                    (webHost.AsistenteDireccionBloqueado ? "APAGADO por --sin-asistente-direccion"
                     : "disponible (Dirección › Asistente; inactivo hasta que se abre)"));
                Console.WriteLine("Autoridad de control: " +
                    (webHost.AutoridadControlExigida
                        ? "EXIGIDA por --autoridad-control (sin control, la red no acciona)"
                        : "solo registro (anota quién acciona desde la red; --autoridad-control para exigirla)"));
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
                    // MQTT no trae identidad: cualquier equipo de la LAN puede
                    // publicar en agp/aog/guidance/command. Para la autoridad
                    // es un remoto que nunca tiene el control: en modo Exigir
                    // sólo pasan lectura y paradas; en SoloRegistro se anota.
                    var puerta = webHost?.Control;
                    if (puerta != null)
                    {
                        bool pilotoOn = host.isBtnAutoSteerOn;
                        var nivel = AgroParallel.Services.Control.ClasificadorComandos.ClasificarComandoGuiado(cmd, pilotoOn);
                        var d = puerta.Autorizar(
                            AgroParallel.Services.Control.ClienteControl.Remoto("mqtt", "MQTT agp/aog/guidance/command", "lan"),
                            nivel, "cmd " + (cmd ?? "").Trim(),
                            AgroParallel.Services.Control.ClasificadorComandos.EsComandoDeDireccion(cmd, pilotoOn));
                        if (!d.Permitido)
                        {
                            Console.WriteLine($"MQTT cmd \"{cmd}\" -> rechazado: {d.Motivo}");
                            return;
                        }
                    }
                    bool ok = host.ExecuteCommand(cmd);
                    Console.WriteLine($"MQTT cmd \"{cmd}\" -> {(ok ? "ok" : "unknown")}");
                });
                Console.WriteLine("Comandos por MQTT en topico agp/aog/guidance/command.");

                // Panel web de CoreX en modo integrado: mismo dashboard y mismo
                // wire que CoreX.exe (:5181). Además aplica la config guardada
                // (puertos serie / NTRIP): sin esto, en integrado nadie abría
                // los puertos al arrancar.
                var panel = new global::AgIO.CoreXEnginePanel(coreX);
                panel.ApplyConfig();
                panel.Start();
                Console.WriteLine("Panel CoreX integrado en http://127.0.0.1:5181 (config: corex-integrado.json).");
            }

            // Después de CoreX: las marcas de máquina/IMU viven ahí. Los nodos
            // se leen del registro del web host (si no hay web host, no hay
            // registro de nodos y se vigila lo demás).
            var vigiaFuentes = new VigiaFuentes(fuentesMudas, host, coreX, () => webHost?.Nodos?.GetAll());
            vigiaFuentes.Start();

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

            // Self-update: cuando ApplyAsync ya lanzó el Updater externo, el
            // Engine tiene que bajarse limpio (webHost/coreX/host.Stop de acá
            // abajo — corte all-off a los relés, guardado, broker). Si nadie
            // se suscribe, el Updater igual lo mata a los 60 s, pero ese kill
            // saltea todo el cierre ordenado.
            AgroParallel.OrbitX.PilotXSelfUpdate.ApplyRequested += () =>
            {
                Log.EventWriter("GuidanceEngine: cierre pedido por self-update (Updater lanzado)");
                exit.Set();
            };

            exit.Wait();

            simTimer?.Dispose();
            vigiaFuentes.Dispose();
            Log.EventWriter("GuidanceEngine: cerrando");
            webHost?.Stop();
            coreX?.Stop();
            host.StopCommandServer();
            host.Stop();
        }
    }
}
