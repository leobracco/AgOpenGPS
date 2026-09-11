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
using AgOpenGPS.IO;

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

            // sim_coords_{lat}_{lon}: reubica el simulador a esa coordenada
            // (teletransporte). Port de GUI.FloatingMenu.cs — reemplaza el OK de
            // FormSimCoords. Mismos guards que el original: sin lote abierto (la
            // guía/cobertura ya calculada quedaría referida a un origen que
            // dejó de tener sentido) y con el simulador prendido (mover la
            // posición de un fix GPS real sería falsificarlo). Sin esto la
            // pantalla sim-coords.html llamaba a un comando que no existía acá
            // — silencioso, "ok:false" sin explicación — y el simulador quedaba
            // parado donde lo dejó la última sesión, casi siempre AFUERA del
            // lote que se abre después: con el pivote fuera del lindero el giro
            // de cabecera nunca arma (IsPointInsideTurnArea siempre negativo).
            if (cmd.StartsWith("sim_coords_"))
            {
                if (IsJobStarted || !isSimTimerEnabled) return false;
                string rest = cmd.Substring("sim_coords_".Length);
                string[] parts = rest.Split('_');
                if (parts.Length != 2) return false;
                var styles = NumberStyles.Float | NumberStyles.AllowLeadingSign;
                if (!double.TryParse(parts[0], styles, CultureInfo.InvariantCulture, out double lat) ||
                    !double.TryParse(parts[1], styles, CultureInfo.InvariantCulture, out double lon))
                    return false;
                if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0) return false;
                Pn.DefineLocalPlane(new Wgs84(lat, lon), true);
                return true;
            }

            // Crear + activar una AB en la posición actual del tractor. Equivale
            // a FormBuildTracks.btnEnter_AB / TrkBuilder_CreateABFromPivot
            // (GuidanceEngineHost.TrackBuilder.cs, ítem 2 del PEDIDO taller —
            // reemplaza el viejo stopgap ad-hoc que vivía acá). "track_new_ab"
            // es el comando que manda el menú Guías; "track_ab_here" permite un
            // heading explícito ("track_ab_here_<grados>").
            if (cmd == "track_new_ab" || cmd == "track_ab_here" || cmd.StartsWith("track_ab_here_"))
            {
                double headingDeg = fixHeading * 180.0 / Math.PI;
                if (cmd.StartsWith("track_ab_here_") &&
                    double.TryParse(cmd.Substring("track_ab_here_".Length), NumberStyles.Any, CultureInfo.InvariantCulture, out double deg))
                {
                    headingDeg = deg;
                }
                TrkBuilder_CreateABFromPivot(headingDeg, null);
                // TrkBuilder_CreateABFromPivot setea Trk.idx pero NO invalida la
                // línea de guiado (eso lo hace TrkBuilder_CloseUse en el flujo del
                // panel de tracks). Al invocarse suelto vía "track_new_ab" hay que
                // invalidar acá: sin esto, con autosteer ON, BuildCurrentABLineList
                // saltea el rebuild (CABLine.cs:82,122) y el mapa sigue mostrando la
                // guía vieja en vez de la recién creada.
                CurveField.isCurveValid = false;
                ABLineField.isABValid = false;
                // Y GUARDARLA: el alta rápida no pasa por TrkBuilder_CloseUse
                // (que es quien salva) — la guía vivía solo en memoria y al
                // reabrir el lote no estaba ("las guías guardadas deberían
                // aparecer en el listado y no aparecen", banco 2026-08-10).
                // Mismo criterio que FormQuickAB, que salva al crear.
                SaveTracks();
                return true;
            }

            // Secciones individuales (1..16) y zonas (1..8). Port de
            // btnSectionXMan_Click / btnZoneX_Click (Sections.Designer.cs) sin el
            // color del botón. Ciclan Off → Auto → On → Off. Son imprescindibles
            // para sembrar: levantar un cuerpo en una punta, cortar media barra
            // en una cuña. Se rechazan (false → "unknown") los índices que no
            // existen en el implemento actual, en vez de aceptarlos y no hacer
            // nada — un comando que dice "ok" sin efecto es peor que uno que falla.
            if (cmd.StartsWith("seccion_") &&
                int.TryParse(cmd.Substring("seccion_".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int secN))
            {
                if (secN < 1 || secN > 16 || secN > Tool.numOfSections) return false;
                Sections[secN - 1].sectionBtnState = GetNextSectionState(Sections[secN - 1].sectionBtnState);
                return true;
            }

            if (cmd.StartsWith("zona_") &&
                int.TryParse(cmd.Substring("zona_".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int zonaN))
            {
                return ToggleZone(zonaN);
            }

            switch (cmd)
            {
                case "autosteer":
                    ((IAutoSteerHost)this).PerformAutoSteerClick();
                    return true;
                case "uturn":
                    ToggleYouTurn();
                    return true;
                // "Marcar giro": el operario define con dos marcas dónde gira el
                // U-turn sin recorrer el lindero (ver la spec
                // docs/superpowers/specs/2026-08-10-marcar-giro-design.md).
                case "marcar_giro":
                    return MarcarGiroAca();
                case "giro_borrar_marcas":
                    BorrarMarcasGiro();
                    return true;
                case "pick":
                    SelectTrack();
                    return true;
                case "job_close":
                case "lote_cerrar":
                    CloseField();
                    return true;

                // --- resto del vocabulario real de FormGPS.ExecuteGuidanceCommand
                // (GUI.FloatingMenu.cs) que SÍ es lógica pura (PEDIDO taller
                // 2026-07-23, COORDINACION-SESIONES.md: "las barras nativas del
                // cockpit funcionan 100% contra el engine"). Cada caso es la
                // copia fiel del Click WinForms correspondiente (Controls.Designer.cs
                // / Sections.Designer.cs) sin las líneas de imagen de botón/sonido,
                // que no aplican sin UI. ---
                case "autotrack":
                    // btnAutoTrack_Click
                    Trk.isAutoTrack = !Trk.isAutoTrack;
                    return true;
                case "sec_auto":
                    SectionMasterAuto();
                    return true;
                case "sec_manual":
                    SectionMasterManual();
                    return true;
                case "contour":
                    ToggleContour();
                    return true;
                case "contour_lock":
                    // btnContourLock_Click
                    if (Ct.isContourBtnOn) Ct.SetLockToLine();
                    return true;
                case "uturn_skips":
                    CycleYouTurnSkip();
                    return true;

                // Giro manual y salto de guía. En FormGPS NO tienen botón: son
                // zonas invisibles del mapa (GUI.Designer.cs ~1345-1420) que hay
                // que acertar a ciegas. Acá se exponen como comandos para que la
                // barra del cockpit los muestre como lo que son.
                case "uturn_swap":
                    // SwapDirection (GUI.Designer.cs:1551): con el giro ARMADO
                    // (no disparado) invierte el lado y tira el camino — el
                    // updater lo re-arma solo hacia el otro lado en el próximo
                    // fix. Con el giro EN CURSO, apaga el U-turn (abortar).
                    if (!Yt.isYouTurnTriggered)
                    {
                        Yt.isTurnLeft = !Yt.isTurnLeft;
                        Yt.ResetCreatedYouTurn();
                        Yt.turnTooCloseTrigger = false;
                        Yt.isTurnCreationTooClose = false;
                        Log.EventWriter("GuidanceEngine: giro invertido, ahora hacia la " +
                            (Yt.isTurnLeft ? "izquierda" : "derecha"));
                    }
                    else if (Yt.isYouTurnBtnOn)
                    {
                        ToggleYouTurn();
                    }
                    return true;
                case "uturn_manual_izq":
                    return GiroManual(false);
                case var s when s.StartsWith("uturn_skip_"):
                    return FijarSaltoDelGiro(s.Substring("uturn_skip_".Length));
                case var s when s.StartsWith("skips_"):
                    // Combo de la barra de abajo (espejo del cboxpRowWidth
                    // nativo): manda el ANCHO directo 1..10, no las salteadas.
                    // Contra el motor era otro botón muerto: solo lo atendía
                    // FormGPS (GUI.FloatingMenu.cs).
                    return int.TryParse(s.Substring("skips_".Length), NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out int ancho)
                           && AplicarAnchoDelSalto(ancho);
                case "uturn_manual_der":
                    return GiroManual(true);
                case "lateral_izq":
                    return SaltoLateral(false);
                case "lateral_der":
                    return SaltoLateral(true);
                case "center":
                    // btnSnapToPivot_Click
                    Trk.SnapToPivot();
                    return true;
                case "reset_direccion":
                    // En FormGPS es tocar el tractor en el mapa (GUI.Designer.cs
                    // ~1500): resetea la dirección cuando quedó "en reversa".
                    // Pasa con teleports del GPS (relanzar ModSim, mover la
                    // antena, cambiar de lote): el salto dispara IsReverse, el
                    // rumbo GPS queda invertido 180° y con IMU ($PANDA) el
                    // offset re-converge tan lento (peso 0.02) que queda clavado
                    // mirando al revés. Después del reset: avanzar >1,5 km/h en
                    // línea recta para re-aprender el rumbo.
                    Array.Clear(stepFixPts, 0, stepFixPts.Length);
                    isFirstHeadingSet = false;
                    isReverse = false;
                    Log.EventWriter("GuidanceEngine: reset de dirección — avanzar >1,5 km/h para fijar rumbo");
                    return true;
                case "nudge_left":
                    // btnAdjLeft_Click
                    Trk.NudgeTrack(-AgOpenGPS.Properties.Settings.Default.setAS_snapDistance * 0.01);
                    return true;
                case "nudge_right":
                    // btnAdjRight_Click
                    Trk.NudgeTrack(AgOpenGPS.Properties.Settings.Default.setAS_snapDistance * 0.01);
                    return true;
                case "reset_herramienta":
                    ResetToolHeading();
                    return true;
                case "track_next":
                    CycleTrack(forward: true);
                    return true;
                case "track_prev":
                    CycleTrack(forward: false);
                    return true;
                case "track_nearest":
                    // Activar la guía (track AB/curva) MÁS CERCANA al tractor AHORA.
                    // One-shot determinístico: no depende del modo auto-track (que en
                    // headless está muerto porque autoTrack3SecTimer nunca incrementa).
                    if (Trk.gArr != null && Trk.gArr.Count > 0)
                    {
                        // FindClosestRefTrack necesita idx>=0 como semilla (CTrack.cs:31).
                        if (Trk.idx < 0)
                        {
                            Trk.idx = Trk.gArr.FindIndex(t => t.isVisible);
                            if (Trk.idx < 0) Trk.idx = 0;
                        }
                        int near = Trk.FindClosestRefTrack(steerAxlePos);
                        if (near >= 0) Trk.idx = near;
                        // Invalidar para reconstruir la línea desde la nueva guía.
                        CurveField.isCurveValid = false;
                        ABLineField.isABValid = false;
                    }
                    return true;
                case "tracks_off":
                    // btnTracksOff_Click
                    Trk.idx = -1;
                    return true;
                case "hidraulico":
                    ToggleHydraulicLift();
                    return true;
                case "isobus":
                    // btnIsobusSC_Click: pedirle al monitor ISOBUS que prenda o
                    // apague su control de secciones. Es un REQUEST por PGN: el
                    // estado real vuelve del monitor y lo refleja el snapshot
                    // (isobus_on), no este toggle.
                    Isobus.RequestSectionControlEnabled(!Isobus.SectionControlEnabled);
                    return true;
                case "reset_all":
                    // resetALLToolStripMenuItem_Click sin el diálogo (la
                    // confirmación la pone la pantalla ANTES de mandar esto).
                    // Mismo guard que el nativo: con lote abierto no se resetea
                    // nada — cerrarlo primero.
                    //
                    // Después de borrar, el motor SALE: su config sigue viva en
                    // memoria y cualquier Save() posterior resucitaría lo
                    // borrado. El delay deja salir la respuesta HTTP; la
                    // pantalla avisa que hay que reiniciar (el launcher de
                    // cabina levanta todo de vuelta al reiniciar la pantalla).
                    if (IsJobStarted) return false;
                    RegistrySettings.Reset();
                    Log.EventWriter("GuidanceEngine: reset de fabrica (reset_all) — el motor sale para no re-guardar la config vieja");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(700).ConfigureAwait(false);
                        Environment.Exit(0);
                    });
                    return true;
                case "cabecera_onoff":
                    ToggleHeadland();
                    return true;
                case "cabecera_secciones":
                    // cboxIsSectionControlled_Click (sin persistir a Settings:
                    // el motor headless no recarga Properties.Settings por sesión
                    // todavía — mismo criterio que otros toggles en caliente).
                    Bnd.isSectionControlledByHeadland = !Bnd.isSectionControlledByHeadland;
                    return true;
                case "tram_vista":
                    CycleTramDisplayMode();
                    return true;
                case "borrar_contornos":
                    // deleteContourPathsToolStripMenuItem_Click (Controls.Designer.cs).
                    Ct.stripList?.Clear();
                    Ct.ptList?.Clear();
                    Ct.ctList?.Clear();
                    contourSaveList?.Clear();
                    return true;
                case "borrar_aplicado":
                    return DeleteApplied();
                default:
                    return false;
            }
        }

        // btnSectionMasterAuto_Click (Sections.Designer.cs), sin imagen/sonido.
        private void SectionMasterAuto()
        {
            manualBtnState = btnStates.Off;
            autoBtnState = autoBtnState == btnStates.Off ? btnStates.Auto : btnStates.Off;
            if (autoBtnState == btnStates.Auto) MarkAsWorkedTrack();
            SetAllSectionsState(autoBtnState);
        }

        // btnSectionMasterManual_Click (Sections.Designer.cs), sin imagen/sonido.
        private void SectionMasterManual()
        {
            autoBtnState = btnStates.Off;
            manualBtnState = manualBtnState == btnStates.Off ? btnStates.On : btnStates.Off;
            if (manualBtnState == btnStates.On) MarkAsWorkedTrack();
            SetAllSectionsState(manualBtnState);
        }

        // GetNextState (Sections.Designer.cs): Off → Auto → On → Off.
        private static btnStates GetNextSectionState(btnStates state)
        {
            if (state == btnStates.Off) return btnStates.Auto;
            if (state == btnStates.Auto) return btnStates.On;
            if (state == btnStates.On) return btnStates.Off;
            return btnStates.Off;
        }

        // btnZoneX_Click + IndividualZoneAndButtonToState (Sections.Designer.cs).
        // El estado nuevo sale de la ÚLTIMA sección de la zona
        // (zoneRanges[zona]-1) y se aplica a todo el rango. Ojo con el reparto:
        // la zona 1 arranca en 0 y el resto en zoneRanges[zona-1] — es asimétrico
        // a propósito (ver SeccionesCicladoTests).
        private bool ToggleZone(int zona)
        {
            if (zona < 1 || zona > 8) return false;
            if (Tool.isSectionsNotZones) return false;   // el implemento está en modo secciones
            if (Tool.zoneRanges[zona] <= 0) return false; // esa zona no existe

            int inicio = zona == 1 ? 0 : Tool.zoneRanges[zona - 1];
            int fin = Tool.zoneRanges[zona];
            if (fin > 16) fin = 16;
            if (inicio < 0 || inicio >= fin) return false;

            btnStates nuevo = GetNextSectionState(Sections[fin - 1].sectionBtnState);
            for (int i = inicio; i < fin; i++) Sections[i].sectionBtnState = nuevo;
            return true;
        }

        // MarkAsWorkedTrack (Sections.Designer.cs) — pura, sin UI.
        private void MarkAsWorkedTrack()
        {
            if (Trk.idx < 0) return;
            var track = Trk.gArr[Trk.idx];
            if (track.mode == TrackMode.AB) track.workedTracks.Add(ABLineField.howManyPathsAway);
            else if (track.mode == TrackMode.Curve) track.workedTracks.Add(CurveField.howManyPathsAway);
        }

        // AllSectionsAndButtonsToState/AllZonesAndButtonsToState (Sections.Designer.cs)
        // sin el manejo de color de botones (WinForms, bloque 10).
        private void SetAllSectionsState(btnStates state)
        {
            if (Tool.isSectionsNotZones)
            {
                for (int i = 0; i < 16; i++) Sections[i].sectionBtnState = state;
                return;
            }

            if (Tool.zoneRanges[0] == 0) return;
            if (Tool.zoneRanges[1] != 0)
                for (int i = 0; i < Tool.zoneRanges[1]; i++) Sections[i].sectionBtnState = state;
            for (int z = 2; z <= 8; z++)
            {
                if (Tool.zoneRanges[z] != 0)
                    for (int i = Tool.zoneRanges[z - 1]; i < Tool.zoneRanges[z]; i++) Sections[i].sectionBtnState = state;
            }
        }

        // toolStripAreYouSure_Click (Controls.Designer.cs) — borra TODO lo
        // aplicado (contornos + cobertura). Destructivo: mismo guard que el
        // original (solo con lote abierto y master auto/manual apagados —
        // sin eso, FormGPS tampoco mostraba el diálogo de confirmación).
        // Acá no hay diálogo (headless, el caller ya confirmó); si el guard
        // no se cumple, "unknown" (false) en vez de ejecutar a medias.
        private bool DeleteApplied()
        {
            if (!IsJobStarted || autoBtnState != btnStates.Off || manualBtnState != btnStates.Off)
                return false;

            SetAllSectionsState(btnStates.Off);
            manualBtnState = btnStates.Off;
            autoBtnState = btnStates.Off;

            Ct.StopContourLine();
            Ct.ResetContour();
            Fd.workedAreaTotal = 0;
            Fd.workedAreaTotalUser = 0;
            Fd.distanceUser = 0;
            Neta.Reset();
            Fd.actualAreaCovered = 0;

            for (int j = 0; j < TriStripField.Count; j++)
            {
                TriStripField[j].patchList?.Clear();
                TriStripField[j].triangleList?.Clear();
            }
            patchSaveList?.Clear();

            // Y el índice del anti-solape, que es una COPIA aparte de la
            // cobertura: sin esto quedaba lleno de triángulos viejos y las
            // secciones seguían cortando sobre pintura que ya no existía
            // ("la pintura no aparece pero las secciones se cortan igual",
            // reporte 2026-08-07 en el circuito de pruebas).
            AntiSolape?.Reiniciar();

            foreach (var t in Trk.gArr) t.workedTracks.Clear();

            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                ContourFiles.CreateFile(dir);
                // TAMBIÉN Sections.txt: el port original vaciaba solo la RAM y
                // el contorno — el archivo de cobertura quedaba intacto, así
                // que cerrar y reabrir el lote RESUCITABA todo lo borrado
                // (circuito de pruebas 2026-08-07, paso 5). El WinForms sí lo
                // vaciaba (FileCreateSections en toolStripAreYouSure_Click).
                SectionsFiles.CreateEmpty(dir);
                Log.EventWriter("GuidanceEngine: borrar_aplicado — cobertura y contorno vaciados (RAM + disco)");
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: borrar_aplicado al vaciar archivos: " + ex.Message);
            }

            return true;
        }

        // btnContour_Click (Controls.Designer.cs), sin imagen de botón. El
        // "cherry-pick upstream" (resetear trk.idx antes de tocar contour) y el
        // Enable/DisableYouTurnButtons se inlinean acá (son solo Yt.ResetYouTurn()
        // + Yt.isYouTurnBtnOn=false, el resto de esas 2 funciones es imagen de botón).
        private void ToggleContour()
        {
            if (Trk.idx != -1) Trk.idx = -1;
            Trk.isAutoTrack = false;

            Ct.isContourBtnOn = !Ct.isContourBtnOn;

            if (Ct.isContourBtnOn)
            {
                Yt.isYouTurnBtnOn = false;
                Yt.ResetYouTurn();
                guidanceLookAheadTime = 0.5;
                Ct.isLocked = false;
            }
            else
            {
                Yt.isYouTurnBtnOn = false;
                Yt.ResetYouTurn();
                ABLineField.isABValid = false;
                CurveField.isCurveValid = false;
                Ct.isLocked = false;
                guidanceLookAheadTime = AgOpenGPS.Properties.Settings.Default.setAS_guidanceLookAheadTime;
                if (isBtnAutoSteerOn) ((IAutoSteerHost)this).PerformAutoSteerClick();
            }
            Log.EventWriter("GuidanceEngine: contour " + (Ct.isContourBtnOn ? "ON" : "OFF"));
        }

        /// <summary>
        /// Giro manual: dispara el U-turn hacia el lado pedido sin esperar a que
        /// el automático lo decida. Si ya hay uno en curso, lo CANCELA — mismo
        /// botón para armar y para abortar, igual que el original.
        ///
        /// Respeta el límite de velocidad de funciones: un giro disparado a
        /// velocidad de transporte es una máquina cruzándose sola. Devuelve
        /// false si no se pudo (sin guía, o yendo muy rápido), para que la barra
        /// pueda avisar en vez de quedarse muda.
        /// </summary>
        private bool GiroManual(bool haciaLaDerecha)
        {
            if (Trk.idx < 0) return false;              // sin guía no hay a dónde girar
            // NO exige el U-turn automático: el giro manual es justamente para
            // girar cuando el automático no está armado. El nativo tampoco lo
            // pedía (las zonas del mapa solo miraban el feature-flag isUTurnOn
            // de settings, no yt.isYouTurnBtnOn) — el guard que había acá era
            // un exceso del port (aclarado por el usuario 2026-07-31).

            if (Yt.isYouTurnTriggered)
            {
                Yt.ResetYouTurn();
                Log.EventWriter("GuidanceEngine: giro manual cancelado");
                return true;
            }

            if (Vehicle.functionSpeedLimit <= avgSpeed)
            {
                Log.EventWriter($"GuidanceEngine: giro manual rechazado, {avgSpeed:F1} km/h supera el limite de {Vehicle.functionSpeedLimit:F1}");
                return false;
            }

            Yt.isYouTurnTriggered = true;
            Yt.BuildManualYouTurn(haciaLaDerecha, true);
            Log.EventWriter("GuidanceEngine: giro manual a la " + (haciaLaDerecha ? "derecha" : "izquierda"));
            return true;
        }

        /// <summary>
        /// Cuántas guías SALTEA el giro en cabecera, de 0 a 9. Es un NÚMERO que
        /// el operario elige, no un modo que cicla: con 12 m de ancho y una
        /// sembradora que necesita dos pasadas de margen, "salteo 2" es una
        /// decisión concreta y tenerla que buscar ciclando un botón mientras se
        /// llega a la cabecera no sirve.
        ///
        /// Semántica del menú: N = guías salteadas. 0 → va a la contigua,
        /// 1 → saltea una, y así. Internamente rowSkipsWidth es el ANCHO del
        /// movimiento en guías (1 = contigua, como el cboxpRowWidth nativo),
        /// o sea width = N + 1. No confundir los dos números: acá entra lo que
        /// muestra el menú, no el ancho.
        ///
        /// Con 0 (contigua) se apaga el modo alternado; salteando una o más se
        /// prende, que es cuando el patrón alternado tiene sentido.
        /// </summary>
        private bool FijarSaltoDelGiro(string valor)
        {
            if (!int.TryParse(valor, out int n)) return false;
            if (n < 0 || n > 9) return false;

            return AplicarAnchoDelSalto(n + 1);
        }

        /// <summary>
        /// Fija el ancho del movimiento del giro EN GUÍAS (1..10, 1 = contigua).
        /// Es el mismo campo que toca el menú de salteo, pero en la otra
        /// convención: el combo de la barra de abajo (espejo del cboxpRowWidth
        /// nativo) muestra 1..10 y manda "skips_N" con el ancho directo.
        /// </summary>
        private bool AplicarAnchoDelSalto(int width)
        {
            if (width < 1 || width > 10) return false;

            Yt.rowSkipsWidth = width;
            AgOpenGPS.Properties.Settings.Default.set_youSkipWidth = Yt.rowSkipsWidth;
            AgOpenGPS.Properties.Settings.Default.Save();

            if (Yt.rowSkipsWidth < 2)
            {
                Yt.skipMode = SkipMode.Normal;
            }
            else if (Yt.skipMode == SkipMode.Normal)
            {
                Yt.skipMode = SkipMode.Alternative;
                Yt.Set_Alternate_skips();
            }
            else if (Yt.skipMode == SkipMode.Alternative)
            {
                // Recalcular el patrón con el ancho nuevo.
                Yt.Set_Alternate_skips();
            }

            Yt.ResetCreatedYouTurn();
            Log.EventWriter($"GuidanceEngine: el giro se mueve {Yt.rowSkipsWidth} guia(s) (saltea {Yt.rowSkipsWidth - 1}), modo {Yt.skipMode}");
            return true;
        }

        /// <summary>
        /// Salto lateral: corre el guiado UNA pasada al costado sin dar la
        /// vuelta. Es lo que se usa para saltear una guía (esquivar un pozo,
        /// retomar donde quedó) sin tener que rehacer la línea.
        /// </summary>
        private bool SaltoLateral(bool haciaLaDerecha)
        {
            if (Trk.idx < 0) return false;

            if (Vehicle.functionSpeedLimit <= avgSpeed)
            {
                Log.EventWriter($"GuidanceEngine: salto lateral rechazado, {avgSpeed:F1} km/h supera el limite de {Vehicle.functionSpeedLimit:F1}");
                return false;
            }

            Yt.BuildManualYouLateral(haciaLaDerecha);
            Yt.ResetYouTurn();
            Log.EventWriter("GuidanceEngine: salto lateral a la " + (haciaLaDerecha ? "derecha" : "izquierda"));
            return true;
        }

        // btnYouSkipEnable_Click (Controls.Designer.cs), sin imagen de botón.
        private void CycleYouTurnSkip()
        {
            Yt.rowSkipsWidth = AgOpenGPS.Properties.Settings.Default.set_youSkipWidth;
            switch (Yt.skipMode)
            {
                case SkipMode.Normal:
                    Yt.skipMode = SkipMode.Alternative;
                    if (Yt.rowSkipsWidth < 2) Yt.rowSkipsWidth = 2;
                    Yt.Set_Alternate_skips();
                    break;
                case SkipMode.Alternative:
                    Yt.skipMode = SkipMode.IgnoreWorkedTracks;
                    if (Yt.rowSkipsWidth < 2) Yt.rowSkipsWidth = 2;
                    break;
                case SkipMode.IgnoreWorkedTracks:
                    Yt.skipMode = SkipMode.Normal;
                    break;
            }
            Yt.ResetCreatedYouTurn();
        }

        // btnResetToolHeading_Click (Controls.Designer.cs) — matemática pura.
        private void ResetToolHeading()
        {
            tankPos.heading = fixHeading;
            tankPos.easting = hitchPos.easting + (Math.Sin(tankPos.heading) * Tool.tankTrailingHitchLength);
            tankPos.northing = hitchPos.northing + (Math.Cos(tankPos.heading) * Tool.tankTrailingHitchLength);

            toolPivotPos.heading = tankPos.heading;
            toolPivotPos.easting = tankPos.easting + (Math.Sin(toolPivotPos.heading) * Tool.trailingHitchLength);
            toolPivotPos.northing = tankPos.northing + (Math.Cos(toolPivotPos.heading) * Tool.trailingHitchLength);
        }

        // btnCycleLines_Click / btnCycleLinesBk_Click (Controls.Designer.cs), sin
        // imagen/label de botón. btnCycleLinesBk tiene el caso especial de contour
        // lock (mismo criterio que el original: si contour está on, "prev" solo
        // bloquea la línea en vez de ciclar guía).
        private void CycleTrack(bool forward)
        {
            if (!forward && Ct.isContourBtnOn)
            {
                Ct.SetLockToLine();
                return;
            }

            Trk.isAutoTrack = false;
            if (Trk.gArr.Count <= 1) return;

            while (true)
            {
                Trk.idx += forward ? 1 : -1;
                if (Trk.idx >= Trk.gArr.Count) Trk.idx = 0;
                else if (Trk.idx < 0) Trk.idx = Trk.gArr.Count - 1;

                if (Trk.gArr[Trk.idx].isVisible) break;
            }

            // Invalidar la línea activa para que se reconstruya YA desde el nuevo
            // track (sin esto queda la vieja mientras isABValid siga true).
            CurveField.isCurveValid = false;
            ABLineField.isABValid = false;

            if (isBtnAutoSteerOn) ((IAutoSteerHost)this).PerformAutoSteerClick();
            if (Yt.isYouTurnBtnOn) ToggleYouTurn();
        }

        // btnHydLift_Click (Controls.Designer.cs).
        private void ToggleHydraulicLift()
        {
            if (Bnd.isHeadlandOn)
            {
                Vehicle.isHydLiftOn = !Vehicle.isHydLiftOn;
                if (!Vehicle.isHydLiftOn) P239Field.pgn[P239Field.hydLift] = 0;
            }
            else
            {
                P239Field.pgn[P239Field.hydLift] = 0;
                Vehicle.isHydLiftOn = false;
            }
        }

        // btnHeadlandOnOff_Click (Controls.Designer.cs).
        private void ToggleHeadland()
        {
            Bnd.isHeadlandOn = !Bnd.isHeadlandOn;
            if (Vehicle.isHydLiftOn && !Bnd.isHeadlandOn) Vehicle.isHydLiftOn = false;
            if (!Bnd.isHeadlandOn) P239Field.pgn[P239Field.hydLift] = 0;
        }

        // btnTramDisplayMode_Click + GetNextDisplayMode (Controls.Designer.cs).
        private void CycleTramDisplayMode()
        {
            Tram.isLeftManualOn = false;
            Tram.isRightManualOn = false;
            Tram.displayMode = GetNextTramDisplayMode(Tram.displayMode);
        }

        private TramMode GetNextTramDisplayMode(TramMode currentMode)
        {
            TramMode nextMode;
            switch (currentMode)
            {
                case TramMode.None: nextMode = TramMode.All; break;
                case TramMode.All: nextMode = TramMode.FillTracks; break;
                case TramMode.FillTracks: nextMode = TramMode.BoundaryTracks; break;
                case TramMode.BoundaryTracks: nextMode = TramMode.None; break;
                default: throw new ArgumentOutOfRangeException(nameof(currentMode), "TramMode argument out of range");
            }
            if (nextMode.IncludesBoundaryTracks() && Tram.tramBndOuterArr.Count == 0)
                nextMode = GetNextTramDisplayMode(nextMode);
            if (nextMode.IncludesFillTracks() && Tram.tramList.Count == 0)
                nextMode = GetNextTramDisplayMode(nextMode);
            return nextMode;
        }

    }
}
