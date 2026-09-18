// ============================================================================
// GuidanceEngineHost.Position.cs — implementación de IPositionHost.
// Mismo patrón que GPS/AgroParallel/Common/FormGps.PositionHost.cs, sin GL.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using AgOpenGPS.Core;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost : IPositionHost
    {
        ApplicationModel IPositionHost.AppModel => AppModelField;
        CNMEA IPositionHost.Pn => Pn;
        CBoundary IPositionHost.Bnd => Bnd;
        CContour IPositionHost.Ct => Ct;
        CRecordedPath IPositionHost.RecPath => RecPath;
        CABCurve IPositionHost.Curve => CurveField;
        CTool IPositionHost.Tool => Tool;
        CVehicle IPositionHost.Vehicle => Vehicle;
        CAHRS IPositionHost.Ahrs => Ahrs;
        CSection[] IPositionHost.Section => Sections;
        List<CPatches> IPositionHost.TriStrip => TriStripField;
        StringBuilder IPositionHost.SbGrid => sbGrid;

        vec3 IPositionHost.PivotAxlePos { get => pivotAxlePos; set => pivotAxlePos = value; }
        vec3 IPositionHost.SteerAxlePos { get => steerAxlePos; set => steerAxlePos = value; }
        vec3 IPositionHost.ToolPivotPos { get => toolPivotPos; set => toolPivotPos = value; }
        vec3 IPositionHost.TankPos { get => tankPos; set => tankPos = value; }
        vec3 IPositionHost.ToolPos { get => toolPos; set => toolPos = value; }
        vec2 IPositionHost.GuidanceLookPos { get => guidanceLookPos; set => guidanceLookPos = value; }
        vec2 IPositionHost.HitchPos { get => hitchPos; set => hitchPos = value; }
        vec2 IPositionHost.PrevBoundaryPos { get => prevBoundaryPos; set => prevBoundaryPos = value; }
        vec2 IPositionHost.PrevContourPos { get => prevContourPos; set => prevContourPos = value; }
        vec2 IPositionHost.PrevSectionPos { get => prevSectionPos; set => prevSectionPos = value; }
        vec2 IPositionHost.PrevGridPos { get => prevGridPos; set => prevGridPos = value; }
        vec2 IPositionHost.PrevFix { get => prevFix; set => prevFix = value; }
        vec2 IPositionHost.LastReverseFix { get => lastReverseFix; set => lastReverseFix = value; }

        btnStates IPositionHost.ManualBtnState => manualBtnState;
        btnStates IPositionHost.AutoBtnState => autoBtnState;

        double IPositionHost.AvgSpeed => avgSpeed;
        double IPositionHost.FixHeading { get => fixHeading; set => fixHeading = value; }
        double IPositionHost.GpsHz => gpsHz;
        double IPositionHost.GuidanceLookAheadTime => guidanceLookAheadTime;
        double IPositionHost.DistanceCurrentStepFix => distanceCurrentStepFix;
        double IPositionHost.SectionTriggerStepDistance { get => sectionTriggerStepDistance; set => sectionTriggerStepDistance = value; }
        double IPositionHost.SectionTriggerDistance { get => sectionTriggerDistance; set => sectionTriggerDistance = value; }
        double IPositionHost.ContourTriggerDistance { get => contourTriggerDistance; set => contourTriggerDistance = value; }
        double IPositionHost.GridTriggerDistance { get => gridTriggerDistance; set => gridTriggerDistance = value; }
        double IPositionHost.SinSectionHeading { get => sinSectionHeading; set => sinSectionHeading = value; }
        double IPositionHost.CosSectionHeading { get => cosSectionHeading; set => cosSectionHeading = value; }
        int IPositionHost.PatchCounter { get => patchCounter; set => patchCounter = value; }
        bool IPositionHost.IsPatchesChangingColor { get => isPatchesChangingColor; set => isPatchesChangingColor = value; }
        bool IPositionHost.IsLogElevation => false;

        int IPositionHost.StartCounter => startCounter;
        bool IPositionHost.IsFirstFixPositionSet { get => isFirstFixPositionSet; set => isFirstFixPositionSet = value; }
        bool IPositionHost.IsGPSPositionInitialized { get => isGPSPositionInitialized; set => isGPSPositionInitialized = value; }
        bool IPositionHost.IsJobStarted => IsJobStarted;

        bool IPositionHost.IsDayTime { set => isDayTime = value; }
        DateTime IPositionHost.Sunrise => DateTime.Today.AddHours(6);
        DateTime IPositionHost.Sunset => DateTime.Today.AddHours(20);

        // sin cámara: no hay nada que perspectivar en un proceso headless.
        void IPositionHost.SetZoom() { }

        bool IPositionHost.GlMatricesValid => false;
        int[] IPositionHost.GlViewport => Array.Empty<int>();
        double[] IPositionHost.GlProjection => Array.Empty<double>();
        double[] IPositionHost.GlModelView => Array.Empty<double>();
    }
}
