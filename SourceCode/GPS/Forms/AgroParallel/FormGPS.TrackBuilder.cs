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
            public double[] APoint;
            public double[] BPoint;
            public bool CanMakeLine;
            public bool HasBoundaryCurve;
            public int BndSelect;
            public double ABLength;
        }

        internal TrkBuilderSnapshot TrkBuilder_Snapshot(string error = null)
        {
            return new TrkBuilderSnapshot
            {
                Tracks = trk.gArr,
                SelectedIdx = trkBuilderSelIdx,
                ActiveIdx = trk.idx,
                Error = error,
                APoint = TrkBuilder_APointEN(),
                BPoint = TrkBuilder_BPointEN(),
                CanMakeLine = TrkBuilder_CanMakeLine(),
                HasBoundaryCurve = TrkBuilder_HasBoundaryCurve(),
                BndSelect = TrkBuilder_BndSelect(),
                ABLength = ABLine != null ? ABLine.abLength : 2000
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

        // ── ABDraw: tap A/B en contorno ─────────────────────────────────
        private bool trkBuilderIsA = true;
        private int trkBuilderStart = 99999, trkBuilderEnd = 99999;
        private int trkBuilderBndSel = 0;

        internal void TrkBuilder_Tap(double easting, double northing)
        {
            if (bnd == null || bnd.bndList.Count == 0) return;

            if (trkBuilderIsA)
            {
                double minDist = double.MaxValue;
                trkBuilderStart = 99999; trkBuilderEnd = 99999;

                for (int j = 0; j < bnd.bndList.Count; j++)
                {
                    for (int i = 0; i < bnd.bndList[j].fenceLine.Count; i++)
                    {
                        double dist = ((easting - bnd.bndList[j].fenceLine[i].easting) * (easting - bnd.bndList[j].fenceLine[i].easting))
                                        + ((northing - bnd.bndList[j].fenceLine[i].northing) * (northing - bnd.bndList[j].fenceLine[i].northing));
                        if (dist < minDist) { minDist = dist; trkBuilderBndSel = j; trkBuilderStart = i; }
                    }
                }
                trkBuilderIsA = false;
            }
            else
            {
                double minDist = double.MaxValue;
                int j2 = trkBuilderBndSel;
                for (int i = 0; i < bnd.bndList[j2].fenceLine.Count; i++)
                {
                    double dist = ((easting - bnd.bndList[j2].fenceLine[i].easting) * (easting - bnd.bndList[j2].fenceLine[i].easting))
                                    + ((northing - bnd.bndList[j2].fenceLine[i].northing) * (northing - bnd.bndList[j2].fenceLine[i].northing));
                    if (dist < minDist) { minDist = dist; trkBuilderEnd = i; }
                }
                trkBuilderIsA = true;
            }
        }

        internal void TrkBuilder_CancelTouch()
        {
            trkBuilderStart = 99999; trkBuilderEnd = 99999;
            trkBuilderIsA = true;
            curve.desList?.Clear();
        }

        internal bool TrkBuilder_CanMakeLine()
        {
            return trkBuilderStart != 99999 && trkBuilderEnd != 99999;
        }

        internal double[] TrkBuilder_APointEN()
        {
            if (trkBuilderStart == 99999 || trkBuilderBndSel >= bnd.bndList.Count
                || trkBuilderStart >= bnd.bndList[trkBuilderBndSel].fenceLine.Count) return null;
            var p = bnd.bndList[trkBuilderBndSel].fenceLine[trkBuilderStart];
            return new double[] { p.easting, p.northing };
        }

        internal double[] TrkBuilder_BPointEN()
        {
            if (trkBuilderEnd == 99999 || trkBuilderBndSel >= bnd.bndList.Count
                || trkBuilderEnd >= bnd.bndList[trkBuilderBndSel].fenceLine.Count) return null;
            var p = bnd.bndList[trkBuilderBndSel].fenceLine[trkBuilderEnd];
            return new double[] { p.easting, p.northing };
        }

        internal int TrkBuilder_BndSelect() { return trkBuilderBndSel; }

        internal bool TrkBuilder_HasBoundaryCurve()
        {
            for (int i = 0; i < trk.gArr.Count; i++)
                if (trk.gArr[i].mode == TrackMode.bndCurve) return true;
            return false;
        }

        // ── Make Curve desde tap A/B ─────────────────────────────────────
        internal string TrkBuilder_MakeCurve()
        {
            if (trkBuilderStart == 99999 || trkBuilderEnd == 99999) return "sin-puntos";
            int startIdx = trkBuilderStart, endIdx = trkBuilderEnd;
            int bndSel = trkBuilderBndSel;

            bool isLoop = false;
            int limit = endIdx;

            if ((Math.Abs(startIdx - endIdx)) > (bnd.bndList[bndSel].fenceLine.Count * 0.5))
            {
                isLoop = true;
                if (startIdx < endIdx) (endIdx, startIdx) = (startIdx, endIdx);
                limit = endIdx;
                endIdx = bnd.bndList[bndSel].fenceLine.Count;
            }
            else
            {
                if (startIdx > endIdx) (endIdx, startIdx) = (startIdx, endIdx);
            }

            curve.desList?.Clear();
            for (int i = startIdx; i < endIdx; i++)
            {
                curve.desList.Add(new vec3(bnd.bndList[bndSel].fenceLine[i]));
                if (isLoop && i == bnd.bndList[bndSel].fenceLine.Count - 1)
                {
                    i = -1; isLoop = false; endIdx = limit;
                }
            }

            if (curve.desList.Count < 4) { curve.desList?.Clear(); return "pocos-puntos"; }

            CABCurve.MakePointMinimumSpacing(ref curve.desList, 1.6);
            CABCurve.CalculateHeadings(ref curve.desList);

            trk.gArr.Add(new CTrk());
            int idx = trk.gArr.Count - 1;

            trk.gArr[idx].ptA = new vec2(curve.desList[0].easting, curve.desList[0].northing);
            trk.gArr[idx].ptB = new vec2(curve.desList[curve.desList.Count - 1].easting, curve.desList[curve.desList.Count - 1].northing);

            double x = 0, y = 0;
            foreach (vec3 pt in curve.desList) { x += Math.Cos(pt.heading); y += Math.Sin(pt.heading); }
            x /= curve.desList.Count; y /= curve.desList.Count;
            trk.gArr[idx].heading = Math.Atan2(y, x);
            if (trk.gArr[idx].heading < 0) trk.gArr[idx].heading += glm.twoPI;

            curve.AddFirstLastPoints(ref curve.desList);
            CABCurve.CalculateHeadings(ref curve.desList);

            trk.gArr[idx].mode = TrackMode.Curve;
            trk.gArr[idx].name = "Cu " + Math.Round(glm.toDegrees(trk.gArr[idx].heading), 1).ToString(CultureInfo.InvariantCulture) + "\u00B0";

            foreach (vec3 item in curve.desList)
                trk.gArr[idx].curvePts.Add(item);

            trkBuilderSelIdx = idx;
            trkBuilderStart = 99999; trkBuilderEnd = 99999;
            curve.desList?.Clear();
            return null;
        }

        // ── Make AB Line desde tap A/B ───────────────────────────────────
        internal string TrkBuilder_MakeABLine()
        {
            if (trkBuilderStart == 99999 || trkBuilderEnd == 99999) return "sin-puntos";
            int startIdx = trkBuilderStart, endIdx = trkBuilderEnd;
            int bndSel = trkBuilderBndSel;

            if ((Math.Abs(startIdx - endIdx)) <= (bnd.bndList[bndSel].fenceLine.Count * 0.5))
            {
                if (startIdx < endIdx) (endIdx, startIdx) = (startIdx, endIdx);
            }
            else
            {
                if (startIdx > endIdx) (endIdx, startIdx) = (startIdx, endIdx);
            }

            double abHead = Math.Atan2(
                bnd.bndList[bndSel].fenceLine[endIdx].easting - bnd.bndList[bndSel].fenceLine[startIdx].easting,
                bnd.bndList[bndSel].fenceLine[endIdx].northing - bnd.bndList[bndSel].fenceLine[startIdx].northing);
            if (abHead < 0) abHead += glm.twoPI;

            trk.gArr.Add(new CTrk());
            int idx = trk.gArr.Count - 1;

            trk.gArr[idx].heading = abHead;
            trk.gArr[idx].mode = TrackMode.AB;
            trk.gArr[idx].ptA.easting = bnd.bndList[bndSel].fenceLine[startIdx].easting;
            trk.gArr[idx].ptA.northing = bnd.bndList[bndSel].fenceLine[startIdx].northing;
            trk.gArr[idx].ptB.easting = bnd.bndList[bndSel].fenceLine[endIdx].easting;
            trk.gArr[idx].ptB.northing = bnd.bndList[bndSel].fenceLine[endIdx].northing;
            trk.gArr[idx].name = "AB " + Math.Round(glm.toDegrees(abHead), 1).ToString(CultureInfo.InvariantCulture) + "\u00B0";

            trkBuilderSelIdx = idx;
            trkBuilderStart = 99999; trkBuilderEnd = 99999;
            return null;
        }

        // ── Make Boundary Curve ──────────────────────────────────────────
        internal void TrkBuilder_MakeBoundaryCurve()
        {
            if (bnd == null) return;
            for (int q = 0; q < bnd.bndList.Count; q++)
            {
                var bndPts = new List<vec3>();
                foreach (vec3 pt in bnd.bndList[q].fenceLine)
                    bndPts.Add(new vec3(pt));

                if (bndPts.Count < 4) continue;

                trk.gArr.Add(new CTrk());
                int idx = trk.gArr.Count - 1;

                trk.gArr[idx].ptA = new vec2(bndPts[0].easting, bndPts[0].northing);
                trk.gArr[idx].ptB = new vec2(bndPts[bndPts.Count - 2].easting, bndPts[bndPts.Count - 2].northing);
                trk.gArr[idx].name = q == 0 ? "Boundary Curve" : "Inner Boundary Curve " + q.ToString();
                trk.gArr[idx].heading = 0;
                trk.gArr[idx].mode = TrackMode.bndCurve;

                foreach (vec3 pt in bndPts)
                    trk.gArr[idx].curvePts.Add(pt);

                trkBuilderSelIdx = idx;
            }
            trkBuilderStart = 99999; trkBuilderEnd = 99999;
        }

        // ── Extend A/B de curva ──────────────────────────────────────────
        internal void TrkBuilder_ExtendA()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= trk.gArr.Count) return;
            if (trk.gArr[trkBuilderSelIdx].mode != TrackMode.Curve) return;
            vec3 s = new vec3(trk.gArr[trkBuilderSelIdx].curvePts[0]);
            for (int i = 1; i < 50; i++)
            {
                vec3 pt = new vec3(s);
                pt.easting -= (Math.Sin(pt.heading) * i);
                pt.northing -= (Math.Cos(pt.heading) * i);
                trk.gArr[trkBuilderSelIdx].curvePts.Insert(0, pt);
            }
        }

        internal void TrkBuilder_ExtendB()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= trk.gArr.Count) return;
            if (trk.gArr[trkBuilderSelIdx].mode != TrackMode.Curve) return;
            int ptCnt = trk.gArr[trkBuilderSelIdx].curvePts.Count - 1;
            for (int i = 1; i < 50; i++)
            {
                vec3 pt = new vec3(trk.gArr[trkBuilderSelIdx].curvePts[ptCnt]);
                pt.easting += (Math.Sin(pt.heading) * i);
                pt.northing += (Math.Cos(pt.heading) * i);
                trk.gArr[trkBuilderSelIdx].curvePts.Add(pt);
            }
        }

        // ── Creación interactiva: Record Curve ─────────────────────────
        private bool trkBuilderRecording = false;
        private vec2 trkBuilderRecPtA;

        internal void TrkBuilder_RecordCurveA()
        {
            trkBuilderRecPtA = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
            curve.desList?.Clear();
            curve.isMakingCurve = true;
            curve.isRecordingCurve = true;
            trkBuilderRecording = true;
        }

        internal void TrkBuilder_RecordCurvePause()
        {
            if (!trkBuilderRecording) return;
            curve.isRecordingCurve = !curve.isRecordingCurve;
        }

        internal string TrkBuilder_RecordCurveB(string name)
        {
            if (!trkBuilderRecording) return "no-recording";
            curve.isMakingCurve = false;
            curve.isRecordingCurve = false;
            trkBuilderRecording = false;

            vec2 ptB = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);

            int cnt = curve.desList.Count;
            if (cnt < 4) { curve.desList?.Clear(); return "pocos-puntos"; }

            CABCurve.MakePointMinimumSpacing(ref curve.desList, 1.6);
            CABCurve.CalculateHeadings(ref curve.desList);

            trk.gArr.Add(new CTrk());
            int idx = trk.gArr.Count - 1;

            trk.gArr[idx].ptA = new vec2(trkBuilderRecPtA);
            trk.gArr[idx].ptB = new vec2(ptB);
            trk.gArr[idx].mode = TrackMode.Curve;

            double x = 0, y = 0;
            foreach (vec3 pt in curve.desList) { x += Math.Cos(pt.heading); y += Math.Sin(pt.heading); }
            x /= curve.desList.Count; y /= curve.desList.Count;
            double aveH = Math.Atan2(y, x);
            if (aveH < 0) aveH += glm.twoPI;
            trk.gArr[idx].heading = aveH;

            curve.AddFirstLastPoints(ref curve.desList);
            CABCurve.CalculateHeadings(ref curve.desList);

            foreach (vec3 item in curve.desList)
                trk.gArr[idx].curvePts.Add(item);

            trk.gArr[idx].name = string.IsNullOrWhiteSpace(name)
                ? "Cu " + Math.Round(glm.toDegrees(aveH), 1).ToString(CultureInfo.InvariantCulture) + "\u00B0"
                : name;

            // Nudge a la derecha por defecto
            double dist = (tool.width - tool.overlap) * 0.5 + tool.offset;
            trk.idx = idx;
            trk.NudgeRefCurve(dist);

            trkBuilderSelIdx = idx;
            curve.desList?.Clear();
            return null;
        }

        internal void TrkBuilder_RecordCurveCancel()
        {
            curve.isMakingCurve = false;
            curve.isRecordingCurve = false;
            curve.desList?.Clear();
            trkBuilderRecording = false;
        }

        internal bool TrkBuilder_IsRecording() { return trkBuilderRecording; }
        internal int TrkBuilder_RecordedCount() { return trkBuilderRecording ? curve.desList.Count : 0; }

        // ── Geometría de tracks para el canvas ──────────────────────────
        internal List<double[][]> TrkBuilder_FencesEN()
        {
            var list = new List<double[][]>();
            if (bnd == null) return list;
            for (int j = 0; j < bnd.bndList.Count; j++)
            {
                var src = bnd.bndList[j].fenceLine;
                var outArr = new double[src.Count][];
                for (int i = 0; i < src.Count; i++)
                    outArr[i] = new double[] { src[i].easting, src[i].northing };
                list.Add(outArr);
            }
            return list;
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
