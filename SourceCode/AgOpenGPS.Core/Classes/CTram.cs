using AgOpenGPS.Core.Models;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public class CTram
    {
        // Host invertido (FormGPS implementa ITramHost) — traspaso 2026-07-17
        private readonly ITramHost mf;

        public List<vec2> tramBndOuterArr = new List<vec2>();
        public List<vec2> tramBndInnerArr = new List<vec2>();

        //tram settings
        //public double wheelTrack;
        public double tramWidth;

        public double halfWheelTrack, alpha;
        public int passes;
        public bool isOuter;

        public bool isLeftManualOn, isRightManualOn;

        //tramlines
        public List<vec2> tramArr = new List<vec2>();

        public List<List<vec2>> tramList = new List<List<vec2>>();

        public TramMode displayMode;
        public TramMode generateMode = TramMode.All;

        // era internal; ahora lo escribe el GPS desde otro assembly (traspaso 2026-07-17)
        public int controlByte;

        public CTram(ITramHost _f)
        {
            //constructor
            mf = _f;

            tramWidth = Properties.Settings.Default.setTram_tramWidth;
            //halfTramWidth = (Math.Round((Properties.Settings.Default.setTram_tramWidth) / 2.0, 3));

            halfWheelTrack = Properties.Settings.Default.setVehicle_trackWidth * 0.5;

            IsTramOuterOrInner();

            passes = Properties.Settings.Default.setTram_passes;
            displayMode = 0;

            alpha = Properties.Settings.Default.setTram_alpha;
        }


        //GetModeBitmap(TramMode) se movió a TramModeBitmaps (CExtensionMethods.cs):
        //era el único uso de System.Drawing acá y es puro asset de UI
        //(traspaso portabilidad 2026-07-16).

        public void IsTramOuterOrInner()
        {
            isOuter = ((int)(tramWidth / mf.ToolWidth + 0.5)) % 2 == 0;
            if (Properties.Settings.Default.setTool_isTramOuterInverted) isOuter = !isOuter;
        }

        public void DrawTram()
        {
            if (mf.CamSetDistance > -500) GL.LineWidth(10);
            else GL.LineWidth(6);

            GL.Color4(0, 0, 0, alpha);

            if (displayMode.IncludesFillTracks())
            {
                if (tramList.Count > 0)
                {
                    for (int i = 0; i < tramList.Count; i++)
                    {
                        GL.Begin(PrimitiveType.LineStrip);
                        for (int h = 0; h < tramList[i].Count; h++)
                        {
                            GL.Vertex2(tramList[i][h].easting, tramList[i][h].northing);
                        }
                        GL.End();
                    }
                }
            }

            if (displayMode.IncludesBoundaryTracks())
            {
                if (tramBndOuterArr.Count > 0)
                {
                    GL.Begin(PrimitiveType.LineLoop);
                    for (int h = 0; h < tramBndOuterArr.Count; h++) GL.Vertex3(tramBndOuterArr[h].easting, tramBndOuterArr[h].northing, 0);
                    GL.End();
                    GL.Begin(PrimitiveType.LineLoop);
                    for (int h = 0; h < tramBndInnerArr.Count; h++) GL.Vertex3(tramBndInnerArr[h].easting, tramBndInnerArr[h].northing, 0);
                    GL.End();
                }
            }

            if (mf.CamSetDistance > -500) GL.LineWidth(4);
            else GL.LineWidth(2);

            GL.Color4(0.930f, 0.72f, 0.73530f, alpha);

            if (displayMode.IncludesFillTracks())
            {
                if (tramList.Count > 0)
                {
                    for (int i = 0; i < tramList.Count; i++)
                    {
                        GL.Begin(PrimitiveType.LineStrip);
                        for (int h = 0; h < tramList[i].Count; h++)
                        {
                            GL.Vertex2(tramList[i][h].easting, tramList[i][h].northing);
                        }
                        GL.End();
                    }
                }
            }
            if (displayMode.IncludesBoundaryTracks())
            {
                if (tramBndOuterArr.Count > 0)
                {
                    GL.Begin(PrimitiveType.LineLoop);
                    for (int h = 0; h < tramBndOuterArr.Count; h++) GL.Vertex3(tramBndOuterArr[h].easting, tramBndOuterArr[h].northing, 0);
                    GL.End();
                    GL.Begin(PrimitiveType.LineLoop);
                    for (int h = 0; h < tramBndInnerArr.Count; h++) GL.Vertex3(tramBndInnerArr[h].easting, tramBndInnerArr[h].northing, 0);
                    GL.End();
                }
            }
        }

        public void BuildTramBnd()
        {
            bool isBndExist = mf.BoundaryList.Count != 0;

            if (isBndExist)
            {
                CreateBoundaryOuterTrack();
                CreateBoundaryInnerTrack();
            }
            else
            {
                tramBndOuterArr?.Clear();
                tramBndInnerArr?.Clear();
            }
        }

        public void CreateBoundaryOuterTrack()
        {
            tramBndOuterArr = CreateBoundaryTrack(0.5 * tramWidth - halfWheelTrack);
        }

        public void CreateBoundaryInnerTrack()
        {
            tramBndInnerArr = CreateBoundaryTrack(0.5 * tramWidth + halfWheelTrack);
        }

        private List<vec2> CreateBoundaryTrack(double distance)
        {
            List<vec2> newTrack = new List<vec2>();

            int ptCount = mf.BoundaryList[0].fenceLine.Count;
            if (ptCount < 2) return newTrack;

            // Identical to the headland "Build Around" algorithm (btnBndLoop_Click):
            // 1. Shift each boundary point inward along its perpendicular by 'distance'.
            // 2. Reject any shifted point closer than 'distance' to any original boundary
            //    point — removes self-intersecting loops at concave corners naturally.
            // 3. Reject consecutive points less than 1 m apart.
            // 4. Close the loop by appending the first accepted point twice,
            //    then MakePointMinimumSpacing fills gaps (e.g. at sharp convex corners)
            //    and CalculateHeadings recalculates all bisector headings.

            double distSq = distance * distance * 0.999;

            List<vec3> rawList = new List<vec3>();

            for (int i = 0; i < ptCount; i++)
            {
                double heading = mf.BoundaryList[0].fenceLine[i].heading;

                vec3 pt = new vec3(
                    mf.BoundaryList[0].fenceLine[i].easting - (Math.Sin(glm.PIBy2 + heading) * distance),
                    mf.BoundaryList[0].fenceLine[i].northing - (Math.Cos(glm.PIBy2 + heading) * distance),
                    heading);

                bool add = true;

                for (int j = 0; j < ptCount; j++)
                {
                    double check = glm.DistanceSquared(
                        pt.northing, pt.easting,
                        mf.BoundaryList[0].fenceLine[j].northing,
                        mf.BoundaryList[0].fenceLine[j].easting);

                    if (check < distSq)
                    {
                        add = false;
                        break;
                    }
                }

                if (!add) continue;

                if (rawList.Count > 0)
                {
                    double spacingSq = (pt.easting - rawList[rawList.Count - 1].easting) * (pt.easting - rawList[rawList.Count - 1].easting)
                                     + (pt.northing - rawList[rawList.Count - 1].northing) * (pt.northing - rawList[rawList.Count - 1].northing);
                    if (spacingSq > 1.0)
                        rawList.Add(pt);
                }
                else
                {
                    rawList.Add(pt);
                }
            }

            if (rawList.Count < 4)
            {
                foreach (var p in rawList) newTrack.Add(new vec2(p.easting, p.northing));
                return newTrack;
            }

            // Close the loop and smooth, exactly as the headland algorithm does.
            rawList.Add(new vec3(rawList[0]));
            rawList.Add(new vec3(rawList[0]));

            CurveSmoothing.MakePointMinimumSpacing(ref rawList, 1.2);
            CurveSmoothing.CalculateHeadings(ref rawList);

            foreach (var p in rawList)
                newTrack.Add(new vec2(p.easting, p.northing));

            return newTrack;
        }

    }
}