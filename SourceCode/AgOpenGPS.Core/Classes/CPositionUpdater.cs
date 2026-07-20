using AgOpenGPS.Core.Models;
using System;
using System.Globalization;

namespace AgOpenGPS
{
    /// <summary>
    /// Los pasos "hoja" del loop de posición: agregar punto de lote,
    /// agregar puntos de contorno, agregar puntos de sección/recorrido,
    /// inicializar las primeras posiciones GPS, calcular pivote/hitch/tool
    /// y el lookahead de cada sección. Vivían embebidos en FormGPS
    /// (GPS/Forms/Position.designer.cs): se movieron a Core porque son
    /// orquestación/matemática pura entre objetos ya portados (bnd/ct/
    /// recPath/curve/tool/vehicle/pn/triStrip) — traspaso portabilidad,
    /// bloque 9 matriz Android (2026-07-19). El form conserva el loop
    /// (UpdateFixPosition/TheRest) y delega estos pasos acá; el host cruza
    /// por IPositionHost.
    /// </summary>
    public class CPositionUpdater
    {
        private readonly IPositionHost mf;

        public CPositionUpdater(IPositionHost host)
        {
            mf = host;
        }

        //perimeter and boundary point generation
        public void AddBoundaryPoint()
        {
            //save the north & east as previous
            mf.PrevBoundaryPos = new vec2(mf.Pn.fix.easting, mf.Pn.fix.northing);

            CBoundary bnd = mf.Bnd;
            vec3 pivotAxlePos = mf.PivotAxlePos;

            //build the boundary line
            if (bnd.isOkToAddPoints && (!bnd.isRecBoundaryWhenSectionOn ||
                (bnd.isRecBoundaryWhenSectionOn && (mf.ManualBtnState == btnStates.On || mf.AutoBtnState == btnStates.Auto))))
            {
                if (bnd.isDrawAtPivot)
                {
                    if (bnd.isDrawRightSide)
                    {
                        //Right side
                        vec3 point = new vec3(
                            pivotAxlePos.easting + (Math.Sin(pivotAxlePos.heading - glm.PIBy2) * -bnd.createBndOffset),
                            pivotAxlePos.northing + (Math.Cos(pivotAxlePos.heading - glm.PIBy2) * -bnd.createBndOffset),
                            pivotAxlePos.heading);
                        bnd.bndBeingMadePts.Add(point);
                    }

                    //draw on left side
                    else
                    {
                        //Right side
                        vec3 point = new vec3(
                            pivotAxlePos.easting + (Math.Sin(pivotAxlePos.heading - glm.PIBy2) * bnd.createBndOffset),
                            pivotAxlePos.northing + (Math.Cos(pivotAxlePos.heading - glm.PIBy2) * bnd.createBndOffset),
                            pivotAxlePos.heading);
                        bnd.bndBeingMadePts.Add(point);
                    }
                }
                else
                {
                    //draw at tool
                    CSection[] section = mf.Section;
                    int numOfSections = mf.Tool.numOfSections;

                    if (bnd.isDrawRightSide)
                    {
                        //Right side
                        vec3 point = new vec3(section[numOfSections - 1].rightPoint.easting, section[numOfSections - 1].rightPoint.northing, 0);
                        bnd.bndBeingMadePts.Add(point);
                    }

                    //draw on left side
                    else
                    {
                        //Right side
                        vec3 point = new vec3(section[0].leftPoint.easting, section[0].leftPoint.northing, 0);
                        bnd.bndBeingMadePts.Add(point);
                    }
                }
            }
        }

        public void AddContourPoints()
        {
            CContour ct = mf.Ct;
            vec3 pivotAxlePos = mf.PivotAxlePos;

            //if (isConstantContourOn)
            {
                //record contour all the time
                //Contour Base Track.... At least One section on, turn on if not
                if (mf.PatchCounter != 0)
                {
                    //keep the line going, everything is on for recording path
                    if (ct.isContourOn) ct.AddPoint(pivotAxlePos);
                    else
                    {
                        ct.StartContourLine();
                        ct.AddPoint(pivotAxlePos);
                    }
                }

                //All sections OFF so if on, turn off
                else
                {
                    if (ct.isContourOn)
                    { ct.StopContourLine(); }
                }

                //Build contour line if close enough to a patch
                if (ct.isContourBtnOn) ct.BuildContourGuidanceLine(pivotAxlePos);
            }

            //save the north & east as previous
            mf.PrevContourPos = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
        }

