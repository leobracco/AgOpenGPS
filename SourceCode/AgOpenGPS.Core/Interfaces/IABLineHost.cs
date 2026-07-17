using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CABLine necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// Tool/Tram/Vehicle/Bnd/Mc/Ahrs/Font ya viven en Core y se exponen
    /// directos; CTrack/CYouTurn/CGuidance (todavía en GPS) se exponen
    /// granulares hasta que se muevan.
    /// </summary>
    public interface IABLineHost
    {
        CTool Tool { get; }
        CTram Tram { get; }
        CVehicle Vehicle { get; }
        CBoundary Bnd { get; }
        CModuleComm Mc { get; }
        CAHRS Ahrs { get; }

        /// <summary>font (Core.DrawLib.Font) — texto 3D en el mapa.</summary>
        AgOpenGPS.Core.DrawLib.Font TextFont { get; }

        // --- trk (CTrack, todavía en GPS) ---
        /// <summary>trk.idx — índice de la pista activa.</summary>
        int TrackIdx { get; }

        /// <summary>trk.gArr — lista de pistas (CTrk ya vive en Core).</summary>
        List<CTrk> Tracks { get; }

        // --- yt (CYouTurn, todavía en GPS) ---
        bool IsYouTurnTriggered { get; }
        bool YouTurnDistanceFromYouTurnLine();
        double YouTurnSteerAngle { get; }
        double YouTurnDistanceFromCurrentLine { get; }
        vec2 YouTurnGoalPoint { get; }
        vec2 YouTurnRadiusPoint { get; }
        double YouTurnPpRadius { get; }
        void DrawYouTurn();

        // --- gyd (CGuidance, todavía en GPS) ---
        double SideHillCompFactor { get; }
        void StanleyGuidanceABLine(vec3 curPtA, vec3 curPtB, vec3 pivot, vec3 steer);

        // --- escalares del host ---
        double SecondsSinceStart { get; }
        bool IsBtnAutoSteerOn { get; }
        vec2 GuidanceLookPos { get; }
        bool IsStanleyUsed { get; }
        bool IsReverse { get; }

        /// <summary>fixHeading — rumbo actual del vehículo (rad).</summary>
        double FixHeading { get; }

        double AvgSpeed { get; }

        /// <summary>camHeading — rumbo de cámara para orientar el texto 3D.</summary>
        double CamHeading { get; }

        bool IsSideGuideLines { get; }

        /// <summary>camera.camSetDistance.</summary>
        double CamSetDistance { get; }

        /// <summary>guidanceLineDistanceOff (mm) — salida hacia el autosteer.</summary>
        short GuidanceLineDistanceOff { set; }

        /// <summary>guidanceLineSteerAngle (centigrados) — salida hacia el autosteer.</summary>
        short GuidanceLineSteerAngle { set; }
    }
}
