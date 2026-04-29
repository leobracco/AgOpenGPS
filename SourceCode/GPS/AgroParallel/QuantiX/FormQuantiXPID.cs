// ============================================================================
// FormQuantiXPID.cs - Tuning PID v2 con feedforward, tipo motor, RPM
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXPID : Form
    {
        private readonly QuantiXConfig _cfg;
        private MotoresConfig _motores;
        private MqttClientWrapper _mqtt;

        // Controles PID.
        private ComboBox _cboNodo, _cboMotor, _cboMotorType;
        private NumericUpDown _numKp, _numKi, _numKd;
        private TrackBar _trkMinPwm, _trkDeadband;
        private Label _lblMinPwm, _lblDeadband;
        private NumericUpDown _numMaxHz, _numFFGain, _numAlpha, _numSlewPerSec, _numPIDTime, _numDientes;
        private Panel _graphPanel;
        private Timer _refreshTimer;

        // Historial gráfico.
        private readonly List<double> _histTarget = new List<double>();
        private readonly List<double> _histReal = new List<double>();
        private const int MaxHistory = 100;

        // Live status MQTT.
        private double _liveTarget, _liveReal;
        private int _livePwm, _liveRpm;
        private long _livePulsos;
        private double _liveMeterCal;

        // CSV log.
        private string _pidLogPath;
        private DateTime _pidLogStart;

        public FormQuantiXPID(QuantiXConfig cfg)
        {
            _cfg = cfg ?? QuantiXConfig.Load();
            _motores = MotoresConfig.Load();
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadMotorValues();
            StartMqtt();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_refreshTimer != null) { _refreshTimer.Stop(); _refreshTimer.Dispose(); }
            if (_mqtt != null)
            {
                var m = _mqtt; _mqtt = null;
                System.Threading.Tasks.Task.Run(() => { try { m.Dispose(); } catch { } });
            }
        }

        // =====================================================================
        // UI
        // =====================================================================

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;
            MinimumSize = new Size(480, 400);
            Size = new Size(580, 760);

            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.BgBlack };
            Controls.Add(body);

            int lx = 16, y = 12;

            // ── Nodo + Motor ──
            body.Controls.Add(MkLabel("Nodo:", lx, y));
            _cboNodo = Theme.MkCombo(200);
            _cboNodo.Location = new Point(lx + 40, y);
            foreach (var n in _motores.Nodos) _cboNodo.Items.Add(n.Nombre + " (" + n.Uid + ")");
            if (_cboNodo.Items.Count > 0) _cboNodo.SelectedIndex = 0;
            _cboNodo.SelectedIndexChanged += (s, ev) => LoadMotorValues();
            body.Controls.Add(_cboNodo);

            body.Controls.Add(MkLabel("Motor:", lx + 260, y));
            _cboMotor = Theme.MkCombo(120);
            _cboMotor.Location = new Point(lx + 310, y);
            _cboMotor.Items.AddRange(new object[] { "M0", "M1" });
            _cboMotor.SelectedIndex = 0;
            _cboMotor.SelectedIndexChanged += (s, ev) => LoadMotorValues();
            body.Controls.Add(_cboMotor);
            y += 32;

            // ── Tipo motor ──
            body.Controls.Add(MkLabel("Tipo:", lx, y));
            _cboMotorType = Theme.MkCombo(140);
            _cboMotorType.Location = new Point(lx + 40, y);
            _cboMotorType.Items.AddRange(new object[] { "El\u00E9ctrico DC", "Hidr\u00E1ulico" });
            _cboMotorType.SelectedIndex = 0;
            _cboMotorType.SelectedIndexChanged += (s, ev) =>
            {
                var m = GetSelectedMotor();
                if (m != null) m.MotorType = _cboMotorType.SelectedIndex;
                SendConfig();
            };
            body.Controls.Add(_cboMotorType);

            // Dientes engranaje
            body.Controls.Add(MkLabel("PPR:", lx + 200, y));
            _numDientes = Theme.MkNumeric(1, 2000, 600, 0, 1, 70);
            _numDientes.Location = new Point(lx + 232, y - 2);
            _numDientes.ValueChanged += (s, ev) => { var m = GetSelectedMotor(); if (m != null) m.DientesEngranaje = (int)_numDientes.Value; };
            body.Controls.Add(_numDientes);

            // PIDtime
            body.Controls.Add(MkLabel("PID ms:", lx + 320, y));
            _numPIDTime = Theme.MkNumeric(10, 500, 50, 0, 1, 60);
            _numPIDTime.Location = new Point(lx + 370, y - 2);
            _numPIDTime.ValueChanged += (s, ev) => { var m = GetSelectedMotor(); if (m != null) m.PIDTime = (int)_numPIDTime.Value; SendConfig(); };
            body.Controls.Add(_numPIDTime);
            y += 30;

            // ── Presets ──
            body.Controls.Add(MkLabel("Presets:", lx, y));
            var btnElec = Theme.MkAccentButton("El\u00E9ctrico", 80, 24);
            btnElec.Location = new Point(lx + 60, y - 2);
            btnElec.Click += (s, ev) => ApplyPreset(0, 80, 30, 8, 600, 40, 2, 1.0, 0.4, 5000, 50);
            body.Controls.Add(btnElec);

            var btnHyd = Theme.MkButton("Hidr\u00E1ulico", Theme.Info, Theme.TextPrimary, 80, 24);
            btnHyd.Location = new Point(lx + 150, y - 2);
            btnHyd.Click += (s, ev) => ApplyPreset(1, 35, 20, 30, 1200, 30, 5, 1.0, 0.2, 1000, 200);
            body.Controls.Add(btnHyd);

            var btnAgg = Theme.MkDangerButton("Agresivo", 80, 24);
            btnAgg.Location = new Point(lx + 240, y - 2);
            btnAgg.Click += (s, ev) => ApplyPreset(0, 120, 50, 10, 500, 40, 1, 1.0, 0.5, 8000, 30);
            body.Controls.Add(btnAgg);
            y += 30;

            // ── Feedforward params (ANTES de sliders para que no queden tapados) ──
            body.Controls.Add(MkLabel("Max Hz:", lx, y + 2));
            _numMaxHz = Theme.MkNumeric(1, 500, 40, 1, 1, 65);
            _numMaxHz.Location = new Point(lx + 55, y - 2);
            _numMaxHz.ValueChanged += (s, ev) => { SetVal("MaxHz", (double)_numMaxHz.Value); SendConfig(); };
            body.Controls.Add(_numMaxHz);

            body.Controls.Add(MkLabel("FF Gain:", lx + 140, y + 2));
            _numFFGain = Theme.MkNumeric(0, 3, 1, 2, 0.05m, 65);
            _numFFGain.Location = new Point(lx + 200, y - 2);
            _numFFGain.ValueChanged += (s, ev) => { SetVal("FFGain", (double)_numFFGain.Value); SendConfig(); };
            body.Controls.Add(_numFFGain);

            body.Controls.Add(MkLabel("Filtro:", lx + 285, y + 2));
            _numAlpha = Theme.MkNumeric(0, 1, 0.4m, 2, 0.05m, 60);
            _numAlpha.Location = new Point(lx + 330, y - 2);
            _numAlpha.ValueChanged += (s, ev) => { SetVal("Alpha", (double)_numAlpha.Value); SendConfig(); };
            body.Controls.Add(_numAlpha);

            body.Controls.Add(MkLabel("Rampa:", lx + 400, y + 2));
            _numSlewPerSec = Theme.MkNumeric(100, 20000, 5000, 0, 1, 75);
            _numSlewPerSec.Location = new Point(lx + 445, y - 2);
            _numSlewPerSec.ValueChanged += (s, ev) => { SetVal("SlewRatePerSec", (double)_numSlewPerSec.Value); SendConfig(); };
            body.Controls.Add(_numSlewPerSec);
            y += 30;

            // ── Guía ──
            body.Controls.Add(new Label
            {
                Text = "Kp=reacci\u00F3n | Ki=error acumulado | Kd=freno | FF=arranque directo",
                Font = new Font("Segoe UI", 7f), ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent, Location = new Point(lx, y), AutoSize = true
            });
            y += 18;

            // ── Sliders PID ──
            y = AddPidParam(body, "Kp", lx, y, 0, 500, 0, 1, 0.1m,
                v => { SetVal("Kp", v); SendConfig(); });
            y = AddPidParam(body, "Ki", lx, y, 0, 200, 0, 1, 0.1m,
                v => { SetVal("Ki", v); SendConfig(); });
            y = AddPidParam(body, "Kd", lx, y, 0, 100, 0, 1, 0.1m,
                v => { SetVal("Kd", v); SendConfig(); });
            y = AddSlider(body, "PWM min", lx, y, 0, 4095, 0, out _trkMinPwm, out _lblMinPwm,
                v => { SetVal("PwmMin", v); _lblMinPwm.Text = v.ToString(); SendConfig(); });
            y = AddSlider(body, "Deadband %", lx, y, 0, 20, 0, out _trkDeadband, out _lblDeadband,
                v => { SetVal("Deadband", v); _lblDeadband.Text = v.ToString(); SendConfig(); });

            // ── Guardar ──
            y += 4;
            var btnSave = Theme.MkAccentButton("\u2713 GUARDAR", 120, 30);
            btnSave.Location = new Point(lx, y);
            btnSave.Click += (s, ev) => { _motores.Save(); SendConfig(); };
            body.Controls.Add(btnSave);

            // ── Auto-Tune PID ──
            var btnAutoTune = Theme.MkSecondaryButton("\U0001F3AF AUTO-TUNE PID", 170, 30);
            btnAutoTune.Location = new Point(lx + 130, y);
            bool _autoTuning = false;
            btnAutoTune.Click += (s, ev) =>
            {
                _autoTuning = !_autoTuning;
                if (_autoTuning)
                {
                    string nodoUid = GetSelectedNodoUid();
                    int motorId = _cboMotor != null ? _cboMotor.SelectedIndex : 0;
                    if (string.IsNullOrEmpty(nodoUid)) return;
                    SendCommand(nodoUid, "{\"cmd\":\"autotune_start\",\"id\":" + motorId + "}");
                    btnAutoTune.Text = "\u23F9 DETENER TUNE";
                    btnAutoTune.BackColor = Theme.Warning;
                    btnAutoTune.ForeColor = Color.Black;
                }
                else
                {
                    string nodoUid = GetSelectedNodoUid();
                    int motorId = _cboMotor != null ? _cboMotor.SelectedIndex : 0;
                    if (!string.IsNullOrEmpty(nodoUid))
                        SendCommand(nodoUid, "{\"cmd\":\"autotune_stop\",\"id\":" + motorId + "}");
                    btnAutoTune.Text = "\U0001F3AF AUTO-TUNE PID";
                    btnAutoTune.BackColor = Theme.BgCard2;
                    btnAutoTune.ForeColor = Theme.TextPrimary;
                }
            };
            body.Controls.Add(btnAutoTune);

            var lblLive = new Label
            {
                Name = "lblLive", Text = "", Font = Theme.FontSmall,
                ForeColor = Theme.Accent, BackColor = Color.Transparent,
                Location = new Point(lx + 310, y + 6), Size = new Size(260, 16)
            };
            body.Controls.Add(lblLive);
            y += 38;

            // ── RPM display ──
            var rpmPanel = new Panel
            {
                Location = new Point(lx, y), Size = new Size(540, 42),
                BackColor = Theme.BgCard
            };
            rpmPanel.Paint += (s, ev) => { Theme.DrawRoundedBorder(ev.Graphics, new Rectangle(0, 0, rpmPanel.Width - 1, rpmPanel.Height - 1), Theme.Border, Theme.BorderRadius); };
            body.Controls.Add(rpmPanel);

            rpmPanel.Controls.Add(new Label { Name = "lblRpmBig", Text = "0 RPM", Font = new Font(Theme.FontFamily, 18f, FontStyle.Bold), ForeColor = Theme.Accent, BackColor = Color.Transparent, Location = new Point(12, 6), AutoSize = true });
            rpmPanel.Controls.Add(new Label { Text = "Velocidad del motor", Font = new Font("Segoe UI", 8f), ForeColor = Theme.TextFaint, BackColor = Color.Transparent, Location = new Point(200, 14), AutoSize = true });
            rpmPanel.Controls.Add(new Label { Name = "lblPulsos", Text = "", Font = new Font(Theme.FontFamily, 9f), ForeColor = Theme.TextSecondary, BackColor = Color.Transparent, Location = new Point(380, 14), AutoSize = true });
            y += 52;

            // ── Gráfico ──
            _graphPanel = new Panel { Location = new Point(lx, y), Size = new Size(540, 150), BackColor = Theme.BgCard };
            _graphPanel.Paint += PaintGraph;
            body.Controls.Add(_graphPanel);
        }

        // =====================================================================
        // Slider helper
        // =====================================================================

        private int AddPidParam(Control parent, string label, int x, int y,
            decimal min, decimal max, decimal initial, int decimals, decimal increment,
            Action<double> onChange)
        {
            parent.Controls.Add(MkLabel(label + ":", x, y + 4));
            var num = Theme.MkNumeric(min, max, initial, decimals, increment, 100);
            num.Location = new Point(x + 90, y);
            num.ValueChanged += (s, ev) => onChange((double)num.Value);
            parent.Controls.Add(num);

            // Guardar referencia para LoadMotorValues.
            if (label == "Kp") _numKp = num;
            else if (label == "Ki") _numKi = num;
            else if (label == "Kd") _numKd = num;

            return y + 34;
        }

        private int AddSlider(Control parent, string label, int x, int y,
            int min, int max, int initial, out TrackBar trk, out Label lbl, Action<int> onChange)
        {
            parent.Controls.Add(MkLabel(label + ":", x, y + 2));
            lbl = new Label { Text = initial.ToString(), Font = new Font(Theme.FontFamily, 9f, FontStyle.Bold), ForeColor = Theme.Accent, BackColor = Color.Transparent, Location = new Point(x + 80, y + 2), Size = new Size(50, 16) };
            parent.Controls.Add(lbl);
            trk = new TrackBar { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, initial)), TickFrequency = Math.Max(1, (max - min) / 20), LargeChange = Math.Max(1, (max - min) / 10), Location = new Point(x + 130, y), Size = new Size(400, 28), BackColor = Theme.BgInput };
            var cl = lbl; var ct = trk;
            ct.ValueChanged += (s, ev) => onChange(ct.Value);
            parent.Controls.Add(ct);
            return y + 34;
        }

        // =====================================================================
        // Motor values
        // =====================================================================

        private string GetSelectedNodoUid()
        {
            if (_cboNodo == null || _cboNodo.SelectedIndex < 0) return null;
            if (_cboNodo.SelectedIndex >= _motores.Nodos.Count) return null;
            return _motores.Nodos[_cboNodo.SelectedIndex].Uid;
        }

        private async void SendCommand(string uid, string payload)
        {
            if (_mqtt == null || string.IsNullOrEmpty(uid)) return;
            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic("agp/quantix/" + uid + "/cmd")
                    .WithPayload(payload).Build();
                await _mqtt.PublishAsync(msg);
            }
            catch { }
        }

        private QxMotorConfig GetSelectedMotor()
        {
            if (_cboNodo == null || _cboMotor == null) return null;
            if (_cboNodo.SelectedIndex < 0 || _cboNodo.SelectedIndex >= _motores.Nodos.Count) return null;
            var nodo = _motores.Nodos[_cboNodo.SelectedIndex];
            int mi = _cboMotor.SelectedIndex;
            if (mi < 0 || mi >= nodo.Motores.Length) return null;
            return nodo.Motores[mi];
        }

        private void LoadMotorValues()
        {
            var m = GetSelectedMotor();
            if (m == null) return;

            if (_numKp != null) _numKp.Value = ClampDec(_numKp, (decimal)m.Kp);
            if (_numKi != null) _numKi.Value = ClampDec(_numKi, (decimal)m.Ki);
            if (_numKd != null) _numKd.Value = ClampDec(_numKd, (decimal)m.Kd);
            _trkMinPwm.Value = Clamp(_trkMinPwm, m.PwmMin);
            _trkDeadband.Value = Clamp(_trkDeadband, m.Deadband);
            _lblMinPwm.Text = m.PwmMin.ToString();
            _lblDeadband.Text = m.Deadband.ToString();

            _cboMotorType.SelectedIndex = Math.Max(0, Math.Min(1, m.MotorType));
            _numDientes.Value = Math.Max(_numDientes.Minimum, Math.Min(_numDientes.Maximum, m.DientesEngranaje > 0 ? m.DientesEngranaje : 600));
            _numMaxHz.Value = Math.Max(_numMaxHz.Minimum, Math.Min(_numMaxHz.Maximum, (decimal)(m.MaxHz > 0 ? m.MaxHz : 40)));
            _numFFGain.Value = Math.Max(_numFFGain.Minimum, Math.Min(_numFFGain.Maximum, (decimal)(m.FFGain > 0 ? m.FFGain : 1.0)));
            _numAlpha.Value = Math.Max(_numAlpha.Minimum, Math.Min(_numAlpha.Maximum, (decimal)(m.Alpha > 0 ? m.Alpha : 0.4)));
            _numSlewPerSec.Value = Math.Max(_numSlewPerSec.Minimum, Math.Min(_numSlewPerSec.Maximum, (decimal)(m.SlewRatePerSec > 0 ? m.SlewRatePerSec : 5000)));
            _numPIDTime.Value = Math.Max(_numPIDTime.Minimum, Math.Min(_numPIDTime.Maximum, m.PIDTime > 0 ? m.PIDTime : 50));

            _histTarget.Clear(); _histReal.Clear();
        }

        private void SetVal(string field, double val)
        {
            var m = GetSelectedMotor();
            if (m == null) return;
            switch (field)
            {
                case "Kp": m.Kp = val; break;
                case "Ki": m.Ki = val; break;
                case "Kd": m.Kd = val; break;
                case "PwmMin": m.PwmMin = (int)val; break;
                case "Deadband": m.Deadband = (int)val; break;
                case "MaxHz": m.MaxHz = val; break;
                case "FFGain": m.FFGain = val; break;
                case "Alpha": m.Alpha = val; break;
                case "SlewRatePerSec": m.SlewRatePerSec = val; break;
            }
        }

        private static int Clamp(TrackBar trk, int val)
        {
            return Math.Max(trk.Minimum, Math.Min(trk.Maximum, val));
        }

        private static decimal ClampDec(NumericUpDown num, decimal val)
        {
            return Math.Max(num.Minimum, Math.Min(num.Maximum, val));
        }

        private void ApplyPreset(int motorType, int kp, int ki, int kd, int minPwm, int maxHz, int deadband,
            double ffGain, double alpha, int slewPerSec, int pidTime)
        {
            _cboMotorType.SelectedIndex = motorType;
            if (_numKp != null) _numKp.Value = ClampDec(_numKp, kp);
            if (_numKi != null) _numKi.Value = ClampDec(_numKi, ki);
            if (_numKd != null) _numKd.Value = ClampDec(_numKd, kd);
            _trkMinPwm.Value = Clamp(_trkMinPwm, minPwm);
            _trkDeadband.Value = Clamp(_trkDeadband, deadband);
            _numMaxHz.Value = maxHz;
            _numFFGain.Value = (decimal)ffGain;
            _numAlpha.Value = (decimal)alpha;
            _numSlewPerSec.Value = slewPerSec;
            _numPIDTime.Value = pidTime;
        }

        // =====================================================================
        // MQTT
        // =====================================================================

        private void StartMqtt()
        {
            try
            {
                var v = VistaXConfig.Load();
                var c = new VistaXConfig
                {
                    Enabled = true, BrokerAddress = v.BrokerAddress, BrokerPort = v.BrokerPort,
                    ClientId = "QX_PID", TelemetriaTopic = "agp/quantix/+/status_live",
                    SectionsTopic = "agp/quantix/+/autotune_result", SpeedTopic = ""
                };
                _mqtt = new MqttClientWrapper(c);
                _mqtt.MessageReceived += OnStatus;
                _ = _mqtt.ConnectAsync();

                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qx_pid.csv");
                if (!System.IO.File.Exists(logPath))
                    System.IO.File.WriteAllText(logPath, "time,target,real,pwm,rpm,error_pct,kp,ki,kd,minpwm,maxhz,ff_gain,alpha,slew_per_sec,pid_time\n");
                _pidLogPath = logPath;
                _pidLogStart = DateTime.Now;

                _refreshTimer = new Timer { Interval = 100 };
                _refreshTimer.Tick += OnRefreshTick;
                _refreshTimer.Start();
            }
            catch { }
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            _histTarget.Add(_liveTarget);
            _histReal.Add(_liveReal);
            while (_histTarget.Count > MaxHistory) { _histTarget.RemoveAt(0); _histReal.RemoveAt(0); }
            _graphPanel.Invalidate();

            var motor = GetSelectedMotor();
            int ppr = motor != null && motor.DientesEngranaje > 0 ? motor.DientesEngranaje : 24;
            double meterCal = motor != null && motor.MeterCal > 0 ? motor.MeterCal : 14.6;

            // Convertir Hz → RPM
            int rpmTarget = ppr > 0 ? (int)(_liveTarget * 60.0 / ppr) : 0;
            int rpmReal = _liveRpm > 0 ? _liveRpm : (ppr > 0 ? (int)(_liveReal * 60.0 / ppr) : 0);

            // Calcular kg/ha real (inversa del bridge)
            // g/s = Hz × MeterCal(g/pulso) → kg/ha = g/s × 10 / (ancho × vel_m/s)
            // Usamos los datos del último log si están disponibles
            double kgHaReal = 0;
            double kgHaTarget = 0;
            // Estimación: si conocemos ancho y velocidad desde el bridge
            // Por ahora calculamos desde los Hz y MeterCal
            // g/s_real = _liveReal * meterCal
            double gsPorSegReal = _liveReal * meterCal;
            double gsPorSegTarget = _liveTarget * meterCal;

            var lbl = Controls.Find("lblLive", true);
            if (lbl.Length > 0)
                lbl[0].Text = string.Format("OBJ:{0} RPM  REAL:{1} RPM  PWM:{2}  ERR:{3:F1}%  |  {4:F1} g/pulso",
                    rpmTarget, rpmReal, _livePwm,
                    _liveTarget > 0 ? ((_liveReal - _liveTarget) / _liveTarget * 100) : 0,
                    meterCal);

            var lblRpm = Controls.Find("lblRpmBig", true);
            if (lblRpm.Length > 0) lblRpm[0].Text = rpmReal.ToString() + " RPM";

            var lblPulsos = Controls.Find("lblPulsos", true);
            if (lblPulsos.Length > 0)
                lblPulsos[0].Text = string.Format("{0:F0} g/s · {1:N0} pulsos", gsPorSegReal, _livePulsos);

            // CSV log cada 200ms
            if (_pidLogPath != null && _histTarget.Count % 2 == 0)
            {
                try
                {
                    double elapsed = (DateTime.Now - _pidLogStart).TotalSeconds;
                    double errPct = _liveTarget > 0 ? ((_liveReal - _liveTarget) / _liveTarget * 100) : 0;
                    var lm = GetSelectedMotor();
                    if (lm != null)
                    {
                        System.IO.File.AppendAllText(_pidLogPath, string.Format(CultureInfo.InvariantCulture,
                            "{0:F1},{1:F2},{2:F2},{3},{4},{5:F1},{6},{7},{8},{9},{10:F1},{11:F2},{12:F2},{13:F0},{14}\n",
                            elapsed, _liveTarget, _liveReal, _livePwm, _liveRpm, errPct,
                            (int)lm.Kp, (int)lm.Ki, (int)lm.Kd, lm.PwmMin,
                            lm.MaxHz, lm.FFGain, lm.Alpha, lm.SlewRatePerSec, lm.PIDTime));
                    }
                }
                catch { }
            }
        }

        private void OnStatus(string topic, string payload)
        {
            // Auto-tune result.
            if (topic.Contains("autotune_result"))
            {
                try { OnAutoTuneResult(payload); } catch { }
                return;
            }

            if (!topic.Contains("status_live")) return;
            try
            {
                int id = (int)ExtractNum(payload, "\"id\":");
                if (id != _cboMotor.SelectedIndex) return;
                _liveTarget = ExtractNum(payload, "\"pps_target\":");
                _liveReal = ExtractNum(payload, "\"pps_real\":");
                _livePwm = (int)ExtractNum(payload, "\"pwm\":");
                _liveRpm = (int)ExtractNum(payload, "\"rpm\":");
                _livePulsos = (long)ExtractNum(payload, "\"pulsos\":");
            }
            catch { }
        }

        private void OnAutoTuneResult(string payload)
        {
            bool ok = payload.Contains("\"ok\":true");
            double kp = ExtractNum(payload, "\"kp\":");
            double ki = ExtractNum(payload, "\"ki\":");
            double kd = ExtractNum(payload, "\"kd\":");

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnAutoTuneResult(payload)));
                return;
            }

            if (ok)
            {
                string msg = string.Format("Auto-Tune completado:\n\nKp = {0:F1}\nKi = {1:F1}\nKd = {2:F1}\n\n\u00BFAplicar estos valores?",
                    kp, ki, kd);
                if (MessageBox.Show(this, msg, "Auto-Tune PID", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    var m = GetSelectedMotor();
                    if (m != null)
                    {
                        m.Kp = kp;
                        m.Ki = ki;
                        m.Kd = kd;
                        _motores.Save();
                        LoadMotorValues();
                        SendConfig();
                    }
                }
            }
            else
            {
                MessageBox.Show(this, "Auto-Tune fall\u00F3: no se detectaron oscilaciones suficientes.\n\nVerific\u00E1 que el motor est\u00E9 conectado y el sensor funcione.",
                    "Auto-Tune PID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async void SendConfig()
        {
            if (_mqtt == null || _cboNodo == null || _cboNodo.SelectedIndex < 0) return;
            var nodo = _motores.Nodos[_cboNodo.SelectedIndex];
            if (string.IsNullOrEmpty(nodo.Uid)) return;
            var m = GetSelectedMotor();
            if (m == null) return;

            int mi = _cboMotor.SelectedIndex;
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"configs\":[{\"idx\":").Append(mi);
            sb.Append(",\"config_pid\":{\"kp\":").Append(m.Kp.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"ki\":").Append(m.Ki.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"kd\":").Append(m.Kd.ToString(CultureInfo.InvariantCulture)).Append("}");
            sb.Append(",\"calibracion\":{\"pwm_min\":").Append(m.PwmMin).Append(",\"pwm_max\":").Append(m.PwmMax).Append("}");
            sb.Append(",\"meter_cal\":").Append(m.MeterCal.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"motor_type\":").Append(m.MotorType);
            sb.Append(",\"max_hz\":").Append(m.MaxHz.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"ff_gain\":").Append(m.FFGain.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"alpha\":").Append(m.Alpha.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"slew_rate_per_sec\":").Append(m.SlewRatePerSec.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"pid_time\":").Append(m.PIDTime);
            sb.Append(",\"deadband\":").Append(m.Deadband);
            sb.Append(",\"max_integral\":").Append(m.MaxIntegral.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"pulses_per_rev\":").Append(m.DientesEngranaje);
            sb.Append("}]}");

            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic("agp/quantix/" + nodo.Uid + "/config")
                    .WithPayload(sb.ToString()).WithRetainFlag(true).Build();
                await _mqtt.PublishAsync(msg);
            }
            catch { }
        }

        // =====================================================================
        // Gráfico
        // =====================================================================

        private void PaintGraph(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.BgCard);
            int w = _graphPanel.Width, h = _graphPanel.Height, pad = 4;
            Theme.DrawRoundedBorder(g, new Rectangle(0, 0, w - 1, h - 1), Theme.Border, Theme.BorderRadius);
            if (_histTarget.Count < 2) return;

            double maxVal = 1;
            foreach (var v in _histTarget) if (v > maxVal) maxVal = v;
            foreach (var v in _histReal) if (v > maxVal) maxVal = v;
            maxVal *= 1.2;
            int n = _histTarget.Count;
            float dx = (float)(w - pad * 2) / Math.Max(1, MaxHistory - 1);

            using (var pen = new Pen(Color.FromArgb(100, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B), 1f))
            {
                pen.DashStyle = DashStyle.Dash;
                for (int i = 1; i < n; i++)
                {
                    float x0 = pad + (MaxHistory - n + i - 1) * dx, x1 = pad + (MaxHistory - n + i) * dx;
                    float y0 = h - pad - (float)(_histTarget[i - 1] / maxVal * (h - pad * 2));
                    float y1 = h - pad - (float)(_histTarget[i] / maxVal * (h - pad * 2));
                    g.DrawLine(pen, x0, y0, x1, y1);
                }
            }

            Color realColor = Theme.Accent;
            if (_liveTarget > 0)
            {
                double err = Math.Abs(_liveReal - _liveTarget) / _liveTarget;
                if (err > 0.2) realColor = Color.FromArgb(220, 40, 0);
                else if (err > 0.1) realColor = Color.FromArgb(255, 220, 0);
            }
            using (var pen = new Pen(realColor, 2f))
            {
                for (int i = 1; i < n; i++)
                {
                    float x0 = pad + (MaxHistory - n + i - 1) * dx, x1 = pad + (MaxHistory - n + i) * dx;
                    float y0 = h - pad - (float)(_histReal[i - 1] / maxVal * (h - pad * 2));
                    float y1 = h - pad - (float)(_histReal[i] / maxVal * (h - pad * 2));
                    g.DrawLine(pen, x0, y0, x1, y1);
                }
            }

            using (var f = new Font("Segoe UI", 7f))
            {
                using (var b = new SolidBrush(Color.FromArgb(100, Theme.Accent))) g.DrawString("--- Objetivo", f, b, 4, 2);
                using (var b = new SolidBrush(realColor)) g.DrawString("\u2501 Real", f, b, 80, 2);
                using (var b = new SolidBrush(Theme.TextFaint)) g.DrawString(maxVal.ToString("F1"), f, b, w - 40, 2);
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private Label MkLabel(string text, int x, int y)
        {
            return new Label { Text = text, Font = Theme.FontSmall, ForeColor = Theme.TextSecondary, BackColor = Color.Transparent, Location = new Point(x, y), AutoSize = true };
        }

        private static double ExtractNum(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += key.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
            if (end == idx) return 0;
            double val;
            double.TryParse(json.Substring(idx, end - idx), NumberStyles.Any, CultureInfo.InvariantCulture, out val);
            return val;
        }
    }
}
