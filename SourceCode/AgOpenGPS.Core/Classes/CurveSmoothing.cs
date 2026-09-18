using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    // Estáticos de suavizado/heading de polilíneas extraídos de CABCurve
    // (traspaso portabilidad 2026-07-17): son matemática pura sobre vec3 y
    // los necesita CTram en Core. CABCurve delega acá para no tocar los
    // ~20 call sites existentes.
    public static class CurveSmoothing
    {
        public static void CalculateHeadings(ref List<vec3> xList)
        {
            //to calc heading based on next and previous points to give an average heading.
            int cnt = xList.Count;
            if (cnt > 3)
            {
                vec3[] arr = new vec3[cnt];
                cnt--;
                xList.CopyTo(arr);
                xList.Clear();

                vec3 pt3 = arr[0];
                pt3.heading = Math.Atan2(arr[1].easting - arr[0].easting, arr[1].northing - arr[0].northing);
                if (pt3.heading < 0) pt3.heading += glm.twoPI;
                xList.Add(pt3);

                //middle points
                for (int i = 1; i < cnt; i++)
                {
                    pt3 = arr[i];
                    pt3.heading = Math.Atan2(arr[i + 1].easting - arr[i - 1].easting, arr[i + 1].northing - arr[i - 1].northing);
                    if (pt3.heading < 0) pt3.heading += glm.twoPI;
                    xList.Add(pt3);
                }

                pt3 = arr[arr.Length - 1];
                pt3.heading = Math.Atan2(arr[arr.Length - 1].easting - arr[arr.Length - 2].easting,
                    arr[arr.Length - 1].northing - arr[arr.Length - 2].northing);
                if (pt3.heading < 0) pt3.heading += glm.twoPI;
                xList.Add(pt3);
            }
        }

        /// <summary>
        /// Calculates headings for a closed loop (like a boundary curve).
        /// The last point should have the same coordinates as the first point,
        /// and its heading should wrap around to point towards the second point.
        /// </summary>
        public static void CalculateHeadingsClosedLoop(ref List<vec3> xList)
        {
            int cnt = xList.Count;
            if (cnt > 3)
            {
                vec3[] arr = new vec3[cnt];
                xList.CopyTo(arr);
                xList.Clear();

                // First point - wrap to use last point (before the duplicate closing point)
                vec3 pt3 = arr[0];
                pt3.heading = Math.Atan2(arr[1].easting - arr[cnt - 2].easting, arr[1].northing - arr[cnt - 2].northing);
                if (pt3.heading < 0) pt3.heading += glm.twoPI;
                xList.Add(pt3);

                // Middle points (all except first and last)
                for (int i = 1; i < cnt - 1; i++)
                {
                    pt3 = arr[i];
                    pt3.heading = Math.Atan2(arr[i + 1].easting - arr[i - 1].easting, arr[i + 1].northing - arr[i - 1].northing);
                    if (pt3.heading < 0) pt3.heading += glm.twoPI;
                    xList.Add(pt3);
                }

                // Last point (closing point - same as first point) - should point to second point
                pt3 = arr[cnt - 1];
                pt3.heading = Math.Atan2(arr[1].easting - arr[cnt - 2].easting, arr[1].northing - arr[cnt - 2].northing);
                if (pt3.heading < 0) pt3.heading += glm.twoPI;
                xList.Add(pt3);
            }
        }

        public static void MakePointMinimumSpacing(ref List<vec3> xList, double minDistance)
        {
            int cnt = xList.Count;
            if (cnt > 3)
            {
                //make sure point distance isn't too big
                for (int i = 0; i < cnt - 1; i++)
                {
                    int j = i + 1;
                    double distance = glm.Distance(xList[i], xList[j]);
                    if (distance > minDistance)
                    {
                        vec3 pointB = new vec3((xList[i].easting + xList[j].easting) / 2.0,
                            (xList[i].northing + xList[j].northing) / 2.0,
                            xList[i].heading);

                        xList.Insert(j, pointB);
                        cnt = xList.Count;
                        i = -1;
                    }
                }
            }
        }
    }
}
