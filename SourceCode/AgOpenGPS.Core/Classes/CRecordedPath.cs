using AgOpenGPS.Core.Models;
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public class CRecordedPath
    {
        //constructor
        public CRecordedPath(IRecordedPathHost _f)
        {
            mf = _f;
        }

        //pointers to mainform controls
        private readonly IRecordedPathHost mf;

        //the recorded path from driving around
        public List<CRecPathPt> recList = new List<CRecPathPt>();

        //the dubins path to get there
        public List<CRecPathPt> shuttleDubinsList = new List<CRecPathPt>();

        public int shuttleListCount;

        //list of vec3 points of Dubins shortest path between 2 points - To be converted to RecPt
        public List<vec3> shortestDubinsList = new List<vec3>();

        //generated reference line
        public double distanceFromCurrentLinePivot;
        private int A, B, C;

        public int currentPositonIndex;

        //pure pursuit values
        public vec3 pivotAxlePosRP = new vec3(0, 0, 0);

        public vec3 homePos = new vec3();
        public vec2 goalPointRP = new vec2(0, 0);
        public double steerAngleRP, rEastRP, rNorthRP, ppRadiusRP;
        public vec2 radiusPointRP = new vec2(0, 0);

        public bool isEndOfTheRecLine, isRecordOn;
        public bool isDrivingRecordedPath, isFollowingDubinsToPath, isFollowingRecPath, isFollowingDubinsHome;

        public double pivotDistanceError, pivotDistanceErrorLast, pivotDerivative;

        //derivative counters
        private int counter2;

        public double inty;
        public double pivotErrorTotal;

        public int resumeState;

        private int starPathIndx = 0;

        public bool StartDrivingRecordedPath()
        {
            //create the dubins path based on start and goal to start of recorded path
            A = B = C = 0;

            if (recList.Count < 5) return false;

            //save a copy of where we started.
            homePos = mf.PivotAxlePos;

            // Try to find the nearest point of the recordet path in relation to the current position:
            double distance = double.MaxValue;
            int idx = 0;
            int i = 0;

            if (resumeState == 0) //start at the start
            {
                currentPositonIndex = 0;
                idx = 0;
                starPathIndx = 0;
            }
            else if (resumeState == 1) //resume from where stopped mid path
            {
                if (currentPositonIndex + 5 > recList.Count)
                {
                    currentPositonIndex = 0;
                    idx = 0;
                    starPathIndx = 0;
                }
                else
                {
                    idx = starPathIndx = currentPositonIndex;
                }
            }
            else //find closest point
            {
                foreach (CRecPathPt pt in recList)
                {
                    double temp = ((pt.easting - homePos.easting) * (pt.easting - homePos.easting))
                        + ((pt.northing - homePos.northing) * (pt.northing - homePos.northing));

                    if (temp < distance)
                    {
                        distance = temp;
                        idx = i;
                    }
                    i++;
                }

                //scootch down the line a bit
                if (idx + 5 < recList.Count) idx += 5;
                else idx = recList.Count - 1;

                starPathIndx = currentPositonIndex = idx;
            }

            //the goal is the first point of path, the start is the current position
            vec3 goal = new vec3(recList[idx].easting, recList[idx].northing, recList[idx].heading);

            //get the dubins for approach to recorded path
            GetDubinsPath(goal);
            shuttleListCount = shuttleDubinsList.Count;

            //has a valid dubins path been created?
            if (shuttleListCount == 0) return false;

            //starPathIndx = idxFieldSelected;

            //technically all good if we get here so set all the flags
            isFollowingDubinsHome = false;
            isFollowingRecPath = false;
            isFollowingDubinsToPath = true;
            isEndOfTheRecLine = false;
            //currentPositonIndex = 0;
            isDrivingRecordedPath = true;
            return true;
        }

        public bool trig;
        public double north;
        public int pathCount = 0;

        public void UpdatePosition()
        {
            if (isFollowingDubinsToPath)
            {
                //set a speed of 10 kmh
                mf.SimStepDistance = shuttleDubinsList[C].speed / 50;

                pivotAxlePosRP = mf.PivotAxlePos;

                //StanleyDubinsPath(shuttleListCount);
                PurePursuitDubins(shuttleListCount);

                //check if close to recorded path
                int cnt = shuttleDubinsList.Count;
                pathCount = cnt - B;
                if (pathCount < 8)
                {
                    double distSqr = glm.DistanceSquared(pivotAxlePosRP.northing, pivotAxlePosRP.easting, recList[starPathIndx].northing, recList[starPathIndx].easting);
                    if (distSqr < 2)
                    {
                        isFollowingRecPath = true;
                        isFollowingDubinsToPath = false;
                        shuttleDubinsList.Clear();
                        shortestDubinsList.Clear();
                        C = starPathIndx;
                        A = C + 3;
                        B = A + 1;
                    }
                }
            }

            if (isFollowingRecPath)
            {
                pivotAxlePosRP = mf.PivotAxlePos;

                //StanleyRecPath(recListCount);
                PurePursuitRecPath(recList.Count);

                //if end of the line then stop
                if (!isEndOfTheRecLine)
                {
                    mf.SimStepDistance = recList[C].speed / 34.86;
                    north = recList[C].northing;

                    pathCount = recList.Count - C;

                    //section control - only if different click the button
                    bool autoBtn = (mf.AutoBtnState == btnStates.Auto);
                    trig = autoBtn;
                    if (autoBtn != recList[C].autoBtnState) mf.ClickSectionMasterAuto();
                }
                else
                {
                    StopDrivingRecordedPath();
                    return;

                    //create the dubins path based on start and goal to start trip home
                    //GetDubinsPath(homePos);
                    //shuttleListCount = shuttleDubinsList.Count;

                    ////its too small
                    //if (shuttleListCount < 3)
                    //{
                    //    StopDrivingRecordedPath();
                    //    return;
                    //}

                    ////set all the flags
                    //isFollowingDubinsHome = true;
                    //A = B = C = 0;
                    //isFollowingRecPath = false;
                    //isFollowingDubinsToPath = false;
                    //isEndOfTheRecLine = false;
                }
            }

            if (isFollowingDubinsHome)
            {
                int cnt = shuttleDubinsList.Count;
                pathCount = cnt - B;
                if (pathCount < 3)
                {
                    StopDrivingRecordedPath();
                    return;
                }

                mf.SimStepDistance = shuttleDubinsList[C].speed / 35;
                pivotAxlePosRP = mf.PivotAxlePos;

                //StanleyDubinsPath(shuttleListCount);
                PurePursuitDubins(shuttleListCount);
            }
        }

        public void StopDrivingRecordedPath()
        {
            isFollowingDubinsHome = false;
            isFollowingRecPath = false;
            isFollowingDubinsToPath = false;
            shuttleDubinsList.Clear();
            shortestDubinsList.Clear();
            mf.SimStepDistance = 0;
            isDrivingRecordedPath = false;
            mf.OnRecordedPathStopped();
        }

        private void GetDubinsPath(vec3 goal)
        {
            CDubins.turningRadius = mf.YouTurn.youTurnRadius * 1.2;
            CDubins dubPath = new CDubins();

            // current psition
            pivotAxlePosRP = mf.PivotAxlePos;

            //bump it forward
            vec3 pt2 = new vec3
            {
                easting = pivotAxlePosRP.easting + (Math.Sin(pivotAxlePosRP.heading) * 3),
                northing = pivotAxlePosRP.northing + (Math.Cos(pivotAxlePosRP.heading) * 3),
                heading = pivotAxlePosRP.heading
            };

            //get the dubins path vec3 point coordinates of turn
            shortestDubinsList.Clear();
            shuttleDubinsList.Clear();

            shortestDubinsList = dubPath.GenerateDubins(pt2, goal);

            //if Dubins returns 0 elements, there is an unavoidable blockage in the way.
            if (shortestDubinsList.Count > 0)
            {
                shortestDubinsList.Insert(0, mf.PivotAxlePos);

                //transfer point list to recPath class point style
                for (int i = 0; i < shortestDubinsList.Count; i++)
                {
                    CRecPathPt pt = new CRecPathPt(shortestDubinsList[i].easting, shortestDubinsList[i].northing, shortestDubinsList[i].heading, 9.0, false);
                    shuttleDubinsList.Add(pt);
                }
                return;
            }
        }

        private void PurePursuitRecPath(int ptCount)
        {
            //find the closest 2 points to current fix
            double minDistA = 9999999999;
            double dist, dx, dz;

            //set the search range close to current position
            int top = currentPositonIndex + 5;
            if (top > ptCount) top = ptCount;

            for (int t = currentPositonIndex; t < top; t++)
            {
                dist = ((pivotAxlePosRP.easting - recList[t].easting) * (pivotAxlePosRP.easting - recList[t].easting))
                                + ((pivotAxlePosRP.northing - recList[t].northing) * (pivotAxlePosRP.northing - recList[t].northing));
                if (dist < minDistA)
                {
                    minDistA = dist;
                    A = t;
                }
            }

            //Save the closest point
            C = A;

            //next point is the next in list
            B = A + 1;
            if (B == ptCount)
            {
                //don't go past the end of the list - "end of the line" trigger
                A--;
                B--;
                isEndOfTheRecLine = true;
            }

            //save current position
            currentPositonIndex = A;

            //get the distance from currently active AB line
            dx = recList[B].easting - recList[A].easting;
            dz = recList[B].northing - recList[A].northing;

            if (Math.Abs(dx) < Double.Epsilon && Math.Abs(dz) < Double.Epsilon) return;

            //how far from current AB Line is fix
            distanceFromCurrentLinePivot = ((dz * pivotAxlePosRP.easting) - (dx * pivotAxlePosRP.northing) + (recList[B].easting
                        * recList[A].northing) - (recList[B].northing * recList[A].easting))
                            / Math.Sqrt((dz * dz) + (dx * dx));

            //integral slider is set to 0
            if (mf.Vehicle.purePursuitIntegralGain != 0)
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

                if (isFollowingRecPath
                    && Math.Abs(pivotDerivative) < (0.1)
                    && mf.AvgSpeed > 2.5)
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

            if (mf.IsReverse) inty = 0;

            // ** Pure pursuit ** - calc point on ABLine closest to current position
            double U = (((pivotAxlePosRP.easting - recList[A].easting) * dx)
                        + ((pivotAxlePosRP.northing - recList[A].northing) * dz))
                        / ((dx * dx) + (dz * dz));

            rEastRP = recList[A].easting + (U * dx);
            rNorthRP = recList[A].northing + (U * dz);

            //update base on autosteer settings and distance from line
            double goalPointDistance = mf.Vehicle.UpdateGoalPointDistance();

            bool ReverseHeading = !mf.IsReverse;

            int count = ReverseHeading ? 1 : -1;
            CRecPathPt start = new CRecPathPt(rEastRP, rNorthRP, 0, 0, false);
            double distSoFar = 0;

            for (int i = ReverseHeading ? B : A; i < ptCount && i >= 0; i += count)
            {
                // used for calculating the length squared of next segment.
                double tempDist = Math.Sqrt((start.easting - recList[i].easting) * (start.easting - recList[i].easting)
                    + (start.northing - recList[i].northing) * (start.northing - recList[i].northing));

                //will we go too far?
                if ((tempDist + distSoFar) > goalPointDistance)
                {
                    double j = (goalPointDistance - distSoFar) / tempDist; // the remainder to yet travel

                    goalPointRP.easting = (((1 - j) * start.easting) + (j * recList[i].easting));
                    goalPointRP.northing = (((1 - j) * start.northing) + (j * recList[i].northing));
                    break;
                }
                else distSoFar += tempDist;
                start = recList[i];
            }

            //calc "D" the distance from pivotAxlePosRP axle to lookahead point
            double goalPointDistanceSquared = glm.DistanceSquared(goalPointRP.northing, goalPointRP.easting, pivotAxlePosRP.northing, pivotAxlePosRP.easting);

            //calculate the the delta x in local coordinates and steering angle degrees based on wheelbase
            double localHeading = glm.twoPI - mf.FixHeading + inty;

            ppRadiusRP = goalPointDistanceSquared / (2 * (((goalPointRP.easting - pivotAxlePosRP.easting) * Math.Cos(localHeading)) + ((goalPointRP.northing - pivotAxlePosRP.northing) * Math.Sin(localHeading))));

            steerAngleRP = glm.toDegrees(Math.Atan(2 * (((goalPointRP.easting - pivotAxlePosRP.easting) * Math.Cos(localHeading))
                + ((goalPointRP.northing - pivotAxlePosRP.northing) * Math.Sin(localHeading))) * mf.Vehicle.VehicleConfig.Wheelbase / goalPointDistanceSquared));

            if (steerAngleRP < -mf.Vehicle.maxSteerAngle) steerAngleRP = -mf.Vehicle.maxSteerAngle;
            if (steerAngleRP > mf.Vehicle.maxSteerAngle) steerAngleRP = mf.Vehicle.maxSteerAngle;

            //used for smooth mode
            mf.Vehicle.modeActualXTE = (distanceFromCurrentLinePivot);

            //Convert to centimeters
            mf.GuidanceLineDistanceOff = (short)Math.Round(distanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
            mf.GuidanceLineSteerAngle = (short)(steerAngleRP * 100);
        }

        private void PurePursuitDubins(int ptCount)
        {
            double dist, dx, dz;
            double minDistA = 1000000, minDistB = 1000000;

            //find the closest 2 points to current fix
            for (int t = 0; t < ptCount; t++)
            {
                dist = ((pivotAxlePosRP.easting - shuttleDubinsList[t].easting) * (pivotAxlePosRP.easting - shuttleDubinsList[t].easting))
                                + ((pivotAxlePosRP.northing - shuttleDubinsList[t].northing) * (pivotAxlePosRP.northing - shuttleDubinsList[t].northing));
                if (dist < minDistA)
                {
                    minDistB = minDistA;
                    B = A;
                    minDistA = dist;
                    A = t;
                }
                else if (dist < minDistB)
                {
                    minDistB = dist;
                    B = t;
                }
            }

            //just need to make sure the points continue ascending or heading switches all over the place
            if (A > B) { C = A; A = B; B = C; }

            //currentLocationIndex = A;

            //get the distance from currently active AB line
            dx = shuttleDubinsList[B].easting - shuttleDubinsList[A].easting;
            dz = shuttleDubinsList[B].northing - shuttleDubinsList[A].northing;

            if (Math.Abs(dx) < Double.Epsilon && Math.Abs(dz) < Double.Epsilon) return;

            //abHeading = Math.Atan2(dz, dx);

            //how far from current AB Line is fix
            distanceFromCurrentLinePivot = ((dz * pivotAxlePosRP.easting) - (dx * pivotAxlePosRP.northing) + (shuttleDubinsList[B].easting
                        * shuttleDubinsList[A].northing) - (shuttleDubinsList[B].northing * shuttleDubinsList[A].easting))
                            / Math.Sqrt((dz * dz) + (dx * dx));

            //integral slider is set to 0
            if (mf.Vehicle.purePursuitIntegralGain != 0)
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
                    && !mf.YouTurn.isYouTurnTriggered)

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

            if (mf.IsReverse) inty = 0;

            // ** Pure pursuit ** - calc point on ABLine closest to current position
            double U = (((pivotAxlePosRP.easting - shuttleDubinsList[A].easting) * dx)
                        + ((pivotAxlePosRP.northing - shuttleDubinsList[A].northing) * dz))
                        / ((dx * dx) + (dz * dz));

            rEastRP = shuttleDubinsList[A].easting + (U * dx);
            rNorthRP = shuttleDubinsList[A].northing + (U * dz);

            //update base on autosteer settings and distance from line
            double goalPointDistance = mf.Vehicle.UpdateGoalPointDistance();

            bool ReverseHeading = !mf.IsReverse;

            int count = ReverseHeading ? 1 : -1;
            CRecPathPt start = new CRecPathPt(rEastRP, rNorthRP, 0, 0, false);
            double distSoFar = 0;

            for (int i = ReverseHeading ? B : A; i < ptCount && i >= 0; i += count)
            {
                // used for calculating the length squared of next segment.
                double tempDist = Math.Sqrt((start.easting - shuttleDubinsList[i].easting) * (start.easting - shuttleDubinsList[i].easting)
                    + (start.northing - shuttleDubinsList[i].northing) * (start.northing - shuttleDubinsList[i].northing));

                //will we go too far?
                if ((tempDist + distSoFar) > goalPointDistance)
                {
                    double j = (goalPointDistance - distSoFar) / tempDist; // the remainder to yet travel

                    goalPointRP.easting = (((1 - j) * start.easting) + (j * shuttleDubinsList[i].easting));
                    goalPointRP.northing = (((1 - j) * start.northing) + (j * shuttleDubinsList[i].northing));
                    break;
                }
                else distSoFar += tempDist;
                start = shuttleDubinsList[i];
            }

            //calc "D" the distance from pivotAxlePosRP axle to lookahead point
            double goalPointDistanceSquared = glm.DistanceSquared(goalPointRP.northing, goalPointRP.easting, pivotAxlePosRP.northing, pivotAxlePosRP.easting);

            //calculate the the delta x in local coordinates and steering angle degrees based on wheelbase
            //double localHeading = glm.twoPI - mf.FixHeading;

            double localHeading = glm.twoPI - mf.FixHeading + inty;

            ppRadiusRP = goalPointDistanceSquared / (2 * (((goalPointRP.easting - pivotAxlePosRP.easting) * Math.Cos(localHeading)) + ((goalPointRP.northing - pivotAxlePosRP.northing) * Math.Sin(localHeading))));

            steerAngleRP = glm.toDegrees(Math.Atan(2 * (((goalPointRP.easting - pivotAxlePosRP.easting) * Math.Cos(localHeading))
                + ((goalPointRP.northing - pivotAxlePosRP.northing) * Math.Sin(localHeading))) * mf.Vehicle.VehicleConfig.Wheelbase / goalPointDistanceSquared));

            if (steerAngleRP < -mf.Vehicle.maxSteerAngle) steerAngleRP = -mf.Vehicle.maxSteerAngle;
            if (steerAngleRP > mf.Vehicle.maxSteerAngle) steerAngleRP = mf.Vehicle.maxSteerAngle;

            if (ppRadiusRP < -500) ppRadiusRP = -500;
            if (ppRadiusRP > 500) ppRadiusRP = 500;

            radiusPointRP.easting = pivotAxlePosRP.easting + (ppRadiusRP * Math.Cos(localHeading));
            radiusPointRP.northing = pivotAxlePosRP.northing + (ppRadiusRP * Math.Sin(localHeading));

            //angular velocity in rads/sec  = 2PI * m/sec * radians/meters
            // double angVel = glm.twoPI * 0.277777 * mf.pn.speed * (Math.Tan(glm.toRadians(steerAngleRP))) / mf.Vehicle.wheelbase;

            //clamp the steering angle to not exceed safe angular velocity
            //if (Math.Abs(angVel) > mf.Vehicle.maxAngularVelocity)
            //{
            //    steerAngleRP = glm.toDegrees(steerAngleRP > 0 ?
            //            (Math.Atan((mf.Vehicle.wheelbase * mf.Vehicle.maxAngularVelocity) / (glm.twoPI * mf.AvgSpeed * 0.277777)))
            //        : (Math.Atan((mf.Vehicle.wheelbase * -mf.Vehicle.maxAngularVelocity) / (glm.twoPI * mf.AvgSpeed * 0.277777))));
            //}

            //Convert to centimeters
            mf.GuidanceLineDistanceOff = (short)Math.Round(distanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
            mf.GuidanceLineSteerAngle = (short)(steerAngleRP * 100);
        }

        //DrawRecordedLine() y DrawDubins() se movieron a RecordedPathDrawExtensions
        //(DrawLib/GuidanceDrawExtensions.cs): eran el único uso de OpenTK acá
        //(traspaso portabilidad).
    }
}

