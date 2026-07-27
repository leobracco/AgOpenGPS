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
                case "center":
                    // btnSnapToPivot_Click
                    Trk.SnapToPivot();
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

            for (int j = 0; j < TriStripField.Count; j++)
            {
                TriStripField[j].patchList?.Clear();
                TriStripField[j].triangleList?.Clear();
            }
            patchSaveList?.Clear();

            foreach (var t in Trk.gArr) t.workedTracks.Clear();

            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                ContourFiles.CreateFile(dir);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: borrar_aplicado ContourFiles.CreateFile: " + ex.Message);
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