        //add the points for section, contour line points, Area Calc feature
        public void AddSectionOrPathPoints()
        {
            CRecordedPath recPath = mf.RecPath;
            CABCurve curve = mf.Curve;
            vec3 pivotAxlePos = mf.PivotAxlePos;

            if (recPath.isRecordOn)
            {
                //keep minimum speed of 1.0
                double speed = mf.AvgSpeed;
                if (speed < 1.0) speed = 1.0;
                bool autoBtn = (mf.AutoBtnState == btnStates.Auto);

                recPath.recList.Add(new CRecPathPt(pivotAxlePos.easting, pivotAxlePos.northing, pivotAxlePos.heading, speed, autoBtn));
            }

            if (curve.isRecordingCurve)
            {
                curve.desList.Add(new vec3(pivotAxlePos.easting, pivotAxlePos.northing, pivotAxlePos.heading));
            }

            //save the north & east as previous
            mf.PrevSectionPos = new vec2(mf.Pn.fix.easting, mf.Pn.fix.northing);

            // if non zero, at least one section is on.
            int patchCounter = 0;
            bool isPatchesChangingColor = mf.IsPatchesChangingColor;

            //send the current and previous GPS fore/aft corrected fix to each section
            var triStrip = mf.TriStrip;
            for (int j = 0; j < triStrip.Count; j++)
            {
                if (triStrip[j] != null && triStrip[j].isDrawing)
                {
                    if (isPatchesChangingColor)
                    {
                        triStrip[j].numTriangles = 64;
                        isPatchesChangingColor = false;
                    }

                    triStrip[j].AddMappingPoint(j);
                    patchCounter++;
                }
            }

            mf.PatchCounter = patchCounter;
            mf.IsPatchesChangingColor = isPatchesChangingColor;
        }

        //the start of first few frames to initialize entire program
        public void InitializeFirstFewGPSPositions()
        {
            CNMEA pn = mf.Pn;

            if (!mf.IsFirstFixPositionSet)
            {
                if (!mf.IsJobStarted)
                {
                    pn.DefineLocalPlane(mf.AppModel.CurrentLatLon, false);
                }
                GeoCoord fixCoord = mf.AppModel.LocalPlane.ConvertWgs84ToGeoCoord(mf.AppModel.CurrentLatLon);
                pn.fix.northing = fixCoord.Northing;
                pn.fix.easting = fixCoord.Easting;
                //Draw a grid once we know where in the world we are.

                //most recent fixes
                mf.PrevFix = new vec2(pn.fix.easting, pn.fix.northing);

                //run once and return
                mf.IsFirstFixPositionSet = true;
            }
            else
            {
                mf.PrevFix = new vec2(pn.fix.easting, pn.fix.northing);

                //keep here till valid data
                if (mf.StartCounter > 20)
                {
                    mf.IsGPSPositionInitialized = true;
                    mf.LastReverseFix = new vec2(pn.fix.easting, pn.fix.northing);
                }

                //in radians
                mf.FixHeading = 0;
                vec3 toolPivotPos = mf.ToolPivotPos;
                toolPivotPos.heading = mf.FixHeading;
                mf.ToolPivotPos = toolPivotPos;

                //send out initial zero settings
                if (mf.IsGPSPositionInitialized)
                {
                    //set display accordingly
                    mf.IsDayTime = (DateTime.Now.Ticks < mf.Sunset.Ticks && DateTime.Now.Ticks > mf.Sunrise.Ticks);

                    mf.SetZoom();
                }
            }
        }

