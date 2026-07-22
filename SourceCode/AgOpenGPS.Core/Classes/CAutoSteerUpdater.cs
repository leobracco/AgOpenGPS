using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using System;

namespace AgOpenGPS
{
    /// <summary>
    /// Arma y envía el PGN de posición corregida (lat/lon/heading) y el PGN 254
    /// de autosteer (velocidad, distancia a la línea, ángulo de dirección),
    /// incluida la selección de línea AB/curva activa y el promedio de cross
    /// track error. Vivía embebido en UpdateFixPosition (Position.designer.cs):
    /// se movió a Core porque es cálculo/armado de bytes puro sobre objetos ya
    /// portados (trk/ABLine/curve/ct/recPath/vehicle/mc/isobus) — traspaso
    /// portabilidad, bloque 9 matriz Android (2026-07-20). Los toques UI
    /// (click del botón AutoSteer, timed message, timer del simulador) cruzan
    /// por IAutoSteerHost.
    /// </summary>
    public class CAutoSteerUpdater
    {
        private readonly IAutoSteerHost mf;

        public CAutoSteerUpdater(IAutoSteerHost host)
        {
            mf = host;
        }

        public void SendCorrectedPositionPgn()
        {
            CNMEA pn = mf.Pn;

            Wgs84 latLon = mf.AppModel.LocalPlane.ConvertGeoCoordToWgs84(pn.fix.ToGeoCoord());
            byte[] correctedPosition = new byte[30];
            correctedPosition[0] = 0x80;
            correctedPosition[1] = 0x81;
            correctedPosition[2] = 0x7F;
            correctedPosition[3] = 0x64;
            correctedPosition[4] = 24;
            Buffer.BlockCopy(BitConverter.GetBytes(latLon.Longitude), 0, correctedPosition, 5, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(latLon.Latitude), 0, correctedPosition, 13, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(glm.toDegrees(mf.GpsHeading)), 0, correctedPosition, 21, 8);
            mf.SendPgnToLoop(correctedPosition);
        }

