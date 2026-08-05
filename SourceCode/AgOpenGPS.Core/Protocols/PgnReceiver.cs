using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using System;
using System.Diagnostics;

namespace AgOpenGPS
{
    /// <summary>
    /// Parser de los PGNs entrantes desde CoreX (loopback UDP :15555).
    /// Vivía embebido en FormGPS (GPS/Forms/UDPComm.Designer.cs,
    /// ReceiveFromAgIO): se movió a Core porque es lógica pura de protocolo
    /// (traspaso portabilidad — bloque 9 matriz Android). El form conserva
    /// los sockets y la UI; el host cruza por IPgnReceiveHost.
    /// También es dueño del watchdog de sentencias (udpWatch): frena los
    /// fixes que llegan más rápido que udpWatchLimit ms.
    /// </summary>
    public class PgnReceiver
    {
        private readonly IPgnReceiveHost mf;

        /// <summary>Sentencias descartadas por llegar antes del límite.</summary>
        public int MissedSentenceCount { get; set; }

        /// <summary>Tiempo mínimo entre fixes (ms) — SetGPS_udpWatchMsec.</summary>
        public int UdpWatchLimit { get; set; } = 70;

        private readonly Stopwatch udpWatch = new Stopwatch();

        public PgnReceiver(IPgnReceiveHost host)
        {
            mf = host;
        }

        /// <summary>Arranca el watchdog (se llama una vez en el load del host).</summary>
        public void StartWatch()
        {
            udpWatch.Start();
        }

