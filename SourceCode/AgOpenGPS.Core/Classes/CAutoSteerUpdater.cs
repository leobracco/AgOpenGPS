using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using System;

namespace AgOpenGPS
{
    /// <summary>
    /// Arma y envía el PGN de posición corregida (lat/lon/heading) y el PGN 254
    /// de autosteer (velocidad, distancia a la línea, ángulo de dirección),
    /// incluida la selección de línea AB/curva activa y el promedio de cross
    /// track error. Vivía embebido en UpdateFixPosition (Position.designer.cs):
    /// se movió a Core porque es cálculo/armado de bytes puro sobre objetos ya
    /// portados (trk/ABLine/curve/ct/recPath/vehicle/mc/isobus) — traspaso
    /// portabilidad, bloque 9 matriz Android (2026-07-20). Los toques UI
    /// (click del botón AutoSteer, timed message, timer del simulador) cruzan
    /// por IAutoSteerHost.
    /// </summary>
    public class CAutoSteerUpdater
    {
        private readonly IAutoSteerHost mf;

        public CAutoSteerUpdater(IAutoSteerHost host)
        {
            mf = host;
        }

        // ---- Guiado del implemento (nivel A, ver CompensacionImplemento) ----

        /// <summary>Estado de la corrección del implemento (lo lee el state/diagnóstico).</summary>
        public CompensacionImplemento GuiadoImplemento { get; } = new CompensacionImplemento();

        /// <summary>
        /// Salida de emergencia (--sin-guiado-implemento en el Engine): pisa el
        /// setting y deja el guiado como siempre, sin recompilar.
        /// </summary>
        public bool GuiadoImplementoBloqueado { get; set; }

        /// <summary>true si el setting lo pide y nadie lo bloqueó.</summary>
        public bool GuiadoImplementoActivo =>
            !GuiadoImplementoBloqueado && Properties.Settings.Default.setAS_guiadoImplemento == 1;

        // ---- Asistente de calibración de la dirección ----

        /// <summary>
        /// Asistente de calibración (EngineSteerCalService). null = el PGN 254
        /// sale como siempre. Con asistente, solo mientras pide motor el 254
        /// lleva su setpoint y su velocidad (ver IAsistenteDireccionMotor).
        /// </summary>
        public SteerCal.IAsistenteDireccionMotor AsistenteDireccion { get; set; }

        // ---- Manejo libre: velocidad falsa del PGN 254 ----

        /// <summary>
        /// Velocidad (en décimas de km/h) que va en el PGN 254 con el manejo
        /// libre prendido. El tractor está parado, pero el firmware AiO con
        /// &lt; 0,2 km/h no mueve la rueda, así que hay que mandarle algo.
        /// AOG mandaba 8 km/h: con Keya como WAS el firmware corrige el cero
        /// contra el GPS por encima de 1,2 km/h (con el tractor parado eso lo
        /// corre) y además esa velocidad sale por el pin de pulso de velocidad.
        /// baja=true (setAS_freeDriveVelocidadBaja, de fábrica) manda 0,5 km/h:
        /// mueve el motor y no toca ninguna de las dos cosas. baja=false vuelve
        /// a los 8 km/h de siempre (salida de emergencia).
        /// </summary>
        public static int VelocidadManejoLibreX10(bool baja) => baja ? 5 : 80;

        private long ultimoTickImplemento;
        private bool ultimoEstadoImplemento;

        /// <summary>
        /// Avanza la corrección del implemento un paso (con el dt real entre
        /// PGN) y devuelve los puntos que van a GetCurrent*. Apagado, en
        /// contorno, en U-turn o marcha atrás devuelve los reales tal cual.
        /// </summary>
        private void PivotesParaGuia(bool esCurva, out vec3 pivote, out vec3 eje)
        {
            pivote = mf.PivotAxlePos;
            eje = mf.SteerAxlePos;

            bool activo = GuiadoImplementoActivo;
            CTool tool = mf.Tool;

            long ahora = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = ultimoTickImplemento == 0 ? 0
                : (ahora - ultimoTickImplemento) / (double)System.Diagnostics.Stopwatch.Frequency;
            ultimoTickImplemento = ahora;

            if (activo != ultimoEstadoImplemento)
            {
                ultimoEstadoImplemento = activo;
                Log.EventWriter("Guiado del implemento: " + (activo ? "ACTIVO (nivel A)" : "APAGADO"));
                if (activo && tool != null)
                {
                    string aviso = CompensacionImplemento.ValidarPerfil(tool.isToolTrailing, tool.hitchLength,
                        tool.trailingHitchLength, tool.tankTrailingHitchLength, tool.isToolTBT);
                    if (aviso != null) Log.EventWriter("Guiado del implemento — revisar perfil: " + aviso);
                }
            }

            if (!activo || tool == null)
            {
                GuiadoImplemento.Paso(false, false, 0, default, dt);
                return;
            }

            bool anular = mf.IsYouTurnTriggered || mf.IsReverse;
            double curvatura = 0;
            if (esCurva && !anular)
            {
                CABCurve curve = mf.Curve;
                try
                {
                    curvatura = CompensacionImplemento.CurvaturaEnMira(curve.curList, pivote, curve.isHeadingSameWay);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // La curva se rearmó en el medio: este ciclo va sin curvatura
                    // (la rampa de 5 cm/s se encarga de que no se note).
                    curvatura = 0;
                }
            }

            var geo = GeometriaImplemento.DesdeTool(tool, tool.GetHitchLengthFromVehiclePivot());
            GuiadoImplemento.Paso(true, anular, curvatura, geo, dt);

            pivote = GuiadoImplemento.Aplicar(pivote);
            eje = GuiadoImplemento.Aplicar(eje);
        }

