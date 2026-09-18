using AgOpenGPS.Core.Models;
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public class CABLine
    {
        //los colores de dibujo viven en ABLineDrawExtensions (DrawLib)

        public double abHeading, abLength;

        public bool isABValid;

        //the current AB guidance line
        public vec3 currentLinePtA = new vec3(0.0, 0.0, 0.0);
        public vec3 currentLinePtB = new vec3(0.0, 1.0, 0.0);

        public double distanceFromCurrentLinePivot;
        public double distanceFromRefLine;

        //pure pursuit values
        public vec2 goalPointAB = new vec2(0, 0);

        public int howManyPathsAway, lastHowManyPathsAway;
        public bool isMakingABLine;
        public bool isHeadingSameWay = true, lastIsHeadingSameWay;

        //public bool isOnTramLine;
        //public int tramBasedOn;
        public double ppRadiusAB;

        public vec2 radiusPointAB = new vec2(0, 0);
        public double rEastAB, rNorthAB;

        public double snapDistance, lastSecond = 0;
        public double steerAngleAB;
        public int lineWidth, numGuideLines;

        //design
        public vec2 desPtA = new vec2(0.2, 0.15);
        public vec2 desPtB = new vec2(0.3, 0.3);

        public vec2 desLineEndA = new vec2(0.0, 0.0);
        public vec2 desLineEndB = new vec2(999997, 1.0);

        public double desHeading = 0;

        public string desName = "";

        //autosteer errors
        public double pivotDistanceError, pivotDistanceErrorLast, pivotDerivative;

        //derivative counters
        private int counter2;

        public double inty;
        public double pivotErrorTotal;

        //Color tramColor = Color.YellowGreen;

        // Host invertido (FormGPS implementa IABLineHost) — traspaso 2026-07-17.
        // internal (era private): lo lee ABLineDrawExtensions (mismo assembly).
        internal readonly IABLineHost mf;

        public CABLine(IABLineHost _f)
        {
            //constructor
            mf = _f;
            //isOnTramLine = true;
            lineWidth = Properties.Settings.Default.setDisplay_lineWidth;
            abLength = 2000;
            numGuideLines = Properties.Settings.Default.setAS_numGuideLines;
        }

        public void BuildCurrentABLineList(vec3 pivot)
        {
            if (mf.Tracks.Count < mf.TrackIdx || mf.TrackIdx < 0) return;

            CTrk track = mf.Tracks[mf.TrackIdx];

            if (!isABValid || ((mf.SecondsSinceStart - lastSecond) > 0.66 && (!mf.IsBtnAutoSteerOn || mf.Mc.steerSwitchHigh)))
            {
                lastSecond = mf.SecondsSinceStart;

                double dx, dy;

                abHeading = track.heading;

                track.endPtA.easting = track.ptA.easting - (Math.Sin(abHeading) * abLength);
                track.endPtA.northing = track.ptA.northing - (Math.Cos(abHeading) * abLength);

                track.endPtB.easting = track.ptB.easting + (Math.Sin(abHeading) * abLength);
                track.endPtB.northing = track.ptB.northing + (Math.Cos(abHeading) * abLength);

                //move the ABLine over based on the overlap amount set in
                double widthMinusOverlap = mf.Tool.width - mf.Tool.overlap;

                //x2-x1
                dx = track.endPtB.easting - track.endPtA.easting;
                //z2-z1
                dy = track.endPtB.northing - track.endPtA.northing;

                distanceFromRefLine = ((dy * mf.GuidanceLookPos.easting) - (dx * mf.GuidanceLookPos.northing) + (track.endPtB.easting
                                        * track.endPtA.northing) - (track.endPtB.northing * track.endPtA.easting))
                                            / Math.Sqrt((dy * dy) + (dx * dx));

                distanceFromRefLine -= (0.5 * widthMinusOverlap);

                isHeadingSameWay = Math.PI - Math.Abs(Math.Abs(pivot.heading - abHeading) - Math.PI) < glm.PIBy2;

                //if (mf.IsYouTurnTriggered && !isGoingStraightThrough) isHeadingSameWay = !isHeadingSameWay;

                //Which ABLine is the vehicle on, negative is left and positive is right side

                double RefDist = (distanceFromRefLine + (isHeadingSameWay ? mf.Tool.offset : -mf.Tool.offset) - track.nudgeDistance) / widthMinusOverlap;

                if (RefDist < 0) howManyPathsAway = (int)(RefDist - 0.5);
                else howManyPathsAway = (int)(RefDist + 0.5);
            }

            if (!isABValid || howManyPathsAway != lastHowManyPathsAway || (isHeadingSameWay != lastIsHeadingSameWay && mf.Tool.offset != 0))
            {
                isABValid = true;
                lastHowManyPathsAway = howManyPathsAway;
                lastIsHeadingSameWay = isHeadingSameWay;

                double widthMinusOverlap = mf.Tool.width - mf.Tool.overlap;

                double distAway = widthMinusOverlap * howManyPathsAway + (isHeadingSameWay ? -mf.Tool.offset : mf.Tool.offset) + track.nudgeDistance;

                distAway += (0.5 * widthMinusOverlap);

                //move the curline as well. 
                vec2 nudgePtA = new vec2(track.ptA);
                vec2 nudgePtB = new vec2(track.ptB);

                //depending which way you are going, the offset can be either side
                vec2 point1 = new vec2((Math.Cos(-abHeading) * distAway) + nudgePtA.easting, (Math.Sin(-abHeading) * distAway) + nudgePtA.northing);

                vec2 point2 = new vec2((Math.Cos(-abHeading) * distAway) + nudgePtB.easting, (Math.Sin(-abHeading) * distAway) + nudgePtB.northing);

                //create the new line extent points for current ABLine based on original heading of AB line
                currentLinePtA.easting = point1.easting - (Math.Sin(abHeading) * abLength);
                currentLinePtA.northing = point1.northing - (Math.Cos(abHeading) * abLength);

                currentLinePtB.easting = point2.easting + (Math.Sin(abHeading) * abLength);
                currentLinePtB.northing = point2.northing + (Math.Cos(abHeading) * abLength);

                currentLinePtA.heading = abHeading;
                currentLinePtB.heading = abHeading;
            }
        }

        public void GetCurrentABLine(vec3 pivot, vec3 steer)
        {
            double dx, dy;

            //Check uturn first
            if (mf.IsYouTurnTriggered && mf.YouTurnDistanceFromYouTurnLine())//do the pure pursuit from youTurn
            {
                //now substitute what it thinks are AB line values with auto turn values
                steerAngleAB = mf.YouTurnSteerAngle;
                distanceFromCurrentLinePivot = mf.YouTurnDistanceFromCurrentLine;

                goalPointAB = mf.YouTurnGoalPoint;
                radiusPointAB.easting = mf.YouTurnRadiusPoint.easting;
                radiusPointAB.northing = mf.YouTurnRadiusPoint.northing;
                ppRadiusAB = mf.YouTurnPpRadius;

                mf.Vehicle.modeTimeCounter = 0;
                mf.Vehicle.modeActualXTE = (distanceFromCurrentLinePivot);
            }

            //Stanley
            else if (mf.IsStanleyUsed)
                mf.StanleyGuidanceABLine(currentLinePtA, currentLinePtB, pivot, steer);

            //Pure Pursuit
            else
            {
                //get the distance from currently active AB line
                //x2-x1
                dx = currentLinePtB.easting - currentLinePtA.easting;
                //z2-z1
                dy = currentLinePtB.northing - currentLinePtA.northing;

                //how far from current AB Line is fix
                distanceFromCurrentLinePivot = ((dy * pivot.easting) - (dx * pivot.northing) + (currentLinePtB.easting
                            * currentLinePtA.northing) - (currentLinePtB.northing * currentLinePtA.easting))
                            / Math.Sqrt((dy * dy) + (dx * dx));

                //integral slider is set to 0
                if (mf.Vehicle.purePursuitIntegralGain != 0 && !mf.IsReverse)
                {
                    pivotDistanceError = distanceFromCurrentLinePivot * 0.2 + pivotDistanceError * 0.8;

                    if (counter2++ > 4)
                    {
                        pivotDerivative = pivotDistanceError - pivotDistanceErrorLast;
                        pivotDistanceErrorLast = pivotDistanceError;
                        counter2 = 0;
                        pivotDerivative *= 2;

                        //limit the derivative
                        //if (pivotDerivative > 0.03) pivotDerivative = 0.03;
                        //if (pivotDerivative < -0.03) pivotDerivative = -0.03;
                        //if (Math.Abs(pivotDerivative) < 0.01) pivotDerivative = 0;
                    }

                    //pivotErrorTotal = pivotDistanceError + pivotDerivative;

                    if (mf.IsBtnAutoSteerOn
                        && Math.Abs(pivotDerivative) < (0.1)
                        && mf.AvgSpeed > 2.5
                        && !mf.IsYouTurnTriggered)
                    //&& Math.Abs(pivotDistanceError) < 0.2)

                    {
                        //if over the line heading wrong way, rapidly decrease integral
                        if ((inty < 0 && distanceFromCurrentLinePivot < 0) || (inty > 0 && distanceFromCurrentLinePivot > 0))
                        {
                            inty += pivotDistanceError * mf.Vehicle.purePursuitIntegralGain * -0.04;
                        }
                        else
                        {
                            if (Math.Abs(distanceFromCurrentLinePivot) > 0.02)
                            {
                                inty += pivotDistanceError * mf.Vehicle.purePursuitIntegralGain * -0.02;
                                if (inty > 0.2) inty = 0.2;
                                else if (inty < -0.2) inty = -0.2;
                            }
                        }
                    }
                    else inty *= 0.95;
                }
                else inty = 0;

                // ** Pure pursuit ** - calc point on ABLine closest to current position
                double U = (((pivot.easting - currentLinePtA.easting) * dx)
                            + ((pivot.northing - currentLinePtA.northing) * dy))
                            / ((dx * dx) + (dy * dy));

                //point on AB line closest to pivot axle point
                rEastAB = currentLinePtA.easting + (U * dx);
                rNorthAB = currentLinePtA.northing + (U * dy);

                //update base on autosteer settings and distance from line
                double goalPointDistance = mf.Vehicle.UpdateGoalPointDistance();

                if (mf.IsReverse ^ isHeadingSameWay)
                {
                    goalPointAB.easting = rEastAB + (Math.Sin(abHeading) * goalPointDistance);
                    goalPointAB.northing = rNorthAB + (Math.Cos(abHeading) * goalPointDistance);
                }
                else
                {
                    goalPointAB.easting = rEastAB - (Math.Sin(abHeading) * goalPointDistance);
                    goalPointAB.northing = rNorthAB - (Math.Cos(abHeading) * goalPointDistance);
                }

                //calc "D" the distance from pivot axle to lookahead point
                double goalPointDistanceDSquared
                    = glm.DistanceSquared(goalPointAB.northing, goalPointAB.easting, pivot.northing, pivot.easting);

                //calculate the the new x in local coordinates and steering angle degrees based on wheelbase
                double localHeading;

                if (isHeadingSameWay) localHeading = glm.twoPI - mf.FixHeading + inty;
                else localHeading = glm.twoPI - mf.FixHeading - inty;

                ppRadiusAB = goalPointDistanceDSquared / (2 * (((goalPointAB.easting - pivot.easting) * Math.Cos(localHeading))
                    + ((goalPointAB.northing - pivot.northing) * Math.Sin(localHeading))));

                steerAngleAB = glm.toDegrees(Math.Atan(2 * (((goalPointAB.easting - pivot.easting) * Math.Cos(localHeading))
                    + ((goalPointAB.northing - pivot.northing) * Math.Sin(localHeading))) * mf.Vehicle.VehicleConfig.Wheelbase
                    / goalPointDistanceDSquared));

                if (mf.Ahrs.imuRoll != 88888)
                    steerAngleAB += mf.Ahrs.imuRoll * -mf.SideHillCompFactor;

                //steerAngleAB *= 1.4;

                if (steerAngleAB < -mf.Vehicle.maxSteerAngle) steerAngleAB = -mf.Vehicle.maxSteerAngle;
                if (steerAngleAB > mf.Vehicle.maxSteerAngle) steerAngleAB = mf.Vehicle.maxSteerAngle;

                //limit circle size for display purpose
                if (ppRadiusAB < -500) ppRadiusAB = -500;
                if (ppRadiusAB > 500) ppRadiusAB = 500;

                radiusPointAB.easting = pivot.easting + (ppRadiusAB * Math.Cos(localHeading));
                radiusPointAB.northing = pivot.northing + (ppRadiusAB * Math.Sin(localHeading));

                //if (mf.isConstantContourOn)
                //{
                //    //angular velocity in rads/sec  = 2PI * m/sec * radians/meters

                //    //clamp the steering angle to not exceed safe angular velocity
                //    if (Math.Abs(mf.setAngVel) > 1000)
                //    {
                //        //mf.setAngVel = mf.setAngVel < 0 ? -mf.Vehicle.maxAngularVelocity : mf.Vehicle.maxAngularVelocity;
                //        mf.setAngVel = mf.setAngVel < 0 ? -1000 : 1000;
                //    }
                //}

                //distance is negative if on left, positive if on right
                if (!isHeadingSameWay)
                    distanceFromCurrentLinePivot *= -1.0;

                //used for acquire/hold mode
                mf.Vehicle.modeActualXTE = (distanceFromCurrentLinePivot);

                double steerHeadingError = (pivot.heading - abHeading);
                //Fix the circular error
                if (steerHeadingError > Math.PI)
                    steerHeadingError -= Math.PI;
                else if (steerHeadingError < -Math.PI)
                    steerHeadingError += Math.PI;

                if (steerHeadingError > glm.PIBy2)
                    steerHeadingError -= Math.PI;
                else if (steerHeadingError < -glm.PIBy2)
                    steerHeadingError += Math.PI;

                mf.Vehicle.modeActualHeadingError = glm.toDegrees(steerHeadingError);

                //Convert to millimeters
                mf.GuidanceLineDistanceOff = (short)Math.Round(distanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
                mf.GuidanceLineSteerAngle = (short)(steerAngleAB * 100);
            }

            //mf.setAngVel = 0.277777 * mf.AvgSpeed * (Math.Tan(glm.toRadians(steerAngleAB))) / mf.Vehicle.wheelbase;
            //mf.setAngVel = glm.toDegrees(mf.setAngVel);
        }

        //DrawABLineNew() y DrawABLines() se movieron a ABLineDrawExtensions
        //(DrawLib/GuidanceDrawExtensions.cs): eran el único uso de GLW acá
        //(traspaso portabilidad).

        public void BuildTram()
        {
            if (mf.Tram.generateMode.IncludesBoundaryTracks())
            {
                mf.Tram.BuildTramBnd();
            }
            else
            {
                mf.Tram.tramBndOuterArr?.Clear();
                mf.Tram.tramBndInnerArr?.Clear();
            }

            mf.Tram.tramList?.Clear();
            mf.Tram.tramArr?.Clear();

            if (!mf.Tram.generateMode.IncludesFillTracks())
            {
                return;
            }

            List<vec2> tramRef = new List<vec2>();

            bool isBndExist = mf.Bnd.bndList.Count != 0;

            abHeading = mf.Tracks[mf.TrackIdx].heading;

            double hsin = Math.Sin(abHeading);
            double hcos = Math.Cos(abHeading);

            double len = glm.Distance(mf.Tracks[mf.TrackIdx].endPtA, mf.Tracks[mf.TrackIdx].endPtB);
            //divide up the AB line into segments
            vec2 P1 = new vec2();
            for (int i = 0; i < (int)len; i += 4)
            {
                P1.easting = (hsin * i) + mf.Tracks[mf.TrackIdx].endPtA.easting;
                P1.northing = (hcos * i) + mf.Tracks[mf.TrackIdx].endPtA.northing;
                tramRef.Add(P1);
            }

            //create list of list of points of triangle strip of AB Highlight
            double headingCalc = abHeading + glm.PIBy2;

            hsin = Math.Sin(headingCalc);
            hcos = Math.Cos(headingCalc);

            mf.Tram.tramList?.Clear();
            mf.Tram.tramArr?.Clear();

            //no boundary starts on first pass
            bool skipFirstPass = isBndExist && mf.Tram.generateMode.IncludesBoundaryTracks();
            int startPass = skipFirstPass ? 1 : 0;

            double widd;
            for (int i = startPass; i < mf.Tram.passes; i++)
            {
                mf.Tram.tramArr = new List<vec2>
                {
                    Capacity = 128
                };

                mf.Tram.tramList.Add(mf.Tram.tramArr);

                widd = (mf.Tram.tramWidth * 0.5) - mf.Tram.halfWheelTrack;
                widd += (mf.Tram.tramWidth * i);

                for (int j = 0; j < tramRef.Count; j++)
                {
                    P1.easting = hsin * widd + tramRef[j].easting;
                    P1.northing = (hcos * widd) + tramRef[j].northing;

                    if (!isBndExist || mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(P1))
                    {
                        mf.Tram.tramArr.Add(P1);
                    }
                }
            }

            for (int i = startPass; i < mf.Tram.passes; i++)
            {
                mf.Tram.tramArr = new List<vec2>
                {
                    Capacity = 128
                };

                mf.Tram.tramList.Add(mf.Tram.tramArr);

                widd = (mf.Tram.tramWidth * 0.5) + mf.Tram.halfWheelTrack;
                widd += (mf.Tram.tramWidth * i);

                for (int j = 0; j < tramRef.Count; j++)
                {
                    P1.easting = (hsin * widd) + tramRef[j].easting;
                    P1.northing = (hcos * widd) + tramRef[j].northing;

                    if (!isBndExist || mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(P1))
                    {
                        mf.Tram.tramArr.Add(P1);
                    }
                }
            }

            tramRef?.Clear();
            //outside tram

            if (mf.Bnd.bndList.Count == 0 || mf.Tram.passes != 0)
            {
                //return;
            }
        }
    }
}