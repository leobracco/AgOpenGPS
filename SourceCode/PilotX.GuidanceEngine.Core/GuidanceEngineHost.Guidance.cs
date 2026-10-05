// ============================================================================
// GuidanceEngineHost.Guidance.cs — IABLineHost, IABCurveHost, IGuidanceHost,
// ITrackHost, IYouTurnHost. Mismo patrón que FormGps.ABLineHost.cs/
// FormGps.YouTurnHost.cs, sin GL (DrawYouTurn no-op) ni sonidos (log).
// ============================================================================

using System;
using System.Collections.Generic;
using AgLibrary.Logging;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost :
        IABLineHost, IABCurveHost, IGuidanceHost, ITrackHost, IYouTurnHost
    {
        // camera.camSetDistance en FormGPS — acá un escalar fijo: solo afecta
        // la orientación del texto 3D dibujado, irrelevante sin render.
        public double camSetDistance = -100;
        public bool isBoundAlarming;

        CTool IABLineHost.Tool => Tool;
        CTram IABLineHost.Tram => Tram;
        CVehicle IABLineHost.Vehicle => Vehicle;
        CBoundary IABLineHost.Bnd => Bnd;
        CModuleComm IABLineHost.Mc => Mc;
        CAHRS IABLineHost.Ahrs => Ahrs;

        int IABLineHost.TrackIdx { get => Trk.idx; set => Trk.idx = value; }
        List<CTrk> IABLineHost.Tracks => Trk.gArr;

        bool IABLineHost.IsYouTurnTriggered => Yt.isYouTurnTriggered;
        bool IABLineHost.YouTurnDistanceFromYouTurnLine() => Yt.DistanceFromYouTurnLine();
        double IABLineHost.YouTurnSteerAngle => Yt.steerAngleYT;
        double IABLineHost.YouTurnDistanceFromCurrentLine => Yt.distanceFromCurrentLine;
        vec2 IABLineHost.YouTurnGoalPoint => Yt.goalPointYT;
        vec2 IABLineHost.YouTurnRadiusPoint => Yt.radiusPointYT;
        double IABLineHost.YouTurnPpRadius => Yt.ppRadiusYT;

        // sin ventana/GL: nada que dibujar.
        void IABLineHost.DrawYouTurn() { }

        double IABLineHost.SideHillCompFactor => Gyd.sideHillCompFactor;
        void IABLineHost.StanleyGuidanceABLine(vec3 curPtA, vec3 curPtB, vec3 pivot, vec3 steer)
            => Gyd.StanleyGuidanceABLine(curPtA, curPtB, pivot, steer);

        double IABLineHost.SecondsSinceStart => secondsSinceStart;
        bool IABLineHost.IsBtnAutoSteerOn => isBtnAutoSteerOn;
        vec2 IABLineHost.GuidanceLookPos => guidanceLookPos;
        bool IABLineHost.IsStanleyUsed => isStanleyUsed;
        bool IABLineHost.IsReverse => isReverse;
        double IABLineHost.FixHeading => fixHeading;
        double IABLineHost.AvgSpeed => avgSpeed;
        double IABLineHost.CamHeading => camHeading;
        bool IABLineHost.IsSideGuideLines => isSideGuideLines;
        double IABLineHost.CamSetDistance => camSetDistance;

        short IABLineHost.GuidanceLineDistanceOff { set => guidanceLineDistanceOff = value; }
        short IABLineHost.GuidanceLineSteerAngle { set => guidanceLineSteerAngle = value; }

        // ---- IABCurveHost ----
        CABLine IABCurveHost.ABLine => ABLineField;
        vec3 IABCurveHost.PivotAxlePos => pivotAxlePos;
        vec3 IABCurveHost.SteerAxlePos => steerAxlePos;
        void IABCurveHost.PerformAutoSteerClick() => ((IAutoSteerHost)this).PerformAutoSteerClick();
        void IABCurveHost.TimedMessageBox(int timeout, string title, string message)
            => ((IAutoSteerHost)this).TimedMessageBox(timeout, title, message);
        void IABCurveHost.StanleyGuidanceCurve(vec3 pivot, vec3 steer, ref List<vec3> curList)
            => Gyd.StanleyGuidanceCurve(pivot, steer, ref curList);

        // ---- IGuidanceHost ----
        CABLine IGuidanceHost.ABLine => ABLineField;
        CABCurve IGuidanceHost.Curve => CurveField;

        // ---- ITrackHost ----
        vec3 ITrackHost.SteerAxlePos => steerAxlePos;

        // ---- IYouTurnHost ----
        btnStates IYouTurnHost.AutoBtnState => autoBtnState;
        btnStates IYouTurnHost.ManualBtnState => manualBtnState;
        double IYouTurnHost.DistancePivotToTurnLine { get => distancePivotToTurnLine; set => distancePivotToTurnLine = value; }
        int IYouTurnHost.MakeUTurnCounter { get => makeUTurnCounter; set => makeUTurnCounter = value; }
        vec3 IYouTurnHost.PivotAxlePos => pivotAxlePos;
        vec3 IYouTurnHost.SteerAxlePos => steerAxlePos;
        bool IYouTurnHost.IsBoundAlarming { get => isBoundAlarming; set => isBoundAlarming = value; }
        void IYouTurnHost.ClearUTurnPgn() => P239Field.pgn[P239Field.uturn] = 0;
        CYouTurn IYouTurnHost.Yt => Yt;
        int IYouTurnHost.CrossTrackError => crossTrackError;
        bool IYouTurnHost.IsTurnSoundOn => false;
        void IYouTurnHost.PlayTurnTooCloseSound() => Log.EventWriter("GuidanceEngine: [sonido] giro demasiado cerca");
        void IYouTurnHost.PlayBoundaryAlarmSound() => Log.EventWriter("GuidanceEngine: [sonido] alarma de boundary");

        // Mismo comportamiento que btnAutoYouTurn_Click (Controls.Designer.cs),
        // sin la línea de ícono (btnAutoYouTurn.Image, irrelevante sin UI).
        // Comando "uturn" en GuidanceEngineHost.Commands.cs.
        public void ToggleYouTurn()
        {
            Yt.isTurnCreationTooClose = false;

            if (Bnd.bndList.Count == 0)
            {
                Log.EventWriter("GuidanceEngine: uturn intentado sin boundary");
                return;
            }

            Yt.turnTooCloseTrigger = false;

            if (!Yt.isYouTurnBtnOn)
            {
                Yt.ResetCreatedYouTurn();

                if (Trk.idx == -1) return;

                Yt.isYouTurnBtnOn = true;
                Yt.isTurnCreationTooClose = false;
                Yt.isTurnCreationNotCrossingError = false;
                Yt.ResetYouTurn();
            }
            else
            {
                Yt.isYouTurnBtnOn = false;
                Yt.RestorePreTriggerState();
                Yt.ResetYouTurn();
                Yt.ResetCreatedYouTurn();
            }

            string msg = "GuidanceEngine: uturn " + (Yt.isYouTurnBtnOn ? "ON" : "OFF");
            Log.EventWriter(msg);
            Console.WriteLine(msg);
        }

        // Mismo comportamiento que btnTrack_Click (Controls.Designer.cs) para
        // el caso "elegir guía" — selecciona la primera guía visible cargada
        // (o la 0 si ninguna está marcada visible). Sin esto Trk.idx se queda
        // en -1 después de OpenField() y ninguna guía queda activa (mismo
        // comportamiento que FormGPS: cargar el archivo no selecciona nada
        // solo). El resto de btnTrack_Click (flyout de nudge/build/ABDraw)
        // es panel HTML/WinForms, no aplica sin UI. Comando "pick" en
        // GuidanceEngineHost.Commands.cs — mismo nombre que
        // IGuidanceCalculator.ExecuteCommand ("pick" = elegir guía).
        public void SelectTrack()
        {
            if (Trk.gArr.Count > 0 && Trk.idx == -1)
            {
                Trk.idx = Trk.gArr.FindIndex(t => t.isVisible);
                if (Trk.idx == -1) Trk.idx = 0;
            }
            string msg = "GuidanceEngine: pick -> track idx=" + Trk.idx + "/" + Trk.gArr.Count;
            Log.EventWriter(msg);
            Console.WriteLine(msg);
        }
    }
}
