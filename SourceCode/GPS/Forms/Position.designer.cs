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

            if (Properties.Settings.Default.setGPS_headingFromWhichSource == "Dual" && ahrs.autoSwitchDualFixOn)
            {
                if (Math.Abs(pn.speed) > ahrs.autoSwitchDualFixSpeed)
                {
                    headingFromSource = "Fix";
                    ahrs.isDualAsIMU = true;
                }
                else
                {
                    headingFromSource = "Dual";
                    ahrs.isDualAsIMU = false;
                    ahrs.imuHeading = 99999;
                }
            }

            #region Heading
            switch (headingFromSource)
            {
                //calculate current heading only when moving, otherwise use last
                case "Fix":
                    {
                        #region Start

                        if (Properties.Settings.Default.setGPS_headingFromWhichSource == "Dual" && ahrs.autoSwitchDualFixOn)
                        {
                            lblSpeed.ForeColor = System.Drawing.Color.Red;
                        }

                        distanceCurrentStepFixDisplay = glm.Distance(prevDistFix, pn.fix);
                        distanceCurrentStepFixDisplay *= 100;
                        prevDistFix = pn.fix;

                        if (Math.Abs(avgSpeed) < 1.5 && !isFirstHeadingSet)
                            goto byPass;

                        if (!isFirstHeadingSet) //set in steer settings, Stanley
                        {
                            prevFix.easting = stepFixPts[0].easting; prevFix.northing = stepFixPts[0].northing;

                            if (stepFixPts[2].isSet == 0)
                            {
                                //this is the first position no roll or offset correction
                                if (stepFixPts[0].isSet == 0)
                                {
                                    stepFixPts[0].easting = pn.fix.easting;
                                    stepFixPts[0].northing = pn.fix.northing;
                                    stepFixPts[0].isSet = 1;
                                    return;
                                }

                                //and the second
                                if (stepFixPts[1].isSet == 0)
                                {
                                    for (int i = totalFixSteps - 1; i > 0; i--) stepFixPts[i] = stepFixPts[i - 1];
                                    stepFixPts[0].easting = pn.fix.easting;
                                    stepFixPts[0].northing = pn.fix.northing;
                                    stepFixPts[0].isSet = 1;
                                    return;
                                }

                                //the critcal moment for checking initial direction/heading.
                                for (int i = totalFixSteps - 1; i > 0; i--) stepFixPts[i] = stepFixPts[i - 1];
                                stepFixPts[0].easting = pn.fix.easting;
                                stepFixPts[0].northing = pn.fix.northing;
                                stepFixPts[0].isSet = 1;

                                gpsHeading = Math.Atan2(pn.fix.easting - stepFixPts[2].easting,
                                    pn.fix.northing - stepFixPts[2].northing);

                                if (gpsHeading < 0) gpsHeading += glm.twoPI;
                                else if (gpsHeading >= glm.twoPI) gpsHeading -= glm.twoPI;

                                fixHeading = gpsHeading;

                                //set the imu to gps heading offset
                                if (ahrs.imuHeading != 99999)
                                {
                                    double imuHeading = (glm.toRadians(ahrs.imuHeading));
                                    imuGPS_Offset = 0;

                                    //Difference between the IMU heading and the GPS heading
                                    double gyroDelta = (imuHeading + imuGPS_Offset) - gpsHeading;

                                    if (gyroDelta < 0) gyroDelta += glm.twoPI;
                                    else if (gyroDelta > glm.twoPI) gyroDelta -= glm.twoPI;

                                    //calculate delta based on circular data problem 0 to 360 to 0, clamp to +- 2 Pi
                                    if (gyroDelta >= -glm.PIBy2 && gyroDelta <= glm.PIBy2) gyroDelta *= -1.0;
                                    else
                                    {
                                        if (gyroDelta > glm.PIBy2) { gyroDelta = glm.twoPI - gyroDelta; }
                                        else { gyroDelta = (glm.twoPI + gyroDelta) * -1.0; }
                                    }
                                    if (gyroDelta > glm.twoPI) gyroDelta -= glm.twoPI;
                                    else if (gyroDelta < -glm.twoPI) gyroDelta += glm.twoPI;

                                    //moe the offset to line up imu with gps
                                    imuGPS_Offset = (gyroDelta);
                                    imuGPS_Offset = Math.Round(imuGPS_Offset, 6);

                                    if (imuGPS_Offset >= glm.twoPI) imuGPS_Offset -= glm.twoPI;
                                    else if (imuGPS_Offset <= 0) imuGPS_Offset += glm.twoPI;

                                    //determine the Corrected heading based on gyro and GPS
                                    imuCorrected = imuHeading + imuGPS_Offset;
                                    if (imuCorrected >= glm.twoPI) imuCorrected -= glm.twoPI;
                                    else if (imuCorrected < 0) imuCorrected += glm.twoPI;

                                    fixHeading = imuCorrected;
                                }

                                //set the camera 
                                camHeading = glm.toDegrees(gpsHeading);

                                //now we have a heading, fix the first 3
                                if (vehicle.VehicleConfig.AntennaOffset != 0)
                                {
                                    for (int i = 0; i < 3; i++)
                                    {
                                        stepFixPts[i].easting = (Math.Cos(-gpsHeading) * vehicle.VehicleConfig.AntennaOffset) + stepFixPts[i].easting;
                                        stepFixPts[i].northing = (Math.Sin(-gpsHeading) * vehicle.VehicleConfig.AntennaOffset) + stepFixPts[i].northing;
                                    }
                                }

                                if (ahrs.imuRoll != 88888)
                                {
                                    //change for roll to the right is positive times -1
                                    rollCorrectionDistance = Math.Tan(glm.toRadians((ahrs.imuRoll))) * -vehicle.VehicleConfig.AntennaHeight;

                                    // roll to left is positive  **** important!!
                                    // not any more - April 30, 2019 - roll to right is positive Now! Still Important
                                    for (int i = 0; i < 3; i++)
                                    {
                                        stepFixPts[i].easting = (Math.Cos(-gpsHeading) * rollCorrectionDistance) + stepFixPts[i].easting;
                                        stepFixPts[i].northing = (Math.Sin(-gpsHeading) * rollCorrectionDistance) + stepFixPts[i].northing;
                                    }
                                }

                                //get the distance from first to 2nd point, update fix with new offset/roll
                                stepFixPts[0].distance = glm.Distance(stepFixPts[1], stepFixPts[0]);
                                pn.fix.easting = stepFixPts[0].easting;
                                pn.fix.northing = stepFixPts[0].northing;

                                isFirstHeadingSet = true;
                                hasBeenFirstHeadingSet = true;
                                TimedMessageBox(2000, "Direction Reset", "Forward is Set");
                                Log.EventWriter("Forward Is Set");

                                lastGPS = pn.fix;

                                return;
                            }
                        }
                        #endregion

                        #region Offset Roll
                        if (vehicle.VehicleConfig.AntennaOffset != 0)
                        {
                            pn.fix.easting = (Math.Cos(-gpsHeading) * vehicle.VehicleConfig.AntennaOffset) + pn.fix.easting;
                            pn.fix.northing = (Math.Sin(-gpsHeading) * vehicle.VehicleConfig.AntennaOffset) + pn.fix.northing;
                        }

                        uncorrectedEastingGraph = pn.fix.easting;

                        //originalEasting = pn.fix.easting;
                        if (ahrs.imuRoll != 88888)
                        {
                            //change for roll to the right is positive times -1
                            rollCorrectionDistance = Math.Sin(glm.toRadians((ahrs.imuRoll))) * -vehicle.VehicleConfig.AntennaHeight;
                            correctionDistanceGraph = rollCorrectionDistance;

                            pn.fix.easting = (Math.Cos(-gpsHeading) * rollCorrectionDistance) + pn.fix.easting;
                            pn.fix.northing = (Math.Sin(-gpsHeading) * rollCorrectionDistance) + pn.fix.northing;
                        }

                        #endregion

                        #region Fix Heading

                        //how far since last fix
                        distanceCurrentStepFix = glm.Distance(stepFixPts[0], pn.fix);

                        if (distanceCurrentStepFix < gpsMinimumStepDistance)
                        {
                            goto byPass;
                        }
                        
                        if ((fd.distanceUser += distanceCurrentStepFix) > 9999) fd.distanceUser = 0;

                        double minFixHeadingDistSquared = minHeadingStepDist * minHeadingStepDist;
                        fixToFixHeadingDistance = 0;

                        for (int i = 0; i < totalFixSteps; i++)
                        {
                            fixToFixHeadingDistance = glm.DistanceSquared(stepFixPts[i], pn.fix);
                            currentStepFix = i;

                            if (fixToFixHeadingDistance > minFixHeadingDistSquared)
                            {
                                break;
                            }
                        }

                        if (fixToFixHeadingDistance < minFixHeadingDistSquared * 0.5)
                            goto byPass;

                        double newGPSHeading = Math.Atan2(pn.fix.easting - stepFixPts[currentStepFix].easting,
                                                pn.fix.northing - stepFixPts[currentStepFix].northing);
                        if (newGPSHeading < 0) newGPSHeading += glm.twoPI;

                        //imu on board
                        if (ahrs.imuHeading != 99999)
                        {
                            isChangingDirection = false;

                            if (ahrs.isReverseOn)
                            {
                                ////what is angle between the last valid heading before stopping and one just now
                                delta = Math.Abs(Math.PI - Math.Abs(Math.Abs(newGPSHeading - imuCorrected) - Math.PI));

                                //ie change in direction
                                if (delta > 1.57) //
                                {
                                    isReverse = true;
                                    newGPSHeading += Math.PI;
                                    if (newGPSHeading < 0) newGPSHeading += glm.twoPI;
                                    else if (newGPSHeading >= glm.twoPI) newGPSHeading -= glm.twoPI;
                                    isReverseWithIMU = true;
                                }
                                else
                                {
                                    isReverse = false;
                                    isReverseWithIMU = false;
                                }
                            }
                            else
                            {
                                isReverse = false;
                            }

                            if (isReverse)
                                newGPSHeading -= glm.toRadians(vehicle.VehicleConfig.AntennaPivot / 1
                                    * mc.actualSteerAngleDegrees * ahrs.reverseComp);
                            else
                                newGPSHeading -= glm.toRadians(vehicle.VehicleConfig.AntennaPivot / 1
                                    * mc.actualSteerAngleDegrees * ahrs.forwardComp);

                            if (newGPSHeading < 0) newGPSHeading += glm.twoPI;
                            else if (newGPSHeading >= glm.twoPI) newGPSHeading -= glm.twoPI;

                            gpsHeading = newGPSHeading;

                            #region IMU Fusion

                            // IMU Fusion with heading correction, add the correction
                            //current gyro angle in radians
                            double imuHeading = (glm.toRadians(ahrs.imuHeading));

                            //Difference between the IMU heading and the GPS heading
                            double gyroDelta = 0;

                            //if (!isReverseWithIMU)
                            gyroDelta = (imuHeading + imuGPS_Offset) - gpsHeading;

                            if (gyroDelta < 0) gyroDelta += glm.twoPI;
                            else if (gyroDelta >= glm.twoPI) gyroDelta -= glm.twoPI;

                            //calculate delta based on circular data problem 0 to 360 to 0, clamp to +- 2 Pi
                            if (gyroDelta >= -glm.PIBy2 && gyroDelta <= glm.PIBy2) gyroDelta *= -1.0;
                            else
                            {
                                if (gyroDelta > glm.PIBy2) { gyroDelta = glm.twoPI - gyroDelta; }
                                else { gyroDelta = (glm.twoPI + gyroDelta) * -1.0; }
                            }
                            if (gyroDelta > glm.twoPI) gyroDelta -= glm.twoPI;
                            else if (gyroDelta < -glm.twoPI) gyroDelta += glm.twoPI;

                            //moe the offset to line up imu with gps
                            if (!isReverseWithIMU)
                                imuGPS_Offset += (gyroDelta * (ahrs.fusionWeight));
                            else
                                imuGPS_Offset += (gyroDelta * (0.02));

                            if (imuGPS_Offset > glm.twoPI) imuGPS_Offset -= glm.twoPI;
                            else if (imuGPS_Offset < 0) imuGPS_Offset += glm.twoPI;

                            //determine the Corrected heading based on gyro and GPS
                            imuCorrected = imuHeading + imuGPS_Offset;
                            if (imuCorrected >= glm.twoPI) imuCorrected -= glm.twoPI;
                            else if (imuCorrected < 0) imuCorrected += glm.twoPI;

                            //use imu as heading when going slow
                            fixHeading = imuCorrected;

                            #endregion
                        }
                        else
                        {
                            if (ahrs.isReverseOn)
                            {
                                ////what is angle between the last valid heading before stopping and one just now
                                delta = Math.Abs(Math.PI - Math.Abs(Math.Abs(newGPSHeading - gpsHeading) - Math.PI));

                                filteredDelta = delta * 0.2 + filteredDelta * 0.8;

                                //filtered delta different then delta
                                if (Math.Abs(filteredDelta - delta) > 0.5)
                                {
                                    isChangingDirection = true;
                                }
                                else
                                {
                                    isChangingDirection = false;
                                }

                                //we can't be sure if changing direction so do nothing
                                if (isChangingDirection)
                                    goto byPass;

                                //ie change in direction
                                if (filteredDelta > 1.57) //
                                {
                                    isReverse = true;
                                    newGPSHeading += Math.PI;
                                    if (newGPSHeading < 0) newGPSHeading += glm.twoPI;
                                    else if (newGPSHeading >= glm.twoPI) newGPSHeading -= glm.twoPI;
                                }
                                else
                                    isReverse = false;

                                if (isReverse)
                                    newGPSHeading -= glm.toRadians(vehicle.VehicleConfig.AntennaPivot / 1 * mc.actualSteerAngleDegrees * ahrs.reverseComp);
                                else
                                    newGPSHeading -= glm.toRadians(vehicle.VehicleConfig.AntennaPivot / 1 * mc.actualSteerAngleDegrees * ahrs.forwardComp);

                                if (newGPSHeading < 0) newGPSHeading += glm.twoPI;
                                else if (newGPSHeading >= glm.twoPI) newGPSHeading -= glm.twoPI;
                            }
                            else
                            {
                                isReverse = false;
                                isChangingDirection = false;
                            }

                            //set the headings
                            fixHeading = gpsHeading = newGPSHeading;
                        }

                        //save current fix and set as valid
                        for (int i = totalFixSteps - 1; i > 0; i--) stepFixPts[i] = stepFixPts[i - 1];
                        stepFixPts[0].easting = pn.fix.easting;
                        stepFixPts[0].northing = pn.fix.northing;
                        stepFixPts[0].isSet = 1;

                        #endregion

                        #region Camera

                        double camDelta = fixHeading - smoothCamHeading;

                        if (camDelta < 0) camDelta += glm.twoPI;
                        else if (camDelta > glm.twoPI) camDelta -= glm.twoPI;

                        //calculate delta based on circular data problem 0 to 360 to 0, clamp to +- 2 Pi
                        if (camDelta >= -glm.PIBy2 && camDelta <= glm.PIBy2) camDelta *= -1.0;
                        else
                        {
                            if (camDelta > glm.PIBy2) { camDelta = glm.twoPI - camDelta; }
                            else { camDelta = (glm.twoPI + camDelta) * -1.0; }
                        }
                        if (camDelta > glm.twoPI) camDelta -= glm.twoPI;
                        else if (camDelta < -glm.twoPI) camDelta += glm.twoPI;

                        smoothCamHeading -= camDelta * camSmoothFactor;

                        if (smoothCamHeading > glm.twoPI) smoothCamHeading -= glm.twoPI;
                        else if (smoothCamHeading < -glm.twoPI) smoothCamHeading += glm.twoPI;

                        camHeading = glm.toDegrees(smoothCamHeading);

                    #endregion

                    //Calculate a million other things
                    byPass:
                        if (ahrs.imuHeading != 99999)
                        {
                            imuCorrected = (glm.toRadians(ahrs.imuHeading)) + imuGPS_Offset;
                            if (imuCorrected >= glm.twoPI) imuCorrected -= glm.twoPI;
                            else if (imuCorrected < 0) imuCorrected += glm.twoPI;

                            //use imu as heading when going slow
                            fixHeading = imuCorrected;
                        }

                        camDelta = fixHeading - smoothCamHeading;

                        if (camDelta < 0) camDelta += glm.twoPI;
                        else if (camDelta > glm.twoPI) camDelta -= glm.twoPI;

                        //calculate delta based on circular data problem 0 to 360 to 0, clamp to +- 2 Pi
                        if (camDelta >= -glm.PIBy2 && camDelta <= glm.PIBy2) camDelta *= -1.0;
                        else
                        {
                            if (camDelta > glm.PIBy2) { camDelta = glm.twoPI - camDelta; }
                            else { camDelta = (glm.twoPI + camDelta) * -1.0; }
                        }
                        if (camDelta > glm.twoPI) camDelta -= glm.twoPI;
                        else if (camDelta < -glm.twoPI) camDelta += glm.twoPI;

                        smoothCamHeading -= camDelta * camSmoothFactor;

                        if (smoothCamHeading > glm.twoPI) smoothCamHeading -= glm.twoPI;
                        else if (smoothCamHeading < -glm.twoPI) smoothCamHeading += glm.twoPI;

                        camHeading = glm.toDegrees(smoothCamHeading);

                        TheRest();
                        break;
                    }

                case "VTG":
                    {
                        isFirstHeadingSet = true;
                        if (avgSpeed > 1)
                        {
                            //use NMEA headings for camera and tractor graphic
                            fixHeading = glm.toRadians(pn.headingTrue);
                            camHeading = pn.headingTrue;
                            gpsHeading = fixHeading;
                        }

                        //grab the most current fix to last fix distance
                        distanceCurrentStepFix = glm.Distance(pn.fix, prevFix);

                        #region Antenna Offset

                        if (vehicle.VehicleConfig.AntennaOffset != 0)
                        {
                            pn.fix.easting = (Math.Cos(-fixHeading) * vehicle.VehicleConfig.AntennaOffset) + pn.fix.easting;
                            pn.fix.northing = (Math.Sin(-fixHeading) * vehicle.VehicleConfig.AntennaOffset) + pn.fix.northing;
                        }
                        #endregion

                        uncorrectedEastingGraph = pn.fix.easting;

                        //an IMU with heading correction, add the correction
                        if (ahrs.imuHeading != 99999)
                        {
                            //current gyro angle in radians
                            double correctionHeading = (glm.toRadians(ahrs.imuHeading));

                            //Difference between the IMU heading and the GPS heading
                            double gyroDelta = (correctionHeading + imuGPS_Offset) - gpsHeading;
                            if (gyroDelta < 0) gyroDelta += glm.twoPI;

                            //calculate delta based on circular data problem 0 to 360 to 0, clamp to +- 2 Pi
                            if (gyroDelta >= -glm.PIBy2 && gyroDelta <= glm.PIBy2) gyroDelta *= -1.0;
                            else
                            {
                                if (gyroDelta > glm.PIBy2) { gyroDelta = glm.twoPI - gyroDelta; }
                                else { gyroDelta = (glm.twoPI + gyroDelta) * -1.0; }
                            }
                            if (gyroDelta > glm.twoPI) gyroDelta -= glm.twoPI;
                            if (gyroDelta < -glm.twoPI) gyroDelta += glm.twoPI;

                            //if the gyro and last corrected fix is < 10 degrees, super low pass for gps
                            if (Math.Abs(gyroDelta) < 0.18)
                            {
                                //a bit of delta and add to correction to current gyro
                                imuGPS_Offset += (gyroDelta * (0.1));
                                if (imuGPS_Offset > glm.twoPI) imuGPS_Offset -= glm.twoPI;
                                if (imuGPS_Offset < -glm.twoPI) imuGPS_Offset += glm.twoPI;
                            }
                            else
                            {
                                //a bit of delta and add to correction to current gyro
                                imuGPS_Offset += (gyroDelta * (0.2));
                                if (imuGPS_Offset > glm.twoPI) imuGPS_Offset -= glm.twoPI;
                                if (imuGPS_Offset < -glm.twoPI) imuGPS_Offset += glm.twoPI;
                            }

                            //determine the Corrected heading based on gyro and GPS
                            imuCorrected = correctionHeading + imuGPS_Offset;
                            if (imuCorrected > glm.twoPI) imuCorrected -= glm.twoPI;
                            if (imuCorrected < 0) imuCorrected += glm.twoPI;

                            fixHeading = imuCorrected;

                            camHeading = fixHeading;
                            if (camHeading > glm.twoPI) camHeading -= glm.twoPI;
                            camHeading = glm.toDegrees(camHeading);
                        }

                        #region Roll

                        if (ahrs.imuRoll != 88888)
                        {
                            //change for roll to the right is positive times -1
                            rollCorrectionDistance = Math.Sin(glm.toRadians((ahrs.imuRoll))) * -vehicle.VehicleConfig.AntennaHeight;
                            correctionDistanceGraph = rollCorrectionDistance;

                            // roll to right is positive  **** important!!
                            pn.fix.easting = (Math.Cos(-fixHeading) * rollCorrectionDistance) + pn.fix.easting;
                            pn.fix.northing = (Math.Sin(-fixHeading) * rollCorrectionDistance) + pn.fix.northing;
                        }

                        #endregion Roll

                        TheRest();

                        break;
                    }

                case "Dual":
                    {
                        if (Properties.Settings.Default.setGPS_headingFromWhichSource == "Dual" && ahrs.autoSwitchDualFixOn)
                        {
                            lblSpeed.ForeColor = System.Drawing.Color.Green;
                            isChangingDirection = false;
                        }

                        isFirstHeadingSet = true;

                        //use Dual Antenna heading for camera and tractor graphic
                        fixHeading = glm.toRadians(pn.headingTrueDual);
                        gpsHeading = fixHeading;

                        uncorrectedEastingGraph = pn.fix.easting;

                        if (vehicle.VehicleConfig.AntennaOffset != 0)
                        {
                            pn.fix.easting = (Math.Cos(-fixHeading) * vehicle.VehicleConfig.AntennaOffset) + pn.fix.easting;
                            pn.fix.northing = (Math.Sin(-fixHeading) * vehicle.VehicleConfig.AntennaOffset) + pn.fix.northing;
                        }

                        if (ahrs.imuRoll != 88888 && vehicle.VehicleConfig.AntennaHeight != 0)
                        {

                            //change for roll to the right is positive times -1
                            rollCorrectionDistance = Math.Sin(glm.toRadians((ahrs.imuRoll))) * -vehicle.VehicleConfig.AntennaHeight;
                            correctionDistanceGraph = rollCorrectionDistance;

                            pn.fix.easting = (Math.Cos(-gpsHeading) * rollCorrectionDistance) + pn.fix.easting;
                            pn.fix.northing = (Math.Sin(-gpsHeading) * rollCorrectionDistance) + pn.fix.northing;
                        }

                        //grab the most current fix and save the distance from the last fix
                        distanceCurrentStepFix = glm.Distance(pn.fix, prevDistFix);
                        prevDistFix  = pn.fix;

                        //userDistance can be reset
                        distanceCurrentStepFixDisplay = distanceCurrentStepFix * 100;

                        distanceCurrentStepFix = glm.Distance(prevFix, pn.fix);

                        if (distanceCurrentStepFix > 0.1)
                        {
                            if ((fd.distanceUser += distanceCurrentStepFix) > 9999) fd.distanceUser = 0;
                            prevFix = pn.fix;
                        }

                        if (glm.Distance(lastReverseFix, pn.fix) > dualReverseDetectionDistance)
                        {
                            //most recent heading
                            double newHeading = Math.Atan2(pn.fix.easting - lastReverseFix.easting,
                                                        pn.fix.northing - lastReverseFix.northing);

                            if (newHeading < 0) newHeading += glm.twoPI;

                            //what is angle between the last reverse heading and current dual heading
                            double delta = Math.Abs(Math.PI - Math.Abs(Math.Abs(newHeading - fixHeading) - Math.PI));

                            //are we going backwards
                            isReverse = (delta > 2);

                            //save for next meter check
                            lastReverseFix = pn.fix;
                        }

                        double camDelta = fixHeading - smoothCamHeading;

                        if (camDelta < 0) camDelta += glm.twoPI;
                        else if (camDelta > glm.twoPI) camDelta -= glm.twoPI;

                        //calculate delta based on circular data problem 0 to 360 to 0, clamp to +- 2 Pi
                        if (camDelta >= -glm.PIBy2 && camDelta <= glm.PIBy2) camDelta *= -1.0;
                        else
                        {
                            if (camDelta > glm.PIBy2) { camDelta = glm.twoPI - camDelta; }
                            else { camDelta = (glm.twoPI + camDelta) * -1.0; }
                        }
                        if (camDelta > glm.twoPI) camDelta -= glm.twoPI;
                        else if (camDelta < -glm.twoPI) camDelta += glm.twoPI;

                        smoothCamHeading -= camDelta * camSmoothFactor;

                        if (smoothCamHeading > glm.twoPI) smoothCamHeading -= glm.twoPI;
                        else if (smoothCamHeading < -glm.twoPI) smoothCamHeading += glm.twoPI;

                        camHeading = glm.toDegrees(smoothCamHeading);

                        // FOR AUTOSWITCH DUALFIX2FIX START
                        // save current fix and set as valid to make switch dual to fix2fix fluid
                        for (int i = totalFixSteps - 1; i > 0; i--) stepFixPts[i] = stepFixPts[i - 1];
                        stepFixPts[0].easting = pn.fix.easting;
                        stepFixPts[0].northing = pn.fix.northing;
                        stepFixPts[0].isSet = 1;
                        // FOR AUTOSWITCH DUALFIX2FIX END

                        TheRest();

                        break;
                    }

                default:
                    break;
            }

            if (fixHeading >= glm.twoPI) fixHeading -= glm.twoPI;

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

            //if an outer boundary is set, then apply critical stop logic
            if (bnd.bndList != null && bnd.bndList.Count > 0)
            {
                //check if inside all fence
                if (!yt.isYouTurnBtnOn)
                {
                    mc.isOutOfBounds = !bnd.IsPointInsideFenceArea(pivotAxlePos);
                }
                else //Youturn is on
                {
                    bool isInTurnBounds = bnd.IsPointInsideTurnArea(pivotAxlePos) != -1;
                    //Are we inside outer and outside inner all turn boundaries, no turn creation problems
                    //if we are too much off track > 1.3m, kill the diagnostic creation, start again
                    //if (!yt.isYouTurnTriggered) 
                    if (isInTurnBounds)
                    {
                        mc.isOutOfBounds = false;
                        //now check to make sure we are not in an inner turn boundary - drive thru is ok
                        if (yt.youTurnPhase != 10)
                        {
                            if (crossTrackError > 1000)
                            {
                                yt.ResetCreatedYouTurn();
                            }
                            else
                            {
                                if (trk.gArr[trk.idx].mode == TrackMode.AB)
                                {
                                    yt.BuildABLineDubinsYouTurn();
                                }
                                else yt.BuildCurveDubinsYouTurn();
                            }

                            if (yt.uTurnStyle == 0 && yt.youTurnPhase == 10)
                            {
                                yt.SmoothYouTurn(6);
                            }

                            if (yt.isTurnCreationTooClose && !yt.turnTooCloseTrigger)
                            {
                                yt.turnTooCloseTrigger = true;
                                if (sounds.isTurnSoundOn)
                                {
                                    sounds.sndUTurnTooClose.Play();
                                    Log.EventWriter("U Turn Creation Failure");
                                }
                            }
                        }
                        else if (yt.ytList.Count > 5)//wait to trigger the actual turn since its made and waiting
                        {
                            //distance from current pivot to first point of youturn pattern
                            distancePivotToTurnLine = glm.Distance(yt.ytList[2], pivotAxlePos);

                            if ((distancePivotToTurnLine <= 20.0) && (distancePivotToTurnLine >= 18.0) && !yt.isYouTurnTriggered)

                                if (!sounds.isBoundAlarming)
                                {
                                    if (sounds.isTurnSoundOn) sounds.sndBoundaryAlarm.Play();
                                    sounds.isBoundAlarming = true;
                                }

                            //if we are close enough to pattern, trigger.
                            if ((distancePivotToTurnLine <= 1.0) && (distancePivotToTurnLine >= 0) && !yt.isYouTurnTriggered)
                            {
                                yt.YouTurnTrigger();
                                sounds.isBoundAlarming = false;
                            }

                            //if (isBtnAutoSteerOn && guidanceLineDistanceOff > 300 && !yt.isYouTurnTriggered)
                            //{
                            //    yt.ResetCreatedYouTurn();
                            //}
                        }
                    }
                    else
                    {
                        if (!yt.isYouTurnTriggered)
                        {
                            yt.ResetCreatedYouTurn();
                            mc.isOutOfBounds = !bnd.IsPointInsideFenceArea(pivotAxlePos);
                        }

                    }

                    //}
                    //// here is stop logic for out of bounds - in an inner or out the outer turn border.
                    //else
                    //{
                    //    //mc.isOutOfBounds = true;
                    //    if (isBtnAutoSteerOn)
                    //    {
                    //        if (yt.isYouTurnBtnOn)
                    //        {
                    //            yt.ResetCreatedYouTurn();
                    //            //sim.stepDistance = 0 / 17.86;
                    //        }
                    //    }
                    //    else
                    //    {
                    //        yt.isTurnCreationTooClose = false;
                    //    }

                    //}
                }
            }
            else
            {
                mc.isOutOfBounds = false;
            }

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