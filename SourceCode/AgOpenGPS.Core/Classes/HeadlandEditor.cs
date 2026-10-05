// ============================================================================
// HeadlandEditor.cs — geometría del editor de cabecera (cabecera.html).
//
// Era `partial class FormGPS` (FormGPS.HeadlandEdit.cs) y por eso SOLO existía
// bajo WinForms: contra el motor headless /api/headland daba 404 y la pantalla
// Cabecera no podía construir nada. Cuarto caso del mismo hueco (perfiles,
// banderas, contorno, cabecera).
//
// El código no tenía UNA sola dependencia de WinForms — 738 líneas de pura
// geometría — así que no se reescribió: se movió tal cual y lo que tocaba del
// host pasó a campos y callbacks. FormGPS ahora delega acá, así que los dos
// stacks corren EXACTAMENTE el mismo algoritmo y no hay dos cabeceras que se
// puedan desincronizar.
//
// El algoritmo de offset es el de FormHeadLine.btnBndLoop_Click, sin cambios.
// No es thread-safe: cada host lo llama desde su propio hilo (FormGPS desde el
// hilo UI, el motor desde el hilo del request).
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public sealed class HeadlandEditor
    {
        private readonly CBoundary _bnd;
        private readonly CHeadLine _hdl;
        private readonly CTool _tool;

        /// <summary>Persistir la cabecera (Headland.txt). Lo hace el host, que
        /// es quien sabe dónde vive el lote.</summary>
        private readonly Action _guardar;

        /// <summary>Factor de unidades display→metros: 1.0 métrico, glm.ft2m en
        /// pies. Es Func y no valor porque el operario puede cambiar unidades
        /// con el editor abierto.</summary>
        private readonly Func<double> _ftOrMtoMFn;

        /// <summary>"m" o "ft" para mostrar.</summary>
        private readonly Func<string> _unidadesFn;

        /// <summary>Guardar el flag "secciones controladas por cabecera" en la
        /// config del host.</summary>
        private readonly Action<bool> _guardarSeccionControlada;

        /// <summary>Recalcular la extensión del lote (bounding box). En FormGPS
        /// es CalculateMinMax; en el motor headless no hay nada que recalcular
        /// para dibujar, así que va vacío.</summary>
        private readonly Action _recalcularExtension;

        /// <summary>Refrescar paneles/zoom del host. Solo WinForms; en el motor
        /// va vacío.</summary>
        private readonly Action _refrescarUi;

        public HeadlandEditor(
            CBoundary bnd,
            CHeadLine hdl,
            CTool tool,
            Action guardar,
            Func<double> ftOrMtoM = null,
            Func<string> unidades = null,
            Action<bool> guardarSeccionControlada = null,
            Action recalcularExtension = null,
            Action refrescarUi = null)
        {
            _bnd = bnd;
            _hdl = hdl;
            _tool = tool;
            _guardar = guardar ?? (() => { });
            _ftOrMtoMFn = ftOrMtoM ?? (() => 1.0);
            _unidadesFn = unidades ?? (() => "m");
            _guardarSeccionControlada = guardarSeccionControlada ?? (_ => { });
            _recalcularExtension = recalcularExtension ?? (() => { });
            _refrescarUi = refrescarUi ?? (() => { });
        }

        private double _ftOrMtoM => _ftOrMtoMFn();
        private string _unidades => _unidadesFn();

        /// <summary>
        /// Segmento i→i+1 de una polilínea, cerrando contra el punto 0 en el
        /// último. Es GeoRefactorHelper.GetLineSegment, que vive en el proyecto
        /// GPS (WinForms) y por eso no se puede referenciar desde Core; son dos
        /// líneas y traer el helper entero acá era mover código ajeno de carril.
        /// </summary>
        private static GeoLineSegment SegmentoDe(List<vec3> lista, int i)
        {
            int sig = (i + 1) % lista.Count;
            return new GeoLineSegment(lista[i].ToGeoCoord(), lista[sig].ToGeoCoord());
        }

        // ── Sesión de reshape manual (fase 2, ex FormHeadLine slice) ────────
        private bool hdEdIsA = true;
        private int hdEdStart = 99999, hdEdEnd = 99999;
        private int hdEdBndSel = 0;
        private string hdEdMode;                                // "curve"|"ab"|null
        private readonly List<vec3> hdEdSlice = new List<vec3>();
        private readonly List<vec3> hdEdBackup = new List<vec3>();

        // true si hay contorno externo con puntos.
        public bool E_HasBoundary()
        {
            return _bnd != null && _bnd.bndList.Count > 0
                   && _bnd.bndList[0].fenceLine != null
                   && _bnd.bndList[0].fenceLine.Count > 0;
        }

        public double[][] E_FenceEN()
        {
            if (!E_HasBoundary()) return new double[0][];
            var src = _bnd.bndList[0].fenceLine;
            var outArr = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                outArr[i] = new double[] { src[i].easting, src[i].northing };
            return outArr;
        }

        public double[][] E_HeadlandEN()
        {
            if (!E_HasBoundary()) return new double[0][];
            var src = _bnd.bndList[0].hdLine;
            if (src == null) return new double[0][];
            var outArr = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                outArr[i] = new double[] { src[i].easting, src[i].northing };
            return outArr;
        }

        public bool E_IsHeadlandOn()
        {
            return _bnd != null && _bnd.bndList.Count > 0
                   && _bnd.bndList[0].hdLine != null
                   && _bnd.bndList[0].hdLine.Count > 0
                   && _bnd.isHeadlandOn;
        }

        // Ancho útil de la herramienta en metros (para "usar ancho de herramienta").
        public double E_ToolWidthM()
        {
            return _tool != null ? _tool.width : 0.0;
        }

        // _unidades viene con espacio inicial (" m" / " ft"); lo normalizamos.
        public string E_Units()
        {
            return string.IsNullOrEmpty(_unidades) ? "m" : _unidades.Trim();
        }

        public bool E_IsSectionControlled()
        {
            return _bnd != null && _bnd.isSectionControlledByHeadland;
        }

        // Recalcula isHeadlandOn tras cualquier mutación de hdLine.
        private void E_RefreshOnFlag()
        {
            _bnd.isHeadlandOn = _bnd.bndList.Count > 0
                               && _bnd.bndList[0].hdLine != null
                               && _bnd.bndList[0].hdLine.Count > 0;
        }

        // Offset idéntico a FormHeadLine.btnBndLoop_Click. distanceDisplay en
        // unidades display; ==0 copia contorno→cabecera. Devuelve false si el
        // offset colapsa (sin puntos válidos) — en ese caso NO toca hdLine.
        public bool E_BuildAround(double distanceDisplay)
        {
            if (!E_HasBoundary()) return false;

            int ptCount = _bnd.bndList[0].fenceLine.Count;

            if (distanceDisplay == 0)
            {
                _hdl.desList.Clear();
                _bnd.bndList[0].hdLine?.Clear();
                for (int i = 0; i < ptCount; i++)
                    _bnd.bndList[0].hdLine.Add(new vec3(_bnd.bndList[0].fenceLine[i]));
            }
            else
            {
                _hdl.desList?.Clear();
                vec3 pt3 = new vec3();

                double moveDist = distanceDisplay * _ftOrMtoM;
                double distSq = (moveDist) * (moveDist) * 0.999;

                for (int i = 0; i < ptCount; i++)
                {
                    pt3.easting = _bnd.bndList[0].fenceLine[i].easting -
                        (Math.Sin(glm.PIBy2 + _bnd.bndList[0].fenceLine[i].heading) * (moveDist));
                    pt3.northing = _bnd.bndList[0].fenceLine[i].northing -
                        (Math.Cos(glm.PIBy2 + _bnd.bndList[0].fenceLine[i].heading) * (moveDist));
                    pt3.heading = _bnd.bndList[0].fenceLine[i].heading;

                    bool Add = true;
                    for (int j = 0; j < ptCount; j++)
                    {
                        double check = glm.DistanceSquared(pt3.northing, pt3.easting,
                                            _bnd.bndList[0].fenceLine[j].northing, _bnd.bndList[0].fenceLine[j].easting);
                        if (check < distSq) { Add = false; break; }
                    }

                    if (Add)
                    {
                        if (_hdl.desList.Count > 0)
                        {
                            double dist = ((pt3.easting - _hdl.desList[_hdl.desList.Count - 1].easting) * (pt3.easting - _hdl.desList[_hdl.desList.Count - 1].easting))
                                + ((pt3.northing - _hdl.desList[_hdl.desList.Count - 1].northing) * (pt3.northing - _hdl.desList[_hdl.desList.Count - 1].northing));
                            if (dist > 1)
                                _hdl.desList.Add(pt3);
                        }
                        else _hdl.desList.Add(pt3);
                    }
                }

                if (_hdl.desList.Count == 0)
                    return false;

                pt3 = new vec3(_hdl.desList[0]);
                _hdl.desList.Add(pt3);

                int cnt = _hdl.desList.Count;
                if (cnt > 3)
                {
                    pt3 = new vec3(_hdl.desList[0]);
                    _hdl.desList.Add(pt3);

                    CABCurve.MakePointMinimumSpacing(ref _hdl.desList, 1.2);
                    CABCurve.CalculateHeadings(ref _hdl.desList);

                    _bnd.bndList[0].hdLine.Clear();
                    foreach (vec3 item in _hdl.desList)
                        _bnd.bndList[0].hdLine.Add(item);
                }
            }

            _guardar();
            E_RefreshOnFlag();
            return true;
        }

        // Cabecera = copia del contorno.
        public void E_Reset()
        {
            if (!E_HasBoundary()) return;
            _hdl.desList.Clear();
            _bnd.bndList[0].hdLine?.Clear();
            int ptCount = _bnd.bndList[0].fenceLine.Count;
            for (int i = 0; i < ptCount; i++)
                _bnd.bndList[0].hdLine.Add(new vec3(_bnd.bndList[0].fenceLine[i]));
            _guardar();
            E_RefreshOnFlag();
        }

        // Apagar cabecera: limpiar hdLine + persistir.
        public void E_TurnOff()
        {
            if (_bnd == null || _bnd.bndList.Count == 0) return;
            _bnd.bndList[0].hdLine?.Clear();
            _guardar();
            _bnd.isHeadlandOn = false;
        }

        public void E_SetSectionControlled(bool on)
        {
            if (_bnd == null) return;
            _bnd.isSectionControlledByHeadland = on;
            _guardarSeccionControlada(on);

        }

        // ════════════════════════════════════════════════════════════════════
        //  Reshape manual — port fiel de FormHeadLine (slice + clip + undo)
        // ════════════════════════════════════════════════════════════════════

        public List<double[][]> E_FencesEN()
        {
            var list = new List<double[][]>();
            if (_bnd == null) return list;
            for (int j = 0; j < _bnd.bndList.Count; j++)
            {
                var src = _bnd.bndList[j].fenceLine;
                var outArr = new double[src.Count][];
                for (int i = 0; i < src.Count; i++)
                    outArr[i] = new double[] { src[i].easting, src[i].northing };
                list.Add(outArr);
            }
            return list;
        }

        public int E_BndSelect() { return hdEdBndSel; }
        public string E_SliceMode() { return hdEdSlice.Count > 0 ? hdEdMode : null; }
        public bool E_CanUndo() { return hdEdBackup.Count > 0; }

        public double[][] E_SliceEN()
        {
            var outArr = new double[hdEdSlice.Count][];
            for (int i = 0; i < hdEdSlice.Count; i++)
                outArr[i] = new double[] { hdEdSlice[i].easting, hdEdSlice[i].northing };
            return outArr;
        }

        public double[] E_APointEN()
        {
            if (hdEdStart == 99999 || hdEdBndSel >= _bnd.bndList.Count
                || hdEdStart >= _bnd.bndList[hdEdBndSel].fenceLine.Count) return null;
            var p = _bnd.bndList[hdEdBndSel].fenceLine[hdEdStart];
            return new double[] { p.easting, p.northing };
        }

        public double[] E_BPointEN()
        {
            if (hdEdEnd == 99999 || hdEdBndSel >= _bnd.bndList.Count
                || hdEdEnd >= _bnd.bndList[hdEdBndSel].fenceLine.Count) return null;
            var p = _bnd.bndList[hdEdBndSel].fenceLine[hdEdEnd];
            return new double[] { p.easting, p.northing };
        }

        // FormHeadLine_Load: _hdl.idx=-1, hdLine=contorno si estaba vacía, si no
        // spacing mínimo + headings. Limpia el estado de toques.
        public string E_Open()
        {
            if (!E_HasBoundary()) return "sin-contorno";

            _recalcularExtension();
            _hdl.idx = -1;
            hdEdStart = 99999; hdEdEnd = 99999;
            hdEdIsA = true; hdEdBndSel = 0; hdEdMode = null;
            _hdl.desList?.Clear();
            hdEdSlice.Clear();
            hdEdBackup.Clear();

            if (_bnd.bndList[0].hdLine.Count == 0)
            {
                _bnd.bndList[0].hdLine?.Clear();
                for (int i = 0; i < _bnd.bndList[0].fenceLine.Count; i++)
                    _bnd.bndList[0].hdLine.Add(new vec3(_bnd.bndList[0].fenceLine[i]));
            }
            else
            {
                CABCurve.MakePointMinimumSpacing(ref _bnd.bndList[0].hdLine, 1.2);
                CABCurve.CalculateHeadings(ref _bnd.bndList[0].hdLine);
            }
            return null;
        }

        // oglSelf_MouseDown desde coordenadas de campo. mode: "curve"|"ab".
        public string E_Tap(double easting, double northing, string mode, double distanceDisplay)
        {
            if (!E_HasBoundary()) return "sin-contorno";

            //nativo: con curva la distancia 0 no mueve nada → error
            if (distanceDisplay == 0 && mode == "curve")
                return "distancia-cero";

            hdEdSlice.Clear();

            if (hdEdIsA)
            {
                double minDistA = double.MaxValue;
                hdEdStart = 99999; hdEdEnd = 99999;

                for (int j = 0; j < _bnd.bndList.Count; j++)
                {
                    for (int i = 0; i < _bnd.bndList[j].fenceLine.Count; i++)
                    {
                        double dist = ((easting - _bnd.bndList[j].fenceLine[i].easting) * (easting - _bnd.bndList[j].fenceLine[i].easting))
                                        + ((northing - _bnd.bndList[j].fenceLine[i].northing) * (northing - _bnd.bndList[j].fenceLine[i].northing));
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

                for (int i = 0; i < _bnd.bndList[j2].fenceLine.Count; i++)
                {
                    double dist = ((easting - _bnd.bndList[j2].fenceLine[i].easting) * (easting - _bnd.bndList[j2].fenceLine[i].easting))
                                    + ((northing - _bnd.bndList[j2].fenceLine[i].northing) * (northing - _bnd.bndList[j2].fenceLine[i].northing));
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

                if ((Math.Abs(start - end)) > (_bnd.bndList[bndSelect].fenceLine.Count * 0.5))
                {
                    if (start < end) (start, end) = (end, start);

                    isLoop = true;
                    if (start < end) { limit = end; end = 0; }
                    else { limit = end; end = _bnd.bndList[bndSelect].fenceLine.Count; }
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
                        pt3 = _bnd.bndList[bndSelect].fenceLine[i];
                        hdEdSlice.Add(pt3);

                        if (isLoop && i == _bnd.bndList[bndSelect].fenceLine.Count - 1)
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
                        pt3 = _bnd.bndList[bndSelect].fenceLine[i];
                        hdEdSlice.Add(pt3);

                        if (isLoop && i == 0)
                        {
                            i = _bnd.bndList[bndSelect].fenceLine.Count - 1;
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
                if ((Math.Abs(start - end)) > (_bnd.bndList[bndSelect].fenceLine.Count * 0.5))
                {
                    if (start < end) (start, end) = (end, start);
                }
                else
                {
                    if (start > end) (start, end) = (end, start);
                }

                vec3 ptA = new vec3(_bnd.bndList[bndSelect].fenceLine[start]);
                vec3 ptB = new vec3(_bnd.bndList[bndSelect].fenceLine[end]);

                double abHead = Math.Atan2(
                    _bnd.bndList[bndSelect].fenceLine[end].easting - _bnd.bndList[bndSelect].fenceLine[start].easting,
                    _bnd.bndList[bndSelect].fenceLine[end].northing - _bnd.bndList[bndSelect].fenceLine[start].northing);
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
                E_SetLineDistance(distanceDisplay);

            return null;
        }

        // SetLineDistance: offsetea la línea de corte hacia adentro con culling
        // de auto-intersección + espaciado mínimo 1 m.
        private void E_SetLineDistance(double distanceDisplay)
        {
            _hdl.desList?.Clear();

            if (hdEdSlice.Count < 1) return;

            //_ftOrMtoM viene de LoadSettings(); fallback métrico si quedó en 0
            double distAway = distanceDisplay * (_ftOrMtoM > 0 ? _ftOrMtoM : 1.0);

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
                    if (_hdl.desList.Count > 0)
                    {
                        double dist = ((point.easting - _hdl.desList[_hdl.desList.Count - 1].easting) * (point.easting - _hdl.desList[_hdl.desList.Count - 1].easting))
                            + ((point.northing - _hdl.desList[_hdl.desList.Count - 1].northing) * (point.northing - _hdl.desList[_hdl.desList.Count - 1].northing));
                        if (dist > 1)
                            _hdl.desList.Add(point);
                    }
                    else _hdl.desList.Add(point);
                }
            }

            hdEdSlice.Clear();
            for (int i = 0; i < _hdl.desList.Count; i++)
                hdEdSlice.Add(new vec3(_hdl.desList[i]));

            _hdl.desList?.Clear();
        }

        public void E_CancelTouch()
        {
            hdEdStart = 99999; hdEdEnd = 99999;
            hdEdIsA = true;
            hdEdSlice.Clear();
            hdEdMode = null;
        }

        // btnALength/btnBLength (+9 m en pasos de 1 m) y btnAShrink/btnBShrink (-5 pts).
        public void E_Extend(string endSide, bool grow)
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
        public string E_Clip()
        {
            int startBnd = 0, endBnd = 0, startLine = 0, endLine = 0;
            int isStart = 0;

            if (hdEdSlice.Count == 0) return null;

            //backup para Undo
            hdEdBackup.Clear();
            foreach (var item in _bnd.bndList[0].hdLine)
                hdEdBackup.Add(item);

            for (int i = 0; i < hdEdSlice.Count - 2; i++)
            {
                for (int k = 0; k < _bnd.bndList[0].hdLine.Count - 2; k++)
                {
                    GeoLineSegment sliceSegment = SegmentoDe(hdEdSlice, i);
                    GeoLineSegment headLineSegment = _bnd.bndList[0].GetHeadLineSegment(k);
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
            if ((Math.Abs(startBnd - endBnd)) > (_bnd.bndList[hdEdBndSel].fenceLine.Count * 0.5))
            {
                if (startBnd < endBnd) (startBnd, endBnd) = (endBnd, startBnd);

                _hdl.desList?.Clear();

                for (int i = endBnd; i < startBnd; i++)
                    _hdl.desList.Add(_bnd.bndList[0].hdLine[i]);

                for (int i = startLine; i < endLine; i++)
                    _hdl.desList.Add(hdEdSlice[i]);

                _bnd.bndList[0].hdLine.Clear();
                foreach (var item in _hdl.desList)
                    _bnd.bndList[0].hdLine.Add(item);
            }
            //completamente entre inicio y fin
            else
            {
                if (startBnd > endBnd) (startBnd, endBnd) = (endBnd, startBnd);

                _hdl.desList?.Clear();

                for (int i = 0; i < startBnd; i++)
                    _hdl.desList.Add(_bnd.bndList[0].hdLine[i]);

                for (int i = startLine; i < endLine; i++)
                    _hdl.desList.Add(hdEdSlice[i]);

                for (int i = endBnd; i < _bnd.bndList[0].hdLine.Count; i++)
                    _hdl.desList.Add(_bnd.bndList[0].hdLine[i]);

                _bnd.bndList[0].hdLine.Clear();
                foreach (var item in _hdl.desList)
                    _bnd.bndList[0].hdLine.Add(item);
            }

            _hdl.desList?.Clear();
            hdEdSlice.Clear();
            hdEdMode = null;
            return null;
        }

        public void E_Undo()
        {
            if (hdEdBackup.Count == 0) return;
            _bnd.bndList[0].hdLine?.Clear();
            foreach (var item in hdEdBackup)
                _bnd.bndList[0].hdLine.Add(item);
            hdEdBackup.Clear();
        }

        // btnExit + bloque post-diálogo de GetHeadland(): suavizado por
        // decimación de rumbo (delta>0.005), persistir, recalcular isHeadlandOn
        // y refrescar paneles.
        public void E_CloseSession()
        {
            if (_bnd == null || _bnd.bndList.Count == 0) return;

            _hdl.idx = hdEdSlice.Count > 0 ? 0 : -1;
            hdEdSlice.Clear();
            hdEdBackup.Clear();
            hdEdMode = null;
            hdEdStart = 99999; hdEdEnd = 99999; hdEdIsA = true;

            if (_bnd.bndList[0].hdLine.Count > 0)
            {
                vec3[] hdArr = new vec3[_bnd.bndList[0].hdLine.Count];
                _bnd.bndList[0].hdLine.CopyTo(hdArr);
                _bnd.bndList[0].hdLine?.Clear();

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
                        _bnd.bndList[0].hdLine.Add(new vec3(hdArr[i].easting, hdArr[i].northing, hdArr[i].heading));
                        continue;
                    }
                    delta += (hdArr[i - 1].heading - hdArr[i].heading);

                    if (Math.Abs(delta) > 0.005)
                    {
                        _bnd.bndList[0].hdLine.Add(new vec3(hdArr[i].easting, hdArr[i].northing, hdArr[i].heading));
                        delta = 0;
                    }
                }
                vec3 ptEnd = new vec3(hdArr[hdArr.Length - 1].easting, hdArr[hdArr.Length - 1].northing, hdArr[hdArr.Length - 1].heading);
                _bnd.bndList[0].hdLine.Add(ptEnd);
            }

            _guardar();

            //bloque post-diálogo del launcher nativo (GetHeadland)
            _bnd.isHeadlandOn = (_bnd.bndList.Count > 0 && _bnd.bndList[0].hdLine.Count > 0);
            _refrescarUi();
        }
    }
}
