// ============================================================================
// GuidanceEngineHost.cs — host headless para el loop de guiado (bloque 14,
// "proceso único"). Reemplaza a FormGPS: implementa las mismas interfaces
// I*Host que hoy implementa FormGPS vía partials (GPS/AgroParallel/Common/
// FormGps.*Host.cs), pero sin WinForms ni OpenGL. Orquesta los mismos
// CPositionUpdater/CHeadingUpdater/CAutoSteerUpdater/CYouTurnUpdater/
// CSectionCalculator/CSettingsSender ya extraídos a Core (bloque 9).
//
// Protocolo de red: idéntico al de FormGPS (UDPComm.Designer.cs) — escucha
// PGN en 127.0.0.1:15555 y contesta a CoreX en 127.255.255.255:17777, mismo
// CRC. Así corre contra un CoreX/ModSim real sin tocarlos.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using AgLibrary.Logging;
using AgOpenGPS.Core;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        // ---- modelo de guiado (mismos objetos que hoy vive en FormGPS) ----
        public readonly ApplicationModel AppModelField;
        public readonly CNMEA Pn;
        public readonly CAHRS Ahrs = new CAHRS();
        public readonly CSim Sim;
        public readonly WorldGrid WorldGridField = new WorldGrid();
        // No readonly: GuidanceEngineVehicleToolService (PilotX.Android) las
        // reconstruye al guardar config, mismo patron que FormGPS.vehicle/tool.
        public CVehicle Vehicle;
        public readonly CBoundary Bnd;
        public readonly CTrack Trk;
        public readonly CABLine ABLineField;
        public readonly CABCurve CurveField;
        public CTool Tool;
        public readonly CModuleComm Mc;

        /// <summary>
        /// Secciones que un producto externo pide mantener APAGADAS, en bits
        /// (bit 0 = sección 1). Hoy la escribe el bridge de QuantiX con los
        /// surcos de los motores que el operario apagó a mano desde el overlay:
        /// si ese motor no dosifica, esa parte NO se sembró y no tiene que
        /// quedar pintada como trabajada — si no, la pasada siguiente la
        /// saltea por anti-solape y el surco queda sin sembrar de verdad.
        ///
        /// Canal deliberadamente chico y de un solo sentido: el bridge vive en
        /// AgroParallel.Services y el guiado en AgOpenGPS.Core, que no se
        /// referencian entre sí; el ejecutable los conecta al arrancar. En 0
        /// (nadie la escribe, bridge apagado) el comportamiento es el de
        /// siempre: no apaga nada.
        /// </summary>
        public volatile uint SeccionesApagadasExternas;
        public readonly CYouTurn Yt;
        public readonly CContour Ct;
        public readonly CRecordedPath RecPath;
        public readonly CFieldData Fd;
        public readonly CISOBUS Isobus;
        public readonly CTram Tram;
        public readonly CGuidance Gyd;
        public readonly CSection[] Sections = new CSection[MaxSectionsConst];
        public readonly List<CPatches> TriStripField = new List<CPatches>();

        public const int MaxSectionsConst = 64;

        // PGN de salida (mismos byte[] fijos que FormGPS).
        public readonly CPGN_FE P254Field = new CPGN_FE();
        public readonly CPGN_EF P239Field = new CPGN_EF();
        public readonly CPGN_E5 P229Field = new CPGN_E5();
        public readonly CPGN_FC P252Field = new CPGN_FC();
        public readonly CPGN_FB P251Field = new CPGN_FB();
        public readonly CPGN_EE P238Field = new CPGN_EE();
        public readonly CPGN_EC P236Field = new CPGN_EC();
        public readonly CPGN_EB P235Field = new CPGN_EB();

        // ---- orquestadores (bloque 9, ya en Core) ----
        public readonly CPositionUpdater PositionUpdater;
        public readonly CHeadingUpdater HeadingUpdater;
        public readonly CAutoSteerUpdater AutoSteerUpdater;
        public readonly CYouTurnUpdater YouTurnUpdater;
        public readonly CSectionCalculator SectionCalculator;
        public readonly CSettingsSender SettingsSender;
        public readonly PgnReceiver PgnReceiverField;

        // ---- red (mismo protocolo UDP loopback que FormGPS) ----
        private Socket _loopBackSocket;
        // Windows: broadcast de loopback (127.255.255.255) — lo escuchan CoreX
        // Y cualquier relay del banco a la vez, semántica histórica del wire.
        // Linux: ese broadcast NO se entrega a sockets ligados a 127.0.0.1
        // (medido con tcpdump en WSL 2026-08-15: los PGN de respuesta salían y
        // nadie los recibía) — va unicast al mismo puerto.
        private EndPoint _epAgIO = new IPEndPoint(
            OperatingSystem.IsWindows() ? IPAddress.Parse("127.255.255.255") : IPAddress.Loopback,
            17777);
        private EndPoint _endPointLoopBack = new IPEndPoint(IPAddress.Loopback, 0);
        private byte[] _loopBuffer = new byte[1024];
        private bool _running;

        public GuidanceEngineHost(DirectoryInfo baseDirectory)
        {
            AppModelField = new ApplicationModel(baseDirectory);

            Pn = new CNMEA(this);
            Sim = new CSim(this);
            Vehicle = new CVehicle(this);
            Bnd = new CBoundary(this);
            Trk = new CTrack(this);
            ABLineField = new CABLine(this);
            CurveField = new CABCurve(this);
            Tool = new CTool(this);
            Mc = new CModuleComm(this);
            // Delegates del switch de trabajo/dirección remoto (PGN 253 byte 11).
            // En FormGPS los cablean los botones (FormGPS.cs:565); acá van a los
            // mismos handlers de comando. Sin esto, bajar la herramienta con el
            // toggle del Hub prendido no prendía las secciones — el bit llegaba
            // (PgnReceiver → workSwitchHigh) pero nadie lo convertía en acción
            // (reporte de banco 2026-08-12, BenchX + firmware AiO).
            Mc.ToggleSectionMasterManual = SectionMasterManual;
            Mc.ToggleSectionMasterAuto = SectionMasterAuto;
            Mc.ToggleAutoSteer = () => ((IAutoSteerHost)this).PerformAutoSteerClick();
            Yt = new CYouTurn(this);
            Ct = new CContour(this);
            RecPath = new CRecordedPath(this);
            Fd = new CFieldData(this);
            Isobus = new CISOBUS(this);
            Tram = new CTram(this);
            Gyd = new CGuidance(this);

            for (int i = 0; i < Sections.Length; i++) Sections[i] = new CSection();

            PositionUpdater = new CPositionUpdater(this);
            HeadingUpdater = new CHeadingUpdater(this);
            AutoSteerUpdater = new CAutoSteerUpdater(this);
            YouTurnUpdater = new CYouTurnUpdater(this);
            SectionCalculator = new CSectionCalculator(this);
            SettingsSender = new CSettingsSender(this);
            PgnReceiverField = new PgnReceiver(this);

            headingFromSource = "Fix";

            AplicarGeometriaDeSecciones();
        }

        /// <summary>
        /// Reparte el ancho del implemento entre las secciones (posición lateral
        /// izquierda/derecha de cada una). Es lo que hacía
        /// <c>FormGPS.LoadSettings</c> (GUI.Designer.cs) al arrancar y el motor
        /// headless NO estaba haciendo.
        ///
        /// Sin esto, todas las secciones se quedan con el default de
        /// <c>CSection</c> (positionLeft=-4 / positionRight=+4): quedan TODAS
        /// encimadas en el mismo lugar, los bordes de la tira de cobertura
        /// colapsan en un punto y la huella sale de ancho CERO. Síntoma en
        /// cabina: "prendo una sección y pinta cualquier cosa".
        ///
        /// Hay que volver a llamarlo cada vez que cambie la config del implemento
        /// (ancho, cantidad de secciones, modo secciones/zonas u offset).
        /// </summary>
        public void AplicarGeometriaDeSecciones()
        {
            try
            {
                if (Tool.isSectionsNotZones)
                {
                    SectionCalculator.SectionSetPosition();
                    SectionCalculator.SectionCalcWidths();
                }
                else
                {
                    SectionCalculator.SectionCalcMulti();
                }
            }
            catch { /* config incompleta: mejor seguir que no arrancar */ }
        }

        // ---- arranque/parada del socket loopback (equivalente a
        // StartLoopbackServer/ReceiveAppData de UDPComm.Designer.cs, sin el
        // BeginInvoke a hilo de UI — acá no hay hilo de UI). ----
        /// <summary>
        /// Pasa los ajustes de guiado del perfil del vehículo a los campos que
        /// usa el pipeline. FormGPS lo hace en LoadSettings (GUI.Designer.cs);
        /// el motor NO lo hacía, así que corría con los valores por defecto del
        /// código aunque el perfil dijera otra cosa.
        ///
        /// El más caro era guidanceLookAheadTime: quedaba en 2,0 s contra 1,5 s
        /// configurado, o sea que el punto de anticipación iba un 33% más
        /// adelante de lo pedido. Con pasadas de 4 m ese metro extra alcanza
        /// para que el cálculo de "en qué pasada estoy" caiga en la de al lado
        /// al entrar en ángulo — y el operario ve que el piloto no agarra la
        /// línea más cercana.
        ///
        /// Es un error silencioso: no falla nada, solo guía distinto de lo
        /// configurado. Mismo criterio que el Load del perfil en Program.cs.
        /// </summary>
        private void CargarAjustesDeGuiado()
        {
            var s = AgOpenGPS.Properties.Settings.Default;

            guidanceLookAheadTime = s.setAS_guidanceLookAheadTime;
            isSteerInReverse = s.setAS_isSteerInReverse;
            Gyd.sideHillCompFactor = s.setAS_sideHillComp;

            // CONTROLADOR: isStanleyUsed nacía en `true` (State.cs:106) y nada
            // lo pisaba — el motor guiaba SIEMPRE con Stanley mientras el 6.8.5
            // (y nuestro WinForms) cargan el perfil, default false = Pure
            // Pursuit. Confirmado por DOS comparaciones independientes
            // (2026-08-06): Stanley sin tunear satura a ±30°, dispara falsa
            // reversa, invierte el signo del error (CGuidance.cs:45) y mutea
            // el PGN 254 → el círculo eterno contra ModSim.
            isStanleyUsed = s.setVehicle_isStanleyUsed;

            // Paridad con el 6.8.5: la base fix-to-fix del rumbo GPS quedaba
            // en 1,0 m (default del código) vs 0,5 m del perfil — el doble de
            // latencia de rumbo alimentando la fusión y el detector de reversa.
            minHeadingStepDist = s.setF_minHeadingStepDistance;

            // Alarma RTK + kill del piloto: mismo load que FormGPS.LoadSettings
            // (GUI.Designer.cs:615-616 del 6.8.6). Los dos defaults son false —
            // el bloque de ComprobarAlarmaRtk no actúa hasta que el operario
            // los prende en Configuración › Rumbo.
            isRTK_AlarmOn = s.setGPS_isRTK;
            isRTK_KillAutosteer = s.setGPS_isRTK_KillAutoSteer;

            // Aviso de proximidad a cabecera (lo consume CheckHeadlandProximity
            // en el tick de medio segundo): mismo load que FormGPS.
            isHeadlandDistanceOn = s.isHeadlandDistanceOn;

            // Invalidar guías para que se recalculen con los valores nuevos:
            // si el ancho o el offset cambiaron, la línea vieja quedó mal.
            ABLineField.isABValid = false;
            CurveField.isCurveValid = false;

            Log.EventWriter($"GuidanceEngine: ajustes de guiado del perfil — " +
                $"lookAhead={guidanceLookAheadTime:F2}s, reversa={isSteerInReverse}, " +
                $"sideHill={Gyd.sideHillCompFactor:F2}, " +
                $"controlador={(isStanleyUsed ? "Stanley" : "PurePursuit")}, " +
                $"minHeadingStep={minHeadingStepDist:F2}m, " +
                $"alarmaRTK={isRTK_AlarmOn} (kill={isRTK_KillAutosteer})");
        }

        /// <summary>
        /// Alarma por pérdida de RTK y kill del piloto. Port 1:1 del bloque de
        /// OpenGL.Designer.cs:505-561 del 6.8.6 (vivía en el PAINT de oglMain —
        /// por eso el motor headless no lo ejecutaba nunca y el piloto seguía
        /// enganchado guiando con un fix degradado). Corre una vez por fix.
        ///
        /// Con isRTK_AlarmOn y fixQuality != 4 (RTK fijo): alarma una sola vez
        /// (flanco), y si isRTK_KillAutosteer y el piloto está enganchado, lo
        /// desengancha por el MISMO camino que el toggle remoto
        /// (PerformAutoSteerClick, que ya loguea "autosteer OFF"). La
        /// recuperación pide 1 s continuo de fixQuality == 4
        /// (RTK_RECOVER_DEBOUNCE_MS, mismo debounce que upstream) antes de
        /// declarar "RTK recuperado". Los sonidos del original
        /// (sndRTKAlarm/sndRTKRecoverd) acá son log + TimedMessageBox (que en
        /// este host también termina en el log).
        /// </summary>
        private void ComprobarAlarmaRtk()
        {
            if (!isRTK_AlarmOn) return;

            if (Pn.fixQuality != 4)
            {
                // PERDIDO: alarmar (una vez) y armar la "recuperación" para después.
                if (!isRTKAlarming)
                {
                    if (isRTK_KillAutosteer && isBtnAutoSteerOn)
                    {
                        ((IAutoSteerHost)this).PerformAutoSteerClick();
                        ((IAutoSteerHost)this).TimedMessageBox(2000, "Piloto desenganchado", "Alarma de fix RTK");
                        Log.EventWriter("RTK perdido: piloto desenganchado");
                    }

                    Log.EventWriter("Alarma RTK: fix perdido");
                }

                isRTKAlarming = true;
                rtkWasAlarming = true;
                RTKBackSinceUtc = DateTime.MinValue; // no hay fix: resetear el debounce
            }
            else // Pn.fixQuality == 4
            {
                // FIJO: limpiar el flag de alarma.
                isRTKAlarming = false;

                // Si veníamos alarmando, detectar el flanco de "recuperado" con debounce.
                if (rtkWasAlarming)
                {
                    if (RTKBackSinceUtc == DateTime.MinValue)
                    {
                        // primer fix con 4 después de una pérdida → arranca el debounce
                        RTKBackSinceUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        var stableMs = (DateTime.UtcNow - RTKBackSinceUtc).TotalMilliseconds;
                        if (stableMs >= RTK_RECOVER_DEBOUNCE_MS)
                        {
                            // Evento "recuperado", una sola vez
                            Log.EventWriter("RTK recuperado");

                            rtkWasAlarming = false;
                            RTKBackSinceUtc = DateTime.MinValue;
                        }
                    }
                }
                else
                {
                    // Estado fijo normal, nada que hacer
                    RTKBackSinceUtc = DateTime.MinValue;
                }
            }
        }

        private readonly System.Diagnostics.Stopwatch _relojMedioSegundo = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Contadores de MEDIO segundo. Réplica del bloque
        /// `if (oneHalfSecondCounter >= 2)` del tick de la GUI de 6.8.6
        /// (GUI.Designer.cs:342-352), quedándose solo con la lógica:
        /// CheckHeadlandProximity (CHead.cs) calcula el punto/distancia a la
        /// cabecera y el aviso de proximidad — en el motor headless no tenía
        /// NINGÚN caller, así que HeadlandNearestPoint/HeadlandDistance
        /// quedaban en null para siempre. (isFlashOnOff y las etiquetas de
        /// velocidad del mismo bloque son puro display y no se portan; el
        /// "Steer Safe Off, No Tracks" del mismo tick queda fuera de este
        /// port a propósito — es otro bloque.)
        /// </summary>
        private void TickDeMedioSegundo()
        {
            if (_relojMedioSegundo.ElapsedMilliseconds < 500) return;
            _relojMedioSegundo.Restart();

            Bnd.CheckHeadlandProximity();
        }

        /// <summary>
        /// Pasa la configuración de los switches REMOTOS (el de trabajo y el de
        /// dirección cableados al módulo de máquina) del perfil del vehículo a
        /// <see cref="Mc"/>. Es lo que hacía <c>FormGPS.LoadSettings</c> y el
        /// motor headless NO estaba haciendo.
        ///
        /// El agujero: <c>CModuleComm</c> nace con TODO apagado salvo
        /// <c>isWorkSwitchActiveLow</c> (CModuleComm.cs:49-57) y nada volvía a
        /// tocar esos seis campos hasta el próximo guardado del panel. O sea que
        /// después de cada arranque el switch físico quedaba INERTE:
        /// <c>CheckWorkAndSteerSwitch</c> entra al bloque de trabajo/dirección
        /// solo con <c>isRemoteWorkSystemOn</c> en true (CModuleComm.cs:73), y
        /// ese arrancaba siempre en false aunque el perfil dijera lo contrario.
        ///
        /// Consecuencia en el lote: el operario habilita el switch de trabajo,
        /// apaga la pantalla al terminar la jornada y al otro día siembra
        /// creyendo que el corte por switch está activo. No lo está, y nada se
        /// lo avisa — baja la herramienta y las secciones no acompañan.
        ///
        /// <c>isRemoteWorkSystemOn</c> se DERIVA de los dos habilitadores en vez
        /// de leerse del XML, igual que hace el guardado del panel
        /// (EngineConfigVehiculoService.GuardarSwitches). Así un perfil viejo con
        /// el flag en true pero los dos switches deshabilitados no revive el
        /// bloque: ante la duda, el switch remoto no manda.
        /// </summary>
        public void CargarSwitchesRemotos()
        {
            var s = AgOpenGPS.Properties.Settings.Default;

            Mc.isWorkSwitchEnabled = s.setF_isWorkSwitchEnabled;
            Mc.isSteerWorkSwitchEnabled = s.setF_isSteerWorkSwitchEnabled;
            // OJO con la semántica: "activo con contacto cerrado" = true. La
            // comparación del motor es `workSwitchHigh != isWorkSwitchActiveLow`,
            // así que darlo vuelta hace que la máquina aplique AL REVÉS del
            // switch físico (secciones prendidas con el implemento levantado).
            // Se copia tal cual el perfil, sin "corregir" nada.
            // Vía CModuleComm: si ToolX es dueño del bit, cambiar la polaridad
            // invierte el bit y su "old" juntos para no fabricar un flanco.
            Mc.SetWorkSwitchActiveLow(s.setF_isWorkSwitchActiveLow);
            Mc.isWorkSwitchManualSections = s.setF_isWorkSwitchManualSections;
            Mc.isSteerWorkSwitchManualSections = s.setF_isSteerWorkSwitchManualSections;
            Mc.isRemoteWorkSystemOn = Mc.isWorkSwitchEnabled || Mc.isSteerWorkSwitchEnabled;
            // ToolX (switch de trabajo inalámbrico): pasa por el mismo habilitador
            // "trabajo" de arriba; esto sólo dice si se aceptan sus frames. Si el
            // perfil nuevo lo prende o apaga, ToolX vuelve a "nunca visto".
            if (Mc.isToolXWorkSwitch != s.setF_isToolXWorkSwitch) Mc.ResetToolX();
            Mc.isToolXWorkSwitch = s.setF_isToolXWorkSwitch;

            Log.EventWriter($"GuidanceEngine: switches remotos del perfil — " +
                $"trabajo={Mc.isWorkSwitchEnabled} (contactoCerrado={Mc.isWorkSwitchActiveLow}, " +
                $"manual={Mc.isWorkSwitchManualSections}, toolx={Mc.isToolXWorkSwitch}), " +
                $"direccion={Mc.isSteerWorkSwitchEnabled} (manual={Mc.isSteerWorkSwitchManualSections}), " +
                $"sistemaRemoto={Mc.isRemoteWorkSystemOn}");
        }

        private readonly System.Diagnostics.Stopwatch _relojSegundo = System.Diagnostics.Stopwatch.StartNew();

        // Reloj desde el arranque para secondsSinceStart. En FormGPS ese campo
        // lo actualiza el TICK DE LA GUI (GUI.Designer.cs:390): acá quedaba
        // clavado en 0 y BuildCurrentABLineList/BuildCurveCurrentList nunca
        // re-elegían la PARALELA más cercana con el piloto apagado (su gate es
        // "pasaron 0.66 s desde el último pick") — la línea activa quedaba
        // congelada donde se construyó y el tractor se alejaba de ella. Tercer
        // contador de la misma familia (autoTrack3SecTimer, makeUTurnCounter).
        private readonly System.Diagnostics.Stopwatch _relojArranque = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Contadores de UN SEGUNDO. Réplica del bloque `if (oneSecondCounter >= 4)`
        /// de AOG 6.8.5 (GUI.Designer.cs:285-298), quedándose SOLO con lo que es
        /// lógica y descartando lo que ahí mismo actualiza etiquetas.
        ///
        /// El problema: en 6.8.5 estos dos contadores los incrementa el tick de
        /// la GUI de WinForms. El motor headless no tiene GUI, así que nunca
        /// incrementaban y las dos funciones que dependen de ellos quedaban
        /// muertas sin dar ningún error:
        ///
        ///   · trk.autoTrack3SecTimer — la rutina que elige la guía MÁS CERCANA
        ///     (CAutoSteerUpdater.cs:61) corre solo si este contador llegó a 1.
        ///     Nunca llegaba, así que al prender el piloto se enganchaba a la
        ///     guía que estuviera seleccionada, no a la de al lado del tractor.
        ///     Es exactamente el sintoma reportado desde la cabina.
        ///   · vehicle.deadZoneDelayCounter — la zona muerta de la dirección
        ///     (CAutoSteerUpdater.cs:177) nunca superaba su umbral.
        ///
        /// Se usa reloj de pared y no un conteo de fixes: los fixes llegan a
        /// ~10 Hz pero el ritmo depende del GPS, y "3 segundos" tiene que ser
        /// tres segundos de verdad.
        /// </summary>
        private void TickDeUnSegundo()
        {
            if (_relojSegundo.ElapsedMilliseconds < 1000) return;
            _relojSegundo.Restart();

            Trk.autoTrack3SecTimer++;
            Vehicle.deadZoneDelayCounter++;

            // En FormGPS este contador sube 4 veces por segundo en el TICK DE LA
            // GUI (GUI.Designer.cs:385). CreateABOmegaTurn/WideTurn no arman el
            // giro hasta que llega a 4 ("wait 1.5 seconds" tras completar uno):
            // acá quedaba clavado en 0 y el U-turn automático NUNCA se armaba —
            // phase 0 eterno, sin error. +4 por segundo replica la cadencia
            // (granularidad 1 s en vez de 250 ms: la espera post-giro queda en
            // 1-2 s, igual de inofensiva). Cap para no desbordar en una jornada.
            if (makeUTurnCounter < 100000) makeUTurnCounter += 4;
        }

        /// <summary>
        /// "Enganchar al pivote": al prender el piloto, corre la guía activa
        /// para que pase por donde está el tractor, una sola vez por enganche.
        ///
        /// En FormGPS esto NO vive en el guiado: está adentro del código que
        /// DIBUJA el indicador de estado del piloto (OpenGL.Designer.cs ~1815),
        /// mezclado con el GL.Color4 del semáforo. Como el motor headless no
        /// dibuja, el comportamiento se perdía entero y sin dejar rastro.
        ///
        /// isAutoSnapped es el que hace que sea UNA vez y no en cada fix: si se
        /// llamara siempre, la guía perseguiría al tractor y no habría guía.
        /// Se rearma al soltar el piloto o al tomar el volante.
        /// </summary>
        private void EngancharGuiaAlPivote()
        {
            if (Mc.steerSwitchHigh)          // el operario tomó el volante
            {
                Trk.isAutoSnapped = false;
            }
            else if (isBtnAutoSteerOn)
            {
                if (Trk.isAutoSnapToPivot && !Trk.isAutoSnapped)
                {
                    Trk.SnapToPivot();
                    Trk.isAutoSnapped = true;
                    Log.EventWriter("GuidanceEngine: guia enganchada al pivote al prender el piloto");
                }
            }
            else
            {
                Trk.isAutoSnapped = false;
            }
        }

        public void Start()
        {
            if (_running) return;
            CargarAjustesDeGuiado();
            CargarSwitchesRemotos();
            _loopBackSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _loopBackSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _loopBackSocket.Bind(new IPEndPoint(IPAddress.Loopback, 15555));
            _running = true;
            // Igual que FormGPS (FormGPS.cs:849): arrancar el watchdog del
            // PgnReceiver. Sin esto, udpWatch nunca corre → ElapsedMilliseconds
            // queda en 0 → el gate del PGN 0xD6 (< UdpWatchLimit) descarta TODOS
            // los fixes de GPS antes de arrancar el watch → deadlock, nunca se
            // procesa posición real (el modo --sim no pasa por acá y por eso sí
            // andaba).
            PgnReceiverField.StartWatch();
            ArmarRecepcionLoopback();
            Log.EventWriter("GuidanceEngine: UDP loopback escuchando en 127.0.0.1:15555");
        }

        public void Stop()
        {
            _running = false;
            try { _loopBackSocket?.Close(); } catch { }
            _loopBackSocket = null;
        }

        // Serializa el pipeline de fix. En FormGPS todos los PGN se procesan en
        // el hilo de UI; acá ReceiveAppData re-arma el BeginReceiveFrom ANTES de
        // procesar, así que ante una ráfaga (GGA+VTG del mismo fix, PGN de
        // módulos) DOS hilos del pool entraban juntos a UpdateFixPosition →
        // SectionControlToUpdate → AntiSolape.Sincronizar, y el Dictionary del
        // CoverageIndex se corrompía ("A concurrent update was performed...").
        // Con el índice corrupto CADA fix siguiente tiraba la excepción y las
        // secciones quedaban CONGELADAS en su último estado — apagar el master
        // manual no las apagaba. Todo lo que corre bajo ReceiveFromAgIO asume
        // un solo hilo (Pn, heading, cobertura): este lock restituye el
        // contrato del original.
        private readonly object _fixPipelineLock = new object();

        /// <summary>Entrada serializada al pipeline de fix (la usa también el
        /// simulador, que llega por su propio timer y no por UDP).</summary>
        internal void ProcesarFixSerializado(Action accion)
        {
            lock (_fixPipelineLock) accion();
        }

        // OJO — patrón anti-recursión (mismo arreglo que UdpBridgeService):
        // en net9 BeginReceiveFrom completa SINCRÓNICO si ya hay datagrama
        // encolado e invoca el callback INLINE. Re-armar adentro del callback
        // apilaba un frame por datagrama pendiente → StackOverflowException y
        // el engine muerto sin log al activar el piloto (2026-08-05). El
        // callback solo atiende completados asíncronos; los sincrónicos los
        // drena el while de ArmarRecepcionLoopback con stack plano.
        private void ReceiveAppData(IAsyncResult ar)
        {
            if (ar.CompletedSynchronously) return;   // lo drena ArmarRecepcionLoopback
            if (ProcesarDatagrama(ar)) ArmarRecepcionLoopback();
        }

        internal void ArmarRecepcionLoopback()
        {
            while (_running)
            {
                var s = _loopBackSocket;
                if (s == null) return;
                IAsyncResult ar;
                try
                {
                    ar = s.BeginReceiveFrom(_loopBuffer, 0, _loopBuffer.Length, SocketFlags.None,
                        ref _endPointLoopBack, ReceiveAppData, null);
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    Log.EventWriter("GuidanceEngine: error re-armando recepción UDP: " + ex);
                    // Reintento en frío: no girar caliente ni quedar sordo.
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        System.Threading.Thread.Sleep(100);
                        ArmarRecepcionLoopback();
                    });
                    return;
                }
                if (!ar.CompletedSynchronously) return;  // sigue el callback
                if (!ProcesarDatagrama(ar)) return;
            }
        }

        /// <summary>Devuelve false solo si hay que dejar de escuchar.</summary>
        private bool ProcesarDatagrama(IAsyncResult ar)
        {
            if (!_running) return false;
            var s = _loopBackSocket;
            if (s == null) return false;
            try
            {
                int len = s.EndReceiveFrom(ar, ref _endPointLoopBack);
                byte[] data = new byte[len];
                Array.Copy(_loopBuffer, data, len);

                // TryEnter y DESCARTE, no lock: bajo una inundación UDP (eco,
                // relay loco, ModSim reflejando) un lock convencional encolaba
                // work-items sin límite (300+ hilos bloqueados, GB de byte[]).
                // Con telemetría solo importa el dato MÁS NUEVO: si el pipeline
                // está ocupado, este datagrama se tira y listo.
                if (System.Threading.Monitor.TryEnter(_fixPipelineLock))
                {
                    try { PgnReceiverField.ReceiveFromAgIO(data); }
                    finally { System.Threading.Monitor.Exit(_fixPipelineLock); }
                }
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                // ToString y no Message: acá cae CUALQUIER excepción del pipeline
                // de fix (NMEA→UpdateFixPosition→secciones). Sin el stack, un bug
                // real (p.ej. colección corrupta) queda enterrado como una línea
                // repetida 2000 veces y las secciones congeladas sin pista.
                // SocketException también cae acá (10054 por ICMP de un destino
                // apagado): es ruido, hay que seguir escuchando.
                Log.EventWriter("GuidanceEngine: error de recepción UDP: " + ex);
                return true;
            }
        }

        // equivalente exacto a FormGPS.SendPgnToLoop (UDPComm.Designer.cs:108).
        public void SendPgnToLoop(byte[] byteData)
        {
            if (_loopBackSocket != null && byteData.Length > 2)
            {
                try
                {
                    int crc = 0;
                    for (int i = 2; i + 1 < byteData.Length; i++) crc += byteData[i];
                    byteData[byteData.Length - 1] = (byte)crc;

                    _loopBackSocket.BeginSendTo(byteData, 0, byteData.Length, SocketFlags.None,
                        _epAgIO, EndSend, null);
                }
                catch { }
            }
        }

        private void EndSend(IAsyncResult ar)
        {
            try { _loopBackSocket?.EndSend(ar); } catch { }
        }

        // ---- traza de guiado (diagnóstico círculos 2026-08-06) --------------
        // Una línea CSV por fix con TODO lo que decide el volante. Es la
        // radiografía que faltó todo el día: cuando el tractor toca la línea y
        // se dispara, acá queda impreso QUÉ variable flipeó (lado, pasada,
        // rumbo, goal point, reversa). Costo: un archivo de ~50 KB/min con
        // piloto activo; no escribe nada con el piloto apagado. Sacarla cuando
        // el guiado quede validado.
        private System.IO.StreamWriter _traza;
        private void TrazaGuiado()
        {
            try
            {
                if (!isBtnAutoSteerOn) return;
                if (_traza == null)
                {
                    var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
                    System.IO.Directory.CreateDirectory(dir);
                    _traza = new System.IO.StreamWriter(
                        System.IO.Path.Combine(dir, "traza-guiado.csv"), append: false)
                    { AutoFlush = true };
                    _traza.WriteLine("hora,fixHeading_deg,gpsHeading_deg,imuCorr_deg,offsetIMU_deg," +
                        "isReverse,sameWay,pasada,xte_pivot_m,steer_cmd_deg,status,goalDist_m,trackIdx,vel_kmh");
                }
                double imuC = Ahrs.imuHeading == 99999 ? -1 : Ahrs.imuHeading * 0.1;
                _traza.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:HH:mm:ss.fff},{1:F1},{2:F1},{3:F1},{4:F2},{5},{6},{7},{8:F3},{9:F2},{10},{11:F2},{12},{13:F2}",
                    DateTime.Now,
                    fixHeading * 57.29578,
                    gpsHeading * 57.29578,
                    imuC,
                    imuGPS_Offset * 57.29578,
                    isReverse ? 1 : 0,
                    ABLineField.isHeadingSameWay ? 1 : 0,
                    ABLineField.howManyPathsAway,
                    ABLineField.distanceFromCurrentLinePivot,
                    guidanceLineSteerAngle / 100.0,
                    P254Field.pgn[P254Field.status],
                    Vehicle.UpdateGoalPointDistance(),
                    Trk.idx,
                    avgSpeed));
            }
            catch { /* la traza jamás voltea el pipeline */ }
        }

        // ---- pipeline por fix — equivalente a FormGPS.UpdateFixPosition()
        // (Position.designer.cs), pero sin las 2 líneas de refresh de GL
        // (oglBack/oglMain) ni el timer de frameTime, que son puro render. ----
        public void UpdateFixPosition()
        {
            // Medir la frecuencia REAL de fixes (port 1:1 de
            // Position.designer.cs:131-145 del 6.8.6). Va ANTES del guard de
            // inicialización, igual que upstream. Los clamps 70/3 son los del
            // original y acotan los dos extremos: el primer fix (stopwatch en 0
            // → Hz infinito → 70) y una pausa larga del GPS (dt de 60 s → 0,016
            // Hz → 3). El filtro 0.98/0.02 hace el resto: un solo valor loco
            // mueve gpsHz menos del 2%.
            timeSliceOfLastFix = (double)(swFrame.ElapsedTicks) / (double)System.Diagnostics.Stopwatch.Frequency;

            swFrame.Reset();
            swFrame.Start();

            //get Hz from timeslice
            nowHz = 1 / timeSliceOfLastFix;
            if (nowHz > 70) nowHz = 70;
            if (nowHz < 3) nowHz = 3;

            //simple comp filter
            gpsHz = 0.98 * gpsHz + 0.02 * nowHz;

            startCounter++;

            if (!isGPSPositionInitialized)
            {
                PositionUpdater.InitializeFirstFewGPSPositions();
                return;
            }

            if (!isFirstHeadingSet && hasBeenFirstHeadingSet)
            {
                for (int i = 0; i < stepFixPts.Length; i++)
                {
                    stepFixPts[i].isSet = 0;
                    stepFixPts[i].easting = 0;
                    stepFixPts[i].northing = 0;
                    stepFixPts[i].distance = 0;
                }
                prevFix = Pn.fix;
                prevDistFix = Pn.fix;
                gpsHeading = 0;
                fixHeading = 0;
                imuGPS_Offset = 0;
                hasBeenFirstHeadingSet = false;
            }

            Pn.speed = Pn.vtgSpeed;
            Pn.AverageTheSpeed();
            // Ultimo fix procesado: el state provider lo usa para NO retener una
            // velocidad vieja si el GPS se corta (el HUD mostraba 3,5 km/h
            // congelados para siempre, y QuantiX seguia dosificando con ella).
            lastFixUtc = DateTime.UtcNow;

            HeadingUpdater.UpdateHeading();
            AutoSteerUpdater.SendCorrectedPositionPgn();
            AutoSteerUpdater.BuildAndSendAutoSteerPgn();
            // Después de armar/mandar el PGN de dirección, igual que upstream:
            // en 6.8.6 el kill vive en el PAINT de oglMain, que corre recién
            // después de UpdateFixPosition — la latencia de un fix es la misma.
            ComprobarAlarmaRtk();
            TrazaGuiado();
            secondsSinceStart = _relojArranque.Elapsed.TotalSeconds;
            TickDeUnSegundo();
            TickDeMedioSegundo();
            YouTurnUpdater.UpdateYouTurnState();
            EngancharGuiaAlPivote();

            if (IsJobStarted)
            {
                // Control de secciones + cobertura (crea/gestiona las tiras CPatches
                // que AddSectionOrPathPoints va llenando de triángulos). Corre después
                // del pipeline de posición y antes de enviar los PGN, porque
                // BuildMachineByte (dentro) puebla los bytes de sección de P239/P229.
                SectionControlToUpdate();

                P239Field.pgn[P239Field.geoStop] = Mc.isOutOfBounds ? (byte)1 : (byte)0;
                SendPgnToLoop(P239Field.pgn);
                SendPgnToLoop(P229Field.pgn);
            }
        }
    }
}
