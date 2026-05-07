// ============================================================================
// FormSistemaRed.cs - Tab Red del módulo Sistema
// Selecciona interfaz (LAN/WiFi) y modo (DHCP/estática). Aplica con netsh.
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.Sistema
{
    public class FormSistemaRed : Form
    {
        private ComboBox _cboIface;
        private RadioButton _rbDhcp, _rbStatic;
        private TextBox _txtIp, _txtMask, _txtGw, _txtDns1, _txtDns2;
        private Label _lblCurrent;
        private Label _lblStatus;

        public FormSistemaRed()
        {
            Text = "Red";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.BgBlack;
            ForeColor = Theme.TextPrimary;
            Theme.ApplyToForm(this);
            BuildUI();
            LoadInterfaces();
        }

        private void BuildUI()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 12,
                BackColor = Theme.BgBlack,
                Padding = new Padding(20)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 12; i++)
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            // Interfaz
            root.Controls.Add(MkLabel("Interfaz:"), 0, 0);
            _cboIface = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Theme.BgInput,
                ForeColor = Theme.TextPrimary,
                Font = new Font(Theme.FontFamily, 10f),
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Fill
            };
            _cboIface.SelectedIndexChanged += (s, e) => RefreshCurrent();
            root.Controls.Add(_cboIface, 1, 0);

            // Estado actual
            _lblCurrent = MkLabel("");
            _lblCurrent.ForeColor = Theme.TextSecondary;
            _lblCurrent.Dock = DockStyle.Fill;
            root.SetColumnSpan(_lblCurrent, 2);
            root.Controls.Add(_lblCurrent, 0, 1);

            // Modo
            root.Controls.Add(MkLabel("Modo:"), 0, 2);
            var pnlMode = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            _rbDhcp = new RadioButton
            {
                Text = "DHCP (automática)",
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent,
                Font = new Font(Theme.FontFamily, 10f),
                Location = new Point(0, 6),
                AutoSize = true,
                Checked = true
            };
            _rbStatic = new RadioButton
            {
                Text = "IP estática",
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent,
                Font = new Font(Theme.FontFamily, 10f),
                Location = new Point(180, 6),
                AutoSize = true
            };
            _rbDhcp.CheckedChanged += (s, e) => UpdateMode();
            _rbStatic.CheckedChanged += (s, e) => UpdateMode();
            pnlMode.Controls.Add(_rbDhcp);
            pnlMode.Controls.Add(_rbStatic);
            root.Controls.Add(pnlMode, 1, 2);

            // IP
            root.Controls.Add(MkLabel("IP:"), 0, 3);
            _txtIp = MkInput(); _txtIp.Dock = DockStyle.Fill;
            root.Controls.Add(_txtIp, 1, 3);

            // Máscara
            root.Controls.Add(MkLabel("Máscara:"), 0, 4);
            _txtMask = MkInput(); _txtMask.Dock = DockStyle.Fill; _txtMask.Text = "255.255.255.0";
            root.Controls.Add(_txtMask, 1, 4);

            // Gateway
            root.Controls.Add(MkLabel("Gateway:"), 0, 5);
            _txtGw = MkInput(); _txtGw.Dock = DockStyle.Fill;
            root.Controls.Add(_txtGw, 1, 5);

            // DNS1
            root.Controls.Add(MkLabel("DNS 1:"), 0, 6);
            _txtDns1 = MkInput(); _txtDns1.Dock = DockStyle.Fill; _txtDns1.Text = "8.8.8.8";
            root.Controls.Add(_txtDns1, 1, 6);

            // DNS2
            root.Controls.Add(MkLabel("DNS 2:"), 0, 7);
            _txtDns2 = MkInput(); _txtDns2.Dock = DockStyle.Fill; _txtDns2.Text = "1.1.1.1";
            root.Controls.Add(_txtDns2, 1, 7);

            // Botones
            var pnlBtns = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            var btnApply = MkBtn("Aplicar", 140); btnApply.Location = new Point(0, 0);
            btnApply.Click += (s, e) => Apply();
            var btnRefresh = MkBtn("Refrescar", 140); btnRefresh.Location = new Point(150, 0);
            btnRefresh.Click += (s, e) => RefreshCurrent();
            pnlBtns.Controls.Add(btnApply);
            pnlBtns.Controls.Add(btnRefresh);
            root.SetColumnSpan(pnlBtns, 2);
            root.Controls.Add(pnlBtns, 0, 9);

            // Status
            _lblStatus = MkLabel("");
            _lblStatus.ForeColor = Theme.Accent;
            _lblStatus.Dock = DockStyle.Fill;
            root.SetColumnSpan(_lblStatus, 2);
            root.Controls.Add(_lblStatus, 0, 10);

            Controls.Add(root);
            UpdateMode();
        }

        private Label MkLabel(string t)
        {
            return new Label
            {
                Text = t,
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                Font = new Font(Theme.FontFamily, 10f),
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            };
        }

        private TextBox MkInput()
        {
            return new TextBox
            {
                Font = new Font(Theme.FontFamily, 10f),
                BackColor = Theme.BgInput,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private Button MkBtn(string text, int w)
        {
            var b = new Button
            {
                Text = text,
                Width = w,
                Height = 36,
                Font = new Font(Theme.FontFamily, 9.5f, FontStyle.Bold),
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private void UpdateMode()
        {
            bool en = _rbStatic.Checked;
            _txtIp.Enabled = en;
            _txtMask.Enabled = en;
            _txtGw.Enabled = en;
            _txtDns1.Enabled = en;
            _txtDns2.Enabled = en;
        }

        private void LoadInterfaces()
        {
            _cboIface.Items.Clear();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    _cboIface.Items.Add(ni.Name);
                }
            }
            catch { }
            if (_cboIface.Items.Count > 0) _cboIface.SelectedIndex = 0;
        }

        private static string Netsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding("IBM850"),
                    Verb = "runas"
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    return o ?? "";
                }
            }
            catch (Exception ex) { return "ERROR: " + ex.Message; }
        }

        private void RefreshCurrent()
        {
            if (_cboIface.SelectedItem == null) { _lblCurrent.Text = ""; return; }
            string name = _cboIface.SelectedItem.ToString();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != name) continue;
                    var ipProps = ni.GetIPProperties();
                    string ip = "-", mask = "-", gw = "-";
                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            ip = ua.Address.ToString();
                            mask = ua.IPv4Mask != null ? ua.IPv4Mask.ToString() : "-";
                            break;
                        }
                    }
                    foreach (var g in ipProps.GatewayAddresses)
                    {
                        if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            gw = g.Address.ToString(); break;
                        }
                    }
                    _lblCurrent.Text = "Actual:  IP=" + ip + "   Máscara=" + mask + "   GW=" + gw + "   [" + ni.OperationalStatus + "]";
                    return;
                }
            }
            catch { }
            _lblCurrent.Text = "";
        }

        private void Apply()
        {
            if (_cboIface.SelectedItem == null) { _lblStatus.Text = "Falta interfaz."; return; }
            string name = _cboIface.SelectedItem.ToString();

            string r;
            if (_rbDhcp.Checked)
            {
                r = Netsh("interface ip set address name=\"" + name + "\" source=dhcp");
                Netsh("interface ip set dns name=\"" + name + "\" source=dhcp");
                _lblStatus.Text = "DHCP aplicado: " + r;
            }
            else
            {
                if (string.IsNullOrEmpty(_txtIp.Text) || string.IsNullOrEmpty(_txtMask.Text))
                {
                    _lblStatus.Text = "Faltan IP/máscara.";
                    return;
                }
                string gwArg = string.IsNullOrEmpty(_txtGw.Text) ? "" : (" " + _txtGw.Text + " 1");
                r = Netsh("interface ip set address name=\"" + name + "\" static " + _txtIp.Text + " " + _txtMask.Text + gwArg);
                if (!string.IsNullOrEmpty(_txtDns1.Text))
                    Netsh("interface ip set dns name=\"" + name + "\" static " + _txtDns1.Text);
                if (!string.IsNullOrEmpty(_txtDns2.Text))
                    Netsh("interface ip add dns name=\"" + name + "\" " + _txtDns2.Text + " index=2");
                _lblStatus.Text = "IP estática aplicada.";
            }

            // refrescar
            var t = new Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Stop(); RefreshCurrent(); };
            t.Start();
        }
    }
}
