using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Core.Translations;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenGPS
{
    public class CABCurve
    {
        //pointers to mainform controls
        // Host invertido (FormGPS implementa IABCurveHost) — traspaso 2026-07-17
        // internal (era private): lo lee ABCurveDrawExtensions (mismo assembly)
        internal readonly IABCurveHost mf;

        //flag for starting stop adding points
        public bool isBtnTrackOn, isMakingCurve, isRecordingCurve;

        public double distanceFromCurrentLinePivot;
        public double distanceFromRefLine;

        public bool isHeadingSameWay = true, lastIsHeadingSameWay = true;

        public int howManyPathsAway, lastHowManyPathsAway;
        public vec2 refPoint1 = new vec2(1, 1), refPoint2 = new vec2(2, 2);

        private int A, B, C;
        private int rA, rB;

        public int currentLocationIndex;

        //pure pursuit values
        public vec2 goalPointCu = new vec2(0, 0);

        public vec2 radiusPointCu = new vec2(0, 0);
        public double steerAngleCu, rEastCu, rNorthCu, ppRadiusCu, manualUturnHeading;

        public bool isSmoothWindowOpen;
        public List<vec3> smooList = new List<vec3>();

        //the list of points of curve to drive on
        public List<vec3> curList = new List<vec3>();

        //guidelines
        // era private; lo lee ABCurveDrawExtensions (mismo assembly)
        internal List<List<vec3>> guideArr = new List<List<vec3>>();

        public bool isCurveValid;

        public double lastSecond = 0;

        public List<vec3> desList = new List<vec3>();
        public string desName = "**";

        public double pivotDistanceError, pivotDistanceErrorLast, pivotDerivative;

        //derivative counters
        private int counter2;
        public double inty;

        // Should we find the global nearest curve point (instead of local) on the next search.
        private bool findGlobalNearestCurvePoint = true;
        private CancellationTokenSource cts;
        private Task<List<vec3>> build;
        private Task<List<List<vec3>>> buildList;

        public CABCurve(IABCurveHost _f)
        {
            //constructor
            mf = _f;
            desList.Capacity = 1024;
            curList.Capacity = 1024;
        }

        // Helper to get extended reference curve (200m extension at both ends for calculations only)
        // Uses same logic as AddFirstLastPoints but returns a new list instead of modifying input
        private List<vec3> GetExtendedReferenceCurve(CTrk track)
        {
            if (track == null || track.curvePts == null || track.curvePts.Count == 0)
                return new List<vec3>();

            // For closed loops like boundary curves, don't add extensions
            // Check if first and last points are at the same location (closing the loop)
            if (track.mode == TrackMode.bndCurve || track.curvePts.Count < 2)
            {
                // For boundary curves, return the curve points as-is without extensions
                // The boundary is already a closed loop
                List<vec3> result = new List<vec3>();
                foreach (vec3 pt in track.curvePts)
                {
                    result.Add(new vec3(pt));
                }
                return result;
            }

            List<vec3> extended = new List<vec3>(track.curvePts);
            int ptCnt = track.curvePts.Count - 1;
            vec3 start;

            // Add 200 points to end (going forward using last point's heading)
            for (int i = 1; i < 200; i++)
            {
                vec3 pt = new vec3(track.curvePts[ptCnt]);
                pt.easting += (Math.Sin(pt.heading) * i);
                pt.northing += (Math.Cos(pt.heading) * i);
                extended.Add(pt);
            }

            // Add 200 points to beginning (going backward using first point's heading)
            start = new vec3(track.curvePts[0]);
            for (int i = 1; i < 200; i++)
            {
                vec3 pt = new vec3(start);
                pt.easting -= (Math.Sin(pt.heading) * i);
                pt.northing -= (Math.Cos(pt.heading) * i);
                extended.Insert(0, pt);
            }

            return extended;
        }


        public async void BuildCurveCurrentList(vec3 pivot)
        {
            double minDistA = 1000000, minDistB;

            //move the ABLine over based on the overlap amount set in vehicle
            double widthMinusOverlap = mf.Tool.width - mf.Tool.overlap;

            CTrk track = mf.Tracks[mf.TrackIdx];

            if (!isCurveValid || ((mf.SecondsSinceStart - lastSecond) > 0.66 && (!mf.IsBtnAutoSteerOn || mf.Mc.steerSwitchHigh)))
            {
                lastSecond = mf.SecondsSinceStart;

                if (track.mode != TrackMode.waterPivot)
                {
                    int refCount = track.curvePts.Count;
                    if (refCount < 2)
                    {
                        curList?.Clear();
                        return;
                    }

                    //close call hit
                    int cc = 0, dd;

                    for (int j = 0; j < refCount; j += 10)
                    {
                        double dist = ((mf.GuidanceLookPos.easting - track.curvePts[j].easting)
                            * (mf.GuidanceLookPos.easting - track.curvePts[j].easting))
                                        + ((mf.GuidanceLookPos.northing - track.curvePts[j].northing)
                                        * (mf.GuidanceLookPos.northing - track.curvePts[j].northing));
                        if (dist < minDistA)
                        {
                            minDistA = dist;
                            cc = j;
                        }
                    }

                    minDistA = minDistB = 1000000;

                    dd = cc + 7; if (dd > refCount - 1) dd = refCount;
                    cc -= 7; if (cc < 0) cc = 0;

                    //find the closest 2 points to current close call
                    for (int j = cc; j < dd; j++)
                    {
                        double dist = ((mf.GuidanceLookPos.easting - track.curvePts[j].easting)
                            * (mf.GuidanceLookPos.easting - track.curvePts[j].easting))
                                        + ((mf.GuidanceLookPos.northing - track.curvePts[j].northing)
                                        * (mf.GuidanceLookPos.northing - track.curvePts[j].northing));
                        if (dist < minDistA)
                        {
                            minDistB = minDistA;
                            rB = rA;
                            minDistA = dist;
                            rA = j;
                        }
                        else if (dist < minDistB)
                        {
                            minDistB = dist;
                            rB = j;
                        }
                    }

                    if (rA > rB) { C = rA; rA = rB; rB = C; }

                    //same way as line creation or not
                    isHeadingSameWay = Math.PI - Math.Abs(Math.Abs(pivot.heading - track.curvePts[rA].heading) - Math.PI) < glm.PIBy2;

                    //which side of the closest point are we on is next
                    //calculate endpoints of reference line based on closest point
                    refPoint1.easting = track.curvePts[rA].easting - (Math.Sin(track.curvePts[rA].heading) * 300.0);
                    refPoint1.northing = track.curvePts[rA].northing - (Math.Cos(track.curvePts[rA].heading) * 300.0);

                    refPoint2.easting = track.curvePts[rA].easting + (Math.Sin(track.curvePts[rA].heading) * 300.0);
                    refPoint2.northing = track.curvePts[rA].northing + (Math.Cos(track.curvePts[rA].heading) * 300.0);

                    //x2-x1
                    double dx = refPoint2.easting - refPoint1.easting;
                    //z2-z1
                    double dz = refPoint2.northing - refPoint1.northing;

                    //how far are we away from the reference line at 90 degrees - 2D cross product and distance
                    distanceFromRefLine = ((dz * mf.GuidanceLookPos.easting) - (dx * mf.GuidanceLookPos.northing) + (refPoint2.easting
                                        * refPoint1.northing) - (refPoint2.northing * refPoint1.easting))
                                        / Math.Sqrt((dz * dz) + (dx * dx));
                }
                else //pivot guide list
                {
                    //cross product
                    isHeadingSameWay = ((mf.PivotAxlePos.easting - track.ptA.easting) * (mf.SteerAxlePos.northing - track.ptA.northing)
                        - (mf.PivotAxlePos.northing - track.ptA.northing) * (mf.SteerAxlePos.easting - track.ptA.easting)) < 0;

                    //pivot circle center
                    distanceFromRefLine = -glm.Distance(mf.GuidanceLookPos, track.ptA);
                }

                distanceFromRefLine -= (0.5 * widthMinusOverlap);

                double RefDist = (distanceFromRefLine + (isHeadingSameWay ? mf.Tool.offset : -mf.Tool.offset) - track.nudgeDistance) / widthMinusOverlap;

                if (RefDist < 0) howManyPathsAway = (int)(RefDist - 0.5);
                else howManyPathsAway = (int)(RefDist + 0.5);
            }

            if (!isCurveValid || howManyPathsAway != lastHowManyPathsAway || (isHeadingSameWay != lastIsHeadingSameWay && mf.Tool.offset != 0))
            {
                //is boundary curve - use task
                isCurveValid = true;
                lastHowManyPathsAway = howManyPathsAway;
                lastIsHeadingSameWay = isHeadingSameWay;
                double distAway = widthMinusOverlap * howManyPathsAway + (isHeadingSameWay ? -mf.Tool.offset : mf.Tool.offset) + track.nudgeDistance;

                distAway += (0.5 * widthMinusOverlap);

                cts?.Cancel();
                cts = new CancellationTokenSource();

                if (build != null) await build;

                build = Task.Run(() => BuildNewOffsetList(distAway, track, cts.Token), cts.Token);
                curList = await build;
                findGlobalNearestCurvePoint = true;

                if (mf.IsSideGuideLines && mf.CamSetDistance > mf.Tool.width * -400)
                {
                    if (buildList != null)
                        await buildList;
                    //build the list list of guide lines
                    buildList = Task.Run(() => BuildCurveGuidelines(distAway, mf.ABLine.numGuideLines, track, cts.Token), cts.Token);
                    guideArr = await buildList;
                }
                else
                {
                    if (buildList != null) await buildList;
                    guideArr?.Clear();
                }
            }
        }

        public List<vec3> BuildNewOffsetList(double distAway, CTrk track, CancellationToken ct = default)
        {
            //the list of points of curve new list from async
            List<vec3> newCurList = new List<vec3>();

            try
            {
                if (track.mode == TrackMode.AB)
                {
                    //move the curline as well. 
                    vec2 nudgePtA = new vec2(track.ptA);
                    vec2 nudgePtB = new vec2(track.ptB);

                    //depending which way you are going, the offset can be either side
                    vec2 point1 = new vec2((Math.Cos(-track.heading) * distAway) + nudgePtA.easting,
                    (Math.Sin(-track.heading) * distAway) + nudgePtA.northing);

                    vec2 point2 = new vec2((Math.Cos(-track.heading) * distAway) + nudgePtB.easting,
                    (Math.Sin(-track.heading) * distAway) + nudgePtB.northing);

                    //create the new line extent points for current ABLine based on original heading of AB line
                    double easting1 = point1.easting - (Math.Sin(track.heading) * mf.ABLine.abLength);
                    double northing1 = point1.northing - (Math.Cos(track.heading) * mf.ABLine.abLength);

                    newCurList.Add(new vec3(easting1, northing1, track.heading));

                    double easting2 = point2.easting + (Math.Sin(track.heading) * mf.ABLine.abLength);
                    double northing2 = point2.northing + (Math.Cos(track.heading) * mf.ABLine.abLength);
                    newCurList.Add(new vec3(easting2, northing2, track.heading));
                }
                else if (track.mode == TrackMode.waterPivot)
                {
                    //max 2 cm offset from correct circle or limit to 500 points
                    double Angle = glm.twoPI / Math.Min(Math.Max(Math.Ceiling(glm.twoPI / (2 * Math.Acos(1 - (0.02 / Math.Abs(distAway))))), 50), 500);//limit between 50 and 500 points

                    vec3 centerPos = new vec3(track.ptA.easting, track.ptA.northing, 0);
                    double rotation = 0;

                    while (rotation < glm.twoPI)
                    {
                        //Update the heading
                        rotation += Angle;
                        //Add the new coordinate to the path
                        newCurList.Add(new vec3(centerPos.easting + distAway * Math.Sin(rotation), centerPos.northing + distAway * Math.Cos(rotation), 0));
                    }

                    if (newCurList.Count > 1)
                    {
                        vec3[] arr = new vec3[newCurList.Count];
                        newCurList.CopyTo(arr);
                        newCurList.Clear();

                        for (int i = 0; i < (arr.Length - 1); i++)
                        {
                            arr[i].heading = Math.Atan2(arr[i + 1].easting - arr[i].easting, arr[i + 1].northing - arr[i].northing);
                            if (arr[i].heading < 0) arr[i].heading += glm.twoPI;
                            if (arr[i].heading >= glm.twoPI) arr[i].heading -= glm.twoPI;
                        }

                        arr[arr.Length - 1].heading = Math.Atan2(arr[0].easting - arr[arr.Length - 1].easting, arr[0].northing - arr[arr.Length - 1].northing);

                        newCurList.AddRange(arr);
                    }
                }
                else
                {
                    vec3 point;

                    double step = (mf.Tool.width - mf.Tool.overlap) * 0.48;
                    if (step > 4) step = 4;
                    if (step < 1) step = 1;

                    double distSqAway = (distAway * distAway) - 0.01;

                    int refCount = track.curvePts.Count;
                    for (int i = 0; i < refCount; i++)
                    {
                        if (ct.IsCancellationRequested)
                            break;
                        point = new vec3(
                        track.curvePts[i].easting + (Math.Sin(glm.PIBy2 + track.curvePts[i].heading) * distAway),
                        track.curvePts[i].northing + (Math.Cos(glm.PIBy2 + track.curvePts[i].heading) * distAway),
                        track.curvePts[i].heading);
                        bool Add = true;

                        for (int t = 0; t < refCount; t++)
                        {
                            double dist = ((point.easting - track.curvePts[t].easting) * (point.easting - track.curvePts[t].easting))
                                + ((point.northing - track.curvePts[t].northing) * (point.northing - track.curvePts[t].northing));
                            if (dist < distSqAway)
                            {
                                Add = false;
                                break;
                            }
                        }

                        if (Add)
                        {
                            if (newCurList.Count > 0)
                            {
                                double dist = ((point.easting - newCurList[newCurList.Count - 1].easting) * (point.easting - newCurList[newCurList.Count - 1].easting))
                                    + ((point.northing - newCurList[newCurList.Count - 1].northing) * (point.northing - newCurList[newCurList.Count - 1].northing));
                                if (dist > step)
                                    newCurList.Add(point);
                            }
                            else newCurList.Add(point);
                        }
                    }

                    int cnt = newCurList.Count;
                    if (cnt > 6 && !ct.IsCancellationRequested)
                    {
                        vec3[] arr = new vec3[cnt];
                        newCurList.CopyTo(arr);

                        newCurList.Clear();

                        for (int i = 0; i < (arr.Length - 1); i++)
                        {
                            if (ct.IsCancellationRequested)
                                break;
                            arr[i].heading = Math.Atan2(arr[i + 1].easting - arr[i].easting, arr[i + 1].northing - arr[i].northing);
                            if (arr[i].heading < 0) arr[i].heading += glm.twoPI;
                            if (arr[i].heading >= glm.twoPI) arr[i].heading -= glm.twoPI;
                        }

                        arr[arr.Length - 1].heading = arr[arr.Length - 2].heading;

                        cnt = arr.Length;
                        double distance;

                        //add the first point of loop - it will be p1
                        newCurList.Add(arr[0]);

                        for (int i = 0; i < cnt - 3; i++)
                        {
                            if (ct.IsCancellationRequested)
                                break;
                            // add p1
                            newCurList.Add(arr[i + 1]);

                            distance = glm.Distance(arr[i + 1], arr[i + 2]);

                            if (distance > step)
                            {
                                int loopTimes = (int)(distance / step + 1);
                                for (int j = 1; j < loopTimes; j++)
                                {
                                    vec3 pos = new vec3(glm.Catmull(j / (double)(loopTimes), arr[i], arr[i + 1], arr[i + 2], arr[i + 3]));
                                    newCurList.Add(pos);
                                }
                            }
                        }

                        newCurList.Add(arr[cnt - 2]);
                        newCurList.Add(arr[cnt - 1]);

                        //to calc heading based on next and previous points to give an average heading.
                        cnt = newCurList.Count;
                        arr = new vec3[cnt];
                        cnt--;
                        newCurList.CopyTo(arr);
                        newCurList.Clear();

                        newCurList.Add(new vec3(arr[0]));

                        //middle points
                        for (int i = 1; i < cnt; i++)
                        {
                            vec3 pt3 = new vec3(arr[i])
                            {
                                heading = Math.Atan2(arr[i + 1].easting - arr[i - 1].easting, arr[i + 1].northing - arr[i - 1].northing)
                            };
                            if (pt3.heading < 0) pt3.heading += glm.twoPI;
                            newCurList.Add(pt3);
                        }

                        int k = arr.Length - 1;
                        vec3 pt33 = new vec3(arr[k])
                        {
                            heading = Math.Atan2(arr[k].easting - arr[k - 1].easting, arr[k].northing - arr[k - 1].northing)
                        };
                        if (pt33.heading < 0) pt33.heading += glm.twoPI;
                        newCurList.Add(pt33);

                        if (!ct.IsCancellationRequested && mf.Bnd.bndList.Count > 0 && !(track.mode == TrackMode.bndCurve))
                        {
                            int ptCnt = newCurList.Count - 1;

                            bool isAdding = false;
                            //end
                            while (mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(newCurList[newCurList.Count - 1]))
                            {
                                if (ct.IsCancellationRequested)
                                    break;
                                isAdding = true;
                                for (int i = 1; i < 10; i++)
                                {
                                    vec3 pt = new vec3(newCurList[ptCnt]);
                                    pt.easting += (Math.Sin(pt.heading) * i * 2);
                                    pt.northing += (Math.Cos(pt.heading) * i * 2);
                                    newCurList.Add(pt);
                                }
                                ptCnt = newCurList.Count - 1;
                            }

                            if (isAdding)
                            {
                                vec3 pt = new vec3(newCurList[newCurList.Count - 1]);
                                for (int i = 1; i < 5; i++)
                                {
                                    pt.easting += (Math.Sin(pt.heading) * 2);
                                    pt.northing += (Math.Cos(pt.heading) * 2);
                                    newCurList.Add(pt);
                                }
                            }

                            isAdding = false;

                            //and the beginning
                            pt33 = new vec3(newCurList[0]);

                            while (mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(newCurList[0]))
                            {
                                if (ct.IsCancellationRequested)
                                    break;
                                isAdding = true;
                                pt33 = new vec3(newCurList[0]);

                                for (int i = 1; i < 10; i++)
                                {
                                    vec3 pt = new vec3(pt33);
                                    pt.easting -= (Math.Sin(pt.heading) * i * 2);
                                    pt.northing -= (Math.Cos(pt.heading) * i * 2);
                                    newCurList.Insert(0, pt);
                                }
                            }

                            if (isAdding)
                            {
                                vec3 pt = new vec3(newCurList[0]);
                                for (int i = 1; i < 5; i++)
                                {
                                    pt.easting -= (Math.Sin(pt.heading) * 2);
                                    pt.northing -= (Math.Cos(pt.heading) * 2);
                                    newCurList.Insert(0, pt);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.EventWriter("Exception Build new offset curve" + e.ToString());
            }

            // Resample to uniform spacing to prevent lookahead jumping
            if (newCurList.Count > 2)
            {
                double targetSpacing = 1.0; // 1 meter uniform spacing
                newCurList = ResampleCurveToUniformSpacing(newCurList, targetSpacing);
            }

            return newCurList;
        }

        private List<List<vec3>> BuildCurveGuidelines(double distAway, int _passes, CTrk track, CancellationToken ct)
        {
            // the listlist of all the guidelines
            List<List<vec3>> newGuideLL = new List<List<vec3>>();

            try
            {
                bool isSwitch = isHeadingSameWay;

                //left side
                for (int numGuides = -1; numGuides > -_passes; numGuides--)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    if (numGuides == 0) continue;

                    //the list of points of curve new list from async
                    List<vec3> newGuideList = new List<vec3>();

                    newGuideLL.Add(newGuideList);
                    double nextGuideDist = 0;
                    if (isHeadingSameWay)
                    {
                        nextGuideDist = (mf.Tool.width - mf.Tool.overlap) * numGuides;
                        nextGuideDist += (isSwitch ? mf.Tool.offset * 2 : 0);
                        isSwitch = !isSwitch;
                    }
                    else
                    {
                        nextGuideDist = (mf.Tool.width - mf.Tool.overlap) * -numGuides;
                        nextGuideDist += (isSwitch ? 0 : -mf.Tool.offset * 2);
                        isSwitch = !isSwitch;
                    }

                    // distAway already includes track.nudgeDistance
                    nextGuideDist += distAway;

                    double step = (mf.Tool.width - mf.Tool.overlap) * 0.48;
                    if (step > 4) step = 4;
                    if (step < 1) step = 1;

                    double distSqAway = (nextGuideDist * nextGuideDist) - 0.01;

                    int refCount = track.curvePts.Count;
                    for (int i = 0; i < refCount; i++)
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        vec3 point = new vec3(
                        track.curvePts[i].easting + (Math.Sin(glm.PIBy2 + track.curvePts[i].heading) * nextGuideDist),
                        track.curvePts[i].northing + (Math.Cos(glm.PIBy2 + track.curvePts[i].heading) * nextGuideDist),
                        track.curvePts[i].heading);
                        bool add = true;

                        for (int t = 0; t < refCount; t++)
                        {
                            double dist = ((point.easting - track.curvePts[t].easting) * (point.easting - track.curvePts[t].easting))
                                + ((point.northing - track.curvePts[t].northing) * (point.northing - track.curvePts[t].northing));
                            if (dist < distSqAway)
                            {
                                add = false;
                                break;
                            }
                        }

                        if (add)
                        {
                            if (newGuideList.Count > 0)
                            {
                                double dist = ((point.easting - newGuideList[newGuideList.Count - 1].easting) * (point.easting - newGuideList[newGuideList.Count - 1].easting))
                                    + ((point.northing - newGuideList[newGuideList.Count - 1].northing) * (point.northing - newGuideList[newGuideList.Count - 1].northing));
                                if (dist > step)
                                {
                                    if (mf.Bnd.bndList.Count > 0)
                                    {
                                        if (mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(point))
                                        {
                                            newGuideList.Add(point);
                                        }
                                    }
                                    else
                                    {
                                        newGuideList.Add(point);
                                    }
                                }
                            }
                            else
                            {
                                if (mf.Bnd.bndList.Count > 0)
                                {
                                    if (mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(point))
                                    {
                                        newGuideList.Add(point);
                                    }
                                }
                                else
                                {
                                    newGuideList.Add(point);
                                }
                            }
                        }
                    }

                    if (newGuideList == null || newGuideList.Count == 0)
                        continue;

                    AddGuidelineExtensions(ref newGuideList);
                }

                //right side
                for (int numGuides = 1; numGuides < _passes; numGuides++)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    if (numGuides == 0) continue;

                    //the list of points of curve new list from async
                    List<vec3> newGuideList = new List<vec3>();

                    newGuideLL.Add(newGuideList);
                    double nextGuideDist = 0;
                    if (isHeadingSameWay)
                    {
                        nextGuideDist = (mf.Tool.width - mf.Tool.overlap) * numGuides;
                        nextGuideDist += (isSwitch ? mf.Tool.offset * 2 : 0);
                        isSwitch = !isSwitch;
                    }
                    else
                    {
                        nextGuideDist = (mf.Tool.width - mf.Tool.overlap) * -numGuides;
                        nextGuideDist += (isSwitch ? 0 : -mf.Tool.offset * 2);
                        isSwitch = !isSwitch;
                    }

                    // distAway already includes track.nudgeDistance
                    nextGuideDist += distAway;

                    double step = (mf.Tool.width - mf.Tool.overlap) * 0.48;
                    if (step > 4) step = 4;
                    if (step < 1) step = 1;

                    double distSqAway = (nextGuideDist * nextGuideDist) - 0.01;

                    int refCount = track.curvePts.Count;
                    for (int i = 0; i < refCount; i++)
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        vec3 point = new vec3(
                        track.curvePts[i].easting + (Math.Sin(glm.PIBy2 + track.curvePts[i].heading) * nextGuideDist),
                        track.curvePts[i].northing + (Math.Cos(glm.PIBy2 + track.curvePts[i].heading) * nextGuideDist),
                        track.curvePts[i].heading);
                        bool add = true;

                        for (int t = 0; t < refCount; t++)
                        {
                            double dist = ((point.easting - track.curvePts[t].easting) * (point.easting - track.curvePts[t].easting))
                                + ((point.northing - track.curvePts[t].northing) * (point.northing - track.curvePts[t].northing));
                            if (dist < distSqAway)
                            {
                                add = false;
                                break;
                            }
                        }

                        if (add)
                        {
                            if (newGuideList.Count > 0)
                            {
                                double dist = ((point.easting - newGuideList[newGuideList.Count - 1].easting) * (point.easting - newGuideList[newGuideList.Count - 1].easting))
                                    + ((point.northing - newGuideList[newGuideList.Count - 1].northing) * (point.northing - newGuideList[newGuideList.Count - 1].northing));
                                if (dist > step)
                                {
                                    if (mf.Bnd.bndList.Count > 0)
                                    {
                                        if (mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(point))
                                        {
                                            newGuideList.Add(point);
                                        }
                                    }
                                    else
                                    {
                                        newGuideList.Add(point);
                                    }
                                }
                            }
                            else
                            {
                                if (mf.Bnd.bndList.Count > 0)
                                {
                                    if (mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(point))
                                    {
                                        newGuideList.Add(point);
                                    }
                                }
                                else
                                {
                                    newGuideList.Add(point);
                                }
                            }
                        }
                    }

                    if (newGuideList == null || newGuideList.Count == 0)
                        continue;

                    AddGuidelineExtensions(ref newGuideList);
                }
            }
            catch (Exception e)
            {
                Log.EventWriter("Exception Build new offset curve" + e.ToString());
            }

            return newGuideLL;
        }

        public void GetCurrentCurveLine(vec3 pivot, vec3 steer)
        {
            if (mf.Tracks[mf.TrackIdx].curvePts == null || mf.Tracks[mf.TrackIdx].curvePts.Count < 5)
            {
                if (mf.Tracks[mf.TrackIdx].mode != TrackMode.waterPivot)
                {
                    return;
                }
            }

            double dist, dx, dz;
            //int ptCount = curList.Count;

            if (curList.Count > 0)
            {
                // Update based on autosteer settings and distance from line
                double goalPointDistance = mf.Vehicle.UpdateGoalPointDistance();
                bool ReverseHeading = mf.IsReverse ? !isHeadingSameWay : isHeadingSameWay;

                if (mf.IsYouTurnTriggered && mf.YouTurnDistanceFromYouTurnLine())//do the pure pursuit from youTurn
                {
                    //now substitute what it thinks are AB line values with auto turn values
                    steerAngleCu = mf.YouTurnSteerAngle;
                    distanceFromCurrentLinePivot = mf.YouTurnDistanceFromCurrentLine;

                    goalPointCu = mf.YouTurnGoalPoint;
                    radiusPointCu.easting = mf.YouTurnRadiusPoint.easting;
                    radiusPointCu.northing = mf.YouTurnRadiusPoint.northing;
                    ppRadiusCu = mf.YouTurnPpRadius;
                    mf.Vehicle.modeActualXTE = (distanceFromCurrentLinePivot);
                }
                else if (mf.IsStanleyUsed)//Stanley
                {
                    mf.StanleyGuidanceCurve(pivot, steer, ref curList);
                }
                else// Pure Pursuit ------------------------------------------
                {
                    double minDistA;
                    double minDistB;

                    //If is a curve
                    if (mf.Tracks[mf.TrackIdx].mode <= TrackMode.Curve)
                    {
                        minDistB = double.MaxValue;
                        //close call hit
                        int cc, dd;

                        if (findGlobalNearestCurvePoint)
                        {
                            // When not already following some line, find the globally nearest point

                            cc = findNearestGlobalCurvePoint(pivot, 10);

                            findGlobalNearestCurvePoint = false;
                        }
                        else
                        {
                            // When already "locked" to follow some line, try to find the "local" nearest point
                            // based on the last one. This prevents jumping between lines close to each other (or crossing lines).
                            // As this is prone to find a "local minimum", this should only be used when already following some line.

                            cc = findNearestLocalCurvePoint(pivot, currentLocationIndex, goalPointDistance, ReverseHeading);
                        }

                        minDistA = double.MaxValue;

                        dd = cc + 8; if (dd > curList.Count - 1) dd = curList.Count;
                        cc -= 8; if (cc < 0) cc = 0;

                        //find the closest 2 points to current close call
                        for (int j = cc; j < dd; j++)
                        {
                            dist = glm.DistanceSquared(pivot, curList[j]);
                            if (dist < minDistA)
                            {
                                minDistB = minDistA;
                                B = A;
                                minDistA = dist;
                                A = j;
                            }
                            else if (dist < minDistB)
                            {
                                minDistB = dist;
                                B = j;
                            }
                        }

                        //just need to make sure the points continue ascending or heading switches all over the place
                        if (A > B) { C = A; A = B; B = C; }

                        currentLocationIndex = A;

                        if (A > curList.Count - 1 || B > curList.Count - 1)
                            return;
                    }
                    else
                    {
                        if (findGlobalNearestCurvePoint)
                        {
                            // When not already following some line, find the globally nearest point

                            A = findNearestGlobalCurvePoint(pivot);

                            findGlobalNearestCurvePoint = false;
                        }
                        else
                        {
                            // When already "locked" to follow some line, try to find the "local" nearest point
                            // based on the last one. This prevents jumping between lines close to each other (or crossing lines).
                            // As this is prone to find a "local minimum", this should only be used when already following some line.

                            A = findNearestLocalCurvePoint(pivot, currentLocationIndex, goalPointDistance, ReverseHeading);
                        }

                        currentLocationIndex = A;

                        if (A > curList.Count - 1)
                            return;

                        //initial forward Test if pivot InRange AB
                        if (A == curList.Count - 1) B = 0;
                        else B = A + 1;

                        if (glm.InRangeBetweenAB(curList[A].easting, curList[A].northing,
                             curList[B].easting, curList[B].northing, pivot.easting, pivot.northing))
                            goto SegmentFound;

                        //step back one
                        if (A == 0)
                        {
                            A = curList.Count - 1;
                            B = 0;
                        }
                        else
                        {
                            A--;
                            B = A + 1;
                        }

                        if (glm.InRangeBetweenAB(curList[A].easting, curList[A].northing,
                            curList[B].easting, curList[B].northing, pivot.easting, pivot.northing))
                            goto SegmentFound;

                        //realy really lost
                        return;
                    }

                SegmentFound:

                    //get the distance from currently active AB line

                    dx = curList[B].easting - curList[A].easting;
                    dz = curList[B].northing - curList[A].northing;

                    if (Math.Abs(dx) < Double.Epsilon && Math.Abs(dz) < Double.Epsilon) return;

                    //abHeading = Math.Atan2(dz, dx);

                    //how far from current AB Line is fix
                    distanceFromCurrentLinePivot = ((dz * pivot.easting) - (dx * pivot.northing) + (curList[B].easting
                                * curList[A].northing) - (curList[B].northing * curList[A].easting))
                                    / Math.Sqrt((dz * dz) + (dx * dx));

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

                        if (mf.IsBtnAutoSteerOn && mf.AvgSpeed > 2.5 && Math.Abs(pivotDerivative) < 0.1)
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
                    double U = (((pivot.easting - curList[A].easting) * dx)
                                + ((pivot.northing - curList[A].northing) * dz))
                                / ((dx * dx) + (dz * dz));

                    rEastCu = curList[A].easting + (U * dx);
                    rNorthCu = curList[A].northing + (U * dz);
                    manualUturnHeading = curList[A].heading;

                    int count = ReverseHeading ? 1 : -1;
                    vec3 start = new vec3(rEastCu, rNorthCu, 0);
                    double distSoFar = 0;

                    for (int i = ReverseHeading ? B : A; i < curList.Count && i >= 0;)
                    {
                        // used for calculating the length squared of next segment.
                        double tempDist = glm.Distance(start, curList[i]);

                        //will we go too far?
                        if ((tempDist + distSoFar) > goalPointDistance)
                        {
                            double j = (goalPointDistance - distSoFar) / tempDist; // the remainder to yet travel

                            goalPointCu.easting = (((1 - j) * start.easting) + (j * curList[i].easting));
                            goalPointCu.northing = (((1 - j) * start.northing) + (j * curList[i].northing));
                            break;
                        }
                        else distSoFar += tempDist;
                        start = curList[i];
                        i += count;
                        if (i < 0) i = curList.Count - 1;
                        if (i > curList.Count - 1) i = 0;
                    }

                    if (mf.Tracks[mf.TrackIdx].mode <= TrackMode.Curve)
                    {
                        if (mf.IsBtnAutoSteerOn && !mf.IsReverse)
                        {
                            if (isHeadingSameWay)
                            {
                                if (glm.Distance(goalPointCu, curList[(curList.Count - 1)]) < 0.5)
                                {
                                    mf.PerformAutoSteerClick();
                                    mf.TimedMessageBox(2000, gStr.gsGuidanceStopped, gStr.gsPastEndOfCurve);
                                    Log.EventWriter("Autosteer Stop, Past End of Curve");

                                }
                            }
                            else
                            {
                                if (glm.Distance(goalPointCu, curList[0]) < 0.5)
                                {
                                    mf.PerformAutoSteerClick();
                                    mf.TimedMessageBox(2000, gStr.gsGuidanceStopped, gStr.gsPastEndOfCurve);
                                    Log.EventWriter("Autosteer Stop, Past End of Curve");
                                }
                            }
                        }
                    }

                    //calc "D" the distance from pivot axle to lookahead point
                    double goalPointDistanceSquared = glm.DistanceSquared(goalPointCu.northing, goalPointCu.easting, pivot.northing, pivot.easting);

                    //calculate the the delta x in local coordinates and steering angle degrees based on wheelbase
                    //double localHeading = glm.twoPI - mf.FixHeading;

                    double localHeading;
                    if (ReverseHeading) localHeading = glm.twoPI - mf.FixHeading + inty;
                    else localHeading = glm.twoPI - mf.FixHeading - inty;

                    ppRadiusCu = goalPointDistanceSquared / (2 * (((goalPointCu.easting - pivot.easting) * Math.Cos(localHeading)) + ((goalPointCu.northing - pivot.northing) * Math.Sin(localHeading))));

                    steerAngleCu = glm.toDegrees(Math.Atan(2 * (((goalPointCu.easting - pivot.easting) * Math.Cos(localHeading))
                        + ((goalPointCu.northing - pivot.northing) * Math.Sin(localHeading))) * mf.Vehicle.VehicleConfig.Wheelbase / goalPointDistanceSquared));

                    if (mf.Ahrs.imuRoll != 88888)
                        steerAngleCu += mf.Ahrs.imuRoll * -mf.SideHillCompFactor;

                    if (steerAngleCu < -mf.Vehicle.maxSteerAngle) steerAngleCu = -mf.Vehicle.maxSteerAngle;
                    if (steerAngleCu > mf.Vehicle.maxSteerAngle) steerAngleCu = mf.Vehicle.maxSteerAngle;

                    if (!isHeadingSameWay)
                        distanceFromCurrentLinePivot *= -1.0;

                    //used for acquire/hold mode
                    mf.Vehicle.modeActualXTE = (distanceFromCurrentLinePivot);

                    double steerHeadingError = (pivot.heading - curList[A].heading);
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

                    //Convert to centimeters
                    mf.GuidanceLineDistanceOff = (short)Math.Round(distanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
                    mf.GuidanceLineSteerAngle = (short)(steerAngleCu * 100);
                }
            }
            else
            {
                //invalid distance so tell AS module
                distanceFromCurrentLinePivot = 32000;
                mf.GuidanceLineDistanceOff = 32000;
            }
        }

        //DrawCurveNew() y DrawCurve() se movieron a ABCurveDrawExtensions
        //(DrawLib/GuidanceDrawExtensions.cs): eran el unico uso de OpenTK aca
        //(traspaso portabilidad).

        public void BuildTram()
        {
            //if all or bnd only then make outer loop pass
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

            bool isBndExist = mf.Bnd.bndList.Count != 0;

            int refCount = mf.Tracks[mf.TrackIdx].curvePts.Count;

            bool skipFirstPass = isBndExist && mf.Tram.generateMode.IncludesBoundaryTracks();
            int startPass = skipFirstPass ? 1 : 0;

            double widd;

            for (int i = startPass; i <= mf.Tram.passes; i++)
            {
                mf.Tram.tramArr = new List<vec2>
                {
                    Capacity = 128
                };

                mf.Tram.tramList.Add(mf.Tram.tramArr);

                widd = (mf.Tram.tramWidth * 0.5) - mf.Tram.halfWheelTrack;
                widd += (mf.Tram.tramWidth * i);

                double distSqAway = widd * widd * 0.999999;

                for (int j = 0; j < refCount; j += 1)
                {
                    vec2 point = new vec2(
                    (Math.Sin(glm.PIBy2 + mf.Tracks[mf.TrackIdx].curvePts[j].heading) *
                        widd) + mf.Tracks[mf.TrackIdx].curvePts[j].easting,
                    (Math.Cos(glm.PIBy2 + mf.Tracks[mf.TrackIdx].curvePts[j].heading) *
                        widd) + mf.Tracks[mf.TrackIdx].curvePts[j].northing
                        );

                    bool Add = true;
                    for (int t = 0; t < refCount; t++)
                    {
                        //distance check to be not too close to ref line
                        double dist = ((point.easting - mf.Tracks[mf.TrackIdx].curvePts[t].easting) * (point.easting - mf.Tracks[mf.TrackIdx].curvePts[t].easting))
                            + ((point.northing - mf.Tracks[mf.TrackIdx].curvePts[t].northing) * (point.northing - mf.Tracks[mf.TrackIdx].curvePts[t].northing));
                        if (dist < distSqAway)
                        {
                            Add = false;
                            break;
                        }
                    }
                    if (Add)
                    {
                        //a new point only every 2 meters
                        double dist = mf.Tram.tramArr.Count > 0 ? ((point.easting - mf.Tram.tramArr[mf.Tram.tramArr.Count - 1].easting) * (point.easting - mf.Tram.tramArr[mf.Tram.tramArr.Count - 1].easting))
                            + ((point.northing - mf.Tram.tramArr[mf.Tram.tramArr.Count - 1].northing) * (point.northing - mf.Tram.tramArr[mf.Tram.tramArr.Count - 1].northing)) : 3.0;
                        if (dist > 2)
                        {
                            //if inside the boundary, add
                            if (!isBndExist || mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(point))
                            {
                                mf.Tram.tramArr.Add(point);
                            }
                        }
                    }
                }
            }

            for (int i = startPass; i <= mf.Tram.passes; i++)
            {
                mf.Tram.tramArr = new List<vec2>
                {
                    Capacity = 128
                };

                mf.Tram.tramList.Add(mf.Tram.tramArr);

                widd = (mf.Tram.tramWidth * 0.5) + mf.Tram.halfWheelTrack;
                widd += (mf.Tram.tramWidth * i);
                double distSqAway = widd * widd * 0.999999;

                for (int j = 0; j < refCount; j += 1)
                {
                    vec2 point = new vec2(
                    Math.Sin(glm.PIBy2 + mf.Tracks[mf.TrackIdx].curvePts[j].heading) *
                        widd + mf.Tracks[mf.TrackIdx].curvePts[j].easting,
                    Math.Cos(glm.PIBy2 + mf.Tracks[mf.TrackIdx].curvePts[j].heading) *
                        widd + mf.Tracks[mf.TrackIdx].curvePts[j].northing
                        );

                    bool Add = true;
                    for (int t = 0; t < refCount; t++)
                    {
                        //distance check to be not too close to ref line
                        double dist = ((point.easting - mf.Tracks[mf.TrackIdx].curvePts[t].easting) * (point.easting - mf.Tracks[mf.TrackIdx].curvePts[t].easting))
                            + ((point.northing - mf.Tracks[mf.TrackIdx].curvePts[t].northing) * (point.northing - mf.Tracks[mf.TrackIdx].curvePts[t].northing));
                        if (dist < distSqAway)
                        {
                            Add = false;
                            break;
                        }
                    }
                    if (Add)
                    {
                        //a new point only every 2 meters
                        double dist = mf.Tram.tramArr.Count > 0 ? ((point.easting - mf.Tram.tramArr[mf.Tram.tramArr.Count - 1].easting) * (point.easting - mf.Tram.tramArr[mf.Tram.tramArr.Count - 1].easting))
                            + ((point.northing - mf.Tram.tramArr[mf.Tram.tramArr.Count - 1].northing) * (point.northing - mf.Tram.tramArr[mf.Tram.tramArr.Count - 1].northing)) : 3.0;
                        if (dist > 2)
                        {
                            //if inside the boundary, add
                            if (!isBndExist || mf.Bnd.bndList[0].fenceLineEar.IsPointInPolygon(point))
                            {
                                mf.Tram.tramArr.Add(point);
                            }
                        }
                    }
                }
            }
        }

        //for calculating for display the averaged new line
        public void SmoothAB(int smPts)
        {
            //countExit the reference list of original curve
            int cnt = mf.Tracks[mf.TrackIdx].curvePts.Count;

            //just go back if not very long
            if (cnt < 100) return;

            //the temp array
            vec3[] arr = new vec3[cnt];

            //read the points before and after the setpoint
            for (int s = 0; s < smPts / 2; s++)
            {
                arr[s].easting = mf.Tracks[mf.TrackIdx].curvePts[s].easting;
                arr[s].northing = mf.Tracks[mf.TrackIdx].curvePts[s].northing;
                arr[s].heading = mf.Tracks[mf.TrackIdx].curvePts[s].heading;
            }

            for (int s = cnt - (smPts / 2); s < cnt; s++)
            {
                arr[s].easting = mf.Tracks[mf.TrackIdx].curvePts[s].easting;
                arr[s].northing = mf.Tracks[mf.TrackIdx].curvePts[s].northing;
                arr[s].heading = mf.Tracks[mf.TrackIdx].curvePts[s].heading;
            }

            //average them - center weighted average
            for (int i = smPts / 2; i < cnt - (smPts / 2); i++)
            {
                for (int j = -smPts / 2; j < smPts / 2; j++)
                {
                    arr[i].easting += mf.Tracks[mf.TrackIdx].curvePts[j + i].easting;
                    arr[i].northing += mf.Tracks[mf.TrackIdx].curvePts[j + i].northing;
                }
                arr[i].easting /= smPts;
                arr[i].northing /= smPts;
                arr[i].heading = mf.Tracks[mf.TrackIdx].curvePts[i].heading;
            }

            //make a list to draw
            smooList?.Clear();

            if (arr == null || cnt < 1) return;
            if (smooList == null) return;

            for (int i = 0; i < cnt; i++)
            {
                smooList.Add(arr[i]);
            }
        }

        // Los cuerpos de CalculateHeadings / CalculateHeadingsClosedLoop /
        // MakePointMinimumSpacing se movieron a CurveSmoothing (AgOpenGPS.Core)
        // — matemática pura que CTram necesita en Core (traspaso 2026-07-17).
        // Se mantienen estos wrappers para no tocar los ~20 call sites.
        public static void CalculateHeadings(ref List<vec3> xList)
            => CurveSmoothing.CalculateHeadings(ref xList);

        public static void CalculateHeadingsClosedLoop(ref List<vec3> xList)
            => CurveSmoothing.CalculateHeadingsClosedLoop(ref xList);

        public static void MakePointMinimumSpacing(ref List<vec3> xList, double minDistance)
            => CurveSmoothing.MakePointMinimumSpacing(ref xList, minDistance);

        private List<vec3> AddGuidelineExtensions(ref List<vec3> guideLine)
        {
            vec3 startExtension = new vec3
            {
                easting = guideLine[0].easting - (Math.Sin(guideLine[0].heading) * 2000.0),
                northing = guideLine[0].northing - (Math.Cos(guideLine[0].heading) * 2000.0)
            };
            guideLine.Insert(0, startExtension);

            vec3 endExtension = new vec3
            {
                easting = guideLine[guideLine.Count - 1].easting + (Math.Sin(guideLine[guideLine.Count - 1].heading) * 2000.0),
                northing = guideLine[guideLine.Count - 1].northing + (Math.Cos(guideLine[guideLine.Count - 1].heading) * 2000.0)
            };
            guideLine.Add(endExtension);
            return guideLine;
        }

        // Resample curve points to uniform spacing to prevent lookahead jumping
        private static List<vec3> ResampleCurveToUniformSpacing(List<vec3> originalList, double targetSpacing)
        {
            if (originalList == null || originalList.Count < 2)
                return originalList;

            List<vec3> resampledList = new List<vec3>
            {
                // Always add the first point
                originalList[0]
            };

            double accumulatedDistance = 0;
            int sourceIndex = 1;

            while (sourceIndex < originalList.Count)
            {
                double segmentLength = glm.Distance(originalList[sourceIndex - 1], originalList[sourceIndex]);

                if (segmentLength < 0.001) // Skip duplicate points
                {
                    sourceIndex++;
                    continue;
                }

                accumulatedDistance += segmentLength;

                // Add points at uniform intervals
                while (accumulatedDistance >= targetSpacing && sourceIndex < originalList.Count)
                {
                    // Calculate how far back we need to go on this segment
                    double overshoot = accumulatedDistance - targetSpacing;
                    double ratio = 1.0 - (overshoot / segmentLength);

                    vec3 newPoint = new vec3(
                        originalList[sourceIndex - 1].easting + ratio * (originalList[sourceIndex].easting - originalList[sourceIndex - 1].easting),
                        originalList[sourceIndex - 1].northing + ratio * (originalList[sourceIndex].northing - originalList[sourceIndex - 1].northing),
                        originalList[sourceIndex - 1].heading
                    );

                    resampledList.Add(newPoint);
                    accumulatedDistance -= targetSpacing;
                }

                sourceIndex++;
            }

            // Always add the final point if we didn't generate enough samples
            // This ensures short curves (< targetSpacing) remain valid with at least 2 points
            if (resampledList.Count == 1)
            {
                resampledList.Add(originalList[originalList.Count - 1]);
            }

            // Recalculate headings for the resampled points
            for (int i = 0; i < resampledList.Count - 1; i++)
            {
                double newHeading = Math.Atan2(
                    resampledList[i + 1].easting - resampledList[i].easting,
                    resampledList[i + 1].northing - resampledList[i].northing);

                if (newHeading < 0)
                    newHeading += glm.twoPI;

                resampledList[i] = new vec3(
                    resampledList[i].easting,
                    resampledList[i].northing,
                    newHeading
                );
            }

            // Set last point heading same as previous
            if (resampledList.Count > 1)
            {
                resampledList[resampledList.Count - 1] = new vec3(
                    resampledList[resampledList.Count - 1].easting,
                    resampledList[resampledList.Count - 1].northing,
                    resampledList[resampledList.Count - 2].heading
                );
            }

            return resampledList;
        }

        //turning the visual line into the real reference line to use
        public void SaveSmoothList()
        {
            //oops no smooth list generated
            if (smooList == null) return;
            int cnt = smooList.Count;
            if (cnt == 0) return;

            //eek
            mf.Tracks[mf.TrackIdx].curvePts?.Clear();

            //copy to an array to calculate all the new headings
            vec3[] arr = new vec3[cnt];
            smooList.CopyTo(arr);

            //calculate new headings on smoothed line
            for (int i = 1; i < cnt - 1; i++)
            {
                arr[i].heading = Math.Atan2(arr[i + 1].easting - arr[i].easting, arr[i + 1].northing - arr[i].northing);
                if (arr[i].heading < 0) arr[i].heading += glm.twoPI;
                mf.Tracks[mf.TrackIdx].curvePts.Add(arr[i]);
            }
        }

        //add extensons
        public void AddFirstLastPoints(ref List<vec3> xList)
        {
            int ptCnt = xList.Count - 1;
            vec3 start;

            if (mf.Bnd.bndList.Count > 0)
            {
                for (int i = 1; i < 100; i++)
                {
                    vec3 pt = new vec3(xList[ptCnt]);
                    pt.easting += (Math.Sin(pt.heading) * i);
                    pt.northing += (Math.Cos(pt.heading) * i);
                    xList.Add(pt);
                }

                //and the beginning
                start = new vec3(xList[0]);

                for (int i = 1; i < 100; i++)
                {
                    vec3 pt = new vec3(start);
                    pt.easting -= (Math.Sin(pt.heading) * i);
                    pt.northing -= (Math.Cos(pt.heading) * i);
                    xList.Insert(0, pt);
                }

            }
            else
            {
                for (int i = 1; i < 300; i++)
                {
                    vec3 pt = new vec3(xList[ptCnt]);
                    pt.easting += (Math.Sin(pt.heading) * i);
                    pt.northing += (Math.Cos(pt.heading) * i);
                    xList.Add(pt);
                }

                //and the beginning
                start = new vec3(xList[0]);

                for (int i = 1; i < 300; i++)
                {
                    vec3 pt = new vec3(start);
                    pt.easting -= (Math.Sin(pt.heading) * i);
                    pt.northing -= (Math.Cos(pt.heading) * i);
                    xList.Insert(0, pt);
                }
            }
        }

        public void ResetCurveLine()
        {
            curList?.Clear();
            mf.TrackIdx = -1;
        }

        // Searches for the nearest "global" curve point to the refPoint by checking all points of the curve.
        // Parameter "increment" added here to give possibility to make a "sparser" search (to speed it up?)
        // Return: index to the nearest point
        private int findNearestGlobalCurvePoint(vec3 refPoint, int increment = 1)
        {
            double minDist = double.MaxValue;
            int minDistIndex = 0;

            for (int i = 0; i < curList.Count; i += increment)
            {
                double dist = glm.DistanceSquared(refPoint, curList[i]);
                if (dist < minDist)
                {
                    minDist = dist;
                    minDistIndex = i;
                }
            }
            return minDistIndex;
        }

        // Searches for the nearest "local" curve point to the refPoint by traversing forward and backward on the curve
        // startIndex means the starting point (index to curList) of the search.
        // Return: index to the nearest (local) point
        private int findNearestLocalCurvePoint(vec3 refPoint, int startIndex, double minSearchDistance, bool reverseSearchDirection)
        {
            double minDist = glm.DistanceSquared(refPoint, curList[(startIndex + curList.Count) % curList.Count]);
            int minDistIndex = startIndex;

            int directionMultiplier = reverseSearchDirection ? 1 : -1;
            double distSoFar = 0;
            vec3 start = curList[startIndex];

            // First: search forward in the direction of travel
            // This prevents jumping backwards to closer points
            int offset = 1;

            while (offset < curList.Count)
            {
                int pointIndex = (startIndex + (offset * directionMultiplier) + curList.Count) % curList.Count;
                double dist = glm.DistanceSquared(refPoint, curList[pointIndex]);

                if (dist < minDist)
                {
                    minDist = dist;
                    minDistIndex = pointIndex;
                }

                distSoFar += glm.Distance(start, curList[pointIndex]);
                start = curList[pointIndex];

                offset++;

                if (distSoFar > minSearchDistance)
                {
                    break;
                }
            }

            // Continue traversing forward until the distance starts growing
            while (offset < curList.Count)
            {
                int pointIndex = (startIndex + (offset * directionMultiplier) + curList.Count) % curList.Count;
                double dist = glm.DistanceSquared(refPoint, curList[pointIndex]);
                if (dist < minDist)
                {
                    minDist = dist;
                    minDistIndex = pointIndex;
                }
                else
                {
                    // Getting farther, no point to continue
                    break;
                }
                offset++;
            }

            // Only check backwards if we haven't found a good point forward
            // This prevents jumping back unless absolutely necessary (e.g., sharp turn or lost tracking)
            // We limit backwards search to a small distance to avoid jumping to parallel lines
            double backwardSearchLimit = 3.0; // Only search 3 meters backward
            distSoFar = 0;
            start = curList[startIndex];

            for (offset = 1; offset < curList.Count && distSoFar < backwardSearchLimit; offset++)
            {
                int pointIndex = (startIndex + (offset * (-directionMultiplier)) + curList.Count) % curList.Count;

                distSoFar += glm.Distance(start, curList[pointIndex]);
                start = curList[pointIndex];

                if (distSoFar >= backwardSearchLimit)
                    break;

                double dist = glm.DistanceSquared(refPoint, curList[pointIndex]);

                // Only accept backwards point if it's significantly closer (20% threshold)
                if (dist < minDist * 0.8)
                {
                    minDist = dist;
                    minDistIndex = pointIndex;
                }
                else
                {
                    // Not significantly closer, stop searching backwards
                    break;
                }
            }

            return minDistIndex;
        }
    }
}