        // Unproyecta un punto de pantalla (screenX, screenY, origen arriba-izq
        // de oglMain) al plano z=0 del mundo (east/north), con las matrices GL
        // capturadas en el ultimo frame. Vivía en FormGPS.cs (shapefile-inspect-
        // click): se movió a Core porque no toca GL directo, solo lee las
        // matrices ya capturadas como datos — traspaso portabilidad, bloque 9
        // matriz Android (2026-07-19).
        public bool UnprojectMouseToGround(int screenX, int screenY, out double east, out double north)
        {
            east = 0; north = 0;
            if (!mf.GlMatricesValid) return false;

            int[] glViewport = mf.GlViewport;
            int vpW = glViewport[2];
            int vpH = glViewport[3];
            if (vpW <= 0 || vpH <= 0) return false;

            // GL viewport tiene origen abajo-izq; oglMain abajo-arriba invertido.
            double ndcX = (2.0 * (screenX - glViewport[0])) / vpW - 1.0;
            double ndcY = 1.0 - (2.0 * (screenY - glViewport[1])) / vpH;

            // mvp = modelview * projection (convention columna-major GL; el
            // producto se hace en el mismo orden en el que GL aplica M y P:
            // p_clip = P * M * p_world, entonces invirtiendo:
            // p_world = (P * M)^(-1) * p_clip = invMvp * p_clip).
            double[] mvp = Mat4Math.MulMat4(mf.GlProjection, mf.GlModelView);
            double[] inv;
            if (!Mat4Math.InvertMat4(mvp, out inv)) return false;

            double nx, ny, nz;
            if (!Mat4Math.TransformMat4Point(inv, ndcX, ndcY, -1.0, out nx, out ny, out nz)) return false;
            double fx, fy, fz;
            if (!Mat4Math.TransformMat4Point(inv, ndcX, ndcY, 1.0, out fx, out fy, out fz)) return false;

            double dz = fz - nz;
            if (Math.Abs(dz) < 1e-9) return false;
            double t = -nz / dz;
            east = nx + t * (fx - nx);
            north = ny + t * (fy - ny);
            return true;
        }

        public void TheRest()
        {
            CNMEA pn = mf.Pn;

            //positions and headings
            CalculatePositionHeading();

            //calculate lookahead at full speed, no sentence misses
            vec3 toolPos = mf.ToolPos;
            CalculateSectionLookAhead(toolPos.northing, toolPos.easting, mf.CosSectionHeading, mf.SinSectionHeading);

            //To prevent drawing high numbers of triangles, determine and test before drawing vertex
            mf.SectionTriggerDistance = glm.Distance(pn.fix, mf.PrevSectionPos);
            mf.ContourTriggerDistance = glm.Distance(pn.fix, mf.PrevContourPos);
            mf.GridTriggerDistance = glm.DistanceSquared(pn.fix, mf.PrevGridPos);

            if (mf.IsLogElevation && mf.GridTriggerDistance > 2.9 && mf.PatchCounter != 0 && mf.IsJobStarted)
            {
                vec3 pivotAxlePos = mf.PivotAxlePos;

                //grab fix and elevation
                mf.SbGrid.Append(
                    mf.AppModel.CurrentLatLon.Latitude.ToString("N7", CultureInfo.InvariantCulture) + ","
                    + mf.AppModel.CurrentLatLon.Longitude.ToString("N7", CultureInfo.InvariantCulture) + ","
                    + Math.Round((pn.altitude - mf.Vehicle.VehicleConfig.AntennaHeight), 3).ToString(CultureInfo.InvariantCulture) + ","
                    + pn.fixQuality.ToString(CultureInfo.InvariantCulture) + ","
                    + pn.fix.easting.ToString("N2", CultureInfo.InvariantCulture) + ","
                    + pn.fix.northing.ToString("N2", CultureInfo.InvariantCulture) + ","
                    + pivotAxlePos.heading.ToString("N3", CultureInfo.InvariantCulture) + ","
                    + Math.Round(mf.Ahrs.imuRoll, 3).ToString(CultureInfo.InvariantCulture) +
                    "\r\n");

                mf.PrevGridPos = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
            }

            //contour points
            if (mf.IsJobStarted && (mf.ContourTriggerDistance > mf.Tool.contourWidth
                || mf.ContourTriggerDistance > mf.SectionTriggerStepDistance))
            {
                AddContourPoints();
            }

            //section on off and points
            if (mf.SectionTriggerDistance > mf.SectionTriggerStepDistance && mf.IsJobStarted)
            {
                AddSectionOrPathPoints();
            }

            //test if travelled far enough for new boundary point
            if (mf.Bnd.isOkToAddPoints)
            {
                double boundaryDistance = glm.Distance(pn.fix, mf.PrevBoundaryPos);
                if (boundaryDistance > 1) AddBoundaryPoint();
            }
        }

