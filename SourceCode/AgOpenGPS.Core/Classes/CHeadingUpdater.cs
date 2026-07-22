using AgLibrary.Logging;
using System;

namespace AgOpenGPS
{
    /// <summary>
    /// Switch de heading (Fix/VTG/Dual): decide de qué fuente sale el heading
    /// del frame, con fusión IMU/GPS, detección de reversa y suavizado de
    /// cámara. Vivía embebido en UpdateFixPosition (Position.designer.cs): se
    /// movió a Core porque es cálculo numérico puro sobre objetos ya
    /// portados (pn/ahrs/vehicle/mc/fd) — traspaso portabilidad, bloque 9
    /// matriz Android (2026-07-20). Es la pieza más grande y compleja de
    /// UpdateFixPosition (gotos originales del caso "Fix" preservados tal
    /// cual, son válidos dentro del mismo método). Los toques UI que
    /// quedaban adentro (color de lblSpeed, TimedMessageBox) cruzan por
    /// IHeadingHost.
    /// </summary>
    public class CHeadingUpdater
    {
        private readonly IHeadingHost mf;

        public CHeadingUpdater(IHeadingHost host)
        {
            mf = host;
        }

        public void UpdateHeading()
        {
            if (AgOpenGPS.Properties.Settings.Default.setGPS_headingFromWhichSource == "Dual" && mf.Ahrs.autoSwitchDualFixOn)
            {
                if (Math.Abs(mf.Pn.speed) > mf.Ahrs.autoSwitchDualFixSpeed)
                {
                    mf.HeadingFromSource = "Fix";
                    mf.Ahrs.isDualAsIMU = true;
                }
                else
                {
                    mf.HeadingFromSource = "Dual";
                    mf.Ahrs.isDualAsIMU = false;
                    mf.Ahrs.imuHeading = 99999;
                }
            }

            switch (mf.HeadingFromSource)
            {
                //calculate current heading only when moving, otherwise use last
                case "Fix":
                    {
                        if (AgOpenGPS.Properties.Settings.Default.setGPS_headingFromWhichSource == "Dual" && mf.Ahrs.autoSwitchDualFixOn)
                        {
                            mf.SetSpeedLabelColor(true);
                        }

                        mf.DistanceCurrentStepFixDisplay = glm.Distance(mf.PrevDistFix, mf.Pn.fix);
                        mf.DistanceCurrentStepFixDisplay *= 100;
                        mf.PrevDistFix = mf.Pn.fix;

                        if (Math.Abs(mf.AvgSpeed) < 1.5 && !mf.IsFirstHeadingSet)
                            goto byPass;

                        if (!mf.IsFirstHeadingSet) //set in steer settings, Stanley
                        {
                            vec2 prevFix = mf.PrevFix;
                            prevFix.easting = mf.StepFixPts[0].easting; prevFix.northing = mf.StepFixPts[0].northing;
                            mf.PrevFix = prevFix;

                            if (mf.StepFixPts[2].isSet == 0)
                            {
                                //this is the first position no roll or offset correction
                                if (mf.StepFixPts[0].isSet == 0)
                                {
                                    mf.StepFixPts[0].easting = mf.Pn.fix.easting;
                                    mf.StepFixPts[0].northing = mf.Pn.fix.northing;
                                    mf.StepFixPts[0].isSet = 1;
                                    return;
                                }

                                //and the second
                                if (mf.StepFixPts[1].isSet == 0)
                                {
                                    for (int i = mf.StepFixPts.Length - 1; i > 0; i--) mf.StepFixPts[i] = mf.StepFixPts[i - 1];
                                    mf.StepFixPts[0].easting = mf.Pn.fix.easting;
                                    mf.StepFixPts[0].northing = mf.Pn.fix.northing;
                                    mf.StepFixPts[0].isSet = 1;
                                    return;
                                }

                                //the critcal moment for checking initial direction/heading.
                                for (int i = mf.StepFixPts.Length - 1; i > 0; i--) mf.StepFixPts[i] = mf.StepFixPts[i - 1];
                                mf.StepFixPts[0].easting = mf.Pn.fix.easting;
                                mf.StepFixPts[0].northing = mf.Pn.fix.northing;
                                mf.StepFixPts[0].isSet = 1;

                                double gpsHeading = Math.Atan2(mf.Pn.fix.easting - mf.StepFixPts[2].easting,
                                    mf.Pn.fix.northing - mf.StepFixPts[2].northing);

                                if (gpsHeading < 0) gpsHeading += glm.twoPI;
                                else if (gpsHeading >= glm.twoPI) gpsHeading -= glm.twoPI;

                                mf.GpsHeading = gpsHeading;
                                mf.FixHeading = gpsHeading;

                                //set the imu to gps heading offset
                                if (mf.Ahrs.imuHeading != 99999)
                                {
                                    double imuHeading = (glm.toRadians(mf.Ahrs.imuHeading));
                                    mf.ImuGpsOffset = 0;

                                    //Difference between the IMU heading and the GPS heading
                                    double gyroDelta = (imuHeading + mf.ImuGpsOffset) - gpsHeading;

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
                                    mf.ImuGpsOffset = gyroDelta;
                                    mf.ImuGpsOffset = Math.Round(mf.ImuGpsOffset, 6);

                                    if (mf.ImuGpsOffset >= glm.twoPI) mf.ImuGpsOffset -= glm.twoPI;
                                    else if (mf.ImuGpsOffset <= 0) mf.ImuGpsOffset += glm.twoPI;

                                    //determine the Corrected heading based on gyro and GPS
                                    mf.ImuCorrected = imuHeading + mf.ImuGpsOffset;
                                    if (mf.ImuCorrected >= glm.twoPI) mf.ImuCorrected -= glm.twoPI;
                                    else if (mf.ImuCorrected < 0) mf.ImuCorrected += glm.twoPI;

                                    mf.FixHeading = mf.ImuCorrected;
                                }

                                //set the camera
                                mf.CamHeading = glm.toDegrees(gpsHeading);

                                //now we have a heading, fix the first 3
                                if (mf.Vehicle.VehicleConfig.AntennaOffset != 0)
                                {
                                    for (int i = 0; i < 3; i++)
                                    {
                                        mf.StepFixPts[i].easting = (Math.Cos(-gpsHeading) * mf.Vehicle.VehicleConfig.AntennaOffset) + mf.StepFixPts[i].easting;
                                        mf.StepFixPts[i].northing = (Math.Sin(-gpsHeading) * mf.Vehicle.VehicleConfig.AntennaOffset) + mf.StepFixPts[i].northing;
                                    }
                                }

                                if (mf.Ahrs.imuRoll != 88888)
                                {
                                    //change for roll to the right is positive times -1
                                    mf.RollCorrectionDistance = Math.Tan(glm.toRadians((mf.Ahrs.imuRoll))) * -mf.Vehicle.VehicleConfig.AntennaHeight;

                                    // roll to left is positive  **** important!!
                                    // not any more - April 30, 2019 - roll to right is positive Now! Still Important
                                    for (int i = 0; i < 3; i++)
                                    {
                                        mf.StepFixPts[i].easting = (Math.Cos(-gpsHeading) * mf.RollCorrectionDistance) + mf.StepFixPts[i].easting;
                                        mf.StepFixPts[i].northing = (Math.Sin(-gpsHeading) * mf.RollCorrectionDistance) + mf.StepFixPts[i].northing;
                                    }
                                }

                                //get the distance from first to 2nd point, update fix with new offset/roll
                                mf.StepFixPts[0].distance = glm.Distance(mf.StepFixPts[1], mf.StepFixPts[0]);
                                mf.Pn.fix.easting = mf.StepFixPts[0].easting;
                                mf.Pn.fix.northing = mf.StepFixPts[0].northing;

                                mf.IsFirstHeadingSet = true;
                                mf.HasBeenFirstHeadingSet = true;
                                mf.TimedMessageBox(2000, "Direction Reset", "Forward is Set");
                                Log.EventWriter("Forward Is Set");

                                mf.LastGps = mf.Pn.fix;

                                return;
                            }
                        }

                        #region Offset Roll
                        if (mf.Vehicle.VehicleConfig.AntennaOffset != 0)
                        {
                            mf.Pn.fix.easting = (Math.Cos(-mf.GpsHeading) * mf.Vehicle.VehicleConfig.AntennaOffset) + mf.Pn.fix.easting;
                            mf.Pn.fix.northing = (Math.Sin(-mf.GpsHeading) * mf.Vehicle.VehicleConfig.AntennaOffset) + mf.Pn.fix.northing;
                        }

                        mf.UncorrectedEastingGraph = mf.Pn.fix.easting;

                        if (mf.Ahrs.imuRoll != 88888)
                        {
                            //change for roll to the right is positive times -1
                            mf.RollCorrectionDistance = Math.Sin(glm.toRadians((mf.Ahrs.imuRoll))) * -mf.Vehicle.VehicleConfig.AntennaHeight;
                            mf.CorrectionDistanceGraph = mf.RollCorrectionDistance;

                            mf.Pn.fix.easting = (Math.Cos(-mf.GpsHeading) * mf.RollCorrectionDistance) + mf.Pn.fix.easting;
                            mf.Pn.fix.northing = (Math.Sin(-mf.GpsHeading) * mf.RollCorrectionDistance) + mf.Pn.fix.northing;
                        }

                        #endregion

                        #region Fix Heading

                        //how far since last fix
                        mf.DistanceCurrentStepFix = glm.Distance(mf.StepFixPts[0], mf.Pn.fix);

                        if (mf.DistanceCurrentStepFix < mf.GpsMinimumStepDistance)
                        {
                            goto byPass;
                        }

                        mf.Fd.distanceUser += mf.DistanceCurrentStepFix;
                        if (mf.Fd.distanceUser > 9999) mf.Fd.distanceUser = 0;

                        double minFixHeadingDistSquared = mf.MinHeadingStepDist * mf.MinHeadingStepDist;
                        mf.FixToFixHeadingDistance = 0;

                        for (int i = 0; i < mf.StepFixPts.Length; i++)
                        {
                            mf.FixToFixHeadingDistance = glm.DistanceSquared(mf.StepFixPts[i], mf.Pn.fix);
                            mf.CurrentStepFix = i;

                            if (mf.FixToFixHeadingDistance > minFixHeadingDistSquared)
                            {
                                break;
                            }
                        }

                        if (mf.FixToFixHeadingDistance < minFixHeadingDistSquared * 0.5)
                            goto byPass;

                        double newGPSHeading = Math.Atan2(mf.Pn.fix.easting - mf.StepFixPts[mf.CurrentStepFix].easting,
                                                mf.Pn.fix.northing - mf.StepFixPts[mf.CurrentStepFix].northing);
                        if (newGPSHeading < 0) newGPSHeading += glm.twoPI;

                        //imu on board
                        if (mf.Ahrs.imuHeading != 99999)
                        {
                            mf.IsChangingDirection = false;

                            if (mf.Ahrs.isReverseOn)
                            {
                                ////what is angle between the last valid heading before stopping and one just now
                                mf.Delta = Math.Abs(Math.PI - Math.Abs(Math.Abs(newGPSHeading - mf.ImuCorrected) - Math.PI));

                                //ie change in direction
                                if (mf.Delta > 1.57) //
                                {
                                    mf.IsReverse = true;
                                    newGPSHeading += Math.PI;
                                    if (newGPSHeading < 0) newGPSHeading += glm.twoPI;
                                    else if (newGPSHeading >= glm.twoPI) newGPSHeading -= glm.twoPI;
                                    mf.IsReverseWithIMU = true;
                                }
                                else
                                {
                                    mf.IsReverse = false;
                                    mf.IsReverseWithIMU = false;
                                }
                            }
                            else
                            {
                                mf.IsReverse = false;
                            }

                            if (mf.IsReverse)
                                newGPSHeading -= glm.toRadians(mf.Vehicle.VehicleConfig.AntennaPivot / 1
                                    * mf.Mc.actualSteerAngleDegrees * mf.Ahrs.reverseComp);
                            else
                                newGPSHeading -= glm.toRadians(mf.Vehicle.VehicleConfig.AntennaPivot / 1
                                    * mf.Mc.actualSteerAngleDegrees * mf.Ahrs.forwardComp);

                            if (newGPSHeading < 0) newGPSHeading += glm.twoPI;
                            else if (newGPSHeading >= glm.twoPI) newGPSHeading -= glm.twoPI;

                            mf.GpsHeading = newGPSHeading;

                            #region IMU Fusion

                            // IMU Fusion with heading correction, add the correction
                            //current gyro angle in radians
                            double imuHeading = (glm.toRadians(mf.Ahrs.imuHeading));

                            //Difference between the IMU heading and the GPS heading
                            double gyroDelta = (imuHeading + mf.ImuGpsOffset) - mf.GpsHeading;

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
                            if (!mf.IsReverseWithIMU)
                                mf.ImuGpsOffset += (gyroDelta * (mf.Ahrs.fusionWeight));
                            else
                                mf.ImuGpsOffset += (gyroDelta * (0.02));

                            if (mf.ImuGpsOffset > glm.twoPI) mf.ImuGpsOffset -= glm.twoPI;
                            else if (mf.ImuGpsOffset < 0) mf.ImuGpsOffset += glm.twoPI;

                            //determine the Corrected heading based on gyro and GPS
                            mf.ImuCorrected = imuHeading + mf.ImuGpsOffset;
                            if (mf.ImuCorrected >= glm.twoPI) mf.ImuCorrected -= glm.twoPI;
                            else if (mf.ImuCorrected < 0) mf.ImuCorrected += glm.twoPI;

                            //use imu as heading when going slow
                            mf.FixHeading = mf.ImuCorrected;

                            #endregion
                        }
                        else
                        {
                            if (mf.Ahrs.isReverseOn)
                            {
                                ////what is angle between the last valid heading before stopping and one just now
                                mf.Delta = Math.Abs(Math.PI - Math.Abs(Math.Abs(newGPSHeading - mf.GpsHeading) - Math.PI));

                                mf.FilteredDelta = mf.Delta * 0.2 + mf.FilteredDelta * 0.8;

                                //filtered delta different then delta
                                if (Math.Abs(mf.FilteredDelta - mf.Delta) > 0.5)
                                {
                                    mf.IsChangingDirection = true;
                                }
                                else
                                {
                                    mf.IsChangingDirection = false;
                                }

                                //we can't be sure if changing direction so do nothing
                                if (mf.IsChangingDirection)
                                    goto byPass;

                                //ie change in direction
                                if (mf.FilteredDelta > 1.57) //
                                {
                                    mf.IsReverse = true;
                                    newGPSHeading += Math.PI;
                                    if (newGPSHeading < 0) newGPSHeading += glm.twoPI;
                                    else if (newGPSHeading >= glm.twoPI) newGPSHeading -= glm.twoPI;
                                }
                                else
                                    mf.IsReverse = false;

                                if (mf.IsReverse)
                                    newGPSHeading -= glm.toRadians(mf.Vehicle.VehicleConfig.AntennaPivot / 1 * mf.Mc.actualSteerAngleDegrees * mf.Ahrs.reverseComp);
                                else
                                    newGPSHeading -= glm.toRadians(mf.Vehicle.VehicleConfig.AntennaPivot / 1 * mf.Mc.actualSteerAngleDegrees * mf.Ahrs.forwardComp);

                                if (newGPSHeading < 0) newGPSHeading += glm.twoPI;
                                else if (newGPSHeading >= glm.twoPI) newGPSHeading -= glm.twoPI;
                            }
                            else
                            {
                                mf.IsReverse = false;
                                mf.IsChangingDirection = false;
                            }

                            //set the headings
                            mf.FixHeading = mf.GpsHeading = newGPSHeading;
                        }

                        //save current fix and set as valid
                        for (int i = mf.StepFixPts.Length - 1; i > 0; i--) mf.StepFixPts[i] = mf.StepFixPts[i - 1];
                        mf.StepFixPts[0].easting = mf.Pn.fix.easting;
                        mf.StepFixPts[0].northing = mf.Pn.fix.northing;
                        mf.StepFixPts[0].isSet = 1;

                        #endregion

                        #region Camera

                        double camDelta = mf.FixHeading - mf.SmoothCamHeading;

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

                        mf.SmoothCamHeading -= camDelta * mf.CamSmoothFactor;

                        if (mf.SmoothCamHeading > glm.twoPI) mf.SmoothCamHeading -= glm.twoPI;
                        else if (mf.SmoothCamHeading < -glm.twoPI) mf.SmoothCamHeading += glm.twoPI;

                        mf.CamHeading = glm.toDegrees(mf.SmoothCamHeading);

                    #endregion

                    //Calculate a million other things
                    byPass:
                        if (mf.Ahrs.imuHeading != 99999)
                        {
                            mf.ImuCorrected = (glm.toRadians(mf.Ahrs.imuHeading)) + mf.ImuGpsOffset;
                            if (mf.ImuCorrected >= glm.twoPI) mf.ImuCorrected -= glm.twoPI;
                            else if (mf.ImuCorrected < 0) mf.ImuCorrected += glm.twoPI;

                            //use imu as heading when going slow
                            mf.FixHeading = mf.ImuCorrected;
                        }

                        camDelta = mf.FixHeading - mf.SmoothCamHeading;

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

                        mf.SmoothCamHeading -= camDelta * mf.CamSmoothFactor;

                        if (mf.SmoothCamHeading > glm.twoPI) mf.SmoothCamHeading -= glm.twoPI;
                        else if (mf.SmoothCamHeading < -glm.twoPI) mf.SmoothCamHeading += glm.twoPI;

                        mf.CamHeading = glm.toDegrees(mf.SmoothCamHeading);

                        mf.TheRest();
                        break;
                    }

                case "VTG":
                    {
                        mf.IsFirstHeadingSet = true;
                        if (mf.AvgSpeed > 1)
                        {
                            //use NMEA headings for camera and tractor graphic
                            mf.FixHeading = glm.toRadians(mf.Pn.headingTrue);
                            mf.CamHeading = mf.Pn.headingTrue;
                            mf.GpsHeading = mf.FixHeading;
                        }

                        //grab the most current fix to last fix distance
                        mf.DistanceCurrentStepFix = glm.Distance(mf.Pn.fix, mf.PrevFix);

                        #region Antenna Offset

                        if (mf.Vehicle.VehicleConfig.AntennaOffset != 0)
                        {
                            mf.Pn.fix.easting = (Math.Cos(-mf.FixHeading) * mf.Vehicle.VehicleConfig.AntennaOffset) + mf.Pn.fix.easting;
                            mf.Pn.fix.northing = (Math.Sin(-mf.FixHeading) * mf.Vehicle.VehicleConfig.AntennaOffset) + mf.Pn.fix.northing;
                        }
                        #endregion

                        mf.UncorrectedEastingGraph = mf.Pn.fix.easting;

                        //an IMU with heading correction, add the correction
                        if (mf.Ahrs.imuHeading != 99999)
                        {
                            //current gyro angle in radians
                            double correctionHeading = (glm.toRadians(mf.Ahrs.imuHeading));

                            //Difference between the IMU heading and the GPS heading
                            double gyroDelta = (correctionHeading + mf.ImuGpsOffset) - mf.GpsHeading;
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
                                mf.ImuGpsOffset += (gyroDelta * (0.1));
                                if (mf.ImuGpsOffset > glm.twoPI) mf.ImuGpsOffset -= glm.twoPI;
                                if (mf.ImuGpsOffset < -glm.twoPI) mf.ImuGpsOffset += glm.twoPI;
                            }
                            else
                            {
                                //a bit of delta and add to correction to current gyro
                                mf.ImuGpsOffset += (gyroDelta * (0.2));
                                if (mf.ImuGpsOffset > glm.twoPI) mf.ImuGpsOffset -= glm.twoPI;
                                if (mf.ImuGpsOffset < -glm.twoPI) mf.ImuGpsOffset += glm.twoPI;
                            }

                            //determine the Corrected heading based on gyro and GPS
                            mf.ImuCorrected = correctionHeading + mf.ImuGpsOffset;
                            if (mf.ImuCorrected > glm.twoPI) mf.ImuCorrected -= glm.twoPI;
                            if (mf.ImuCorrected < 0) mf.ImuCorrected += glm.twoPI;

                            mf.FixHeading = mf.ImuCorrected;

                            mf.CamHeading = mf.FixHeading;
                            if (mf.CamHeading > glm.twoPI) mf.CamHeading -= glm.twoPI;
                            mf.CamHeading = glm.toDegrees(mf.CamHeading);
                        }

                        #region Roll

                        if (mf.Ahrs.imuRoll != 88888)
                        {
                            //change for roll to the right is positive times -1
                            mf.RollCorrectionDistance = Math.Sin(glm.toRadians((mf.Ahrs.imuRoll))) * -mf.Vehicle.VehicleConfig.AntennaHeight;
                            mf.CorrectionDistanceGraph = mf.RollCorrectionDistance;

                            // roll to right is positive  **** important!!
                            mf.Pn.fix.easting = (Math.Cos(-mf.FixHeading) * mf.RollCorrectionDistance) + mf.Pn.fix.easting;
                            mf.Pn.fix.northing = (Math.Sin(-mf.FixHeading) * mf.RollCorrectionDistance) + mf.Pn.fix.northing;
                        }

                        #endregion Roll

                        mf.TheRest();

                        break;
                    }

                case "Dual":
                    {
                        if (AgOpenGPS.Properties.Settings.Default.setGPS_headingFromWhichSource == "Dual" && mf.Ahrs.autoSwitchDualFixOn)
                        {
                            mf.SetSpeedLabelColor(false);
                            mf.IsChangingDirection = false;
                        }

                        mf.IsFirstHeadingSet = true;

                        //use Dual Antenna heading for camera and tractor graphic
                        mf.FixHeading = glm.toRadians(mf.Pn.headingTrueDual);
                        mf.GpsHeading = mf.FixHeading;

                        mf.UncorrectedEastingGraph = mf.Pn.fix.easting;

                        if (mf.Vehicle.VehicleConfig.AntennaOffset != 0)
                        {
                            mf.Pn.fix.easting = (Math.Cos(-mf.FixHeading) * mf.Vehicle.VehicleConfig.AntennaOffset) + mf.Pn.fix.easting;
                            mf.Pn.fix.northing = (Math.Sin(-mf.FixHeading) * mf.Vehicle.VehicleConfig.AntennaOffset) + mf.Pn.fix.northing;
                        }

                        if (mf.Ahrs.imuRoll != 88888 && mf.Vehicle.VehicleConfig.AntennaHeight != 0)
                        {
                            //change for roll to the right is positive times -1
                            mf.RollCorrectionDistance = Math.Sin(glm.toRadians((mf.Ahrs.imuRoll))) * -mf.Vehicle.VehicleConfig.AntennaHeight;
                            mf.CorrectionDistanceGraph = mf.RollCorrectionDistance;

                            mf.Pn.fix.easting = (Math.Cos(-mf.GpsHeading) * mf.RollCorrectionDistance) + mf.Pn.fix.easting;
                            mf.Pn.fix.northing = (Math.Sin(-mf.GpsHeading) * mf.RollCorrectionDistance) + mf.Pn.fix.northing;
                        }

                        //grab the most current fix and save the distance from the last fix
                        mf.DistanceCurrentStepFix = glm.Distance(mf.Pn.fix, mf.PrevDistFix);
                        mf.PrevDistFix = mf.Pn.fix;

                        //userDistance can be reset
                        mf.DistanceCurrentStepFixDisplay = mf.DistanceCurrentStepFix * 100;

                        mf.DistanceCurrentStepFix = glm.Distance(mf.PrevFix, mf.Pn.fix);

                        if (mf.DistanceCurrentStepFix > 0.1)
                        {
                            mf.Fd.distanceUser += mf.DistanceCurrentStepFix;
                            if (mf.Fd.distanceUser > 9999) mf.Fd.distanceUser = 0;
                            mf.PrevFix = mf.Pn.fix;
                        }

                        if (glm.Distance(mf.LastReverseFix, mf.Pn.fix) > mf.DualReverseDetectionDistance)
                        {
                            //most recent heading
                            double newHeading = Math.Atan2(mf.Pn.fix.easting - mf.LastReverseFix.easting,
                                                        mf.Pn.fix.northing - mf.LastReverseFix.northing);

                            if (newHeading < 0) newHeading += glm.twoPI;

                            //what is angle between the last reverse heading and current dual heading
                            double delta = Math.Abs(Math.PI - Math.Abs(Math.Abs(newHeading - mf.FixHeading) - Math.PI));

                            //are we going backwards
                            mf.IsReverse = (delta > 2);

                            //save for next meter check
                            mf.LastReverseFix = mf.Pn.fix;
                        }

                        double camDelta = mf.FixHeading - mf.SmoothCamHeading;

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

                        mf.SmoothCamHeading -= camDelta * mf.CamSmoothFactor;

                        if (mf.SmoothCamHeading > glm.twoPI) mf.SmoothCamHeading -= glm.twoPI;
                        else if (mf.SmoothCamHeading < -glm.twoPI) mf.SmoothCamHeading += glm.twoPI;

                        mf.CamHeading = glm.toDegrees(mf.SmoothCamHeading);

                        // FOR AUTOSWITCH DUALFIX2FIX START
                        // save current fix and set as valid to make switch dual to fix2fix fluid
                        for (int i = mf.StepFixPts.Length - 1; i > 0; i--) mf.StepFixPts[i] = mf.StepFixPts[i - 1];
                        mf.StepFixPts[0].easting = mf.Pn.fix.easting;
                        mf.StepFixPts[0].northing = mf.Pn.fix.northing;
                        mf.StepFixPts[0].isSet = 1;
                        // FOR AUTOSWITCH DUALFIX2FIX END

                        mf.TheRest();

                        break;
                    }

                default:
                    break;
            }

            if (mf.FixHeading >= glm.twoPI) mf.FixHeading -= glm.twoPI;
        }
    }
}
