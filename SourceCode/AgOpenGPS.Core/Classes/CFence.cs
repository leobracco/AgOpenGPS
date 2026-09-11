using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class CBoundary
    {
        public List<vec3> bndBeingMadePts = new List<vec3>(128);

        public double createBndOffset;
        public bool isBndBeingMade;

        public bool isDrawRightSide = true, isDrawAtPivot = true, isOkToAddPoints = false;
        public bool isRecBoundaryWhenSectionOn = false;

        public bool IsPointInsideFenceArea(vec3 testPoint)
        {
            //first where are we, must be inside outer and outside of inner geofence non drive thru turn borders
            if (bndList[0].fenceLineEar.IsPointInPolygon(testPoint))
            {
                for (int i = 1; i < bndList.Count; i++)
                {
                    //make sure not inside a non drivethru boundary
                    //if (bndList[i].isDriveThru) continue;
                    if (bndList[i].fenceLineEar.IsPointInPolygon(testPoint))
                    {
                        return false;
                    }
                }
                //we are safely inside outer, outside inner boundaries
                return true;
            }
            return false;
        }

        public bool IsPointInsideFenceArea(vec2 testPoint)
        {
            //first where are we, must be inside outer and outside of inner geofence non drive thru turn borders
            if (bndList[0].fenceLineEar.IsPointInPolygon(testPoint))
            {
                for (int i = 1; i < bndList.Count; i++)
                {
                    //make sure not inside a non drivethru boundary
                    //if (bndList[i].isDriveThru) continue;
                    if (bndList[i].fenceLineEar.IsPointInPolygon(testPoint))
                    {
                        return false;
                    }
                }
                //we are safely inside outer, outside inner boundaries
                return true;
            }
            return false;
        }

        //DrawFenceLines() se movió a BoundaryDrawExtensions
        //(DrawLib/GuidanceDrawExtensions.cs): era el único uso de OpenTK acá
        //(traspaso portabilidad).
    }
}