// ============================================================================
// EngineWebHost.cs — levanta el AgpWebHost (netstandard2.0, EmbedIO :5180)
// contra el GuidanceEngineHost en vez de FormGPS. Sirve exactamente los 6
// endpoints /api/aog/{state,coverage,tool,tram,paths,guidance} que PilotX.Desktop
// pollea para renderizar el mapa — o sea, el motor headless queda "detrás" de
// la MISMA API HTTP que hoy sirve FormGPS, sin WinForms/GL.
//
// No usa AgpWebHostBootstrap (net48, WinForms Shell): instancia AgpWebHost
// directo. Todos los servicios que el mapa no necesita van en null — sus
// controllers se registran solo `if (svc != null)`, así que quedan fuera; los
// controllers "siempre-on" se construyen lazy por request y PilotX.Desktop
// nunca los toca. wwwroot = null: PilotX.Desktop es nativo, no carga HTML.
// ============================================================================

using System;
using System.IO;
using AgroParallel.FlowX;
using AgroParallel.Services;
using AgroParallel.WebHost;
using PilotX.GuidanceEngine.Adapters;

namespace AgOpenGPS
{
    public sealed class EngineWebHost
    {
        // Ubica el wwwroot del Hub para servir las páginas HTML (config, colores,
        // gráficos, etc.) que las barras del cockpit abren en el WebView de
        // PilotX.Desktop. Prueba el Build empaquetado y el WebUI del source.
        private static string ResolveWwwroot()
        {
            var baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                // INSTALADO: el motor vive en <install>\Engine\ y el wwwroot lo
                // deja el paquete en <install>\AgroParallel\wwwroot (hermano de
                // Engine\). Va PRIMERO porque es el layout de la cabina; sin esto
                // las pantallas HTML del Hub daban 404 al abrirlas desde
                // PilotX.Desktop (Dirección, config, gráficos…).
                Path.GetFullPath(Path.Combine(baseDir, "..", "AgroParallel", "wwwroot")),
                // DESARROLLO: corriendo desde SourceCode\...\bin\<cfg>\net9.0.
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "AgroParallel", "Web", "AgroParallel.WebUI", "wwwroot")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "Build", "AgroParallel", "wwwroot")),
                Path.GetFullPath(Path.Combine(baseDir, "wwwroot")),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c))
                {
                    Console.WriteLine("wwwroot: " + c);
                    return c;
                }

            // Sin wwwroot la API sigue andando pero TODA página del Hub da 404 —
            // avisarlo fuerte, que es exactamente el síntoma que se ve en cabina.
            Console.Error.WriteLine("wwwroot NO ENCONTRADO — las páginas del Hub van a dar 404. Buscado en:");
            foreach (var c in candidates) Console.Error.WriteLine("  " + c);
            return null;
        }

        private readonly GuidanceEngineHost _host;
        private readonly int _port;
        private readonly string _brokerHost;
        private readonly int _brokerPort;
        private AgpWebHost _web;
        private NodoRegistryService _nodos;
        private FlowXBridge _flowxBridge;
        private System.Threading.Timer _flowxRetry;
        private AgroParallel.OrbitX.OrbitXSync _orbitxSync;
        private AgroParallel.Soporte.SoporteRemotoService _soporte;
        private AgroParallel.Soporte.ChatSoporteService _chat;
        private AgroParallel.Soporte.ReporteFallaService _reporteFalla;
        private System.Threading.Timer _orbitxRetry;
        private AgroParallel.Services.SonidosAlarmService _sonidos;
        private AgroParallel.QuantiX.QuantiXMotorBridge _quantixBridge;
        private System.Threading.Timer _quantixRetry;
        private AgroParallel.Cut.CutDispatcher _cutDispatcher;
        private AgroParallel.SectionX.SectionsSpeedPublisher _sectionsSpeed;
        private System.Threading.Timer _cutRetry;
        private EnginePilotXUpdateService _pilotxUpdate;
        private AgroParallel.Services.Tareas.TareasService _tareas;
        private System.Threading.Timer _tareasTick;
        private AgroParallel.Services.VistaX.VistaXRegistroLote _vxRegistro;

        /// <summary>Registro de nodos MQTT compartido: lo usan los bridges que
        /// publican targets (QuantiX/SectionX) en vez de abrir otra conexión.</summary>
        public NodoRegistryService Nodos => _nodos;

        /// <summary>Monitor de fuente muda (lo crea Program y lo alimenta
        /// VigiaFuentes). Acá solo se lee, para el reporte de falla.</summary>
        public AgroParallel.Diagnostico.MonitorFuentesMudas FuentesMudas { get; set; }

        public EngineWebHost(GuidanceEngineHost host, int port = 5180,
            string brokerHost = "127.0.0.1", int brokerPort = 1883)
        {
            _host = host;
            _port = port;
            _brokerHost = brokerHost;
            _brokerPort = brokerPort;
        }

        public string Url => _web?.Url;

        /// <summary>`--sin-asistente-direccion`: no se conecta el asistente de
        /// calibración (ni endpoints ni enganche al PGN 254). Antes de Start().</summary>
        public bool AsistenteDireccionBloqueado { get; set; }

        /// <summary>`--autoridad-control`: el accionamiento desde la red de quien
        /// no tiene el control se RECHAZA (423). Sin el flag (default) sólo se
        /// anota quién acciona: la PWA del celular ya acciona hoy (QuantiX,
        /// VistaX, FlowX) y no se la rompe. Antes de Start().</summary>
        public bool AutoridadControlExigida { get; set; }

        /// <summary>Puerta de la autoridad de control (null antes de Start()). La
        /// usa Program para filtrar los comandos que llegan por MQTT.</summary>
        public AgroParallel.Services.Control.PuertaControl Control => _web?.Control;

        public void Start()
        {
            if (_web != null) return;

            // Quién sabe el lote abierto: PrescripcionService lo usa para atar
            // la activa al lote al activarla y para NO dibujar/dosificar la
            // prescripción de otro lote (reporte 2026-08-10, lote nuevo con el
            // shape de "Las de atras").
            AgroParallel.Services.PrescripcionService.LoteActualProvider =
                () => _host.IsJobStarted ? _host.currentFieldDirectory : "";

            var state = new EngineStateProvider(_host);
            // Prescripción (.shp): upload + carga automática + dosis por
            // posición. El state y el controller comparten LA MISMA capa —
            // dos instancias significarían que el mapa dibuja un shape y
            // QuantiX dosifica con otro.
            var shape = new EngineShapeService(_host);
            state.Shape = shape;
            var coverage = new EngineCoverageService(_host);
            var guidance = new EngineGuidanceCalculator(_host);
            var toolGeom = new EngineToolGeometryCalculator(_host);
            var tram = new EngineTramCalculator(_host);
            var paths = new EnginePathsCalculator(_host);
            var lotes = new EngineLotesService(_host);
            var trackBuilder = new EngineTrackBuilderService(_host);
            var trackList = new EngineTrackListService(_host);
            var sectionsCore = new EngineSectionControlService(_host);
            // Config de vehículo/herramienta/IMU: es lo que hace andar la pantalla
            // de Configuración. Al guardar la herramienta recalcula la geometría
            // de secciones, si no la huella queda con el reparto viejo.
            var vehicleTool = new EngineVehicleToolService(_host);
            // Pantalla de Configuración (config.html) y calibración de IMU:
            // ambas daban 404 contra el motor.
            var configVehiculo = new EngineConfigVehiculoService(_host);
            var imuCalibracion = new EngineImuCalibracionService(_host);
            // Config de dirección: implementación compartida con FormGPS (archivo
            // linkeado). El engine no tiene hilo de UI, así que SendSettings va
            // directo; el ángulo vivo del WAS sale del CModuleComm del host.
            // La velocidad es para el manejo libre: sin ella el servicio falla
            // cerrado y no deja prenderlo (mover el volante sin guía con el
            // tractor andando no puede depender de "no sé a qué velocidad va").
            var steerConfig = new AgroParallel.Adapters.SteerConfigService(
                _host.Vehicle,
                () => _host.Mc.actualSteerAngleDegrees,
                () => _host.SettingsSender.SendSettings(),
                applyLive: null,
                avgSpeed: () => _host.avgSpeed);

            // Asistente de calibración de la dirección (Dirección › Asistente).
            // Arranca INACTIVO: hasta que el operario lo abre, el PGN 254 sale
            // como siempre. `--sin-asistente-direccion` ni siquiera lo conecta.
            EngineSteerCalService steerCal = null;
            if (!AsistenteDireccionBloqueado)
            {
                steerCal = new EngineSteerCalService(_host, steerConfig);
                _host.AutoSteerUpdater.AsistenteDireccion = steerCal;
            }

            // Cero automático del WAS (Dirección › Sensor): apagado de fábrica
            // (setAS_ceroWasAuto). Prendido mide y PROPONE; el offset cambia
            // solo cuando el operario toca Aplicar, con el piloto suelto.
            var asistenteRef = steerCal;
            var ceroWas = new EngineCeroWasService(_host, steerConfig,
                asistenteEnCurso: () => asistenteRef != null && asistenteRef.EnCurso);

            // ── Productos X-* ────────────────────────────────────────────────
            // Sin esto el motor headless servía el mapa pero NADA de QuantiX,
            // VistaX, FlowX ni nodos: contra PilotX.Desktop esas pantallas daban
            // 404 y el operario veía paneles vacíos. Mismo bloque que arma el
            // host WinForms (AgpWebHostBootstrap) y el head Android.
            _nodos = new NodoRegistryService();
            try { _nodos.Start(_brokerHost, _brokerPort); }
            catch (Exception ex)
            {
                // El registro de nodos es por MQTT: si el broker no está, los
                // paneles quedan sin nodos pero el guiado tiene que seguir.
                Console.Error.WriteLine("[Engine] NodoRegistry: " + ex.Message);
            }

            var vistaxCfg = new VistaXConfigService();
            var insumosCat = new InsumoCatalogService();
            var sectionxCfg = new SectionXConfigService();
            var orbitxCfg = new OrbitXConfigService();
            var quantixCfg = new QuantiXConfigService(_nodos);
            // UNA sola instancia de implemento compartida: si el live de VistaX
            // arma la suya, el overlay muestra geometría vieja hasta reiniciar.
            var implemento = new ImplementoService(vistaxCfg, vehicleTool, quantixCfg, sectionxCfg);
            // Trenes en el mapa: el calculator desplaza las secciones del tren
            // trasero a su posición física real (barra de hace N metros).
            toolGeom.ImplementoProvider = () => implemento.GetImplemento();
            var vistaxLive = new VistaXLiveService(_nodos, vistaxCfg, insumosCat, state, sectionsCore, implemento, quantixCfg);
            // cargarImplemento: misma instancia que usa el bridge — el widget
            // calcula ancho/surcos por motor con los MISMOS datos que el motor.
            var quantixRuntime = new QuantiXRuntimeService(state,
                cargarImplemento: () => implemento.GetImplemento());
            var flowxCfg = new FlowXConfigService();
            var flowxLive = new FlowXLiveService(_nodos, flowxCfg);
            var stormxCfg = new StormXConfigService();
            var stormxLive = new StormXLiveService(_nodos, stormxCfg);
            var linexCfg = new LineXConfigService();
            var linexLive = new LineXLiveService(_nodos, linexCfg);

            // Brillo/apagado por OS: Windows = DDC/CI (dxva2) + WMI; Linux =
            // sysfs backlight + brightnessctl + ddcutil. Sin esto
            // api/sistema/brillo devolvía siempre ok:false,value:-1.
#pragma warning disable CA1416 // la rama Windows solo se instancia detrás del guard
            AgroParallel.Services.Abstractions.ISistemaService sistema = OperatingSystem.IsWindows()
                ? new EngineSistemaService()
                : new EngineSistemaServiceLinux();
#pragma warning restore CA1416

            // WiFi propio (página wifi.html del Hub): netsh en Windows, nmcli
            // en Linux — el operario nunca ve el panel de red del SO.
            AgroParallel.Services.Abstractions.IWifiService wifi = OperatingSystem.IsWindows()
                ? new AgroParallel.Services.WifiServiceWindows()
                : (AgroParallel.Services.Abstractions.IWifiService)new AgroParallel.Services.WifiServiceLinux();
            _web = new AgpWebHost(
                state,                 // requerido
                sistema: sistema,
                wifi: wifi,
                nodos: _nodos,
                orbitxCfg: orbitxCfg,
                sectionxCfg: sectionxCfg,
                camarasCfg: new CamarasConfigService(),
                quantixCfg: quantixCfg,
                vistaxCfg: vistaxCfg,
                vistaxLive: vistaxLive,
                debug: new DebugLogService(),
                lotes: lotes,
                // Sin esto PerfilesController no se registra y /api/aog/perfiles
                // da 404: la pantalla de perfiles del Hub no lista nada.
                perfiles: new EnginePerfilService(_host),
                // Sin esto FlagsController no se registra y /api/flags da 404:
                // la pantalla de banderas no funciona en el stack Avalonia.
                flags: new EngineFlagsService(_host),
                // Sin esto ContornoController no se registra y /api/contorno da
                // 404: la pantalla de contorno decía "Sin conexión con PilotX" y
                // no se podía hacer el lindero manejando.
                contorno: new EngineContornoService(_host),
                // Sin esto HeadlandController no se registra y /api/headland da
                // 404: la pantalla Cabecera no puede construir nada, y sin
                // cabecera no hay corte automático de secciones en el borde.
                headlandEdit: new EngineHeadlandEditService(_host),
                // Cabecera por líneas (el hermano complicado de la anterior):
                // marca líneas A/B sobre el borde y arma la cabecera con los
                // cruces. Es lo que sirve en lotes que no son un rectángulo.
                cabeceraLineas: new EngineCabeceraLineasService(_host),
                // Tramlines: las huellas por donde pasa el pulverizador
                // después. Sin esto /api/tramlines daba 404 y quedaban 5 íconos
                // muertos en el tablero de cierre.
                tramLine: new EngineTramLineService(_host),
                // AB rápido: crear una guía manejando (curva, AB o A+), sin
                // pasar por la pantalla de guías.
                quickAb: new EngineQuickAbService(_host),
                // Panel simple de tram (el del menú de config): genera las
                // huellas desde la guía activa con pasadas configurables.
                tramSimple: new EngineTramSimpleService(_host),
                // Mover guía: correr la activa de a pasos o la referencia (que
                // corre el patrón entero). Destraba 13 íconos del tablero.
                nudge: new EngineNudgeService(_host),
                // Caminos grabados (.rec): listar, cargar, borrar y nombrar.
                recPath: new EngineRecPathService(_host),
                vehicleTool: vehicleTool,
                shapefile: shape,
                coverage: coverage,
                sectionsCore: sectionsCore,
                quantixRuntime: quantixRuntime,
                guidance: guidance,
                // Self-update de PilotX vía OrbitX (página /actualizar del
                // Hub). Sin esto PilotXUpdateController no se registraba y
                // /api/pilotx/update/* daba 404: el update estaba escrito pero
                // desconectado desde que el WinForms se eliminó (2026-08-14).
                pilotxUpdate: (_pilotxUpdate = new EnginePilotXUpdateService()),
                flowxCfg: flowxCfg,
                flowxLive: flowxLive,
                stormxCfg: stormxCfg,
                stormxLive: stormxLive,
                linexCfg: linexCfg,
                linexLive: linexLive,
                wwwroot: ResolveWwwroot(),
                port: _port,
                toolGeometry: toolGeom,
                tram: tram,
                implemento: implemento,
                paths: paths,
                trackBuilder: trackBuilder,
                trackList: trackList,
                steerConfig: steerConfig,
                configVehiculo: configVehiculo,
                imuCalibracion: imuCalibracion);
            _web.SteerCal = steerCal;
            _web.CeroWas = ceroWas;

            // Autoridad de control: quién acciona desde la red va al registro de
            // eventos (lo ve soporte); el desenganche por pérdida de control usa
            // el mismo cartel que los otros desenganches automáticos.
            _web.ModoControl = AutoridadControlExigida
                ? AgroParallel.Services.Control.ModoAutoridad.Exigir
                : AgroParallel.Services.Control.ModoAutoridad.SoloRegistro;
            _web.LogControl = msg =>
            {
                Console.WriteLine(msg);
                try { AgLibrary.Logging.Log.EventWriter(msg); } catch { }
            };
            _web.DesengancharPiloto = motivo => _host.DesengancharPilotoPorControl(motivo);

            // Alarmas sonoras de cabina: detecta piloto/dosis/motor/tubo/tolva
            // y publica disparos; los clientes (Desktop, pantalla Sonidos)
            // consultan /api/sonidos/estado y suenan ellos.
            try
            {
                _sonidos = new AgroParallel.Services.SonidosAlarmService(state, _nodos, vistaxLive);
                _sonidos.Start();
                _web.Sonidos = _sonidos;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] SonidosAlarm: " + ex.Message);
            }

            // Tareas de trabajo: la tarea del lote abierto (Tareas.json en la
            // carpeta del lote). El área es la MISMA que ve el operario en el
            // HA de la barra de arriba (WorkedAreaTotalM2); la cobertura y el
            // plano local salen del lote abierto para el export a SHP.
            try
            {
                _tareas = new AgroParallel.Services.Tareas.TareasService(
                    loteDir: () => lotes.GetCurrentFieldDirectory(),
                    areaTrabajadaM2: () => _host.Fd.workedAreaTotal,
                    insumoActivo: () => insumosCat.GetActivo(),
                    cobertura: () => coverage.GetSnapshot(),
                    aLatLon: (e, n) =>
                    {
                        var w = _host.AppModelField.LocalPlane.ConvertGeoCoordToWgs84(
                            new AgOpenGPS.Core.Models.GeoCoord(n, e));
                        return new[] { w.Latitude, w.Longitude };
                    });
                // Cerrar el lote con la tarea en curso la deja en pausa (el
                // lote sigue abierto en el momento del evento).
                _host.AntesDeCerrarLote += _tareas.AntesDeCerrarLote;
                // Vigila el área cada 5 s: si borran el pintado con la tarea
                // activa, lo trabajado hasta ahí no se pierde.
                _tareasTick = new System.Threading.Timer(_ => _tareas?.Tick(), null, 5000, 5000);
                _web.Tareas = _tareas;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] Tareas: " + ex.Message);
            }

            _web.Start();

            // Registro VistaX por lote: tramos de ~10 m con sem/m, dobles,
            // fallas y CV por surco en <lote>/VistaX/Surcos/ (lo sube
            // OrbitXSync y lo usa el informe de la Tarea). Sin VistaX
            // sembrando no escribe nada.
            try
            {
                _vxRegistro = new AgroParallel.Services.VistaX.VistaXRegistroLote(state, vistaxLive, vistaxLive);
                _vxRegistro.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] Registro VistaX: " + ex.Message);
            }

            // FlowX comanda la válvula de dosificación líquida: va atado al
            // ciclo de vida del host. Si flowX.json está vacío o deshabilitado,
            // sale en silencio.
            try
            {
                _flowxBridge = new FlowXBridge(state, FlowXConfig.Load());
                _ = _flowxBridge.StartAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] FlowXBridge: " + ex.Message);
            }

            // VIGILANTE cada 15 s (primer tick a los 2 s), mismo patrón que
            // _quantixRetry y _cutRetry. FlowXBridge.StartAsync() conecta al
            // broker UNA vez y, si falla, loguea y queda mudo para siempre: no
            // reintenta ni nadie lo miraba. El broker (CoreX) levanta en el
            // mismo arranque que este host, así que la carrera es real y el
            // síntoma es mudo: el nodo aparece ONLINE (habla solo con el
            // broker) pero nunca recibe target, y su firmware cierra TODAS las
            // secciones a los 4 s por comms-loss. El operario ve las secciones
            // abiertas en PilotX y las válvulas cerradas en el nodo, sin un
            // solo error a la vista (caso de campo 2026-09-05, con las 5
            // secciones forzadas en manual y el mapeo de cortes correcto).
            // Cubre además el alta del nodo DESPUÉS de arrancar el motor:
            // StartAsync sale solo con "Deshabilitado o sin nodos" y así queda.
            _flowxRetry = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (_flowxBridge != null && _flowxBridge.IsRunning) return;

                    // Instancia nueva con la config recién leída: el nodo pudo
                    // darse de alta o habilitarse después del arranque.
                    try { _flowxBridge?.Stop(); } catch { }
                    _flowxBridge = new FlowXBridge(state, FlowXConfig.Load());
                    _flowxBridge.StartAsync().GetAwaiter().GetResult();
                    if (_flowxBridge.IsRunning)
                        Console.WriteLine("[Engine] FlowXBridge (re)arrancado por el vigilante.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[Engine] FlowXBridge retry: " + ex.Message);
                }
            }, null, 2000, 15000);

            // OrbitXConfigService (arriba) solo lee/escribe orbitX.json y prueba
            // /health una vez por click de "Probar conexión" — el heartbeat de
            // verdad (sync periódico + auto-registro + firmware mirror :8088) lo
            // hace ESTA clase (OrbitXSync), que FormGPS instancia en su Load()
            // pero acá nunca se creaba. Con esto el motor headless nunca latía:
            // el dispositivo quedaba vinculado (token/estab_slug ya en
            // orbitX.json de una sesión FormGPS anterior) pero invisible en el
            // dashboard cloud porque nadie mandaba el heartbeat. Mismo patrón
            // que FlowXBridge: state ya es el IAogStateProvider que necesita.
            try
            {
                _orbitxSync = new AgroParallel.OrbitX.OrbitXSync(
                    state, AgroParallel.OrbitX.OrbitXConfig.Load());
                // Lotes del cloud: el KML se importa con los writers del motor
                // (crear/actualizar el lindero SIN abrir el lote).
                _orbitxSync.ImportarLoteDesdeKml = lotes.CrearLoteDesdeKmlSinAbrir;
                lotes.AlBorrarLote = nombre => _orbitxSync.LotesBorrados.Encolar(nombre);
                // Nodos (uid, tipo, firmware, online) para que OrbitX los muestre en Dispositivos.
                _orbitxSync.NodosProvider = () => _nodos?.GetAll();
                // Telemetría por motor de QuantiX dentro del punto de tracking:
                // sin esto el cloud ve por dónde anduvo el tractor pero no qué
                // dosificó cada surco, que es la pregunta que abrió Las Gringas.
                _orbitxSync.NodosLiveProvider = () => _nodos?.GetAll();
                _orbitxSync.QuantiXConfigProvider = () => quantixCfg.GetMotores();
                _orbitxSync.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] OrbitXSync: " + ex.Message);
            }

            // Canal de diagnostico remoto. Pregunta cada 20 s si hay algo
            // pendiente y devuelve el texto; no abre ningun puerto ni acepta
            // conexiones entrantes. Sin identidad de dispositivo no hace nada,
            // asi que en una pantalla no vinculada simplemente duerme.
            //
            // Existe porque el 2026-09-05 una pantalla quedo sin arrancar en
            // plena campana y la unica forma de ver que pasaba era dictarle
            // comandos por telefono a quien estuviera adelante. Tres horas
            // para preguntas que se contestan en dos minutos.
            try
            {
                _soporte = new AgroParallel.Soporte.SoporteRemotoService(
                    () => AgroParallel.OrbitX.OrbitXConfig.Load(),
                    m => Console.WriteLine("[Engine] " + m));
                _soporte.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] SoporteRemoto: " + ex.Message);
            }

            // Chat de soporte (capa 1): mismo transporte pull que el canal de
            // diagnóstico, pero para una conversación humano↔pantalla. Vive acá
            // (con la config del cloud) y la UI lo consume por /api/chat/* — por
            // eso se lo pasamos al host web. Sin identidad de dispositivo duerme.
            try
            {
                _chat = new AgroParallel.Soporte.ChatSoporteService(
                    () => AgroParallel.OrbitX.OrbitXConfig.Load(),
                    m => Console.WriteLine("[Engine] " + m));
                _chat.Start();
                if (_web != null) _web.Chat = _chat;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] ChatSoporte: " + ex.Message);
            }

            // "Reportar falla" en un toque (SISTEMA › Reportar falla): la
            // pantalla manda captura + descripción + sus logs; acá se suma lo
            // del motor (logs, config sanitizada, perfil, lote, fuentes mudas),
            // se arma el ZIP, queda en cola en disco y se sube a OrbitX cuando
            // haya red. Sin vinculación queda en cola (y se puede sacar por
            // pendrive). Existe porque el soporte remoto era lento: pantallas
            // que fallaban y nadie podía leer los logs.
            try
            {
                var colaReportes = new AgroParallel.Soporte.ColaReportesFalla(
                    Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, "data", "reportes_falla"));
                _reporteFalla = new AgroParallel.Soporte.ReporteFallaService(
                    colaReportes,
                    () => AgroParallel.OrbitX.OrbitXConfig.Load(),
                    () => ReporteFallaEngine.Contexto(_host, FuentesMudas, () => _nodos?.GetAll()),
                    m => Console.WriteLine("[Engine] " + m));
                _reporteFalla.Start();
                if (_web != null) _web.ReporteFalla = _reporteFalla;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] ReporteFalla: " + ex.Message);
            }

            // Bridge de motores QuantiX: el que PUBLICA los targets de dosis a
            // los nodos por MQTT. En FormGPS lo instancia el Load() del form —
            // acá no lo arrancaba nadie: el nodo conectaba, mandaba telemetría
            // y esperaba órdenes que nunca llegaban ("veo el nodo pero no
            // gira"). Vigilante cada 30 s (primer tick al toque): arranca el
            // bridge en cuanto haya nodos configurados (el auto-registro por
            // announcement puede llegar DESPUÉS del arranque del motor).
            _quantixRetry = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (_quantixBridge != null && _quantixBridge.IsRunning) return;
                    if (AgroParallel.QuantiX.MotoresConfig.Load().Nodos.Count == 0) return;

                    _quantixBridge = new AgroParallel.QuantiX.QuantiXMotorBridge(state, _nodos, new PrescripcionService());
                    // Tren del motor derivado del implemento central (Task 5),
                    // con fallback al campo manual si no hay dato derivable.
                    _quantixBridge.ImplementoProvider = () => implemento.GetImplemento();
                    // Puente bridge → guiado: los surcos de los motores que el
                    // operario apagó a mano no se pintan (si no dosifica, no se
                    // sembró). Se conecta acá porque el exe es el único que ve
                    // los dos assemblies; sin esto la máscara queda en 0 y el
                    // pintado se comporta como siempre.
                    _quantixBridge.OnSeccionesApagadas =
                        mask => { if (_host != null) _host.SeccionesApagadasExternas = mask; };
                    _ = _quantixBridge.StartAsync();
                    Console.WriteLine("[Engine] QuantiXMotorBridge arrancado: hay nodos configurados.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[Engine] QuantiX bridge: " + ex.Message);
                }
            }, null, 2000, 30000);

            // Despachador de corte unificado (SectionX relays + LineX servo) +
            // publisher de velocidad por sección (agp/aog/sections_speed @5Hz,
            // lo consumen QuantiX/FlowX/VistaX). En FormGPS los instanciaba el
            // Load() del form — acá no los arrancaba NADIE desde que el stack
            // WinForms se eliminó (2026-08-14): CutDispatcher.Current quedaba
            // null, /api/sectionx/status devolvía connected=false y la UI
            // mostraba "broker caído" con el broker vivo, y los relés de corte
            // nunca recibían órdenes. El dispatcher arranca SIEMPRE (sin gate
            // enabled/nodos): cada adapter se auto-filtra por su config.
            try
            {
                var sxAdapter = new AgroParallel.Cut.SectionXCutAdapter
                {
                    // Tren derivado del implemento central, con fallback al
                    // campo manual por nodo si no hay dato derivable.
                    ImplementoProvider = () => implemento.GetImplemento()
                };
                _cutDispatcher = new AgroParallel.Cut.CutDispatcher(
                    state,
                    new AgroParallel.Cut.ICutAdapter[]
                    {
                        sxAdapter,
                        new AgroParallel.Cut.LineXCutAdapter()
                    });
                _sectionsSpeed = new AgroParallel.SectionX.SectionsSpeedPublisher(state);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] CutDispatcher: " + ex.Message);
            }

            // Vigilante cada 15 s (primer tick al segundo): StartAsync sale en
            // silencio si el broker todavía no levantó y NO reintenta solo, así
            // que acá se insiste hasta conectar; y si el broker se cae después
            // de conectar (DisconnectedAsync baja MqttConnected), se baja limpio
            // y se rearranca cuando vuelva. Mismo patrón que _quantixRetry.
            _cutRetry = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (_cutDispatcher != null)
                    {
                        if (_cutDispatcher.IsRunning && !_cutDispatcher.MqttConnected)
                            _cutDispatcher.Stop();
                        if (!_cutDispatcher.IsRunning)
                            _ = _cutDispatcher.StartAsync();
                    }
                    if (_sectionsSpeed != null && !_sectionsSpeed.IsRunning)
                        _ = _sectionsSpeed.StartAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[Engine] CutDispatcher retry: " + ex.Message);
                }
            }, null, 1000, 15000);

            // Vigilante de la vinculación: si el sync no corre (arrancó con
            // enabled=false o sin token — el caso REAL: el motor arranca sin
            // vincular y el operario vincula DESPUÉS desde la pantalla OrbitX,
            // que solo escribe orbitX.json), recargar la config cada 30 s y
            // arrancarlo apenas esté habilitada. Sin esto la vinculación no
            // hacía nada hasta reiniciar el motor: heartbeat muerto y las
            // prescripciones del cloud sin bajar, con todo "conectado".
            _orbitxRetry = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (_orbitxSync != null && _orbitxSync.IsRunning) return;
                    var cfg = AgroParallel.OrbitX.OrbitXConfig.Load();
                    if (!cfg.Enabled || string.IsNullOrEmpty(cfg.DeviceToken)) return;

                    try { _orbitxSync?.Dispose(); } catch { }
                    _orbitxSync = new AgroParallel.OrbitX.OrbitXSync(state, cfg);
                    _orbitxSync.ImportarLoteDesdeKml = lotes.CrearLoteDesdeKmlSinAbrir;
                    lotes.AlBorrarLote = nombre => _orbitxSync.LotesBorrados.Encolar(nombre);
                    // Nodos (uid, tipo, firmware, online) para que OrbitX los muestre en Dispositivos.
                    _orbitxSync.NodosProvider = () => _nodos?.GetAll();
                    // Igual que arriba: el sync que rearranca el vigilante
                    // tiene que quedar con los MISMOS providers, si no la
                    // telemetría de motores desaparece justo después de que el
                    // operario vincula la pantalla.
                    _orbitxSync.NodosLiveProvider = () => _nodos?.GetAll();
                    _orbitxSync.QuantiXConfigProvider = () => quantixCfg.GetMotores();
                    _orbitxSync.Start();
                    Console.WriteLine("[Engine] OrbitXSync (re)arrancado: la vinculación apareció en orbitX.json.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[Engine] OrbitXSync retry: " + ex.Message);
                }
            }, null, 30000, 30000);
        }

        public void Stop()
        {
            try { _tareasTick?.Dispose(); } catch { }
            _tareasTick = null;
            if (_tareas != null) { try { _host.AntesDeCerrarLote -= _tareas.AntesDeCerrarLote; } catch { } }
            _tareas = null;
            try { _flowxRetry?.Dispose(); } catch { }
            _flowxRetry = null;
            try { _soporte?.Dispose(); } catch { }
            _soporte = null;
            try { _chat?.Dispose(); } catch { }
            _chat = null;
            try { _reporteFalla?.Dispose(); } catch { }
            _reporteFalla = null;
            try { _flowxBridge?.Stop(); _flowxBridge?.Dispose(); } catch { }
            _flowxBridge = null;
            // Antes de _web?.Stop(): orbitX.json no se puede escribir mientras
            // el guardado del lote está en curso (mismo motivo que FormGPS.cs
            // para su propio orbitXSync.Stop() en el shutdown).
            try { _quantixRetry?.Dispose(); } catch { }
            _quantixRetry = null;
            try { _cutRetry?.Dispose(); } catch { }
            _cutRetry = null;
            // Dispose llama Stop(), que manda el all-off a los relés antes de
            // soltar el MQTT: las secciones no quedan abiertas al apagar.
            try { _cutDispatcher?.Dispose(); } catch { }
            _cutDispatcher = null;
            try { _sectionsSpeed?.Dispose(); } catch { }
            _sectionsSpeed = null;
            try { _quantixBridge?.Stop(); } catch { }
            _quantixBridge = null;
            try { _orbitxRetry?.Dispose(); } catch { }
            _orbitxRetry = null;
            try { _orbitxSync?.Dispose(); } catch { }
            _orbitxSync = null;
            // Antes de _web?.Stop() (que para el live de VistaX): cierra el
            // tramo en curso, guarda resumen.json y regenera el SHP del lote.
            try { _vxRegistro?.Dispose(); } catch { }
            _vxRegistro = null;
            try { _web?.Stop(); } catch { }
            _web = null;
            try { _pilotxUpdate?.Dispose(); } catch { }
            _pilotxUpdate = null;
            try { _nodos?.Dispose(); } catch { }
            _nodos = null;
        }
    }
}
