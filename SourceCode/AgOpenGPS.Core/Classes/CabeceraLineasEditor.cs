// ============================================================================
// CabeceraLineasEditor.cs — constructor de cabecera por líneas (reemplazo de
// FormHeadAche): selección de puntos A/B sobre el contorno, líneas Curva/AB con
// extensión de 30 m en las puntas, offset hacia adentro, ciclar/borrar/extender,
// y "Build", que arma la cabecera buscando los cruces entre líneas consecutivas.
//
// Era `partial class FormGPS` y por eso solo existía bajo WinForms: contra el
// motor headless /api/cabecera-lineas daba 404. Mismo caso que HeadlandEditor —
// y por el mismo motivo, no se reescribió nada: las 633 líneas no tenían UNA
// sola dependencia de WinForms (las tres que parecían serlo eran comentarios
// nombrando botones). Se movieron tal cual y lo que tocaba del host pasó a
// campos y callbacks; FormGPS ahora delega, así que los dos stacks corren el
// mismo algoritmo y no pueden divergir.
//
// No es thread-safe: cada host lo llama desde su propio hilo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public sealed class CabeceraLineasEditor
    {
        private readonly CBoundary _bnd;
        private readonly CHeadLine _hdl;
        private readonly CTool _tool;

        /// <summary>Persistir las líneas de cabecera (Headlines.txt).</summary>
        private readonly Action _guardarLineas;

        /// <summary>Persistir la cabecera armada (Headland.txt).</summary>
        private readonly Action _guardarCabecera;

        /// <summary>Leer Headlines.txt del lote. El "Open" del editor recarga
        /// las líneas guardadas antes de empezar a editar.</summary>
        private readonly Action _cargarLineas;

        private readonly CABCurve _curve;
        private readonly CVehicle _vehicle;

        private readonly Func<bool> _hayLoteFn;
        private readonly Func<double> _ftOrMtoMFn;
        private readonly Func<double> _m2DisplayFn;
        private readonly Func<string> _unidadesFn;
        private readonly Action<bool> _guardarSeccionControlada;
        private readonly Action _recalcularExtension;
        private readonly Action _refrescarUi;

        public CabeceraLineasEditor(
            CBoundary bnd,
            CHeadLine hdl,
            CTool tool,
            CABCurve curve,
            CVehicle vehicle,
            Action guardarLineas,
            Action cargarLineas,
            Action guardarCabecera,
            Func<bool> hayLote,
            Func<double> ftOrMtoM = null,
            Func<double> m2Display = null,
            Func<string> unidades = null,
            Action<bool> guardarSeccionControlada = null,
            Action recalcularExtension = null,
            Action refrescarUi = null)
        {
            _bnd = bnd;
            _hdl = hdl;
            _tool = tool;
            _curve = curve;
            _vehicle = vehicle;
            _guardarLineas = guardarLineas ?? (() => { });
            _cargarLineas = cargarLineas ?? (() => { });
            _guardarCabecera = guardarCabecera ?? (() => { });
            _hayLoteFn = hayLote ?? (() => false);
            _ftOrMtoMFn = ftOrMtoM ?? (() => 1.0);
            _m2DisplayFn = m2Display ?? (() => 1.0);
            _unidadesFn = unidades ?? (() => "m");
            _guardarSeccionControlada = guardarSeccionControlada ?? (_ => { });
            _recalcularExtension = recalcularExtension ?? (() => { });
            _refrescarUi = refrescarUi ?? (() => { });
        }

        private bool _hayLote => _hayLoteFn();
        private double _ftOrMtoM => _ftOrMtoMFn();
        private double _m2Display => _m2DisplayFn();
        private string _unidades => _unidadesFn();

        // Estado de sesión (equivalente a los fields de FormHeadAche).
        private bool cabLinIsA = true;
        private int cabLinStart = 99999, cabLinEnd = 99999;
        private int cabLinBndSelect = 0;

        public sealed class CabLinTrackSnapshot
        {
            public string Name;
            public string Mode;              // "curve" | "ab"
            public List<double[]> Points = new List<double[]>();
        }

        public sealed class CabLinSnapshot
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

        public CabLinSnapshot CabLin_Snapshot(string error = null)
        {
            var s = new CabLinSnapshot
            {
                JobStarted = _hayLote,
                HasBoundary = _bnd.bndList.Count > 0 && _bnd.bndList[0].fenceLine.Count > 0,
                Units = string.IsNullOrEmpty(_unidades) ? "m" : _unidades.Trim(),
                //m2FtOrM se setea en LoadSettings(); si un arranque parcial lo
                //dejó en 0, caemos a métrico (1.0) para no mostrar ancho 0.
                ToolWidthDisplay = (_tool.width - _tool.overlap) * (_m2Display > 0 ? _m2Display : 1.0),
                BndSelect = cabLinBndSelect,
                SelIdx = _hdl.idx,
                IsSectionControlled = _bnd.isSectionControlledByHeadland,
                Error = error
            };

            for (int j = 0; j < _bnd.bndList.Count; j++)
            {
                var f = new List<double[]>(_bnd.bndList[j].fenceLine.Count);
                foreach (vec3 p in _bnd.bndList[j].fenceLine) f.Add(CabLin_EN(p));
                s.Fences.Add(f);
            }

            for (int i = 0; i < _hdl.tracksArr.Count; i++)
            {
                var t = new CabLinTrackSnapshot
                {
                    Name = _hdl.tracksArr[i].name,
                    Mode = _hdl.tracksArr[i].mode == (int)TrackMode.AB ? "ab" : "curve"
                };
                foreach (vec3 p in _hdl.tracksArr[i].trackPts) t.Points.Add(CabLin_EN(p));
                s.Tracks.Add(t);
            }

            if (_bnd.bndList.Count > 0 && _bnd.bndList[0].hdLine != null)
                foreach (vec3 p in _bnd.bndList[0].hdLine) s.HdLine.Add(CabLin_EN(p));

            if (cabLinStart != 99999 && cabLinBndSelect < _bnd.bndList.Count
                && cabLinStart < _bnd.bndList[cabLinBndSelect].fenceLine.Count)
                s.APoint = CabLin_EN(_bnd.bndList[cabLinBndSelect].fenceLine[cabLinStart]);
            if (cabLinEnd != 99999 && cabLinBndSelect < _bnd.bndList.Count
                && cabLinEnd < _bnd.bndList[cabLinBndSelect].fenceLine.Count)
                s.BPoint = CabLin_EN(_bnd.bndList[cabLinBndSelect].fenceLine[cabLinEnd]);

            return s;
        }

        // FormHeadAche ctor + Load: preparar sesión de edición.
        public CabLinSnapshot CabLin_Open()
        {
            if (!_hayLote) return CabLin_Snapshot("sin-lote");
            if (_bnd.bndList.Count == 0 || _bnd.bndList[0].fenceLine.Count == 0)
                return CabLin_Snapshot("sin-contorno");

            _recalcularExtension();
            _hdl.idx = -1;
            _cargarLineas();
            _bnd.bndList[0].hdLine?.Clear();

            cabLinIsA = true;
            cabLinStart = 99999; cabLinEnd = 99999;
            cabLinBndSelect = 0;
            return CabLin_Snapshot();
        }

        // oglSelf_MouseDown (parte lógica): tap en coords de campo E/N.
        public CabLinSnapshot CabLin_Tap(double easting, double northing, string mode, double distanceDisplay)
        {
            if (!_hayLote) return CabLin_Snapshot("sin-lote");
            if (_bnd.bndList.Count == 0 || _bnd.bndList[0].fenceLine.Count == 0)
                return CabLin_Snapshot("sin-contorno");

            _bnd.bndList[0].hdLine?.Clear();
            _hdl.idx = -1;

            if (cabLinIsA)
            {
                double minDistA = double.MaxValue;
                cabLinStart = 99999; cabLinEnd = 99999;

                for (int j = 0; j < _bnd.bndList.Count; j++)
                {
                    for (int i = 0; i < _bnd.bndList[j].fenceLine.Count; i++)
                    {
                        double dist = ((easting - _bnd.bndList[j].fenceLine[i].easting) * (easting - _bnd.bndList[j].fenceLine[i].easting))
                                        + ((northing - _bnd.bndList[j].fenceLine[i].northing) * (northing - _bnd.bndList[j].fenceLine[i].northing));
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

                for (int i = 0; i < _bnd.bndList[j].fenceLine.Count; i++)
                {
                    double dist = ((easting - _bnd.bndList[j].fenceLine[i].easting) * (easting - _bnd.bndList[j].fenceLine[i].easting))
                                    + ((northing - _bnd.bndList[j].fenceLine[i].northing) * (northing - _bnd.bndList[j].fenceLine[i].northing));
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
                _hdl.tracksArr.Add(new CHeadPath());
                _hdl.idx = _hdl.tracksArr.Count - 1;

                bool isLoop = false;
                int limit = end;

                if ((Math.Abs(start - end)) > (_bnd.bndList[bndSelect].fenceLine.Count * 0.5))
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
                        end = _bnd.bndList[bndSelect].fenceLine.Count;
                    }
                }
                else
                {
                    if (start > end) { (start, end) = (end, start); }
                }

                _hdl.tracksArr[_hdl.idx].a_point = start;
                _hdl.tracksArr[_hdl.idx].trackPts?.Clear();

                if (start < end)
                {
                    for (int i = start; i <= end; i++)
                    {
                        _hdl.tracksArr[_hdl.idx].trackPts.Add(new vec3(_bnd.bndList[bndSelect].fenceLine[i]));

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
                        _hdl.tracksArr[_hdl.idx].trackPts.Add(new vec3(_bnd.bndList[bndSelect].fenceLine[i]));

                        if (isLoop && i == 0)
                        {
                            i = _bnd.bndList[bndSelect].fenceLine.Count - 1;
                            isLoop = false;
                            end = limit;
                        }
                    }
                }

                CABCurve.CalculateHeadings(ref _hdl.tracksArr[_hdl.idx].trackPts);

                int ptCnt = _hdl.tracksArr[_hdl.idx].trackPts.Count - 1;

                for (int i = 1; i < 30; i++)
                {
                    vec3 pnt = new vec3(_hdl.tracksArr[_hdl.idx].trackPts[ptCnt]);
                    pnt.easting += (Math.Sin(pnt.heading) * i);
                    pnt.northing += (Math.Cos(pnt.heading) * i);
                    _hdl.tracksArr[_hdl.idx].trackPts.Add(pnt);
                }

                vec3 stat = new vec3(_hdl.tracksArr[_hdl.idx].trackPts[0]);

                for (int i = 1; i < 30; i++)
                {
                    vec3 pnt = new vec3(stat);
                    pnt.easting -= (Math.Sin(pnt.heading) * i);
                    pnt.northing -= (Math.Cos(pnt.heading) * i);
                    _hdl.tracksArr[_hdl.idx].trackPts.Insert(0, pnt);
                }

                _hdl.tracksArr[_hdl.idx].name = _hdl.idx.ToString() + " Cu " + DateTime.Now.ToString("mm:ss", CultureInfo.InvariantCulture);
                _hdl.tracksArr[_hdl.idx].moveDistance = 0;
                _hdl.tracksArr[_hdl.idx].mode = (int)TrackMode.Curve;

                _guardarLineas();
            }
            else
            {
                // ── Línea AB recta entre A y B (interpolada a 1 m) ──
                if ((Math.Abs(start - end)) > (_bnd.bndList[bndSelect].fenceLine.Count * 0.5))
                {
                    if (start < end) { (start, end) = (end, start); }
                }
                else
                {
                    if (start > end) { (start, end) = (end, start); }
                }

                vec3 ptA = new vec3(_bnd.bndList[bndSelect].fenceLine[start]);
                vec3 ptB = new vec3(_bnd.bndList[bndSelect].fenceLine[end]);

                double abHead = Math.Atan2(
                    _bnd.bndList[bndSelect].fenceLine[end].easting - _bnd.bndList[bndSelect].fenceLine[start].easting,
                    _bnd.bndList[bndSelect].fenceLine[end].northing - _bnd.bndList[bndSelect].fenceLine[start].northing);
                if (abHead < 0) abHead += glm.twoPI;

                if (_hdl.idx < _hdl.tracksArr.Count - 1)
                {
                    _hdl.idx++;
                    _hdl.tracksArr.Insert(_hdl.idx, new CHeadPath());
                }
                else
                {
                    _hdl.tracksArr.Add(new CHeadPath());
                    _hdl.idx = _hdl.tracksArr.Count - 1;
                }

                _hdl.tracksArr[_hdl.idx].a_point = start;
                _hdl.tracksArr[_hdl.idx].trackPts?.Clear();

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
                    _hdl.tracksArr[_hdl.idx].trackPts.Add(ptC);
                }

                int ptCnt = _hdl.tracksArr[_hdl.idx].trackPts.Count - 1;

                for (int i = 1; i < 30; i++)
                {
                    vec3 pnt = new vec3(_hdl.tracksArr[_hdl.idx].trackPts[ptCnt]);
                    pnt.easting += (Math.Sin(pnt.heading) * i);
                    pnt.northing += (Math.Cos(pnt.heading) * i);
                    _hdl.tracksArr[_hdl.idx].trackPts.Add(pnt);
                }

                vec3 stat = new vec3(_hdl.tracksArr[_hdl.idx].trackPts[0]);

                for (int i = 1; i < 30; i++)
                {
                    vec3 pnt = new vec3(stat);
                    pnt.easting -= (Math.Sin(pnt.heading) * i);
                    pnt.northing -= (Math.Cos(pnt.heading) * i);
                    _hdl.tracksArr[_hdl.idx].trackPts.Insert(0, pnt);
                }

                _hdl.tracksArr[_hdl.idx].name = _hdl.idx.ToString() + " AB " + DateTime.Now.ToString("hh:mm:ss", CultureInfo.InvariantCulture);
                _hdl.tracksArr[_hdl.idx].moveDistance = 0;
                _hdl.tracksArr[_hdl.idx].mode = (int)TrackMode.AB;

                _guardarLineas();
            }

            cabLinStart = 99999; cabLinEnd = 99999;

            // ── Offset hacia adentro por distanceDisplay (unidades display) ──
            _hdl.desList?.Clear();

            if (_hdl.tracksArr.Count < 1 || _hdl.idx == -1) return CabLin_Snapshot();

            //_ftOrMtoM viene de LoadSettings(); fallback métrico si quedó en 0
            double distAway = distanceDisplay * (_ftOrMtoM > 0 ? _ftOrMtoM : 1.0);
            _hdl.tracksArr[_hdl.idx].moveDistance += distAway;

            double distSqAway = (distAway * distAway) - 0.01;
            vec3 point;

            int refCount = _hdl.tracksArr[_hdl.idx].trackPts.Count;
            for (int i = 0; i < refCount; i++)
            {
                point = new vec3(
                _hdl.tracksArr[_hdl.idx].trackPts[i].easting - (Math.Sin(glm.PIBy2 + _hdl.tracksArr[_hdl.idx].trackPts[i].heading) * distAway),
                _hdl.tracksArr[_hdl.idx].trackPts[i].northing - (Math.Cos(glm.PIBy2 + _hdl.tracksArr[_hdl.idx].trackPts[i].heading) * distAway),
                _hdl.tracksArr[_hdl.idx].trackPts[i].heading);
                bool Add = true;

                for (int t = 0; t < refCount; t++)
                {
                    double dist = ((point.easting - _hdl.tracksArr[_hdl.idx].trackPts[t].easting) * (point.easting - _hdl.tracksArr[_hdl.idx].trackPts[t].easting))
                        + ((point.northing - _hdl.tracksArr[_hdl.idx].trackPts[t].northing) * (point.northing - _hdl.tracksArr[_hdl.idx].trackPts[t].northing));
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

            _hdl.tracksArr[_hdl.idx].trackPts.Clear();

            for (int i = 0; i < _hdl.desList.Count; i++)
            {
                _hdl.tracksArr[_hdl.idx].trackPts.Add(new vec3(_hdl.desList[i]));
            }

            _hdl.desList?.Clear();

            return CabLin_Snapshot();
        }

        public CabLinSnapshot CabLin_CancelTouch()
        {
            cabLinStart = 99999; cabLinEnd = 99999;
            cabLinIsA = true;
            _curve.desList?.Clear();
            return CabLin_Snapshot();
        }

        public CabLinSnapshot CabLin_Cycle(int dir)
        {
            _bnd.bndList[0].hdLine?.Clear();

            if (_hdl.tracksArr.Count > 0)
            {
                _hdl.idx += (dir >= 0 ? 1 : -1);
                if (_hdl.idx > (_hdl.tracksArr.Count - 1)) _hdl.idx = 0;
                if (_hdl.idx < 0) _hdl.idx = (_hdl.tracksArr.Count - 1);
            }
            else _hdl.idx = -1;

            return CabLin_Snapshot();
        }

        public CabLinSnapshot CabLin_DeleteTrack()
        {
            if (_hdl.tracksArr.Count > 0 && _hdl.idx > -1)
            {
                _hdl.tracksArr.RemoveAt(_hdl.idx);
                _hdl.idx--;
            }

            if (_hdl.tracksArr.Count > 0)
            {
                if (_hdl.idx == -1) _hdl.idx++;
            }
            else _hdl.idx = -1;

            return CabLin_Snapshot();
        }

        // btnALength/btnBLength (+9 m en pasos de 1 m) y btnAShrink/btnBShrink (-5 pts).
        public CabLinSnapshot CabLin_Extend(string endSide, bool grow)
        {
            if (_hdl.idx > -1)
            {
                if (endSide == "a")
                {
                    if (grow)
                    {
                        vec3 start = new vec3(_hdl.tracksArr[_hdl.idx].trackPts[0]);
                        for (int i = 1; i < 10; i++)
                        {
                            vec3 pt = new vec3(start);
                            pt.easting -= (Math.Sin(pt.heading) * i);
                            pt.northing -= (Math.Cos(pt.heading) * i);
                            _hdl.tracksArr[_hdl.idx].trackPts.Insert(0, pt);
                        }
                    }
                    else if (_hdl.tracksArr[_hdl.idx].trackPts.Count > 8)
                        _hdl.tracksArr[_hdl.idx].trackPts.RemoveRange(0, 5);
                }
                else
                {
                    if (grow)
                    {
                        int ptCnt = _hdl.tracksArr[_hdl.idx].trackPts.Count - 1;
                        for (int i = 1; i < 10; i++)
                        {
                            vec3 pt = new vec3(_hdl.tracksArr[_hdl.idx].trackPts[ptCnt]);
                            pt.easting += (Math.Sin(pt.heading) * i);
                            pt.northing += (Math.Cos(pt.heading) * i);
                            _hdl.tracksArr[_hdl.idx].trackPts.Add(pt);
                        }
                    }
                    else if (_hdl.tracksArr[_hdl.idx].trackPts.Count > 8)
                        _hdl.tracksArr[_hdl.idx].trackPts.RemoveRange(_hdl.tracksArr[_hdl.idx].trackPts.Count - 5, 5);
                }
            }

            return CabLin_Snapshot();
        }

        // btnBndLoop_Click: arma la cabecera con los cruces entre líneas.
        public CabLinSnapshot CabLin_BuildHeadland()
        {
            if (_bnd.bndList.Count == 0) return CabLin_Snapshot("sin-contorno");

            _hdl.tracksArr.Sort((p, q) => p.a_point.CompareTo(q.a_point));
            _guardarLineas();

            _hdl.idx = -1;

            _bnd.bndList[0].hdLine?.Clear();

            int nextLine = 0;
            var crossings = new List<int>();

            int isStart = 0;

            for (int lineNum = 0; lineNum < _hdl.tracksArr.Count; lineNum++)
            {
                nextLine = lineNum - 1;
                if (nextLine < 0) nextLine = _hdl.tracksArr.Count - 1;

                if (nextLine == lineNum)
                    return CabLin_Snapshot("una-sola-linea");

                for (int i = 0; i < _hdl.tracksArr[lineNum].trackPts.Count - 2; i++)
                {
                    GeoLineSegment headPathSegment = _hdl.tracksArr[lineNum].GetHeadPathSegment(i);
                    for (int k = 0; k < _hdl.tracksArr[nextLine].trackPts.Count - 2; k++)
                    {
                        GeoLineSegment otherSegment = _hdl.tracksArr[nextLine].GetHeadPathSegment(k);
                        GeoCoord? intersectionPoint = headPathSegment.IntersectionPoint(otherSegment);
                        if (intersectionPoint.HasValue)
                        {
                            if (isStart == 0) i++;
                            crossings.Add(i);
                            isStart++;
                            if (isStart == 2) goto again;
                            nextLine = lineNum + 1;

                            if (nextLine > _hdl.tracksArr.Count - 1) nextLine = 0;
                        }
                    }
                }

            again:
                isStart = 0;
            }

            if (crossings.Count != _hdl.tracksArr.Count * 2)
            {
                _bnd.bndList[0].hdLine?.Clear();
                return CabLin_Snapshot("cruces");
            }

            for (int i = 0; i < _hdl.tracksArr.Count; i++)
            {
                int low = crossings[i * 2];
                int high = crossings[i * 2 + 1];
                for (int k = low; k < high; k++)
                {
                    _bnd.bndList[0].hdLine.Add(_hdl.tracksArr[i].trackPts[k]);
                }
            }

            vec3[] hdArr;

            if (_bnd.bndList[0].hdLine.Count > 0)
            {
                hdArr = new vec3[_bnd.bndList[0].hdLine.Count];
                _bnd.bndList[0].hdLine.CopyTo(hdArr);
                _bnd.bndList[0].hdLine?.Clear();
            }
            else
            {
                _bnd.bndList[0].hdLine?.Clear();
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
                    _bnd.bndList[0].hdLine.Add(new vec3(hdArr[i].easting, hdArr[i].northing, hdArr[i].heading));
                    continue;
                }
                delta += (hdArr[i - 1].heading - hdArr[i].heading);

                if (Math.Abs(delta) > 0.005)
                {
                    vec3 pt = new vec3(hdArr[i].easting, hdArr[i].northing, hdArr[i].heading);
                    _bnd.bndList[0].hdLine.Add(pt);
                    delta = 0;
                }
            }

            _guardarCabecera();
            return CabLin_Snapshot();
        }

        // btnDeleteHeadland ("Reset").
        public CabLinSnapshot CabLin_ResetHeadland()
        {
            cabLinStart = 99999; cabLinEnd = 99999;
            cabLinIsA = true;
            _hdl.desList?.Clear();
            if (_bnd.bndList.Count > 0) _bnd.bndList[0].hdLine?.Clear();
            return CabLin_Snapshot();
        }

        // btnHeadlandOff: apagar cabecera y persistir.
        public CabLinSnapshot CabLin_TurnOff()
        {
            if (_bnd.bndList.Count > 0) _bnd.bndList[0].hdLine?.Clear();
            _guardarCabecera();
            _bnd.isHeadlandOn = false;
            _vehicle.isHydLiftOn = false;
            return CabLin_Snapshot();
        }

        public void CabLin_SetSectionControlled(bool on)
        {
            _bnd.isSectionControlledByHeadland = on;
            _guardarSeccionControlada(on);

        }

        // FormClosing + bloque post-diálogo del launcher nativo.
        public void CabLin_CloseSession()
        {
            if (!_hayLote) return;

            _guardarLineas();
            _hdl.idx = _hdl.tracksArr.Count > 0 ? 0 : -1;

            _bnd.isHeadlandOn = (_bnd.bndList.Count > 0 && _bnd.bndList[0].hdLine.Count > 0);

            _refrescarUi();
        }
    }
}
