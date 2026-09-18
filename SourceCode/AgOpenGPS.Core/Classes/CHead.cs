using System;

namespace AgOpenGPS
{
    public partial class CBoundary
    {
        public bool isHeadlandOn;

        public bool isToolInHeadland,
            isToolOuterPointsInHeadland, isSectionControlledByHeadland;

        public vec2? HeadlandNearestPoint { get; private set; } = null;
        public double? HeadlandDistance { get; private set; } = null;

        public void SetHydPosition()
        {
            if (mf.IsHydLiftOn && mf.AvgSpeed > 0.2 && !mf.IsReverse)
            {
                if (isToolInHeadland)
                {
                    mf.SetHydLiftPgn(2);
                    if (mf.IsHydLiftChange != isToolInHeadland)
                    {
                        if (mf.IsHydLiftSoundOn) mf.PlayHydLiftUp();
                        mf.IsHydLiftChange = isToolInHeadland;
                    }
                }
                else
                {
                    mf.SetHydLiftPgn(1);
                    if (mf.IsHydLiftChange != isToolInHeadland)
                    {
                        if (mf.IsHydLiftSoundOn) mf.PlayHydLiftDn();
                        mf.IsHydLiftChange = isToolInHeadland;
                    }
                }
            }
        }

        public void WhereAreToolCorners()
        {
            if (bndList.Count > 0 && bndList[0].hdLine.Count > 0)
            {
                bool isLeftInWk, isRightInWk = true;

                for (int j = 0; j < mf.ToolNumOfSections; j++)
                {
                    isLeftInWk = j == 0 ? IsPointInsideHeadArea(mf.Section[j].leftPoint) : isRightInWk;
                    isRightInWk = IsPointInsideHeadArea(mf.Section[j].rightPoint);

                    //save left side
                    if (j == 0)
                        mf.ToolIsLeftSideInHeadland = !isLeftInWk;

                    //merge the two sides into in or out
                    mf.Section[j].isInHeadlandArea = !isLeftInWk && !isRightInWk;
                }

                //save right side
                mf.ToolIsRightSideInHeadland = !isRightInWk;

                //is the tool in or out based on endpoints
                isToolOuterPointsInHeadland = mf.ToolIsLeftSideInHeadland && mf.ToolIsRightSideInHeadland;
            }
        }

        public void WhereAreToolLookOnPoints()
        {
            if (bndList.Count > 0 && bndList[0].hdLine.Count > 0)
            {
                bool isLookRightIn = false;

                vec3 toolFix = mf.ToolPivotPos;
                double sinAB = Math.Sin(toolFix.heading);
                double cosAB = Math.Cos(toolFix.heading);

                //generated box for finding closest point
                double pos = 0;
                double mOn = (mf.ToolLookAheadOnPixelsRight - mf.ToolLookAheadOnPixelsLeft) / mf.ToolRpWidth;

                for (int j = 0; j < mf.ToolNumOfSections; j++)
                {
                    bool isLookLeftIn = j == 0 ? IsPointInsideHeadArea(new vec2(
                        mf.Section[j].leftPoint.easting + (sinAB * mf.ToolLookAheadOnPixelsLeft * 0.1),
                        mf.Section[j].leftPoint.northing + (cosAB * mf.ToolLookAheadOnPixelsLeft * 0.1))) : isLookRightIn;

                    pos += mf.Section[j].rpSectionWidth;
                    double endHeight = (mf.ToolLookAheadOnPixelsLeft + (mOn * pos)) * 0.1;

                    isLookRightIn = IsPointInsideHeadArea(new vec2(
                        mf.Section[j].rightPoint.easting + (sinAB * endHeight),
                        mf.Section[j].rightPoint.northing + (cosAB * endHeight)));

                    mf.Section[j].isLookOnInHeadland = !isLookLeftIn && !isLookRightIn;
                }
            }
        }

        public bool IsPointInsideHeadArea(vec2 pt)
        {
            //if inside outer boundary, then potentially add
            if (bndList[0].hdLine.IsPointInPolygon(pt))
            {
                for (int i = 1; i < bndList.Count; i++)
                {
                    if (bndList[i].hdLine.IsPointInPolygon(pt))
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }
        public void CheckHeadlandProximity()
        {
            if (!isHeadlandOn || bndList.Count == 0 || bndList[0].hdLine.Count < 2)
            {
                HeadlandNearestPoint = null;
                HeadlandDistance = null;
                return;
            }

            vec3 vehiclePos = mf.ToolPivotPos;

            vec2? nearest = glm.RaycastToPolygon(vehiclePos, bndList[0].hdLine);
            if (!nearest.HasValue)
            {
                HeadlandNearestPoint = null;
                HeadlandDistance = null;
                return;
            }

            vec2 nearestVal = nearest.Value;
            double distance = glm.Distance(vehiclePos.ToVec2(), nearestVal);

            HeadlandNearestPoint = nearestVal;
            HeadlandDistance = distance;

            bool isInside = bndList[0].hdLine.IsPointInPolygon(vehiclePos.ToVec2());

            double dx = nearestVal.easting - vehiclePos.easting;
            double dy = nearestVal.northing - vehiclePos.northing;
            double angleToPolygon = Math.Atan2(dx, dy);
            double headingDiff = glm.AngleDiff(vehiclePos.heading, angleToPolygon);
            bool headingOk = headingDiff < glm.toRadians(60); // eventueel verwijderen: zit al in GetClosestPointInFront

            // Warning Logic
            bool shouldPlay =
                (isInside && headingOk && distance < 20.0) ||
                (!isInside && headingOk && distance < 5.0);

            if (shouldPlay && mf.IsHeadlandDistanceOn)
            {
                if (!mf.IsBoundAlarming)
                {
                    mf.PlayHeadlandSound();
                    mf.IsBoundAlarming = true;
                }
            }
            else
            {
                mf.IsBoundAlarming = false;
            }
        }

    }
}