//private void StanleyDubinsPath(int ptCount)
//{
//    //distanceFromCurrentLine = 9999;
//    //find the closest 2 points to current fix
//    double minDistA = 9999999999;
//    for (int t = 0; t < ptCount; t++)
//    {
//        double dist = ((pivotAxlePosRP.easting - shuttleDubinsList[t].easting) * (pivotAxlePosRP.easting - shuttleDubinsList[t].easting))
//                        + ((pivotAxlePosRP.northing - shuttleDubinsList[t].northing) * (pivotAxlePosRP.northing - shuttleDubinsList[t].northing));
//        if (dist < minDistA)
//        {
//            minDistA = dist;
//            A = t;
//        }
//    }

//    //save the closest point
//    C = A;
//    //next point is the next in list
//    B = A + 1;
//    if (B == ptCount) { A--; B--; }                //don't go past the end of the list - "end of the line" trigger

//    //get the distance from currently active AB line
//    //x2-x1
//    double dx = shuttleDubinsList[B].easting - shuttleDubinsList[A].easting;
//    //z2-z1
//    double dz = shuttleDubinsList[B].northing - shuttleDubinsList[A].northing;

//    if (Math.Abs(dx) < Double.Epsilon && Math.Abs(dz) < Double.Epsilon) return;

//    //abHeading = Math.Atan2(dz, dx);
//    abHeading = shuttleDubinsList[A].heading;

