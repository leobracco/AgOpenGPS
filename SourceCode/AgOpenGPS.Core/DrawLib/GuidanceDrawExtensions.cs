// Dibujo GL de las clases de guiado CYouTurn/CTram/CRecordedPath. Vivía
// embebido en cada clase: se movió acá para que queden sin OpenTK
// (traspaso portabilidad — DrawLib es la capa GL, Windows-only hasta el
// port a GL ES/Skia). Lo que la clase no expone (lineWidth del ABLine,
// camSetDistance de la cámara) entra por parámetro desde el caller GL.

using AgOpenGPS.Core.Models;
using OpenTK.Graphics.OpenGL;

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
