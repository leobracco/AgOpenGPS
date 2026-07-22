// ============================================================================
// GuidanceEngineHost.Heading.cs — implementación de IAutoSteerHost e
// IHeadingHost. Mismo patrón que FormGps.AutoSteerHost.cs/FormGps.HeadingHost.cs.
// Los 3 métodos de UI real (SendPgnToLoop aparte, ya implementado en el
// archivo principal) se resuelven: SendPgnToLoop -> socket propio;
// PerformAutoSteerClick -> toggle directo (sin UI de botón detrás, ver nota);
// TimedMessageBox/SetSpeedLabelColor -> log, no hay pantalla que avisar.
// ============================================================================

using AgLibrary.Logging;
using AgOpenGPS.Core;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost : IAutoSteerHost, IHeadingHost
    {
        ApplicationModel IAutoSteerHost.AppModel => AppModelField;
        CNMEA IAutoSteerHost.Pn => Pn;
        CFieldData IAutoSteerHost.Fd => Fd;
        CContour IAutoSteerHost.Ct => Ct;
        CTrack IAutoSteerHost.Trk => Trk;
        CABLine IAutoSteerHost.ABLine => ABLineField;
        CABCurve IAutoSteerHost.Curve => CurveField;
        CRecordedPath IAutoSteerHost.RecPath => RecPath;
        CVehicle IAutoSteerHost.Vehicle => Vehicle;
        CAHRS IAutoSteerHost.Ahrs => Ahrs;
        CModuleComm IAutoSteerHost.Mc => Mc;
        CISOBUS IAutoSteerHost.Isobus => Isobus;
        CPGN_FE IAutoSteerHost.P254 => P254Field;

        vec3 IAutoSteerHost.PivotAxlePos => pivotAxlePos;
        vec3 IAutoSteerHost.SteerAxlePos => steerAxlePos;

        double IAutoSteerHost.GpsHeading { get => gpsHeading; set => gpsHeading = value; }
        double IAutoSteerHost.AvgSpeed => avgSpeed;
        double IAutoSteerHost.LightbarDistance { get => lightbarDistance; set => lightbarDistance = value; }
        short IAutoSteerHost.GuidanceLineDistanceOff { get => guidanceLineDistanceOff; set => guidanceLineDistanceOff = value; }
        short IAutoSteerHost.GuidanceLineSteerAngle { get => guidanceLineSteerAngle; set => guidanceLineSteerAngle = value; }
        int IAutoSteerHost.CrossTrackError { get => crossTrackError; set => crossTrackError = value; }
        int IAutoSteerHost.MinSteerSpeedTimer { get => minSteerSpeedTimer; set => minSteerSpeedTimer = value; }

        bool IAutoSteerHost.IsBtnAutoSteerOn => isBtnAutoSteerOn;
        bool IAutoSteerHost.IsReverse { get => isReverse; set => isReverse = value; }
        bool IAutoSteerHost.IsChangingDirection { get => isChangingDirection; set => isChangingDirection = value; }
        bool IAutoSteerHost.IsSteerInReverse => isSteerInReverse;
        bool IAutoSteerHost.IsMetric => isMetric;
        bool IAutoSteerHost.IsSimTimerEnabled => isSimTimerEnabled;

        // No hay botón físico detrás: alterna el estado directo. La lógica de
        // negocio real (btnAutoSteer_Click en FormGPS.cs) además toca color
        // de botón/sonido — irrelevante sin UI. Si a futuro este engine
        // corre standalone en producción, acá es donde entraría el comando
        // remoto real (MQTT/REST) para engatillar autosteer.
        void IAutoSteerHost.PerformAutoSteerClick()
        {
            isBtnAutoSteerOn = !isBtnAutoSteerOn;
            Log.EventWriter("GuidanceEngine: autosteer " + (isBtnAutoSteerOn ? "ON" : "OFF") + " (auto, sin UI)");
        }

        void IAutoSteerHost.TimedMessageBox(int timeout, string title, string message)
            => Log.EventWriter($"GuidanceEngine [{title}] {message}");

        // ---- IHeadingHost ----
        string IHeadingHost.HeadingFromSource { get => headingFromSource; set => headingFromSource = value; }
        double IHeadingHost.FixHeading { get => fixHeading; set => fixHeading = value; }
        double IHeadingHost.CamHeading { get => camHeading; set => camHeading = value; }
        double IHeadingHost.SmoothCamHeading { get => smoothCamHeading; set => smoothCamHeading = value; }
        vec2 IHeadingHost.PrevFix { get => prevFix; set => prevFix = value; }
        vec2 IHeadingHost.PrevDistFix { get => prevDistFix; set => prevDistFix = value; }
        vec2 IHeadingHost.LastReverseFix { get => lastReverseFix; set => lastReverseFix = value; }
        vec2 IHeadingHost.LastGps { get => lastGps; set => lastGps = value; }
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

        void IHeadingHost.SetSpeedLabelColor(bool isRed) { /* sin pantalla */ }

        void IHeadingHost.TheRest() => PositionUpdater.TheRest();
    }
}
