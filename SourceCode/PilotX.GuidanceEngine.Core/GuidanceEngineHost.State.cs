// ============================================================================
// GuidanceEngineHost.State.cs — campos escalares equivalentes a los que
// hoy viven sueltos en Position.designer.cs / FormGPS.cs (WinForms). Mismos
// nombres/semántica, sin nada de UI.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using AgOpenGPS.Core;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        public bool isFirstFixPositionSet;
        public bool isGPSPositionInitialized;
        public bool isFirstHeadingSet;
        private bool hasBeenFirstHeadingSet;
        public bool isReverse;
        public bool isSteerInReverse = true;
        public bool isChangingDirection;
        public bool isReverseWithIMU;

        public vec3 pivotAxlePos = new vec3(0, 0, 0);
        public vec3 steerAxlePos = new vec3(0, 0, 0);
        public vec3 toolPivotPos = new vec3(0, 0, 0);
        public vec3 toolPos = new vec3(0, 0, 0);
        public vec3 tankPos = new vec3(0, 0, 0);
        public vec2 hitchPos = new vec2(0, 0);

        public vec2 prevFix = new vec2(0, 0);
        public vec2 prevDistFix = new vec2(0, 0);
        public vec2 lastReverseFix = new vec2(0, 0);
        public vec2 lastGps = new vec2(0, 0);
        public vec2 guidanceLookPos = new vec2(0, 0);
        public vec2 prevBoundaryPos = new vec2(0, 0);
        public vec2 prevContourPos = new vec2(0, 0);
        public vec2 prevSectionPos = new vec2(0, 0);
        public vec2 prevGridPos = new vec2(0, 0);

        public string headingFromSource;
        public double fixHeading;
        public double gpsHeading = 10.0;
        public double camHeading;
        public double smoothCamHeading;

        public double avgSpeed;
        public double previousSpeed;
        public int crossTrackError;

        private const int totalFixSteps = 10;
        public vecFix2Fix[] stepFixPts = new vecFix2Fix[totalFixSteps];
        private int currentStepFix;
        public double distanceCurrentStepFix;
        public double distanceCurrentStepFixDisplay;
        public double fixToFixHeadingDistance;
        public double minHeadingStepDist = 1;
        public double gpsMinimumStepDistance = 0.05;

        private double delta;
        private double filteredDelta;
        public double imuGPS_Offset;
        public double imuCorrected;
        public double rollCorrectionDistance;
        public double correctionDistanceGraph;
        public double uncorrectedEastingGraph;
        public double camSmoothFactor = 0.2;
        public double dualReverseDetectionDistance = 0.1;

        public double distancePivotToTurnLine = -2222;
        public double distanceToolToTurnLine = -2222;
        public int makeUTurnCounter;

        public double sectionTriggerDistance;
        public double contourTriggerDistance;
        public double sectionTriggerStepDistance;
        public double gridTriggerDistance;
        public double sinSectionHeading = 0.0;
        public double cosSectionHeading = 1.0;
        public int patchCounter;
        public bool isPatchesChangingColor;
        public int startCounter;
        public double gpsHz = 10;
        public double guidanceLookAheadTime = 2;

        public bool isBtnAutoSteerOn;
        public int minSteerSpeedTimer;
        public double lightbarDistance;
        public short guidanceLineDistanceOff;
        public short guidanceLineSteerAngle;

        public btnStates manualBtnState = btnStates.Off;
        public btnStates autoBtnState = btnStates.Off;

        // isJobStarted: en FormGPS depende de AppModel.Fields.ActiveField —
        // acá mismo criterio (deja abrir/cerrar lote real vía AppModelField).
        public bool IsJobStarted => AppModelField.Fields.ActiveField != null;

        public bool isDayTime = true;
        public bool isMetric = true;
        public bool isStanleyUsed = true;
        public bool isSideGuideLines;
        public bool isSvennArrowOn;
        public double secondsSinceStart;

        public bool isSimTimerEnabled;

        public double toolWidth => Tool.width;

        public string currentFieldDirectory = "";
        public StringBuilder sbGrid = new StringBuilder();
    }
}
