// ============================================================================
// UDP.designer.cs — Wrapper WinForms sobre UdpBridgeService (portable).
// Los sockets viven en AgroParallel.Services.UdpBridgeService (netstandard2.0);
// acá queda solo la lógica de UI (labels, monitor) y el ruteo PGN↔serial que
// depende del form. Los datos recibidos llegan por events del servicio y se
// re-despachan al hilo UI con BeginInvoke (igual que antes).
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using AgLibrary.Logging;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgIO
{
    public class CTraffic
    {
        public int cntrGPSIn = 0;
        public int cntrGPSInBytes = 0;
        public int cntrGPSOut = 0;

        public uint helloFromMachine = 99, helloFromAutoSteer = 99, helloFromIMU = 99;
    }

    public class CScanReply
    {
        public string steerIP =   "";
        public string machineIP = "";
        public string GPS_IP =    "";
        public string IMU_IP =    "";
        public string subnetStr = "";

        public byte[] subnet = { 0, 0, 0 };

        public bool isNewSteer, isNewMachine, isNewGPS, isNewIMU;

        public bool isNewData = false;
    }

    public partial class FormLoop
    {
        // Bridge UDP portable (loopback :17777↔:15555 + LAN :9999↔:8888).
        public readonly IUdpBridgeService udpBridge = new UdpBridgeService();

        public bool isUDPNetworkConnected;

        public IPEndPoint epModule = new IPEndPoint(IPAddress.Parse(
                Properties.Settings.Default.etIP_SubnetOne.ToString() + "." +
                Properties.Settings.Default.etIP_SubnetTwo.ToString() + "." +
                Properties.Settings.Default.etIP_SubnetThree.ToString() + ".255"), 8888);
        private IPEndPoint epNtrip;

        public IPEndPoint epModuleSet = new IPEndPoint(IPAddress.Parse("255.255.255.255"), 8888);
        public byte[] ipAutoSet = { 192, 168, 5 };

        //class for counting bytes
        public CTraffic traffic = new CTraffic();
        public CScanReply scanReply = new CScanReply();

        //scan results placed here
        public string scanReturn = "Scanning...";

        //used to send communication check pgn= C8 or 200
        private byte[] helloFromAgIO = { 0x80, 0x81, 0x7F, 200, 3, 56, 0, 0, 0x47 };

        public IPAddress ipCurrent;

        //initialize udp network
        public void LoadUDPNetwork()
        {
            helloFromAgIO[5] = 56;

            lblIP.Text = "";
            try //udp network
            {
                foreach (IPAddress IPA in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (IPA.AddressFamily == AddressFamily.InterNetwork)
                    {
                        lblIP.Text += IPA.ToString().Trim() + "\r\n";
                    }
                }

                udpBridge.OnUdpReceived += (data, ep) =>
                {
                    try { BeginInvoke((MethodInvoker)(() => ReceiveFromUDP(data, ep))); }
                    catch { }
                };
                udpBridge.StartUdp(9999);

                isUDPNetworkConnected = udpBridge.IsUdpConnected;

                if (isUDPNetworkConnected)
                {
                    Log.EventWriter("UDP Network is connected: " + epModule.ToString());
                    btnUDP.BackColor = Color.LimeGreen;
                }
                else
                {
                    Log.EventWriter("UDP Network Failed to Connect");
                    btnUDP.BackColor = Color.Red;
                    lblIP.Text = "Error";
                }
            }
            catch (Exception e)
            {
                Log.EventWriter("Catch -> Load UDP Server" + e);
                // Modo demonio: sin diálogos; ya quedó en el log de eventos.
                btnUDP.BackColor = Color.Red;
                lblIP.Text = "Error";
            }
        }

        private void LoadLoopback()
        {
            try //loopback
            {
                string loopIp =
                    Properties.Settings.Default.eth_loopOne.ToString() + "." +
                    Properties.Settings.Default.eth_loopTwo.ToString() + "." +
                    Properties.Settings.Default.eth_loopThree.ToString() + "." +
                    Properties.Settings.Default.eth_loopFour.ToString();

                udpBridge.OnLoopbackReceived += (data, ep) =>
                {
                    try { BeginInvoke((MethodInvoker)(() => ReceiveFromLoopBack(data))); }
                    catch { }
                };
                udpBridge.StartLoopback(loopIp, 17777, 15555);

                if (udpBridge.IsLoopbackConnected)
                    Log.EventWriter("Loopback is Connected: " + IPAddress.Loopback.ToString() + ":17777");
                else
                    Log.EventWriter("[Aviso] Loopback Server load error");
            }
            catch (Exception ex)
            {
                Log.EventWriter("Catch - > Load UDP Loopback Failed: " + ex.ToString());
                Log.EventWriter("[Aviso] Loopback Server load error: " + ex.Message);
            }
        }

        #region Send LoopBack

        private void SendToLoopBackMessageAOG(byte[] byteData)
        {
            udpBridge.SendToLoopback(byteData);
        }

        #endregion

        #region Receive LoopBack

        private void ReceiveFromLoopBack(byte[] data)
        {
            //Send out to udp network
            SendUDPMessage(data, epModule);

            if (data[0] == 0x80 && data[1] == 0x81)
            {
                //ruteo PGN→puertos serie: lógica pura en CPgnRouter (portabilidad)
                CPgnRouter.RouteLoopbackPgn(data[3], out bool toSteer, out bool toMachine);
                if (toSteer) SendSteerModulePort(data, data.Length);
                if (toMachine) SendMachineModulePort(data, data.Length);
            }
        }

        #endregion

        #region Send UDP

        public void SendUDPMessage(byte[] byteData, IPEndPoint endPoint)
        {
            if (isUDPNetworkConnected)
            {
                if (isUDPMonitorOn)
                {
                    if (epNtrip != null && endPoint.Port == epNtrip.Port)
                    {
                        if (isNTRIPLogOn)
                            logUDPSentence.Append(DateTime.Now.ToString("ss.fff\t") + endPoint.ToString() + "\t" + " > NTRIP\r\n");
                    }
                    else
                    {
                        logUDPSentence.Append(DateTime.Now.ToString("ss.fff\t") + endPoint.ToString() + "\t" + " > " + byteData[3].ToString() + "\r\n");
                    }
                }

                udpBridge.SendUdpTo(byteData, endPoint);
            }
        }

        #endregion

        #region Receive UDP

        private void ReceiveFromUDP(byte[] data, IPEndPoint remoteEp)
        {
            try
            {
                if (data[0] == 0x80 && data[1] == 0x81)
                {
                    //module return via udp sent to AOG
                    SendToLoopBackMessageAOG(data);

                    //check for Scan and Hello
                    if (data[3] == 126 && data.Length == 11)
                    {

                        traffic.helloFromAutoSteer = 0;
                        if (isViewAdvanced)
                        {
                            lblPing.Text = (((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds - pingSecondsStart) * 1000).ToString("N0");
                            double actualSteerAngle = (Int16)((data[6] << 8) + data[5]);
                            lblSteerAngle.Text = (actualSteerAngle * 0.01).ToString("N1");
                            lblWASCounts.Text = ((Int16)((data[8] << 8) + data[7])).ToString();

                            lblSwitchStatus.Text = ((data[9] & 2) == 2).ToString();
                            lblWorkSwitchStatus.Text = ((data[9] & 1) == 1).ToString();
                        }
                    }

                    else if (data[3] == 123 && data.Length == 11)
                    {

                        traffic.helloFromMachine = 0;

                        if (isViewAdvanced)
                        {
                            lblPingMachine.Text = (((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds - pingSecondsStart) * 1000).ToString("N0");
                            lbl1To8.Text = Convert.ToString(data[5], 2).PadLeft(8, '0');
                            lbl9To16.Text = Convert.ToString(data[6], 2).PadLeft(8, '0');
                        }
                    }

                    else if (data[3] == 121 && data.Length == 11)
                        traffic.helloFromIMU = 0;

                    //scan Reply: parsing puro en CPgnRouter (portabilidad)
                    else if (data[3] == 203 && data.Length == 13)
                    {
                        CPgnRouter.ParseScanReply(data, scanReply);
                    }

                    if (isUDPMonitorOn)
                    {
                        logUDPSentence.Append(DateTime.Now.ToString("ss.fff\t") + (remoteEp?.ToString() ?? "") + "\t" + " < " + data[3].ToString() + "\r\n");
                    }

                } // end of pgns

                else if (data[0] == 36 && (data[1] == 71 || data[1] == 80 || data[1] == 75))
                {
                    traffic.cntrGPSOut += data.Length;
                    nmea.ParseIncoming(Encoding.ASCII.GetString(data));

                    if (isUDPMonitorOn && isGPSLogOn)
                    {
                        logUDPSentence.Append(DateTime.Now.ToString("ss.fff\t") + System.Text.Encoding.ASCII.GetString(data));
                    }
                }
            }
            catch
            {
            }
        }

        #endregion
    }
}
