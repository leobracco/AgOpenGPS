// Dibujo GL de las clases de guiado CYouTurn/CTram/CRecordedPath. Vivía
// embebido en cada clase: se movió acá para que queden sin OpenTK
// (traspaso portabilidad — DrawLib es la capa GL, Windows-only hasta el
// port a GL ES/Skia). Lo que la clase no expone (lineWidth del ABLine,
// camSetDistance de la cámara) entra por parámetro desde el caller GL.

using AgOpenGPS.Core.Models;
using OpenTK.Graphics.OpenGL;
using System;

namespace AgOpenGPS
{
    public static class YouTurnDrawExtensions
    {
        public static void DrawYouTurn(this CYouTurn yt, int lineWidth)
        {
            if (yt.ytList.Count < 3) return;

            GL.PointSize(lineWidth + 2);

            if (yt.isYouTurnTriggered)
                GL.Color3(0.95f, 0.5f, 0.95f);
            else if (yt.isOutOfBounds)
                GL.Color3(0.9495f, 0.395f, 0.325f);
            else
                GL.Color3(0.395f, 0.925f, 0.30f);

            GL.Begin(PrimitiveType.Points);
            for (int i = 0; i < yt.ytList.Count; i++)
            {
                GL.Vertex2(yt.ytList[i].easting, yt.ytList[i].northing);
            }
            GL.End();
        }
    }

    public static class TramDrawExtensions
    {
        public static void DrawTram(this CTram tram, double camSetDistance)
        {
            if (camSetDistance > -500) GL.LineWidth(10);
            else GL.LineWidth(6);

            GL.Color4(0, 0, 0, tram.alpha);

            if (tram.displayMode.IncludesFillTracks())
            {
                DrawFillTracks(tram);
            }

            if (tram.displayMode.IncludesBoundaryTracks())
            {
                DrawBoundaryTracks(tram);
            }

            if (camSetDistance > -500) GL.LineWidth(4);
            else GL.LineWidth(2);

            GL.Color4(0.930f, 0.72f, 0.73530f, tram.alpha);

            if (tram.displayMode.IncludesFillTracks())
            {
                DrawFillTracks(tram);
            }
            if (tram.displayMode.IncludesBoundaryTracks())
            {
                DrawBoundaryTracks(tram);
            }
        }

        private static void DrawFillTracks(CTram tram)
        {
            if (tram.tramList.Count > 0)
            {
                for (int i = 0; i < tram.tramList.Count; i++)
                {
                    GL.Begin(PrimitiveType.LineStrip);
                    for (int h = 0; h < tram.tramList[i].Count; h++)
                    {
                        GL.Vertex2(tram.tramList[i][h].easting, tram.tramList[i][h].northing);
                    }
                    GL.End();
                }
            }
        }

        private static void DrawBoundaryTracks(CTram tram)
        {
            if (tram.tramBndOuterArr.Count > 0)
            {
                GL.Begin(PrimitiveType.LineLoop);
                for (int h = 0; h < tram.tramBndOuterArr.Count; h++) GL.Vertex3(tram.tramBndOuterArr[h].easting, tram.tramBndOuterArr[h].northing, 0);
                GL.End();
                GL.Begin(PrimitiveType.LineLoop);
                for (int h = 0; h < tram.tramBndInnerArr.Count; h++) GL.Vertex3(tram.tramBndInnerArr[h].easting, tram.tramBndInnerArr[h].northing, 0);
                GL.End();
            }
        }
    }