//    //how far from current AB Line is fix
//    distanceFromCurrentLinePivot = ((dz * pivotAxlePosRP.easting) - (dx * pivotAxlePosRP
//        .northing) + (shuttleDubinsList[B].easting
//                * shuttleDubinsList[A].northing) - (shuttleDubinsList[B].northing * shuttleDubinsList[A].easting))
//                    / Math.Sqrt((dz * dz) + (dx * dx));

//    //are we on the right side or not
//    isOnRightSideCurrentLine = distanceFromCurrentLinePivot > 0;

//    // calc point on ABLine closest to current position
//    double U = (((pivotAxlePosRP.easting - shuttleDubinsList[A].easting) * dx)
//                + ((pivotAxlePosRP.northing - shuttleDubinsList[A].northing) * dz))
//                / ((dx * dx) + (dz * dz));

//    rEastRP = shuttleDubinsList[A].easting + (U * dx);
//    rNorthRP = shuttleDubinsList[A].northing + (U * dz);

//    //the first part of stanley is to extract heading error
//    double abFixHeadingDelta = (pivotAxlePosRP.heading - abHeading);

//    //Fix the circular error - get it from -Pi/2 to Pi/2
//    if (abFixHeadingDelta > Math.PI) abFixHeadingDelta -= Math.PI;
//    else if (abFixHeadingDelta < Math.PI) abFixHeadingDelta += Math.PI;
//    if (abFixHeadingDelta > glm.PIBy2) abFixHeadingDelta -= Math.PI;
//    else if (abFixHeadingDelta < -glm.PIBy2) abFixHeadingDelta += Math.PI;