        public void BuildAndSendAutoSteerPgn()
        {
            CNMEA pn = mf.Pn;
            CTrack trk = mf.Trk;
            CModuleComm mc = mf.Mc;
            CPGN_FE p_254 = mf.P254;

            //preset the values
            mf.GuidanceLineDistanceOff = 32000;

            if (mf.Ct.isContourBtnOn)
            {
                mf.Ct.DistanceFromContourLine(mf.PivotAxlePos, mf.SteerAxlePos);
            }
            else
            {
                //auto track routine
                if (trk.isAutoTrack && !mf.IsBtnAutoSteerOn && trk.autoTrack3SecTimer >= 1)
                {
                    trk.autoTrack3SecTimer = 0;
                    int lastIndex = trk.idx;
                    trk.idx = trk.FindClosestRefTrack(mf.SteerAxlePos);
                    if (lastIndex != trk.idx)
                    {
                        mf.Curve.isCurveValid = false;
                        mf.ABLine.isABValid = false;
                    }
                }

                //like normal
                if (trk.gArr != null && trk.gArr.Count > 0 && trk.idx >= 0 && trk.idx < trk.gArr.Count)
                {
                    if (trk.gArr[trk.idx].mode == TrackMode.AB)
                    {
                        mf.ABLine.BuildCurrentABLineList(mf.PivotAxlePos);
                        mf.ABLine.GetCurrentABLine(mf.PivotAxlePos, mf.SteerAxlePos);
                    }
                    else
                    {
                        mf.Curve.BuildCurveCurrentList(mf.PivotAxlePos);
                        mf.Curve.GetCurrentCurveLine(mf.PivotAxlePos, mf.SteerAxlePos);
                    }
                }
            }

            // autosteer at full speed of updates

            //if the whole path driving driving process is green
            if (mf.RecPath.isDrivingRecordedPath) mf.RecPath.UpdatePosition();

            // If Drive button off - normal autosteer
            if (!mf.Vehicle.isInFreeDriveMode)
            {
                //fill up0 the appropriate arrays with new values
                p_254.pgn[p_254.speedHi] = unchecked((byte)((int)(Math.Abs(mf.AvgSpeed) * 10.0) >> 8));
                p_254.pgn[p_254.speedLo] = unchecked((byte)((int)(Math.Abs(mf.AvgSpeed) * 10.0)));

                //save distance for display
                mf.LightbarDistance = mf.GuidanceLineDistanceOff;
                mf.Isobus.SetGuidanceLineDeviation(mf.GuidanceLineDistanceOff * 100);
                int currentSpeed = (int)(mf.AvgSpeed * 1000 / 3.6);  // convert from km/h to mm/s
                if (mf.IsReverse)
                {
                    currentSpeed = -currentSpeed;
                }
                mf.Isobus.SetActualSpeed(currentSpeed);
                mf.Isobus.SetTotalDistance((int)(mf.Fd.distanceUser * 1000)); // convert from meter to mm

                if (!mf.IsBtnAutoSteerOn) //32020 means auto steer is off
                {
                    mf.GuidanceLineDistanceOff = 32020;
                    p_254.pgn[p_254.status] = 0;
                }
                else p_254.pgn[p_254.status] = 1;

                if (mf.RecPath.isDrivingRecordedPath || mf.RecPath.isFollowingDubinsToPath) p_254.pgn[p_254.status] = 1;

                //convert to cm from mm and divide by 2 - lightbar
                int distanceX2;
                if (mf.GuidanceLineDistanceOff == 32020 || mf.GuidanceLineDistanceOff == 32000)
                    distanceX2 = 255;
                else
                {
                    distanceX2 = (int)(mf.GuidanceLineDistanceOff * 0.05);

                    if (distanceX2 < -127) distanceX2 = -127;
                    else if (distanceX2 > 127) distanceX2 = 127;
                    distanceX2 += 127;
                }

                p_254.pgn[p_254.lineDistance] = unchecked((byte)distanceX2);

                if (!mf.IsSimTimerEnabled)
                {
                    if (mf.IsBtnAutoSteerOn && mf.AvgSpeed > mf.Vehicle.maxSteerSpeed)
                    {
                        mf.PerformAutoSteerClick();
                    }

                    if (mf.IsBtnAutoSteerOn && mf.AvgSpeed < mf.Vehicle.minSteerSpeed)
                    {
                        mf.MinSteerSpeedTimer++;
                        if (mf.MinSteerSpeedTimer > 80)
                        {
                            mf.PerformAutoSteerClick();
                            if (mf.IsMetric)
                                mf.TimedMessageBox(3000, "AutoSteer Disabled", "Below Minimum Safe Steering Speed: " + mf.Vehicle.minSteerSpeed.ToString("N0") + " Kmh");
                            else
                                mf.TimedMessageBox(3000, "AutoSteer Disabled", "Below Minimum Safe Steering Speed: " + Speed.KmhToMph(mf.Vehicle.minSteerSpeed).ToString("N1") + " MPH");

                            Log.EventWriter("Steer Off, Below Min Steering Speed");
                        }
                    }
                    else
                    {
                        mf.MinSteerSpeedTimer = 0;
                    }
                }

                if (!AgOpenGPS.Properties.Settings.Default.setAutoSwitchDualFixOn && mf.IsChangingDirection && mf.Ahrs.imuHeading == 99999)
                {
                    p_254.pgn[p_254.status] = 0;
                }
                //for now if backing up, turn off autosteer
                if (!mf.IsSteerInReverse)
                {
                    if (mf.IsReverse) p_254.pgn[p_254.status] = 0;
                }

                // delay on dead zone.
                if (p_254.pgn[p_254.status] == 1 && !mf.IsReverse
                    && Math.Abs(mf.GuidanceLineSteerAngle - mc.actualSteerAngleDegrees * 100) < mf.Vehicle.deadZoneHeading)
                {
                    if (mf.Vehicle.deadZoneDelayCounter > mf.Vehicle.deadZoneDelay)
                    {
                        mf.Vehicle.isInDeadZone = true;
                    }
                }
                else
                {
                    mf.Vehicle.deadZoneDelayCounter = 0;
                    mf.Vehicle.isInDeadZone = false;
                }

                if (!mf.Vehicle.isInDeadZone)
                {
                    p_254.pgn[p_254.steerAngleHi] = unchecked((byte)(mf.GuidanceLineSteerAngle >> 8));
                    p_254.pgn[p_254.steerAngleLo] = unchecked((byte)(mf.GuidanceLineSteerAngle));
                }
            }
            else //Drive button is on
            {
                //fill up the auto steer array with free drive values
                p_254.pgn[p_254.speedHi] = unchecked((byte)((int)(80) >> 8));
                p_254.pgn[p_254.speedLo] = unchecked((byte)((int)(80)));

                //turn on status to operate
                p_254.pgn[p_254.status] = 1;

                //send the steer angle
                mf.GuidanceLineSteerAngle = (Int16)(mf.Vehicle.driveFreeSteerAngle * 100);

                p_254.pgn[p_254.steerAngleHi] = unchecked((byte)(mf.GuidanceLineSteerAngle >> 8));
                p_254.pgn[p_254.steerAngleLo] = unchecked((byte)(mf.GuidanceLineSteerAngle));
            }

            //out serial to autosteer module  //indivdual classes load the distance and heading deltas
            mf.SendPgnToLoop(p_254.pgn);

            //for average cross track error
            if (mf.GuidanceLineDistanceOff < 29000)
            {
                mf.CrossTrackError = (int)((double)mf.CrossTrackError * 0.90 + Math.Abs((double)mf.GuidanceLineDistanceOff) * 0.1);
            }
            else
            {
                mf.CrossTrackError = 0;
            }
        }
    }
}
