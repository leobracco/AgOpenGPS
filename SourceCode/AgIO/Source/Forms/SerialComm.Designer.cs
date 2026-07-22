//Please, if you use this, share the improvements

using System;
using System.Text;
using System.Windows.Forms;
using AgLibrary.Logging;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgIO
{
    public partial class FormLoop
    {
        public static string portNameGPS = "***";
        public static int baudRateGPS = 4800;

        public static string portNameGPS2 = "***";
        public static int baudRateGPS2 = 4800;

        public static string portNameRtcm = "***";
        public static int baudRateRtcm = 4800;

        public static string portNameIMU = "***";
        public static int baudRateIMU = 38400;

        public static string portNameSteerModule = "***";
        public static int baudRateSteerModule = 38400;

        public static string portNameMachineModule = "***";
        public static int baudRateMachineModule = 38400;

        //used to decide to autoconnect section arduino this run
        public string recvGPSSentence = "GPS";
        public string recvGPS2Sentence = "GPS2";
        public string recvIMUSentence = "IMU";
        public string recvSteerModuleSentence = "Module 1";
        public string recvMachineModuleSentence = "Module 2";

        public bool isGPSCommOpen = false;

        public byte checksumSent = 0;
        public byte checksumRecd = 0;

        //used to decide to autoconnect autosteer arduino this run
        public bool wasGPSConnectedLastRun = false;
        public bool wasMachineModuleConnectedLastRun = false;
        public bool wasSteerModuleConnectedLastRun = false;
        public bool wasIMUConnectedLastRun = false;
        public bool wasRtcmConnectedLastRun = false;

        // Puertos serie ruteados por ISerialPortService (Windows hoy,
        // USB-OTG/UsbSerialForAndroid en Android — bloque 8 matriz Android,
        // 2026-07-22). Antes eran System.IO.Ports.SerialPort directo.
        public ISerialPortService spGPS = new WindowsSerialPortService();
        public ISerialPortService spGPS2 = new WindowsSerialPortService();
        public ISerialPortService spRtcm = new WindowsSerialPortService();
        public ISerialPortService spIMU = new WindowsSerialPortService();
        public ISerialPortService spSteerModule = new WindowsSerialPortService();
        public ISerialPortService spMachineModule = new WindowsSerialPortService();

        // Framing PGN byte a byte: máquina de estados portable (netstandard),
        // una instancia por puerto. Antes estaba triplicada acá.
        private readonly PgnFrameParser pgnParserSteer = new PgnFrameParser();
        private readonly PgnFrameParser pgnParserMachine = new PgnFrameParser();
        private readonly PgnFrameParser pgnParserIMU = new PgnFrameParser();

        // Buffer de líneas de GPS2 — ISerialPortService entrega bytes crudos
        // por evento (no hay ReadLine()); reconstruye la misma semántica que
        // SerialPort.ReadLine() (corta en '\n', conserva un eventual '\r'
        // previo tal cual llegó, ya que ese es el terminador NewLine default).
        private readonly StringBuilder gps2LineBuffer = new StringBuilder();

        private void InitPgnFrameParsers()
        {
            pgnParserSteer.OnFrame += frame =>
            {
                try { BeginInvoke((MethodInvoker)(() => ReceiveSteerModulePort(frame))); }
                catch { }
            };
            pgnParserMachine.OnFrame += frame =>
            {
                try { BeginInvoke((MethodInvoker)(() => ReceiveMachineModulePort(frame))); }
                catch { }
            };
            pgnParserIMU.OnFrame += frame =>
            {
                try { BeginInvoke((MethodInvoker)(() => ReceiveIMUPort(frame))); }
                catch { }
            };

            // Wiring único de los receptores — a diferencia del SerialPort
            // directo de antes, el wrapper ISerialPortService persiste entre
            // ciclos Open/Close (solo el puerto físico interno se recrea), así
            // que no hace falta suscribir/desuscribir en cada Open/Close.
            spIMU.OnDataReceived += bytes => ProcessSerialBytesForPgn(bytes, pgnParserIMU);
            spSteerModule.OnDataReceived += bytes => ProcessSerialBytesForPgn(bytes, pgnParserSteer);
            spMachineModule.OnDataReceived += bytes => ProcessSerialBytesForPgn(bytes, pgnParserMachine);
            spGPS.OnDataReceived += bytes =>
            {
                string sentence = Encoding.ASCII.GetString(bytes);
                try { BeginInvoke((MethodInvoker)(() => ReceiveGPSPort(sentence))); }
                catch { }
            };
            spGPS2.OnDataReceived += OnGps2DataReceived;
        }

        // Guarda de basura: si en un solo receive se acumularon > 100 bytes sin
        // haber cerrado un frame PGN, se descarta ese lote y se resetea el
        // parser — mismo umbral/criterio que el BytesToRead > 100 original.
        private static void ProcessSerialBytesForPgn(byte[] bytes, PgnFrameParser parser)
        {
            if (bytes.Length > 100)
            {
                parser.Reset();
                return;
            }
            for (int i = 0; i < bytes.Length; i++)
                parser.ProcessByte(bytes[i]);
        }

        private void OnGps2DataReceived(byte[] bytes)
        {
            gps2LineBuffer.Append(Encoding.ASCII.GetString(bytes));

            int nl;
            while ((nl = gps2LineBuffer.ToString().IndexOf('\n')) >= 0)
            {
                string line = gps2LineBuffer.ToString(0, nl);
                gps2LineBuffer.Remove(0, nl + 1);
                try { BeginInvoke((MethodInvoker)(() => ReceiveGPS2Port(line))); }
                catch { }
            }
        }

        #region IMUSerialPort //--------------------------------------------------------------------
        private void ReceiveIMUPort(byte[] Data)
        {
            SendToLoopBackMessageAOG(Data);
            traffic.helloFromIMU = 0;
        }

        //Send machine info out to machine board
        public void SendIMUPort(byte[] items, int numItems)
        {
            //Tell Arduino to turn section on or off accordingly
            if (spIMU.IsOpen)
            {
                try
                {
                    spIMU.Write(items, 0, numItems);
                }
                catch (Exception)
                {
                    CloseIMUPort();
                }
            }
        }

        //open the Arduino serial port
        public void OpenIMUPort()
        {
            if (!spIMU.IsOpen)
            {
                spIMU.DtrEnable = true;
                spIMU.RtsEnable = true;
            }

            try { spIMU.Open(portNameIMU, baudRateIMU); }
            catch (Exception ex)
            {
                Log.EventWriter("No Arduino Port, IMU Port Exc: " + ex.ToString());

                Log.EventWriter("[Aviso] Puerto serie no disponible: " + ex.Message);


                Properties.Settings.Default.setPort_wasIMUConnected = false;
                Properties.Settings.Default.Save();
                wasIMUConnectedLastRun = false;
            }

            if (spIMU.IsOpen)
            {
                //short delay for the use of mega2560, it is working in debugmode with breakpoint
                System.Threading.Thread.Sleep(500); // 500 was not enough

                spIMU.DiscardOutBuffer();
                spIMU.DiscardInBuffer();

                Properties.Settings.Default.setPort_portNameIMU = portNameIMU;
                Properties.Settings.Default.setPort_wasIMUConnected = true;
                Properties.Settings.Default.Save();
                wasIMUConnectedLastRun = true;
                lblIMUComm.Text = portNameIMU;
            }
        }

        //close the machine port
        public void CloseIMUPort()
        {
            if (spIMU.IsOpen)
            {
                try
                {
                    spIMU.Close();
                    byte[] imuClose = new byte[] { 0x80, 0x81, 0x7C, 0xD4, 2, 1, 0, 0xCC };

                    //tell AOG IMU is disconnected
                    SendToLoopBackMessageAOG(imuClose);
                }

                catch (Exception e)
                {
                    Log.EventWriter("Closing Machine Serial Port" + e.ToString());
                    Log.EventWriter("[Aviso] Cierre de puerto serie: " + e.Message);
                }

                Properties.Settings.Default.setPort_wasIMUConnected = false;
                Properties.Settings.Default.Save();

                wasIMUConnectedLastRun = false;
            }

            else
            {
                byte[] imuClose = new byte[] { 0x80, 0x81, 0x7C, 0xD4, 2, 1, 0, 0xCC };

                //tell AOG IMU is disconnected
                SendToLoopBackMessageAOG(imuClose);
                wasIMUConnectedLastRun = false;
            }

            wasIMUConnectedLastRun = false;
            lblIMUComm.Text = "---";
        }
        #endregion ----------------------------------------------------------------

        #region SteerModuleSerialPort //--------------------------------------------------------------------
        private void ReceiveSteerModulePort(byte[] Data)
        {
            SendToLoopBackMessageAOG(Data);
            traffic.helloFromAutoSteer = 0;
        }

        //Send machine info out to machine board
        public void SendSteerModulePort(byte[] items, int numItems)
        {
            //Tell Arduino to turn section on or off accordingly
            if (spSteerModule.IsOpen)
            {
                try
                {
                    spSteerModule.Write(items, 0, numItems);
                }
                catch (Exception ex)
                {
                    Log.EventWriter("Catch - > Serial Steer module disconnect: " + ex.ToString());
                    CloseSteerModulePort();
                }
            }
        }

        //open the Arduino serial port
        public void OpenSteerModulePort()
        {
            if (!spSteerModule.IsOpen)
            {
                spSteerModule.DtrEnable = true;
                spSteerModule.RtsEnable = true;
            }

            try
            {
                spSteerModule.Open(portNameSteerModule, baudRateSteerModule);
                //short delay for the use of mega2560, it is working in debugmode with breakpoint
                System.Threading.Thread.Sleep(1000); // 500 was not enough

            }
            catch (Exception e)
            {
                Log.EventWriter("Opening Machine Port" + e.ToString());

                Log.EventWriter("[Aviso] Puerto serie no disponible: " + e.Message);


                Properties.Settings.Default.setPort_wasSteerModuleConnected = false;
                Properties.Settings.Default.Save();
            }

            if (spSteerModule.IsOpen)
            {
                spSteerModule.DiscardOutBuffer();
                spSteerModule.DiscardInBuffer();

                Properties.Settings.Default.setPort_portNameSteer = portNameSteerModule;
                Properties.Settings.Default.setPort_wasSteerModuleConnected = true;
                Properties.Settings.Default.Save();

                wasSteerModuleConnectedLastRun = true;
                lblMod1Comm.Text = portNameSteerModule;
            }
        }

        //close the machine port
        public void CloseSteerModulePort()
        {
            if (spSteerModule.IsOpen)
            {
                try { spSteerModule.Close(); }
                catch (Exception e)
                {
                    Log.EventWriter("Closing Machine Serial Port" + e.ToString());
                    Log.EventWriter("[Aviso] Cierre de puerto serie: " + e.Message);
                }

                Properties.Settings.Default.setPort_wasSteerModuleConnected = false;
                Properties.Settings.Default.Save();
            }

            wasSteerModuleConnectedLastRun = false;
            lblMod1Comm.Text = "---";
        }
        #endregion ----------------------------------------------------------------

        #region MachineModuleSerialPort // Machine Port ------------------------------------------------

        private void ReceiveMachineModulePort(byte[] Data)
        {
            try
            {
                SendToLoopBackMessageAOG(Data);
                traffic.helloFromMachine = 0;
            }
            catch (Exception e)
            {
                Log.EventWriter("Machine Module Send Exc: " + e.ToString());
            }
        }

        //Send machine info out to machine board
        public void SendMachineModulePort(byte[] items, int numItems)
        {
            if (spMachineModule.IsOpen)
            {
                try
                {
                    spMachineModule.Write(items, 0, numItems);
                }
                catch (Exception ex)
                {
                    Log.EventWriter("Catch - > Serial Machine module disconnect: " + ex.ToString());
                    CloseMachineModulePort();
                }
            }
        }

        //open the Arduino serial port
        public void OpenMachineModulePort()
        {
            if (!spMachineModule.IsOpen)
            {
                spMachineModule.DtrEnable = true;
                spMachineModule.RtsEnable = true;
            }

            try
            {
                spMachineModule.Open(portNameMachineModule, baudRateMachineModule);
                //short delay for the use of mega2560, it is working in debugmode with breakpoint
                System.Threading.Thread.Sleep(1000); // 500 was not enough

            }
            catch (Exception e)
            {
                Log.EventWriter("Opening Machine Port: " + e.ToString());

                Log.EventWriter("[Aviso] Puerto serie no disponible: " + e.Message);


                Properties.Settings.Default.setPort_wasMachineModuleConnected = false;
                Properties.Settings.Default.Save();
            }

            if (spMachineModule.IsOpen)
            {
                spMachineModule.DiscardOutBuffer();
                spMachineModule.DiscardInBuffer();

                Properties.Settings.Default.setPort_portNameMachine = portNameMachineModule;
                Properties.Settings.Default.setPort_wasMachineModuleConnected = true;
                Properties.Settings.Default.Save();

                wasMachineModuleConnectedLastRun = true;
                lblMod2Comm.Text = portNameMachineModule;
            }
        }

        //close the machine port
        public void CloseMachineModulePort()
        {
            if (spMachineModule.IsOpen)
            {
                try { spMachineModule.Close(); }
                catch (Exception e)
                {
                    Log.EventWriter("Closing Machine Serial Port: " + e.ToString());
                    Log.EventWriter("[Aviso] Cierre de puerto serie: " + e.Message);
                }

                Properties.Settings.Default.setPort_wasMachineModuleConnected = false;
                Properties.Settings.Default.Save();
            }

            wasMachineModuleConnectedLastRun = false;
            lblMod2Comm.Text = "---";
        }
        #endregion --------------------------------------------------------------------

        #region GPS SerialPort --------------------------------------------------------------------------

        public void SendGPSPort(byte[] data)
        {
            try
            {
                if (spRtcm.IsOpen)
                {
                    spRtcm.Write(data, 0, data.Length);
                }

                else if (spGPS.IsOpen)
                {
                    spGPS.Write(data, 0, data.Length);
                }
            }
            catch (Exception e)
            {
                Log.EventWriter("Opening RTCM Port: " + e.ToString());
            }
        }

        public void OpenGPSPort()
        {

            if (spGPS.IsOpen)
            {
                //close it first
                CloseGPSPort();
            }

            spGPS.WriteTimeout = 1000;

            try { spGPS.Open(portNameGPS, baudRateGPS); }
            catch (Exception ex)
            {
                Log.EventWriter("Catch - > Serial GPS Open Fail: " + ex.ToString());
            }

            if (spGPS.IsOpen)
            {
                //discard any stuff in the buffers
                spGPS.DiscardOutBuffer();
                spGPS.DiscardInBuffer();

                Properties.Settings.Default.setPort_portNameGPS = portNameGPS;
                Properties.Settings.Default.setPort_baudRateGPS = baudRateGPS;
                Properties.Settings.Default.setPort_wasGPSConnected = true;
                Properties.Settings.Default.Save();
                lblGPS1Comm.Text = portNameGPS;
                wasGPSConnectedLastRun = true;
            }
        }
        public void CloseGPSPort()
        {
            try { spGPS.Close(); }
            catch (Exception e)
            {
                Log.EventWriter("Closing GPS Port" + e.ToString());
                Log.EventWriter("[Aviso] Cierre de puerto serie: " + e.Message);
            }

            lblGPS1Comm.Text = "---";
            wasGPSConnectedLastRun = false;
        }

        //called by the GPS delegate every time a chunk is rec'd
        private void ReceiveGPSPort(string sentence)
        {
            nmea.ParseIncoming(sentence);

            traffic.cntrGPSOut += sentence.Length;
            if (isGPSCommOpen) recvGPSSentence = sentence;
        }
        #endregion SerialPortGPS

        #region GPS2 SerialPort //--------------------------------------------------------------------------

        //called by the GPS2 delegate every time a chunk is rec'd
        private void ReceiveGPS2Port(string sentence)
        {
            recvGPS2Sentence = sentence;
        }
        public void SendGPS2Port(byte[] data)
        {
            try
            {
                if (spGPS2.IsOpen)
                {
                    spGPS2.Write(data, 0, data.Length);
                }
            }
            catch (Exception)
            {
            }

        }
        public void OpenGPS2Port()
        {
            //close it first
            CloseGPS2Port();

            spGPS2.WriteTimeout = 1000;

            try { spGPS2.Open(portNameGPS2, baudRateGPS2); }
            catch (Exception ex)
            {
                Log.EventWriter("Catch - > Serial GPS Open Fail: " + ex.ToString());
            }

            if (spGPS2.IsOpen)
            {
                //discard any stuff in the buffers
                spGPS2.DiscardOutBuffer();
                spGPS2.DiscardInBuffer();
                gps2LineBuffer.Clear();

                Properties.Settings.Default.setPort_portNameGPS2 = portNameGPS2;
                Properties.Settings.Default.setPort_baudRateGPS2 = baudRateGPS2;
                Properties.Settings.Default.Save();
            }
        }
        public void CloseGPS2Port()
        {
            try { spGPS2.Close(); }
            catch (Exception e)
            {
                Log.EventWriter("Closing GPS2 Port" + e.ToString());
                Log.EventWriter("[Aviso] Cierre de puerto serie: " + e.Message);
            }
        }
        #endregion //--------------------------------------------------------

        public void OpenRtcmPort()
        {
            if (spRtcm.IsOpen)
            {
                //close it first
                CloseRtcmPort();
            }

            spRtcm.WriteTimeout = 1000;

            try { spRtcm.Open(portNameRtcm, baudRateRtcm); }
            catch (Exception ex)
            {
                Log.EventWriter("Catch - > Serial RTCM Open Fail: " + ex.ToString());
            }

            if (spRtcm.IsOpen)
            {
                //discard any stuff in the buffers
                spRtcm.DiscardOutBuffer();
                spRtcm.DiscardInBuffer();

                Properties.Settings.Default.setPort_portNameRtcm = portNameRtcm;
                Properties.Settings.Default.setPort_baudRateRtcm = baudRateRtcm;
                Properties.Settings.Default.setPort_wasRtcmConnected = true;
                Properties.Settings.Default.Save();
                wasRtcmConnectedLastRun = true;
            }
        }

        public void CloseRtcmPort()
        {
            try { spRtcm.Close(); }
            catch (Exception e)
            {
                Log.EventWriter("Closing RTCM Port" + e.ToString());
                Log.EventWriter("[Aviso] Cierre de puerto serie: " + e.Message);
            }

            wasRtcmConnectedLastRun = false;
        }
    }//end class
}//end namespace