//    //normally set to 1, less then unity gives less heading error.
//    abFixHeadingDelta *= mf.Vehicle.stanleyHeadingErrorGain;
//    if (abFixHeadingDelta > 0.74) abFixHeadingDelta = 0.74;
//    if (abFixHeadingDelta < -0.74) abFixHeadingDelta = -0.74;

//    //the non linear distance error part of stanley
//    steerAngleRP = Math.Atan((distanceFromCurrentLinePivot * mf.Vehicle.stanleyDistanceErrorGain) / ((mf.pn.speed * 0.277777) + 1));

//    //clamp it to max 42 degrees
//    if (steerAngleRP > 0.74) steerAngleRP = 0.74;
//    if (steerAngleRP < -0.74) steerAngleRP = -0.74;

//    //add them up and clamp to max in vehicle settings
//    steerAngleRP = glm.toDegrees((steerAngleRP + abFixHeadingDelta) * -1.0);
//    if (steerAngleRP < -mf.Vehicle.maxSteerAngle) steerAngleRP = -mf.Vehicle.maxSteerAngle;
//    if (steerAngleRP > mf.Vehicle.maxSteerAngle) steerAngleRP = mf.Vehicle.maxSteerAngle;

//    //Convert to millimeters and round properly to above/below .5
//    distanceFromCurrentLinePivot = Math.Round(distanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);

