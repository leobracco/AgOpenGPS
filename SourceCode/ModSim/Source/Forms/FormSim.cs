using ModSim.Properties;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;

namespace ModSim
{
    public partial class FormSim : Form
    {
        public FormSim()
        {
            InitializeComponent();
        }

        //First run
        private void FormSim_Load(object sender, EventArgs e)
        {
            cboxGGA.Checked = Settings.Default.isGGA;
            cboxVTG.Checked = Settings.Default.isVTG;
            cboxAVR.Checked = Settings.Default.isAVR;
            cboxHDT.Checked = Settings.Default.isHDT;
            cboxRMC.Checked = Settings.Default.isRMC;
            cboxOGI.Checked = Settings.Default.isOGI;
            cboxNDA.Checked = Settings.Default.isNDA;
            cboxKSXT.Checked = Settings.Default.isKSXT;

            latitude = Settings.Default.setGPS_SimLatitude;
            nudLat.Value = (decimal)Settings.Default.setGPS_SimLatitude;
            longitude = Settings.Default.setGPS_SimLongitude;
            nudLon.Value = (decimal)Settings.Default.setGPS_SimLongitude;

            lblIPSet1.Text = Properties.Settings.Default.etIP_SubnetOne.ToString();
            lblIPSet2.Text = Properties.Settings.Default.etIP_SubnetTwo.ToString();
            lblIPSet3.Text = Properties.Settings.Default.etIP_SubnetThree.ToString();

            // 4to octeto del destino (255 = broadcast a toda la subred, como
            // siempre; un numero concreto = unicast a ESA pantalla sola). Se
            // agrega por codigo al lado de los 3 octetos para no tocar el
            // Designer. Cambiarlo reconstruye el endpoint al instante: no hace
            // falta reiniciar ModSim para apuntarle a otra maquina.
            var nudHost4 = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 255,
                Value = Properties.Settings.Default.etIP_HostFour,
                Location = new System.Drawing.Point(lblIPSet3.Right + 6, lblIPSet3.Top - 3),
                Width = 58,
                Font = lblIPSet3.Font
            };
            var lblHost4 = new Label
            {
                Text = "(255 = todas)",
                AutoSize = true,
                BackColor = System.Drawing.Color.Transparent,
                Location = new System.Drawing.Point(nudHost4.Right + 70, lblIPSet3.Top),
                Font = lblIPSet3.Font
            };
            nudHost4.ValueChanged += (s2, e2) =>
            {
                Properties.Settings.Default.etIP_HostFour = (byte)nudHost4.Value;
                Properties.Settings.Default.Save();
                epAgIO = new IPEndPoint(IPAddress.Parse(
                    Properties.Settings.Default.etIP_SubnetOne.ToString() + "." +
                    Properties.Settings.Default.etIP_SubnetTwo.ToString() + "." +
                    Properties.Settings.Default.etIP_SubnetThree.ToString() + "." +
                    Properties.Settings.Default.etIP_HostFour.ToString()), 9999);
            };
            lblIPSet3.Parent.Controls.Add(nudHost4);
            lblIPSet3.Parent.Controls.Add(lblHost4);

            lblScanReply.Text = "No";

            LoadUDPNetwork();
        }

        private void FormSim_FormClosing(object sender, FormClosingEventArgs e)
        {
            //save settings before exit
            Settings.Default.isGGA = cboxGGA.Checked;
            Settings.Default.isVTG = cboxVTG.Checked;
            Settings.Default.isAVR = cboxAVR.Checked;
            Settings.Default.isHDT = cboxHDT.Checked;
            Settings.Default.isRMC = cboxRMC.Checked;
            Settings.Default.isOGI = cboxOGI.Checked;
            Settings.Default.isNDA = cboxNDA.Checked;
            Settings.Default.isKSXT = cboxKSXT.Checked;

            Settings.Default.Save();

            if (UDPSocket != null)
            {
                try
                {
                    UDPSocket.Shutdown(SocketShutdown.Both);
                }
                finally { UDPSocket.Close(); }
            }
        }

        private void lblIP_Click(object sender, EventArgs e)
        {
            lblIP.Text = "";
            foreach (IPAddress IPA in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (IPA.AddressFamily == AddressFamily.InterNetwork)
                {
                    _ = IPA.ToString();
                    lblIP.Text += IPA.ToString() + "\r\n";
                }
            }
        }
    }
}

