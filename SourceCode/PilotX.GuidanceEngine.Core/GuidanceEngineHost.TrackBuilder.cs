// ============================================================================
// GuidanceEngineHost.TrackBuilder.cs — gestión de tracks headless (bloque 14,
// ítem 2 del PEDIDO taller: reemplaza el stopgap "track_new_ab" de
// GuidanceEngineHost.Commands.cs por el ITrackBuilderService completo).
//
// Port 1:1 de FormGPS.TrackBuilder.cs (GPS/Forms/AgroParallel/), que ya era
// casi toda lógica pura (trk.gArr/curve/bnd/tool son los mismos objetos Core
// que ya vive en GuidanceEngineHost desde el bloque 9). Los únicos cambios
// reales vs el original:
//   - btnAutoSteer.PerformClick()/btnAutoYouTurn.PerformClick() -> los
//     mismos métodos que ya usa Commands.cs (PerformAutoSteerClick/
//     ToggleYouTurn), sin pasar por un botón que acá no existe.
//   - FileSaveTracks() -> SaveTracks() (TrackFiles.Save, mismo streamer que
//     ya usa GuidanceEngineHost.Job.cs para el Load).
//   - PanelUpdateRightAndBottom()/twoSecondCounter: puro refresh de UI
//     WinForms, no aplican sin ventana — se omiten.
// public en vez de internal: EngineTrackBuilderService vive en otro proyecto
// (PilotX.GuidanceEngine/Adapters), mismo criterio que ExecuteCommand.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        // ── Sesión de TrackBuilder ──────────────────────────────────────
        private readonly List<CTrk> trkBuilderBackup = new List<CTrk>();
        private int trkBuilderSelIdx = -1;
        private int trkBuilderOrigIdx = -1;

        // ── Open ────────────────────────────────────────────────────────
        public void TrkBuilder_Open()
        {
            trkBuilderBackup.Clear();
            foreach (var item in Trk.gArr)
                trkBuilderBackup.Add(new CTrk(item));

            trkBuilderOrigIdx = Trk.idx;
            trkBuilderSelIdx = -1;
        }

        // ── Snapshot ────────────────────────────────────────────────────
        public sealed class TrkBuilderSnapshot
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

        public TrkBuilderSnapshot TrkBuilder_Snapshot(string error = null)
        {
            return new TrkBuilderSnapshot
            {
                Tracks = Trk.gArr,
                SelectedIdx = trkBuilderSelIdx,
                ActiveIdx = Trk.idx,
                Error = error,
                APoint = TrkBuilder_APointEN(),
                BPoint = TrkBuilder_BPointEN(),
                CanMakeLine = TrkBuilder_CanMakeLine(),
                HasBoundaryCurve = TrkBuilder_HasBoundaryCurve(),
                BndSelect = TrkBuilder_BndSelect(),
                ABLength = ABLineField != null ? ABLineField.abLength : 2000
            };
        }

        // ── Toggle visibility ───────────────────────────────────────────
        public void TrkBuilder_ToggleVisibility(int index)
        {
            if (index < 0 || index >= Trk.gArr.Count) return;
            Trk.gArr[index].isVisible = !Trk.gArr[index].isVisible;
            trkBuilderSelIdx = -1;
        }

        public void TrkBuilder_ToggleAll(bool visible)
        {
            for (int i = 0; i < Trk.gArr.Count; i++)
                Trk.gArr[i].isVisible = visible;
        }

        // ── Select ──────────────────────────────────────────────────────
        public void TrkBuilder_Select(int index)
        {
            if (index < 0 || index >= Trk.gArr.Count) return;
            if (!Trk.gArr[index].isVisible)
            {
                trkBuilderSelIdx = -1;
                return;
            }
            trkBuilderSelIdx = (trkBuilderSelIdx == index) ? -1 : index;
        }

        // ── Delete ──────────────────────────────────────────────────────
        public void TrkBuilder_Delete()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= Trk.gArr.Count) return;
            Trk.gArr.RemoveAt(trkBuilderSelIdx);
            trkBuilderSelIdx = -1;
            Trk.idx = Trk.gArr.Count - 1;
        }

        // ── Duplicate ───────────────────────────────────────────────────
        public void TrkBuilder_Duplicate(string newName)
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= Trk.gArr.Count) return;
            Trk.gArr.Add(new CTrk(Trk.gArr[trkBuilderSelIdx]));
            int idx = Trk.gArr.Count - 1;
            Trk.gArr[idx].name = string.IsNullOrWhiteSpace(newName)
                ? Trk.gArr[trkBuilderSelIdx].name + " Copia"
                : newName;
            trkBuilderSelIdx = -1;
        }

        // ── Rename ──────────────────────────────────────────────────────
        public void TrkBuilder_Rename(string newName)
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= Trk.gArr.Count) return;
            Trk.gArr[trkBuilderSelIdx].name = string.IsNullOrWhiteSpace(newName)
                ? "Sin nombre " + DateTime.Now.ToString("hh:mm:ss", CultureInfo.InvariantCulture)
                : newName;
        }

        // ── Move up/down ────────────────────────────────────────────────
        public void TrkBuilder_MoveUp()
        {
            if (trkBuilderSelIdx <= 0 || trkBuilderSelIdx >= Trk.gArr.Count) return;
            Trk.gArr.Reverse(trkBuilderSelIdx - 1, 2);
            trkBuilderSelIdx--;
        }

        public void TrkBuilder_MoveDown()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= Trk.gArr.Count - 1) return;
            Trk.gArr.Reverse(trkBuilderSelIdx, 2);
            trkBuilderSelIdx++;
        }

        // ── Swap A↔B ────────────────────────────────────────────────────
        public void TrkBuilder_SwapAB()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= Trk.gArr.Count) return;
            int idx = trkBuilderSelIdx;

            if (Trk.gArr[idx].mode == TrackMode.AB)
            {
                vec2 bob = Trk.gArr[idx].ptA;
                Trk.gArr[idx].ptA = Trk.gArr[idx].ptB;
                Trk.gArr[idx].ptB = new vec2(bob);

                Trk.gArr[idx].heading += Math.PI;
                if (Trk.gArr[idx].heading < 0) Trk.gArr[idx].heading += glm.twoPI;
                if (Trk.gArr[idx].heading > glm.twoPI) Trk.gArr[idx].heading -= glm.twoPI;
            }
            else
            {
                int cnt = Trk.gArr[idx].curvePts.Count;
                if (cnt > 0)
                {
                    Trk.gArr[idx].curvePts.Reverse();

                    vec3[] arr = new vec3[cnt];
                    cnt--;
                    Trk.gArr[idx].curvePts.CopyTo(arr);
                    Trk.gArr[idx].curvePts.Clear();

                    Trk.gArr[idx].heading += Math.PI;
                    if (Trk.gArr[idx].heading < 0) Trk.gArr[idx].heading += glm.twoPI;
                    if (Trk.gArr[idx].heading > glm.twoPI) Trk.gArr[idx].heading -= glm.twoPI;

                    for (int i = 1; i < cnt; i++)
                    {
                        vec3 pt3 = arr[i];
                        pt3.heading += Math.PI;
                        if (pt3.heading > glm.twoPI) pt3.heading -= glm.twoPI;
                        if (pt3.heading < 0) pt3.heading += glm.twoPI;
                        Trk.gArr[idx].curvePts.Add(new vec3(pt3));
                    }

                    vec2 temp = new vec2(Trk.gArr[idx].ptA);
                    Trk.gArr[idx].ptA = new vec2(Trk.gArr[idx].ptB);
                    Trk.gArr[idx].ptB = new vec2(temp);
                }
            }
        }

        // ── Crear AB desde posición actual (A+ heading) ─────────────────
        public void TrkBuilder_CreateABFromPivot(double headingDeg, string name)
        {
            double heading = glm.toRadians(headingDeg);

            Trk.gArr.Add(new CTrk());
            int idx = Trk.gArr.Count - 1;

            Trk.gArr[idx].ptA = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
            Trk.gArr[idx].ptB = new vec2(
                pivotAxlePos.easting + (Math.Sin(heading) * 200),
                pivotAxlePos.northing + (Math.Cos(heading) * 200));

            Trk.gArr[idx].mode = TrackMode.AB;
            Trk.gArr[idx].heading = heading;

            Trk.gArr[idx].name = string.IsNullOrWhiteSpace(name)
                ? "A+ " + Math.Round(headingDeg, 1).ToString(CultureInfo.InvariantCulture) + "°"
                : name;

            double dist = (Tool.width - Tool.overlap) * 0.5 + Tool.offset;
            Trk.idx = idx;
            Trk.NudgeRefABLine(dist);
        }

        // ── Close Use ───────────────────────────────────────────────────
        public void TrkBuilder_CloseUse()
        {
            CurveField.desList?.Clear();

            if (Yt.isYouTurnBtnOn) ToggleYouTurn();

            SaveTracks();

            // Elegir el track destino y ASIGNAR Trk.idx ANTES de invalidar la
            // línea. El orden viejo (invalidar → SaveTracks lento → recién ahí
            // idx) dejaba una ventana en la que el pipeline de posición
            // reconstruía la línea desde el track ANTERIOR y la marcaba válida
            // — el OK del listado "no abría nada" aunque el índice cambiara
            // después (banco 2026-08-11). CycleTrack, el camino que sí andaba,
            // hace exactamente esto: primero idx, después invalidar.
            int destino = -1;
            if (trkBuilderSelIdx > -1 && trkBuilderSelIdx < Trk.gArr.Count
                && Trk.gArr[trkBuilderSelIdx].isVisible)
            {
                destino = trkBuilderSelIdx;
            }
            else
            {
                // Sin selección: la primera guía visible. El tilde con el
                // listado recién abierto tiene que activar ALGO, no cerrarse
                // en silencio.
                for (int i = 0; i < Trk.gArr.Count; i++)
                {
                    if (Trk.gArr[i].isVisible) { destino = i; break; }
                }
            }

            Trk.idx = destino;
            CurveField.isCurveValid = false;
            ABLineField.isABValid = false;

            if (destino > -1)
            {
                Yt.ResetYouTurn();
                Log.EventWriter(string.Format(CultureInfo.InvariantCulture,
                    "GuidanceEngine: tracks/use activo la guia [{0}] '{1}' (seleccion={2})",
                    destino, Trk.gArr[destino].name, trkBuilderSelIdx));
            }
            else
            {
                Yt.isYouTurnBtnOn = false;
                Yt.ResetYouTurn();
                if (Trk.gArr.Count > 0 && isBtnAutoSteerOn)
                    ((IAutoSteerHost)this).PerformAutoSteerClick();
                Log.EventWriter("GuidanceEngine: tracks/use sin guia visible para activar — guia desactivada");
            }

            trkBuilderBackup.Clear();
            trkBuilderSelIdx = -1;
        }

        // ── ABDraw: tap A/B en contorno ─────────────────────────────────
        private bool trkBuilderIsA = true;
        private int trkBuilderStart = 99999, trkBuilderEnd = 99999;
        private int trkBuilderBndSel = 0;

        public void TrkBuilder_Tap(double easting, double northing)
        {
            if (Bnd == null || Bnd.bndList.Count == 0) return;

            if (trkBuilderIsA)
            {
                double minDist = double.MaxValue;
                trkBuilderStart = 99999; trkBuilderEnd = 99999;

                for (int j = 0; j < Bnd.bndList.Count; j++)
                {
                    for (int i = 0; i < Bnd.bndList[j].fenceLine.Count; i++)
                    {
                        double dist = ((easting - Bnd.bndList[j].fenceLine[i].easting) * (easting - Bnd.bndList[j].fenceLine[i].easting))
                                        + ((northing - Bnd.bndList[j].fenceLine[i].northing) * (northing - Bnd.bndList[j].fenceLine[i].northing));
                        if (dist < minDist) { minDist = dist; trkBuilderBndSel = j; trkBuilderStart = i; }
                    }
                }
                trkBuilderIsA = false;
            }
            else
            {
                double minDist = double.MaxValue;
                int j2 = trkBuilderBndSel;
                for (int i = 0; i < Bnd.bndList[j2].fenceLine.Count; i++)
                {
                    double dist = ((easting - Bnd.bndList[j2].fenceLine[i].easting) * (easting - Bnd.bndList[j2].fenceLine[i].easting))
                                    + ((northing - Bnd.bndList[j2].fenceLine[i].northing) * (northing - Bnd.bndList[j2].fenceLine[i].northing));
                    if (dist < minDist) { minDist = dist; trkBuilderEnd = i; }
                }
                trkBuilderIsA = true;
            }
        }

        public void TrkBuilder_CancelTouch()
        {
            trkBuilderStart = 99999; trkBuilderEnd = 99999;
            trkBuilderIsA = true;
            CurveField.desList?.Clear();
        }

        public bool TrkBuilder_CanMakeLine()
        {
            return trkBuilderStart != 99999 && trkBuilderEnd != 99999;
        }

        public double[] TrkBuilder_APointEN()
        {
            if (trkBuilderStart == 99999 || trkBuilderBndSel >= Bnd.bndList.Count
                || trkBuilderStart >= Bnd.bndList[trkBuilderBndSel].fenceLine.Count) return null;
            var p = Bnd.bndList[trkBuilderBndSel].fenceLine[trkBuilderStart];
            return new double[] { p.easting, p.northing };
        }

        public double[] TrkBuilder_BPointEN()
        {
            if (trkBuilderEnd == 99999 || trkBuilderBndSel >= Bnd.bndList.Count
                || trkBuilderEnd >= Bnd.bndList[trkBuilderBndSel].fenceLine.Count) return null;
            var p = Bnd.bndList[trkBuilderBndSel].fenceLine[trkBuilderEnd];
            return new double[] { p.easting, p.northing };
        }

        public int TrkBuilder_BndSelect() { return trkBuilderBndSel; }

        public bool TrkBuilder_HasBoundaryCurve()
        {
            for (int i = 0; i < Trk.gArr.Count; i++)
                if (Trk.gArr[i].mode == TrackMode.bndCurve) return true;
            return false;
        }

        // ── Make Curve desde tap A/B ─────────────────────────────────────
        public string TrkBuilder_MakeCurve()
        {
            if (trkBuilderStart == 99999 || trkBuilderEnd == 99999) return "sin-puntos";
            int startIdx = trkBuilderStart, endIdx = trkBuilderEnd;
            int bndSel = trkBuilderBndSel;

            bool isLoop = false;
            int limit = endIdx;

            if ((Math.Abs(startIdx - endIdx)) > (Bnd.bndList[bndSel].fenceLine.Count * 0.5))
            {
                isLoop = true;
                if (startIdx < endIdx) (endIdx, startIdx) = (startIdx, endIdx);
                limit = endIdx;
                endIdx = Bnd.bndList[bndSel].fenceLine.Count;
            }
            else
            {
                if (startIdx > endIdx) (endIdx, startIdx) = (startIdx, endIdx);
            }

            CurveField.desList?.Clear();
            for (int i = startIdx; i < endIdx; i++)
            {
                CurveField.desList.Add(new vec3(Bnd.bndList[bndSel].fenceLine[i]));
                if (isLoop && i == Bnd.bndList[bndSel].fenceLine.Count - 1)
                {
                    i = -1; isLoop = false; endIdx = limit;
                }
            }

            if (CurveField.desList.Count < 4) { CurveField.desList?.Clear(); return "pocos-puntos"; }

            CABCurve.MakePointMinimumSpacing(ref CurveField.desList, 1.6);
            CABCurve.CalculateHeadings(ref CurveField.desList);

            Trk.gArr.Add(new CTrk());
            int idx = Trk.gArr.Count - 1;

            Trk.gArr[idx].ptA = new vec2(CurveField.desList[0].easting, CurveField.desList[0].northing);
            Trk.gArr[idx].ptB = new vec2(CurveField.desList[CurveField.desList.Count - 1].easting, CurveField.desList[CurveField.desList.Count - 1].northing);

            double x = 0, y = 0;
            foreach (vec3 pt in CurveField.desList) { x += Math.Cos(pt.heading); y += Math.Sin(pt.heading); }
            x /= CurveField.desList.Count; y /= CurveField.desList.Count;
            Trk.gArr[idx].heading = Math.Atan2(y, x);
            if (Trk.gArr[idx].heading < 0) Trk.gArr[idx].heading += glm.twoPI;

            CurveField.AddFirstLastPoints(ref CurveField.desList);
            CABCurve.CalculateHeadings(ref CurveField.desList);

            Trk.gArr[idx].mode = TrackMode.Curve;
            Trk.gArr[idx].name = "Cu " + Math.Round(glm.toDegrees(Trk.gArr[idx].heading), 1).ToString(CultureInfo.InvariantCulture) + "°";

            foreach (vec3 item in CurveField.desList)
                Trk.gArr[idx].curvePts.Add(item);

            trkBuilderSelIdx = idx;
            trkBuilderStart = 99999; trkBuilderEnd = 99999;
            CurveField.desList?.Clear();
            return null;
        }

        // ── Make AB Line desde tap A/B ───────────────────────────────────
        public string TrkBuilder_MakeABLine()
        {
            if (trkBuilderStart == 99999 || trkBuilderEnd == 99999) return "sin-puntos";
            int startIdx = trkBuilderStart, endIdx = trkBuilderEnd;
            int bndSel = trkBuilderBndSel;

            if ((Math.Abs(startIdx - endIdx)) <= (Bnd.bndList[bndSel].fenceLine.Count * 0.5))
            {
                if (startIdx < endIdx) (endIdx, startIdx) = (startIdx, endIdx);
            }
            else
            {
                if (startIdx > endIdx) (endIdx, startIdx) = (startIdx, endIdx);
            }

            double abHead = Math.Atan2(
                Bnd.bndList[bndSel].fenceLine[endIdx].easting - Bnd.bndList[bndSel].fenceLine[startIdx].easting,
                Bnd.bndList[bndSel].fenceLine[endIdx].northing - Bnd.bndList[bndSel].fenceLine[startIdx].northing);
            if (abHead < 0) abHead += glm.twoPI;

            Trk.gArr.Add(new CTrk());
            int idx = Trk.gArr.Count - 1;

            Trk.gArr[idx].heading = abHead;
            Trk.gArr[idx].mode = TrackMode.AB;
            Trk.gArr[idx].ptA.easting = Bnd.bndList[bndSel].fenceLine[startIdx].easting;
            Trk.gArr[idx].ptA.northing = Bnd.bndList[bndSel].fenceLine[startIdx].northing;
            Trk.gArr[idx].ptB.easting = Bnd.bndList[bndSel].fenceLine[endIdx].easting;
            Trk.gArr[idx].ptB.northing = Bnd.bndList[bndSel].fenceLine[endIdx].northing;
            Trk.gArr[idx].name = "AB " + Math.Round(glm.toDegrees(abHead), 1).ToString(CultureInfo.InvariantCulture) + "°";

            trkBuilderSelIdx = idx;
            trkBuilderStart = 99999; trkBuilderEnd = 99999;
            return null;
        }

        // ── Make Boundary Curve ──────────────────────────────────────────
        public void TrkBuilder_MakeBoundaryCurve()
        {
            if (Bnd == null) return;
            for (int q = 0; q < Bnd.bndList.Count; q++)
            {
                var bndPts = new List<vec3>();
                foreach (vec3 pt in Bnd.bndList[q].fenceLine)
                    bndPts.Add(new vec3(pt));

                if (bndPts.Count < 4) continue;

                Trk.gArr.Add(new CTrk());
                int idx = Trk.gArr.Count - 1;

                Trk.gArr[idx].ptA = new vec2(bndPts[0].easting, bndPts[0].northing);
                Trk.gArr[idx].ptB = new vec2(bndPts[bndPts.Count - 2].easting, bndPts[bndPts.Count - 2].northing);
                Trk.gArr[idx].name = q == 0 ? "Boundary Curve" : "Inner Boundary Curve " + q.ToString();
                Trk.gArr[idx].heading = 0;
                Trk.gArr[idx].mode = TrackMode.bndCurve;

                foreach (vec3 pt in bndPts)
                    Trk.gArr[idx].curvePts.Add(pt);

                trkBuilderSelIdx = idx;
            }
            trkBuilderStart = 99999; trkBuilderEnd = 99999;
        }

        // ── Extend A/B de curva ──────────────────────────────────────────
        public void TrkBuilder_ExtendA()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= Trk.gArr.Count) return;
            if (Trk.gArr[trkBuilderSelIdx].mode != TrackMode.Curve) return;
            vec3 s = new vec3(Trk.gArr[trkBuilderSelIdx].curvePts[0]);
            for (int i = 1; i < 50; i++)
            {
                vec3 pt = new vec3(s);
                pt.easting -= (Math.Sin(pt.heading) * i);
                pt.northing -= (Math.Cos(pt.heading) * i);
                Trk.gArr[trkBuilderSelIdx].curvePts.Insert(0, pt);
            }
        }

        public void TrkBuilder_ExtendB()
        {
            if (trkBuilderSelIdx < 0 || trkBuilderSelIdx >= Trk.gArr.Count) return;
            if (Trk.gArr[trkBuilderSelIdx].mode != TrackMode.Curve) return;
            int ptCnt = Trk.gArr[trkBuilderSelIdx].curvePts.Count - 1;
            for (int i = 1; i < 50; i++)
            {
                vec3 pt = new vec3(Trk.gArr[trkBuilderSelIdx].curvePts[ptCnt]);
                pt.easting += (Math.Sin(pt.heading) * i);
                pt.northing += (Math.Cos(pt.heading) * i);
                Trk.gArr[trkBuilderSelIdx].curvePts.Add(pt);
            }
        }

        // ── Creación interactiva: Record Curve ─────────────────────────
        private bool trkBuilderRecording = false;
        private vec2 trkBuilderRecPtA;

        public void TrkBuilder_RecordCurveA()
        {
            trkBuilderRecPtA = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
            CurveField.desList?.Clear();
            CurveField.isMakingCurve = true;
            CurveField.isRecordingCurve = true;
            trkBuilderRecording = true;
        }

        public void TrkBuilder_RecordCurvePause()
        {
            if (!trkBuilderRecording) return;
            CurveField.isRecordingCurve = !CurveField.isRecordingCurve;
        }

        public string TrkBuilder_RecordCurveB(string name)
        {
            if (!trkBuilderRecording) return "no-recording";
            CurveField.isMakingCurve = false;
            CurveField.isRecordingCurve = false;
            trkBuilderRecording = false;

            vec2 ptB = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);

            int cnt = CurveField.desList.Count;
            if (cnt < 4) { CurveField.desList?.Clear(); return "pocos-puntos"; }

            CABCurve.MakePointMinimumSpacing(ref CurveField.desList, 1.6);
            CABCurve.CalculateHeadings(ref CurveField.desList);

            Trk.gArr.Add(new CTrk());
            int idx = Trk.gArr.Count - 1;

            Trk.gArr[idx].ptA = new vec2(trkBuilderRecPtA);
            Trk.gArr[idx].ptB = new vec2(ptB);
            Trk.gArr[idx].mode = TrackMode.Curve;

            double x = 0, y = 0;
            foreach (vec3 pt in CurveField.desList) { x += Math.Cos(pt.heading); y += Math.Sin(pt.heading); }
            x /= CurveField.desList.Count; y /= CurveField.desList.Count;
            double aveH = Math.Atan2(y, x);
            if (aveH < 0) aveH += glm.twoPI;
            Trk.gArr[idx].heading = aveH;

            CurveField.AddFirstLastPoints(ref CurveField.desList);
            CABCurve.CalculateHeadings(ref CurveField.desList);

            foreach (vec3 item in CurveField.desList)
                Trk.gArr[idx].curvePts.Add(item);

            Trk.gArr[idx].name = string.IsNullOrWhiteSpace(name)
                ? "Cu " + Math.Round(glm.toDegrees(aveH), 1).ToString(CultureInfo.InvariantCulture) + "°"
                : name;

            // Nudge a la derecha por defecto
            double dist = (Tool.width - Tool.overlap) * 0.5 + Tool.offset;
            Trk.idx = idx;
            Trk.NudgeRefCurve(dist);

            trkBuilderSelIdx = idx;
            CurveField.desList?.Clear();
            return null;
        }

        public void TrkBuilder_RecordCurveCancel()
        {
            CurveField.isMakingCurve = false;
            CurveField.isRecordingCurve = false;
            CurveField.desList?.Clear();
            trkBuilderRecording = false;
        }

        public bool TrkBuilder_IsRecording() { return trkBuilderRecording; }
        public int TrkBuilder_RecordedCount() { return trkBuilderRecording ? CurveField.desList.Count : 0; }

        // ── Geometría de tracks para el canvas ──────────────────────────
        public List<double[][]> TrkBuilder_FencesEN()
        {
            var list = new List<double[][]>();
            if (Bnd == null) return list;
            for (int j = 0; j < Bnd.bndList.Count; j++)
            {
                var src = Bnd.bndList[j].fenceLine;
                var outArr = new double[src.Count][];
                for (int i = 0; i < src.Count; i++)
                    outArr[i] = new double[] { src[i].easting, src[i].northing };
                list.Add(outArr);
            }
            return list;
        }

        // ── Close Cancel ────────────────────────────────────────────────
        public void TrkBuilder_CloseCancel()
        {
            CurveField.desList?.Clear();

            if (isBtnAutoSteerOn) ((IAutoSteerHost)this).PerformAutoSteerClick();
            if (Yt.isYouTurnBtnOn) ToggleYouTurn();

            Trk.gArr.Clear();
            foreach (var item in trkBuilderBackup)
                Trk.gArr.Add(new CTrk(item));

            Trk.idx = trkBuilderOrigIdx;
            CurveField.isCurveValid = false;
            ABLineField.isABValid = false;

            trkBuilderBackup.Clear();
            trkBuilderSelIdx = -1;
        }

        // Equivalente a FormGPS.FileSaveTracks (SaveOpen.Designer.cs) — mismo
        // streamer portable ya usado por OpenField (TrackFiles.Load).
        public void SaveTracks()
        {
            if (!IsJobStarted) return;
            try
            {
                string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
                TrackFiles.Save(dir, Trk.gArr);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: TrackLines.txt (save): " + ex.Message);
            }
        }
    }
}
