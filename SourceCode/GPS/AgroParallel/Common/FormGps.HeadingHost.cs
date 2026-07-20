// ============================================================================
// FormGps.HeadingHost.cs
// Implementación de IHeadingHost sobre FormGPS. CHeadingUpdater (switch de
// heading Fix/VTG/Dual, extraído de UpdateFixPosition en
// Position.designer.cs) vive en AgOpenGPS.Core y consume el host a través de
// esta interfaz (inversión de dependencia — traspaso de portabilidad bloque
// 9, 2026-07-20).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : IHeadingHost
    {
        string IHeadingHost.HeadingFromSource { get => headingFromSource; set => headingFromSource = value; }

        double IHeadingHost.FixHeading { get => fixHeading; set => fixHeading = value; }
        double IHeadingHost.CamHeading { get => camHeading; set => camHeading = value; }
        double IHeadingHost.SmoothCamHeading { get => smoothCamHeading; set => smoothCamHeading = value; }

        vec2 IHeadingHost.PrevFix { get => prevFix; set => prevFix = value; }
        vec2 IHeadingHost.PrevDistFix { get => prevDistFix; set => prevDistFix = value; }
        vec2 IHeadingHost.LastReverseFix { get => lastReverseFix; set => lastReverseFix = value; }
        vec2 IHeadingHost.LastGps { get => lastGPS; set => lastGPS = value; }

        vecFix2Fix[] IHeadingHost.StepFixPts => stepFixPts;
        int IHeadingHost.CurrentStepFix { get => currentStepFix; set => currentStepFix = value; }

        double IHeadingHost.DistanceCurrentStepFix { get => distanceCurrentStepFix; set => distanceCurrentStepFix = value; }
        double IHeadingHost.DistanceCurrentStepFixDisplay { get => distanceCurrentStepFixDisplay; set => distanceCurrentStepFixDisplay = value; }
        double IHeadingHost.FixToFixHeadingDistance { get => fixToFixHeadingDistance; set => fixToFixHeadingDistance = value; }
        double IHeadingHost.MinHeadingStepDist => minHeadingStepDist;
        double IHeadingHost.GpsMinimumStepDistance => gpsMinimumStepDistance;

        bool IHeadingHost.IsFirstHeadingSet { get => isFirstHeadingSet; set => isFirstHeadingSet = value; }
        bool IHeadingHost.HasBeenFirstHeadingSet { get => hasBeenFirstHeadingSet; set => hasBeenFirstHeadingSet = value; }
        bool IHeadingHost.IsReverseWithIMU { get => isReverseWithIMU; set => isReverseWithIMU = value; }

        double IHeadingHost.Delta { get => delta; set => delta = value; }
        double IHeadingHost.FilteredDelta { get => filteredDelta; set => filteredDelta = value; }

        double IHeadingHost.ImuGpsOffset { get => imuGPS_Offset; set => imuGPS_Offset = value; }
        double IHeadingHost.ImuCorrected { get => imuCorrected; set => imuCorrected = value; }

        double IHeadingHost.RollCorrectionDistance { get => rollCorrectionDistance; set => rollCorrectionDistance = value; }
        double IHeadingHost.CorrectionDistanceGraph { get => correctionDistanceGraph; set => correctionDistanceGraph = value; }
        double IHeadingHost.UncorrectedEastingGraph { get => uncorrectedEastingGraph; set => uncorrectedEastingGraph = value; }

        double IHeadingHost.CamSmoothFactor => camSmoothFactor;
        double IHeadingHost.DualReverseDetectionDistance => dualReverseDetectionDistance;

        void IHeadingHost.SetSpeedLabelColor(bool isRed) => lblSpeed.ForeColor = isRed ? System.Drawing.Color.Red : System.Drawing.Color.Green;

        void IHeadingHost.TheRest() => TheRest();
    }
}