    public static class BoundaryDrawExtensions
    {
        public static void DrawFenceLines(this CBoundary bnd)
        {
            IBoundaryHost mf = bnd.mf;

            if (!mf.Mc.isOutOfBounds)
            {
                GL.Color4(0, 0, 0, 0.8);
                GL.LineWidth(6);

                for (int i = 0; i < bnd.bndList.Count; i++)
                {
                    bnd.bndList[i].fenceLineEar.DrawPolygon();
                }

                GL.Color4(0.95f, 0.44f, 0.350f, 0.8f);
                GL.LineWidth(2);

                for (int i = 0; i < bnd.bndList.Count; i++)
                {
                    bnd.bndList[i].fenceLineEar.DrawPolygon();
                }
            }
            else
            {
                GL.LineWidth(mf.ABLineWidth * 3);
                GL.Color3(0.95f, 0.25f, 0.250f);

                for (int i = 0; i < bnd.bndList.Count; i++)
                {
                    bnd.bndList[i].fenceLineEar.DrawPolygon();
                }
            }

            if (bnd.bndBeingMadePts.Count > 0)
            {
                //the boundary so far
                vec3 pivot = mf.PivotAxlePos;
                GL.LineWidth(mf.ABLineWidth);
                GL.Color3(0.825f, 0.22f, 0.90f);
                GL.Begin(PrimitiveType.LineStrip);
                for (int h = 0; h < bnd.bndBeingMadePts.Count; h++)
                {
                    GL.Vertex2(bnd.bndBeingMadePts[h].easting, bnd.bndBeingMadePts[h].northing);
                }
                GL.Color3(0.295f, 0.972f, 0.290f);
                GL.Vertex2(bnd.bndBeingMadePts[0].easting, bnd.bndBeingMadePts[0].northing);
                GL.End();

                //line from last point to pivot marker
                GL.Color3(0.825f, 0.842f, 0.0f);
                GL.Enable(EnableCap.LineStipple);
                GL.LineStipple(1, 0x0700);
                GL.Begin(PrimitiveType.LineStrip);

                if (bnd.isDrawAtPivot)
                {
                    if (bnd.isDrawRightSide)
                    {
                        GL.Vertex2(bnd.bndBeingMadePts[0].easting, bnd.bndBeingMadePts[0].northing);

                        GL.Vertex2(
                            pivot.easting + (Math.Sin(pivot.heading - glm.PIBy2) * -bnd.createBndOffset),
                            pivot.northing + (Math.Cos(pivot.heading - glm.PIBy2) * -bnd.createBndOffset));
                        GL.Vertex2(
                            bnd.bndBeingMadePts[bnd.bndBeingMadePts.Count - 1].easting,
                            bnd.bndBeingMadePts[bnd.bndBeingMadePts.Count - 1].northing);
                    }
                    else
                    {
                        GL.Vertex2(bnd.bndBeingMadePts[0].easting, bnd.bndBeingMadePts[0].northing);
                        GL.Vertex2(
                            pivot.easting + (Math.Sin(pivot.heading - glm.PIBy2) * bnd.createBndOffset),
                            pivot.northing + (Math.Cos(pivot.heading - glm.PIBy2) * bnd.createBndOffset));
                        GL.Vertex2(
                            bnd.bndBeingMadePts[bnd.bndBeingMadePts.Count - 1].easting,
                            bnd.bndBeingMadePts[bnd.bndBeingMadePts.Count - 1].northing);
                    }
                }
                else //draw from tool
                {
                    if (bnd.isDrawRightSide)
                    {
                        GL.Vertex2(bnd.bndBeingMadePts[0].easting, bnd.bndBeingMadePts[0].northing);
                        GL.Vertex2(
                            mf.Section[mf.ToolNumOfSections - 1].rightPoint.easting,
                            mf.Section[mf.ToolNumOfSections - 1].rightPoint.northing);
                        GL.Vertex2(
                            bnd.bndBeingMadePts[bnd.bndBeingMadePts.Count - 1].easting,
                            bnd.bndBeingMadePts[bnd.bndBeingMadePts.Count - 1].northing);
                    }
                    else
                    {
                        GL.Vertex2(bnd.bndBeingMadePts[0].easting, bnd.bndBeingMadePts[0].northing);
                        GL.Vertex2(mf.Section[0].leftPoint.easting, mf.Section[0].leftPoint.northing);
                        GL.Vertex2(
                            bnd.bndBeingMadePts[bnd.bndBeingMadePts.Count - 1].easting,
                            bnd.bndBeingMadePts[bnd.bndBeingMadePts.Count - 1].northing);
                    }
                }
                GL.End();
                GL.Disable(EnableCap.LineStipple);

                //boundary points
                GL.Color3(0.0f, 0.95f, 0.95f);
                GL.PointSize(6.0f);
                GL.Begin(PrimitiveType.Points);
                for (int h = 0; h < bnd.bndBeingMadePts.Count; h++)
                {
                    GL.Vertex2(bnd.bndBeingMadePts[h].easting, bnd.bndBeingMadePts[h].northing);
                }
                GL.End();
            }
        }
    }