        public void ReceiveFromAgIO(byte[] data)
        {
            if (data.Length > 4 && data[0] == 0x80 && data[1] == 0x81)
            {
                int Length = Math.Max((data[4]) + 5, 5);
                if (data.Length > Length)
                {
                    byte CK_A = 0;
                    for (int j = 2; j < Length; j++)
                    {
                        CK_A += data[j];
                    }

                    if (data[Length] != (byte)CK_A)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }

                CNMEA pn = mf.Pn;
                CAHRS ahrs = mf.Ahrs;
                CModuleComm mc = mf.Mc;

                switch (data[3])
                {
                    case 0xD6:
                        {
                            // OJO: el rate-limit vale SOLO para el pipeline
                            // (UpdateFixPosition), no para los campos. Cuando el
                            // GPS manda GGA y PANDA a la vez, el parser arma DOS
                            // D6 por fix y la velocidad viaja solo en uno (el
                            // builder la "consume" con float.MaxValue): descartar
                            // el segundo PGN ENTERO dejaba vtgSpeed clavada en 0
                            // según el ORDEN de las sentencias — con ModSim 6.8.3
                            // el piloto no tomaba la línea por el gate de
                            // velocidad mínima (2026-08-05).
                            bool correrPipeline = udpWatch.ElapsedMilliseconds >= UdpWatchLimit;
                            if (!correrPipeline)
                            {
                                MissedSentenceCount++;
                            }
                            else
                            {
                                udpWatch.Reset();
                                udpWatch.Start();
                            }

                            double Lon = BitConverter.ToDouble(data, 5);
                            double Lat = BitConverter.ToDouble(data, 13);

                            if (Lon != double.MaxValue && Lat != double.MaxValue)
                            {
                                if (mf.IsSimEnabled) mf.DisableSim();

                                mf.AppModel.CurrentLatLon = new Wgs84(Lat, Lon);

                                GeoCoord fixCoord = mf.AppModel.LocalPlane.ConvertWgs84ToGeoCoord(mf.AppModel.CurrentLatLon);
                                pn.fix.northing = fixCoord.Northing;
                                pn.fix.easting = fixCoord.Easting;

                                //From dual antenna heading sentences
                                float temp = BitConverter.ToSingle(data, 21);
                                if (temp != float.MaxValue)
                                {
                                    pn.headingTrueDual = temp + pn.headingTrueDualOffset;
                                    if (pn.headingTrueDual >= 360) pn.headingTrueDual -= 360;
                                    else if (pn.headingTrueDual < 0) pn.headingTrueDual += 360;

                                    if (ahrs.isDualAsIMU) ahrs.imuHeading = pn.headingTrueDual;
                                }

                                //from single antenna sentences (VTG,RMC)
                                pn.headingTrue = BitConverter.ToSingle(data, 25);

                                //always save the speed.
                                temp = BitConverter.ToSingle(data, 29);
                                if (temp != float.MaxValue)
                                {
                                    pn.vtgSpeed = temp;
                                }

                                //roll in degrees
                                temp = BitConverter.ToSingle(data, 33);
                                if (temp != float.MaxValue)
                                {
                                    if (ahrs.isRollInvert) temp *= -1;
                                    ahrs.imuRoll = temp - ahrs.rollZero;
                                }
                                if (temp == float.MinValue)
                                    ahrs.imuRoll = 0;

                                //altitude in meters
                                temp = BitConverter.ToSingle(data, 37);
                                if (temp != float.MaxValue)
                                    pn.altitude = temp;

                                ushort sats = BitConverter.ToUInt16(data, 41);
                                if (sats != ushort.MaxValue)
                                    pn.satellitesTracked = sats;

                                byte fix = data[43];
                                if (fix != byte.MaxValue)
                                    pn.fixQuality = fix;

                                ushort hdop = BitConverter.ToUInt16(data, 44);
                                if (hdop != ushort.MaxValue)
                                    pn.hdop = hdop * 0.01;

                                ushort age = BitConverter.ToUInt16(data, 46);
                                if (age != ushort.MaxValue)
                                    pn.age = age * 0.01;

                                ushort imuHead = BitConverter.ToUInt16(data, 48);
                                if (imuHead != ushort.MaxValue)
                                {
                                    ahrs.imuHeading = imuHead;
                                    ahrs.imuHeading *= 0.1;
                                }

                                short imuRol = BitConverter.ToInt16(data, 50);
                                if (imuRol != short.MaxValue)
                                {
                                    double rollK = imuRol;
                                    if (ahrs.isRollInvert) rollK *= -0.1;
                                    else rollK *= 0.1;
                                    rollK -= ahrs.rollZero;
                                    ahrs.imuRoll = ahrs.imuRoll * ahrs.rollFilter + rollK * (1 - ahrs.rollFilter);
                                }

                                short imuPich = BitConverter.ToInt16(data, 52);
                                if (imuPich != short.MaxValue)
                                {
                                    ahrs.imuPitch = imuPich;
                                }

                                short imuYaw = BitConverter.ToInt16(data, 54);
                                if (imuYaw != short.MaxValue)
                                {
                                    ahrs.imuYawRate = imuYaw;
                                }

                                mf.OnGpsSentenceReceived();

                                if (correrPipeline) mf.UpdateFixPosition();
                            }
                        }
                        break;

                    case 0xD3: //external IMU
                        {
                            if (data.Length != 14)
                                break;
                            if (ahrs.imuRoll > 25 || ahrs.imuRoll < -25) ahrs.imuRoll = 0;
                            //Heading
                            ahrs.imuHeading = (Int16)((data[6] << 8) + data[5]);
                            ahrs.imuHeading *= 0.1;

                            //Roll
                            double rollK = (Int16)((data[8] << 8) + data[7]);

                            if (ahrs.isRollInvert) rollK *= -0.1;
                            else rollK *= 0.1;
                            rollK -= ahrs.rollZero;
                            ahrs.imuRoll = ahrs.imuRoll * ahrs.rollFilter + rollK * (1 - ahrs.rollFilter);

                            //Angular velocity
                            ahrs.angVel = (Int16)((data[10] << 8) + data[9]);
                            ahrs.angVel /= -2;

                            break;
                        }
                    case 0xD4: //imu disconnect pgn
                        {
                            if (data[5] == 1)
                            {
                                ahrs.imuHeading = 99999;

                                ahrs.imuRoll = 88888;

                                ahrs.angVel = 0;
                            }
                            break;
                        }
                    case 253: //return from autosteer module
                        {
                            //Steer angle actual
                            if (data.Length != 14)
                                break;
                            mc.actualSteerAngleChart = (Int16)((data[6] << 8) + data[5]);
                            mc.actualSteerAngleDegrees = (double)mc.actualSteerAngleChart * 0.01;

                            //Heading
                            double head253 = (Int16)((data[8] << 8) + data[7]);
                            if (head253 != 9999)
                            {
                                ahrs.imuHeading = head253 * 0.1;
                            }

                            //Roll
                            double rollK = (Int16)((data[10] << 8) + data[9]);
                            if (rollK != 8888)
                            {
                                if (ahrs.isRollInvert) rollK *= -0.1;
                                else rollK *= 0.1;
                                rollK -= ahrs.rollZero;
                                ahrs.imuRoll = ahrs.imuRoll * ahrs.rollFilter + rollK * (1 - ahrs.rollFilter);
                            }
                            //else ahrs.imuRoll = 88888;

                            //switch status
                            mc.workSwitchHigh = (data[11] & 1) == 1;
                            mc.steerSwitchHigh = (data[11] & 2) == 2;

                            //the pink steer dot reset
                            mf.OnSteerModuleTraffic();

                            //Actual PWM
                            mc.pwmDisplay = data[12];

                            break;
                        }

                    case 0xF0: // ISOBUS heartbeat
                        {
                            int length = data[4];
                            byte[] pgnData = new byte[length];
                            Array.Copy(data, 5, pgnData, 0, length);
                            mf.Isobus.DeserializeHeartbeat(pgnData);
                            break;
                        }

                    case 250:
                        {
                            if (data.Length != 14)
                                break;
                            mc.sensorData = data[5];
                            break;
                        }

                    case 221: // DD
                        {
                            //{ 0x80, 0x81, 0x7f, 221, number bytes, seconds to display, mystery byte, 98,99,100,101, CRC };
                            if (data.Length < 9) break;

                            if (mf.IsHardwareMessages)
                            {
                                string msg = System.Text.Encoding.UTF8.GetString(data, 7, data[4] - 2);
                                Log.EventWriter(msg);

                                //alerta si byte 6 == 0
                                mf.ShowHardwareMessage(msg, data[6] == 0, data[5]);
                            }
                            else
                            {
                                mf.HideHardwareMessage();
                            }
                            break;
                        }
                    case 222: // 0xDE
                        {
                            //{ 0x80, 0x81, 0x7f, 222, number bytes, mask, command CRC };
                            if (data.Length < 6) break;
                            if (((data[5] & 1) == 1)) //mask bit #0 set and command bit #0 nudge line to the 0 = left 1 = right
                            {
                                double dist = Properties.Settings.Default.setAS_snapDistance * 0.01;
                                if ((data[6] & 1) != 1) { mf.Trk.NudgeTrack(-dist); }
                                if ((data[6] & 1) == 1) { mf.Trk.NudgeTrack(dist); }
                            }
                            if (((data[5] & 2) == 2)) //mask bit #1 set and command bit #0 cycle line to the 0 = left 1 = right
                            {
                                if ((data[6] & 1) != 1) { mf.CycleLineForward(); }
                                if ((data[6] & 1) == 1) { mf.CycleLineBackward(); }
                            }

                            break;
                        }

                    #region Remote Switches
                    case 234://MTZ8302 Feb 2020
                        {
                            //Steer angle actual
                            if (data.Length != 14)
                                break;

                            Buffer.BlockCopy(data, 5, mc.ss, 1, 8);

                            mf.DoRemoteSwitches();

                            break;
                        }
                        #endregion
                }
            }
        }
    }
}