//    //every guidance method dumps into these that are used and sent everywhere, last one wins
//    mf.GuidanceLineDistanceOff = mf.distanceDisplaySteer = (Int16)distanceFromCurrentLinePivot;
//    mf.GuidanceLineSteerAngle = (Int16)(steerAngleRP * 100);
//}
//private void StanleyRecPath(int ptCount)
//{
//    //find the closest 2 points to current fix
//    double minDistA = 9999999999;

//    //set the search range close to current position
//    int top = currentPositonIndex + 5;
//    if (top > ptCount) top = ptCount;

//    double dist;
//    for (int t = currentPositonIndex; t < top; t++)
//    {
//        dist = ((pivotAxlePosRP.easting - recList[t].easting) * (pivotAxlePosRP.easting - recList[t].easting))
//                        + ((pivotAxlePosRP.northing - recList[t].northing) * (pivotAxlePosRP.northing - recList[t].northing));
//        if (dist < minDistA)
//        {
//            minDistA = dist;
//            A = t;
//        }
//    }

//    //Save the closest point
//    C = A;

//    //next point is the next in list
//    B = A + 1;
//    if (B == ptCount)
//    {
//        //don't go past the end of the list - "end of the line" trigger
//        A--;
//        B--;
//        isEndOfTheRecLine = true;
//    }

