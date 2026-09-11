// ============================================================================
// TramLineEditor.cs — constructor de tramlines (port fiel de FormTramLine).
//
// Las tramlines son las huellas por donde pasa el pulverizador después: se
// generan a partir de una guía y el ancho de labor, y se dejan marcadas para
// no pisar el cultivo. Sin esto el operario tiene que acordarse por dónde ir.
//
// Era `partial class FormGPS` y por eso solo existía bajo WinForms: contra el
// motor headless /api/tramlines daba 404. Segundo de la lista de RETIRAR-
// WINFORMS.md, ordenada de más complicado a más simple.
//
// Mismo tratamiento que HeadlandEditor y CabeceraLineasEditor: las 584 líneas
// no tenían UNA sola dependencia de WinForms, así que no se reescribió nada —
// se movieron tal cual y lo que tocaba del host pasó a campos y callbacks.
// FormGPS delega, así que los dos stacks corren el mismo algoritmo.
//
// No es thread-safe: cada host lo llama desde su propio hilo.
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public sealed class TramLineEditor
    {
        private readonly CTram _tram;
        private readonly CBoundary _bnd;
        private readonly CTool _tool;
        private readonly CABCurve _curve;
        private readonly CVehicle _vehicle;
        private readonly CTrack _trk;

        /// <summary>Persistir Tram.txt.</summary>
        private readonly Action _guardarTram;

        private readonly Func<string> _unidadesFn;
        private readonly Func<double> _m2DisplayFn;

        /// <summary>Transparencia de la tramline en el mapa. Vive en la config
        /// del host porque es preferencia de pantalla, no geometría.</summary>
        private readonly Func<double> _alphaGet;
        private readonly Action<double> _alphaSet;

        private readonly Func<double> _maxDiagonalFn;

        /// <summary>Refrescar la imagen del boton de modo. Solo WinForms.</summary>
        private readonly Action _refrescarModo;

        private readonly Action _recalcularExtension;
        private readonly Action _refrescarUi;

        public TramLineEditor(
            CTram tram,
            CBoundary bnd,
            CTool tool,
            CABCurve curve,
            CVehicle vehicle,
            CTrack trk,
            Action guardarTram,
            Func<string> unidades = null,
            Func<double> m2Display = null,
            Func<double> alphaGet = null,
            Action<double> alphaSet = null,
            Func<double> maxDiagonal = null,
            Action refrescarModo = null,
            Action recalcularExtension = null,
            Action refrescarUi = null)
        {
            _tram = tram;
            _bnd = bnd;
            _tool = tool;
            _curve = curve;
            _vehicle = vehicle;
            _trk = trk;
            _guardarTram = guardarTram ?? (() => { });
            _unidadesFn = unidades ?? (() => "m");
            _m2DisplayFn = m2Display ?? (() => 1.0);
            _alphaGet = alphaGet ?? (() => 0.5);
            _alphaSet = alphaSet ?? (_ => { });
            _maxDiagonalFn = maxDiagonal ?? (() => 2000.0);
            _refrescarModo = refrescarModo ?? (() => { });
            _recalcularExtension = recalcularExtension ?? (() => { });
            _refrescarUi = refrescarUi ?? (() => { });
        }

        private string _unidades => _unidadesFn();

        /// <summary>Diagonal del lote: las tramlines se extienden mas alla del
        /// borde para despues recortarlas contra el contorno.</summary>
        private double _maxDiagonalLote => _maxDiagonalFn();
        private double _m2Display => _m2DisplayFn();

        private double _alphaTram
        {
            get => _alphaGet();
            set => _alphaSet(value);
        }

        // ── Sesión de tramlines ─────────────────────────────────────────
        private readonly List<CTrk> tramGTemp = new List<CTrk>();
        private List<vec2> tramTmpArr = new List<vec2>();
        private readonly List<List<vec2>> tramTmpList = new List<List<vec2>>();
        private int tramIndx = -1;
        private int tramPasses = 2, tramStartPass;
        private int tramStep; // 0,1,2 (3-tap cut)
        private vec2 tramPtA = new vec2(9999999, 9999999);
        private vec2 tramPtB = new vec2(9999999, 9999999);

        // ── Snapshot ────────────────────────────────────────────────────

        public sealed class TramSnapshot
        {
            public bool HasBoundary;
            public string Units;
            public double TrackWidthDisplay, TramWidthDisplay, ToolWidthDisplay;
            public List<TramTrackInfo> Tracks = new List<TramTrackInfo>();
            public int SelIdx;
            public List<List<vec2>> NewTrams = new List<List<vec2>>();
            public List<List<vec2>> SavedTrams = new List<List<vec2>>();
            public List<List<vec2>> Fences = new List<List<vec2>>();
            public List<vec2> OuterBnd = new List<vec2>();
            public List<vec2> InnerBnd = new List<vec2>();
            public int Passes, StartPass;
            public bool IsOuter;
            public double Alpha;
            public int CutStep;
            public vec2 PtA, PtB;
            public string Error;
        }

        public sealed class TramTrackInfo
        {
            public int Index;
            public string Name;
            public string Mode; // "ab"|"curve"
            public List<vec3> CurvePts;
            public double Heading;
            public vec2 PtA, PtB;
        }

        public TramSnapshot Tram_Snapshot(string error = null)
        {
            double conv = (_m2Display > 0 ? _m2Display : 1.0);
            string units = string.IsNullOrEmpty(_unidades) ? "m" : _unidades.Trim();

            var snap = new TramSnapshot
            {
                HasBoundary = _bnd != null && _bnd.bndList.Count > 0 && _bnd.bndList[0].fenceLine.Count > 0,
                Units = units,
                TrackWidthDisplay = _vehicle.VehicleConfig.TrackWidth * conv,
                TramWidthDisplay = _tram.tramWidth * conv,
                ToolWidthDisplay = _tool.width * conv,
                SelIdx = tramIndx,
                Passes = tramPasses,
                StartPass = tramStartPass,
                IsOuter = _tram.tramBndOuterArr.Count > 0,
                Alpha = _tram.alpha,
                CutStep = tramStep,
                PtA = tramStep >= 1 ? tramPtA : new vec2(9999999, 9999999),
                PtB = tramStep >= 2 ? tramPtB : new vec2(9999999, 9999999),
                Error = error
            };

            // Tracks
            for (int i = 0; i < tramGTemp.Count; i++)
            {
                var t = tramGTemp[i];
                snap.Tracks.Add(new TramTrackInfo
                {
                    Index = i,
                    Name = t.name,
                    Mode = t.mode == TrackMode.AB ? "ab" : "curve",
                    CurvePts = t.curvePts,
                    Heading = t.heading,
                    PtA = t.ptA,
                    PtB = t.ptB
                });
            }

            // New trams
            foreach (var list in tramTmpList)
                snap.NewTrams.Add(list);

            // Saved trams
            if (_tram.tramList != null)
                foreach (var list in _tram.tramList)
                    snap.SavedTrams.Add(list);

            // Fences
            if (_bnd != null)
                for (int j = 0; j < _bnd.bndList.Count; j++)
                {
                    var src = _bnd.bndList[j].fenceLine;
                    var pts = new List<vec2>();
                    for (int i = 0; i < src.Count; i++)
                        pts.Add(new vec2(src[i].easting, src[i].northing));
                    snap.Fences.Add(pts);
                }

            // Outer/inner _bnd
            if (_tram.tramBndOuterArr != null)
                snap.OuterBnd = _tram.tramBndOuterArr;
            if (_tram.tramBndInnerArr != null)
                snap.InnerBnd = _tram.tramBndInnerArr;

            return snap;
        }

        // ── Open ────────────────────────────────────────────────────────
        public string Tram_Open()
        {
            if (_bnd == null || _bnd.bndList.Count == 0 || _bnd.bndList[0].fenceLine.Count == 0)
                return "sin-contorno";

            _recalcularExtension();
            _tool.halfWidth = (_tool.width - _tool.overlap) / 2.0;

            tramGTemp.Clear();
            tramTmpArr.Clear();
            tramTmpList.Clear();
            tramStep = 0;
            tramPtA = new vec2(9999999, 9999999);
            tramPtB = new vec2(9999999, 9999999);

            foreach (var item in _trk.gArr)
            {
                if ((item.mode == TrackMode.AB || item.mode == TrackMode.Curve) && item.isVisible)
                {
                    tramGTemp.Add(new CTrk(item));
                    if (item.mode == TrackMode.AB)
                        tramGTemp[tramGTemp.Count - 1].isVisible = false;
                    else
                        tramGTemp[tramGTemp.Count - 1].isVisible = true;
                }
            }

            if (tramGTemp.Count == 0)
                return "sin-guias";

            tramIndx = 0;

            // Determinar lado correcto para cada línea
            for (tramIndx = 0; tramIndx < tramGTemp.Count; tramIndx++)
            {
                Tram_BuildTram();
                if (tramTmpList.Count > 0 && tramTmpList[0].Count == 0)
                {
                    tramGTemp[tramIndx].isVisible = !tramGTemp[tramIndx].isVisible;
                    tramTmpList.Clear();
                    tramTmpArr.Clear();
                }
            }

            tramIndx = 0;

            // Reset start/passes
            if (_tram.tramBndOuterArr.Count > 0)
                tramStartPass = 1;
            else
                tramStartPass = 0;
            tramPasses = 2;

            Tram_BuildTram();
            return null;
        }

        // ── Build _tram (redirige según modo) ────────────────────────────
        private void Tram_BuildTram()
        {
            if (tramIndx < 0 || tramIndx >= tramGTemp.Count) return;

            if (tramGTemp[tramIndx].mode == TrackMode.Curve)
                Tram_BuildCurveTram();
            else if (tramGTemp[tramIndx].mode == TrackMode.AB)
                Tram_BuildABTram();
        }

        private void Tram_BuildCurveTram()
        {
            tramTmpList.Clear();
            tramTmpArr.Clear();

            int refCount = tramGTemp[tramIndx].curvePts.Count;
            int cntr = tramStartPass;
            double widd;
            double sideHeading = tramGTemp[tramIndx].isVisible ? Math.PI : 0;

            for (int i = cntr; i <= (tramPasses + tramStartPass) - 1; i++)
            {
                tramTmpArr = new List<vec2> { Capacity = 128 };
                tramTmpList.Add(tramTmpArr);

                widd = (_tram.tramWidth * 0.5) - _tram.halfWheelTrack;
                widd += (_tram.tramWidth * i);
                double distSqAway = widd * widd * 0.999999;

                for (int j = 0; j < refCount; j += 1)
                {
                    vec2 point = new vec2(
                        (Math.Sin(glm.PIBy2 + tramGTemp[tramIndx].curvePts[j].heading + sideHeading) * widd)
                            + tramGTemp[tramIndx].curvePts[j].easting,
                        (Math.Cos(glm.PIBy2 + tramGTemp[tramIndx].curvePts[j].heading + sideHeading) * widd)
                            + tramGTemp[tramIndx].curvePts[j].northing
                    );

                    bool Add = true;
                    for (int t = 0; t < refCount; t++)
                    {
                        double dist = ((point.easting - tramGTemp[tramIndx].curvePts[t].easting) * (point.easting - tramGTemp[tramIndx].curvePts[t].easting))
                            + ((point.northing - tramGTemp[tramIndx].curvePts[t].northing) * (point.northing - tramGTemp[tramIndx].curvePts[t].northing));
                        if (dist < distSqAway) { Add = false; break; }
                    }
                    if (Add)
                    {
                        double dist = tramTmpArr.Count > 0
                            ? ((point.easting - tramTmpArr[tramTmpArr.Count - 1].easting) * (point.easting - tramTmpArr[tramTmpArr.Count - 1].easting))
                              + ((point.northing - tramTmpArr[tramTmpArr.Count - 1].northing) * (point.northing - tramTmpArr[tramTmpArr.Count - 1].northing))
                            : 3.0;
                        if (dist > 1.2)
                        {
                            if (_bnd.bndList[0].fenceLineEar.IsPointInPolygon(point))
                                tramTmpArr.Add(point);
                        }
                    }
                }
            }

            for (int i = cntr; i <= (tramPasses + tramStartPass) - 1; i++)
            {
                tramTmpArr = new List<vec2> { Capacity = 128 };
                tramTmpList.Add(tramTmpArr);

                widd = (_tram.tramWidth * 0.5) + _tram.halfWheelTrack;
                widd += (_tram.tramWidth * i);
                double distSqAway = widd * widd * 0.999999;

                for (int j = 0; j < refCount; j += 1)
                {
                    vec2 point = new vec2(
                        Math.Sin(glm.PIBy2 + tramGTemp[tramIndx].curvePts[j].heading + sideHeading) * widd
                            + tramGTemp[tramIndx].curvePts[j].easting,
                        Math.Cos(glm.PIBy2 + tramGTemp[tramIndx].curvePts[j].heading + sideHeading) * widd
                            + tramGTemp[tramIndx].curvePts[j].northing
                    );

                    bool Add = true;
                    for (int t = 0; t < refCount; t++)
                    {
                        double dist = ((point.easting - tramGTemp[tramIndx].curvePts[t].easting) * (point.easting - tramGTemp[tramIndx].curvePts[t].easting))
                            + ((point.northing - tramGTemp[tramIndx].curvePts[t].northing) * (point.northing - tramGTemp[tramIndx].curvePts[t].northing));
                        if (dist < distSqAway) { Add = false; break; }
                    }
                    if (Add)
                    {
                        double dist = tramTmpArr.Count > 0
                            ? ((point.easting - tramTmpArr[tramTmpArr.Count - 1].easting) * (point.easting - tramTmpArr[tramTmpArr.Count - 1].easting))
                              + ((point.northing - tramTmpArr[tramTmpArr.Count - 1].northing) * (point.northing - tramTmpArr[tramTmpArr.Count - 1].northing))
                            : 3.0;
                        if (dist > 1.2)
                        {
                            if (_bnd.bndList[0].fenceLineEar.IsPointInPolygon(point))
                                tramTmpArr.Add(point);
                        }
                    }
                }
            }
        }

        private void Tram_BuildABTram()
        {
            List<vec2> tramRef = new List<vec2>();
            double abHeading = tramGTemp[tramIndx].heading;
            double hsin = Math.Sin(abHeading);
            double hcos = Math.Cos(abHeading);

            tramGTemp[tramIndx].endPtA.easting = tramGTemp[tramIndx].ptA.easting - (Math.Sin(abHeading) * _maxDiagonalLote);
            tramGTemp[tramIndx].endPtA.northing = tramGTemp[tramIndx].ptA.northing - (Math.Cos(abHeading) * _maxDiagonalLote);
            tramGTemp[tramIndx].endPtB.easting = tramGTemp[tramIndx].ptB.easting + (Math.Sin(abHeading) * _maxDiagonalLote);
            tramGTemp[tramIndx].endPtB.northing = tramGTemp[tramIndx].ptB.northing + (Math.Cos(abHeading) * _maxDiagonalLote);

            double len = glm.Distance(tramGTemp[tramIndx].endPtA, tramGTemp[tramIndx].endPtB);
            vec2 P1 = new vec2();
            for (int i = 0; i < (int)len; i += 2)
            {
                P1.easting = (hsin * i) + tramGTemp[tramIndx].endPtA.easting;
                P1.northing = (hcos * i) + tramGTemp[tramIndx].endPtA.northing;
                tramRef.Add(P1);
            }

            double headingCalc = abHeading + glm.PIBy2;
            if (headingCalc < 0) headingCalc += glm.twoPI;
            if (headingCalc > glm.twoPI) headingCalc -= glm.twoPI;
            if (tramGTemp[tramIndx].isVisible) headingCalc += Math.PI;
            if (headingCalc > glm.twoPI) headingCalc -= glm.twoPI;

            hsin = Math.Sin(headingCalc);
            hcos = Math.Cos(headingCalc);

            tramTmpList.Clear();
            tramTmpArr.Clear();

            int cntr = tramStartPass;
            double widd;

            for (int i = cntr; i < tramPasses + tramStartPass; i++)
            {
                tramTmpArr = new List<vec2> { Capacity = 128 };
                tramTmpList.Add(tramTmpArr);

                widd = (_tram.tramWidth * 0.5) - _tram.halfWheelTrack;
                widd += (_tram.tramWidth * i);

                for (int j = 0; j < tramRef.Count; j++)
                {
                    P1.easting = hsin * widd + tramRef[j].easting;
                    P1.northing = (hcos * widd) + tramRef[j].northing;

                    if (_bnd.bndList[0].fenceLineEar.IsPointInPolygon(P1))
                        tramTmpArr.Add(P1);
                }
            }

            for (int i = cntr; i < tramPasses + tramStartPass; i++)
            {
                tramTmpArr = new List<vec2> { Capacity = 128 };
                tramTmpList.Add(tramTmpArr);

                widd = (_tram.tramWidth * 0.5) + _tram.halfWheelTrack;
                widd += (_tram.tramWidth * i);

                for (int j = 0; j < tramRef.Count; j++)
                {
                    P1.easting = (hsin * widd) + tramRef[j].easting;
                    P1.northing = (hcos * widd) + tramRef[j].northing;

                    if (_bnd.bndList[0].fenceLineEar.IsPointInPolygon(P1))
                        tramTmpArr.Add(P1);
                }
            }

            tramRef.Clear();
        }

        // ── Cycle track ─────────────────────────────────────────────────
        public void Tram_CycleTrack(int dir)
        {
            tramTmpList.Clear();
            tramTmpArr.Clear();

            if (tramGTemp.Count > 0)
            {
                tramIndx += (dir >= 0 ? 1 : -1);
                if (tramIndx > (tramGTemp.Count - 1)) tramIndx = 0;
                if (tramIndx < 0) tramIndx = tramGTemp.Count - 1;
            }
            else tramIndx = -1;

            Tram_BuildTram();
        }

        // ── Swap side ───────────────────────────────────────────────────
        public void Tram_SwapSide()
        {
            if (tramIndx >= 0 && tramIndx < tramGTemp.Count)
            {
                tramGTemp[tramIndx].isVisible = !tramGTemp[tramIndx].isVisible;
                Tram_ResetStartNumLabels();
                Tram_BuildTram();
            }
        }

        // ── Passes / StartPass ──────────────────────────────────────────
        public void Tram_SetPasses(int p)
        {
            tramPasses = Math.Max(1, p);
            Tram_BuildTram();
        }

        public void Tram_SetStartPass(int s)
        {
            tramStartPass = Math.Max(0, s);
            Tram_BuildTram();
        }

        private void Tram_ResetStartNumLabels()
        {
            tramStartPass = _tram.tramBndOuterArr.Count > 0 ? 1 : 0;
            tramPasses = 2;
        }

        // ── Outer _tram boundary toggle ──────────────────────────────────
        public void Tram_SetOuter(bool on)
        {
            _tram.tramBndOuterArr?.Clear();
            _tram.tramBndInnerArr?.Clear();
            if (on)
            {
                _tram.displayMode = TramMode.All;
                _tram.CreateBoundaryOuterTrack();
                _tram.CreateBoundaryInnerTrack();
            }
            Tram_ResetStartNumLabels();
            Tram_BuildTram();
        }

        // ── Alpha ───────────────────────────────────────────────────────
        public void Tram_SetAlpha(double a)
        {
            _tram.alpha = Math.Max(0.2, Math.Min(1.0, a));
        }

        // ── Add lines ───────────────────────────────────────────────────
        public void Tram_AddLines()
        {
            if (tramTmpList.Count > 0)
            {
                for (int i = 0; i < tramTmpList.Count; i++)
                {
                    _tram.tramArr = new List<vec2> { Capacity = 32 };
                    _tram.tramList.Add(_tram.tramArr);

                    for (int j = 0; j < tramTmpList[i].Count; j++)
                    {
                        vec2 tr = new vec2(tramTmpList[i][j]);
                        _tram.tramArr.Add(tr);
                    }
                }
            }

            tramTmpList.Clear();
            tramTmpArr.Clear();
        }

        // ── Delete all ──────────────────────────────────────────────────
        public void Tram_DeleteAll()
        {
            tramTmpList.Clear();
            tramTmpArr.Clear();
            _tram.tramList?.Clear();
            _tram.tramArr?.Clear();
            _tram.tramBndOuterArr?.Clear();
            _tram.tramBndInnerArr?.Clear();

            Tram_ResetStartNumLabels();
            Tram_BuildTram();
        }

        // ── 3-tap cut ───────────────────────────────────────────────────
        public void Tram_Tap(double easting, double northing)
        {
            tramStep++;

            if (tramStep == 1)
            {
                tramPtA = new vec2(easting, northing);
            }
            else if (tramStep == 2)
            {
                tramPtB = new vec2(easting, northing);
            }
            else
            {
                vec2 ptCut = new vec2(easting, northing);

                bool isLeft = (tramPtB.easting - tramPtA.easting) * (ptCut.northing - tramPtA.northing)
                    > (tramPtB.northing - tramPtA.northing) * (ptCut.easting - tramPtA.easting);

                bool isIntersect = false;

                if (tramTmpList.Count > 0)
                {
                    for (int i = 0; i < tramTmpList.Count; i++)
                    {
                        for (int j = 0; j < tramTmpList[i].Count - 1; j++)
                        {
                            if (Tram_GetLineIntersection(
                                tramTmpList[i][j].easting, tramTmpList[i][j].northing,
                                tramTmpList[i][j + 1].easting, tramTmpList[i][j + 1].northing,
                                tramPtA.easting, tramPtA.northing, tramPtB.easting, tramPtB.northing))
                            {
                                isIntersect = true;
                                break;
                            }
                        }

                        if (isIntersect)
                        {
                            for (int h = 0; h < tramTmpList[i].Count; h++)
                            {
                                bool side = (tramPtB.easting - tramPtA.easting) * (tramTmpList[i][h].northing - tramPtA.northing)
                                    > (tramPtB.northing - tramPtA.northing) * (tramTmpList[i][h].easting - tramPtA.easting);
                                if (isLeft == side)
                                {
                                    tramTmpList[i].RemoveAt(h);
                                    h = -1;
                                }
                            }
                        }
                        isIntersect = false;
                    }
                }

                tramPtA = new vec2(9999999, 9999999);
                tramPtB = new vec2(9999999, 9999999);
                tramStep = 0;
            }
        }

        private static bool Tram_GetLineIntersection(double p0x, double p0y, double p1x, double p1y,
            double p2x, double p2y, double p3x, double p3y)
        {
            double s1x = p1x - p0x, s1y = p1y - p0y;
            double s2x = p3x - p2x, s2y = p3y - p2y;

            double s = (-s1y * (p0x - p2x) + s1x * (p0y - p2y)) / (-s2x * s1y + s1x * s2y);
            if (s >= 0 && s <= 1)
            {
                double t = (s2x * (p0y - p2y) - s2y * (p0x - p2x)) / (-s2x * s1y + s1x * s2y);
                if (t >= 0 && t <= 1)
                    return true;
            }
            return false;
        }

        public void Tram_CancelTouch()
        {
            tramPtA = new vec2(9999999, 9999999);
            tramPtB = new vec2(9999999, 9999999);
            tramStep = 0;
        }

        // ── Close (save) ────────────────────────────────────────────────
        public void Tram_CloseSession()
        {
            _guardarTram();
            _refrescarUi();
            _refrescarModo();

            _alphaTram = _tram.alpha;


            tramGTemp.Clear();
            tramTmpList.Clear();
            tramTmpArr.Clear();
        }

        // ── Cancel (revert) ─────────────────────────────────────────────
        public void Tram_CancelSession()
        {
            _tram.tramArr?.Clear();
            _tram.tramList?.Clear();
            _tram.tramBndOuterArr?.Clear();
            _tram.tramBndInnerArr?.Clear();
            _tram.displayMode = 0;

            _guardarTram();
            _refrescarUi();
            _refrescarModo();

            _alphaTram = _tram.alpha;


            tramGTemp.Clear();
            tramTmpList.Clear();
            tramTmpArr.Clear();
        }
    }
}
