// ============================================================================
// NTRIPComm.Designer.cs — Wrapper WinForms sobre NtripClientService (portable).
// El cliente TCP al caster (conexión, auth, GGA periódica, watchdog,
// reconexión) vive en AgroParallel.Services.NtripClientService
// (netstandard2.0). Acá queda solo: UI (labels/botón), metering hacia el
// GPS (queue + timer), y las variantes Radio / Serial-Pass que dependen
// de puertos serie Windows.
// ============================================================================

using System;
using System.Net;
using System.Windows.Forms;
using System.IO.Ports;
using System.Collections.Generic;
using AgLibrary.Logging;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgIO
{
    public partial class FormLoop
    {
        //for the NTRIP CLient counting
        private int ntripCounter = 10;

        // Cliente NTRIP portable (solo modo caster TCP; radio/serial abajo).
        private NtripClientService ntripService;
        private bool isNtripServiceStarted;

        private string mount;
        private string username;
        private string password;

        public string broadCasterIP;
        private int broadCasterPort;

        private int sendGGAInterval = 0;

        public uint tripBytes = 0;
        private int toUDP_Port = 0;
        private int NTRIP_Watchdog = 100;

        public bool isNTRIP_RequiredOn = false;
        public bool isNTRIP_Connected = false;
        public bool isNTRIP_Starting = false;
        public bool isNTRIP_Connecting = false;
        public bool isNTRIP_Sending = false;
        public bool isRunGGAInterval = false;

        public bool isRadio_RequiredOn = false;
        public bool isSerialPass_RequiredOn = false;
        internal SerialPort spRadio = new SerialPort("Radio", 9600, Parity.None, 8, StopBits.One);

        List<int> rList = new List<int>();
        List<int> aList = new List<int>();

        //NTRIP metering
        Queue<byte> rawTrip = new Queue<byte>();

        private void EnsureNtripService()
        {
            if (ntripService != null) return;

            ntripService = new NtripClientService();
            ntripService.OnRtcmData += data =>
            {
                try { BeginInvoke((MethodInvoker)(() => OnAddMessage(data))); }
                catch { }
            };
            ntripService.OnGgaSent += () =>
            {
                try { BeginInvoke((MethodInvoker)(() => isNTRIP_Sending = true)); }
                catch { }
            };
        }

        //set up connection to Caster
        private void DoNTRIPSecondRoutine()
        {
            //count up the ntrip clock only if everything is alive
            if (isNTRIP_RequiredOn || isRadio_RequiredOn || isSerialPass_RequiredOn)
            {
                IncrementNTRIPWatchDog();
            }

            //Have we NTRIP connection (caster TCP → servicio portable)
            if (isNTRIP_RequiredOn)
            {
                if (!isNtripServiceStarted && !isNTRIP_Starting && ntripCounter > 20)
                {
                    StartNTRIP();
                }

                if (isNtripServiceStarted)
                {
                    ntripService.SecondTick();
                    isNTRIP_Connected = ntripService.IsConnected;
                    isNTRIP_Connecting = ntripService.IsConnecting;
                }
            }

            if ((isRadio_RequiredOn || isSerialPass_RequiredOn) && !isNTRIP_Connected && !isNTRIP_Connecting)
            {
                if (!isNTRIP_Starting)
                {
                    StartNTRIP();
                }
            }

            if (isNTRIP_Connecting && ntripCounter > 29)
            {
                TimedMessageBox(1500, "Connection Problem", "Not Connecting To Caster");
                ReconnectRequest();
            }

            if (isNTRIP_RequiredOn || isRadio_RequiredOn)
            {
                //pbarNtripMenu.Value = unchecked((byte)(tripBytes * 0.02));
                lblNTRIPBytes.Text = ((tripBytes >> 10)).ToString("###,###,### kb");

                //Bypass if sleeping
                if (focusSkipCounter != 0)
                {
                    //update byte counter and up counter
                    if (ntripCounter > 59) btnStartStopNtrip.Text = (ntripCounter >> 6) + " Min";
                    else if (ntripCounter < 60 && ntripCounter > 25) btnStartStopNtrip.Text = ntripCounter + " Secs";
                    else btnStartStopNtrip.Text = "In " + (Math.Abs(ntripCounter - 25)) + " secs";

                    //watchdog for Ntrip
                    if (isNTRIP_Connecting)
                    {
                        lblWatch.Text = "Authourizing";
                    }
                    else
                    {
                        if (isNTRIP_RequiredOn && NTRIP_Watchdog > 10)
                        {
                            lblWatch.Text = "Waiting";
                        }
                        else
                        {
                            lblWatch.Text = "Listening";

                            if (isNTRIP_RequiredOn)
                            {
                                lblWatch.Text += " NTRIP";
                            }
                            else if (isRadio_RequiredOn)
                            {
                                lblWatch.Text += " Radio";
                            }
                        }
                    }

                    if (sendGGAInterval > 0 && isNTRIP_Sending)
                    {
                        lblWatch.Text = "Send GGA";
                        isNTRIP_Sending = false;
                    }
                }
            }
            else if (isSerialPass_RequiredOn)
            {
                //pbarNtripMenu.Value = unchecked((byte)(tripBytes * 0.02));
                lblNTRIPBytes.Text = ((tripBytes >> 10)).ToString("###,###,### kb");

                //update byte counter and up counter
                if (ntripCounter > 59) btnStartStopNtrip.Text = (ntripCounter >> 6) + " Min";
                else if (ntripCounter < 60 && ntripCounter > 22) btnStartStopNtrip.Text = ntripCounter + " Secs";
                else btnStartStopNtrip.Text = "In " + (Math.Abs(ntripCounter - 22)) + " secs";
            }
        }

        public void ConfigureNTRIP()
        {
            lblWatch.Text = "Wait GPS";
            lblMessages.Text = "Reading...";
            lblNTRIP_IP.Text = "";
            lblMount.Text = "";

            aList.Clear();
            rList.Clear();
            lblMessages.Text = "Reading....";

            //start NTRIP if required
            isNTRIP_RequiredOn = Properties.Settings.Default.setNTRIP_isOn;
            isRadio_RequiredOn = Properties.Settings.Default.setRadio_isOn;
            isSerialPass_RequiredOn = Properties.Settings.Default.setPass_isOn;

            if (isRadio_RequiredOn || isSerialPass_RequiredOn)
            {
                // Immediatly connect radio
                ntripCounter = 20;
            }

            if (isNTRIP_RequiredOn || isRadio_RequiredOn || isSerialPass_RequiredOn)
            {
                btnStartStopNtrip.Visible = true;
                lblWatch.Visible = true;
                lblNTRIPBytes.Visible = true;
                lblToGPS.Visible = true;
                lblMount.Visible = true;
                lblNTRIP_IP.Visible = true;
            }
            else
            {
                btnStartStopNtrip.Visible = false;
                lblWatch.Visible = false;
                lblNTRIPBytes.Visible = false;
                lblToGPS.Visible = false;
                lblMount.Visible = false;
                lblNTRIP_IP.Visible = false;
            }

            btnStartStopNtrip.Text = "Off";
        }

        public void StartNTRIP()
        {
            if (isNTRIP_RequiredOn)
            {
                broadCasterPort = Properties.Settings.Default.setNTRIP_casterPort; //Select correct port (usually 80 or 2101)
                mount = Properties.Settings.Default.setNTRIP_mount; //Insert the correct mount
                username = Properties.Settings.Default.setNTRIP_userName; //Insert your username!
                password = Properties.Settings.Default.setNTRIP_userPassword; //Insert your password!
                toUDP_Port = Properties.Settings.Default.setNTRIP_sendToUDPPort; //send rtcm to which udp port
                sendGGAInterval = Properties.Settings.Default.setNTRIP_sendGGAInterval; //how often to send fixes

                try
                {
                    //NTRIP endpoint (broadcast RTCM → módulos)
                    epNtrip = new IPEndPoint(IPAddress.Parse(
                        Properties.Settings.Default.etIP_SubnetOne.ToString() + "." +
                        Properties.Settings.Default.etIP_SubnetTwo.ToString() + "." +
                        Properties.Settings.Default.etIP_SubnetThree.ToString() + ".255"), toUDP_Port);

                    EnsureNtripService();
                    ntripService.Connect(new NtripConfig
                    {
                        CasterIp = broadCasterIP,
                        CasterPort = broadCasterPort,
                        Mount = mount,
                        Username = username,
                        Password = password,
                        SendGgaIntervalSec = sendGGAInterval,
                        IsHttp10 = Properties.Settings.Default.setNTRIP_isHTTP10,
                        IsTcp = Properties.Settings.Default.setNTRIP_isTCP,
                        IsGgaManual = Properties.Settings.Default.setNTRIP_isGGAManual,
                        ManualLat = Properties.Settings.Default.setNTRIP_manualLat,
                        ManualLon = Properties.Settings.Default.setNTRIP_manualLon
                    },
                    () => new NtripGpsData
                    {
                        Latitude = latitude,
                        Longitude = longitude,
                        Altitude = altitudeData,
                        FixQuality = fixQualityData,
                        Satellites = satellitesData,
                        Hdop = hdopData,
                        Age = ageData
                    });

                    isNtripServiceStarted = true;

                    Log.EventWriter("NTRIP - IP: " + broadCasterIP.ToString() + ":" + broadCasterPort.ToString()
                        + " To Port: " + toUDP_Port.ToString() + " Mount: " + mount);
                }
                catch (Exception ex)
                {
                    ReconnectRequest();
                    Log.EventWriter("Catch - > NTRIP Reconnect Request: " + ex.ToString());

                    return;
                }

                isNTRIP_Connecting = true;
                lblNTRIP_IP.Text = broadCasterIP;
                lblMount.Text = mount;
            }
            else if (isRadio_RequiredOn)
            {
                if (!string.IsNullOrEmpty(Properties.Settings.Default.setPort_portNameRadio))
                {
                    // Disconnect when already connected
                    if (spRadio != null)
                    {
                        spRadio.Close();
                        spRadio.Dispose();
                    }

                    // Setup and open serial port
                    spRadio = new SerialPort(Properties.Settings.Default.setPort_portNameRadio);
                    spRadio.BaudRate = int.Parse(Properties.Settings.Default.setPort_baudRateRadio);
                    spRadio.DataReceived += NtripPort_DataReceived;
                    isNTRIP_Connecting = false;
                    isNTRIP_Connected = true;

                    try
                    {
                        spRadio.Open();
                    }
                    catch (Exception ex)
                    {
                        isNTRIP_Connecting = false;
                        isNTRIP_Connected = false;
                        isRadio_RequiredOn = false;
                        Log.EventWriter("Catch - > Error connecting to radio" + ex.ToString());

                        TimedMessageBox(2000, "Error connecting to radio", $"{ex.Message}");
                    }
                }
            }
            else if (isSerialPass_RequiredOn)
            {
                toUDP_Port = Properties.Settings.Default.setNTRIP_sendToUDPPort; //send rtcm to which udp port
                epNtrip = new IPEndPoint(IPAddress.Parse(
                    Properties.Settings.Default.etIP_SubnetOne.ToString() + "." +
                    Properties.Settings.Default.etIP_SubnetTwo.ToString() + "." +
                    Properties.Settings.Default.etIP_SubnetThree.ToString() + ".255"), toUDP_Port);

                if (!string.IsNullOrEmpty(Properties.Settings.Default.setPort_portNameRadio))
                {
                    // Disconnect when already connected
                    if (spRadio != null)
                    {
                        spRadio.Close();
                        spRadio.Dispose();
                    }

                    // Setup and open serial port
                    spRadio = new SerialPort(Properties.Settings.Default.setPort_portNameRadio);
                    spRadio.BaudRate = int.Parse(Properties.Settings.Default.setPort_baudRateRadio);
                    spRadio.DataReceived += NtripPort_DataReceived;
                    isNTRIP_Connecting = false;
                    isNTRIP_Connected = true;
                    lblWatch.Text = "RTCM Serial";


                    try
                    {
                        spRadio.Open();
                    }
                    catch (Exception ex)
                    {
                        isNTRIP_Connecting = false;
                        isNTRIP_Connected = false;
                        isSerialPass_RequiredOn = false;
                        Log.EventWriter("Catch - > Serial Pass Radio: " + ex.ToString());

                        TimedMessageBox(2000, "Error connecting to Serial Pass", $"{ex.Message}");
                    }
                }
            }
        }

        private void ReconnectRequest()
        {
            //TimedMessageBox(2000, "NTRIP Not Connected", " Reconnect Request");
            ntripCounter = 15;
            isNTRIP_Connected = false;
            isNTRIP_Starting = false;
            isNTRIP_Connecting = false;
        }

        private void IncrementNTRIPWatchDog()
        {
            //increment once every second
            ntripCounter++;

            //Thinks is connected but not receiving anything.
            //Caster TCP: la reconexión la maneja el servicio; acá solo radio/serial.
            if (NTRIP_Watchdog++ > 30 && isNTRIP_Connected && !isNTRIP_RequiredOn)
                ReconnectRequest();
        }

        public void OnAddMessage(byte[] data)
        {
            //update gui with stats
            tripBytes += (uint)data.Length;

            if (isViewAdvanced && isNTRIP_RequiredOn)
            {
                int mess = 0;
                //lblPacketSize.Text = data.Length.ToString();

                try
                {
                    lblStationID.Text = (((data[4] & 15) << 8) + (data[5])).ToString();

                    for (int i = 0; i < data.Length - 5; i++)
                    {

                        if (data[i] == 211 && (data[i + 1] >> 2) == 0)
                        {
                            mess = ((data[i + 3] << 4) + (data[i + 4] >> 4));
                            if (mess > 1000 && mess < 1231)
                            {
                                rList.Add(mess);
                                i += (data[i + 1] << 6) + (data[i + 2]) + 5;
                                if (data[i + 1] != 211)
                                {
                                    //rList.Clear();
                                    //break;
                                }
                            }
                            else
                            {
                                rList.Clear();
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    //MessageBox.Show("Error");
                }
            }

            //reset watchdog since we have updated data
            NTRIP_Watchdog = 0;

            if (isNTRIP_RequiredOn)
            {
                //move the ntrip stream to queue
                for (int i = 0; i < data.Length; i++)
                {
                    rawTrip.Enqueue(data[i]);
                }

                ntripMeterTimer.Enabled = true;
            }
            else
            {
                lblToGPS.Text = data.Length.ToString();
                //send it
                SendNTRIP(data);
            }


        }

        private void ntripMeterTimer_Tick(object sender, EventArgs e)
        {
            //we really should get here, but have to check
            if (rawTrip.Count == 0) return;

            //how many bytes in the Queue
            int cnt = rawTrip.Count;

            //how many sends have occured
            traffic.cntrGPSIn++;

            //128 bytes chunks max
            if (cnt > packetSizeNTRIP) cnt = packetSizeNTRIP;

            //new data array to send
            byte[] trip = new byte[cnt];

            traffic.cntrGPSInBytes += cnt;

            //dequeue into the array
            for (int i = 0; i < cnt; i++) trip[i] = rawTrip.Dequeue();

            //send it
            SendNTRIP(trip);

            //Are we done?
            if (rawTrip.Count == 0)
            {
                ntripMeterTimer.Enabled = false;

                if (focusSkipCounter != 0)
                {
                    lblToGPS.Text = traffic.cntrGPSInBytes == 0 ? "---" : (traffic.cntrGPSInBytes).ToString();
                    traffic.cntrGPSInBytes = 0;
                }
            }

            //Can't keep up as internet dumped a shit load so clear
            if (rawTrip.Count > 10000) rawTrip.Clear();

            ////show how many bytes left in the queue
            if (isViewAdvanced)
                lblCount.Text = rawTrip.Count.ToString();
        }

        public void SendNTRIP(byte[] data)
        {
            //serial send out GPS port
            if (isSendToSerial)
            {
                SendGPSPort(data);
            }

            //send out UDP Port
            if (isSendToUDP)
            {
                SendUDPMessage(data, epNtrip);
            }
        }

        private void NtripPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // Check if we got any data
            try
            {
                SerialPort comport = (SerialPort)sender;
                if (comport.BytesToRead < 32)
                    return;

                int nBytesRec = comport.BytesToRead;

                if (nBytesRec > 0)
                {
                    byte[] localMsg = new byte[nBytesRec];
                    comport.Read(localMsg, 0, nBytesRec);

                    BeginInvoke((MethodInvoker)(() => OnAddMessage(localMsg)));
                }
                else
                {
                    // If no data was recieved then the connection is probably dead
                    // TODO: What can we do?
                }
            }
            catch (Exception)
            {
                //MessageBox.Show( this, ex.Message, "Unusual error druing Recieve!" );
            }
        }

        private void ShutDownNTRIP()
        {
            if (isNtripServiceStarted)
            {
                ntripService.Disconnect();
                isNtripServiceStarted = false;

                ReconnectRequest();

                //Also stop the requests now
                isNTRIP_RequiredOn = false;
            }
            else if (spRadio != null)
            {
                spRadio.Close();
                spRadio.Dispose();
                spRadio = null;

                ReconnectRequest();

                //Also stop the requests now
                isRadio_RequiredOn = false;
            }
        }

        private void SettingsShutDownNTRIP()
        {
            if (isNtripServiceStarted)
            {
                ntripService.Disconnect();
                isNtripServiceStarted = false;
                ReconnectRequest();
            }

            if (spRadio != null && spRadio.IsOpen)
            {
                spRadio.Close();
                spRadio.Dispose();
                spRadio = null;
                ReconnectRequest();
            }
        }
    }
}