//    //save current position
//    currentPositonIndex = A;

//    //get the distance from currently active AB line
//    double dx = recList[B].easting - recList[A].easting;
//    double dz = recList[B].northing - recList[A].northing;

//    if (Math.Abs(dx) < Double.Epsilon && Math.Abs(dz) < Double.Epsilon) return;

//    abHeading = Math.Atan2(dx, dz);
//    //abHeading = recList[A].heading;

//    //how far from current AB Line is fix
//    distanceFromCurrentLinePivot =
//        ((dz * pivotAxlePosRP.easting) - (dx * pivotAxlePosRP.northing) + (recList[B].easting
//                * recList[A].northing) - (recList[B].northing * recList[A].easting))
//                    / Math.Sqrt((dz * dz) + (dx * dx));

//    //are we on the right side or not
//    isOnRightSideCurrentLine = distanceFromCurrentLinePivot > 0;

//    // calc point on ABLine closest to current position
//    double U = (((pivotAxlePosRP.easting - recList[A].easting) * dx)
//                + ((pivotAxlePosRP.northing - recList[A].northing) * dz))
//                / ((dx * dx) + (dz * dz));

//    rEastRP = recList[A].easting + (U * dx);
//    rNorthRP = recList[A].northing + (U * dz);

//    //the first part of stanley is to extract heading error
//    double abFixHeadingDelta = (pivotAxlePosRP.heading - abHeading);

