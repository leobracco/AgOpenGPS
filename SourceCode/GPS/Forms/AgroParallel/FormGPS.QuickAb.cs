// ============================================================================
// FormGPS.QuickAb.cs — lógica del widget "AB rápido" (ab-rapido.html).
// Reemplaza el WinForm FormQuickAB: crear guías manejando en 3 modos —
// Curva (grabar puntos), Línea AB (punto A + punto B) y A+ (punto A + rumbo).
// La geometría es idéntica al form nativo (mismos cálculos sobre ABLine/curve/
// trk); el preview se dibuja en el mapa GL como siempre (isMakingABLine /
// desList). El "timer1" de 500 ms del form se replica con QuickAb_Tick(),
// invocado desde GetState() (el JS pollea a ese ritmo).
// Todos los métodos asumen hilo UI (el adapter marshalea).
// ============================================================================

using System;
using System.Globalization;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
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

        internal QuickAbSnapshot QuickAb_Snapshot()
        {
            return new QuickAbSnapshot
            {
                HasField = isJobStarted,
                Mode = QuickAb_ModeName(quickAbMode),
                Phase = quickAbPhase == 1 ? "capture" : quickAbPhase == 2 ? "name" : "choose",
                RefRight = quickAbRefRight,
                AMarked = quickAbAMarked,
                BMarked = quickAbBMarked,
                Recording = curve.isRecordingCurve,
                Points = curve.desList != null ? curve.desList.Count : 0,
                HeadingDeg = Math.Round(glm.toDegrees(ABLine.desHeading), 1),
                SuggestedName = quickAbName ?? ""
            };
        }

        // GET /state: mientras se maneja, actualizar el punto B con la posición
        // del tractor (réplica de timer1_Tick, 500 ms).
        internal QuickAbSnapshot QuickAb_Tick()
        {
            bool tracking =
                quickAbPhase == 1 && quickAbAMarked &&
                ((quickAbMode == 2 && !quickAbBMarked) ||
                 (quickAbMode == 3 && !quickAbHeadingLocked));

            if (tracking)
            {
                ABLine.desPtB = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);

                ABLine.desHeading = Math.Atan2(ABLine.desPtB.easting - ABLine.desPtA.easting,
                    ABLine.desPtB.northing - ABLine.desPtA.northing);
                if (ABLine.desHeading < 0) ABLine.desHeading += glm.twoPI;

                QuickAb_SetLineEnds();
            }
            return QuickAb_Snapshot();
        }

        private void QuickAb_SetLineEnds()
        {
            ABLine.desLineEndA.easting = ABLine.desPtA.easting - (Math.Sin(ABLine.desHeading) * 1000);
            ABLine.desLineEndA.northing = ABLine.desPtA.northing - (Math.Cos(ABLine.desHeading) * 1000);

            ABLine.desLineEndB.easting = ABLine.desPtA.easting + (Math.Sin(ABLine.desHeading) * 1000);
            ABLine.desLineEndB.northing = ABLine.desPtA.northing + (Math.Cos(ABLine.desHeading) * 1000);
        }

        // Réplica de btnzABCurve/btnzABLine/btnzAPlus: elegir modo.
        internal QuickAbSnapshot QuickAb_Start(string mode)
        {
            if (!isJobStarted) return QuickAb_Snapshot();

            // Como el launcher nativo: contour apagado antes de crear guías.
            if (ct.isContourBtnOn) btnContour.PerformClick();

            quickAbMode = mode == "curve" ? 1 : mode == "ab" ? 2 : mode == "aplus" ? 3 : 0;
            quickAbPhase = quickAbMode != 0 ? 1 : 0;
            quickAbAMarked = false;
            quickAbBMarked = false;
            quickAbHeadingLocked = false;
            quickAbName = "";
            curve.desList?.Clear();
            return QuickAb_Snapshot();
        }

        internal QuickAbSnapshot QuickAb_ToggleSide()
        {
            quickAbRefRight = !quickAbRefRight;
            return QuickAb_Snapshot();
        }

        // Réplica de btnACurve/btnALine/btnAPlus.
        internal QuickAbSnapshot QuickAb_MarkA()
        {
            if (quickAbPhase != 1) return QuickAb_Snapshot();

            if (quickAbMode == 1)
            {
                if (curve.isMakingCurve)
                {
                    // Segundo toque y siguientes: agregar punto manual.
                    curve.desList.Add(new vec3(pivotAxlePos.easting, pivotAxlePos.northing, pivotAxlePos.heading));
                }
                else
                {
                    quickAbPtA = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
                    curve.isMakingCurve = true;
                    curve.isRecordingCurve = true;
                    quickAbAMarked = true;
                }
            }
            else if (quickAbMode == 2)
            {
                ABLine.isMakingABLine = true;
                ABLine.desPtA = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
                ABLine.desPtB.easting = ABLine.desPtA.easting - (Math.Sin(pivotAxlePos.heading) * 1);
                ABLine.desPtB.northing = ABLine.desPtA.northing - (Math.Cos(pivotAxlePos.heading) * 1);
                ABLine.desHeading = pivotAxlePos.heading;
                QuickAb_SetLineEnds();
                quickAbAMarked = true;
            }
            else if (quickAbMode == 3)
            {
                ABLine.isMakingABLine = true;
                ABLine.desPtA = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
                ABLine.desPtB.easting = ABLine.desPtA.easting + (Math.Sin(pivotAxlePos.heading) * 1);
                ABLine.desPtB.northing = ABLine.desPtA.northing + (Math.Cos(pivotAxlePos.heading) * 1);
                ABLine.desHeading = pivotAxlePos.heading;
                QuickAb_SetLineEnds();
                quickAbAMarked = true;
                quickAbHeadingLocked = false;
            }
            return QuickAb_Snapshot();
        }

        // Réplica de btnPausePlayCurve.
        internal QuickAbSnapshot QuickAb_PauseToggle()
        {
            if (quickAbMode == 1 && curve.isMakingCurve)
                curve.isRecordingCurve = !curve.isRecordingCurve;
            return QuickAb_Snapshot();
        }

        // Réplica de nudHeading (A+): rumbo manual en grados.
        internal QuickAbSnapshot QuickAb_SetHeading(double degrees)
        {
            if (quickAbMode == 3 && quickAbAMarked)
            {
                quickAbHeadingLocked = true;
                ABLine.desHeading = glm.toRadians(degrees);
                if (ABLine.desHeading < 0) ABLine.desHeading += glm.twoPI;

                ABLine.desPtB.easting = ABLine.desPtA.easting + (Math.Sin(ABLine.desHeading) * 200);
                ABLine.desPtB.northing = ABLine.desPtA.northing + (Math.Cos(ABLine.desHeading) * 200);
                QuickAb_SetLineEnds();
            }
            return QuickAb_Snapshot();
        }

        // Réplica de btnBLine (ab: fija B) y btnBCurve (curva: cierra y arma track).
        internal QuickAbSnapshot QuickAb_MarkB()
        {
            if (quickAbPhase != 1 || !quickAbAMarked) return QuickAb_Snapshot();

            if (quickAbMode == 2)
            {
                ABLine.desPtB = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);

                ABLine.desHeading = Math.Atan2(ABLine.desPtB.easting - ABLine.desPtA.easting,
                    ABLine.desPtB.northing - ABLine.desPtA.northing);
                if (ABLine.desHeading < 0) ABLine.desHeading += glm.twoPI;

                QuickAb_SetLineEnds();
                quickAbBMarked = true;
                return QuickAb_Snapshot();
            }

            if (quickAbMode == 1)
            {
                curve.isMakingCurve = false;
                curve.isRecordingCurve = false;
                quickAbPtB = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);

                int cnt = curve.desList.Count;
                if (cnt > 3)
                {
                    CABCurve.MakePointMinimumSpacing(ref curve.desList, 1.6);
                    CABCurve.CalculateHeadings(ref curve.desList);

                    trk.gArr.Add(new CTrk());
                    int idx = trk.gArr.Count - 1;

                    trk.gArr[idx].ptA = new vec2(quickAbPtA);
                    trk.gArr[idx].ptB = new vec2(quickAbPtB);
                    trk.gArr[idx].mode = TrackMode.Curve;

                    // Rumbo promedio de la curva.
                    double x = 0, y = 0;
                    foreach (vec3 pt in curve.desList)
                    {
                        x += Math.Cos(pt.heading);
                        y += Math.Sin(pt.heading);
                    }
                    x /= curve.desList.Count;
                    y /= curve.desList.Count;
                    double aveLineHeading = Math.Atan2(y, x);
                    if (aveLineHeading < 0) aveLineHeading += glm.twoPI;

                    trk.gArr[idx].heading = aveLineHeading;

                    curve.AddFirstLastPoints(ref curve.desList);
                    QuickAb_SmoothAB(4);
                    CABCurve.CalculateHeadings(ref curve.desList);

                    foreach (vec3 item in curve.desList)
                        trk.gArr[idx].curvePts.Add(item);

                    quickAbName = "Cu " +
                        (Math.Round(glm.toDegrees(aveLineHeading), 1)).ToString(CultureInfo.InvariantCulture) + "\u00B0 ";
                    curve.desName = quickAbName;

                    double dist = (tool.width - tool.overlap) * (quickAbRefRight ? 0.5 : -0.5) + tool.offset;
                    trk.idx = idx;
                    trk.NudgeRefCurve(dist);

                    quickAbPhase = 2;
                }
                else
                {
                    // Puntos insuficientes: el form nativo se cierra sin crear nada.
                    curve.desList?.Clear();
                    QuickAb_Reset();
                    var s = QuickAb_Snapshot();
                    s.Error = "puntos-insuficientes";
                    return s;
                }
            }
            return QuickAb_Snapshot();
        }

        // Réplica de btnEnter_AB / btnEnter_APlus: confirmar línea → fase nombre.
        internal QuickAbSnapshot QuickAb_Commit()
        {
            if (quickAbPhase != 1 || !quickAbAMarked) return QuickAb_Snapshot();
            if (quickAbMode == 2 && !quickAbBMarked) return QuickAb_Snapshot();
            if (quickAbMode != 2 && quickAbMode != 3) return QuickAb_Snapshot();

            ABLine.isMakingABLine = false;
            trk.gArr.Add(new CTrk());
            int idx = trk.gArr.Count - 1;

            trk.gArr[idx].ptA = new vec2(ABLine.desPtA);
            trk.gArr[idx].ptB = new vec2(ABLine.desPtB);
            trk.gArr[idx].mode = TrackMode.AB;
            trk.gArr[idx].heading = ABLine.desHeading;

            string prefix = quickAbMode == 2 ? "AB " : "A+";
            quickAbName = prefix +
                (Math.Round(glm.toDegrees(ABLine.desHeading), 5)).ToString(CultureInfo.InvariantCulture) + "\u00B0 ";
            trk.gArr[idx].name = quickAbName;
            ABLine.desName = quickAbName;

            double dist = (tool.width - tool.overlap) * (quickAbRefRight ? 0.5 : -0.5) + tool.offset;
            trk.idx = idx;
            trk.NudgeRefABLine(dist);

            quickAbPhase = 2;
            return QuickAb_Snapshot();
        }

        // Réplica de btnAdd: nombrar, persistir y cerrar la sesión.
        internal QuickAbSnapshot QuickAb_Save(string name)
        {
            if (quickAbPhase != 2) return QuickAb_Snapshot();

            if (string.IsNullOrWhiteSpace(name))
                name = "No Name " + DateTime.Now.ToString("hh:mm:ss", CultureInfo.InvariantCulture);

            int idx = trk.gArr.Count - 1;
            trk.gArr[idx].name = name.Trim();

            curve.desList?.Clear();
            FileSaveTracks();

            bool stopped = false;
            if (isBtnAutoSteerOn)
            {
                btnAutoSteer.PerformClick();
                stopped = true;
            }
            if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();

            ABLine.isMakingABLine = false;
            trk.idx = idx;

            QuickAb_Reset();
            var s = QuickAb_Snapshot();
            s.GuidanceStopped = stopped;
            return s;
        }

        // Réplica de btnCancelCurve / cierre sin guardar.
        internal QuickAbSnapshot QuickAb_Cancel()
        {
            curve.desList?.Clear();
            ABLine.isMakingABLine = false;
            curve.isMakingCurve = false;
            curve.isRecordingCurve = false;
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

            // Como el FormClosing nativo: refrescar paneles enseguida.
            twoSecondCounter = 100;
            PanelUpdateRightAndBottom();
        }

        // Réplica de SmoothAB del form (promedio centrado sobre desList).
        private void QuickAb_SmoothAB(int smPts)
        {
            int cnt = curve.desList.Count;
            vec3[] arr = new vec3[cnt];

            for (int s = 0; s < smPts / 2; s++)
            {
                arr[s].easting = curve.desList[s].easting;
                arr[s].northing = curve.desList[s].northing;
                arr[s].heading = curve.desList[s].heading;
            }

            for (int s = cnt - (smPts / 2); s < cnt; s++)
            {
                arr[s].easting = curve.desList[s].easting;
                arr[s].northing = curve.desList[s].northing;
                arr[s].heading = curve.desList[s].heading;
            }

            for (int i = smPts / 2; i < cnt - (smPts / 2); i++)
            {
                for (int j = -smPts / 2; j < smPts / 2; j++)
                {
                    arr[i].easting += curve.desList[j + i].easting;
                    arr[i].northing += curve.desList[j + i].northing;
                }
                arr[i].easting /= smPts;
                arr[i].northing /= smPts;
                arr[i].heading = curve.desList[i].heading;
            }

            curve.desList?.Clear();
            for (int i = 0; i < cnt; i++)
                curve.desList.Add(arr[i]);
        }

        // POCO intermedio (assembly GPS) — el adapter lo copia al DTO de Models.
        internal sealed class QuickAbSnapshot
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
