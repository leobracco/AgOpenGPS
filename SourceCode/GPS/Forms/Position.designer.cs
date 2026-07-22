//Please, if you use this, share the improvements

using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        //very first fix to setup grid etc
        public bool isFirstFixPositionSet = false, isGPSPositionInitialized = false, isFirstHeadingSet = false,
            isReverse = false, isSteerInReverse = true, isSuperSlow = false;
        public double startGPSHeading = 0;

        //string to record fixes for elevation maps
        public StringBuilder sbGrid = new StringBuilder();

        // autosteer variables for sending serial
        public short guidanceLineDistanceOff, guidanceLineSteerAngle;
        public double avGuidanceSteerAngle;

        public short errorAngVel;
        public double setAngVel, actAngVel;
        public bool isConstantContourOn;

        //guidance line look ahead
        public double guidanceLookAheadTime = 2;
        public vec2 guidanceLookPos = new vec2(0, 0);
        public double dualReverseDetectionDistance;

        //for heading or Atan2 as camera
        public string headingFromSource, headingFromSourceBak;

        public vec3 pivotAxlePos = new vec3(0, 0, 0);
        public vec3 steerAxlePos = new vec3(0, 0, 0);
        public vec3 toolPivotPos = new vec3(0, 0, 0);
        public vec3 toolPos = new vec3(0, 0, 0);
        public vec3 tankPos = new vec3(0, 0, 0);
        public vec2 hitchPos = new vec2(0, 0);

        //history
        public vec2 prevFix = new vec2(0, 0);
        public vec2 prevJumpFix = new vec2(0, 0);
        public vec2 prevDistFix = new vec2(0, 0);
        public vec2 lastReverseFix = new vec2(0, 0);

        //headings
        public double camHeading = 0.0, smoothCamHeading = 0, gpsHeading = 10.0, prevGPSHeading = 0.0;

        //storage for the cos and sin of heading
        public double cosSectionHeading = 1.0, sinSectionHeading = 0.0;

        //how far travelled since last section was added, section points
        double sectionTriggerDistance = 0, contourTriggerDistance = 0, sectionTriggerStepDistance = 0, gridTriggerDistance = 0;

        public vec2 prevSectionPos = new vec2(0, 0);
        public vec2 prevContourPos = new vec2(0, 0);
        public vec2 prevGridPos = new vec2(0, 0);
        public int patchCounter = 0;

        public vec2 prevBoundaryPos = new vec2(0, 0);

        //Everything is so wonky at the start
        int startCounter = 0;

        //individual points for the flags in a list
        public List<CFlag> flagPts = new List<CFlag>();

        //tally counters for display
        //public double totalSquareMetersWorked = 0, totalUserSquareMeters = 0, userSquareMetersAlarm = 0;

        public double avgSpeed, previousSpeed;//for average speed
        public int crossTrackError;

        //youturn
        public double distancePivotToTurnLine = -2222;
        public double distanceToolToTurnLine = -2222;

        //the value to fill in you turn progress bar
        public int youTurnProgressBar = 0;

        //IMU 
        public double rollCorrectionDistance = 0;
        public double imuGPS_Offset, imuCorrected;

        //step position - slow speed spinner killer
        private int currentStepFix = 0;
        private const int totalFixSteps = 10;
        public vecFix2Fix[] stepFixPts = new vecFix2Fix[totalFixSteps];
        public double distanceCurrentStepFix = 0, distanceCurrentStepFixDisplay = 0, minHeadingStepDist = 1, startSpeed = 0.5;
        public double fixToFixHeadingDistance = 0, gpsMinimumStepDistance = 0.05;
        private bool hasBeenFirstHeadingSet = false;

        public bool isChangingDirection, isReverseWithIMU;

        private double nowHz = 0, filteredDelta = 0, delta = 0;

        public bool isRTK_AlarmOn, isRTK_KillAutosteer;
        private DateTime RTKBackSinceUtc = DateTime.MinValue;
        private const int RTK_RECOVER_DEBOUNCE_MS = 1000;

        public double headlandDistanceDelta = 0, boundaryDistanceDelta = 0;

        public vec2 lastGPS = new vec2(0, 0);

        public double uncorrectedEastingGraph = 0;
        public double correctionDistanceGraph = 0;

        double frameTimeRough = 3;
        public double timeSliceOfLastFix = 0;

        public bool isMaxAngularVelocity = false;

        public int minSteerSpeedTimer = 0;

        //public vec2 jumpFix = new vec2(0, 0);
        //public double jumpDistance = 0, jumpDistanceMax;
        //public double jumpDistanceAlarm = 20;
        //public int jumpCounter = 0;

        public double camSmoothFactor = ((double)(Properties.Settings.Default.setDisplay_camSmooth) * 0.004) + 0.2;

        //agrega punto de lote/contorno/sección e inicializa primeras posiciones GPS (Core, host invertido)
        public CPositionUpdater positionUpdater;

        //PGN de posición corregida + PGN 254 de autosteer (Core, host invertido)
        public CAutoSteerUpdater autoSteerUpdater;

        //stop crítico por boundary + creación/disparo del youturn (Core, host invertido)
        public CYouTurnUpdater youTurnUpdater;

        //switch de heading Fix/VTG/Dual (Core, host invertido)
        public CHeadingUpdater headingUpdater;

        public void UpdateFixPosition()
        {
            _updateFixTimer?.Start();
            //Measure the frequency of the GPS updates
            timeSliceOfLastFix = (double)(swFrame.ElapsedTicks) / (double)System.Diagnostics.Stopwatch.Frequency;

            swFrame.Reset();
            swFrame.Start();

            //get Hz from timeslice
            nowHz = 1 / timeSliceOfLastFix;
            if (nowHz > 70) nowHz = 70;
            if (nowHz < 3) nowHz = 3;

            //simple comp filter
            gpsHz = 0.98 * gpsHz + 0.02 * nowHz;

            //Initialization counter
            startCounter++;

            if (!isGPSPositionInitialized)
            {
                InitializeFirstFewGPSPositions();
                return;
            }
            // Detect re-initialization of heading
            if (!isFirstHeadingSet && hasBeenFirstHeadingSet)
            {
                for (int i = 0; i < totalFixSteps; i++)
                {
                    stepFixPts[i].isSet = 0;
                    stepFixPts[i].easting = 0;
                    stepFixPts[i].northing = 0;
                    stepFixPts[i].distance = 0;
                }

                prevFix = pn.fix;
                prevDistFix = pn.fix;
                gpsHeading = 0;
                fixHeading = 0;
                imuGPS_Offset = 0;
                hasBeenFirstHeadingSet = false;
            }

            pn.speed = pn.vtgSpeed;
            pn.AverageTheSpeed();

            #region Heading
            //Core: CHeadingUpdater (traspaso portabilidad 2026-07-20)
            headingUpdater.UpdateHeading();
            #endregion

            #region Corrected Position
            //Core: CAutoSteerUpdater (traspaso portabilidad 2026-07-20)
            autoSteerUpdater.SendCorrectedPositionPgn();
            #endregion

            #region AutoSteer
            //Core: CAutoSteerUpdater (traspaso portabilidad 2026-07-20)
            autoSteerUpdater.BuildAndSendAutoSteerPgn();
            #endregion

            #region Youturn
            //Core: CYouTurnUpdater (traspaso portabilidad 2026-07-20)
            youTurnUpdater.UpdateYouTurnState();
            #endregion

            if (isJobStarted)
            {
                oglBack.Refresh();

                p_239.pgn[p_239.geoStop] = mc.isOutOfBounds ? (byte)1 : (byte)0;

                SendPgnToLoop(p_239.pgn);

                SendPgnToLoop(p_229.pgn);
            }

            //stop the timer and calc how long it took to do calcs and draw
            frameTimeRough = (double)(swFrame.ElapsedTicks * 1000) / (double)System.Diagnostics.Stopwatch.Frequency;

            if (frameTimeRough > 80) frameTimeRough = 80;
            frameTime = frameTime * 0.90 + frameTimeRough * 0.1;
            _updateFixTimer?.Stop();

            //update main window
            oglMain.MakeCurrent();
            oglMain.Refresh();
        }

        //Core: CPositionUpdater (traspaso portabilidad 2026-07-19)
        private void TheRest() => positionUpdater.TheRest();

        //all the hitch, pivot, section, trailing hitch, headings and fixes
        //all the hitch, pivot, section, trailing hitch, headings and fixes — Core: CPositionUpdater (traspaso portabilidad 2026-07-19)
        private void CalculatePositionHeading() => positionUpdater.CalculatePositionHeading();

        //calculate the extreme tool left, right velocities, each section lookahead, and whether or not its going backwards — Core: CPositionUpdater (traspaso portabilidad 2026-07-19)
        public void CalculateSectionLookAhead(double northing, double easting, double cosHeading, double sinHeading)
            => positionUpdater.CalculateSectionLookAhead(northing, easting, cosHeading, sinHeading);

        //perimeter and boundary point generation — Core: CPositionUpdater (traspaso portabilidad 2026-07-19)
        public void AddBoundaryPoint() => positionUpdater.AddBoundaryPoint();

        //Core: CPositionUpdater (traspaso portabilidad 2026-07-19)
        private void AddContourPoints() => positionUpdater.AddContourPoints();

        //add the points for section, contour line points, Area Calc feature — Core: CPositionUpdater (traspaso portabilidad 2026-07-19)
        private void AddSectionOrPathPoints() => positionUpdater.AddSectionOrPathPoints();

        //the start of first few frames to initialize entire program — Core: CPositionUpdater (traspaso portabilidad 2026-07-19)
        private void InitializeFirstFewGPSPositions() => positionUpdater.InitializeFirstFewGPSPositions();
    }//end class
}//end namespace