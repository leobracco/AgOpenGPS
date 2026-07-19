// Dibujo GL de las clases de guiado CYouTurn/CTram/CRecordedPath. Vivía
// embebido en cada clase: se movió acá para que queden sin OpenTK
// (traspaso portabilidad — DrawLib es la capa GL, Windows-only hasta el
// port a GL ES/Skia). Lo que la clase no expone (lineWidth del ABLine,
// camSetDistance de la cámara) entra por parámetro desde el caller GL.

using AgOpenGPS.Core.Drawing;
using AgOpenGPS.Core.DrawLib;
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

    public static class CameraDrawExtensions
    {
        public static void SetLookAt(this AgOpenGPS.Core.Camera camera, double lookAtX, double lookAtY, double directionHintInDegrees)
        {
            //back the camera up
            GLW.Translate(0, 0, -camera.DistanceToLookAt);

            GLW.RotateX(camera.PitchInDegrees);
            GLW.Translate(camera.PanX, camera.PanY);

            if (camera.FollowDirectionHint)
            {
                GLW.RotateZ(directionHintInDegrees);
            }
            GLW.Translate(-lookAtX, -lookAtY, 0.0);
        }
    }

    public static class ABLineDrawExtensions
    {
        private static readonly ColorRgba newAbLineColor = new ColorRgba(0.95f, 0.70f, 0.50f);
        private static readonly ColorRgba pointsTextGreen = new ColorRgba(0.2f, 0.950f, 0.20f);
        private static readonly ColorRgba pointARed = new ColorRgba(0.95f, 0.0f, 0.0f);
        private static readonly ColorRgba pointBCyan = new ColorRgba(0.0f, 0.90f, 0.95f);
        private static readonly ColorRgba referenceLineRed = new ColorRgba(0.930f, 0.2f, 0.2f);
        private static readonly ColorRgba shadowAreaGray = new ColorRgba(0.5f, 0.5f, 0.5f, 0.2f);
        private static readonly ColorRgba shadowLinesGray = new ColorRgba(0.55f, 0.55f, 0.55f, 0.2f);
        //estilo PilotX: guía activa blanca, vecinas gris claro (pedido 2026-07-16)
        private static readonly ColorRgba currentAbLinePurple = new ColorRgba(0.98f, 0.98f, 0.98f);
        private static readonly ColorRgba extraGuidelinesBlack = new ColorRgba(0.0f, 0.0f, 0.0f, 0.5f);
        private static readonly ColorRgba extraGuidelinesGreen = new ColorRgba(0.72f, 0.75f, 0.72f, 0.6f);

        public static void DrawABLineNew(this CABLine abLine)
        {
            IABLineHost mf = abLine.mf;
            Font textFont = ((ITextFontHost)mf).TextFont;

            //ABLine currently being designed
            GeoCoord[] desLineEndPoints = { abLine.desLineEndA.ToGeoCoord(), abLine.desLineEndB.ToGeoCoord() };

            GLW.SetLineWidth(abLine.lineWidth);
            GLW.SetColor(newAbLineColor);
            GLW.DrawLinesPrimitive(desLineEndPoints);

            GLW.SetColor(pointsTextGreen);
            textFont.DrawText3D(abLine.desPtA.easting, abLine.desPtA.northing, "&A", mf.CamHeading);
            textFont.DrawText3D(abLine.desPtB.easting, abLine.desPtB.northing, "&B", mf.CamHeading);
        }

        public static void DrawABLines(this CABLine abLine)
        {
            IABLineHost mf = abLine.mf;
            Font textFont = ((ITextFontHost)mf).TextFont;

            // Draw AB Points
            CTrk track = mf.Tracks[mf.TrackIdx];
            GLW.SetPointSize(8.0f);
            GLW.BeginPointsPrimitive();

            GLW.SetColor(pointBCyan);
            GLW.Vertex2(track.ptB.ToGeoCoord());
            GLW.SetColor(pointARed);
            GLW.Vertex2(track.ptA.ToGeoCoord());
            GLW.EndPrimitive();

            GLW.DrawPoint(track.ptA.ToGeoCoord());

            if (!abLine.isMakingABLine)
            {
                textFont.DrawText3D(track.ptA.easting, track.ptA.northing, "&A", mf.CamHeading);
                textFont.DrawText3D(track.ptB.easting, track.ptB.northing, "&B", mf.CamHeading);
            }

            GLW.SetPointSize(1.0f);

            // PilotX: centrar el tramo dibujado en el vehículo. La guía
            // matemática es infinita pero el dibujo eran ±2000 m fijos desde
            // el origen del lote: manejando más lejos la línea "se cortaba"
            // (visto con el sim a 6 km). Se proyecta la posición actual sobre
            // la línea y se dibuja ±abLength alrededor de esa proyección.
            double sinH = Math.Sin(abLine.abHeading), cosH = Math.Cos(abLine.abHeading);
            double distAlong = ((mf.GuidanceLookPos.easting - abLine.currentLinePtA.easting) * sinH)
                             + ((mf.GuidanceLookPos.northing - abLine.currentLinePtA.northing) * cosH);
            vec3 drawPtA = new vec3(
                abLine.currentLinePtA.easting + (sinH * (distAlong - abLine.abLength)),
                abLine.currentLinePtA.northing + (cosH * (distAlong - abLine.abLength)), abLine.abHeading);
            vec3 drawPtB = new vec3(
                abLine.currentLinePtA.easting + (sinH * (distAlong + abLine.abLength)),
                abLine.currentLinePtA.northing + (cosH * (distAlong + abLine.abLength)), abLine.abHeading);

            //Draw reference AB line (recentrada igual que la actual)
            double refAlong = ((mf.GuidanceLookPos.easting - track.ptA.easting) * sinH)
                            + ((mf.GuidanceLookPos.northing - track.ptA.northing) * cosH);
            GeoCoord[] abEndPoints = {
                new vec2(track.ptA.easting + (sinH * (refAlong - abLine.abLength)),
                         track.ptA.northing + (cosH * (refAlong - abLine.abLength))).ToGeoCoord(),
                new vec2(track.ptA.easting + (sinH * (refAlong + abLine.abLength)),
                         track.ptA.northing + (cosH * (refAlong + abLine.abLength))).ToGeoCoord()
            };
            GLW.SetLineWidth(4.0f);
            GLW.EnableLineStipple();
            GLW.SetLineStipple(1, 0x0F00);
            GLW.SetColor(referenceLineRed);
            GLW.DrawLinesPrimitive(abEndPoints);
            GLW.DisableLineStipple();

            // shadow
            double shadowOffset = abLine.isHeadingSameWay ? mf.Tool.offset : -mf.Tool.offset;
            GeoCoord ptA = drawPtA.ToGeoCoord();
            GeoCoord ptB = drawPtB.ToGeoCoord();
            GeoDir abDir = new GeoDir(abLine.abHeading);
            GeoDir perpendicalurRightDir = abDir.PerpendicularRight;
            GeoDelta rightOffset = (shadowOffset + 0.5 * mf.Tool.width) * perpendicalurRightDir;
            GeoDelta leftOffset = (shadowOffset - 0.5 * mf.Tool.width) * perpendicalurRightDir;

            GeoCoord[] shadowCoords = {
                ptA + leftOffset,
                ptA + rightOffset,
                ptB + rightOffset,
                ptB + leftOffset
            };

            GLW.SetColor(shadowAreaGray);
            GLW.DrawTriangleFanPrimitive(shadowCoords);
            GLW.SetColor(shadowLinesGray);
            GLW.SetLineWidth(1.0f);
            GLW.DrawLineLoopPrimitive(shadowCoords);

            //draw current AB Line
            GeoCoord[] currentAbLine = { drawPtA.ToGeoCoord(), drawPtB.ToGeoCoord() };
            LineStyle blackBackgroundStyle = new LineStyle(abLine.lineWidth * 3, Colors.Black);
            LineStyle purpleForgroundStyle = new LineStyle(abLine.lineWidth, currentAbLinePurple);
            GLW.DrawLinesPrimitiveLayered(
                currentAbLine,
                blackBackgroundStyle,
                purpleForgroundStyle);

            if (mf.IsSideGuideLines && mf.CamSetDistance > mf.Tool.width * -400)
            {
                double toolWidth = mf.Tool.width - mf.Tool.overlap;
                GeoLineSegment currentLine = new GeoLineSegment(drawPtA.ToGeoCoord(), drawPtB.ToGeoCoord());
                GeoDir perpendicularRightDir = currentLine.Direction.PerpendicularRight;
                GeoLineSegment[] lines = new GeoLineSegment[2 * abLine.numGuideLines];
                int linesIndex = 0;

                double oddOffset = 2 * (abLine.isHeadingSameWay ? mf.Tool.offset : -mf.Tool.offset);
                for (int i = 1; i <= abLine.numGuideLines; i += 2)
                {
                    GeoLineSegment rightOddLine = currentLine.Shifted((toolWidth * i + oddOffset) * perpendicularRightDir);
                    GeoLineSegment leftOddLine = currentLine.Shifted((toolWidth * -i + oddOffset) * perpendicularRightDir);
                    lines[linesIndex++] = rightOddLine;
                    lines[linesIndex++] = leftOddLine;
                }
                for (int i = 2; i <= abLine.numGuideLines; i += 2)
                {
                    GeoLineSegment rightEvenLine = currentLine.Shifted((toolWidth * i) * perpendicularRightDir);
                    GeoLineSegment leftEvenLine = currentLine.Shifted((toolWidth * -i) * perpendicularRightDir);
                    lines[linesIndex++] = rightEvenLine;
                    lines[linesIndex++] = leftEvenLine;
                }
                LineStyle extraGuidelinesBackgroundStyle = new LineStyle(abLine.lineWidth * 3, extraGuidelinesBlack);
                LineStyle extraGuidelinesForegroundStyle = new LineStyle(abLine.lineWidth, extraGuidelinesGreen);
                GLW.DrawLinesPrimitiveLayered(
                    lines,
                    extraGuidelinesBackgroundStyle,
                    extraGuidelinesForegroundStyle);
            }
            mf.DrawYouTurn();

            GLW.SetPointSize(1.0f);
            GLW.SetLineWidth(1.0f);
        }
    }

    public static class ABCurveDrawExtensions
    {
        public static void DrawCurveNew(this CABCurve curve)
        {
            if (curve.desList.Count > 0)
            {
                GL.Color3(0.95f, 0.42f, 0.750f);
                GL.LineWidth(4.0f);
                GL.Begin(PrimitiveType.LineStrip);
                for (int h = 0; h < curve.desList.Count; h++)
                {
                    GL.Vertex2(curve.desList[h].easting, curve.desList[h].northing);
                }
                GL.End();

                GL.Enable(EnableCap.LineStipple);
                GL.LineStipple(1, 0x0F00);
                GL.Begin(PrimitiveType.Lines);
                GL.Color3(0.99f, 0.99f, 0.0);
                GL.Vertex2(curve.desList[curve.desList.Count - 1].easting, curve.desList[curve.desList.Count - 1].northing);
                GL.Vertex2(curve.mf.PivotAxlePos.easting, curve.mf.PivotAxlePos.northing);
                GL.End();

                GL.Disable(EnableCap.LineStipple);
            }
        }

        public static void DrawCurve(this CABCurve curve)
        {
            IABCurveHost mf = curve.mf;

            if (mf.TrackIdx == -1) return;

            int ptCount = mf.Tracks[mf.TrackIdx].curvePts.Count;

            if (mf.Tracks[mf.TrackIdx].mode != TrackMode.waterPivot)
            {
                if (mf.Tracks[mf.TrackIdx].curvePts == null || mf.Tracks[mf.TrackIdx].curvePts.Count == 0) return;

                GL.LineWidth(4);
                GL.Color3(0.96, 0.2f, 0.2f);
                GL.Begin(PrimitiveType.Lines);

                for (int h = 0; h < ptCount; h++)
                {
                    GL.Vertex2(mf.Tracks[mf.TrackIdx].curvePts[h].easting, mf.Tracks[mf.TrackIdx].curvePts[h].northing);
                }
                GL.End();

                GL.Color3(0.40f, 0.90f, 0.95f);
                Font textFont = ((ITextFontHost)mf).TextFont;
                textFont.DrawText3D(mf.Tracks[mf.TrackIdx].ptA.easting, mf.Tracks[mf.TrackIdx].ptA.northing, "&A", mf.CamHeading);
                textFont.DrawText3D(mf.Tracks[mf.TrackIdx].ptB.easting, mf.Tracks[mf.TrackIdx].ptB.northing, "&B", mf.CamHeading);

                if (curve.isSmoothWindowOpen)
                {
                    if (curve.smooList == null || curve.smooList.Count == 0) return;

                    GL.LineWidth(mf.ABLine.lineWidth);
                    GL.Color3(0.930f, 0.92f, 0.260f);
                    GL.Begin(PrimitiveType.Lines);
                    for (int h = 0; h < curve.smooList.Count; h++)
                    {
                        GL.Vertex2(curve.smooList[h].easting, curve.smooList[h].northing);
                    }
                    GL.End();
                }
            }
            if (!curve.isSmoothWindowOpen) //normal. Smoothing window is not open.
            {
                if (curve.curList.Count > 0)
                {
                    GL.LineWidth(mf.ABLine.lineWidth * 3);
                    GL.Color3(0, 0, 0);

                    //ablines and curves are a line - the rest a loop
                    if (mf.Tracks[mf.TrackIdx].mode <= TrackMode.Curve)
                    {
                        GL.Begin(PrimitiveType.LineStrip);
                    }
                    else
                    {
                        if (mf.Tracks[mf.TrackIdx].mode == TrackMode.waterPivot)
                        {
                            GL.PointSize(15.0f);
                            GL.Begin(PrimitiveType.Points);
                            GL.Vertex2(
                                mf.Tracks[mf.TrackIdx].ptA.easting,
                                mf.Tracks[mf.TrackIdx].ptA.northing);
                            GL.End();
                        }

                        GL.Begin(PrimitiveType.LineLoop);
                    }

                    for (int h = 0; h < curve.curList.Count; h++)
                    {
                        GL.Vertex2(curve.curList[h].easting, curve.curList[h].northing);
                    }
                    GL.End();

                    GL.LineWidth(mf.ABLine.lineWidth);
                    //estilo PilotX: guía activa blanca (pedido 2026-07-16)
                    GL.Color3(0.98f, 0.98f, 0.98f);
                    if (mf.Tracks[mf.TrackIdx].mode <= TrackMode.Curve)
                    {
                        GL.Begin(PrimitiveType.LineStrip);
                    }
                    else
                    {
                        if (mf.Tracks[mf.TrackIdx].mode == TrackMode.waterPivot)
                        {
                            GL.PointSize(15.0f);
                            GL.Begin(PrimitiveType.Points);
                            GL.Vertex2(
                                mf.Tracks[mf.TrackIdx].ptA.easting,
                                mf.Tracks[mf.TrackIdx].ptA.northing);
                            GL.End();
                        }

                        GL.Begin(PrimitiveType.LineLoop);
                    }

                    for (int h = 0; h < curve.curList.Count; h++)
                    {
                        GL.Vertex2(curve.curList[h].easting, curve.curList[h].northing);
                    }
                    GL.End();

                    mf.DrawYouTurn();
                }
            }

            if (curve.guideArr.Count > 0)
            {
                GL.LineWidth(mf.ABLine.lineWidth * 3);
                GL.Color3(0, 0, 0);

                if (mf.Tracks[mf.TrackIdx].mode != TrackMode.bndCurve)
                    GL.Begin(PrimitiveType.LineStrip);
                else
                    GL.Begin(PrimitiveType.LineLoop);

                for (int i = 0; i < curve.guideArr.Count; i++)
                {
                    GL.Begin(PrimitiveType.LineStrip);
                    for (int h = 0; h < curve.guideArr[i].Count; h++)
                    {
                        GL.Vertex2(curve.guideArr[i][h].easting, curve.guideArr[i][h].northing);
                    }
                    GL.End();
                }
                GL.End();

                GL.LineWidth(mf.ABLine.lineWidth);
                //estilo PilotX: guías vecinas gris claro (pedido 2026-07-16)
                GL.Color4(0.72, 0.75, 0.72, 0.6);

                if (mf.Tracks[mf.TrackIdx].mode != TrackMode.bndCurve)
                    GL.Begin(PrimitiveType.LineStrip);
                else
                    GL.Begin(PrimitiveType.LineLoop);

                for (int i = 0; i < curve.guideArr.Count; i++)
                {
                    GL.Begin(PrimitiveType.LineStrip);
                    for (int h = 0; h < curve.guideArr[i].Count; h++)
                    {
                        GL.Vertex2(curve.guideArr[i][h].easting, curve.guideArr[i][h].northing);
                    }
                    GL.End();
                }
                GL.End();
            }

            GL.PointSize(1.0f);
        }
    }

    public static class VehicleDrawExtensions
    {
        public static void DrawVehicle(this CVehicle vehicle)
        {
            IVehicleHost mf = vehicle.mf;
            IVehicleTexturesHost textures = (IVehicleTexturesHost)mf;
            VehicleConfig VehicleConfig = vehicle.VehicleConfig;

            GL.Rotate(glm.toDegrees(-mf.FixHeading), 0.0, 0.0, 1.0);
            //mf.font.DrawText3D(0, 0, "&TGF");
            if (mf.IsFirstHeadingSet && !mf.Tool.isToolFrontFixed)
            {
                // Draw the rigid hitch
                double hitchLengthFromPivot = mf.Tool.GetHitchLengthFromVehiclePivot();
                double hitchHeading = mf.Tool.GetHitchHeadingFromVehiclePivot(hitchLengthFromPivot);
                double hitchAngleOffset = hitchHeading - mf.FixHeading;
                double sinOffset = Math.Sin(hitchAngleOffset);
                double cosOffset = Math.Cos(hitchAngleOffset);

                XyCoord TransformVertex(double lateral, double longitudinal)
                {
                    double x = lateral * cosOffset + longitudinal * sinOffset;
                    double y = longitudinal * cosOffset - lateral * sinOffset;
                    return new XyCoord(x, y);
                }

                XyCoord[] vertices;
                if (!mf.Tool.isToolRearFixed)
                {
                    vertices = new XyCoord[]
                    {
                        TransformVertex(0, hitchLengthFromPivot), TransformVertex(0, 0)
                    };
                }
                else
                {
                    vertices = new XyCoord[]
                    {
                        TransformVertex(-0.35, hitchLengthFromPivot), TransformVertex(-0.35, 0),
                        TransformVertex( 0.35, hitchLengthFromPivot), TransformVertex( 0.35, 0)
                    };
                }
                LineStyle backgroundLineStyle = new LineStyle(4, Colors.Black);
                LineStyle foregroundLineStyle = new LineStyle(1, Colors.HitchRigidColor);
                GLW.DrawLinesPrimitiveLayered(vertices, backgroundLineStyle, foregroundLineStyle);
            }

            //draw the vehicle Body
            if (!mf.IsFirstHeadingSet && mf.HeadingFromSource != "Dual")
            {
                GL.Color4(1, 1, 1, 0.75);
                textures.QuestionMarkTexture.Draw(new XyCoord(1.0, 5.0), new XyCoord(5.0, 1.0));
            }

            //3 vehicle types  tractor=0 harvestor=1 Articulated=2
            ColorRgba vehicleColor = new ColorRgba(
                VehicleConfig.Color.Red,
                VehicleConfig.Color.Green,
                VehicleConfig.Color.Blue,
                (byte)(255.0 * VehicleConfig.Opacity));

            if (VehicleConfig.IsImage)
            {
                if (VehicleConfig.Type == VehicleType.Tractor)
                {
                    //vehicle body
                    GLW.SetColor(vehicleColor);

                    AckermannAngles(
                        -(mf.IsSimEnabled ? mf.Sim.steerangleAve : mf.Mc.actualSteerAngleDegrees),
                        out double leftAckermann,
                        out double rightAckermann);
                    XyCoord tractorCenter = new XyCoord(0.0, 0.5 * VehicleConfig.Wheelbase);
                    textures.TractorTexture.DrawCentered(
                        tractorCenter,
                        new XyDelta(VehicleConfig.TrackWidth, -1.0 * VehicleConfig.Wheelbase));

                    //right wheel
                    GL.PushMatrix();
                    GL.Translate(0.5 * VehicleConfig.TrackWidth, VehicleConfig.Wheelbase, 0);
                    GL.Rotate(rightAckermann, 0, 0, 1);

                    XyDelta frontWheelDelta = new XyDelta(0.5 * VehicleConfig.TrackWidth, -0.75 * VehicleConfig.Wheelbase);
                    textures.FrontWheelTexture.DrawCenteredAroundOrigin(frontWheelDelta);

                    GL.PopMatrix();

                    //Left Wheel
                    GL.PushMatrix();

                    GL.Translate(-VehicleConfig.TrackWidth * 0.5, VehicleConfig.Wheelbase, 0);
                    GL.Rotate(leftAckermann, 0, 0, 1);

                    textures.FrontWheelTexture.DrawCenteredAroundOrigin(frontWheelDelta);

                    GL.PopMatrix();
                    //disable, straight color
                }
                else if (VehicleConfig.Type == VehicleType.Harvester)
                {
                    //vehicle body

                    AckermannAngles(
                        mf.IsSimEnabled ? mf.Sim.steerAngle : mf.Mc.actualSteerAngleDegrees,
                        out double leftAckermannAngle,
                        out double rightAckermannAngle);
                    ColorRgba harvesterWheelColor = new ColorRgba(
                        Colors.HarvesterWheelColor.Red,
                        Colors.HarvesterWheelColor.Green,
                        Colors.HarvesterWheelColor.Blue,
                        (byte)(255.0 * VehicleConfig.Opacity));
                    GLW.SetColor(harvesterWheelColor);
                    //right wheel
                    GL.PushMatrix();
                    GL.Translate(VehicleConfig.TrackWidth * 0.5, -VehicleConfig.Wheelbase, 0);
                    GL.Rotate(rightAckermannAngle, 0, 0, 1);
                    XyDelta forntWheelDelta = new XyDelta(0.25 * VehicleConfig.TrackWidth, 0.5 * VehicleConfig.Wheelbase);
                    textures.FrontWheelTexture.DrawCenteredAroundOrigin(forntWheelDelta);
                    GL.PopMatrix();

                    //Left Wheel
                    GL.PushMatrix();
                    GL.Translate(-VehicleConfig.TrackWidth * 0.5, -VehicleConfig.Wheelbase, 0);
                    GL.Rotate(leftAckermannAngle, 0, 0, 1);
                    textures.FrontWheelTexture.DrawCenteredAroundOrigin(forntWheelDelta);
                    GL.PopMatrix();

                    GLW.SetColor(vehicleColor);
                    textures.HarvesterTexture.DrawCenteredAroundOrigin(
                        new XyDelta(VehicleConfig.TrackWidth, -1.5 * VehicleConfig.Wheelbase));
                    //disable, straight color
                }
                else if (VehicleConfig.Type == VehicleType.Articulated)
                {
                    double modelSteerAngle = 0.5 * (mf.IsSimEnabled ? mf.Sim.steerAngle : mf.Mc.actualSteerAngleDegrees);
                    GLW.SetColor(vehicleColor);

                    XyDelta articulated = new XyDelta(VehicleConfig.TrackWidth, -0.65 * VehicleConfig.Wheelbase);
                    GL.PushMatrix();
                    GL.Translate(0, -VehicleConfig.Wheelbase * 0.5, 0);
                    GL.Rotate(modelSteerAngle, 0, 0, 1);
                    textures.ArticulatedRearTexture.DrawCenteredAroundOrigin(articulated);
                    GL.PopMatrix();

                    GL.PushMatrix();
                    GL.Translate(0, VehicleConfig.Wheelbase * 0.5, 0);
                    GL.Rotate(-modelSteerAngle, 0, 0, 1);
                    textures.ArticulatedFrontTexture.DrawCenteredAroundOrigin(articulated);
                    GL.PopMatrix();
                }
            }
            else
            {
                GL.Color4(1.2, 1.20, 0.0, VehicleConfig.Opacity);
                GL.Begin(PrimitiveType.TriangleFan);
                GL.Vertex2(0, VehicleConfig.AntennaPivot);
                GL.Vertex2(1.0, -0);
                GL.Color4(0.0, 1.20, 1.22, VehicleConfig.Opacity);
                GL.Vertex2(0, VehicleConfig.Wheelbase);
                GL.Color4(1.220, 0.0, 1.2, VehicleConfig.Opacity);
                GL.Vertex2(-1.0, -0);
                GL.Vertex2(1.0, -0);
                GL.End();

                GL.LineWidth(3);
                GL.Color3(0.12, 0.12, 0.12);
                GL.Begin(PrimitiveType.LineLoop);
                {
                    GL.Vertex2(-1.0, 0);
                    GL.Vertex2(1.0, 0);
                    GL.Vertex2(0, VehicleConfig.Wheelbase);
                }
                GL.End();
            }
            if (mf.CamSetDistance > -75 && mf.IsFirstHeadingSet)
            {
                //draw the bright antenna dot
                // background layer
                GLW.SetPointSize(16.0f);
                GLW.SetColor(Colors.Black);
                GLW.DrawPoint(-VehicleConfig.AntennaOffset, VehicleConfig.AntennaPivot, 0.1);
                // foreground layer
                GLW.SetPointSize(10.0f);
                GLW.SetColor(Colors.AntennaColor);
                GLW.DrawPoint(-VehicleConfig.AntennaOffset, VehicleConfig.AntennaPivot, 0.1);
            }

            if (mf.Bnd.isBndBeingMade && mf.Bnd.isDrawAtPivot)
            {
                if (mf.Bnd.isDrawRightSide)
                {
                    GL.LineWidth(2);
                    GL.Color3(0.0, 1.270, 0.0);
                    GL.Begin(PrimitiveType.LineStrip);
                    {
                        GL.Vertex2(0.0, 0.0);
                        GL.Color3(1.270, 1.220, 0.20);
                        GL.Vertex2(mf.Bnd.createBndOffset, 0);
                        GL.Vertex2(mf.Bnd.createBndOffset * 0.75, 0.25);
                    }
                    GL.End();
                }
                //draw on left side
                else
                {
                    GL.LineWidth(2);
                    GL.Color3(0.0, 1.270, 0.0);
                    GL.Begin(PrimitiveType.LineStrip);
                    {
                        GL.Vertex2(0.0, 0.0);
                        GL.Color3(1.270, 1.220, 0.20);
                        GL.Vertex2(-mf.Bnd.createBndOffset, 0);
                        GL.Vertex2(-mf.Bnd.createBndOffset * 0.75, 0.25);
                    }
                    GL.End();
                }
            }

            //Svenn Arrow
            if (mf.IsSvennArrowOn && mf.CamSetDistance > -1000)
            {
                //double offs = distanceFromCurrentLinePivot de la curva * 0.3;
                double svennDist = mf.CamSetDistance * -0.07;
                double svennWidth = svennDist * 0.22;
                GLW.SetLineWidth(mf.ABLineWidth);
                GLW.SetColor(Colors.SvenArrowColor);
                XyCoord[] vertices = {
                    new XyCoord(svennWidth, VehicleConfig.Wheelbase + svennDist),
                    new XyCoord(0, VehicleConfig.Wheelbase + svennWidth + 0.5 + svennDist),
                    new XyCoord(-svennWidth, VehicleConfig.Wheelbase + svennDist)
                };
                GLW.DrawLineStripPrimitive(vertices);
            }
            GL.LineWidth(1);
        }

        private static void AckermannAngles(double wheelAngle, out double leftAckermannAngle, out double rightAckermannAngle)
        {
            leftAckermannAngle = wheelAngle;
            rightAckermannAngle = wheelAngle;
            if (wheelAngle > 0.0)
            {
                leftAckermannAngle *= 1.25;
            }
            else
            {
                rightAckermannAngle *= 1.25;
            }
        }
    }

    public static class ToolDrawExtensions
    {
        private static void DrawHitch(double trailingTank)
        {
            XyCoord[] vertices = {
                new XyCoord(-0.57, trailingTank),
                new XyCoord(0.0, 0.0),
                new XyCoord(0.57, trailingTank)
            };
            LineStyle backgroundLineStyle = new LineStyle(6.0f, Colors.Black);
            LineStyle foregroundLineStyle = new LineStyle(1.0f, Colors.HitchColor);
            GLW.DrawLineLoopPrimitiveLayered(vertices, backgroundLineStyle, foregroundLineStyle);
        }

        private static void DrawTrailingHitch(CTool tool, double trailingTool)
        {
            XyCoord[] vertices = {
                new XyCoord(-0.65 + tool.offset, trailingTool),
                new XyCoord(0.0, 0.0),
                new XyCoord(0.65 + tool.offset, trailingTool)
            };
            LineStyle backgroundLineStyle = new LineStyle(6.0f, Colors.Black);
            LineStyle foregroundLineStyle = new LineStyle(1.0f, Colors.HitchTrailingColor);
            GLW.DrawLineLoopPrimitiveLayered(vertices, backgroundLineStyle, foregroundLineStyle);
        }

        public static void DrawTool(this CTool tool)
        {
            IToolHost mf = tool.mf;

            //translate and rotate at pivot axle
            GL.Translate(mf.PivotAxlePos.easting, mf.PivotAxlePos.northing, 0);
            GL.PushMatrix();

            //translate down to the hitch pin
            double pivotToHitchLength = tool.GetHitchLengthFromVehiclePivot();
            double hitchHeading = tool.GetHitchHeadingFromVehiclePivot(pivotToHitchLength);
            GL.Translate(
                Math.Sin(hitchHeading) * pivotToHitchLength,
                Math.Cos(hitchHeading) * pivotToHitchLength,
                0);

            //settings doesn't change trailing hitch length if set to rigid, so do it here
            double trailingTank, trailingTool;
            if (tool.isToolTrailing)
            {
                trailingTank = tool.tankTrailingHitchLength;
                trailingTool = tool.trailingHitchLength;
            }
            else { trailingTank = 0; trailingTool = 0; }

            // if there is a trailing tow between hitch
            if (tool.isToolTBT && tool.isToolTrailing)
            {
                //rotate to tank heading
                GL.Rotate(glm.toDegrees(-mf.TankPos.heading), 0.0, 0.0, 1.0);

                DrawHitch(trailingTank);

                GL.Color4(1, 1, 1, 0.75);
                XyCoord toolAxleCenter = new XyCoord(0.0, trailingTank);
                XyDelta deltaToU1V1 = new XyDelta(1.5, 1.0);
                ((IToolTexturesHost)mf).ToolAxleTexture.DrawCentered(toolAxleCenter, deltaToU1V1);

                //move down the tank hitch, unwind, rotate to section heading
                GL.Translate(0.0, trailingTank, 0.0);
                GL.Rotate(glm.toDegrees(mf.TankPos.heading), 0.0, 0.0, 1.0);
            }
            GL.Rotate(glm.toDegrees(-mf.ToolPivotPos.heading), 0.0, 0.0, 1.0);

            //draw the hitch if trailing
            if (tool.isToolTrailing)
            {
                DrawTrailingHitch(tool, trailingTool);

                if (Math.Abs(tool.trailingToolToPivotLength) > 1 && mf.CamSetDistance > -100)
                {
                    tool.textRotate += (mf.Sim.stepDistance);
                    GL.Color4(1, 1, 1, 0.75);
                    XyCoord rightTire00 = new XyCoord(0.75 + tool.offset, trailingTool + 0.51);
                    XyCoord rightTire11 = new XyCoord(1.4 + tool.offset, trailingTool - 0.51);
                    XyCoord leftTire00 = new XyCoord(-0.75 + tool.offset, trailingTool + 0.51);
                    XyCoord lefttTire11 = new XyCoord(-1.4 + tool.offset, trailingTool - 0.51);
                    Texture2D tireTexture = ((IToolTexturesHost)mf).TireTexture;
                    tireTexture.Draw(rightTire00, rightTire11);
                    tireTexture.Draw(leftTire00, lefttTire11);
                }
                trailingTool -= tool.trailingToolToPivotLength;
            }

            if (mf.IsJobStarted)
            {
                //look ahead lines
                GL.LineWidth(3);
                GL.Begin(PrimitiveType.Lines);

                //lookahead section on
                GL.Color3(0.20f, 0.7f, 0.2f);
                GL.Vertex2(tool.farLeftPosition, tool.lookAheadDistanceOnPixelsLeft * 0.1 + trailingTool);
                GL.Vertex2(tool.farRightPosition, tool.lookAheadDistanceOnPixelsRight * 0.1 + trailingTool);

                //lookahead section off
                GL.Color3(0.70f, 0.2f, 0.2f);
                GL.Vertex2(tool.farLeftPosition, tool.lookAheadDistanceOffPixelsLeft * 0.1 + trailingTool);
                GL.Vertex2(tool.farRightPosition, tool.lookAheadDistanceOffPixelsRight * 0.1 + trailingTool);

                if (mf.IsHydLiftOn)
                {
                    GL.Color3(0.70f, 0.2f, 0.72f);
                    GL.Vertex2(mf.Section[0].positionLeft, (mf.HydLiftLookAheadDistanceLeft * 0.1) + trailingTool);
                    GL.Vertex2(mf.Section[tool.numOfSections - 1].positionRight, (mf.HydLiftLookAheadDistanceRight * 0.1) + trailingTool);
                }
                GL.End();
            }

            //draw the sections
            GL.LineWidth(2);

            double hite = mf.CamSetDistance / -250;
            if (hite > 4) hite = 4;
            if (hite < 1) hite = 1;

            for (int j = 0; j < tool.numOfSections; j++)
            {
                //if section is on, green, if off, red color
                if (mf.Section[j].isSectionOn)
                {
                    if (mf.Section[j].sectionBtnState == btnStates.Auto)
                    {
                        if (mf.Section[j].isMappingOn) GL.Color3(0.0f, 0.95f, 0.0f);
                        else GL.Color3(0.970f, 0.30f, 0.970f);
                    }
                    else GL.Color3(0.97, 0.97, 0);
                }
                else
                {
                    if (!mf.Section[j].isMappingOn) GL.Color3(0.950f, 0.2f, 0.2f);
                    else GL.Color3(0.00f, 0.250f, 0.97f);
                }

                double mid = (mf.Section[j].positionRight - mf.Section[j].positionLeft) / 2 + mf.Section[j].positionLeft;
                XyCoord[] vertices = {
                    new XyCoord(mf.Section[j].positionLeft, trailingTool),
                    new XyCoord(mf.Section[j].positionLeft, trailingTool - hite),
                    new XyCoord(mid, trailingTool - hite * 1.5),
                    new XyCoord(mf.Section[j].positionRight, trailingTool - hite),
                    new XyCoord(mf.Section[j].positionRight, trailingTool),
                };
                GLW.DrawTriangleFanPrimitive(vertices);

                if (mf.CamSetDistance > -tool.width * 200)
                {
                    GLW.SetColor(Colors.Black);
                    GLW.DrawLineLoopPrimitive(vertices);
                }
            }

            //zones
            if (!tool.isSectionsNotZones && tool.zones > 0 && mf.CamSetDistance > -150)
            {
                GL.Begin(PrimitiveType.Lines);
                for (int i = 1; i < tool.zones; i++)
                {
                    GL.Color3(0.5f, 0.80f, 0.950f);
                    GL.Vertex2(mf.Section[tool.zoneRanges[i]].positionLeft, trailingTool - 0.4);
                    GL.Vertex2(mf.Section[tool.zoneRanges[i]].positionLeft, trailingTool + 0.2);
                }
                GL.End();
            }

            //tram Dots
            if (tool.isDisplayTramControl && mf.Tram.displayMode != 0)
            {
                if (mf.CamSetDistance > -300)
                {
                    if (mf.CamSetDistance > -100)
                        GL.PointSize(12);
                    else GL.PointSize(8);

                    ColorRgba rightMarkerColor = ((mf.Tram.controlByte) & 1) != 0 ? Colors.TramMarkerOnColor : Colors.Black;
                    ColorRgba leftMarkerColor = ((mf.Tram.controlByte) & 2) != 0 ? Colors.TramMarkerOnColor : Colors.Black;
                    double rightX = mf.Tram.isOuter ? tool.farRightPosition - mf.Tram.halfWheelTrack : mf.Tram.halfWheelTrack;
                    double leftX = mf.Tram.isOuter ? tool.farLeftPosition + mf.Tram.halfWheelTrack : -mf.Tram.halfWheelTrack;
                    // section markers
                    GL.Begin(PrimitiveType.Points);
                    GLW.SetColor(rightMarkerColor);
                    GL.Vertex2(rightX, trailingTool);
                    GLW.SetColor(leftMarkerColor);
                    GL.Vertex2(leftX, trailingTool);
                    GL.End();
                }
            }

            GL.PopMatrix();
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
