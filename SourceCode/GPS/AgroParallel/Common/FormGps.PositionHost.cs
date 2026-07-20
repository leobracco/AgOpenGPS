// ============================================================================
// FormGps.PositionHost.cs
// Implementación de IPositionHost sobre FormGPS. CPositionUpdater (los 4
// pasos hoja del loop de posición, extraídos de Position.designer.cs) vive
// en AgOpenGPS.Core y consume el host a través de esta interfaz (inversión
// de dependencia — traspaso de portabilidad 2026-07-19).
// ============================================================================

using AgOpenGPS.Core;
using AgOpenGPS.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace AgOpenGPS
{
    public partial class FormGPS : IPositionHost
    {
        ApplicationModel IPositionHost.AppModel => AppModel;
        CNMEA IPositionHost.Pn => pn;
        CBoundary IPositionHost.Bnd => bnd;
        CContour IPositionHost.Ct => ct;
        CRecordedPath IPositionHost.RecPath => recPath;
        CABCurve IPositionHost.Curve => curve;
        CTool IPositionHost.Tool => tool;
        CVehicle IPositionHost.Vehicle => vehicle;
        CAHRS IPositionHost.Ahrs => ahrs;
        CSection[] IPositionHost.Section => section;
        List<CPatches> IPositionHost.TriStrip => triStrip;
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
        bool IPositionHost.IsLogElevation => isLogElevation;

        int IPositionHost.StartCounter => startCounter;
        bool IPositionHost.IsFirstFixPositionSet { get => isFirstFixPositionSet; set => isFirstFixPositionSet = value; }
        bool IPositionHost.IsGPSPositionInitialized { get => isGPSPositionInitialized; set => isGPSPositionInitialized = value; }
        bool IPositionHost.IsJobStarted => isJobStarted;

        bool IPositionHost.IsDayTime { set => isDayTime = value; }
        DateTime IPositionHost.Sunrise => sunrise;
        DateTime IPositionHost.Sunset => sunset;

        void IPositionHost.SetZoom() => SetZoom();

        bool IPositionHost.GlMatricesValid => _glMatricesValid;
        int[] IPositionHost.GlViewport => _glViewport;
        double[] IPositionHost.GlProjection => _glProjection;
        double[] IPositionHost.GlModelView => _glModelView;
    }
}
