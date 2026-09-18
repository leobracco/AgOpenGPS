using AgOpenGPS.Core;
using AgOpenGPS.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CPositionUpdater (AddBoundaryPoint/AddContourPoints/
    /// AddSectionOrPathPoints/InitializeFirstFewGPSPositions/
    /// CalculatePositionHeading/CalculateSectionLookAhead, extraídos de
    /// Position.designer.cs) necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (bloque 9
    /// matriz Android, 2026-07-19). El estado que tocan estos métodos
    /// (bnd/ct/recPath/curve/tool/vehicle/pn/triStrip/section) ya vive en
    /// Core y se expone directo; lo único que es UI/render (SetZoom) cruza
    /// como método.
    /// </summary>
    public interface IPositionHost
    {
        ApplicationModel AppModel { get; }
        CNMEA Pn { get; }
        CBoundary Bnd { get; }
        CContour Ct { get; }
        CRecordedPath RecPath { get; }
        CABCurve Curve { get; }
        CTool Tool { get; }
        CVehicle Vehicle { get; }
        CAHRS Ahrs { get; }
        CSection[] Section { get; }
        List<CPatches> TriStrip { get; }
        StringBuilder SbGrid { get; }

        vec3 PivotAxlePos { get; set; }
        vec3 SteerAxlePos { get; set; }
        vec3 ToolPivotPos { get; set; }
        vec3 TankPos { get; set; }
        vec3 ToolPos { get; set; }
        vec2 GuidanceLookPos { get; set; }
        vec2 HitchPos { get; set; }
        vec2 PrevBoundaryPos { get; set; }
        vec2 PrevContourPos { get; set; }
        vec2 PrevSectionPos { get; set; }
        vec2 PrevGridPos { get; set; }
        vec2 PrevFix { get; set; }
        vec2 LastReverseFix { get; set; }

        btnStates ManualBtnState { get; }
        btnStates AutoBtnState { get; }

        double AvgSpeed { get; }
        double FixHeading { get; set; }
        double GpsHz { get; }
        double GuidanceLookAheadTime { get; }
        double DistanceCurrentStepFix { get; }
        double SectionTriggerStepDistance { get; set; }
        double SectionTriggerDistance { get; set; }
        double ContourTriggerDistance { get; set; }
        double GridTriggerDistance { get; set; }
        double SinSectionHeading { get; set; }
        double CosSectionHeading { get; set; }
        int PatchCounter { get; set; }
        bool IsPatchesChangingColor { get; set; }
        bool IsLogElevation { get; }

        /// <summary>startCounter — frames desde el arranque del loop de posición.</summary>
        int StartCounter { get; }
        bool IsFirstFixPositionSet { get; set; }
        bool IsGPSPositionInitialized { get; set; }
        bool IsJobStarted { get; }

        /// <summary>isDayTime — solo lo calcula este método, no lo lee.</summary>
        bool IsDayTime { set; }
        DateTime Sunrise { get; }
        DateTime Sunset { get; }

        /// <summary>Recalcula perspectiva/grid según distancia de cámara (GL, se queda en FormGPS).</summary>
        void SetZoom();

        /// <summary>¿Las matrices GL de abajo son válidas (se capturaron este frame)?</summary>
        bool GlMatricesValid { get; }
        /// <summary>Viewport GL [x, y, width, height] del último frame.</summary>
        int[] GlViewport { get; }
        /// <summary>Matriz de proyección GL (column-major, 16) del último frame.</summary>
        double[] GlProjection { get; }
        /// <summary>Matriz modelview GL (column-major, 16) del último frame.</summary>
        double[] GlModelView { get; }
    }
}
