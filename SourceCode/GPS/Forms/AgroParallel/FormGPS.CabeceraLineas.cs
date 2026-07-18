// ============================================================================
// FormGPS.CabeceraLineas.cs — lógica del constructor de cabecera por líneas
// (cabecera-lineas.html, reemplazo de FormHeadAche). Port fiel: selección de
// puntos A/B sobre el contorno, líneas Curva/AB con extensión 30 m en las
// puntas, offset hacia adentro, ciclar/borrar/extender, y "Build" que arma
// bnd.bndList[0].hdLine buscando los cruces entre líneas consecutivas.
// Todos los métodos asumen hilo UI (el adapter marshalea con Invoke).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // Estado de sesión (equivalente a los fields de FormHeadAche).
        private bool cabLinIsA = true;
        private int cabLinStart = 99999, cabLinEnd = 99999;
        private int cabLinBndSelect = 0;

        internal sealed class CabLinTrackSnapshot
        {
            public string Name;
            public string Mode;              // "curve" | "ab"
            public List<double[]> Points = new List<double[]>();
        }

        internal sealed class CabLinSnapshot
        {
            public bool JobStarted;
            public bool HasBoundary;
            public string Units = "m";
            public double ToolWidthDisplay;
            public List<List<double[]>> Fences = new List<List<double[]>>();
            public int BndSelect;
            public List<CabLinTrackSnapshot> Tracks = new List<CabLinTrackSnapshot>();
            public int SelIdx = -1;
            public List<double[]> HdLine = new List<double[]>();
            public double[] APoint;
            public double[] BPoint;
            public bool IsSectionControlled;
            public string Error;
        }

        private static double[] CabLin_EN(vec3 v) { return new double[] { v.easting, v.northing }; }

        internal CabLinSnapshot CabLin_Snapshot(string error = null)
        {
            var s = new CabLinSnapshot
            {
                JobStarted = isJobStarted,
                HasBoundary = bnd.bndList.Count > 0 && bnd.bndList[0].fenceLine.Count > 0,
                Units = string.IsNullOrEmpty(unitsFtM) ? "m" : unitsFtM.Trim(),
                //m2FtOrM se setea en LoadSettings(); si un arranque parcial lo
                //dejó en 0, caemos a métrico (1.0) para no mostrar ancho 0.
                ToolWidthDisplay = (tool.width - tool.overlap) * (m2FtOrM > 0 ? m2FtOrM : 1.0),
                BndSelect = cabLinBndSelect,
                SelIdx = hdl.idx,
                IsSectionControlled = bnd.isSectionControlledByHeadland,
                Error = error
            };

            for (int j = 0; j < bnd.bndList.Count; j++)
            {
                var f = new List<double[]>(bnd.bndList[j].fenceLine.Count);
                foreach (vec3 p in bnd.bndList[j].fenceLine) f.Add(CabLin_EN(p));
                s.Fences.Add(f);
            }

            for (int i = 0; i < hdl.tracksArr.Count; i++)
            {
                var t = new CabLinTrackSnapshot
                {
                    Name = hdl.tracksArr[i].name,
                    Mode = hdl.tracksArr[i].mode == (int)TrackMode.AB ? "ab" : "curve"
                };
                foreach (vec3 p in hdl.tracksArr[i].trackPts) t.Points.Add(CabLin_EN(p));
                s.Tracks.Add(t);
            }

            if (bnd.bndList.Count > 0 && bnd.bndList[0].hdLine != null)
                foreach (vec3 p in bnd.bndList[0].hdLine) s.HdLine.Add(CabLin_EN(p));

            if (cabLinStart != 99999 && cabLinBndSelect < bnd.bndList.Count
                && cabLinStart < bnd.bndList[cabLinBndSelect].fenceLine.Count)
                s.APoint = CabLin_EN(bnd.bndList[cabLinBndSelect].fenceLine[cabLinStart]);
            if (cabLinEnd != 99999 && cabLinBndSelect < bnd.bndList.Count
                && cabLinEnd < bnd.bndList[cabLinBndSelect].fenceLine.Count)
                s.BPoint = CabLin_EN(bnd.bndList[cabLinBndSelect].fenceLine[cabLinEnd]);

            return s;
        }

        // FormHeadAche ctor + Load: preparar sesión de edición.
        internal CabLinSnapshot CabLin_Open()
        {
            if (!isJobStarted) return CabLin_Snapshot("sin-lote");
            if (bnd.bndList.Count == 0 || bnd.bndList[0].fenceLine.Count == 0)
                return CabLin_Snapshot("sin-contorno");

            CalculateMinMax();
            hdl.idx = -1;
            FileLoadHeadLines();
            bnd.bndList[0].hdLine?.Clear();

            cabLinIsA = true;
            cabLinStart = 99999; cabLinEnd = 99999;
            cabLinBndSelect = 0;
            return CabLin_Snapshot();
        }

        // oglSelf_MouseDown (parte lógica): tap en coords de campo E/N.
        internal CabLinSnapshot CabLin_Tap(double easting, double northing, string mode, double distanceDisplay)
        {
            if (!isJobStarted) return CabLin_Snapshot("sin-lote");
            if (bnd.bndList.Count == 0 || bnd.bndList[0].fenceLine.Count == 0)
                return CabLin_Snapshot("sin-contorno");

            bnd.bndList[0].hdLine?.Clear();
            hdl.idx = -1;

            if (cabLinIsA)
            {
                double minDistA = double.MaxValue;
                cabLinStart = 99999; cabLinEnd = 99999;

                for (int j = 0; j < bnd.bndList.Count; j++)
                {
                    for (int i = 0; i < bnd.bndList[j].fenceLine.Count; i++)
                    {
                        double dist = ((easting - bnd.bndList[j].fenceLine[i].easting) * (easting - bnd.bndList[j].fenceLine[i].easting))
                                        + ((northing - bnd.bndList[j].fenceLine[i].northing) * (northing - bnd.bndList[j].fenceLine[i].northing));
                        if (dist < minDistA)
                        {
                            minDistA = dist;
                            cabLinBndSelect = j;
                            cabLinStart = i;
                        }
                    }
                }

                cabLinIsA = false;
                return CabLin_Snapshot();
            }

            // Segundo tap: punto B en el mismo contorno.
            {
                double minDistA = double.MaxValue;
                int j = cabLinBndSelect;

                for (int i = 0; i < bnd.bndList[j].fenceLine.Count; i++)
                {
                    double dist = ((easting - bnd.bndList[j].fenceLine[i].easting) * (easting - bnd.bndList[j].fenceLine[i].easting))
                                    + ((northing - bnd.bndList[j].fenceLine[i].northing) * (northing - bnd.bndList[j].fenceLine[i].northing));
                    if (dist < minDistA)
                    {
                        minDistA = dist;
                        cabLinEnd = i;
                    }
                }

                cabLinIsA = true;

                if (cabLinStart == cabLinEnd)
                {
                    cabLinStart = 99999; cabLinEnd = 99999;
                    return CabLin_Snapshot("mismo-punto");
                }
            }

            int start = cabLinStart, end = cabLinEnd;
            int bndSelect = cabLinBndSelect;

            if (mode != "ab")
            {
                // ── Curva: copia del tramo de fence entre A y B (loop-aware) ──
                hdl.tracksArr.Add(new CHeadPath());
                hdl.idx = hdl.tracksArr.Count - 1;

                bool isLoop = false;
                int limit = end;

                if ((Math.Abs(start - end)) > (bnd.bndList[bndSelect].fenceLine.Count * 0.5))
                {
                    if (start < end) { (start, end) = (end, start); }

                    isLoop = true;
                    if (start < end)
                    {
                        limit = end;
                        end = 0;
                    }
                    else
                    {
                        limit = end;
                        end = bnd.bndList[bndSelect].fenceLine.Count;
                    }
                }
                else
                {
                    if (start > end) { (start, end) = (end, start); }
                }

                hdl.tracksArr[hdl.idx].a_point = start;
                hdl.tracksArr[hdl.idx].trackPts?.Clear();

                if (start < end)
                {
                    for (int i = start; i <= end; i++)
                    {
                        hdl.tracksArr[hdl.idx].trackPts.Add(new vec3(bnd.bndList[bndSelect].fenceLine[i]));

                        if (isLoop && i == bnd.bndList[bndSelect].fenceLine.Count - 1)
                        {
                            i = -1;
                            isLoop = false;
                            end = limit;
                        }
                    }
                }
                else
                {
                    for (int i = start; i >= end; i--)
                    {
                        hdl.tracksArr[hdl.idx].trackPts.Add(new vec3(bnd.bndList[bndSelect].fenceLine[i]));

                        if (isLoop && i == 0)
                        {
                            i = bnd.bndList[bndSelect].fenceLine.Count - 1;
                            isLoop = false;
                            end = limit;
                        }
                    }
                }

                CABCurve.CalculateHeadings(ref hdl.tracksArr[hdl.idx].trackPts);

                int ptCnt = hdl.tracksArr[hdl.idx].trackPts.Count - 1;

                for (int i = 1; i < 30; i++)
                {
                    vec3 pnt = new vec3(hdl.tracksArr[hdl.idx].trackPts[ptCnt]);
                    pnt.easting += (Math.Sin(pnt.heading) * i);
                    pnt.northing += (Math.Cos(pnt.heading) * i);
                    hdl.tracksArr[hdl.idx].trackPts.Add(pnt);
                }

                vec3 stat = new vec3(hdl.tracksArr[hdl.idx].trackPts[0]);

                for (int i = 1; i < 30; i++)
                {
                    vec3 pnt = new vec3(stat);
                    pnt.easting -= (Math.Sin(pnt.heading) * i);
                    pnt.northing -= (Math.Cos(pnt.heading) * i);
                    hdl.tracksArr[hdl.idx].trackPts.Insert(0, pnt);
                }

                hdl.tracksArr[hdl.idx].name = hdl.idx.ToString() + " Cu " + DateTime.Now.ToString("mm:ss", CultureInfo.InvariantCulture);
                hdl.tracksArr[hdl.idx].moveDistance = 0;
                hdl.tracksArr[hdl.idx].mode = (int)TrackMode.Curve;

                FileSaveHeadLines();
            }
            else
            {
                // ── Línea AB recta entre A y B (interpolada a 1 m) ──
                if ((Math.Abs(start - end)) > (bnd.bndList[bndSelect].fenceLine.Count * 0.5))
                {
                    if (start < end) { (start, end) = (end, start); }
                }
                else
                {
                    if (start > end) { (start, end) = (end, start); }
                }

                vec3 ptA = new vec3(bnd.bndList[bndSelect].fenceLine[start]);
                vec3 ptB = new vec3(bnd.bndList[bndSelect].fenceLine[end]);

                double abHead = Math.Atan2(
                    bnd.bndList[bndSelect].fenceLine[end].easting - bnd.bndList[bndSelect].fenceLine[start].easting,
                    bnd.bndList[bndSelect].fenceLine[end].northing - bnd.bndList[bndSelect].fenceLine[start].northing);
                if (abHead < 0) abHead += glm.twoPI;

                if (hdl.idx < hdl.tracksArr.Count - 1)
                {
                    hdl.idx++;
                    hdl.tracksArr.Insert(hdl.idx, new CHeadPath());
                }
                else
                {
                    hdl.tracksArr.Add(new CHeadPath());
                    hdl.idx = hdl.tracksArr.Count - 1;
                }

                hdl.tracksArr[hdl.idx].a_point = start;
                hdl.tracksArr[hdl.idx].trackPts?.Clear();

                ptA.heading = abHead;
                ptB.heading = abHead;

                for (int i = 0; i <= (int)(glm.Distance(ptA, ptB)); i++)
                {
                    vec3 ptC = new vec3(ptA)
                    {
                        easting = (Math.Sin(abHead) * i) + ptA.easting,
                        northing = (Math.Cos(abHead) * i) + ptA.northing,
                        heading = abHead
                    };
                    hdl.tracksArr[hdl.idx].trackPts.Add(ptC);
                }

                int ptCnt = hdl.tracksArr[hdl.idx].trackPts.Count - 1;

                for (int i = 1; i < 30; i++)
                {
                    vec3 pnt = new vec3(hdl.tracksArr[hdl.idx].trackPts[ptCnt]);
                    pnt.easting += (Math.Sin(pnt.heading) * i);
                    pnt.northing += (Math.Cos(pnt.heading) * i);
                    hdl.tracksArr[hdl.idx].trackPts.Add(pnt);
                }

                vec3 stat = new vec3(hdl.tracksArr[hdl.idx].trackPts[0]);

                for (int i = 1; i < 30; i++)
                {
                    vec3 pnt = new vec3(stat);
                    pnt.easting -= (Math.Sin(pnt.heading) * i);
                    pnt.northing -= (Math.Cos(pnt.heading) * i);
                    hdl.tracksArr[hdl.idx].trackPts.Insert(0, pnt);
                }

                hdl.tracksArr[hdl.idx].name = hdl.idx.ToString() + " AB " + DateTime.Now.ToString("hh:mm:ss", CultureInfo.InvariantCulture);
                hdl.tracksArr[hdl.idx].moveDistance = 0;
                hdl.tracksArr[hdl.idx].mode = (int)TrackMode.AB;

                FileSaveHeadLines();
            }

            cabLinStart = 99999; cabLinEnd = 99999;

            // ── Offset hacia adentro por distanceDisplay (unidades display) ──
            hdl.desList?.Clear();

            if (hdl.tracksArr.Count < 1 || hdl.idx == -1) return CabLin_Snapshot();

            //ftOrMtoM viene de LoadSettings(); fallback métrico si quedó en 0
            double distAway = distanceDisplay * (ftOrMtoM > 0 ? ftOrMtoM : 1.0);
            hdl.tracksArr[hdl.idx].moveDistance += distAway;

            double distSqAway = (distAway * distAway) - 0.01;
            vec3 point;

            int refCount = hdl.tracksArr[hdl.idx].trackPts.Count;
            for (int i = 0; i < refCount; i++)
            {
                point = new vec3(
                hdl.tracksArr[hdl.idx].trackPts[i].easting - (Math.Sin(glm.PIBy2 + hdl.tracksArr[hdl.idx].trackPts[i].heading) * distAway),
                hdl.tracksArr[hdl.idx].trackPts[i].northing - (Math.Cos(glm.PIBy2 + hdl.tracksArr[hdl.idx].trackPts[i].heading) * distAway),
                hdl.tracksArr[hdl.idx].trackPts[i].heading);
                bool Add = true;

                for (int t = 0; t < refCount; t++)
                {
                    double dist = ((point.easting - hdl.tracksArr[hdl.idx].trackPts[t].easting) * (point.easting - hdl.tracksArr[hdl.idx].trackPts[t].easting))
                        + ((point.northing - hdl.tracksArr[hdl.idx].trackPts[t].northing) * (point.northing - hdl.tracksArr[hdl.idx].trackPts[t].northing));
                    if (dist < distSqAway)
                    {
                        Add = false;
                        break;
                    }
                }

                if (Add)
                {
                    if (hdl.desList.Count > 0)
                    {
                        double dist = ((point.easting - hdl.desList[hdl.desList.Count - 1].easting) * (point.easting - hdl.desList[hdl.desList.Count - 1].easting))
                            + ((point.northing - hdl.desList[hdl.desList.Count - 1].northing) * (point.northing - hdl.desList[hdl.desList.Count - 1].northing));
                        if (dist > 1)
                            hdl.desList.Add(point);
                    }
                    else hdl.desList.Add(point);
                }
            }

            hdl.tracksArr[hdl.idx].trackPts.Clear();

            for (int i = 0; i < hdl.desList.Count; i++)
            {
                hdl.tracksArr[hdl.idx].trackPts.Add(new vec3(hdl.desList[i]));
            }

            hdl.desList?.Clear();

            return CabLin_Snapshot();
        }

        internal CabLinSnapshot CabLin_CancelTouch()
        {
            cabLinStart = 99999; cabLinEnd = 99999;
            cabLinIsA = true;
            curve.desList?.Clear();
            return CabLin_Snapshot();
        }

        internal CabLinSnapshot CabLin_Cycle(int dir)
        {
            bnd.bndList[0].hdLine?.Clear();

            if (hdl.tracksArr.Count > 0)
            {
                hdl.idx += (dir >= 0 ? 1 : -1);
                if (hdl.idx > (hdl.tracksArr.Count - 1)) hdl.idx = 0;
                if (hdl.idx < 0) hdl.idx = (hdl.tracksArr.Count - 1);
            }
            else hdl.idx = -1;

            return CabLin_Snapshot();
        }

        internal CabLinSnapshot CabLin_DeleteTrack()
        {
            if (hdl.tracksArr.Count > 0 && hdl.idx > -1)
            {
                hdl.tracksArr.RemoveAt(hdl.idx);
                hdl.idx--;
            }

            if (hdl.tracksArr.Count > 0)
            {
                if (hdl.idx == -1) hdl.idx++;
            }
            else hdl.idx = -1;

            return CabLin_Snapshot();
        }

        // btnALength/btnBLength (+9 m en pasos de 1 m) y btnAShrink/btnBShrink (-5 pts).
        internal CabLinSnapshot CabLin_Extend(string endSide, bool grow)
        {
            if (hdl.idx > -1)
            {
                if (endSide == "a")
                {
                    if (grow)
                    {
                        vec3 start = new vec3(hdl.tracksArr[hdl.idx].trackPts[0]);
                        for (int i = 1; i < 10; i++)
                        {
                            vec3 pt = new vec3(start);
                            pt.easting -= (Math.Sin(pt.heading) * i);
                            pt.northing -= (Math.Cos(pt.heading) * i);
                            hdl.tracksArr[hdl.idx].trackPts.Insert(0, pt);
                        }
                    }
                    else if (hdl.tracksArr[hdl.idx].trackPts.Count > 8)
                        hdl.tracksArr[hdl.idx].trackPts.RemoveRange(0, 5);
                }
                else
                {
                    if (grow)
                    {
                        int ptCnt = hdl.tracksArr[hdl.idx].trackPts.Count - 1;
                        for (int i = 1; i < 10; i++)
                        {
                            vec3 pt = new vec3(hdl.tracksArr[hdl.idx].trackPts[ptCnt]);
                            pt.easting += (Math.Sin(pt.heading) * i);
                            pt.northing += (Math.Cos(pt.heading) * i);
                            hdl.tracksArr[hdl.idx].trackPts.Add(pt);
                        }
                    }
                    else if (hdl.tracksArr[hdl.idx].trackPts.Count > 8)
                        hdl.tracksArr[hdl.idx].trackPts.RemoveRange(hdl.tracksArr[hdl.idx].trackPts.Count - 5, 5);
                }
            }

            return CabLin_Snapshot();
        }

        // btnBndLoop_Click: arma la cabecera con los cruces entre líneas.
        internal CabLinSnapshot CabLin_BuildHeadland()
        {
            if (bnd.bndList.Count == 0) return CabLin_Snapshot("sin-contorno");

            hdl.tracksArr.Sort((p, q) => p.a_point.CompareTo(q.a_point));
            FileSaveHeadLines();

            hdl.idx = -1;

            bnd.bndList[0].hdLine?.Clear();

            int nextLine = 0;
            var crossings = new List<int>();

            int isStart = 0;

            for (int lineNum = 0; lineNum < hdl.tracksArr.Count; lineNum++)
            {
                nextLine = lineNum - 1;
                if (nextLine < 0) nextLine = hdl.tracksArr.Count - 1;

                if (nextLine == lineNum)
                    return CabLin_Snapshot("una-sola-linea");

                for (int i = 0; i < hdl.tracksArr[lineNum].trackPts.Count - 2; i++)
                {
                    GeoLineSegment headPathSegment = hdl.tracksArr[lineNum].GetHeadPathSegment(i);
                    for (int k = 0; k < hdl.tracksArr[nextLine].trackPts.Count - 2; k++)
                    {
                        GeoLineSegment otherSegment = hdl.tracksArr[nextLine].GetHeadPathSegment(k);
                        GeoCoord? intersectionPoint = headPathSegment.IntersectionPoint(otherSegment);
                        if (intersectionPoint.HasValue)
                        {
                            if (isStart == 0) i++;
                            crossings.Add(i);
                            isStart++;
                            if (isStart == 2) goto again;
                            nextLine = lineNum + 1;

                            if (nextLine > hdl.tracksArr.Count - 1) nextLine = 0;
                        }
                    }
                }

            again:
                isStart = 0;
            }

            if (crossings.Count != hdl.tracksArr.Count * 2)
            {
                bnd.bndList[0].hdLine?.Clear();
                return CabLin_Snapshot("cruces");
            }

            for (int i = 0; i < hdl.tracksArr.Count; i++)
            {
                int low = crossings[i * 2];
                int high = crossings[i * 2 + 1];
                for (int k = low; k < high; k++)
                {
                    bnd.bndList[0].hdLine.Add(hdl.tracksArr[i].trackPts[k]);
                }
            }

            vec3[] hdArr;

            if (bnd.bndList[0].hdLine.Count > 0)
            {
                hdArr = new vec3[bnd.bndList[0].hdLine.Count];
                bnd.bndList[0].hdLine.CopyTo(hdArr);
                bnd.bndList[0].hdLine?.Clear();
            }
            else
            {
                bnd.bndList[0].hdLine?.Clear();
                return CabLin_Snapshot();
            }

            // headings + decimación por delta de rumbo (idéntico al nativo)
            for (int i = 1; i < hdArr.Length; i++)
            {
                hdArr[i - 1].heading = Math.Atan2(hdArr[i - 1].easting - hdArr[i].easting, hdArr[i - 1].northing - hdArr[i].northing);
                if (hdArr[i].heading < 0) hdArr[i].heading += glm.twoPI;
            }

            double delta = 0;
            for (int i = 0; i < hdArr.Length; i++)
            {
                if (i == 0)
                {
                    bnd.bndList[0].hdLine.Add(new vec3(hdArr[i].easting, hdArr[i].northing, hdArr[i].heading));
                    continue;
                }
                delta += (hdArr[i - 1].heading - hdArr[i].heading);

                if (Math.Abs(delta) > 0.005)
                {
                    vec3 pt = new vec3(hdArr[i].easting, hdArr[i].northing, hdArr[i].heading);
                    bnd.bndList[0].hdLine.Add(pt);
                    delta = 0;
                }
            }

            FileSaveHeadland();
            return CabLin_Snapshot();
        }

        // btnDeleteHeadland ("Reset").
        internal CabLinSnapshot CabLin_ResetHeadland()
        {
            cabLinStart = 99999; cabLinEnd = 99999;
            cabLinIsA = true;
            hdl.desList?.Clear();
            if (bnd.bndList.Count > 0) bnd.bndList[0].hdLine?.Clear();
            return CabLin_Snapshot();
        }

        // btnHeadlandOff: apagar cabecera y persistir.
        internal CabLinSnapshot CabLin_TurnOff()
        {
            if (bnd.bndList.Count > 0) bnd.bndList[0].hdLine?.Clear();
            FileSaveHeadland();
            bnd.isHeadlandOn = false;
            vehicle.isHydLiftOn = false;
            return CabLin_Snapshot();
        }

        internal void CabLin_SetSectionControlled(bool on)
        {
            bnd.isSectionControlledByHeadland = on;
            Properties.Settings.Default.setHeadland_isSectionControlled = on;
            Properties.Settings.Default.Save();
        }

        // FormClosing + bloque post-diálogo del launcher nativo.
        internal void CabLin_CloseSession()
        {
            if (!isJobStarted) return;

            FileSaveHeadLines();
            hdl.idx = hdl.tracksArr.Count > 0 ? 0 : -1;

            bnd.isHeadlandOn = (bnd.bndList.Count > 0 && bnd.bndList[0].hdLine.Count > 0);

            PanelsAndOGLSize();
            PanelUpdateRightAndBottom();
            SetZoom();
        }
    }
}
