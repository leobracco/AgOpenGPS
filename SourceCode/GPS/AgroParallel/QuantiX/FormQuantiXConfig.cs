// ============================================================================
// FormQuantiXConfig.cs - UI nativa para configurar QuantiX UDP (paso 10)
// Ubicación: SourceCode/GPS/AgroParallel/QuantiX/FormQuantiXConfig.cs
// Target: net48 (C# 7.3)
//
// Permite editar quantiX.json sin tocar el archivo a mano:
//   - Habilitar / deshabilitar
//   - Host + Puerto
//   - Sample rate (Hz)
//   - Valor "afuera"
//   - SendOnlyOnChange
//   - IncludePosition
//   - Unidad (etiqueta)
// Ademas un boton "Probar UDP" que envia un paquete sintetico sin tocar el
// sender real — sirve para validar que el listener esta escuchando.
//
// Estilo: Agro Parallel 2026 dark theme via Theme helpers.
// ============================================================================

using System;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using AgroParallel.VistaX;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXConfig : Form
    {
        private readonly QuantiXConfig _cfg;

        private CheckBox _chkEnabled;
        private TextBox _txtHost;
        private NumericUpDown _numPort;
        private NumericUpDown _numRate;
        private NumericUpDown _numOutside;
        private CheckBox _chkOnlyChange;
        private CheckBox _chkIncludePos;
        private TextBox _txtUnit;
        private Button _btnTest;
        private Button _btnOk;
        private Button _btnCancel;
        private Label _lblStatus;

        public FormQuantiXConfig(QuantiXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            BuildUI();
            LoadFromConfig();
        }

        private void BuildUI()
        {
            Text = "QuantiX — Salida UDP de dosis";
            Size = new Size(520, 500);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            Theme.ApplyToForm(this);

            int x0 = 24, labelW = 180, fieldX = 216;
            int y = 24, dy = 44;

            // --- Habilitado ---
            _chkEnabled = Theme.MkCheck("Habilitado", false);
            _chkEnabled.Location = new Point(x0, y);
            Controls.Add(_chkEnabled);
            y += dy;

            // --- Host ---
            AddLabel("Host (IP):", x0, y + 3, labelW);
            _txtHost = Theme.MkTextBox(250);
            _txtHost.Location = new Point(fieldX, y);
            Controls.Add(_txtHost);
            y += dy;

            // --- Puerto ---
            AddLabel("Puerto:", x0, y + 3, labelW);
            _numPort = Theme.MkNumeric(1, 65535, 1, 0, 1m, 130);
            _numPort.Location = new Point(fieldX, y);
            Controls.Add(_numPort);
            y += dy;

            // --- Frecuencia ---
            AddLabel("Frecuencia (Hz):", x0, y + 3, labelW);
            _numRate = Theme.MkNumeric(0, 20, 1, 1, 0.5m, 130);
            _numRate.Location = new Point(fieldX, y);
            Controls.Add(_numRate);
            y += dy;

            // --- Valor fuera de area ---
            AddLabel("Valor fuera de area:", x0, y + 3, labelW);
            _numOutside = Theme.MkNumeric(-1000000, 1000000, 0, 3, 1m, 130);
            _numOutside.Location = new Point(fieldX, y);
            Controls.Add(_numOutside);
            y += dy;

            // --- Unidad ---
            AddLabel("Unidad (etiqueta):", x0, y + 3, labelW);
            _txtUnit = Theme.MkTextBox(130);
            _txtUnit.Location = new Point(fieldX, y);
            Controls.Add(_txtUnit);
            y += dy;

            // --- CheckBoxes ---
            _chkOnlyChange = Theme.MkCheck("Enviar solo al cambiar de valor", false);
            _chkOnlyChange.Location = new Point(x0, y);
            Controls.Add(_chkOnlyChange);
            y += dy - 4;

            _chkIncludePos = Theme.MkCheck("Incluir posicion (lat/lon/heading)", false);
            _chkIncludePos.Location = new Point(x0, y);
            Controls.Add(_chkIncludePos);
            y += dy + 6;

            // --- Status label ---
            _lblStatus = new Label
            {
                Location = new Point(x0, y), Size = new Size(460, 22),
                ForeColor = Theme.TextFaint, Text = "",
                Font = Theme.FontBody, BackColor = Color.Transparent
            };
            Controls.Add(_lblStatus);

            // --- Buttons ---
            int btnY = 410;

            _btnTest = Theme.MkSecondaryButton("Probar UDP", 130, 36);
            _btnTest.Location = new Point(x0, btnY);
            _btnTest.Click += OnTestClick;
            Controls.Add(_btnTest);

            _btnCancel = Theme.MkSecondaryButton("Cancelar", 110, 36);
            _btnCancel.Location = new Point(270, btnY);
            _btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(_btnCancel);

            _btnOk = Theme.MkAccentButton("Aplicar", 110, 36);
            _btnOk.Location = new Point(388, btnY);
            _btnOk.DialogResult = DialogResult.OK;
            _btnOk.Click += OnApplyClick;
            Controls.Add(_btnOk);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void AddLabel(string text, int x, int y, int w)
        {
            Controls.Add(new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 22),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                Font = Theme.FontBody
            });
        }

        private void LoadFromConfig()
        {
            _chkEnabled.Checked = _cfg.Enabled;
            _txtHost.Text = _cfg.UdpHost ?? "127.0.0.1";
            _numPort.Value = Clamp(_cfg.UdpPort, 1, 65535);
            _numRate.Value = (decimal)ClampD(_cfg.SampleRateHz, 0.2, 20);
            _numOutside.Value = (decimal)_cfg.OutsideValue;
            _txtUnit.Text = _cfg.DoseUnit ?? "";
            _chkOnlyChange.Checked = _cfg.SendOnlyOnChange;
            _chkIncludePos.Checked = _cfg.IncludePosition;
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            if (!TryParseHost(_txtHost.Text, out _))
            {
                _lblStatus.ForeColor = Theme.Error;
                _lblStatus.Text = "Host invalido: " + _txtHost.Text;
                DialogResult = DialogResult.None;
                return;
            }

            _cfg.Enabled = _chkEnabled.Checked;
            _cfg.UdpHost = _txtHost.Text.Trim();
            _cfg.UdpPort = (int)_numPort.Value;
            _cfg.SampleRateHz = (double)_numRate.Value;
            _cfg.OutsideValue = (double)_numOutside.Value;
            _cfg.DoseUnit = _txtUnit.Text?.Trim() ?? "";
            _cfg.SendOnlyOnChange = _chkOnlyChange.Checked;
            _cfg.IncludePosition = _chkIncludePos.Checked;
            _cfg.Save();
        }

        private void OnTestClick(object sender, EventArgs e)
        {
            _lblStatus.Text = "";
            _lblStatus.ForeColor = Theme.TextFaint;

            if (!TryParseHost(_txtHost.Text, out IPAddress ip))
            {
                _lblStatus.ForeColor = Theme.Error;
                _lblStatus.Text = "Host invalido: " + _txtHost.Text;
                return;
            }

            try
            {
                using (var udp = new UdpClient())
                {
                    string json = "{\"dose\":0.0,\"inside\":false,\"field\":\"__test__\","
                        + "\"ts\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
                    var bytes = Encoding.UTF8.GetBytes(json);
                    var ep = new IPEndPoint(ip, (int)_numPort.Value);
                    udp.Send(bytes, bytes.Length, ep);
                }
                _lblStatus.ForeColor = Theme.Ok;
                _lblStatus.Text = "Paquete de prueba enviado a "
                    + _txtHost.Text + ":" + (int)_numPort.Value;
            }
            catch (Exception ex)
            {
                _lblStatus.ForeColor = Theme.Error;
                _lblStatus.Text = "Error: " + ex.Message;
            }
        }

        private static bool TryParseHost(string host, out IPAddress ip)
        {
            ip = null;
            if (string.IsNullOrWhiteSpace(host)) return false;
            host = host.Trim();
            if (IPAddress.TryParse(host, out ip)) return true;
            try
            {
                var addrs = Dns.GetHostAddresses(host);
                if (addrs != null && addrs.Length > 0)
                {
                    ip = addrs[0];
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static decimal Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static double ClampD(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
