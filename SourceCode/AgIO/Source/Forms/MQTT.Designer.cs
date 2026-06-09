// ============================================================================
// MQTT.Designer.cs - Broker MQTT embebido en AgIO (MQTTnet Server)
// Partial de FormLoop — sigue el patrón de UDP.designer.cs / NTRIPComm.Designer.cs
// ============================================================================

using AgLibrary.Logging;
using MQTTnet;
using MQTTnet.Server;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgIO
{
    public partial class FormLoop
    {
        // ── Estado del broker ──────────────────────────────────────────
        private MqttServer _mqttServer;
        private bool _mqttRunning;
        private int _mqttPort = 1883;
        private int _mqttClientsConnected;
        private long _mqttMessagesTotal;
        private DateTime _mqttStartTime;

        // Últimos tópicos recibidos (para el monitor).
        private readonly LinkedList<string> _mqttRecentTopics = new LinkedList<string>();
        private readonly object _mqttLock = new object();

        // ── Arranque ───────────────────────────────────────────────────
        private async void StartMqttBroker()
        {
            if (_mqttRunning) return;

            try
            {
                var factory = new MqttFactory();
                var options = new MqttServerOptionsBuilder()
                    .WithDefaultEndpoint()
                    .WithDefaultEndpointPort(_mqttPort)
                    .Build();

                _mqttServer = factory.CreateMqttServer(options);

                _mqttServer.ClientConnectedAsync += e =>
                {
                    _mqttClientsConnected++;
                    AddRecentTopic("[+] " + e.ClientId);
                    Log.EventWriter("MQTT Client connected: " + e.ClientId);
                    return Task.CompletedTask;
                };

                _mqttServer.ClientDisconnectedAsync += e =>
                {
                    if (_mqttClientsConnected > 0) _mqttClientsConnected--;
                    AddRecentTopic("[-] " + e.ClientId);
                    Log.EventWriter("MQTT Client disconnected: " + e.ClientId);
                    return Task.CompletedTask;
                };

                _mqttServer.InterceptingPublishAsync += e =>
                {
                    _mqttMessagesTotal++;
                    AddRecentTopic(e.ApplicationMessage.Topic);
                    return Task.CompletedTask;
                };

                await _mqttServer.StartAsync();
                _mqttRunning = true;
                _mqttStartTime = DateTime.Now;

                Log.EventWriter("MQTT Broker started on port " + _mqttPort);
                UpdateMqttUI();
            }
            catch (Exception ex)
            {
                Log.EventWriter("MQTT Broker start failed: " + ex.Message);
                _mqttRunning = false;
                UpdateMqttUI();

                TimedMessageBox(3000, "MQTT Broker Error",
                    "Port " + _mqttPort + " in use?\n" + ex.Message);
            }
        }

        // ── Parada ─────────────────────────────────────────────────────
        private async void StopMqttBroker()
        {
            if (!_mqttRunning || _mqttServer == null) return;

            try
            {
                await _mqttServer.StopAsync();
                _mqttServer.Dispose();
            }
            catch (Exception ex)
            {
                Log.EventWriter("MQTT Broker stop error: " + ex.Message);
            }
            finally
            {
                _mqttServer = null;
                _mqttRunning = false;
                _mqttClientsConnected = 0;
                _mqttMessagesTotal = 0;
                UpdateMqttUI();
                Log.EventWriter("MQTT Broker stopped");
            }
        }

        // ── Toggle desde el botón ──────────────────────────────────────
        private void btnMQTT_Click(object sender, EventArgs e)
        {
            if (_mqttRunning)
                StopMqttBroker();
            else
                StartMqttBroker();
        }

        // ── Monitor (doble click abre el form) ────────────────────────
        private void btnMQTT_DoubleClick(object sender, EventArgs e)
        {
            ShowMqttMonitor();
        }

        // ── Helpers ────────────────────────────────────────────────────
        private void AddRecentTopic(string topic)
        {
            lock (_mqttLock)
            {
                _mqttRecentTopics.AddFirst(
                    DateTime.Now.ToString("HH:mm:ss") + "  " + topic);
                while (_mqttRecentTopics.Count > 200)
                    _mqttRecentTopics.RemoveLast();
            }
        }

        private void UpdateMqttUI()
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(UpdateMqttUI)); } catch { }
                return;
            }

            if (_mqttRunning)
            {
                btnMQTT.BackColor = Color.LimeGreen;
                lblMQTTStatus.Text = _mqttClientsConnected + " client"
                    + (_mqttClientsConnected != 1 ? "s" : "");
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
            if (!_mqttRunning) return;
            UpdateMqttUI();
        }

        // ── Form de monitor MQTT ───────────────────────────────────────
        private Form _mqttMonitorForm;

        private void ShowMqttMonitor()
        {
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

            // ── Header info ──
            var pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80,
                BackColor = Color.FromArgb(28, 28, 32)
            };

            var lblTitle = new Label
            {
                Text = "MQTT Broker  —  Port " + _mqttPort,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.LimeGreen,
                Location = new Point(16, 8),
                AutoSize = true
            };
            pnlHeader.Controls.Add(lblTitle);

            var lblInfo = new Label
            {
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(180, 180, 180),
                Location = new Point(16, 40),
                AutoSize = true
            };
            pnlHeader.Controls.Add(lblInfo);

            _mqttMonitorForm.Controls.Add(pnlHeader);

            // ── Lista de tópicos recientes ──
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

            // ── Footer con botones ──
            var pnlFooter = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = Color.FromArgb(28, 28, 32)
            };

            var btnClear = new Button
            {
                Text = "Clear",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(60, 60, 65),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(80, 30),
                Location = new Point(16, 7)
            };
            btnClear.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
            btnClear.Click += (s, ev) =>
            {
                lock (_mqttLock) _mqttRecentTopics.Clear();
                lstTopics.Items.Clear();
            };
            pnlFooter.Controls.Add(btnClear);

            var btnToggle = new Button
            {
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(120, 30),
                Location = new Point(110, 7)
            };
            btnToggle.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
            Action updateToggle = () =>
            {
                btnToggle.Text = _mqttRunning ? "Stop Broker" : "Start Broker";
                btnToggle.BackColor = _mqttRunning
                    ? Color.FromArgb(140, 30, 30) : Color.FromArgb(30, 120, 50);
            };
            updateToggle();
            btnToggle.Click += (s, ev) =>
            {
                if (_mqttRunning) StopMqttBroker(); else StartMqttBroker();
                updateToggle();
            };
            pnlFooter.Controls.Add(btnToggle);

            _mqttMonitorForm.Controls.Add(pnlFooter);

            // ── Timer de refresh ──
            var tmrRefresh = new Timer { Interval = 1000 };
            tmrRefresh.Tick += (s, ev) =>
            {
                if (_mqttMonitorForm == null || _mqttMonitorForm.IsDisposed)
                {
                    tmrRefresh.Stop();
                    tmrRefresh.Dispose();
                    return;
                }

                // Info line.
                string status = _mqttRunning ? "RUNNING" : "STOPPED";
                string uptime = "";
                if (_mqttRunning)
                {
                    var ts = DateTime.Now - _mqttStartTime;
                    uptime = string.Format("  |  Uptime: {0:D2}:{1:D2}:{2:D2}",
                        (int)ts.TotalHours, ts.Minutes, ts.Seconds);
                }
                lblInfo.Text = string.Format("Status: {0}  |  Clients: {1}  |  Messages: {2:N0}{3}",
                    status, _mqttClientsConnected, _mqttMessagesTotal, uptime);

                // Topic list.
                string[] items;
                lock (_mqttLock)
                    items = _mqttRecentTopics.Take(200).ToArray();

                lstTopics.BeginUpdate();
                lstTopics.Items.Clear();
                foreach (var t in items)
                    lstTopics.Items.Add(t);
                lstTopics.EndUpdate();

                updateToggle();
            };
            tmrRefresh.Start();

            _mqttMonitorForm.FormClosed += (s, ev) =>
            {
                tmrRefresh.Stop();
                tmrRefresh.Dispose();
                _mqttMonitorForm = null;
            };

            _mqttMonitorForm.Show(this);
        }
    }
}