        //all the hitch, pivot, section, trailing hitch, headings and fixes
        public void CalculatePositionHeading()
        {
            CNMEA pn = mf.Pn;
            CTool tool = mf.Tool;
            CVehicle vehicle = mf.Vehicle;
            double fixHeading = mf.FixHeading;

            #region pivot hitch trail

            //translate from pivot position to steer axle and pivot axle position
            //translate world to the pivot axle
            vec3 pivotAxlePos = mf.PivotAxlePos;
            pivotAxlePos.easting = pn.fix.easting - (Math.Sin(fixHeading) * vehicle.VehicleConfig.AntennaPivot);
            pivotAxlePos.northing = pn.fix.northing - (Math.Cos(fixHeading) * vehicle.VehicleConfig.AntennaPivot);
            pivotAxlePos.heading = fixHeading;
            mf.PivotAxlePos = pivotAxlePos;

            vec3 steerAxlePos = mf.SteerAxlePos;
            steerAxlePos.easting = pivotAxlePos.easting + (Math.Sin(fixHeading) * vehicle.VehicleConfig.Wheelbase);
            steerAxlePos.northing = pivotAxlePos.northing + (Math.Cos(fixHeading) * vehicle.VehicleConfig.Wheelbase);
            steerAxlePos.heading = fixHeading;
            mf.SteerAxlePos = steerAxlePos;

            //guidance look ahead distance based on time or tool width at least
            double guidanceLookDist = (Math.Max(tool.width * 0.5, mf.AvgSpeed * 0.277777 * mf.GuidanceLookAheadTime));
            vec2 guidanceLookPos = mf.GuidanceLookPos;
            guidanceLookPos.easting = pivotAxlePos.easting + (Math.Sin(fixHeading) * guidanceLookDist);
            guidanceLookPos.northing = pivotAxlePos.northing + (Math.Cos(fixHeading) * guidanceLookDist);
            mf.GuidanceLookPos = guidanceLookPos;

            //determine where the rigid vehicle hitch ends
            double hitchLengthFromPivot = tool.GetHitchLengthFromVehiclePivot();
            double hitchHeading = tool.GetHitchHeadingFromVehiclePivot(hitchLengthFromPivot);
            double hitchDistanceFromAntenna = hitchLengthFromPivot - vehicle.VehicleConfig.AntennaPivot;
            vec2 hitchPos = mf.HitchPos;
            hitchPos.easting = pn.fix.easting + (Math.Sin(hitchHeading) * hitchDistanceFromAntenna);
            hitchPos.northing = pn.fix.northing + (Math.Cos(hitchHeading) * hitchDistanceFromAntenna);
            mf.HitchPos = hitchPos;

            vec3 toolPivotPos = mf.ToolPivotPos;
            vec3 toolPos = mf.ToolPos;

            //tool attached via a trailing hitch
            if (tool.isToolTrailing)
            {
                vec3 tankPos = mf.TankPos;
                double distanceCurrentStepFix = mf.DistanceCurrentStepFix;
                int startCounter = mf.StartCounter;
                double over;
                if (tool.isToolTBT)
                {
                    //Torriem rules!!!!! Oh yes, this is all his. Thank-you
                    if (distanceCurrentStepFix != 0)
                    {
                        tankPos.heading = Math.Atan2(hitchPos.easting - tankPos.easting, hitchPos.northing - tankPos.northing);
                        if (tankPos.heading < 0) tankPos.heading += glm.twoPI;
                    }

                    ////the tool is seriously jacknifed or just starting out so just spring it back.
                    over = Math.Abs(Math.PI - Math.Abs(Math.Abs(tankPos.heading - hitchHeading) - Math.PI));

                    if (over < 2.0 && startCounter > 50)
                    {
                        tankPos.easting = hitchPos.easting + (Math.Sin(tankPos.heading) * (tool.tankTrailingHitchLength));
                        tankPos.northing = hitchPos.northing + (Math.Cos(tankPos.heading) * (tool.tankTrailingHitchLength));
                    }

                    //criteria for a forced reset to put tool directly behind vehicle
                    if (over > 2.0 | startCounter < 51)
                    {
                        tankPos.heading = hitchHeading;
                        tankPos.easting = hitchPos.easting + (Math.Sin(tankPos.heading) * (tool.tankTrailingHitchLength));
                        tankPos.northing = hitchPos.northing + (Math.Cos(tankPos.heading) * (tool.tankTrailingHitchLength));
                    }
                }
                else
                {
                    tankPos.heading = hitchHeading;
                    tankPos.easting = hitchPos.easting;
                    tankPos.northing = hitchPos.northing;
                }

                //Torriem rules!!!!! Oh yes, this is all his. Thank-you
                if (distanceCurrentStepFix != 0)
                {
                    toolPivotPos.heading = Math.Atan2(tankPos.easting - toolPivotPos.easting, tankPos.northing - toolPivotPos.northing);
                    if (toolPivotPos.heading < 0) toolPivotPos.heading += glm.twoPI;
                }

                ////the tool is seriously jacknifed or just starting out so just spring it back.
                over = Math.Abs(Math.PI - Math.Abs(Math.Abs(toolPivotPos.heading - tankPos.heading) - Math.PI));

                if (over < 1.9 && startCounter > 50)
                {
                    toolPivotPos.easting = tankPos.easting + (Math.Sin(toolPivotPos.heading) * (tool.trailingHitchLength));
                    toolPivotPos.northing = tankPos.northing + (Math.Cos(toolPivotPos.heading) * (tool.trailingHitchLength));
                }

                //criteria for a forced reset to put tool directly behind vehicle
                if (over > 1.9 | startCounter < 51)
                {
                    toolPivotPos.heading = tankPos.heading;
                    toolPivotPos.easting = tankPos.easting + (Math.Sin(toolPivotPos.heading) * (tool.trailingHitchLength));
                    toolPivotPos.northing = tankPos.northing + (Math.Cos(toolPivotPos.heading) * (tool.trailingHitchLength));
                }

                toolPos.heading = toolPivotPos.heading;
                toolPos.easting = tankPos.easting +
                    (Math.Sin(toolPivotPos.heading) * (tool.trailingHitchLength - tool.trailingToolToPivotLength));
                toolPos.northing = tankPos.northing +
                    (Math.Cos(toolPivotPos.heading) * (tool.trailingHitchLength - tool.trailingToolToPivotLength));

                mf.TankPos = tankPos;
            }

            //rigidly connected to vehicle
            else
            {
                toolPivotPos.heading = hitchHeading;
                toolPivotPos.easting = hitchPos.easting;
                toolPivotPos.northing = hitchPos.northing;

                toolPos.heading = hitchHeading;
                toolPos.easting = hitchPos.easting;
                toolPos.northing = hitchPos.northing;
            }

            mf.ToolPivotPos = toolPivotPos;
            mf.ToolPos = toolPos;

            #endregion

            //used to increase triangle countExit when going around corners, less on straight
            //pick the slow moving side edge of tool
            double distance = tool.width * 0.75;
            if (distance > 5) distance = 5;
            double twist = 1.0;
            //whichever is less
            if (tool.farLeftSpeed < tool.farRightSpeed)
            {
                twist = tool.farLeftSpeed * (tool.width / 50) / tool.farRightSpeed * (50 / tool.width);
            }
            else
            {
                twist = tool.farRightSpeed * (tool.width / 50) / tool.farLeftSpeed * (50 / tool.width);
            }

            twist *= twist;
            if (twist < 0.1) twist = 0.1;
            double sectionTriggerStepDistance = distance * twist;

            if (sectionTriggerStepDistance < 0.7) sectionTriggerStepDistance = 0.7;

            //finally fixed distance for making a curve line
            if (mf.Curve.isRecordingCurve) sectionTriggerStepDistance *= 0.5;

            mf.SectionTriggerStepDistance = sectionTriggerStepDistance;

            //precalc the sin and cos of heading * -1
            mf.SinSectionHeading = Math.Sin(-toolPivotPos.heading);
            mf.CosSectionHeading = Math.Cos(-toolPivotPos.heading);
        }