//    //Fix the circular error - get it from -Pi/2 to Pi/2
//    if (abFixHeadingDelta > Math.PI) abFixHeadingDelta -= Math.PI;
//    else if (abFixHeadingDelta < Math.PI) abFixHeadingDelta += Math.PI;
//    if (abFixHeadingDelta > glm.PIBy2) abFixHeadingDelta -= Math.PI;
//    else if (abFixHeadingDelta < -glm.PIBy2) abFixHeadingDelta += Math.PI;

//    //normally set to 1, less then unity gives less heading error.
//    abFixHeadingDelta *= mf.Vehicle.stanleyHeadingErrorGain;
//    if (abFixHeadingDelta > 0.74) abFixHeadingDelta = 0.74;
//    if (abFixHeadingDelta < -0.74) abFixHeadingDelta = -0.74;

//    //the non linear distance error part of stanley
//    steerAngleRP = Math.Atan((distanceFromCurrentLinePivot * mf.Vehicle.stanleyDistanceErrorGain) / ((mf.pn.speed * 0.277777) + 1));

//    //clamp it to max 42 degrees
//    if (steerAngleRP > 0.74) steerAngleRP = 0.74;
//    if (steerAngleRP < -0.74) steerAngleRP = -0.74;

//    //add them up and clamp to max in vehicle settings
//    steerAngleRP = glm.toDegrees((steerAngleRP + abFixHeadingDelta) * -1.0);
//    if (steerAngleRP < -mf.Vehicle.maxSteerAngle) steerAngleRP = -mf.Vehicle.maxSteerAngle;
//    if (steerAngleRP > mf.Vehicle.maxSteerAngle) steerAngleRP = mf.Vehicle.maxSteerAngle;

//    //Convert to millimeters and round properly to above/below .5
//    distanceFromCurrentLinePivot = Math.Round(distanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);

//    //every guidance method dumps into these that are used and sent everywhere, last one wins
//    mf.GuidanceLineDistanceOff = mf.distanceDisplaySteer = (Int16)distanceFromCurrentLinePivot;
//    mf.GuidanceLineSteerAngle = (Int16)(steerAngleRP * 100);
//}