        public void SendCorrectedPositionPgn()
        {
            CNMEA pn = mf.Pn;

            Wgs84 latLon = mf.AppModel.LocalPlane.ConvertGeoCoordToWgs84(pn.fix.ToGeoCoord());
            byte[] correctedPosition = new byte[30];
            correctedPosition[0] = 0x80;
            correctedPosition[1] = 0x81;
            correctedPosition[2] = 0x7F;
            correctedPosition[3] = 0x64;
            correctedPosition[4] = 24;
            Buffer.BlockCopy(BitConverter.GetBytes(latLon.Longitude), 0, correctedPosition, 5, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(latLon.Latitude), 0, correctedPosition, 13, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(glm.toDegrees(mf.GpsHeading)), 0, correctedPosition, 21, 8);
            mf.SendPgnToLoop(correctedPosition);
        }

        public void BuildAndSendAutoSteerPgn()
        {
            CNMEA pn = mf.Pn;
            CTrack trk = mf.Trk;
            CModuleComm mc = mf.Mc;
            CPGN_FE p_254 = mf.P254;

            //preset the values
            mf.GuidanceLineDistanceOff = 32000;

            if (mf.Ct.isContourBtnOn)
            {
                mf.Ct.DistanceFromContourLine(mf.PivotAxlePos, mf.SteerAxlePos);
                // En contorno no hay modelo de curva: sin corrección, y que no
                // quede una vieja colgada para cuando se vuelva a una guía.
                GuiadoImplemento.Paso(false, false, 0, default, 0);
            }
            else
            {
                //auto track routine
                if (trk.isAutoTrack && !mf.IsBtnAutoSteerOn && trk.autoTrack3SecTimer >= 1)
                {
                    trk.autoTrack3SecTimer = 0;
                    int lastIndex = trk.idx;
                    trk.idx = trk.FindClosestRefTrack(mf.SteerAxlePos);
                    if (lastIndex != trk.idx)
                    {
                        mf.Curve.isCurveValid = false;
                        mf.ABLine.isABValid = false;
                    }
                }

                //like normal
                if (trk.gArr != null && trk.gArr.Count > 0 && trk.idx >= 0 && trk.idx < trk.gArr.Count)
                {
                    // Elección de pasada (Build*) con el pivote REAL; el
                    // seguimiento (GetCurrent*) con el pivote virtual del
                    // guiado del implemento — igual al real si está apagado.
                    if (trk.gArr[trk.idx].mode == TrackMode.AB)
                    {
                        mf.ABLine.BuildCurrentABLineList(mf.PivotAxlePos);
                        PivotesParaGuia(false, out vec3 pivote, out vec3 eje);
                        mf.ABLine.GetCurrentABLine(pivote, eje);
                    }
                    else
                    {
                        mf.Curve.BuildCurveCurrentList(mf.PivotAxlePos);
                        PivotesParaGuia(true, out vec3 pivote, out vec3 eje);
                        mf.Curve.GetCurrentCurveLine(pivote, eje);
                    }
                }
            }

            // autosteer at full speed of updates

            //if the whole path driving driving process is green
            if (mf.RecPath.isDrivingRecordedPath) mf.RecPath.UpdatePosition();

            // Candado del manejo libre: prendido, el módulo recibe status=1 con
            // un ángulo fijo puesto a mano, sin guía y sin mirar dónde está el
            // tractor. Eso es para probar la dirección PARADO. Si el tractor
            // arranca, se apaga solo acá — es el único punto por el que pasan
            // TODOS los caminos que prenden el modo (pantalla Dirección del Hub,
            // FormSteer nativo), y corre en cada PGN, no solo al clickear.
            if (mf.Vehicle.isInFreeDriveMode &&
                Math.Abs(mf.AvgSpeed) > mf.Vehicle.functionSpeedLimit)
            {
                mf.Vehicle.isInFreeDriveMode = false;
                mf.Vehicle.driveFreeSteerAngle = 0;
                mf.Vehicle.freeDriveWatchdog = -1;
                Log.EventWriter("Manejo libre apagado solo: el tractor supero el limite de velocidad");
            }

            // Segundo candado: si lo prendió una pantalla remota (watchdog ≥0),
            // esa pantalla tiene que seguir ahí. Cada consulta suya lo recarga;
            // si deja de contestar —WebView cerrado de golpe, PilotX caído, red
            // cortada— el volante deja de estar bajo control de nadie y el modo
            // se apaga. Lo prendido desde el FormSteer nativo queda en −1 y no
            // pasa por acá: esa ventana vive mientras el modo vive.
            if (mf.Vehicle.isInFreeDriveMode && mf.Vehicle.freeDriveWatchdog >= 0)
            {
                if (--mf.Vehicle.freeDriveWatchdog < 0)
                {
                    mf.Vehicle.isInFreeDriveMode = false;
                    mf.Vehicle.driveFreeSteerAngle = 0;
                    Log.EventWriter("Manejo libre apagado solo: la pantalla dejo de responder");
                }
            }

            // Asistente de calibración: se consulta en cada PGN (así avanza
            // al ritmo del GPS aunque la pantalla no consulte) y solo toma el
            // 254 mientras pide motor. Con el manejo libre prendido no manda
            // nunca (el asistente además se bloquea solo en ese caso).
            double spAsistente = 0, velAsistente = 0;
            bool asistenteMueve = false;
            if (AsistenteDireccion != null)
            {
                try
                {
                    asistenteMueve = AsistenteDireccion.QuiereMotor(out spAsistente, out velAsistente)
                                     && !mf.Vehicle.isInFreeDriveMode;
                }
                catch (Exception ex)
                {
                    // Falla cerrado: sin asistente sano no se mueve nada.
                    asistenteMueve = false;
                    Log.EventWriter("Asistente de direccion: error al consultar, motor sin asistente: " + ex.Message);
                }
            }

            if (asistenteMueve)
            {
                // Velocidad del asistente (0,5 km/h falsos parado, la real
                // andando): la placa con < 0,2 km/h no mueve la rueda.
                int vel10 = (int)Math.Round(Math.Abs(velAsistente) * 10.0);
                p_254.pgn[p_254.speedHi] = unchecked((byte)(vel10 >> 8));
                p_254.pgn[p_254.speedLo] = unchecked((byte)vel10);
                p_254.pgn[p_254.status] = 1;

                // El asistente ya lo acota a ±5°; acá se vuelve a acotar por las dudas.
                double sp = Math.Max(-5.0, Math.Min(5.0, spAsistente));
                mf.GuidanceLineSteerAngle = (Int16)Math.Round(sp * 100);
                p_254.pgn[p_254.steerAngleHi] = unchecked((byte)(mf.GuidanceLineSteerAngle >> 8));
                p_254.pgn[p_254.steerAngleLo] = unchecked((byte)(mf.GuidanceLineSteerAngle));
            }
            // If Drive button off - normal autosteer
            else if (!mf.Vehicle.isInFreeDriveMode)
            {
                //fill up0 the appropriate arrays with new values
                p_254.pgn[p_254.speedHi] = unchecked((byte)((int)(Math.Abs(mf.AvgSpeed) * 10.0) >> 8));
                p_254.pgn[p_254.speedLo] = unchecked((byte)((int)(Math.Abs(mf.AvgSpeed) * 10.0)));

                //save distance for display
                mf.LightbarDistance = mf.GuidanceLineDistanceOff;
                mf.Isobus.SetGuidanceLineDeviation(mf.GuidanceLineDistanceOff * 100);
                int currentSpeed = (int)(mf.AvgSpeed * 1000 / 3.6);  // convert from km/h to mm/s
                if (mf.IsReverse)
                {
                    currentSpeed = -currentSpeed;
                }
                mf.Isobus.SetActualSpeed(currentSpeed);
                mf.Isobus.SetTotalDistance((int)(mf.Fd.distanceUser * 1000)); // convert from meter to mm

                if (!mf.IsBtnAutoSteerOn) //32020 means auto steer is off
                {
                    mf.GuidanceLineDistanceOff = 32020;
                    p_254.pgn[p_254.status] = 0;
                }
                else p_254.pgn[p_254.status] = 1;

                if (mf.RecPath.isDrivingRecordedPath || mf.RecPath.isFollowingDubinsToPath) p_254.pgn[p_254.status] = 1;

                //convert to cm from mm and divide by 2 - lightbar
                int distanceX2;
                if (mf.GuidanceLineDistanceOff == 32020 || mf.GuidanceLineDistanceOff == 32000)
                    distanceX2 = 255;
                else
                {
                    distanceX2 = (int)(mf.GuidanceLineDistanceOff * 0.05);

                    if (distanceX2 < -127) distanceX2 = -127;
                    else if (distanceX2 > 127) distanceX2 = 127;
                    distanceX2 += 127;
                }

                p_254.pgn[p_254.lineDistance] = unchecked((byte)distanceX2);

                if (!mf.IsSimTimerEnabled)
                {
                    if (mf.IsBtnAutoSteerOn && mf.AvgSpeed > mf.Vehicle.maxSteerSpeed)
                    {
                        mf.PerformAutoSteerClick();
                        // Antes se soltaba en silencio: la cabina dice por qué.
                        mf.TimedMessageBox(3000, AvisoPiloto.TituloDesenganche,
                            "Piloto desenganchado: pasaste la velocidad máxima de " +
                            mf.Vehicle.maxSteerSpeed.ToString("N0") + " km/h.");
                        Log.EventWriter("Steer Off, Above Max Steering Speed");
                    }

                    if (mf.IsBtnAutoSteerOn && mf.AvgSpeed < mf.Vehicle.minSteerSpeed)
                    {
                        mf.MinSteerSpeedTimer++;
                        if (mf.MinSteerSpeedTimer > 80)
                        {
                            mf.PerformAutoSteerClick();
                            if (mf.IsMetric)
                                mf.TimedMessageBox(3000, AvisoPiloto.TituloDesenganche, "Piloto desenganchado: por debajo de la velocidad mínima de " + mf.Vehicle.minSteerSpeed.ToString("N1") + " km/h.");
                            else
                                mf.TimedMessageBox(3000, AvisoPiloto.TituloDesenganche, "Piloto desenganchado: por debajo de la velocidad mínima de " + Speed.KmhToMph(mf.Vehicle.minSteerSpeed).ToString("N1") + " mph.");

                            Log.EventWriter("Steer Off, Below Min Steering Speed");
                        }
                    }
                    else
                    {
                        mf.MinSteerSpeedTimer = 0;
                    }
                }

                if (!AgOpenGPS.Properties.Settings.Default.setAutoSwitchDualFixOn && mf.IsChangingDirection && mf.Ahrs.imuHeading == 99999)
                {
                    p_254.pgn[p_254.status] = 0;
                }
                //for now if backing up, turn off autosteer
                if (!mf.IsSteerInReverse)
                {
                    if (mf.IsReverse) p_254.pgn[p_254.status] = 0;
                }

                // delay on dead zone.
                if (p_254.pgn[p_254.status] == 1 && !mf.IsReverse
                    && Math.Abs(mf.GuidanceLineSteerAngle - mc.actualSteerAngleDegrees * 100) < mf.Vehicle.deadZoneHeading)
                {
                    if (mf.Vehicle.deadZoneDelayCounter > mf.Vehicle.deadZoneDelay)
                    {
                        mf.Vehicle.isInDeadZone = true;
                    }
                }
                else
                {
                    mf.Vehicle.deadZoneDelayCounter = 0;
                    mf.Vehicle.isInDeadZone = false;
                }

                if (!mf.Vehicle.isInDeadZone)
                {
                    p_254.pgn[p_254.steerAngleHi] = unchecked((byte)(mf.GuidanceLineSteerAngle >> 8));
                    p_254.pgn[p_254.steerAngleLo] = unchecked((byte)(mf.GuidanceLineSteerAngle));
                }
            }
            else //Drive button is on
            {
                // Velocidad falsa del manejo libre (ver VelocidadManejoLibreX10).
                int velFalsa10 = VelocidadManejoLibreX10(
                    AgOpenGPS.Properties.Settings.Default.setAS_freeDriveVelocidadBaja);
                p_254.pgn[p_254.speedHi] = unchecked((byte)(velFalsa10 >> 8));
                p_254.pgn[p_254.speedLo] = unchecked((byte)velFalsa10);

                //turn on status to operate
                p_254.pgn[p_254.status] = 1;

                //send the steer angle
                mf.GuidanceLineSteerAngle = (Int16)(mf.Vehicle.driveFreeSteerAngle * 100);

                p_254.pgn[p_254.steerAngleHi] = unchecked((byte)(mf.GuidanceLineSteerAngle >> 8));
                p_254.pgn[p_254.steerAngleLo] = unchecked((byte)(mf.GuidanceLineSteerAngle));
            }

            //out serial to autosteer module  //indivdual classes load the distance and heading deltas
            mf.SendPgnToLoop(p_254.pgn);

            //for average cross track error
            if (mf.GuidanceLineDistanceOff < 29000)
            {
                mf.CrossTrackError = (int)((double)mf.CrossTrackError * 0.90 + Math.Abs((double)mf.GuidanceLineDistanceOff) * 0.1);
            }
            else
            {
                mf.CrossTrackError = 0;
            }
        }
    }
}
