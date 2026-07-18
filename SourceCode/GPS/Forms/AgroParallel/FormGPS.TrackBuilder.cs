// ============================================================================
// FormGPS.TrackBuilder.cs — gestión de tracks para HTML (tracks.html).
// Port de las funciones de gestión de FormBuildTracks (CRUD, swap, backup).
// Los métodos de creación interactiva (curva record, AB live) quedan para
// fase 2. Todos los métodos asumen hilo UI (el adapter marshalea).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // ── Sesión de TrackBuilder ──────────────────────────────────────
        private readonly List<CTrk> trkBuilderBackup = new List<CTrk>();
        private int trkBuilderSelIdx = -1;
        private int trkBuilderOrigIdx = -1;

        // ── Open ────────────────────────────────────────────────────────
        internal void TrkBuilder_Open()
        {
            trkBuilderBackup.Clear();
            foreach (var item in trk.gArr)
                trkBuilderBackup.Add(new CTrk(item));

            trkBuilderOrigIdx = trk.idx;
            trkBuilderSelIdx = -1;
        }

        // ── Snapshot ────────────────────────────────────────────────────
        internal sealed class TrkBuilderSnapshot
        {
            public List<CTrk> Tracks;
            public int SelectedIdx;
            public int ActiveIdx;
            public string Error;
        }

        internal TrkBuilderSnapshot TrkBuilder_Snapshot(string error = null)
        {
            return new TrkBuilderSnapshot
            {
                Tracks = trk.gArr,
                SelectedIdx = trkBuilderSelIdx,
                ActiveIdx = trk.idx,
                Error = error
            };
        }

        // ── Toggle visibility ───────────────────────────────────────────
        internal void TrkBuilder_ToggleVisibility(int index)
        {
            if (index < 0 || index >= trk.gArr.Count) return;
            trk.gArr[index].isVisible = !trk.gArr[index].isVisible;
            trkBuilderSelIdx = -1;
        }

        internal void TrkBuilder_ToggleAll(bool visible)
        {
            for (int i = 0; i < trk.gArr.Count; i++)
                trk.gArr[i].isVisible = visible;
        }

        // ── Select ──────────────────────────────────────────────────────
        internal void TrkBuilder_Select(int index)
        {
            if (index < 0 || index >= trk.gArr.Count) return;
            if (!trk.gArr[index].isVisible)
            {
                trkBuilderSelIdx = -1;
                return;
            }
            trkBuilderSelIdx = (trkBuilderSelIdx == index) ? -1 : index;
        }

        // ── Delete ──────────────────────────────────────────────────────
        internal void TrkBuilder_Delete()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= trk.gArr.Count) return;
            trk.gArr.RemoveAt(trkBuilderSelIdx);
            trkBuilderSelIdx = -1;
            trk.idx = trk.gArr.Count - 1;
        }

        // ── Duplicate ───────────────────────────────────────────────────
        internal void TrkBuilder_Duplicate(string newName)
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= trk.gArr.Count) return;
            trk.gArr.Add(new CTrk(trk.gArr[trkBuilderSelIdx]));
            int idx = trk.gArr.Count - 1;
            trk.gArr[idx].name = string.IsNullOrWhiteSpace(newName)
                ? trk.gArr[trkBuilderSelIdx].name + " Copia"
                : newName;
            trkBuilderSelIdx = -1;
        }

        // ── Rename ──────────────────────────────────────────────────────
        internal void TrkBuilder_Rename(string newName)
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= trk.gArr.Count) return;
            trk.gArr[trkBuilderSelIdx].name = string.IsNullOrWhiteSpace(newName)
                ? "Sin nombre " + DateTime.Now.ToString("hh:mm:ss", CultureInfo.InvariantCulture)
                : newName;
        }

        // ── Move up/down ────────────────────────────────────────────────
        internal void TrkBuilder_MoveUp()
        {
            if (trkBuilderSelIdx <= 0 || trkBuilderSelIdx >= trk.gArr.Count) return;
            trk.gArr.Reverse(trkBuilderSelIdx - 1, 2);
            trkBuilderSelIdx--;
        }

        internal void TrkBuilder_MoveDown()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= trk.gArr.Count - 1) return;
            trk.gArr.Reverse(trkBuilderSelIdx, 2);
            trkBuilderSelIdx++;
        }

        // ── Swap A↔B ────────────────────────────────────────────────────
        internal void TrkBuilder_SwapAB()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= trk.gArr.Count) return;
            int idx = trkBuilderSelIdx;

            if (trk.gArr[idx].mode == TrackMode.AB)
            {
                vec2 bob = trk.gArr[idx].ptA;
                trk.gArr[idx].ptA = trk.gArr[idx].ptB;
                trk.gArr[idx].ptB = new vec2(bob);

                trk.gArr[idx].heading += Math.PI;
                if (trk.gArr[idx].heading < 0) trk.gArr[idx].heading += glm.twoPI;
                if (trk.gArr[idx].heading > glm.twoPI) trk.gArr[idx].heading -= glm.twoPI;
            }
            else
            {
                int cnt = trk.gArr[idx].curvePts.Count;
                if (cnt > 0)
                {
                    trk.gArr[idx].curvePts.Reverse();

                    vec3[] arr = new vec3[cnt];
                    cnt--;
                    trk.gArr[idx].curvePts.CopyTo(arr);
                    trk.gArr[idx].curvePts.Clear();

                    trk.gArr[idx].heading += Math.PI;
                    if (trk.gArr[idx].heading < 0) trk.gArr[idx].heading += glm.twoPI;
                    if (trk.gArr[idx].heading > glm.twoPI) trk.gArr[idx].heading -= glm.twoPI;

                    for (int i = 1; i < cnt; i++)
                    {
                        vec3 pt3 = arr[i];
                        pt3.heading += Math.PI;
                        if (pt3.heading > glm.twoPI) pt3.heading -= glm.twoPI;
                        if (pt3.heading < 0) pt3.heading += glm.twoPI;
                        trk.gArr[idx].curvePts.Add(new vec3(pt3));
                    }

                    vec2 temp = new vec2(trk.gArr[idx].ptA);
                    trk.gArr[idx].ptA = new vec2(trk.gArr[idx].ptB);
                    trk.gArr[idx].ptB = new vec2(temp);
                }
            }
        }

        // ── Crear AB desde posición actual (A+ heading) ─────────────────
        internal void TrkBuilder_CreateABFromPivot(double headingDeg, string name)
        {
            double heading = glm.toRadians(headingDeg);

            trk.gArr.Add(new CTrk());
            int idx = trk.gArr.Count - 1;

            trk.gArr[idx].ptA = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
            trk.gArr[idx].ptB = new vec2(
                pivotAxlePos.easting + (Math.Sin(heading) * 200),
                pivotAxlePos.northing + (Math.Cos(heading) * 200));

            trk.gArr[idx].mode = TrackMode.AB;
            trk.gArr[idx].heading = heading;

            trk.gArr[idx].name = string.IsNullOrWhiteSpace(name)
                ? "A+ " + Math.Round(headingDeg, 1).ToString(CultureInfo.InvariantCulture) + "\u00B0"
                : name;

            double dist = (tool.width - tool.overlap) * 0.5 + tool.offset;
            trk.idx = idx;
            trk.NudgeRefABLine(dist);
        }

        // ── Close Use ───────────────────────────────────────────────────
        internal void TrkBuilder_CloseUse()
        {
            curve.isCurveValid = false;
            ABLine.isABValid = false;
            curve.desList?.Clear();

            if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();

            FileSaveTracks();

            if (trkBuilderSelIdx > -1 && trk.gArr.Count > 0
                && trkBuilderSelIdx < trk.gArr.Count
                && trk.gArr[trkBuilderSelIdx].isVisible)
            {
                trk.idx = trkBuilderSelIdx;
                yt.ResetYouTurn();
            }
            else if (trk.gArr.Count > 0)
            {
                bool found = false;
                for (int i = 0; i < trk.gArr.Count; i++)
                {
                    if (trk.gArr[i].isVisible)
                    {
                        trk.idx = i;
                        yt.ResetYouTurn();
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    trk.idx = -1;
                    DisableYouTurnButtons();
                    if (isBtnAutoSteerOn) btnAutoSteer.PerformClick();
                }
            }
            else
            {
                trk.idx = -1;
                DisableYouTurnButtons();
                if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();
            }

            twoSecondCounter = 100;
            PanelUpdateRightAndBottom();

            trkBuilderBackup.Clear();
            trkBuilderSelIdx = -1;
        }

        // ── Close Cancel ────────────────────────────────────────────────
        internal void TrkBuilder_CloseCancel()
        {
            curve.desList?.Clear();

            if (isBtnAutoSteerOn) btnAutoSteer.PerformClick();
            if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();

            trk.gArr.Clear();
            foreach (var item in trkBuilderBackup)
                trk.gArr.Add(new CTrk(item));

            trk.idx = trkBuilderOrigIdx;
            curve.isCurveValid = false;
            ABLine.isABValid = false;
            twoSecondCounter = 100;

            PanelUpdateRightAndBottom();

            trkBuilderBackup.Clear();
            trkBuilderSelIdx = -1;
        }
    }
}
