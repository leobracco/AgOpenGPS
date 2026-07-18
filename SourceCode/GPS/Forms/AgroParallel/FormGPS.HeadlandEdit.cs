// ============================================================================
// FormGPS.HeadlandEdit.cs — geometría del editor de cabecera HTML (cabecera.html).
// Reemplaza el flujo "Build Around" de FormHeadLine. El algoritmo de offset es
// idéntico a FormHeadLine.btnBndLoop_Click; acá vive dentro de FormGPS para que
// el adapter IHeadlandEditService lo invoque sin abrir el form WinForms.
// Todos los métodos asumen que corren en el hilo UI (el adapter marshalea).
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Helpers;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // ── Sesión de reshape manual (fase 2, ex FormHeadLine slice) ────────
        private bool hdEdIsA = true;
        private int hdEdStart = 99999, hdEdEnd = 99999;
        private int hdEdBndSel = 0;
        private string hdEdMode;                                // "curve"|"ab"|null
        private readonly List<vec3> hdEdSlice = new List<vec3>();
        private readonly List<vec3> hdEdBackup = new List<vec3>();

        // true si hay contorno externo con puntos.
        internal bool HeadlandEdit_HasBoundary()
        {
            return bnd != null && bnd.bndList.Count > 0
                   && bnd.bndList[0].fenceLine != null
                   && bnd.bndList[0].fenceLine.Count > 0;
        }

        internal double[][] HeadlandEdit_FenceEN()
        {
            if (!HeadlandEdit_HasBoundary()) return new double[0][];
            var src = bnd.bndList[0].fenceLine;
            var outArr = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                outArr[i] = new double[] { src[i].easting, src[i].northing };
            return outArr;
        }

        internal double[][] HeadlandEdit_HeadlandEN()
        {
            if (!HeadlandEdit_HasBoundary()) return new double[0][];
            var src = bnd.bndList[0].hdLine;
            if (src == null) return new double[0][];
            var outArr = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                outArr[i] = new double[] { src[i].easting, src[i].northing };
            return outArr;
        }

        internal bool HeadlandEdit_IsHeadlandOn()
        {
            return bnd != null && bnd.bndList.Count > 0
                   && bnd.bndList[0].hdLine != null
                   && bnd.bndList[0].hdLine.Count > 0
                   && bnd.isHeadlandOn;
        }

        // Ancho útil de la herramienta en metros (para "usar ancho de herramienta").
        internal double HeadlandEdit_ToolWidthM()
        {
            return tool != null ? tool.width : 0.0;
        }

        // unitsFtM viene con espacio inicial (" m" / " ft"); lo normalizamos.
        internal string HeadlandEdit_Units()
        {
            return string.IsNullOrEmpty(unitsFtM) ? "m" : unitsFtM.Trim();
        }

        internal bool HeadlandEdit_IsSectionControlled()
        {
            return bnd != null && bnd.isSectionControlledByHeadland;
        }

        // Recalcula isHeadlandOn tras cualquier mutación de hdLine.
        private void HeadlandEdit_RefreshOnFlag()
        {
            bnd.isHeadlandOn = bnd.bndList.Count > 0
                               && bnd.bndList[0].hdLine != null
                               && bnd.bndList[0].hdLine.Count > 0;
        }

        // Offset idéntico a FormHeadLine.btnBndLoop_Click. distanceDisplay en
        // unidades display; ==0 copia contorno→cabecera. Devuelve false si el
        // offset colapsa (sin puntos válidos) — en ese caso NO toca hdLine.
        internal bool HeadlandEdit_BuildAround(double distanceDisplay)
        {
            if (!HeadlandEdit_HasBoundary()) return false;

            int ptCount = bnd.bndList[0].fenceLine.Count;

            if (distanceDisplay == 0)
            {
                hdl.desList.Clear();
                bnd.bndList[0].hdLine?.Clear();
                for (int i = 0; i < ptCount; i++)
                    bnd.bndList[0].hdLine.Add(new vec3(bnd.bndList[0].fenceLine[i]));
            }
            else
            {
                hdl.desList?.Clear();
                vec3 pt3 = new vec3();

                double moveDist = distanceDisplay * ftOrMtoM;
                double distSq = (moveDist) * (moveDist) * 0.999;

                for (int i = 0; i < ptCount; i++)
                {
                    pt3.easting = bnd.bndList[0].fenceLine[i].easting -
                        (Math.Sin(glm.PIBy2 + bnd.bndList[0].fenceLine[i].heading) * (moveDist));
                    pt3.northing = bnd.bndList[0].fenceLine[i].northing -
                        (Math.Cos(glm.PIBy2 + bnd.bndList[0].fenceLine[i].heading) * (moveDist));
                    pt3.heading = bnd.bndList[0].fenceLine[i].heading;

                    bool Add = true;
                    for (int j = 0; j < ptCount; j++)
                    {
                        double check = glm.DistanceSquared(pt3.northing, pt3.easting,
                                            bnd.bndList[0].fenceLine[j].northing, bnd.bndList[0].fenceLine[j].easting);
                        if (check < distSq) { Add = false; break; }
                    }

                    if (Add)
                    {
                        if (hdl.desList.Count > 0)
                        {
                            double dist = ((pt3.easting - hdl.desList[hdl.desList.Count - 1].easting) * (pt3.easting - hdl.desList[hdl.desList.Count - 1].easting))
                                + ((pt3.northing - hdl.desList[hdl.desList.Count - 1].northing) * (pt3.northing - hdl.desList[hdl.desList.Count - 1].northing));
                            if (dist > 1)
                                hdl.desList.Add(pt3);
                        }
                        else hdl.desList.Add(pt3);
                    }
                }

                if (hdl.desList.Count == 0)
                    return false;

                pt3 = new vec3(hdl.desList[0]);
                hdl.desList.Add(pt3);

                int cnt = hdl.desList.Count;
                if (cnt > 3)
                {
                    pt3 = new vec3(hdl.desList[0]);
                    hdl.desList.Add(pt3);

                    CABCurve.MakePointMinimumSpacing(ref hdl.desList, 1.2);
                    CABCurve.CalculateHeadings(ref hdl.desList);

                    bnd.bndList[0].hdLine.Clear();
                    foreach (vec3 item in hdl.desList)
                        bnd.bndList[0].hdLine.Add(item);
                }
            }

            FileSaveHeadland();
            HeadlandEdit_RefreshOnFlag();
            return true;
        }

        // Cabecera = copia del contorno.
        internal void HeadlandEdit_Reset()
        {
            if (!HeadlandEdit_HasBoundary()) return;
            hdl.desList.Clear();
            bnd.bndList[0].hdLine?.Clear();
            int ptCount = bnd.bndList[0].fenceLine.Count;
            for (int i = 0; i < ptCount; i++)
                bnd.bndList[0].hdLine.Add(new vec3(bnd.bndList[0].fenceLine[i]));
            FileSaveHeadland();
            HeadlandEdit_RefreshOnFlag();
        }

        // Apagar cabecera: limpiar hdLine + persistir.
        internal void HeadlandEdit_TurnOff()
        {
            if (bnd == null || bnd.bndList.Count == 0) return;
            bnd.bndList[0].hdLine?.Clear();
            FileSaveHeadland();
            bnd.isHeadlandOn = false;
        }

        internal void HeadlandEdit_SetSectionControlled(bool on)
        {
            if (bnd == null) return;
            bnd.isSectionControlledByHeadland = on;
            Properties.Settings.Default.setHeadland_isSectionControlled = on;
            Properties.Settings.Default.Save();
        }

        // ════════════════════════════════════════════════════════════════════
        //  Reshape manual — port fiel de FormHeadLine (slice + clip + undo)
        // ════════════════════════════════════════════════════════════════════

        internal List<double[][]> HeadlandEdit_FencesEN()
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

        internal int HeadlandEdit_BndSelect() { return hdEdBndSel; }
        internal string HeadlandEdit_SliceMode() { return hdEdSlice.Count > 0 ? hdEdMode : null; }
        internal bool HeadlandEdit_CanUndo() { return hdEdBackup.Count > 0; }

        internal double[][] HeadlandEdit_SliceEN()
        {
            var outArr = new double[hdEdSlice.Count][];
            for (int i = 0; i < hdEdSlice.Count; i++)
                outArr[i] = new double[] { hdEdSlice[i].easting, hdEdSlice[i].northing };
            return outArr;
        }

        internal double[] HeadlandEdit_APointEN()
        {
            if (hdEdStart == 99999 || hdEdBndSel >= bnd.bndList.Count
                || hdEdStart >= bnd.bndList[hdEdBndSel].fenceLine.Count) return null;
            var p = bnd.bndList[hdEdBndSel].fenceLine[hdEdStart];
            return new double[] { p.easting, p.northing };
        }

        internal double[] HeadlandEdit_BPointEN()
        {
            if (hdEdEnd == 99999 || hdEdBndSel >= bnd.bndList.Count
                || hdEdEnd >= bnd.bndList[hdEdBndSel].fenceLine.Count) return null;
            var p = bnd.bndList[hdEdBndSel].fenceLine[hdEdEnd];
            return new double[] { p.easting, p.northing };
        }

        // FormHeadLine_Load: hdl.idx=-1, hdLine=contorno si estaba vacía, si no
        // spacing mínimo + headings. Limpia el estado de toques.
        internal string HeadlandEdit_Open()
        {
            if (!HeadlandEdit_HasBoundary()) return "sin-contorno";

            CalculateMinMax();
            hdl.idx = -1;
            hdEdStart = 99999; hdEdEnd = 99999;
            hdEdIsA = true; hdEdBndSel = 0; hdEdMode = null;
            hdl.desList?.Clear();
            hdEdSlice.Clear();
            hdEdBackup.Clear();

            if (bnd.bndList[0].hdLine.Count == 0)
            {
                bnd.bndList[0].hdLine?.Clear();
                for (int i = 0; i < bnd.bndList[0].fenceLine.Count; i++)
                    bnd.bndList[0].hdLine.Add(new vec3(bnd.bndList[0].fenceLine[i]));
            }
            else
            {
                CABCurve.MakePointMinimumSpacing(ref bnd.bndList[0].hdLine, 1.2);
                CABCurve.CalculateHeadings(ref bnd.bndList[0].hdLine);
            }
            return null;
        }

        // oglSelf_MouseDown desde coordenadas de campo. mode: "curve"|"ab".
        internal string HeadlandEdit_Tap(double easting, double northing, string mode, double distanceDisplay)
        {
            if (!HeadlandEdit_HasBoundary()) return "sin-contorno";

            //nativo: con curva la distancia 0 no mueve nada → error
            if (distanceDisplay == 0 && mode == "curve")
                return "distancia-cero";

            hdEdSlice.Clear();

            if (hdEdIsA)
            {
                double minDistA = double.MaxValue;
                hdEdStart = 99999; hdEdEnd = 99999;

                for (int j = 0; j < bnd.bndList.Count; j++)
                {
                    for (int i = 0; i < bnd.bndList[j].fenceLine.Count; i++)
                    {
                        double dist = ((easting - bnd.bndList[j].fenceLine[i].easting) * (easting - bnd.bndList[j].fenceLine[i].easting))
                                        + ((northing - bnd.bndList[j].fenceLine[i].northing) * (northing - bnd.bndList[j].fenceLine[i].northing));
                        if (dist < minDistA)
                        {
                            minDistA = dist;
                            hdEdBndSel = j;
                            hdEdStart = i;
                        }
                    }
                }

                hdEdIsA = false;
                return null;
            }

            //segundo toque → punto B en el mismo contorno
            {
                double minDistA = double.MaxValue;
                int j2 = hdEdBndSel;

                for (int i = 0; i < bnd.bndList[j2].fenceLine.Count; i++)
                {
                    double dist = ((easting - bnd.bndList[j2].fenceLine[i].easting) * (easting - bnd.bndList[j2].fenceLine[i].easting))
                                    + ((northing - bnd.bndList[j2].fenceLine[i].northing) * (northing - bnd.bndList[j2].fenceLine[i].northing));
                    if (dist < minDistA)
                    {
                        minDistA = dist;
                        hdEdEnd = i;
                    }
                }

                hdEdIsA = true;
            }

            int start = hdEdStart, end = hdEdEnd;
            int bndSelect = hdEdBndSel;

            if (mode == "curve")
            {
                bool isLoop = false;
                int limit = end;

                if ((Math.Abs(start - end)) > (bnd.bndList[bndSelect].fenceLine.Count * 0.5))
                {
                    if (start < end) (start, end) = (end, start);

                    isLoop = true;
                    if (start < end) { limit = end; end = 0; }
                    else { limit = end; end = bnd.bndList[bndSelect].fenceLine.Count; }
                }
                else
                {
                    if (start > end) (start, end) = (end, start);
                }

                hdEdSlice.Clear();
                vec3 pt3;

                if (start < end)
                {
                    for (int i = start; i <= end; i++)
                    {
                        pt3 = bnd.bndList[bndSelect].fenceLine[i];
                        hdEdSlice.Add(pt3);

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
                        pt3 = bnd.bndList[bndSelect].fenceLine[i];
                        hdEdSlice.Add(pt3);

                        if (isLoop && i == 0)
                        {
                            i = bnd.bndList[bndSelect].fenceLine.Count - 1;
                            isLoop = false;
                            end = limit;
                        }
                    }
                }

                int ptCnt = hdEdSlice.Count - 1;

                if (ptCnt > 0)
                {
                    var slice = hdEdSlice;
                    var copy = new List<vec3>(slice);
                    CABCurve.CalculateHeadings(ref copy);
                    hdEdSlice.Clear();
                    hdEdSlice.AddRange(copy);

                    ptCnt = hdEdSlice.Count - 1;
                    for (int i = 1; i < 30; i++)
                    {
                        vec3 pt = new vec3(hdEdSlice[ptCnt]);
                        pt.easting += (Math.Sin(pt.heading) * i);
                        pt.northing += (Math.Cos(pt.heading) * i);
                        hdEdSlice.Add(pt);
                    }

                    vec3 stat = new vec3(hdEdSlice[0]);
                    for (int i = 1; i < 30; i++)
                    {
                        vec3 pt = new vec3(stat);
                        pt.easting -= (Math.Sin(pt.heading) * i);
                        pt.northing -= (Math.Cos(pt.heading) * i);
                        hdEdSlice.Insert(0, pt);
                    }

                    hdEdMode = "curve";
                }
                else
                {
                    hdEdStart = 99999; hdEdEnd = 99999;
                    return null;
                }

                hdEdStart = 99999; hdEdEnd = 99999;
            }
            else //recta AB
            {
                if ((Math.Abs(start - end)) > (bnd.bndList[bndSelect].fenceLine.Count * 0.5))
                {
                    if (start < end) (start, end) = (end, start);
                }
                else
                {
                    if (start > end) (start, end) = (end, start);
                }

                vec3 ptA = new vec3(bnd.bndList[bndSelect].fenceLine[start]);
                vec3 ptB = new vec3(bnd.bndList[bndSelect].fenceLine[end]);

                double abHead = Math.Atan2(
                    bnd.bndList[bndSelect].fenceLine[end].easting - bnd.bndList[bndSelect].fenceLine[start].easting,
                    bnd.bndList[bndSelect].fenceLine[end].northing - bnd.bndList[bndSelect].fenceLine[start].northing);
                if (abHead < 0) abHead += glm.twoPI;

                hdEdSlice.Clear();

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
                    hdEdSlice.Add(ptC);
                }

                int ptCnt = hdEdSlice.Count - 1;

                for (int i = 1; i < 30; i++)
                {
                    vec3 pt = new vec3(hdEdSlice[ptCnt]);
                    pt.easting += (Math.Sin(pt.heading) * i);
                    pt.northing += (Math.Cos(pt.heading) * i);
                    hdEdSlice.Add(pt);
                }

                vec3 stat = new vec3(hdEdSlice[0]);
                for (int i = 1; i < 30; i++)
                {
                    vec3 pt = new vec3(stat);
                    pt.easting -= (Math.Sin(pt.heading) * i);
                    pt.northing -= (Math.Cos(pt.heading) * i);
                    hdEdSlice.Insert(0, pt);
                }

                hdEdMode = "ab";
                hdEdStart = 99999; hdEdEnd = 99999;
            }

            //offset hacia adentro (SetLineDistance)
            if (distanceDisplay != 0)
                HeadlandEdit_SetLineDistance(distanceDisplay);

            return null;
        }

        // SetLineDistance: offsetea la línea de corte hacia adentro con culling
        // de auto-intersección + espaciado mínimo 1 m.
        private void HeadlandEdit_SetLineDistance(double distanceDisplay)
        {
            hdl.desList?.Clear();

            if (hdEdSlice.Count < 1) return;

            //ftOrMtoM viene de LoadSettings(); fallback métrico si quedó en 0
            double distAway = distanceDisplay * (ftOrMtoM > 0 ? ftOrMtoM : 1.0);

            double distSqAway = (distAway * distAway) - 0.01;
            vec3 point;

            int refCount = hdEdSlice.Count;
            for (int i = 0; i < refCount; i++)
            {
                point = new vec3(
                hdEdSlice[i].easting - (Math.Sin(glm.PIBy2 + hdEdSlice[i].heading) * distAway),
                hdEdSlice[i].northing - (Math.Cos(glm.PIBy2 + hdEdSlice[i].heading) * distAway),
                hdEdSlice[i].heading);
                bool Add = true;

                for (int t = 0; t < refCount; t++)
                {
                    double dist = ((point.easting - hdEdSlice[t].easting) * (point.easting - hdEdSlice[t].easting))
                        + ((point.northing - hdEdSlice[t].northing) * (point.northing - hdEdSlice[t].northing));
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

            hdEdSlice.Clear();
            for (int i = 0; i < hdl.desList.Count; i++)
                hdEdSlice.Add(new vec3(hdl.desList[i]));

            hdl.desList?.Clear();
        }

        internal void HeadlandEdit_CancelTouch()
        {
            hdEdStart = 99999; hdEdEnd = 99999;
            hdEdIsA = true;
            hdEdSlice.Clear();
            hdEdMode = null;
        }

        // btnALength/btnBLength (+9 m en pasos de 1 m) y btnAShrink/btnBShrink (-5 pts).
        internal void HeadlandEdit_Extend(string endSide, bool grow)
        {
            if (hdEdSlice.Count == 0) return;

            if (endSide == "a")
            {
                if (grow)
                {
                    vec3 start = new vec3(hdEdSlice[0]);
                    for (int i = 1; i < 10; i++)
                    {
                        vec3 pt = new vec3(start);
                        pt.easting -= (Math.Sin(pt.heading) * i);
                        pt.northing -= (Math.Cos(pt.heading) * i);
                        hdEdSlice.Insert(0, pt);
                    }
                }
                else if (hdEdSlice.Count > 8)
                    hdEdSlice.RemoveRange(0, 5);
            }
            else
            {
                if (grow)
                {
                    int ptCnt = hdEdSlice.Count - 1;
                    for (int i = 1; i < 10; i++)
                    {
                        vec3 pt = new vec3(hdEdSlice[ptCnt]);
                        pt.easting += (Math.Sin(pt.heading) * i);
                        pt.northing += (Math.Cos(pt.heading) * i);
                        hdEdSlice.Add(pt);
                    }
                }
                else if (hdEdSlice.Count > 8)
                    hdEdSlice.RemoveRange(hdEdSlice.Count - 5, 5);
            }
        }

        // btnSlice_Click: corta la cabecera con la línea de corte. Backup previo
        // para Undo. Error "cruces" si no encuentra 2 cruces.
        internal string HeadlandEdit_Clip()
        {
            int startBnd = 0, endBnd = 0, startLine = 0, endLine = 0;
            int isStart = 0;

            if (hdEdSlice.Count == 0) return null;

            //backup para Undo
            hdEdBackup.Clear();
            foreach (var item in bnd.bndList[0].hdLine)
                hdEdBackup.Add(item);

            for (int i = 0; i < hdEdSlice.Count - 2; i++)
            {
                for (int k = 0; k < bnd.bndList[0].hdLine.Count - 2; k++)
                {
                    GeoLineSegment sliceSegment = GeoRefactorHelper.GetLineSegment(hdEdSlice, i);
                    GeoLineSegment headLineSegment = bnd.bndList[0].GetHeadLineSegment(k);
                    GeoCoord? intersectionPoint = sliceSegment.IntersectionPoint(headLineSegment);

                    if (intersectionPoint.HasValue)
                    {
                        if (isStart == 0)
                        {
                            startBnd = k + 1;
                            startLine = i + 1;
                        }
                        else
                        {
                            endBnd = k + 1;
                            endLine = i;
                        }
                        isStart++;
                    }
                }
            }

            if (isStart < 2)
            {
                hdEdBackup.Clear();
                return "cruces";
            }

            //cruza el empalme inicio/fin de la cabecera
            if ((Math.Abs(startBnd - endBnd)) > (bnd.bndList[hdEdBndSel].fenceLine.Count * 0.5))
            {
                if (startBnd < endBnd) (startBnd, endBnd) = (endBnd, startBnd);

                hdl.desList?.Clear();

                for (int i = endBnd; i < startBnd; i++)
                    hdl.desList.Add(bnd.bndList[0].hdLine[i]);

                for (int i = startLine; i < endLine; i++)
                    hdl.desList.Add(hdEdSlice[i]);

                bnd.bndList[0].hdLine.Clear();
                foreach (var item in hdl.desList)
                    bnd.bndList[0].hdLine.Add(item);
            }
            //completamente entre inicio y fin
            else
            {
                if (startBnd > endBnd) (startBnd, endBnd) = (endBnd, startBnd);

                hdl.desList?.Clear();

                for (int i = 0; i < startBnd; i++)
                    hdl.desList.Add(bnd.bndList[0].hdLine[i]);

                for (int i = startLine; i < endLine; i++)
                    hdl.desList.Add(hdEdSlice[i]);

                for (int i = endBnd; i < bnd.bndList[0].hdLine.Count; i++)
                    hdl.desList.Add(bnd.bndList[0].hdLine[i]);

                bnd.bndList[0].hdLine.Clear();
                foreach (var item in hdl.desList)
                    bnd.bndList[0].hdLine.Add(item);
            }

            hdl.desList?.Clear();
            hdEdSlice.Clear();
            hdEdMode = null;
            return null;
        }

        internal void HeadlandEdit_Undo()
        {
            if (hdEdBackup.Count == 0) return;
            bnd.bndList[0].hdLine?.Clear();
            foreach (var item in hdEdBackup)
                bnd.bndList[0].hdLine.Add(item);
            hdEdBackup.Clear();
        }

        // btnExit + bloque post-diálogo de GetHeadland(): suavizado por
        // decimación de rumbo (delta>0.005), persistir, recalcular isHeadlandOn
        // y refrescar paneles.
        internal void HeadlandEdit_CloseSession()
        {
            if (bnd == null || bnd.bndList.Count == 0) return;

            hdl.idx = hdEdSlice.Count > 0 ? 0 : -1;
            hdEdSlice.Clear();
            hdEdBackup.Clear();
            hdEdMode = null;
            hdEdStart = 99999; hdEdEnd = 99999; hdEdIsA = true;

            if (bnd.bndList[0].hdLine.Count > 0)
            {
                vec3[] hdArr = new vec3[bnd.bndList[0].hdLine.Count];
                bnd.bndList[0].hdLine.CopyTo(hdArr);
                bnd.bndList[0].hdLine?.Clear();

                //rumbos intermedios
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
                        bnd.bndList[0].hdLine.Add(new vec3(hdArr[i].easting, hdArr[i].northing, hdArr[i].heading));
                        delta = 0;
                    }
                }
                vec3 ptEnd = new vec3(hdArr[hdArr.Length - 1].easting, hdArr[hdArr.Length - 1].northing, hdArr[hdArr.Length - 1].heading);
                bnd.bndList[0].hdLine.Add(ptEnd);
            }

            FileSaveHeadland();

            //bloque post-diálogo del launcher nativo (GetHeadland)
            bnd.isHeadlandOn = (bnd.bndList.Count > 0 && bnd.bndList[0].hdLine.Count > 0);
            PanelsAndOGLSize();
            PanelUpdateRightAndBottom();
            SetZoom();
        }
    }
}
