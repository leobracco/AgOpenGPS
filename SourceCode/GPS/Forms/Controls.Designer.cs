//Please, if you use this, share the improvements

using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Core.Translations;
using AgOpenGPS.Forms;
using AgOpenGPS.Forms.Pickers;
using AgOpenGPS.Forms.Profiles;
using AgOpenGPS.IO;
using AgOpenGPS.Properties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        #region Right Menu
        public bool isABCyled = false;
        private void btnContour_Click(object sender, EventArgs e)
        {
            // Cherry-pick upstream 08bf5665 (6.8.3): si había un track activo,
            // resetearlo antes de tocar contour evita el crash al volver de
            // Contour a Tracks con un trk.idx que ya no es válido.
            if (trk.idx != -1)
            {
                trk.idx = -1;
            }

            trk.isAutoTrack = false;
            btnAutoTrack.Image = Resources.AutoTrackOff;

            ct.isContourBtnOn = !ct.isContourBtnOn;
            btnContour.Image = ct.isContourBtnOn ? Properties.Resources.ContourOn : Properties.Resources.ContourOff;

            if (ct.isContourBtnOn)
            {
                DisableYouTurnButtons();
                guidanceLookAheadTime = 0.5;
                btnContourLock.Image = Resources.ColorUnlocked;
                ct.isLocked = false;

                // Ocultar btnTrack mientras Contour está activo — si lo dejamos
                // visible el operario puede tocarlo y entra en estado inválido.
                btnTrack.Enabled = false;
                btnTrack.Visible = false;
            }

            else
            {
                EnableYouTurnButtons();
                ABLine.isABValid = false;
                curve.isCurveValid = false;
                ct.isLocked = false;
                guidanceLookAheadTime = Properties.Settings.Default.setAS_guidanceLookAheadTime;
                btnContourLock.Image = Resources.ColorUnlocked;
                if (isBtnAutoSteerOn)
                {
                    btnAutoSteer.PerformClick();
                    TimedMessageBox(2000, gStr.gsGuidanceStopped, gStr.gsContourOn);
                }

                btnTrack.Enabled = true;
                btnTrack.Visible = true;
            }

            PanelUpdateRightAndBottom();
        }

        private void btnContourLock_Click(object sender, EventArgs e)
        {
            if (ct.isContourBtnOn)
            {
                ct.SetLockToLine();
            }
        }
        public void SetContourLockImage(bool isOn)
        {
            btnContourLock.Image = isOn ? Resources.ColorLocked : Resources.ColorUnlocked;
        }
        private void btnTrack_Click(object sender, EventArgs e)
        {
            //if contour is on, turn it off
            if (ct.isContourBtnOn) { if (ct.isContourBtnOn) btnContour.PerformClick(); }

            if (trk.gArr.Count > 0)
            {
                if (trk.idx == -1)
                {
                    //find index of first visible track
                    trk.idx = trk.gArr.FindIndex(track => track.isVisible);

                    //otherwise default to index 0
                    if (trk.idx == -1) trk.idx = 0;

                    EnableYouTurnButtons();
                    PanelUpdateRightAndBottom();
                    twoSecondCounter = 100;
                    return;
                }

                EnableYouTurnButtons();
                twoSecondCounter = 100;
            }

            if (flp1.Visible)
            {
                flp1.Visible = false;
            }
            else
            {
                flp1.Visible = true;

                //build the flyout based on properties of program
                int tracksTotal = 0, tracksVisible = 0;
                bool isBnd = bnd.bndList.Count > 0;

                for (int i = 0; i < trk.gArr.Count; i++)
                {
                    tracksTotal++;
                    if (trk.gArr[i].isVisible)
                    {
                        tracksVisible++;
                    }
                }

                int btnCount = 0;
                //nudge closest
                flp1.Controls[0].Visible = tracksVisible > 0;

                //always these 3 - Build and if a bnd then ABDraw
                flp1.Controls[1].Visible = isBnd;

                flp1.Controls[2].Visible = true;
                flp1.Controls[3].Visible = true;

                //auto snap to pivot
                flp1.Controls[4].Visible = tracksVisible > 0;

                //off button
                flp1.Controls[5].Visible = tracksVisible > 0;

                //ref nudge
                flp1.Controls[6].Visible = tracksVisible > 0;

                for (int i = 0; i < flp1.Controls.Count; i++)
                {
                    if (flp1.Controls[i].Visible) btnCount++;
                }

                //position of panel
                flp1.Top = this.Height - 120 - (btnCount * 75);
                flp1.Left = this.Width - 120 - flp1.Width;
                trackMethodPanelCounter = 4;
            }

            PanelUpdateRightAndBottom();
        }
        private void btnAutoSteer_Click(object sender, EventArgs e)
        {
            longAvgPivDistance = 0;

            if (!timerSim.Enabled)
            {
                if (avgSpeed > vehicle.maxSteerSpeed)
                {
                    if (isBtnAutoSteerOn)
                    {
                        isBtnAutoSteerOn = false;
                        btnAutoSteer.Image = trk.isAutoSnapToPivot ? Properties.Resources.AutoSteerOffSnapToPivot : Properties.Resources.AutoSteerOff;
                        //if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();
                        if (sounds.isSteerSoundOn) sounds.sndAutoSteerOff.Play();
                    }

                    Log.EventWriter("Steer Off, Above Max Safe Speed for Autosteer");

                    if (isMetric)
                        TimedMessageBox(3000, "AutoSteer Disabled", "Above Maximum Safe Steering Speed: " + vehicle.maxSteerSpeed.ToString("N0") + " Kmh");
                    else
                        TimedMessageBox(3000, "AutoSteer Disabled", "Above Maximum Safe Steering Speed: " + Speed.KmhToMph(vehicle.maxSteerSpeed).ToString("N1") + " MPH");

                    return;
                }
            }

            if (isBtnAutoSteerOn)
            {
                isBtnAutoSteerOn = false;
                btnAutoSteer.Image = trk.isAutoSnapToPivot ? Properties.Resources.AutoSteerOffSnapToPivot : Properties.Resources.AutoSteerOff;
                //if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();
                if (sounds.isSteerSoundOn) sounds.sndAutoSteerOff.Play();
            }
            else
            {
                if (ct.isContourBtnOn | trk.idx > -1)
                {
                    isBtnAutoSteerOn = true;
                    btnAutoSteer.Image = trk.isAutoSnapToPivot ? Properties.Resources.AutoSteerOnSnapToPivot : Properties.Resources.AutoSteerOn;
                    if (sounds.isSteerSoundOn) sounds.sndAutoSteerOn.Play();

                    //redraw uturn if btn enabled.
                    if (yt.isYouTurnBtnOn)
                    {
                        yt.ResetYouTurn();
                    }
                }
                else
                {
                    TimedMessageBox(2000, (gStr.gsNoGuidanceLines), (gStr.gsTurnOnContourOrMakeABLine));
                }
            }
        }

        private void btnAutoYouTurn_Click(object sender, EventArgs e)
        {
            yt.isTurnCreationTooClose = false;

            if (bnd.bndList.Count == 0)
            {
                TimedMessageBox(2000, gStr.gsNoBoundary, gStr.gsCreateABoundaryFirst);
                Log.EventWriter("Uturn attempted without boundary");
                return;
            }

            yt.turnTooCloseTrigger = false;

            if (!yt.isYouTurnBtnOn)
            {
                //new direction so reset where to put turn diagnostic
                yt.ResetCreatedYouTurn();

                if (trk.idx == -1) return;

                yt.isYouTurnBtnOn = true;
                yt.isTurnCreationTooClose = false;
                yt.isTurnCreationNotCrossingError = false;
                yt.ResetYouTurn();
                btnAutoYouTurn.Image = Properties.Resources.Youturn80;
            }
            else
            {
                yt.isYouTurnBtnOn = false;
                //yt.rowSkipsWidth = Properties.Settings.Default.set_youSkipWidth;
                //yt.Set_Alternate_skips();

                btnAutoYouTurn.Image = Properties.Resources.YouTurnNo;

                // If a turn was already triggered, restore the original path before resetting
                yt.RestorePreTriggerState();

                yt.ResetYouTurn();

                //new direction so reset where to put turn diagnostic
                yt.ResetCreatedYouTurn();
            }
        }
        private void btnCycleLines_Click(object sender, EventArgs e)
        {
            trk.isAutoTrack = false;
            btnAutoTrack.Image = Resources.AutoTrackOff;

            if (trk.gArr.Count > 1)
            {
                while (true)
                {
                    trk.idx++;
                    if (trk.idx == trk.gArr.Count) trk.idx = 0;

                    if (trk.gArr[trk.idx].isVisible)
                    {
                        guideLineCounter = 20;
                        lblGuidanceLine.Visible = true;
                        lblGuidanceLine.Text = trk.gArr[trk.idx].name;
                        break;
                    }
                }

                if (isBtnAutoSteerOn)
                {
                    btnAutoSteer.PerformClick();
                    TimedMessageBox(2000, gStr.gsGuidanceStopped, "Track Changed");
                }

                if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();

                lblNumCu.Text = (trk.idx + 1).ToString() + "/" + trk.gArr.Count.ToString();
            }

            twoSecondCounter = 100;

            ABLine.isABValid = false;
            curve.isCurveValid = false;
        }

        private void btnCycleLinesBk_Click(object sender, EventArgs e)
        {
            if (ct.isContourBtnOn)
            {
                ct.SetLockToLine();
                return;
            }

            trk.isAutoTrack = false;
            btnAutoTrack.Image = Resources.AutoTrackOff;

            if (trk.gArr.Count > 1)
            {
                while (true)
                {
                    trk.idx--;
                    if (trk.idx == -1) trk.idx = trk.gArr.Count - 1;

                    if (trk.gArr[trk.idx].isVisible)
                    {
                        guideLineCounter = 20;
                        lblGuidanceLine.Visible = true;
                        lblGuidanceLine.Text = trk.gArr[trk.idx].name;
                        break;
                    }
                }

                if (isBtnAutoSteerOn)
                {
                    btnAutoSteer.PerformClick();
                    TimedMessageBox(2000, gStr.gsGuidanceStopped, "Track Changed");
                }

                lblNumCu.Text = (trk.idx + 1).ToString() + "/" + trk.gArr.Count.ToString();
            }

            ABLine.isABValid = false;
            curve.isCurveValid = false;

            twoSecondCounter = 100;
        }

        #endregion

        #region Track Flyout

        private void btnRefNudge_Click(object sender, EventArgs e)
        {
            if (trk.idx > -1)
            {
                // Mover guía migrado a HTML (pages/mover-guia.html, solapa Referencia).
                if (!LaunchAvaloniaWidget("pages/mover-guia.html?tab=ref", "float", "Mover guía", 480, 640))
                { OpenAgroParallelHub("pages/mover-guia.html?tab=ref"); }
            }
            else
            {
                TimedMessageBox(1500, gStr.gsNoABLineActive, gStr.gsPleaseEnterABLine);
                return;
            }
            if (flp1.Visible)
            {
                flp1.Visible = false;
            }

            panelRight.Visible = false;

            this.Activate();
        }
        private void btnTracksOff_Click(object sender, EventArgs e)
        {
            trk.idx = -1;

            if (flp1.Visible)
            {
                flp1.Visible = false;
            }
            PanelUpdateRightAndBottom();
        }
        private void btnNudge_Click(object sender, EventArgs e)
        {
            if (trk.idx > -1)
            {
                // Mover guía migrado a HTML (pages/mover-guia.html, solapa Guía).
                if (!LaunchAvaloniaWidget("pages/mover-guia.html", "float", "Mover guía", 480, 640))
                { OpenAgroParallelHub("pages/mover-guia.html"); }
            }
            else
            {
                TimedMessageBox(1500, gStr.gsNoABLineActive, gStr.gsPleaseEnterABLine);
                return;
            }

            if (flp1.Visible)
            {
                flp1.Visible = false;
            }

            this.Activate();

        }
        private void btnBuildTracks_Click(object sender, EventArgs e)
        {
            //if contour is on, turn it off
            if (ct.isContourBtnOn) { if (ct.isContourBtnOn) btnContour.PerformClick(); }

            //check if window already exists
            Form fc = Application.OpenForms["FormBuildTracks"];

            if (fc != null)
            {
                fc.Focus();
                return;
            }

            Form form = new FormBuildTracks(this);
            form.Show(this);

            if (flp1.Visible)
            {
                flp1.Visible = false;
            }
        }

        private void btnPlusAB_Click(object sender, EventArgs e)
        {
            //if contour is on, turn it off
            if (ct.isContourBtnOn) { if (ct.isContourBtnOn) btnContour.PerformClick(); }

            // AB rápido migrado a HTML (pages/ab-rapido.html, ex FormQuickAB).
            if (!LaunchAvaloniaWidget("pages/ab-rapido.html", "float", "AB rápido", 382, 269))
            { OpenAgroParallelHub("pages/ab-rapido.html"); }

            if (flp1.Visible)
            {
                flp1.Visible = false;
            }
        }

        private void btnABDraw_Click(object sender, EventArgs e)
        {
            if (bnd.bndList.Count == 0)
            {
                TimedMessageBox(2000, gStr.gsNoBoundary, gStr.gsCreateABoundaryFirst);
                return;
            }

            if (ct.isContourBtnOn) { if (ct.isContourBtnOn) btnContour.PerformClick(); }

            if (flp1.Visible)
            {
                flp1.Visible = false;
            }

            using (var form = new FormABDraw(this))
            {
                form.ShowDialog(this);
            }

            PanelUpdateRightAndBottom();
        }
        private void cboxAutoSnapToPivot_Click(object sender, EventArgs e)
        {
            trk.isAutoSnapToPivot = cboxAutoSnapToPivot.Checked;
            trackMethodPanelCounter = 1;

            //show the correct icon variant on the AutoSteer button
            if (isBtnAutoSteerOn)
                btnAutoSteer.Image = trk.isAutoSnapToPivot ? Properties.Resources.AutoSteerOnSnapToPivot : Properties.Resources.AutoSteerOn;
            else
                btnAutoSteer.Image = trk.isAutoSnapToPivot ? Properties.Resources.AutoSteerOffSnapToPivot : Properties.Resources.AutoSteerOff;
        }
        #endregion

        #region Field Menu
        private void toolStripBtnFieldTools_Click(object sender, EventArgs e)
        {
            headlandToolStripMenuItem.Enabled = (bnd.bndList.Count > 0);
            headlandBuildToolStripMenuItem.Enabled = (bnd.bndList.Count > 0);

            tramLinesMenuField.Enabled = tramsMultiMenuField.Enabled = (trk.gArr.Count > 0 && trk.idx > -1);
        }

        public bool isCancelJobMenu;

        private void btnJobMenu_Click(object sender, EventArgs e)
        {
            // Remember current state before opening the Job dialog
            var wasJobStarted = isJobStarted;
            var prevFieldDir = currentFieldDirectory;

            Form f = Application.OpenForms["FormGPSData"];
            if (f != null)

            {
                f.Focus();
                f.Close();
            }

            f = Application.OpenForms["FormFieldData"];
            if (f != null)

            {
                f.Focus();
                f.Close();
            }

            f = Application.OpenForms["FormEventViewer"];
            if (f != null)
            {
                f.Focus();
                f.Close();
            }

            f = Application.OpenForms["FormPan"];
            if (f != null)
            {
                isPanFormVisible = false;
                f.Focus();
                f.Close();
            }

            // PilotX: el Hub WebView y los widgets web (barra rápida de guías,
            // cámaras, etc.) son OwnedForms PERMANENTES — con el guard original
            // el menú de lote quedaba bloqueado siempre ("cerrá las ventanas").
            // Solo bloquean las ventanas nativas de AOG que hayan quedado abiertas.
            var blockingForms = this.OwnedForms
                .Where(of => !(of is global::AgroParallel.Shell.FormAgroParallelHubWebView2))
                .ToArray();
            if (blockingForms.Length > 0)
            {
                TimedMessageBox(2000, gStr.gsWindowsStillOpen, gStr.gsCloseAllWindowsFirst);
                return;
            }

            using (var form = new FormJob(this))
            {
                var result = form.ShowDialog(this);

                if (isCancelJobMenu)
                {
                    isCancelJobMenu = false;
                    return;
                }

                if (isJobStarted)
                {
                    if (autoBtnState == btnStates.Auto) btnSectionMasterAuto.PerformClick();
                    if (manualBtnState == btnStates.On) btnSectionMasterManual.PerformClick();
                }

                if (result == DialogResult.Yes)
                {
                    using (var form2 = new FormFieldDir(this)) { form2.ShowDialog(this); }
                }
                else if (result == DialogResult.No)
                {
                    using (var form2 = new FormFieldKML(this)) { form2.ShowDialog(this); }
                }
                else if (result == DialogResult.Retry)
                {
                    using (var form2 = new FormFieldExisting(this)) { form2.ShowDialog(this); }
                }
                else if (result == DialogResult.Abort)
                {
                    using (var form2 = new FormFieldIsoXml(this)) { form2.ShowDialog(this); }
                }

                // ---- Only log "Opened" if a field was newly opened or changed ----
                bool openedNewOrChanged =
                    isJobStarted &&
                    (!wasJobStarted ||
                     !string.Equals(currentFieldDirectory, prevFieldDir, StringComparison.OrdinalIgnoreCase));

                if (openedNewOrChanged)
                {
                    double distance = AppModel.CurrentLatLon.DistanceInKiloMeters(AppModel.LocalPlane.Origin);
                    if (distance > 10)
                    {
                        TimedMessageBox(2500, "High Field Start Distance Warning",
                            "Field Start is " + distance.ToString("N1") + " km From current position");
                        Log.EventWriter("High Field Start Distance Warning");
                    }

                    Log.EventWriter("** Opened **  " + currentFieldDirectory + "   " +
                        DateTime.Now.ToString("f", CultureInfo.InvariantCulture));

                    Settings.Default.setF_CurrentDir = currentFieldDirectory;
                    Settings.Default.Save();
                }
            }

            FieldMenuButtonEnableDisable(isJobStarted);
            toolStripBtnFieldTools.Enabled = isJobStarted;
            bnd.isHeadlandOn = (bnd.bndList.Count > 0 && bnd.bndList[0].hdLine.Count > 0);
            trk.idx = -1;
            PanelUpdateRightAndBottom();
        }

        public async Task FileSaveEverythingBeforeClosingField()
        {
            // Save the current field data before closing
            if (ct.isContourOn) ct.StopContourLine();

            if (autoBtnState == btnStates.Auto)
                btnSectionMasterAuto.PerformClick();

            if (manualBtnState == btnStates.On)
                btnSectionMasterManual.PerformClick();

            for (int j = 0; j < tool.numOfSections; j++)
            {
                section[j].sectionOnOffCycle = false;
                section[j].sectionOffRequest = false;
            }

            for (int j = 0; j < triStrip.Count; j++)
            {
                if (triStrip[j].isDrawing) triStrip[j].TurnMappingOff();
            }

            // Save field data with individual exception handling for each operation
            await Task.Run(() =>
            {
                try { FileSaveBoundary(); }
                catch (Exception ex) { Log.EventWriter($"CRITICAL: Boundary save failed: {ex}"); throw; }

                try { FileSaveSections(); }
                catch (Exception ex) { Log.EventWriter($"CRITICAL: Sections save failed: {ex}"); throw; }

                try { FileSaveContour(); }
                catch (Exception ex) { Log.EventWriter($"CRITICAL: Contour save failed: {ex}"); throw; }

                try { FileSaveTracks(); }
                catch (Exception ex) { Log.EventWriter($"CRITICAL: Tracks save failed: {ex}"); throw; }

                try { ExportFieldAs_KML(); }
                catch (Exception ex) { Log.EventWriter($"WARNING: KML export failed: {ex}"); }

                //ExportFieldAs_ISOXMLv3(); NOTE: This is very very slow, commented out until we have a field exporter

                try { ExportFieldAs_ISOXMLv4(); }
                catch (Exception ex) { Log.EventWriter($"WARNING: ISOXML export failed: {ex}"); }
            });

            Log.EventWriter("** Field closed **   " + currentFieldDirectory + "   " +
                DateTime.Now.ToString("f", CultureInfo.InvariantCulture));

            this.Invoke((MethodInvoker)(() =>
            {
                panelRight.Enabled = false;
                FieldMenuButtonEnableDisable(false);
                JobClose();
                Text = "PilotX · Agro Parallel";
            }));
        }

        private void tramLinesMenuField_Click(object sender, EventArgs e)
        {
            if (ct.isContourBtnOn) btnContour.PerformClick();

            if (trk.idx == -1)
            {
                TimedMessageBox(1500, gStr.gsNoABLineActive, gStr.gsPleaseEnterABLine);
                panelRight.Enabled = true;
                return;
            }

            // Tramlines simples migrado a HTML (pages/tramline.html). El editor
            // avanzado (FormTramLine, corte por mouse) queda para una fase 2.
            if (!LaunchAvaloniaWidget("pages/tramline.html", "float", "Tramlines", 560, 720))
            { OpenAgroParallelHub("pages/tramline.html"); }
            this.Activate();
        }

        private void tramLinesMenuMulti_Click(object sender, EventArgs e)
        {
            if (ct.isContourBtnOn) btnContour.PerformClick();

            if (trk.gArr.Count < 1)
            {
                TimedMessageBox(1500, gStr.gsNoGuidanceLines, gStr.gsNoGuidanceLines);
                panelRight.Enabled = true;
                return;
            }
            if (bnd.bndList.Count < 1)
            {
                TimedMessageBox(1500, gStr.gsNoBoundary, gStr.gsCreateABoundaryFirst);
                panelRight.Enabled = true;
                return;
            }

            Form form99 = new FormTramLine(this);
            form99.ShowDialog(this);
        }

        public void GetHeadland()
        {
            using (var form = new FormHeadLine(this))
            {
                form.ShowDialog(this);
            }

            bnd.isHeadlandOn = (bnd.bndList.Count > 0 && bnd.bndList[0].hdLine.Count > 0);

            PanelsAndOGLSize();
            PanelUpdateRightAndBottom();
            SetZoom();
        }
        private void headlandToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (bnd.bndList.Count == 0)
            {
                TimedMessageBox(2000, gStr.gsNoBoundary, gStr.gsCreateABoundaryFirst);
                return;
            }

            // Cabecera migrada a HTML (pages/cabecera.html, flujo Build Around).
            // FormHeadLine se mantiene para el reshape manual (fase 2).
            if (!LaunchAvaloniaWidget("pages/cabecera.html", "float", "Cabecera", 1000, 720))
            { OpenAgroParallelHub("pages/cabecera.html"); }
            this.Activate();
        }
        private void headlandBuildToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (bnd.bndList.Count == 0)
            {
                TimedMessageBox(2000, gStr.gsNoBoundary, gStr.gsCreateABoundaryFirst);
                return;
            }

            using (var form = new FormHeadAche(this))
            {
                form.ShowDialog(this);
            }

            bnd.isHeadlandOn = (bnd.bndList.Count > 0 && bnd.bndList[0].hdLine.Count > 0);

            PanelsAndOGLSize();
            PanelUpdateRightAndBottom();
            SetZoom();
        }
        private void boundariesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isJobStarted) return;

            using (var boundaryForm = new FormBoundary(this))
            {
                var result = boundaryForm.ShowDialog(this);

                if (result == DialogResult.OK)
                {
                    var boundaryPlayer = new FormBoundaryPlayer(this);
                    boundaryPlayer.FormClosed += (s, args) => toolStripBtnFieldTools.Enabled = true;
                    toolStripBtnFieldTools.Enabled = false;
                    boundaryPlayer.Show(this);
                }
                else if (result == DialogResult.Yes)
                {
                    new FormMap(this).Show(this);
                }
            }

            PanelUpdateRightAndBottom();
        }

        #endregion

        #region Recorded Path
        private void btnPathGoStop_Click(object sender, EventArgs e)
        {
            #region Turn off Guidance
            //if contour is on, turn it off
            if (ct.isContourBtnOn) { if (ct.isContourBtnOn) btnContour.PerformClick(); }
            //btnContourPriority.Enabled = true;

            if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();
            if (isBtnAutoSteerOn)
            {
                btnAutoSteer.PerformClick();
                TimedMessageBox(2000, gStr.gsGuidanceStopped, "Paths Enabled");
                Log.EventWriter("Autosteer On While Enable Paths");
            }

            DisableYouTurnButtons();

            if (trk.idx > -1)
            {
                trk.idx = -1;
            }

            PanelUpdateRightAndBottom();

            #endregion

            //already running?
            if (recPath.isDrivingRecordedPath)
            {
                recPath.StopDrivingRecordedPath();
                btnPathGoStop.Image = Properties.Resources.boundaryPlay;
                btnPathRecordStop.Enabled = true;
                btnPickPath.Enabled = true;
                btnResumePath.Enabled = true;
                return;
            }

            //start the recorded path driving process
            if (!recPath.StartDrivingRecordedPath())
            {
                //Cancel the recPath - something went seriously wrong
                recPath.StopDrivingRecordedPath();
                TimedMessageBox(1500, gStr.gsProblemMakingPath, gStr.gsCouldntGenerateValidPath);
                btnPathGoStop.Image = Properties.Resources.boundaryPlay;
                btnPathRecordStop.Enabled = true;
                btnPickPath.Enabled = true;
                btnResumePath.Enabled = true;
                return;
            }
            else
            {
                btnPathGoStop.Image = Properties.Resources.boundaryStop;
                btnPathRecordStop.Enabled = false;
                btnPickPath.Enabled = false;
                btnResumePath.Enabled = false;
            }
        }
        private void btnPathRecordStop_Click(object sender, EventArgs e)
        {
            if (recPath.isRecordOn)
            {
                recPath.isRecordOn = false;
                btnPathRecordStop.Image = Properties.Resources.BoundaryRecord;
                btnPathGoStop.Enabled = true;
                btnPickPath.Enabled = true;
                btnResumePath.Enabled = true;

                using (var form = new FormRecordName(this))
                {
                    form.ShowDialog(this);
                    if (form.DialogResult == DialogResult.OK)
                    {
                        String filename = form.filename + ".rec";
                        FileSaveRecPath();
                        FileSaveRecPath(filename);
                    }
                    else
                    {
                        recPath.recList.Clear();
                    }
                }
            }
            else if (isJobStarted)
            {
                recPath.recList.Clear();
                recPath.isRecordOn = true;
                btnPathRecordStop.Image = Properties.Resources.boundaryStop;
                btnPathGoStop.Enabled = false;
                btnPickPath.Enabled = false;
                btnResumePath.Enabled = false;
            }
        }
        private void btnResumePath_Click(object sender, EventArgs e)
        {
            if (recPath.resumeState == 0)
            {
                recPath.resumeState++;
                btnResumePath.Image = Properties.Resources.pathResumeLast;
                TimedMessageBox(1500, "Resume Style", "Last Stopped Position");
            }

            else if (recPath.resumeState == 1)
            {
                recPath.resumeState++;
                btnResumePath.Image = Properties.Resources.pathResumeClose;
                TimedMessageBox(1500, "Resume Style", "Closest Point");
            }
            else
            {
                recPath.resumeState = 0;
                btnResumePath.Image = Properties.Resources.pathResumeStart;
                TimedMessageBox(1500, "Resume Style", "Start At Beginning");
            }
        }
        private void btnSwapABRecordedPath_Click(object sender, EventArgs e)
        {
            int cnt = recPath.recList.Count;
            List<CRecPathPt> _recList = new List<CRecPathPt>();

            for (int i = cnt - 1; i > -1; i--)
            {
                recPath.recList[i].heading += (glm.PIBy2) + (glm.PIBy2);
                if (recPath.recList[i].heading < -glm.twoPI) recPath.recList[i].heading += glm.twoPI;

                _recList.Add(recPath.recList[i]);
            }
            recPath.recList.Clear();
            for (int i = 0; i < cnt; i++)
            {
                recPath.recList.Add(_recList[i]);
            }
        }
        private void btnPickPath_Click(object sender, EventArgs e)
        {
            recPath.resumeState = 0;
            btnResumePath.Image = Properties.Resources.pathResumeStart;
            recPath.currentPositonIndex = 0;

            using (FormRecordPicker form = new FormRecordPicker(this))
            {
                //returns full field.txt file dir name
                if (form.ShowDialog(this) == DialogResult.Yes)
                {
                }
            }
        }
        private void recordedPathStripMenu_Click(object sender, EventArgs e)
        {
            recPath.resumeState = 0;
            btnResumePath.Image = Properties.Resources.pathResumeStart;
            recPath.currentPositonIndex = 0;

            if (isJobStarted)
            {
                if (panelDrag.Visible)
                {
                    panelDrag.Visible = false;
                    recPath.recList.Clear();
                    recPath.StopDrivingRecordedPath();
                }
                else
                {
                    FileLoadRecPath();
                    panelDrag.Visible = true;
                }
            }
            else
            {
                TimedMessageBox(3000, gStr.gsFieldNotOpen, gStr.gsStartNewField);
            }
        }

        private void copyTracksToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (isJobStarted)
            {
                using (Forms.Field.FormCopyTracks form = new Forms.Field.FormCopyTracks(this))
                {
                    form.ShowDialog(this);
                }
            }
            else
            {
                TimedMessageBox(3000, gStr.gsFieldNotOpen, gStr.gsStartNewField);
            }
        }

        #endregion

        #region Left Panel Menu
        private void steerWizardMenuItem_Click(object sender, EventArgs e)
        {
            Form fcs = Application.OpenForms["FormSteer"];

            if (fcs != null)
            {
                fcs.Focus();
                fcs.Close();
            }

            //check if window already exists
            Form fc = Application.OpenForms["FormSteerWiz"];

            if (fc != null)
            {
                fc.Focus();
                //fc.Close();
                return;
            }

            //
            Form form = new FormSteerWiz(this);
            form.Show(this);

        }
        private void toolStripDropDownButtonDistance_Click(object sender, EventArgs e)
        {
            fd.distanceUser = 0;
        }
        private void btnNavigationSettings_Click(object sender, EventArgs e)
        {
            //buttonPanelCounter = 0;
            Form f = Application.OpenForms["FormGPSData"];

            if (f != null)
            {
                f.Focus();
                f.Close();
            }

            Form f1 = Application.OpenForms["FormFieldData"];

            if (f1 != null)
            {
                f1.Focus();
                f1.Close();
            }

            panelNavigation.Location = new System.Drawing.Point(90, 100);

            if (panelNavigation.Visible)
            {
                panelNavigation.Visible = false;
            }
            else
            {
                panelNavigation.Visible = true;
                navPanelCounter = 2;
                if (displayBrightness.IsSupported) btnBrightnessDn.Text = (displayBrightness.GetBrightness().ToString()) + "%";
                else btnBrightnessDn.Text = "??";
            }

            if (isJobStarted) btnGrid.Enabled = true;
            else btnGrid.Enabled = false;

        }
        private void btnStartAgIO_Click(object sender, EventArgs e)
        {
            Log.EventWriter("CoreX abierto desde el menú");

            // CoreX corre headless (sin ventana): su panel es el dashboard web
            // en 127.0.0.1:5181, que abrimos dentro del shell del Hub (overlay
            // sobre el mapa; la página trae sus botones Hub/Cerrar).
            Process[] processName = Process.GetProcessesByName("CoreX");
            bool justStarted = false;
            if (processName.Length == 0)
            {
                //Start application here
                string strPath = Path.Combine(Application.StartupPath, "CoreX.exe");

                try
                {
                    ProcessStartInfo processInfo = new ProcessStartInfo();
                    processInfo.FileName = strPath;
                    processInfo.WorkingDirectory = Path.GetDirectoryName(strPath);
                    Process proc = Process.Start(processInfo);
                    justStarted = true;
                }
                catch
                {
                    TimedMessageBox(2000, "No se encontró", "No se encuentra CoreX.exe");
                    Log.EventWriter("CoreX.exe no encontrado");
                    return;
                }
            }

            const string coreXUrl = "http://127.0.0.1:5181/";
            if (justStarted)
            {
                // Recién lanzado: darle ~2 s a EmbedIO para levantar el server
                // antes de navegar, si no el WebView muestra error de conexión.
                var t = new System.Windows.Forms.Timer { Interval = 2000 };
                t.Tick += (s2, e2) =>
                {
                    t.Stop(); t.Dispose();
                    OpenAgroParallelHub(coreXUrl);
                };
                t.Start();
            }
            else
            {
                OpenAgroParallelHub(coreXUrl);
            }
        }
        private void btnAutoSteerConfig_Click(object sender, EventArgs e)
        {
            //check if window already exists
            Form fc = Application.OpenForms["FormSteer"];

            if (fc != null)
            {
                fc.Focus();
                fc.Close();

                return;
            }

            //
            Form form = new FormSteer(this);
            //form.Top = 0;
            //form.Left = 0;
            form.Show(this);
            this.Activate();

        }
        private void btnConfig_Click(object sender, EventArgs e)
        {
            // AgroParallel: la Configuración ahora es HTML (pages/config.html)
            FloatMenuOpenConfig("tabSummary");
        }

        #endregion

        #region Flags
        private void toolStripMenuItemFlagRed_Click(object sender, EventArgs e)
        {
            flagColor = 0;
            btnFlag.Image = Properties.Resources.FlagRed;
        }
        private void toolStripMenuGrn_Click(object sender, EventArgs e)
        {
            flagColor = 1;
            btnFlag.Image = Properties.Resources.FlagGrn;
        }
        private void toolStripMenuYel_Click(object sender, EventArgs e)
        {
            flagColor = 2;
            btnFlag.Image = Properties.Resources.FlagYel;
        }
        private void toolStripMenuFlagForm_Click(object sender, EventArgs e)
        {
            // Banderas migrado a HTML (pages/banderas.html, ex FormFlags).
            if (flagPts.Count > 0)
            {
                flagNumberPicked = 1;
                OpenFlagsWidget();
            }
        }

        // Abre el widget de banderas (reemplazo de FormFlags/FormEnterFlag).
        private void OpenFlagsWidget(bool addPane = false)
        {
            string page = addPane ? "pages/banderas.html?add=1" : "pages/banderas.html";
            if (!LaunchAvaloniaWidget(page, "float", "Banderas", 382, 320))
            {
                OpenAgroParallelHub(page);
            }
        }
        private void btnFlag_Click(object sender, EventArgs e)
        {
            int nextflag = flagPts.Count + 1;
            CFlag flagPt = new CFlag(
                AppModel.CurrentLatLon.Latitude,
                AppModel.CurrentLatLon.Longitude,
                pn.fix.easting, pn.fix.northing,
                fixHeading, flagColor, nextflag, nextflag.ToString());
            flagPts.Add(flagPt);
            flagPts = FlagsFiles.DeduplicateFlags(flagPts);
            FileSaveFlags();

            if (flagPts.Count > 0)
            {
                flagNumberPicked = nextflag;
                OpenFlagsWidget();
            }
        }

        private void btnSnapToPivot_Click(object sender, EventArgs e)
        {
            trk.SnapToPivot();
        }

        private void btnAdjRight_Click(object sender, EventArgs e)
        {
            trk.NudgeTrack(Properties.Settings.Default.setAS_snapDistance * 0.01);
        }

        private void btnAdjLeft_Click(object sender, EventArgs e)
        {
            trk.NudgeTrack(-Properties.Settings.Default.setAS_snapDistance * 0.01);
        }

        #endregion

        #region Top Panel
        private void btnFieldStats_Click(object sender, EventArgs e)
        {
            // AgroParallel: el popup táctil reemplaza a FormFieldData. Se abre
            // como WIDGET chico flotante (ventana independiente encima del mapa,
            // X nativa), no como página dentro del Hub. Así el operario sigue
            // viendo la pantalla principal y el widget queda flotando al lado.
            // Preferimos el widget Avalonia si está presente; si no (caso normal,
            // spike no shippeado), abrimos un widget flotante WebView2.
            if (!isJobStarted) return;

            if (!LaunchAvaloniaWidget("pages/datos-lote.html", "float",
                                      "Datos del lote", 520, 620))
            {
                OpenAgroParallelWidget("pages/datos-lote.html", "Datos del lote", 520, 620);
            }
        }

        private void btnGPSData_Click(object sender, EventArgs e)
        {
            // AgroParallel: el popup táctil reemplaza a FormGPSData. Idem
            // btnFieldStats: widget Avalonia draggable. Si no hay exe Avalonia,
            // fallback al Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/datos-gps.html", "float",
                                      "Antena GPS", 680, 720))
            {
                OpenAgroParallelHub("pages/datos-gps.html");
            }
        }
        private void btnShutdown_Click(object sender, EventArgs e)
        {
            Form f = Application.OpenForms["FormGPSData"];

            if (f != null)
            {
                f.Focus();
                f.Close();
            }

            f = null;
            f = Application.OpenForms["FormFieldData"];

            if (f != null)
            {
                f.Focus();
                f.Close();
            }

            f = null;
            f = Application.OpenForms["FormEventViewer"];

            if (f != null)
            {
                f.Focus();
                f.Close();
            }

            f = null;
            f = Application.OpenForms["FormPan"];

            if (f != null)
            {
                isPanFormVisible = false;
                f.Focus();
                f.Close();
            }

            Close();
        }
        private void btnMinimizeMainForm_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }
        private void btnMaximizeMainForm_Click(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Maximized)
                this.WindowState = FormWindowState.Normal;
            else this.WindowState = FormWindowState.Maximized;

            FormGPS_ResizeEnd(this, e);
        }
        private void lblCurrentField_Click(object sender, EventArgs e)
        {
            isPauseFieldTextCounter = !isPauseFieldTextCounter;
            if (isPauseFieldTextCounter)
            {
                //lblCurrentField.Text = "\u23F8";
                fourSecondCounter = 4;
            }
            else
            {
                fourSecondCounter = 4;
            }
        }

        #endregion

        #region File Menu

        //File drop down items

        private void flagByLatLonToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Bandera por lat/lon migrado a HTML (banderas.html?add=1, ex FormEnterFlag).
            OpenFlagsWidget(addPane: true);
        }
        private void setWorkingDirectoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (isJobStarted)
            {
                // Show timed message if a job is still open
                TimedMessageBox(2000, gStr.gsFieldIsOpen, gStr.gsCloseFieldFirst);
                return;
            }

            FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                ShowNewFolderButton = true,
                Description = "Currently: " + RegistrySettings.workingDirectory
            };

            if (RegistrySettings.workingDirectory == RegistrySettings.defaultString)
                fbd.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            else
                fbd.SelectedPath = RegistrySettings.workingDirectory;

            if (fbd.ShowDialog(this) == DialogResult.OK)
            {
                // Save new working directory to registry
                RegistrySettings.Save(RegKeys.workingDirectory, fbd.SelectedPath);

                // Inform user that app needs to restart
                FormDialog.Show("Restart Required",
                    gStr.gsProgramWillExitPleaseRestart,
                    DialogSeverity.Info);

                // Close the app
                Close();
            }
        }

        private void enterSimCoordsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormSimCoords se reemplazó por la
            // página HTML /pages/sim-coords.html (reubicar el simulador a una lat/lon,
            // aplica por POST /api/aog/guidance/command sim_coords_<lat>_<lon>).
            // Widget Avalonia si existe; si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/sim-coords.html", "float",
                                      "Coordenadas del simulador", 560, 620))
            {
                OpenAgroParallelHub("pages/sim-coords.html");
            }
        }

        private void hotKeysToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var form = new Form_Keys(this))
            {
                form.ShowDialog(this);
            }
        }

        private void kioskModeToolStrip_Click(object sender, EventArgs e)
        {
            isKioskMode = !isKioskMode;

            if (isKioskMode)
            {
                kioskModeToolStrip.Checked = true;
                this.WindowState = FormWindowState.Maximized;
                isFullScreen = true;
                btnMaximizeMainForm.Visible = false;
                btnMinimizeMainForm.Visible = false;
                Settings.Default.setWindow_isKioskMode = true;
            }
            else
            {
                kioskModeToolStrip.Checked = false;
                btnMaximizeMainForm.Visible = true;
                btnMinimizeMainForm.Visible = true;
                Settings.Default.setWindow_isKioskMode = false;
            }

            Settings.Default.Save();
        }
        private void resetALLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (isJobStarted)
            {
                // Show message if field is still open
                FormDialog.Show("Warning", gStr.gsCloseFieldFirst, DialogSeverity.Warning);
            }
            else
            {
                // Ask user for confirmation before resetting everything
                DialogResult result = FormDialog.ShowQuestion(gStr.gsResetAll, gStr.gsReallyResetEverything);

                if (result == DialogResult.OK)
                {
                    // Reset registry settings
                    RegistrySettings.Reset();

                    // Notify user and close app
                    FormDialog.Show("Restart Required", gStr.gsProgramWillExitPleaseRestart, DialogSeverity.Info);
                    Close();
                }
            }
        }

        private void helpMenuItem_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormHelp (que linkeaba a la comunidad
            // upstream de AgOpenGPS) se reemplazó por la página HTML /pages/ayuda.html,
            // con contenido propio de PilotX: accesos a las utilidades internas del Hub
            // (asistente, vehículo, IMU, eventos, debug, sistema, actualizar, celular,
            // OrbitX) + "Acerca de" con la versión instalada. Widget Avalonia si existe;
            // si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/ayuda.html", "float",
                                      "Ayuda", 900, 680))
            {
                OpenAgroParallelHub("pages/ayuda.html");
            }
        }
        private void simulatorOnToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (isJobStarted)
            {
                TimedMessageBox(2000, gStr.gsFieldIsOpen, gStr.gsCloseFieldFirst);
                return;
            }
            if (simulatorOnToolStripMenuItem.Checked)
            {
                if (sentenceCounter < 299)
                {
                    TimedMessageBox(2000, "Connected", "GPS");
                    simulatorOnToolStripMenuItem.Checked = false;
                    return;
                }
            }

            timerSim.Enabled = panelSim.Visible = simulatorOnToolStripMenuItem.Checked;
            isFirstFixPositionSet = false;
            isGPSPositionInitialized = false;
            isFirstHeadingSet = false;
            startCounter = 0;

            Settings.Default.setMenu_isSimulatorOn = simulatorOnToolStripMenuItem.Checked;
            Settings.Default.Save();
        }
        private void colorsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormColor se reemplazó por la página
            // HTML /pages/colores.html (colores de marco/campo/texto para día y noche
            // + suavizado de cámara + modo día, aplica por POST
            // /api/aog/guidance/command display_colors_...). Widget Avalonia si existe;
            // si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/colores.html", "float",
                                      "Colores de pantalla", 720, 640))
            {
                OpenAgroParallelHub("pages/colores.html");
            }
        }
        private void colorsSectionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (tool.isSectionsNotZones)
            {
                // AgroParallel: la ventana WinForms FormColorSection se reemplazó por
                // la página HTML /pages/colores-secciones.html (16 colores de sección
                // + modo multicolor, aplica por POST /api/aog/guidance/command
                // sec_colors_<hex1>_..._<hex16>_<0|1>). Widget Avalonia si existe; si
                // no, Hub WebView2.
                if (!LaunchAvaloniaWidget("pages/colores-secciones.html", "float",
                                          "Colores de secciones", 700, 700))
                {
                    OpenAgroParallelHub("pages/colores-secciones.html");
                }
            }
            else
            {
                TimedMessageBox(2000, "Cannot use with zones", "Only for Sections");
            }
        }

        //Profiles
        private void newProfileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var form = new FormNewProfile(this))
            {
                form.ShowDialog(this);
            }
        }

        private void loadProfileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var form = new FormLoadProfile(this))
            {
                form.ShowDialog(this);
            }
        }

        #endregion

        #region Languages
        private void InitializeLanguages()
        {
            menustripLanguage.DropDownItems.Clear();
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Čeština (Czech)", "cs"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Dansk (Denmark)", "da"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Deutsch (Germany)", "de"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("English (Canada)", "en"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Eesti (Estonia)", "et"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Español (Spanish)", "es"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Français (France)", "fr"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Hrvatski (Croatia)", "hr"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Italiano (Italy)", "it"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Latviski (Latvia)", "lv"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Lietuvių (Lithuania)", "lt"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Magyar (Hungary)", "hu"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Nederlands (Holland)", "nl"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Norsk (Norway)", "no"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Polski (Poland)", "pl"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Português (Portuguese)", "pt"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Română (Romanian)", "ro"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("русский (Russia)", "ru"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Suomalainen (Finland)", "fi"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Slovenčina (Slovakia)", "sk"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Serbia (Servië)", "sr"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Svenska (Sweden)", "sv"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Türkçe (Turkey)", "tr"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("Yкраїнська (Ukraine)", "uk"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("中国人 (Chinese)", "zh-CHS"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("日本語 (Japanese)", "ja"));
            menustripLanguage.DropDownItems.Add(CreateLanguageMenuItem("한국인 (Korean)", "ko"));
        }

        private ToolStripMenuItem CreateLanguageMenuItem(string text, string lang)
        {
            var menuItem = new ToolStripMenuItem()
            {
                CheckOnClick = true,
                Text = text,
                Tag = lang
            };
            menuItem.Click += languageMenuItem_Click;
            return menuItem;
        }

        private void languageMenuItem_Click(object sender, EventArgs e)
        {
            var menuItem = (ToolStripMenuItem)sender;
            SetLanguage((string)menuItem.Tag);
        }

        private void SetLanguage(string lang)
        {
            foreach (var menuItem in menustripLanguage.DropDownItems.OfType<ToolStripMenuItem>())
            {
                menuItem.Checked = (string)menuItem.Tag == lang;
            }

            RegistrySettings.Save(RegKeys.language, lang);

            Thread.CurrentThread.CurrentCulture = new CultureInfo(lang);
            Thread.CurrentThread.CurrentUICulture = new CultureInfo(lang);

            LoadText();
        }

        #endregion

        #region Bottom Menu
        public void CloseTopMosts()
        {
            Form fc = Application.OpenForms["FormSteer"];
            if (fc != null)
            {
                fc.Focus();
                fc.Close();
            }
            fc = Application.OpenForms["FormSteerGraph"];
            if (fc != null)
            {
                fc.Focus();
                fc.Close();
            }
            fc = Application.OpenForms["FormGPSData"];
            if (fc != null)
            {
                fc.Focus();
                fc.Close();
            }
        }

        private void btnAutoTrack_Click(object sender, EventArgs e)
        {
            trk.isAutoTrack = !trk.isAutoTrack;
            btnAutoTrack.Image = trk.isAutoTrack ? Resources.AutoTrack : Resources.AutoTrackOff;
        }

        private void btnResetToolHeading_Click(object sender, EventArgs e)
        {
            tankPos.heading = fixHeading;
            tankPos.easting = hitchPos.easting + (Math.Sin(tankPos.heading) * (tool.tankTrailingHitchLength));
            tankPos.northing = hitchPos.northing + (Math.Cos(tankPos.heading) * (tool.tankTrailingHitchLength));

            toolPivotPos.heading = tankPos.heading;
            toolPivotPos.easting = tankPos.easting + (Math.Sin(toolPivotPos.heading) * (tool.trailingHitchLength));
            toolPivotPos.northing = tankPos.northing + (Math.Cos(toolPivotPos.heading) * (tool.trailingHitchLength));
        }

        private void btnTramDisplayMode_Click(object sender, EventArgs e)
        {
            tram.isLeftManualOn = false;
            tram.isRightManualOn = false;

            tram.displayMode = GetNextDisplayMode(tram.displayMode);
            btnTramDisplayMode.Image = TramModeBitmaps.Get(tram.displayMode);
        }

        private TramMode GetNextDisplayMode(TramMode currentMode)
        {
            TramMode nextMode;

            switch (currentMode)
            {
                case TramMode.None:
                    nextMode = TramMode.All;
                    break;
                case TramMode.All:
                    nextMode = TramMode.FillTracks;
                    break;
                case TramMode.FillTracks:
                    nextMode = TramMode.BoundaryTracks;
                    break;
                case TramMode.BoundaryTracks:
                    nextMode = TramMode.None;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(currentMode), "TramMode argument out of range");
            }
            // Skip inapplicable modes
            if (nextMode.IncludesBoundaryTracks() && tram.tramBndOuterArr.Count == 0)
            {
                nextMode = GetNextDisplayMode(nextMode);
            }
            if (nextMode.IncludesFillTracks() && tram.tramList.Count == 0)
            {
                nextMode = GetNextDisplayMode(nextMode);
            }
            return nextMode;
        }

        public bool isPatchesChangingColor = false;

        private void btnChangeMappingColor_Click(object sender, EventArgs e)
        {
            using (var form = new FormColorPicker(this, sectionColorDay))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    sectionColorDay = form.useThisColor;
                }
            }

            Settings.Default.setDisplay_colorSectionsDay = sectionColorDay;
            Settings.Default.Save();

            isPatchesChangingColor = true;
        }

        private void btnYouSkipEnable_Click(object sender, EventArgs e)
        {
            yt.rowSkipsWidth = Properties.Settings.Default.set_youSkipWidth;
            switch (yt.skipMode)
            {
                case SkipMode.Normal:
                    btnYouSkipEnable.Image = Resources.YouSkipOn;
                    yt.skipMode = SkipMode.Alternative;
                    //make sure at least 1
                    if (yt.rowSkipsWidth < 2)
                    {
                        yt.rowSkipsWidth = 2;
                        cboxpRowWidth.Text = "1";
                    }
                    yt.Set_Alternate_skips();
                    break;
                case SkipMode.Alternative:
                    btnYouSkipEnable.Image = Resources.YouSkipWorkedTracks;
                    yt.skipMode = SkipMode.IgnoreWorkedTracks;
                    //make sure at least 1
                    if (yt.rowSkipsWidth < 2)
                    {
                        yt.rowSkipsWidth = 2;
                        cboxpRowWidth.Text = "1";
                    }
                    break;
                case SkipMode.IgnoreWorkedTracks:
                    btnYouSkipEnable.Image = Resources.YouSkipOff;
                    yt.skipMode = SkipMode.Normal;
                    break;
            }
            yt.ResetCreatedYouTurn();
        }

        private void cboxpRowWidth_SelectedIndexChanged(object sender, EventArgs e)
        {
            yt.rowSkipsWidth = cboxpRowWidth.SelectedIndex + 1;
            yt.Set_Alternate_skips();
            if (!yt.isYouTurnTriggered) yt.ResetCreatedYouTurn();
            Properties.Settings.Default.set_youSkipWidth = yt.rowSkipsWidth;
            Properties.Settings.Default.Save();
        }

        private void btnHeadlandOnOff_Click(object sender, EventArgs e)
        {
            bnd.isHeadlandOn = !bnd.isHeadlandOn;
            if (bnd.isHeadlandOn)
            {
                btnHeadlandOnOff.Image = Properties.Resources.HeadlandOn;
            }
            else
            {
                btnHeadlandOnOff.Image = Properties.Resources.HeadlandOff;
            }

            if (vehicle.isHydLiftOn && !bnd.isHeadlandOn) vehicle.isHydLiftOn = false;

            if (!bnd.isHeadlandOn)
            {
                p_239.pgn[p_239.hydLift] = 0;
                btnHydLift.Image = Properties.Resources.HydraulicLiftOff;
            }

            PanelUpdateRightAndBottom();
        }
        private void cboxIsSectionControlled_Click(object sender, EventArgs e)
        {
            if (cboxIsSectionControlled.Checked) cboxIsSectionControlled.Image = Properties.Resources.HeadlandSectionOn;
            else cboxIsSectionControlled.Image = Properties.Resources.HeadlandSectionOff;
            bnd.isSectionControlledByHeadland = cboxIsSectionControlled.Checked;
            Properties.Settings.Default.setHeadland_isSectionControlled = cboxIsSectionControlled.Checked;
            Properties.Settings.Default.Save();
        }

        private void btnHydLift_Click(object sender, EventArgs e)
        {
            if (bnd.isHeadlandOn)
            {
                vehicle.isHydLiftOn = !vehicle.isHydLiftOn;
                if (vehicle.isHydLiftOn)
                {
                    btnHydLift.Image = Properties.Resources.HydraulicLiftOn;
                }
                else
                {
                    btnHydLift.Image = Properties.Resources.HydraulicLiftOff;
                    p_239.pgn[p_239.hydLift] = 0;
                }
            }
            else
            {
                p_239.pgn[p_239.hydLift] = 0;
                vehicle.isHydLiftOn = false;
                btnHydLift.Image = Properties.Resources.HydraulicLiftOff;
            }
        }

        #endregion

        #region Tools Menu

        private void allSettingsMenuItem_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormAllSettings se reemplazó por
            // la página HTML /pages/ajustes-todos.html (volcado solo-lectura de
            // ajustes + telemetría). Widget Avalonia si existe; si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/ajustes-todos.html", "float",
                                      "Todos los ajustes", 900, 720))
            {
                OpenAgroParallelHub("pages/ajustes-todos.html");
            }
        }
        private void boundaryToolToolStripMenu_Click(object sender, EventArgs e)
        {
            if (isJobStarted)
            {
                using (var form = new FormBndTool(this))
                {
                    form.ShowDialog(this);
                }
            }
        }
        private void SmoothABtoolStripMenu_Click(object sender, EventArgs e)
        {
            if (isJobStarted && trk.idx > -1)
            {
                if (!LaunchAvaloniaWidget("pages/suavizar-ab.html", "float", "Suavizar AB", 360, 300))
                { OpenAgroParallelHub("pages/suavizar-ab.html"); }
                this.Activate();
            }
            else
            {
                if (!isJobStarted) TimedMessageBox(2000, gStr.gsFieldNotOpen, gStr.gsStartNewField);
                else TimedMessageBox(2000, gStr.gsCurveNotOn, gStr.gsTurnABCurveOn);
            }
        }
        private void deleteContourPathsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //FileCreateContour();
            ct.stripList?.Clear();
            ct.ptList?.Clear();
            ct.ctList?.Clear();
            contourSaveList?.Clear();
        }
        private void toolStripAreYouSure_Click(object sender, EventArgs e)
        {
            if (isJobStarted)
            {
                if (autoBtnState == btnStates.Off && manualBtnState == btnStates.Off)
                {
                    DialogResult result = FormDialog.ShowQuestion(
                        gStr.gsDeleteAllContoursAndSections,
                        gStr.gsDeleteForSure);

                    if (result == DialogResult.OK)
                    {
                        //FileCreateElevation();

                        if (tool.isSectionsNotZones)
                        {
                            //Update the button colors and text
                            AllSectionsAndButtonsToState(btnStates.Off);

                            //enable disable manual buttons
                            LineUpIndividualSectionBtns();
                        }
                        else
                        {
                            AllZonesAndButtonsToState(btnStates.Off);
                            LineUpAllZoneButtons();
                        }

                        //turn manual button off
                        manualBtnState = btnStates.Off;
                        btnSectionMasterManual.Image = Properties.Resources.ManualOff;

                        //turn auto button off
                        autoBtnState = btnStates.Off;
                        btnSectionMasterAuto.Image = Properties.Resources.SectionMasterOff;


                        //clear out the contour Lists
                        ct.StopContourLine();
                        ct.ResetContour();
                        fd.workedAreaTotal = 0;
                        fd.workedAreaTotalUser = 0;
                        fd.distanceUser = 0;

                        //clear the section lists
                        for (int j = 0; j < triStrip.Count; j++)
                        {
                            //clean out the lists
                            triStrip[j].patchList?.Clear();
                            triStrip[j].triangleList?.Clear();
                        }
                        patchSaveList?.Clear();

                        //delete all worked Lanes too
                        foreach (CTrk TrackItem in trk.gArr)
                        {
                            TrackItem.workedTracks.Clear();
                        }

                        FileCreateContour();
                        FileCreateSections();


                        Log.EventWriter("All Section Mapping Deleted");
                        // COREX_FIELD_MOD_START
                        NotificarBorradoArea();
                        // COREX_FIELD_MOD_END
                    }
                    else
                    {
                        TimedMessageBox(1500, gStr.gsNothingDeleted, gStr.gsActionHasBeenCancelled);
                    }
                }
                else
                {
                    TimedMessageBox(1500, "Sections are on", "Turn Auto or Manual Off First");
                }
            }
        }
        private void headingChartToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormGraphHeading se reemplazó por la
            // página HTML /pages/grafico-rumbo.html (rumbo GPS vs IMU corregido en
            // vivo, solo lectura). Widget Avalonia si existe; si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/grafico-rumbo.html", "float",
                                      "Gráfico de rumbo", 900, 700))
            {
                OpenAgroParallelHub("pages/grafico-rumbo.html");
            }
        }
        private void toolStripAutoSteerChart_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormGraphSteer se reemplazó por la
            // página HTML /pages/grafico-direccion.html (ángulo de dirección real vs
            // seteado en vivo, solo lectura). Widget Avalonia si existe; si no, Hub.
            if (!LaunchAvaloniaWidget("pages/grafico-direccion.html", "float",
                                      "Gráfico de dirección", 900, 700))
            {
                OpenAgroParallelHub("pages/grafico-direccion.html");
            }
        }
        private void xTEChartToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormGraphXTE se reemplazó por la
            // página HTML /pages/grafico-xte.html (gráfico de guiado en vivo,
            // solo lectura). Widget Avalonia si existe; si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/grafico-xte.html", "float",
                                      "Gráfico XTE", 900, 700))
            {
                OpenAgroParallelHub("pages/grafico-xte.html");
            }
        }
        private void eventViewerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormEventViewer se reemplazó por
            // la página HTML /pages/eventos.html (visor solo-lectura del registro
            // de eventos). Widget Avalonia si existe; si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/eventos.html", "float",
                                      "Eventos", 900, 720))
            {
                OpenAgroParallelHub("pages/eventos.html");
            }
            this.Activate();
        }
        private void webcamToolStrip_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormWebCam (webcam USB por DirectShow)
            // se reemplazó por la página HTML /pages/camaras.html, que ya integra las
            // cámaras Hikvision del equipo vía su API (RTSP→MediaMTX) en el Hub. Widget
            // Avalonia si existe; si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/camaras.html", "float",
                                      "Cámaras", 1000, 720))
            {
                OpenAgroParallelHub("pages/camaras.html");
            }
            this.Activate();
        }
        private void offsetFixToolStrip_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormShiftPos se reemplazó por la
            // página HTML /pages/corregir-posicion.html (corrimiento de deriva GPS,
            // aplica en vivo por POST /api/aog/guidance/command). Widget Avalonia
            // si existe; si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/corregir-posicion.html", "float",
                                      "Corregir posición", 620, 640))
            {
                OpenAgroParallelHub("pages/corregir-posicion.html");
            }
        }
        private void correctionToolStrip_Click(object sender, EventArgs e)
        {
            // AgroParallel: la ventana WinForms FormCorrection se reemplazó por la
            // página HTML /pages/grafico-correccion.html (chequeo de roll: corrección
            // por roll del IMU vs deriva GPS, buffer rodante). Widget Avalonia si
            // existe; si no, Hub WebView2.
            if (!LaunchAvaloniaWidget("pages/grafico-correccion.html", "float",
                                      "Chequeo de roll", 900, 700))
            {
                OpenAgroParallelHub("pages/grafico-correccion.html");
            }
        }

        #endregion

        #region Nav Panel

        private void btnTiltUp_Click(object sender, EventArgs e)
        {
            camera.PitchInDegrees -= ((camera.PitchInDegrees * 0.012) - 1);
            if (camera.PitchInDegrees > -58) camera.PitchInDegrees = 0;
            navPanelCounter = 2;
        }

        private void btnTiltDn_Click(object sender, EventArgs e)
        {
            if (camera.PitchInDegrees > -59) camera.PitchInDegrees = -60;
            camera.PitchInDegrees += ((camera.PitchInDegrees * 0.012) - 1);
            if (camera.PitchInDegrees < -70) camera.PitchInDegrees = -70;
            navPanelCounter = 2;
        }

        private void btnN2D_Click(object sender, EventArgs e)
        {
            camera.FollowDirectionHint = false;
            camera.PitchInDegrees = 0;
            navPanelCounter = 0;
        }
        private void btn2D_Click(object sender, EventArgs e)
        {
            camera.FollowDirectionHint = true;
            camera.PitchInDegrees = 0;
            navPanelCounter = 0;
        }

        private void btn3D_Click(object sender, EventArgs e)
        {
            camera.FollowDirectionHint = true;
            camera.PitchInDegrees = -65;
            navPanelCounter = 0;
        }

        private void btnGrid_Click(object sender, EventArgs e)
        {
            var form = new FormGrid(this, worldGrid.FieldGrid);
            form.Show(this);
            navPanelCounter = 0;
        }
        private void btnBrightnessUp_Click(object sender, EventArgs e)
        {
            if (displayBrightness.IsSupported)
            {
                displayBrightness.BrightnessIncrease();
                btnBrightnessDn.Text = displayBrightness.GetBrightness().ToString() + "%";
                Settings.Default.setDisplay_brightness = displayBrightness.GetBrightness();
                Settings.Default.Save();
            }
            navPanelCounter = 3;
        }
        private void btnBrightnessDn_Click(object sender, EventArgs e)
        {
            if (displayBrightness.IsSupported)
            {
                displayBrightness.BrightnessDecrease();
                btnBrightnessDn.Text = displayBrightness.GetBrightness().ToString() + "%";
                Settings.Default.setDisplay_brightness = displayBrightness.GetBrightness();
                Settings.Default.Save();
            }
            navPanelCounter = 3;
        }
        private void btnDayNightMode_Click(object sender, EventArgs e)
        {
            SwapDayNightMode();
            navPanelCounter = 0;
        }

        #endregion

        #region OpenGL Window context Menu and functions
        private void contextMenuStripOpenGL_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            //dont bring up menu if no flag selected
            if (flagNumberPicked == 0) e.Cancel = true;
        }
        private void googleEarthOpenGLContextMenu_Click(object sender, EventArgs e)
        {
            if (isJobStarted)
            {
                //save new copy of kml with selected flag and view in GoogleEarth
                FileSaveSingleFlagKML(flagNumberPicked);

                //Process.Start(@"C:\Program Files (x86)\Google\Google Earth\client\googleearth", workingDirectory + currentFieldDirectory + "\\Flags.KML");
                Process.Start(Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory, "Flag.KML"));
            }
        }

        private void lblHardwareMessage_Click(object sender, EventArgs e)
        {
            hardwareLineCounter = 1;
        }

        #endregion

        #region Sim controls

        private void btnSimSpeedUp_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (sim.stepDistance < 0)
            {
                sim.stepDistance = 0;
                return;
            }
            if (sim.stepDistance < 0.2) sim.stepDistance += 0.02;
            else sim.stepDistance *= 1.15;

            if (sim.stepDistance > 7.5) sim.stepDistance = 7.5;
        }
        private void btnSpeedDn_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (sim.stepDistance < 0.2 && sim.stepDistance > -0.51) sim.stepDistance -= 0.02;
            else sim.stepDistance *= 0.8;
            if (sim.stepDistance < -0.5) sim.stepDistance = -0.5;
        }

        double lastSimGuidanceAngle = 0;
        private void timerSim_Tick(object sender, EventArgs e)
        {
            if (recPath.isDrivingRecordedPath || isBtnAutoSteerOn && (guidanceLineDistanceOff != 32000))
            {
                if (vehicle.isInDeadZone)
                {
                    sim.DoSimTick((double)lastSimGuidanceAngle);
                }
                else
                {
                    lastSimGuidanceAngle = (double)guidanceLineSteerAngle * 0.01 * 0.9;
                    sim.DoSimTick(lastSimGuidanceAngle);
                }
            }
            else sim.DoSimTick(sim.steerAngleScrollBar);
        }
        private void btnSimReverseDirection_Click(object sender, EventArgs e)
        {
            sim.headingTrue += Math.PI;
            ABLine.isABValid = false;
            curve.isCurveValid = false;
            if (isBtnAutoSteerOn)
            {
                btnAutoSteer.PerformClick();
                TimedMessageBox(2000, gStr.gsGuidanceStopped, "Sim Reverse Touched");
                Log.EventWriter("Steer Off, Sim Reverse Activated");
            }
        }
        private void hsbarSteerAngle_Scroll(object sender, ScrollEventArgs e)
        {
            sim.steerAngleScrollBar = (hsbarSteerAngle.Value - 400) * 0.1;
            btnResetSteerAngle.Text = sim.steerAngleScrollBar.ToString("N1");
        }
        private void btnResetSteerAngle_Click(object sender, EventArgs e)
        {
            sim.steerAngleScrollBar = 0;
            hsbarSteerAngle.Value = 400;
            btnResetSteerAngle.Text = sim.steerAngleScrollBar.ToString("N1");
        }
        private void btnResetSim_Click(object sender, EventArgs e)
        {
            sim.CurrentLatLon = new Wgs84(
                Properties.Settings.Default.setGPS_SimLatitude,
                Properties.Settings.Default.setGPS_SimLongitude);
        }
        private void btnSimSetSpeedToZero_Click(object sender, EventArgs e)
        {
            sim.stepDistance = 0;
        }
        private void btnSimReverse_Click(object sender, EventArgs e)
        {
            sim.stepDistance = 0;
            sim.isAccelBack = true;
        }
        private void btnSimForward_Click(object sender, EventArgs e)
        {
            sim.stepDistance = 0;
            sim.isAccelForward = true;
        }

        #endregion

        public void FixTramModeButton()
        {
            if (tram.tramList.Count > 0 && tram.tramBndOuterArr.Count > 0)
            {
                tram.displayMode = TramMode.All;
            }
            else if (tram.tramList.Count == 0 && tram.tramBndOuterArr.Count > 0)
            {
                tram.displayMode = TramMode.BoundaryTracks;
            }
            else if (tram.tramList.Count > 0 && tram.tramBndOuterArr.Count == 0)
            {
                tram.displayMode = TramMode.FillTracks;
            }
            btnTramDisplayMode.Image = TramModeBitmaps.Get(tram.displayMode);
        }

        private ToolStripMenuItem steerChartToolStripMenuItem;
        private ToolStripMenuItem headingChartToolStripMenuItem;
        private ToolStripMenuItem xTEChartToolStripMenuItem;
    }
}
