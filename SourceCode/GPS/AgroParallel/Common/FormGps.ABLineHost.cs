// ============================================================================
// FormGps.ABLineHost.cs
// Implementación de IABLineHost sobre FormGPS. CABLine vive en AgOpenGPS.Core
// y consume el host a través de esta interfaz (inversión de dependencia —
// traspaso de portabilidad 2026-07-17).
// ============================================================================

using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS : IABLineHost, ITextFontHost
    {
        CTool IABLineHost.Tool => tool;
        CTram IABLineHost.Tram => tram;
        CVehicle IABLineHost.Vehicle => vehicle;
        CBoundary IABLineHost.Bnd => bnd;
        CModuleComm IABLineHost.Mc => mc;
        CAHRS IABLineHost.Ahrs => ahrs;

        AgOpenGPS.Core.DrawLib.Font ITextFontHost.TextFont => font;

        int IABLineHost.TrackIdx
        {
            get => trk.idx;
            set => trk.idx = value;
        }
        List<CTrk> IABLineHost.Tracks => trk.gArr;

        bool IABLineHost.IsYouTurnTriggered => yt.isYouTurnTriggered;
        bool IABLineHost.YouTurnDistanceFromYouTurnLine() => yt.DistanceFromYouTurnLine();
        double IABLineHost.YouTurnSteerAngle => yt.steerAngleYT;
        double IABLineHost.YouTurnDistanceFromCurrentLine => yt.distanceFromCurrentLine;
        vec2 IABLineHost.YouTurnGoalPoint => yt.goalPointYT;
        vec2 IABLineHost.YouTurnRadiusPoint => yt.radiusPointYT;
        double IABLineHost.YouTurnPpRadius => yt.ppRadiusYT;
        void IABLineHost.DrawYouTurn() => yt.DrawYouTurn(ABLine.lineWidth);

        double IABLineHost.SideHillCompFactor => gyd.sideHillCompFactor;
        void IABLineHost.StanleyGuidanceABLine(vec3 curPtA, vec3 curPtB, vec3 pivot, vec3 steer)
            => gyd.StanleyGuidanceABLine(curPtA, curPtB, pivot, steer);

        double IABLineHost.SecondsSinceStart => secondsSinceStart;
        bool IABLineHost.IsBtnAutoSteerOn => isBtnAutoSteerOn;
        vec2 IABLineHost.GuidanceLookPos => guidanceLookPos;
        bool IABLineHost.IsStanleyUsed => isStanleyUsed;
        bool IABLineHost.IsReverse => isReverse;
        double IABLineHost.FixHeading => fixHeading;
        double IABLineHost.AvgSpeed => avgSpeed;
        double IABLineHost.CamHeading => camHeading;
        bool IABLineHost.IsSideGuideLines => isSideGuideLines;
        double IABLineHost.CamSetDistance => camera.camSetDistance;

        short IABLineHost.GuidanceLineDistanceOff { set => guidanceLineDistanceOff = value; }
        short IABLineHost.GuidanceLineSteerAngle { set => guidanceLineSteerAngle = value; }
    }
}
