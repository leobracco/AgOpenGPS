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

        /// <summary>UTC del ultimo fix GPS procesado (UpdateFixPosition).
        /// default = nunca llego un fix.</summary>
        public DateTime lastFixUtc;
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

        // ---- Hz del GPS medido de verdad (port de Position.designer.cs:104,
        // 117-118 y 131-145 del 6.8.6). Antes gpsHz quedaba clavado en 10 y
        // nadie lo escribía: los timers de sección (SectionsRuntime) y las
        // velocidades de extremo de herramienta (CalculateSectionLookAhead)
        // asumían 10 Hz aunque el receptor mandara 5 u 8. ----
        public double gpsHz = 10;
        /// <summary>Segundos entre el fix anterior y este (swFrame).</summary>
        public double timeSliceOfLastFix = 0;
        private double nowHz = 0;
        // Mismo Stopwatch que FormGPS.cs:110: nace SIN arrancar — el primer fix
        // mide 0 ticks → nowHz infinito → lo acota el clamp de 70 del filtro.
        private readonly System.Diagnostics.Stopwatch swFrame = new System.Diagnostics.Stopwatch();

        public double guidanceLookAheadTime = 2;

        // ---- Alarma RTK + kill del piloto (port de Position.designer.cs:106-108
        // y OpenGL.Designer.cs:505-561 del 6.8.6). Los dos settings
        // (setGPS_isRTK / setGPS_isRTK_KillAutoSteer) vienen APAGADOS por
        // defecto en ambos lados: el bloque no hace nada hasta que el operario
        // prende la alarma en Configuración › Rumbo. ----
        public bool isRTK_AlarmOn, isRTK_KillAutosteer;
        private DateTime RTKBackSinceUtc = DateTime.MinValue;
        private const int RTK_RECOVER_DEBOUNCE_MS = 1000;
        // Equivalentes headless de sounds.isRTKAlarming / sounds.RTKWasAlarming
        // (acá no hay CSound: el "sonido" es el log y el aviso al HUD).
        private bool isRTKAlarming, rtkWasAlarming;

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

        // Mismos defaults que GUI.Designer.cs (FormGPS) — usados por
        // GuidanceEngineStateProvider (PilotX.Android). Sin UI de bandera/nudge
        // headless todavia, pero se dejan disponibles para no hardcodear
        // sentinels en el DTO.
        public byte flagColor;
        public bool isNudgeOn = true;
    }
}
