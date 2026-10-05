// ============================================================================
// FormGPS.QuickAb.cs — lógica del widget "AB rápido" (ab-rapido.html).
// Reemplaza el WinForm FormQuickAB: crear guías manejando en 3 modos —
// Curva (grabar puntos), Línea AB (punto A + punto B) y A+ (punto A + rumbo).
// La geometría es idéntica al form nativo (mismos cálculos sobre _abLine/_curve/
// _trk); el preview se dibuja en el mapa GL como siempre (isMakingABLine /
// desList). El "timer1" de 500 ms del form se replica con QuickAb_Tick(),
// invocado desde GetState() (el JS pollea a ese ritmo).
// Todos los métodos asumen hilo UI (el adapter marshalea).
// ============================================================================

using System;
using System.Globalization;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public sealed class QuickAbEditor
    {
        private readonly CABLine _abLine;
        private readonly CABCurve _curve;
        private readonly CTrack _trk;
        private readonly CTool _tool;
        private readonly CYouTurn _yt;
        private readonly CContour _ct;

        /// <summary>Pivote del vehículo: es desde donde se marcan A y B.</summary>
        private readonly Func<vec3> _pivoteFn;

        private readonly Func<bool> _hayLoteFn;

        /// <summary>Si el piloto esta enganchado. Al guardar una guia nueva hay
        /// que soltarlo: quedaria siguiendo la linea vieja.</summary>
        private readonly Func<bool> _pilotoPrendidoFn;

        /// <summary>Persistir TrackLines.txt.</summary>
        private readonly Action _guardarGuias;

        // En FormGPS estas tres eran `btnXxx.PerformClick()` — o sea, se usaba
        // el botón de la pantalla como forma de invocar lógica. Acá entran como
        // callbacks para que el motor pueda hacer lo mismo sin pantalla.
        private readonly Action _alternarContorno;
        private readonly Action _alternarPiloto;
        private readonly Action _alternarGiro;

        private readonly Action _refrescarUi;

        public QuickAbEditor(
            CABLine abLine,
            CABCurve curve,
            CTrack trk,
            CTool tool,
            CYouTurn yt,
            CContour ct,
            Func<vec3> pivote,
            Func<bool> hayLote,
            Func<bool> pilotoPrendido,
            Action guardarGuias,
            Action alternarContorno = null,
            Action alternarPiloto = null,
            Action alternarGiro = null,
            Action refrescarUi = null)
        {
            _abLine = abLine;
            _curve = curve;
            _trk = trk;
            _tool = tool;
            _yt = yt;
            _ct = ct;
            _pivoteFn = pivote ?? (() => new vec3());
            _hayLoteFn = hayLote ?? (() => false);
            _pilotoPrendidoFn = pilotoPrendido ?? (() => false);
            _guardarGuias = guardarGuias ?? (() => { });
            _alternarContorno = alternarContorno ?? (() => { });
            _alternarPiloto = alternarPiloto ?? (() => { });
            _alternarGiro = alternarGiro ?? (() => { });
            _refrescarUi = refrescarUi ?? (() => { });
        }

        private vec3 _pivote => _pivoteFn();
        private bool _hayLote => _hayLoteFn();

        // Sesión: 0 = sin sesión, 1 = curve, 2 = ab, 3 = aplus.
        private int quickAbMode;
        private int quickAbPhase;          // 0 choose, 1 capture, 2 name
        private bool quickAbRefRight = true;
        private bool quickAbAMarked;
        private bool quickAbBMarked;
        private bool quickAbHeadingLocked; // A+: rumbo fijado a mano
        private vec2 quickAbPtA;
        private vec2 quickAbPtB;
        private string quickAbName = "";

        private static string QuickAb_ModeName(int m) =>
            m == 1 ? "curve" : m == 2 ? "ab" : m == 3 ? "aplus" : "none";

        public QuickAbSnapshot QuickAb_Snapshot()
        {
            return new QuickAbSnapshot
            {
                HasField = _hayLote,
                Mode = QuickAb_ModeName(quickAbMode),
                Phase = quickAbPhase == 1 ? "capture" : quickAbPhase == 2 ? "name" : "choose",
                RefRight = quickAbRefRight,
                AMarked = quickAbAMarked,
                BMarked = quickAbBMarked,
                Recording = _curve.isRecordingCurve,
                Points = _curve.desList != null ? _curve.desList.Count : 0,
                HeadingDeg = Math.Round(glm.toDegrees(_abLine.desHeading), 1),
                SuggestedName = quickAbName ?? ""
            };
        }

        // GET /state: mientras se maneja, actualizar el punto B con la posición
        // del tractor (réplica de timer1_Tick, 500 ms).
        public QuickAbSnapshot QuickAb_Tick()
        {
            bool tracking =
                quickAbPhase == 1 && quickAbAMarked &&
                ((quickAbMode == 2 && !quickAbBMarked) ||
                 (quickAbMode == 3 && !quickAbHeadingLocked));

            if (tracking)
            {
                _abLine.desPtB = new vec2(_pivote.easting, _pivote.northing);

                _abLine.desHeading = Math.Atan2(_abLine.desPtB.easting - _abLine.desPtA.easting,
                    _abLine.desPtB.northing - _abLine.desPtA.northing);
                if (_abLine.desHeading < 0) _abLine.desHeading += glm.twoPI;

                QuickAb_SetLineEnds();
            }
            return QuickAb_Snapshot();
        }

        private void QuickAb_SetLineEnds()
        {
            _abLine.desLineEndA.easting = _abLine.desPtA.easting - (Math.Sin(_abLine.desHeading) * 1000);
            _abLine.desLineEndA.northing = _abLine.desPtA.northing - (Math.Cos(_abLine.desHeading) * 1000);

            _abLine.desLineEndB.easting = _abLine.desPtA.easting + (Math.Sin(_abLine.desHeading) * 1000);
            _abLine.desLineEndB.northing = _abLine.desPtA.northing + (Math.Cos(_abLine.desHeading) * 1000);
        }

        // Réplica de btnzABCurve/btnzABLine/btnzAPlus: elegir modo.
        public QuickAbSnapshot QuickAb_Start(string mode)
        {
            if (!_hayLote) return QuickAb_Snapshot();

            // Como el launcher nativo: contour apagado antes de crear guías.
            if (_ct.isContourBtnOn) _alternarContorno();

            quickAbMode = mode == "curve" ? 1 : mode == "ab" ? 2 : mode == "aplus" ? 3 : 0;
            quickAbPhase = quickAbMode != 0 ? 1 : 0;
            quickAbAMarked = false;
            quickAbBMarked = false;
            quickAbHeadingLocked = false;
            quickAbName = "";
            _curve.desList?.Clear();
            return QuickAb_Snapshot();
        }

        public QuickAbSnapshot QuickAb_ToggleSide()
        {
            quickAbRefRight = !quickAbRefRight;
            return QuickAb_Snapshot();
        }

        // Réplica de btnACurve/btnALine/btnAPlus.
        public QuickAbSnapshot QuickAb_MarkA()
        {
            if (quickAbPhase != 1) return QuickAb_Snapshot();

            if (quickAbMode == 1)
            {
                if (_curve.isMakingCurve)
                {
                    // Segundo toque y siguientes: agregar punto manual.
                    _curve.desList.Add(new vec3(_pivote.easting, _pivote.northing, _pivote.heading));
                }
                else
                {
                    quickAbPtA = new vec2(_pivote.easting, _pivote.northing);
                    _curve.isMakingCurve = true;
                    _curve.isRecordingCurve = true;
                    quickAbAMarked = true;
                }
            }
            else if (quickAbMode == 2)
            {
                _abLine.isMakingABLine = true;
                _abLine.desPtA = new vec2(_pivote.easting, _pivote.northing);
                _abLine.desPtB.easting = _abLine.desPtA.easting - (Math.Sin(_pivote.heading) * 1);
                _abLine.desPtB.northing = _abLine.desPtA.northing - (Math.Cos(_pivote.heading) * 1);
                _abLine.desHeading = _pivote.heading;
                QuickAb_SetLineEnds();
                quickAbAMarked = true;
            }
            else if (quickAbMode == 3)
            {
                _abLine.isMakingABLine = true;
                _abLine.desPtA = new vec2(_pivote.easting, _pivote.northing);
                _abLine.desPtB.easting = _abLine.desPtA.easting + (Math.Sin(_pivote.heading) * 1);
                _abLine.desPtB.northing = _abLine.desPtA.northing + (Math.Cos(_pivote.heading) * 1);
                _abLine.desHeading = _pivote.heading;
                QuickAb_SetLineEnds();
                quickAbAMarked = true;
                quickAbHeadingLocked = false;
            }
            return QuickAb_Snapshot();
        }

        // Réplica de btnPausePlayCurve.
        public QuickAbSnapshot QuickAb_PauseToggle()
        {
            if (quickAbMode == 1 && _curve.isMakingCurve)
                _curve.isRecordingCurve = !_curve.isRecordingCurve;
            return QuickAb_Snapshot();
        }

        // Réplica de nudHeading (A+): rumbo manual en grados.
        public QuickAbSnapshot QuickAb_SetHeading(double degrees)
        {
            if (quickAbMode == 3 && quickAbAMarked)
            {
                quickAbHeadingLocked = true;
                _abLine.desHeading = glm.toRadians(degrees);
                if (_abLine.desHeading < 0) _abLine.desHeading += glm.twoPI;

                _abLine.desPtB.easting = _abLine.desPtA.easting + (Math.Sin(_abLine.desHeading) * 200);
                _abLine.desPtB.northing = _abLine.desPtA.northing + (Math.Cos(_abLine.desHeading) * 200);
                QuickAb_SetLineEnds();
            }
            return QuickAb_Snapshot();
        }

        // Réplica de btnBLine (ab: fija B) y btnBCurve (curva: cierra y arma track).
        public QuickAbSnapshot QuickAb_MarkB()
        {
            if (quickAbPhase != 1 || !quickAbAMarked) return QuickAb_Snapshot();

            if (quickAbMode == 2)
            {
                _abLine.desPtB = new vec2(_pivote.easting, _pivote.northing);

                _abLine.desHeading = Math.Atan2(_abLine.desPtB.easting - _abLine.desPtA.easting,
                    _abLine.desPtB.northing - _abLine.desPtA.northing);
                if (_abLine.desHeading < 0) _abLine.desHeading += glm.twoPI;

                QuickAb_SetLineEnds();
                quickAbBMarked = true;
                return QuickAb_Snapshot();
            }

            if (quickAbMode == 1)
            {
                _curve.isMakingCurve = false;
                _curve.isRecordingCurve = false;
                quickAbPtB = new vec2(_pivote.easting, _pivote.northing);

                int cnt = _curve.desList.Count;
                if (cnt > 3)
                {
                    CABCurve.MakePointMinimumSpacing(ref _curve.desList, 1.6);
                    CABCurve.CalculateHeadings(ref _curve.desList);

                    _trk.gArr.Add(new CTrk());
                    int idx = _trk.gArr.Count - 1;

                    _trk.gArr[idx].ptA = new vec2(quickAbPtA);
                    _trk.gArr[idx].ptB = new vec2(quickAbPtB);
                    _trk.gArr[idx].mode = TrackMode.Curve;

                    // Rumbo promedio de la curva.
                    double x = 0, y = 0;
                    foreach (vec3 pt in _curve.desList)
                    {
                        x += Math.Cos(pt.heading);
                        y += Math.Sin(pt.heading);
                    }
                    x /= _curve.desList.Count;
                    y /= _curve.desList.Count;
                    double aveLineHeading = Math.Atan2(y, x);
                    if (aveLineHeading < 0) aveLineHeading += glm.twoPI;

                    _trk.gArr[idx].heading = aveLineHeading;

                    _curve.AddFirstLastPoints(ref _curve.desList);
                    QuickAb_SmoothAB(4);
                    CABCurve.CalculateHeadings(ref _curve.desList);

                    foreach (vec3 item in _curve.desList)
                        _trk.gArr[idx].curvePts.Add(item);

                    quickAbName = "Cu " +
                        (Math.Round(glm.toDegrees(aveLineHeading), 1)).ToString(CultureInfo.InvariantCulture) + "\u00B0 ";
                    _curve.desName = quickAbName;

                    double dist = (_tool.width - _tool.overlap) * (quickAbRefRight ? 0.5 : -0.5) + _tool.offset;
                    _trk.idx = idx;
                    _trk.NudgeRefCurve(dist);

                    quickAbPhase = 2;
                }
                else
                {
                    // Puntos insuficientes: el form nativo se cierra sin crear nada.
                    _curve.desList?.Clear();
                    QuickAb_Reset();
                    var s = QuickAb_Snapshot();
                    s.Error = "puntos-insuficientes";
                    return s;
                }
            }
            return QuickAb_Snapshot();
        }

        // Réplica de btnEnter_AB / btnEnter_APlus: confirmar línea → fase nombre.
        public QuickAbSnapshot QuickAb_Commit()
        {
            if (quickAbPhase != 1 || !quickAbAMarked) return QuickAb_Snapshot();
            if (quickAbMode == 2 && !quickAbBMarked) return QuickAb_Snapshot();
            if (quickAbMode != 2 && quickAbMode != 3) return QuickAb_Snapshot();

            _abLine.isMakingABLine = false;
            _trk.gArr.Add(new CTrk());
            int idx = _trk.gArr.Count - 1;

            _trk.gArr[idx].ptA = new vec2(_abLine.desPtA);
            _trk.gArr[idx].ptB = new vec2(_abLine.desPtB);
            _trk.gArr[idx].mode = TrackMode.AB;
            _trk.gArr[idx].heading = _abLine.desHeading;

            string prefix = quickAbMode == 2 ? "AB " : "A+";
            quickAbName = prefix +
                (Math.Round(glm.toDegrees(_abLine.desHeading), 5)).ToString(CultureInfo.InvariantCulture) + "\u00B0 ";
            _trk.gArr[idx].name = quickAbName;
            _abLine.desName = quickAbName;

            double dist = (_tool.width - _tool.overlap) * (quickAbRefRight ? 0.5 : -0.5) + _tool.offset;
            _trk.idx = idx;
            _trk.NudgeRefABLine(dist);

            quickAbPhase = 2;
            return QuickAb_Snapshot();
        }

        // Réplica de btnAdd: nombrar, persistir y cerrar la sesión.
        public QuickAbSnapshot QuickAb_Save(string name)
        {
            if (quickAbPhase != 2) return QuickAb_Snapshot();

            if (string.IsNullOrWhiteSpace(name))
                name = "No Name " + DateTime.Now.ToString("hh:mm:ss", CultureInfo.InvariantCulture);

            int idx = _trk.gArr.Count - 1;
            _trk.gArr[idx].name = name.Trim();

            _curve.desList?.Clear();
            _guardarGuias();

            bool stopped = false;
            if (_pilotoPrendidoFn())
            {
                _alternarPiloto();
                stopped = true;
            }
            if (_yt.isYouTurnBtnOn) _alternarGiro();

            _abLine.isMakingABLine = false;
            _trk.idx = idx;

            QuickAb_Reset();
            var s = QuickAb_Snapshot();
            s.GuidanceStopped = stopped;
            return s;
        }

        // Réplica de btnCancelCurve / cierre sin guardar.
        public QuickAbSnapshot QuickAb_Cancel()
        {
            _curve.desList?.Clear();
            _abLine.isMakingABLine = false;
            _curve.isMakingCurve = false;
            _curve.isRecordingCurve = false;
            QuickAb_Reset();
            return QuickAb_Snapshot();
        }

        private void QuickAb_Reset()
        {
            quickAbMode = 0;
            quickAbPhase = 0;
            quickAbAMarked = false;
            quickAbBMarked = false;
            quickAbHeadingLocked = false;
            quickAbName = "";

            // Como el FormClosing nativo: refrescar paneles enseguida. En
            // FormGPS eso se lograba forzando un contador de la GUI; aca lo
            // hace el propio callback, que es lo que ese contador terminaba
            // disparando.
            _refrescarUi();
        }

        // Réplica de SmoothAB del form (promedio centrado sobre desList).
        private void QuickAb_SmoothAB(int smPts)
        {
            int cnt = _curve.desList.Count;
            vec3[] arr = new vec3[cnt];

            for (int s = 0; s < smPts / 2; s++)
            {
                arr[s].easting = _curve.desList[s].easting;
                arr[s].northing = _curve.desList[s].northing;
                arr[s].heading = _curve.desList[s].heading;
            }

            for (int s = cnt - (smPts / 2); s < cnt; s++)
            {
                arr[s].easting = _curve.desList[s].easting;
                arr[s].northing = _curve.desList[s].northing;
                arr[s].heading = _curve.desList[s].heading;
            }

            for (int i = smPts / 2; i < cnt - (smPts / 2); i++)
            {
                for (int j = -smPts / 2; j < smPts / 2; j++)
                {
                    arr[i].easting += _curve.desList[j + i].easting;
                    arr[i].northing += _curve.desList[j + i].northing;
                }
                arr[i].easting /= smPts;
                arr[i].northing /= smPts;
                arr[i].heading = _curve.desList[i].heading;
            }

            _curve.desList?.Clear();
            for (int i = 0; i < cnt; i++)
                _curve.desList.Add(arr[i]);
        }

        // POCO intermedio (assembly GPS) — el adapter lo copia al DTO de Models.
        public sealed class QuickAbSnapshot
        {
            public bool HasField;
            public string Mode;
            public string Phase;
            public bool RefRight;
            public bool AMarked;
            public bool BMarked;
            public bool Recording;
            public int Points;
            public double HeadingDeg;
            public string SuggestedName;
            public bool GuidanceStopped;
            public string Error;
        }
    }
}
