// ============================================================================
// FormVistaXConfig.cs - UI nativa para editar vistaX.json
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXConfig.cs
// Target: net48 (C# 7.3)
//
// Campos principales del VistaXConfig expuestos en un modal: broker MQTT,
// topics, path del implemento, metodo de inicio del monitoreo, y layout
// del panel embebido (alto, ancho %, margen inferior). Al aplicar guarda
// el JSON; el caller (FormGPS) reinicia monitor + panel.
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AgroParallel.VistaX
{
    public class FormVistaXConfig : Form
    {
        private readonly VistaXConfig _cfg;

        private CheckBox _chkEnabled;
        private TextBox _txtHost;
        private NumericUpDown _numPort;
        private TextBox _txtClientId;
        private TextBox _txtTopicTele;
        private TextBox _txtTopicSections;
        private TextBox _txtTopicSpeed;
        private TextBox _txtImplemento;
        private Button _btnImplementoBrowse;
        private ComboBox _cmbMetodo;
        private NumericUpDown _numUmbral;
        private NumericUpDown _numConfirm;
        private NumericUpDown _numPanelH;
        private NumericUpDown _numPanelW;
        private NumericUpDown _numPanelBottom;
        private Button _btnOk;
        private Button _btnCancel;

        public FormVistaXConfig(VistaXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            BuildUI();
            LoadFromConfig();
        }

        private void BuildUI()
        {
            Text = "VistaX — Configuracion";
            Size = new Size(560, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9.5f);

            int labelX = 18, fieldX = 220, fieldW = 300;
            int y = 18, dy = 32;

            _chkEnabled = new CheckBox
            {
                Text = "Habilitado",
                Location = new Point(labelX, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            Controls.Add(_chkEnabled);
            y += dy + 6;

            AddSectionHeader("Broker MQTT", y);
            y += 24;
            y = AddLabeledField("Host:", labelX, fieldX, fieldW, y,
                out _txtHost);
            y = AddLabeledNumeric("Puerto:", labelX, fieldX, 120, y,
                1, 65535, out _numPort);
            y = AddLabeledField("ClientId:", labelX, fieldX, fieldW, y,
                out _txtClientId);
            y += 6;

            AddSectionHeader("Topics", y);
            y += 24;
            y = AddLabeledField("Telemetria:", labelX, fieldX, fieldW, y,
                out _txtTopicTele);
            y = AddLabeledField("Secciones:", labelX, fieldX, fieldW, y,
                out _txtTopicSections);
            y = AddLabeledField("Velocidad:", labelX, fieldX, fieldW, y,
                out _txtTopicSpeed);
            y += 6;

            AddSectionHeader("Implemento", y);
            y += 24;
            Controls.Add(new Label
            {
                Text = "Archivo JSON:",
                Location = new Point(labelX, y + 3),
                Size = new Size(fieldX - labelX - 8, 20)
            });
            _txtImplemento = new TextBox
            {
                Location = new Point(fieldX, y), Size = new Size(fieldW - 40, 24)
            };
            Controls.Add(_txtImplemento);
            _btnImplementoBrowse = new Button
            {
                Text = "...", Location = new Point(fieldX + fieldW - 36, y),
                Size = new Size(36, 26)
            };
            _btnImplementoBrowse.Click += OnBrowseImplemento;
            Controls.Add(_btnImplementoBrowse);
            y += dy;

            AddSectionHeader("Monitoreo", y);
            y += 24;
            Controls.Add(new Label
            {
                Text = "Metodo inicio:",
                Location = new Point(labelX, y + 3),
                Size = new Size(fieldX - labelX - 8, 20)
            });
            _cmbMetodo = new ComboBox
            {
                Location = new Point(fieldX, y), Size = new Size(180, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbMetodo.Items.AddRange(new object[] {
                "sensores", "herramienta", "pintando", "manual"
            });
            Controls.Add(_cmbMetodo);
            y += dy;
            y = AddLabeledNumeric("Umbral sensores activos:", labelX, fieldX, 120, y,
                0, 200, out _numUmbral);
            y = AddLabeledNumeric("Tiempo confirmacion (ms):", labelX, fieldX, 120, y,
                0, 60000, out _numConfirm);
            y += 6;

            AddSectionHeader("Layout del panel embebido", y);
            y += 24;
            y = AddLabeledNumeric("Alto (px):", labelX, fieldX, 120, y,
                40, 800, out _numPanelH);
            y = AddLabeledNumeric("Ancho (%):", labelX, fieldX, 120, y,
                10, 100, out _numPanelW);
            y = AddLabeledNumeric("Margen inferior (px):", labelX, fieldX, 120, y,
                0, 600, out _numPanelBottom);

            _btnCancel = new Button
            {
                Text = "Cancelar",
                Location = new Point(340, Height - 70),
                Size = new Size(100, 32),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(_btnCancel);

            _btnOk = new Button
            {
                Text = "Aplicar",
                Location = new Point(448, Height - 70),
                Size = new Size(100, 32),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = DialogResult.OK
            };
            _btnOk.Click += OnApplyClick;
            Controls.Add(_btnOk);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void AddSectionHeader(string text, int y)
        {
            Controls.Add(new Label
            {
                Text = text,
                Location = new Point(12, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80)
            });
        }

        private int AddLabeledField(string label, int labelX, int fieldX,
            int fieldW, int y, out TextBox tb)
        {
            Controls.Add(new Label
            {
                Text = label,
                Location = new Point(labelX, y + 3),
                Size = new Size(fieldX - labelX - 8, 20)
            });
            tb = new TextBox
            {
                Location = new Point(fieldX, y),
                Size = new Size(fieldW, 24)
            };
            Controls.Add(tb);
            return y + 32;
        }

        private int AddLabeledNumeric(string label, int labelX, int fieldX,
            int fieldW, int y, decimal min, decimal max, out NumericUpDown nud)
        {
            Controls.Add(new Label
            {
                Text = label,
                Location = new Point(labelX, y + 3),
                Size = new Size(fieldX - labelX - 8, 20)
            });
            nud = new NumericUpDown
            {
                Location = new Point(fieldX, y),
                Size = new Size(fieldW, 24),
                Minimum = min,
                Maximum = max
            };
            Controls.Add(nud);
            return y + 32;
        }

        private void OnBrowseImplemento(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Seleccionar JSON de implemento";
                ofd.Filter = "JSON (*.json)|*.json|Todos (*.*)|*.*";
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string candidate = Path.Combine(baseDir, "data", "implementos");
                if (Directory.Exists(candidate)) ofd.InitialDirectory = candidate;
                else if (Directory.Exists(baseDir)) ofd.InitialDirectory = baseDir;

                if (ofd.ShowDialog(this) == DialogResult.OK)
                    _txtImplemento.Text = ofd.FileName;
            }
        }

        private void LoadFromConfig()
        {
            _chkEnabled.Checked = _cfg.Enabled;
            _txtHost.Text = _cfg.BrokerAddress ?? "127.0.0.1";
            _numPort.Value = Clamp(_cfg.BrokerPort, 1, 65535);
            _txtClientId.Text = _cfg.ClientId ?? "";
            _txtTopicTele.Text = _cfg.TelemetriaTopic ?? "";
            _txtTopicSections.Text = _cfg.SectionsTopic ?? "";
            _txtTopicSpeed.Text = _cfg.SpeedTopic ?? "";
            _txtImplemento.Text = _cfg.ImplementoJsonPath ?? "";

            string metodo = (_cfg.MetodoInicio ?? "sensores").Trim().ToLowerInvariant();
            int idx = _cmbMetodo.Items.IndexOf(metodo);
            _cmbMetodo.SelectedIndex = idx >= 0 ? idx : 0;

            _numUmbral.Value = Clamp(_cfg.UmbralSensoresActivos, 0, 200);
            _numConfirm.Value = Clamp(_cfg.TiempoConfirmacionMs, 0, 60000);

            _numPanelH.Value = Clamp(_cfg.PanelHeight > 0 ? _cfg.PanelHeight : 150, 40, 800);
            _numPanelW.Value = Clamp(_cfg.PanelWidthPercent > 0 ? _cfg.PanelWidthPercent : 60, 10, 100);
            _numPanelBottom.Value = Clamp(_cfg.PanelBottomMargin >= 0 ? _cfg.PanelBottomMargin : 20, 0, 600);
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            _cfg.Enabled = _chkEnabled.Checked;
            _cfg.BrokerAddress = (_txtHost.Text ?? "").Trim();
            _cfg.BrokerPort = (int)_numPort.Value;
            _cfg.ClientId = (_txtClientId.Text ?? "").Trim();
            _cfg.TelemetriaTopic = (_txtTopicTele.Text ?? "").Trim();
            _cfg.SectionsTopic = (_txtTopicSections.Text ?? "").Trim();
            _cfg.SpeedTopic = (_txtTopicSpeed.Text ?? "").Trim();
            _cfg.ImplementoJsonPath = (_txtImplemento.Text ?? "").Trim();
            _cfg.MetodoInicio = (_cmbMetodo.SelectedItem as string) ?? "sensores";
            _cfg.UmbralSensoresActivos = (int)_numUmbral.Value;
            _cfg.TiempoConfirmacionMs = (int)_numConfirm.Value;
            _cfg.PanelHeight = (int)_numPanelH.Value;
            _cfg.PanelWidthPercent = (int)_numPanelW.Value;
            _cfg.PanelBottomMargin = (int)_numPanelBottom.Value;
            _cfg.Save();
        }

        private static decimal Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