        //calculate the extreme tool left, right velocities, each section lookahead, and whether or not its going backwards
        public void CalculateSectionLookAhead(double northing, double easting, double cosHeading, double sinHeading)
        {
            CTool tool = mf.Tool;
            CSection[] section = mf.Section;
            double avgSpeed = mf.AvgSpeed;
            double gpsHz = mf.GpsHz;
            double toolPivotHeading = mf.ToolPivotPos.heading;

            //calculate left side of section 1
            vec2 left = new vec2();
            vec2 right = left;
            double leftSpeed = 0, rightSpeed = 0;

            //speed max for section kmh*0.277 to m/s * 10 cm per pixel * 1.7 max speed
            double meterPerSecPerPixel = Math.Abs(avgSpeed) * 4.5;

            //now loop all the section rights and the one extreme left
            for (int j = 0; j < tool.numOfSections; j++)
            {
                if (j == 0)
                {
                    //only one first left point, the rest are all rights moved over to left
                    section[j].leftPoint = new vec2(cosHeading * (section[j].positionLeft) + easting, sinHeading * (section[j].positionLeft) + northing);

                    left = section[j].leftPoint - section[j].lastLeftPoint;

                    //save a copy for next time
                    section[j].lastLeftPoint = section[j].leftPoint;

                    //get the speed for left side only once
                    leftSpeed = left.GetLength() * gpsHz * 10;
                    if (leftSpeed > meterPerSecPerPixel) leftSpeed = meterPerSecPerPixel;
                }
                else
                {
                    //right point from last section becomes this left one
                    section[j].leftPoint = section[j - 1].rightPoint;
                    left = section[j].leftPoint - section[j].lastLeftPoint;

                    //save a copy for next time
                    section[j].lastLeftPoint = section[j].leftPoint;

                    //Save the slower of the 2
                    if (leftSpeed > rightSpeed) leftSpeed = rightSpeed;
                }

                section[j].rightPoint = new vec2(cosHeading * (section[j].positionRight) + easting,
                                    sinHeading * (section[j].positionRight) + northing);

                //now we have left and right for this section
                right = section[j].rightPoint - section[j].lastRightPoint;

                //save a copy for next time
                section[j].lastRightPoint = section[j].rightPoint;

                //grab vector length and convert to meters/sec/10 pixels per meter
                rightSpeed = right.GetLength() * gpsHz * 10;
                if (rightSpeed > meterPerSecPerPixel) rightSpeed = meterPerSecPerPixel;

                //Is section outer going forward or backward
                double head = left.HeadingXZ();

                if (head < 0) head += glm.twoPI;

                if (Math.PI - Math.Abs(Math.Abs(head - toolPivotHeading) - Math.PI) > glm.PIBy2)
                {
                    if (leftSpeed > 0) leftSpeed *= -1;
                }

                head = right.HeadingXZ();
                if (head < 0) head += glm.twoPI;
                if (Math.PI - Math.Abs(Math.Abs(head - toolPivotHeading) - Math.PI) > glm.PIBy2)
                {
                    if (rightSpeed > 0) rightSpeed *= -1;
                }

                double sped = 0;
                //save the far left and right speed in m/sec averaged over 20%
                if (j == 0)
                {
                    sped = (leftSpeed * 0.1);
                    if (sped < 0.1) sped = 0.1;
                    tool.farLeftSpeed = tool.farLeftSpeed * 0.7 + sped * 0.3;
                }
                if (j == tool.numOfSections - 1)
                {
                    sped = (rightSpeed * 0.1);
                    if (sped < 0.1) sped = 0.1;
                    tool.farRightSpeed = tool.farRightSpeed * 0.7 + sped * 0.3;
                }

                //choose fastest speed
                if (leftSpeed > rightSpeed)
                {
                    sped = leftSpeed;
                    leftSpeed = rightSpeed;
                }
                else sped = rightSpeed;
                section[j].speedPixels = section[j].speedPixels * 0.7 + sped * 0.3;
            }
        }
    }
}