    public static class ContourDrawExtensions
    {
        //draw the red follow me line
        public static void DrawContourLine(this CContour ct, int lineWidth, bool isPureDisplayOn, bool isStanleyUsed)
        {
            int ptCount = ct.ctList.Count;
            if (ptCount < 2) return;
            GL.LineWidth(lineWidth);
            GL.Color3(0.98f, 0.2f, 0.980f);
            GL.Begin(PrimitiveType.LineStrip);
            for (int h = 0; h < ptCount; h++)
            {
                GL.Vertex2(ct.ctList[h].easting, ct.ctList[h].northing);
            }
            GL.End();

            GL.PointSize(lineWidth);
            GL.Begin(PrimitiveType.Points);

            GL.Color3(0.87f, 08.7f, 0.25f);
            for (int h = 0; h < ptCount; h++)
            {
                GL.Vertex2(ct.ctList[h].easting, ct.ctList[h].northing);
            }

            GL.End();

            //Draw the captured ref strip, red if locked
            if (ct.isLocked)
            {
                GL.Color3(0.983f, 0.92f, 0.420f);
                GL.LineWidth(4);
            }
            else
            {
                GL.Color3(0.3f, 0.982f, 0.0f);
                GL.LineWidth(lineWidth);
            }

            if (ct.stripNum > -1)
            {
                GL.Begin(PrimitiveType.Points);
                for (int h = 0; h < ct.stripList[ct.stripNum].Count; h++)
                {
                    GL.Vertex2(ct.stripList[ct.stripNum][h].easting, ct.stripList[ct.stripNum][h].northing);
                }
                GL.End();
            }

            GL.Color3(0.35f, 0.30f, 0.90f);
            GL.PointSize(6.0f);
            GL.Begin(PrimitiveType.Points);
            GL.Vertex2(ct.stripList[ct.stripNum][ct.pt].easting, ct.stripList[ct.stripNum][ct.pt].northing);
            GL.End();

            if (isPureDisplayOn && ct.distanceFromCurrentLinePivot != 32000 && !isStanleyUsed)
            {
                //Draw lookahead Point
                GL.PointSize(6.0f);
                GL.Begin(PrimitiveType.Points);

                GL.Color3(1.0f, 0.95f, 0.095f);
                GL.Vertex2(ct.goalPointCT.easting, ct.goalPointCT.northing);
                GL.End();
                GL.PointSize(1.0f);
            }
        }
    }

    public static class RecordedPathDrawExtensions
    {
        public static void DrawRecordedLine(this CRecordedPath recPath)
        {
            int ptCount = recPath.recList.Count;
            if (ptCount < 1) return;
            GL.LineWidth(1);
            GL.Color3(0.98f, 0.92f, 0.460f);
            GL.Begin(PrimitiveType.LineStrip);
            for (int h = 0; h < ptCount; h++)
            {
                GL.Vertex2(recPath.recList[h].easting, recPath.recList[h].northing);
            }
            GL.End();

            if (!recPath.isRecordOn)
            {
                //Draw lookahead Point
                GL.PointSize(16.0f);
                GL.Begin(PrimitiveType.Points);

                GL.Color3(1.0f, 0.5f, 0.95f);
                GL.Vertex2(recPath.recList[recPath.currentPositonIndex].easting, recPath.recList[recPath.currentPositonIndex].northing);
                GL.End();
                GL.PointSize(1.0f);
            }
        }

        public static void DrawDubins(this CRecordedPath recPath)
        {
            if (recPath.shuttleDubinsList.Count > 1)
            {
                GL.PointSize(2);
                GL.Color3(0.298f, 0.96f, 0.2960f);
                GL.Begin(PrimitiveType.Points);
                for (int h = 0; h < recPath.shuttleDubinsList.Count; h++)
                {
                    GL.Vertex2(recPath.shuttleDubinsList[h].easting, recPath.shuttleDubinsList[h].northing);
                }
                GL.End();
            }
        }
    }
}
