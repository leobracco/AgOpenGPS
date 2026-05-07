// ============================================================================
// FormSistemaWifi.cs - Tab WiFi del módulo Sistema
// Escanear redes, conectar con SSID + password (netsh wlan).
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.Sistema
{
    public class FormSistemaWifi : Form
    {
        private ListView _lstRedes;
        private TextBox _txtSsid;
        private TextBox _txtPwd;
        private Label _lblStatus;
        private Button _btnScan;
        private Button _btnConnect;
        private Button _btnDisconnect;
        private Label _lblCurrent;

        public FormSistemaWifi()
        {
            Text = "WiFi";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.BgBlack;
            ForeColor = Theme.TextPrimary;
            Theme.ApplyToForm(this);
            BuildUI();
            RefreshCurrent();
            ScanAsync();
        }

        private void BuildUI()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Theme.BgBlack,
                Padding = new Padding(16)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            // Top: red actual + scan
            var top = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgCard };
            _lblCurrent = new Label
            {
                Text = "Red actual: …",
                Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                Location = new Point(12, 18),
                AutoSize = true
            };
            top.Controls.Add(_lblCurrent);

            _btnScan = MkBtn("Escanear", 110);
            _btnScan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnScan.Location = new Point(top.Width - 230, 14);
            _btnScan.Click += (s, e) => ScanAsync();
            top.Controls.Add(_btnScan);

            _btnDisconnect = MkBtn("Desconectar", 110);
            _btnDisconnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnDisconnect.Location = new Point(top.Width - 115, 14);
            _btnDisconnect.Click += (s, e) => Disconnect();
            top.Controls.Add(_btnDisconnect);

            top.Resize += (s, e) =>
            {
                _btnScan.Location = new Point(top.Width - 240, 14);
                _btnDisconnect.Location = new Point(top.Width - 125, 14);
            };

            root.Controls.Add(top, 0, 0);

            // Middle: lista
            _lstRedes = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BackColor = Theme.BgCard,
                ForeColor = Theme.TextPrimary,
                Font = new Font(Theme.FontFamily, 10f)
            };
            _lstRedes.Columns.Add("SSID", 280);
            _lstRedes.Columns.Add("Señal", 80);
            _lstRedes.Columns.Add("Seguridad", 140);
            _lstRedes.SelectedIndexChanged += (s, e) =>
            {
                if (_lstRedes.SelectedItems.Count > 0)
                    _txtSsid.Text = _lstRedes.SelectedItems[0].Text;
            };
            root.Controls.Add(_lstRedes, 0, 1);

            // Bottom: input + connect
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 2,
                BackColor = Theme.BgCard,
                Padding = new Padding(8)
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));

            bottom.Controls.Add(MkLabel("SSID:"), 0, 0);
            _txtSsid = MkInput();
            _txtSsid.Dock = DockStyle.Fill;
            bottom.Controls.Add(_txtSsid, 1, 0);

            bottom.Controls.Add(MkLabel("Password:"), 0, 1);
            _txtPwd = MkInput();
            _txtPwd.UseSystemPasswordChar = true;
            _txtPwd.Dock = DockStyle.Fill;
            bottom.Controls.Add(_txtPwd, 1, 1);

            var chkShow = new CheckBox
            {
                Text = "Mostrar pwd",
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Left
            };
            chkShow.CheckedChanged += (s, e) => _txtPwd.UseSystemPasswordChar = !chkShow.Checked;
            bottom.Controls.Add(chkShow, 2, 1);

            _btnConnect = MkBtn("Conectar y Guardar", 130);
            _btnConnect.Dock = DockStyle.Fill;
            _btnConnect.Click += (s, e) => Connect();
            bottom.SetRowSpan(_btnConnect, 2);
            bottom.Controls.Add(_btnConnect, 3, 0);

            root.Controls.Add(bottom, 0, 2);

            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                Font = new Font(Theme.FontFamily, 9f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(_lblStatus, 0, 3);

            Controls.Add(root);
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
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        // ==================================================================
        // netsh helpers
        // ==================================================================

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
                    StandardOutputEncoding = Encoding.GetEncoding("IBM850")
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return o ?? "";
                }
            }
            catch (Exception ex) { return "ERROR: " + ex.Message; }
        }

        private void ScanAsync()
        {
            _lblStatus.Text = "Escaneando redes…";
            _lstRedes.Items.Clear();
            // Forzar refresh del scan
            Netsh("wlan show interfaces");
            string output = Netsh("wlan show networks mode=bssid");

            // Parser muy simple por SSID + Auth + Signal
            string ssid = null, sec = null, sig = null;
            foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
            {
                var line = raw.Trim();
                if (line.StartsWith("SSID ") && line.Contains(":"))
                {
                    if (ssid != null) AddRow(ssid, sig, sec);
                    int idx = line.IndexOf(':');
                    ssid = line.Substring(idx + 1).Trim();
                    sec = "?"; sig = "?";
                }
                else if (line.StartsWith("Autenticación") || line.StartsWith("Authentication"))
                {
                    int idx = line.IndexOf(':');
                    if (idx > 0) sec = line.Substring(idx + 1).Trim();
                }
                else if (line.StartsWith("Señal") || line.StartsWith("Signal"))
                {
                    int idx = line.IndexOf(':');
                    if (idx > 0) sig = line.Substring(idx + 1).Trim();
                }
            }
            if (ssid != null) AddRow(ssid, sig, sec);
            _lblStatus.Text = "Escaneo completo: " + _lstRedes.Items.Count + " red(es).";
            RefreshCurrent();
        }

        private void AddRow(string ssid, string sig, string sec)
        {
            if (string.IsNullOrEmpty(ssid)) return;
            var it = new ListViewItem(ssid);
            it.SubItems.Add(sig ?? "");
            it.SubItems.Add(sec ?? "");
            _lstRedes.Items.Add(it);
        }

        private void RefreshCurrent()
        {
            string out_ = Netsh("wlan show interfaces");
            string ssid = "(no conectada)";
            string state = "";
            foreach (var raw in out_.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
            {
                var line = raw.Trim();
                if (line.StartsWith("SSID") && !line.StartsWith("BSSID") && line.Contains(":"))
                {
                    int idx = line.IndexOf(':');
                    var v = line.Substring(idx + 1).Trim();
                    if (!string.IsNullOrEmpty(v)) ssid = v;
                }
                if (line.StartsWith("Estado") || line.StartsWith("State"))
                {
                    int idx = line.IndexOf(':');
                    if (idx > 0) state = line.Substring(idx + 1).Trim();
                }
            }
            _lblCurrent.Text = "Red actual: " + ssid + (string.IsNullOrEmpty(state) ? "" : "   [" + state + "]");
        }

        private void Connect()
        {
            string ssid = (_txtSsid.Text ?? "").Trim();
            string pwd = _txtPwd.Text ?? "";
            if (string.IsNullOrEmpty(ssid))
            {
                _lblStatus.Text = "Falta SSID.";
                return;
            }

            _lblStatus.Text = "Generando perfil…";
            // Escribir profile XML temporal
            string xml = WifiProfileXml(ssid, pwd);
            string tmp = Path.Combine(Path.GetTempPath(), "wifi_" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tmp, xml);

            string r1 = Netsh("wlan add profile filename=\"" + tmp + "\" user=all");
            string r2 = Netsh("wlan connect name=\"" + ssid + "\"");
            try { File.Delete(tmp); } catch { }

            _lblStatus.Text = "Conectando a '" + ssid + "'… " +
                (r2.IndexOf("se ha completado", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 r2.IndexOf("completed successfully", StringComparison.OrdinalIgnoreCase) >= 0
                 ? "OK" : "(ver detalles arriba)");

            // Esperar 2s y refrescar estado
            var t = new Timer { Interval = 2500 };
            t.Tick += (s, e) => { t.Stop(); RefreshCurrent(); };
            t.Start();
        }

        private void Disconnect()
        {
            Netsh("wlan disconnect");
            _lblStatus.Text = "Desconectado.";
            RefreshCurrent();
        }

        private static string WifiProfileXml(string ssid, string password)
        {
            // WPA2-PSK por defecto; si la red es abierta y password vacía, perfil sin auth.
            string ssidHex = "";
            foreach (char c in ssid) ssidHex += ((int)c).ToString("X2");

            if (string.IsNullOrEmpty(password))
            {
                return "<?xml version=\"1.0\"?>" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                "<name>" + ssid + "</name>" +
                "<SSIDConfig><SSID><hex>" + ssidHex + "</hex><name>" + ssid + "</name></SSID></SSIDConfig>" +
                "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                "<MSM><security><authEncryption>" +
                "<authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX>" +
                "</authEncryption></security></MSM></WLANProfile>";
            }

            return "<?xml version=\"1.0\"?>" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                "<name>" + ssid + "</name>" +
                "<SSIDConfig><SSID><hex>" + ssidHex + "</hex><name>" + ssid + "</name></SSID></SSIDConfig>" +
                "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                "<MSM><security><authEncryption>" +
                "<authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX>" +
                "</authEncryption><sharedKey>" +
                "<keyType>passPhrase</keyType><protected>false</protected>" +
                "<keyMaterial>" + System.Security.SecurityElement.Escape(password) + "</keyMaterial>" +
                "</sharedKey></security></MSM></WLANProfile>";
        }
    }
}
