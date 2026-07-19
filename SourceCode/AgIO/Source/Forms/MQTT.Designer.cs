// ============================================================================
// MQTT.Designer.cs - Wrapper WinForms del broker MQTT (delegando a
// MqttBrokerService portable). El servicio vive en AgroParallel.Services
// y corre sin UI; este partial solo maneja botones y el monitor.
// ============================================================================

using AgLibrary.Logging;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AgIO
{
    public partial class FormLoop
    {
        // Servicio portable (netstandard2.0, sin WinForms).
        private IMqttBrokerService _mqttBrokerService;
        private int _mqttPort = 1883;

        private void EnsureMqttService()
        {
            if (_mqttBrokerService == null)
                _mqttBrokerService = new MqttBrokerService();
        }

        // Exponer el servicio para que otros componentes lo consuman.
        public IMqttBrokerService MqttBroker => _mqttBrokerService;

        // ── Arranque ───────────────────────────────────────────────────
        private async void StartMqttBroker()
        {
            EnsureMqttService();
            if (_mqttBrokerService.IsRunning) return;

            try
            {
                await _mqttBrokerService.StartAsync(_mqttPort);
                Log.EventWriter("MQTT Broker started on port " + _mqttPort);
                UpdateMqttUI();
            }
            catch (Exception ex)
            {
                Log.EventWriter("MQTT Broker start failed: " + ex.Message);
                UpdateMqttUI();
                TimedMessageBox(3000, "MQTT Broker Error",
                    "Port " + _mqttPort + " in use?\n" + ex.Message);
            }
        }

        // ── Parada ─────────────────────────────────────────────────────
        private async void StopMqttBroker()
        {
            if (_mqttBrokerService == null || !_mqttBrokerService.IsRunning) return;

            try { await _mqttBrokerService.StopAsync(); }
            catch (Exception ex) { Log.EventWriter("MQTT Broker stop error: " + ex.Message); }
            finally
            {
                UpdateMqttUI();
                Log.EventWriter("MQTT Broker stopped");
            }
        }

        // ── Toggle desde el botón ──────────────────────────────────────
        private void btnMQTT_Click(object sender, EventArgs e)
        {
            EnsureMqttService();
            if (_mqttBrokerService.IsRunning) StopMqttBroker();
            else StartMqttBroker();
        }

        // ── Monitor (doble click abre el form) ────────────────────────
        private void btnMQTT_DoubleClick(object sender, EventArgs e)
        {
            ShowMqttMonitor();
        }

        // ── UI update ─────────────────────────────────────────────────
        private void UpdateMqttUI()
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(UpdateMqttUI)); } catch { }
                return;
            }

            bool running = _mqttBrokerService != null && _mqttBrokerService.IsRunning;
            if (running)
            {
                btnMQTT.BackColor = Color.LimeGreen;
                int clients = _mqttBrokerService.ClientsConnected;
                lblMQTTStatus.Text = clients + " client" + (clients != 1 ? "s" : "");
                lblMQTTStatus.ForeColor = Color.Black;
            }
            else
            {
                btnMQTT.BackColor = Color.Gainsboro;
                lblMQTTStatus.Text = "Off";
                lblMQTTStatus.ForeColor = Color.Gray;
            }
        }

        // ── Llamado desde TwoSecondLoop ────────────────────────────────
        private void DoMqttStatus()
        {
            if (_mqttBrokerService == null || !_mqttBrokerService.IsRunning) return;
            UpdateMqttUI();
        }

        // ── Form de monitor MQTT ───────────────────────────────────────
        private Form _mqttMonitorForm;

        private void ShowMqttMonitor()
        {
            EnsureMqttService();
            if (_mqttMonitorForm != null && !_mqttMonitorForm.IsDisposed)
            {
                _mqttMonitorForm.BringToFront();
                return;
            }

            _mqttMonitorForm = new Form
            {
                Text = "MQTT Broker Monitor",
                Size = new Size(650, 520),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(20, 20, 24),
                ForeColor = Color.White,
                FormBorderStyle = FormBorderStyle.SizableToolWindow
            };

            var pnlHeader = new Panel
            {
                Dock = DockStyle.Top, Height = 80,
                BackColor = Color.FromArgb(28, 28, 32)
            };

            var lblTitle = new Label
            {
                Text = "MQTT Broker  —  Port " + _mqttPort,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.LimeGreen,
                Location = new Point(16, 8), AutoSize = true
            };
            pnlHeader.Controls.Add(lblTitle);

            var lblInfo = new Label
            {
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(180, 180, 180),
                Location = new Point(16, 40), AutoSize = true
            };
            pnlHeader.Controls.Add(lblInfo);
            _mqttMonitorForm.Controls.Add(pnlHeader);

            var lstTopics = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(12, 12, 14),
                ForeColor = Color.FromArgb(0, 230, 118),
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.None,
                SelectionMode = SelectionMode.None
            };
            _mqttMonitorForm.Controls.Add(lstTopics);
            lstTopics.BringToFront();

            var pnlFooter = new Panel
            {
                Dock = DockStyle.Bottom, Height = 44,
                BackColor = Color.FromArgb(28, 28, 32)
            };

            var btnClear = new Button
            {
                Text = "Clear", Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.White, BackColor = Color.FromArgb(60, 60, 65),
                FlatStyle = FlatStyle.Flat, Size = new Size(80, 30), Location = new Point(16, 7)
            };
            btnClear.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
            btnClear.Click += (s, ev) => lstTopics.Items.Clear();
            pnlFooter.Controls.Add(btnClear);

            var btnToggle = new Button
            {
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Size = new Size(120, 30), Location = new Point(110, 7)
            };
            btnToggle.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
            Action updateToggle = () =>
            {
                bool r = _mqttBrokerService != null && _mqttBrokerService.IsRunning;
                btnToggle.Text = r ? "Stop Broker" : "Start Broker";
                btnToggle.BackColor = r ? Color.FromArgb(140, 30, 30) : Color.FromArgb(30, 120, 50);
            };
            updateToggle();
            btnToggle.Click += (s, ev) =>
            {
                if (_mqttBrokerService != null && _mqttBrokerService.IsRunning)
                    StopMqttBroker();
                else StartMqttBroker();
                updateToggle();
            };
            pnlFooter.Controls.Add(btnToggle);
            _mqttMonitorForm.Controls.Add(pnlFooter);

            var tmrRefresh = new Timer { Interval = 1000 };
            tmrRefresh.Tick += (s, ev) =>
            {
                if (_mqttMonitorForm == null || _mqttMonitorForm.IsDisposed)
                {
                    tmrRefresh.Stop(); tmrRefresh.Dispose(); return;
                }
                bool r = _mqttBrokerService != null && _mqttBrokerService.IsRunning;
                lblInfo.Text = string.Format("Status: {0}  |  Clients: {1}  |  Messages: {2:N0}",
                    r ? "RUNNING" : "STOPPED",
                    r ? _mqttBrokerService.ClientsConnected : 0,
                    r ? _mqttBrokerService.MessagesTotal : 0);

                var items = _mqttBrokerService?.GetRecentTopics(200) ?? new System.Collections.Generic.List<string>();
                lstTopics.BeginUpdate();
                lstTopics.Items.Clear();
                foreach (var t in items) lstTopics.Items.Add(t);
                lstTopics.EndUpdate();
                updateToggle();
            };
            tmrRefresh.Start();

            _mqttMonitorForm.FormClosed += (s, ev) =>
            {
                tmrRefresh.Stop(); tmrRefresh.Dispose(); _mqttMonitorForm = null;
            };

            _mqttMonitorForm.Show(this);
        }
    }
}
