// ============================================================================
// FormQuantiXCalibrar.cs - Calibración de motor (mockup Agro Parallel)
// Ubicación: SourceCode/GPS/AgroParallel/QuantiX/FormQuantiXCalibrar.cs
// Target: net48 (C# 7.3)
//
// Flujo:
// 1. Seleccionar nodo + motor, configurar vueltas, PWM, pulsos/vuelta, surcos
// 2. INICIAR → envía comando cal al ESP32 por MQTT
// 3. ESP32 gira el motor N vueltas a PWM fijo, cuenta pulsos
// 4. El usuario pesa/cuenta gramos o semillas por surco
// 5. CALCULAR → promedia surcos → calcula gramos/pulso (MeterCal)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using AgroParallel.Common;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXCalibrar : Form
    {
        private readonly QuantiXConfig _cfg;
        private MotoresConfig _motores;

        // Controles.
        private ComboBox _cboNodo;
        private ComboBox _cboMotor;
        private NumericUpDown _numVueltas;
        private NumericUpDown _numPWM;
        private NumericUpDown _numPulsosPorVuelta;
        private NumericUpDown _numSurcos;
        private Label _lblPulsos;
        private Label _lblVueltas;
        private Label _lblPWMActual;
        private Label _lblLog;
        private Label _lblResultado;
        private Panel _surcoPanel;
        private NumericUpDown _numSurcosTotales;
        private List<NumericUpDown> _surcoInputs = new List<NumericUpDown>();

        // Estado.
        private MqttClientWrapper _mqtt;
        private bool _calibrando;

        // Flat list.
        private class NodoEntry { public QxNodoConfig Nodo; public int Idx; }
        private readonly List<NodoEntry> _nodoEntries = new List<NodoEntry>();

        public FormQuantiXCalibrar(QuantiXConfig cfg)
        {
            _cfg = cfg ?? QuantiXConfig.Load();
            _motores = MotoresConfig.Load();
            BuildUI();
        }

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            var body = new Panel
            {
                Dock = DockStyle.Fill, AutoScroll = true,
                BackColor = Theme.BgBlack
            };
            Controls.Add(body);

            int lx = 24, y = 18;
            int cardW = 580;

            // ════════════════════════════════════════════════════════════
            // PARÁMETROS
            // ════════════════════════════════════════════════════════════
            var cardParams = MkCard(body, lx, y, cardW, 310, "PAR\u00C1METROS");
            int cy = 32;

            // Nodo selector.
            AddLabel(cardParams, "NODO", 18, cy);
            _cboNodo = Theme.MkCombo(260);
            _cboNodo.Location = new Point(18, cy + 18);
            _nodoEntries.Clear();
            foreach (var n in _motores.Nodos)
            {
                _nodoEntries.Add(new NodoEntry { Nodo = n, Idx = _nodoEntries.Count });
                _cboNodo.Items.Add(n.Nombre + " (" + n.Uid + ")");
            }
            if (_cboNodo.Items.Count == 0) _cboNodo.Items.Add("(sin nodos)");
            _cboNodo.SelectedIndex = 0;
            cardParams.Controls.Add(_cboNodo);

            // Motor selector (M0 / M1).
            AddLabel(cardParams, "MOTOR", 300, cy);
            _cboMotor = Theme.MkCombo(240);
            _cboMotor.Location = new Point(300, cy + 18);
            _cboMotor.Items.AddRange(new object[] { "M0 \u2014 Producto 1", "M1 \u2014 Producto 2" });
            _cboMotor.SelectedIndex = 0;
            _cboMotor.SelectedIndexChanged += (s, ev) => LoadMotorPPR();
            _cboNodo.SelectedIndexChanged += (s, ev) => LoadMotorPPR();
            cardParams.Controls.Add(_cboMotor);
            cy += 58;

            // Vueltas + Pulsos por vuelta.
            AddLabel(cardParams, "VUELTAS A GIRAR", 18, cy);
            _numVueltas = Theme.MkNumeric(1, 100, 10, 0, 1, 200);
            _numVueltas.Location = new Point(18, cy + 18);
            cardParams.Controls.Add(_numVueltas);

            AddLabel(cardParams, "PULSOS POR VUELTA", 300, cy);
            _numPulsosPorVuelta = Theme.MkNumeric(1, 2000, 600, 0, 1, 200);
            _numPulsosPorVuelta.Location = new Point(300, cy + 18);
            cardParams.Controls.Add(_numPulsosPorVuelta);
            cy += 58;

            // PWM + Surcos.
            AddLabel(cardParams, "PWM (0\u20134095)", 18, cy);
            _numPWM = Theme.MkNumeric(0, 4095, 2000, 0, 1, 200);
            _numPWM.Location = new Point(18, cy + 18);
            cardParams.Controls.Add(_numPWM);

            AddLabel(cardParams, "SURCOS A MEDIR", 300, cy);
            _numSurcos = Theme.MkNumeric(1, 20, 6, 0, 1, 200);
            _numSurcos.Location = new Point(300, cy + 18);
            _numSurcos.ValueChanged += (s, ev) => RebuildSurcoInputs();
            cardParams.Controls.Add(_numSurcos);
            cy += 58;

            // Total surcos de la máquina (para calcular producción total).
            AddLabel(cardParams, "SURCOS TOTALES M\u00C1QUINA", 18, cy);
            _numSurcosTotales = Theme.MkNumeric(1, 200, 96, 0, 1, 200);
            _numSurcosTotales.Location = new Point(18, cy + 18);
            cardParams.Controls.Add(_numSurcosTotales);
            cy += 58;

            // Botones INICIAR + RESET.
            var btnIniciar = Theme.MkAccentButton("\u25B6  INICIAR", 140, 36);
            btnIniciar.Location = new Point(18, cy);
            btnIniciar.Click += (s, ev) => IniciarCalibracion();
            cardParams.Controls.Add(btnIniciar);

            var btnReset = Theme.MkSecondaryButton("\u21BB  RESET", 110, 36);
            btnReset.Location = new Point(170, cy);
            btnReset.Click += (s, ev) => ResetCalibracion();
            cardParams.Controls.Add(btnReset);

            y += 330;

            // ════════════════════════════════════════════════════════════
            // ESTADO EN VIVO
            // ════════════════════════════════════════════════════════════
            var cardEstado = MkCard(body, lx, y, cardW, 110, "ESTADO EN VIVO");

            _lblPulsos = MkKpiLabel(cardEstado, 18, 36, "0", "PULSOS");
            _lblVueltas = MkKpiLabel(cardEstado, 200, 36, "0.0", "VUELTAS");
            _lblPWMActual = MkKpiLabel(cardEstado, 400, 36, "0", "PWM ACTUAL");

            y += 130;

            // ════════════════════════════════════════════════════════════
            // TEST PWM MÍNIMO
            // ════════════════════════════════════════════════════════════
            var cardPwmTest = MkCard(body, lx, y, cardW, 126, "TEST PWM M\u00CDNIMO \u2014 Encontrar el punto donde el motor empieza a girar");
            int pty = 30;

            var lblPwmSlider = new Label
            {
                Text = "PWM: 0",
                Font = new Font(Theme.FontFamily, 14f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Color.Transparent,
                Location = new Point(18, pty), AutoSize = true
            };
            cardPwmTest.Controls.Add(lblPwmSlider);

            var lblRpmTest = new Label
            {
                Text = "RPM: --",
                Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary, BackColor = Color.Transparent,
                Location = new Point(200, pty + 4), AutoSize = true
            };
            cardPwmTest.Controls.Add(lblRpmTest);
            pty += 32;

            var sliderPwm = new TrackBar
            {
                Minimum = 0, Maximum = 4095, Value = 0,
                TickFrequency = 200, LargeChange = 100, SmallChange = 10,
                Location = new Point(18, pty), Size = new Size(cardW - 100, 30),
                BackColor = Theme.BgInput
            };
            cardPwmTest.Controls.Add(sliderPwm);

            var btnPwmStop = Theme.MkDangerButton("\u25A0 STOP", 60, 28);
            btnPwmStop.Location = new Point(cardW - 80, pty);
            cardPwmTest.Controls.Add(btnPwmStop);

            var btnApplyMinPwm = Theme.MkAccentButton("\u2713 USAR COMO MIN PWM", 200, 28);
            btnApplyMinPwm.Location = new Point(18, pty + 38);
            cardPwmTest.Controls.Add(btnApplyMinPwm);

            // Timer para enviar PWM en vivo y leer RPM.
            Timer pwmTestTimer = null;

            sliderPwm.ValueChanged += (s, ev) =>
            {
                int pwmVal = sliderPwm.Value;
                lblPwmSlider.Text = "PWM: " + pwmVal;

                // Enviar test al motor.
                if (_nodoEntries.Count > 0 && _cboNodo.SelectedIndex >= 0
                    && _cboNodo.SelectedIndex < _nodoEntries.Count && _mqtt != null)
                {
                    var nodo = _nodoEntries[_cboNodo.SelectedIndex].Nodo;
                    int motorId = _cboMotor.SelectedIndex;
                    string topic = "agp/quantix/" + nodo.Uid + "/test";
                    string payload = pwmVal > 0
                        ? "{\"cmd\":\"start\",\"id\":" + motorId + ",\"pwm\":" + pwmVal + "}"
                        : "{\"cmd\":\"stop\",\"id\":" + motorId + "}";
                    try
                    {
                        var msg = new MqttApplicationMessageBuilder()
                            .WithTopic(topic).WithPayload(payload).Build();
                        _ = _mqtt.PublishAsync(msg);
                    }
                    catch { }
                }

                // Iniciar timer de lectura de RPM si no está corriendo.
                if (pwmTestTimer == null && pwmVal > 0)
                {
                    pwmTestTimer = new Timer { Interval = 300 };
                    pwmTestTimer.Tick += (s2, ev2) =>
                    {
                        // Leer RPM del status MQTT (ya viene por OnStatusMessage).
                        lblRpmTest.Text = "RPM: " + _lblPulsos.Text + " pulsos";
                    };
                    pwmTestTimer.Start();
                }
            };

            btnPwmStop.Click += (s, ev) =>
            {
                sliderPwm.Value = 0;
                if (pwmTestTimer != null) { pwmTestTimer.Stop(); pwmTestTimer.Dispose(); pwmTestTimer = null; }

                // Enviar stop.
                if (_nodoEntries.Count > 0 && _cboNodo.SelectedIndex >= 0
                    && _cboNodo.SelectedIndex < _nodoEntries.Count && _mqtt != null)
                {
                    var nodo = _nodoEntries[_cboNodo.SelectedIndex].Nodo;
                    string topic = "agp/quantix/" + nodo.Uid + "/test";
                    string payload = "{\"cmd\":\"stop\",\"id\":" + _cboMotor.SelectedIndex + "}";
                    try
                    {
                        var msg = new MqttApplicationMessageBuilder()
                            .WithTopic(topic).WithPayload(payload).Build();
                        _ = _mqtt.PublishAsync(msg);
                    }
                    catch { }
                }
                lblRpmTest.Text = "RPM: --";
            };

            btnApplyMinPwm.Click += (s, ev) =>
            {
                int minPwm = sliderPwm.Value;
                if (minPwm <= 0) { MessageBox.Show(this, "Sub\u00ED el slider hasta que el motor empiece a girar.", "Min PWM", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

                if (_cboNodo.SelectedIndex >= 0 && _cboNodo.SelectedIndex < _nodoEntries.Count)
                {
                    var nodo = _nodoEntries[_cboNodo.SelectedIndex].Nodo;
                    int mi = _cboMotor.SelectedIndex;
                    if (mi >= 0 && mi < nodo.Motores.Length)
                    {
                        nodo.Motores[mi].PwmMin = minPwm;
                        _motores.Save();

                        // Parar test.
                        sliderPwm.Value = 0;

                        MessageBox.Show(this,
                            "MinPWM = " + minPwm + " aplicado a M" + mi + "\n\n"
                            + "Este es el punto m\u00EDnimo donde el motor arranca.\n"
                            + "El PID no va a bajar de este valor.",
                            "Min PWM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            };

            y += 146;

            // ════════════════════════════════════════════════════════════
            // LOG
            // ════════════════════════════════════════════════════════════
            var cardLog = MkCard(body, lx, y, cardW, 54, "LOG");
            _lblLog = new Label
            {
                Text = "Esperando inicio de calibraci\u00F3n...",
                Font = Theme.FontMono, ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                Location = new Point(18, 30), Size = new Size(cardW - 36, 18)
            };
            cardLog.Controls.Add(_lblLog);
            y += 74;

            // ════════════════════════════════════════════════════════════
            // RESULTADO POR SURCO
            // ════════════════════════════════════════════════════════════
            _surcoPanel = MkCard(body, lx, y, cardW, 300, "RESULTADO \u2014 Ingrese gramos o granos recolectados de cada surco");
            RebuildSurcoInputs();

            // Spacer.
            body.Controls.Add(new Panel
            {
                Location = new Point(0, y + 320),
                Size = new Size(1, 20), BackColor = Color.Transparent
            });
        }

        // =====================================================================
        // Surco inputs dinámicos
        // =====================================================================

        private void LoadMotorPPR()
        {
            if (_cboNodo.SelectedIndex < 0 || _cboNodo.SelectedIndex >= _nodoEntries.Count) return;
            var nodo = _nodoEntries[_cboNodo.SelectedIndex].Nodo;
            int mi = _cboMotor.SelectedIndex;
            if (mi < 0 || mi >= nodo.Motores.Length) return;
            int ppr = nodo.Motores[mi].DientesEngranaje;
            if (ppr > 0)
                _numPulsosPorVuelta.Value = Math.Max(_numPulsosPorVuelta.Minimum,
                    Math.Min(_numPulsosPorVuelta.Maximum, ppr));
        }

        private void RebuildSurcoInputs()
        {
            // Limpiar inputs anteriores (mantener el título del card).
            var toRemove = new List<Control>();
            foreach (Control c in _surcoPanel.Controls)
            {
                if (c.Tag != null && c.Tag.ToString() == "surco")
                    toRemove.Add(c);
            }
            foreach (var c in toRemove) _surcoPanel.Controls.Remove(c);
            _surcoInputs.Clear();

            int count = (int)_numSurcos.Value;
            int sy = 32;

            for (int i = 0; i < count; i++)
            {
                var lbl = new Label
                {
                    Text = "Surco " + (i + 1),
                    Font = new Font(Theme.FontFamily, 10f, FontStyle.Bold),
                    ForeColor = Theme.Accent, BackColor = Color.Transparent,
                    Location = new Point(18, sy + 4), AutoSize = true,
                    Tag = "surco"
                };
                _surcoPanel.Controls.Add(lbl);

                var num = Theme.MkNumeric(0, 100000, 0, 2, 1, 300);
                num.Font = new Font(Theme.FontFamily, 14f, FontStyle.Bold);
                num.Location = new Point(100, sy);
                num.Height = 30;
                num.TextAlign = HorizontalAlignment.Center;
                num.Tag = "surco";
                _surcoPanel.Controls.Add(num);
                _surcoInputs.Add(num);

                var lblUnit = new Label
                {
                    Text = "gramos o semillas",
                    Font = Theme.FontSmall, ForeColor = Theme.TextFaint,
                    BackColor = Color.Transparent,
                    Location = new Point(410, sy + 8), AutoSize = true,
                    Tag = "surco"
                };
                _surcoPanel.Controls.Add(lblUnit);

                sy += 44;
            }

            // Botón CALCULAR.
            var btnCalc = Theme.MkAccentButton("\u2713  CALCULAR", 160, 36);
            btnCalc.Location = new Point(18, sy + 6);
            btnCalc.Tag = "surco";
            btnCalc.Click += (s, ev) => CalcularResultado();
            _surcoPanel.Controls.Add(btnCalc);

            // Resultado.
            if (_lblResultado != null)
                _surcoPanel.Controls.Remove(_lblResultado);
            _lblResultado = new Label
            {
                Text = "", Font = new Font(Theme.FontFamily, 13f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Color.Transparent,
                Location = new Point(200, sy + 12), AutoSize = true,
                Tag = "surco"
            };
            _surcoPanel.Controls.Add(_lblResultado);

            _surcoPanel.Size = new Size(_surcoPanel.Width, sy + 54);
        }

        // =====================================================================
        // Calibración MQTT
        // =====================================================================

        private async void IniciarCalibracion()
        {
            if (_nodoEntries.Count == 0 || _cboNodo.SelectedIndex < 0) return;
            if (_cboNodo.SelectedIndex >= _nodoEntries.Count) return;

            var nodo = _nodoEntries[_cboNodo.SelectedIndex].Nodo;
            int motorId = _cboMotor.SelectedIndex;
            int vueltas = (int)_numVueltas.Value;
            int pulsosPorVuelta = (int)_numPulsosPorVuelta.Value;
            int pwm = (int)_numPWM.Value;
            long pulsosMeta = vueltas * pulsosPorVuelta;

            if (string.IsNullOrEmpty(nodo.Uid))
            {
                _lblLog.Text = "ERROR: nodo sin UID";
                return;
            }

            _lblLog.Text = "Enviando calibraci\u00F3n a " + nodo.Uid + " M" + motorId + "...";
            _lblLog.ForeColor = Theme.Warning;

            try
            {
                // Conectar MQTT si no está.
                if (_mqtt == null)
                {
                    var vistaXCfg = VistaXConfig.Load();
                    var mqttCfg = new VistaXConfig
                    {
                        Enabled = true,
                        BrokerAddress = vistaXCfg.BrokerAddress,
                        BrokerPort = vistaXCfg.BrokerPort,
                        ClientId = "QX_Cal",
                        TelemetriaTopic = "agp/quantix/" + nodo.Uid + "/status_live",
                        SectionsTopic = "",
                        SpeedTopic = ""
                    };
                    _mqtt = new MqttClientWrapper(mqttCfg);
                    _mqtt.MessageReceived += OnStatusMessage;
                    await _mqtt.ConnectAsync();
                }

                // Enviar comando cal: {"cmd":"start","id":0,"pulsos":200,"pwm":2000}
                string topic = "agp/quantix/" + nodo.Uid + "/cal";
                string payload = "{\"cmd\":\"start\""
                    + ",\"id\":" + motorId
                    + ",\"pulsos\":" + pulsosMeta
                    + ",\"pwm\":" + pwm + "}";

                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                await _mqtt.PublishAsync(msg);

                _calibrando = true;
                _lblLog.Text = "Calibrando... meta: " + pulsosMeta + " pulsos (" + vueltas + " vueltas)";
                _lblLog.ForeColor = Theme.Accent;
            }
            catch (Exception ex)
            {
                _lblLog.Text = "ERROR: " + ex.Message;
                _lblLog.ForeColor = Theme.Error;
            }
        }

        private void OnStatusMessage(string topic, string payload)
        {
            if (!_calibrando) return;
            if (!topic.Contains("status_live")) return;

            try
            {
                // {"uid":"...","id":0,"pulsos":150,"pwm":2000,"calibrando":true,...}
                int pulsos = (int)ExtractDouble(payload, "\"pulsos\":");
                int pwm = (int)ExtractDouble(payload, "\"pwm\":");
                bool cal = payload.Contains("\"calibrando\":true");
                int pulsosPorVuelta = (int)_numPulsosPorVuelta.Value;
                double vueltas = pulsosPorVuelta > 0 ? (double)pulsos / pulsosPorVuelta : 0;

                BeginInvoke(new Action(() =>
                {
                    _lblPulsos.Text = pulsos.ToString();
                    _lblVueltas.Text = vueltas.ToString("F1", CultureInfo.InvariantCulture);
                    _lblPWMActual.Text = pwm.ToString() + " / 4095";

                    if (!cal && pulsos > 0)
                    {
                        _calibrando = false;
                        _lblLog.Text = "\u2705 Calibraci\u00F3n completada: " + pulsos + " pulsos";
                        _lblLog.ForeColor = Theme.Accent;
                    }
                }));
            }
            catch { }
        }

        private void ResetCalibracion()
        {
            _calibrando = false;
            _lblPulsos.Text = "0";
            _lblVueltas.Text = "0.0";
            _lblPWMActual.Text = "0";
            _lblLog.Text = "Esperando inicio de calibraci\u00F3n...";
            _lblLog.ForeColor = Theme.TextSecondary;
            foreach (var num in _surcoInputs) num.Value = 0;
            if (_lblResultado != null) _lblResultado.Text = "";

            // Enviar stop al motor.
            if (_mqtt != null && _nodoEntries.Count > 0 && _cboNodo.SelectedIndex >= 0
                && _cboNodo.SelectedIndex < _nodoEntries.Count)
            {
                var nodo = _nodoEntries[_cboNodo.SelectedIndex].Nodo;
                string topic = "agp/quantix/" + nodo.Uid + "/cal";
                string payload = "{\"cmd\":\"stop\",\"id\":" + _cboMotor.SelectedIndex + "}";
                try
                {
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(topic).WithPayload(payload).Build();
                    _ = _mqtt.PublishAsync(msg);
                }
                catch { }
            }
        }

        // =====================================================================
        // Cálculo de resultado
        // =====================================================================

        private void CalcularResultado()
        {
            double suma = 0;
            int count = 0;
            foreach (var num in _surcoInputs)
            {
                double val = (double)num.Value;
                if (val > 0) { suma += val; count++; }
            }

            if (count == 0)
            {
                _lblResultado.Text = "Ingres\u00E1 al menos un valor";
                _lblResultado.ForeColor = Theme.Warning;
                return;
            }

            double promedio = suma / count;
            int pulsosTotales = 0;
            int.TryParse(_lblPulsos.Text, out pulsosTotales);

            if (pulsosTotales <= 0)
            {
                _lblResultado.Text = "No hay pulsos registrados. Inici\u00E1 la calibraci\u00F3n primero.";
                _lblResultado.ForeColor = Theme.Warning;
                return;
            }

            int ppr = (int)_numPulsosPorVuelta.Value;
            double vueltasReales = ppr > 0 ? (double)pulsosTotales / ppr : 0;
            int totalSurcos = (int)_numSurcosTotales.Value;

            // El usuario pesó POR SURCO. El motor mueve TODOS los surcos a la vez.
            // Producción total = promedio_por_surco × cantidad_total_surcos
            double produccionTotalGramos = promedio * totalSurcos;

            // MeterCal = gramos por pulso
            // El bridge usa: pps = productoGramosPorSegundo / MeterCal
            double meterCal = produccionTotalGramos / pulsosTotales;

            string resumen =
                "Pulsos totales: " + pulsosTotales + "\n"
                + "Vueltas reales: " + vueltasReales.ToString("F2", CultureInfo.InvariantCulture)
                + " (" + ppr + " PPR)\n\n"
                + "Surcos medidos: " + count + " de " + _surcoInputs.Count + "\n"
                + "Promedio por surco: " + promedio.ToString("F1", CultureInfo.InvariantCulture) + " g\n"
                + "Total surcos m\u00E1quina: " + totalSurcos + "\n"
                + "Producci\u00F3n total: " + produccionTotalGramos.ToString("F0", CultureInfo.InvariantCulture) + " g\n\n"
                + "MeterCal = " + meterCal.ToString("F2", CultureInfo.InvariantCulture) + " g/pulso\n\n"
                + "Tip: Tom\u00E1 la mayor cantidad de surcos posible\n"
                + "y promedi\u00E1 para mayor precisi\u00F3n.\n\n"
                + "\u00BFAplicar al motor seleccionado?";

            _lblResultado.Text = "MeterCal = " + meterCal.ToString("F2", CultureInfo.InvariantCulture) + " g/pulso";
            _lblResultado.ForeColor = Theme.Accent;

            var r = MessageBox.Show(this, resumen, "Resultado de Calibraci\u00F3n",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (r == DialogResult.Yes && _cboNodo.SelectedIndex >= 0
                && _cboNodo.SelectedIndex < _nodoEntries.Count)
            {
                var nodo = _nodoEntries[_cboNodo.SelectedIndex].Nodo;
                int mi = _cboMotor.SelectedIndex;
                if (mi >= 0 && mi < nodo.Motores.Length)
                {
                    nodo.Motores[mi].MeterCal = Math.Round(meterCal, 4);
                    nodo.Motores[mi].DientesEngranaje = ppr;
                    _motores.Save();
                    _lblLog.Text = "\u2705 MeterCal = " + meterCal.ToString("F4", CultureInfo.InvariantCulture)
                        + " | PPR = " + ppr + " guardados";
                }
            }
        }

        // =====================================================================
        // Helpers UI
        // =====================================================================

        private Panel MkCard(Control parent, int x, int y, int w, int h, string title)
        {
            var card = new Panel
            {
                Location = new Point(x, y), Size = new Size(w, h),
                BackColor = Theme.BgCard
            };
            card.Paint += (s, ev) =>
            {
                Theme.FillRoundedRect(ev.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1),
                    Theme.BgCard, Theme.BorderRadius);
                Theme.DrawRoundedBorder(ev.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1),
                    Theme.Border, Theme.BorderRadius);
            };
            card.Controls.Add(new Label
            {
                Text = "\u25CF  " + title,
                Font = new Font(Theme.FontFamily, 9f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Color.Transparent,
                Location = new Point(14, 8), AutoSize = true
            });
            parent.Controls.Add(card);
            return card;
        }

        private void AddLabel(Control parent, string text, int x, int y)
        {
            parent.Controls.Add(new Label
            {
                Text = text, Font = Theme.FontSmall,
                ForeColor = Theme.TextSecondary, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            });
        }

        private Label MkKpiLabel(Control parent, int x, int y, string val, string label)
        {
            var cardKpi = new Panel
            {
                Location = new Point(x, y), Size = new Size(160, 56),
                BackColor = Color.Transparent
            };
            cardKpi.Paint += (s, ev) =>
            {
                Theme.FillRoundedRect(ev.Graphics, new Rectangle(0, 0, 159, 55),
                    Theme.BgInput, 6);
                Theme.DrawRoundedBorder(ev.Graphics, new Rectangle(0, 0, 159, 55),
                    Theme.Border, 6);
            };

            var lblVal = new Label
            {
                Text = val, Font = new Font(Theme.FontFamily, 20f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary, BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill
            };
            cardKpi.Controls.Add(lblVal);

            parent.Controls.Add(cardKpi);
            parent.Controls.Add(new Label
            {
                Text = label, Font = Theme.FontSmall,
                ForeColor = Theme.TextFaint, BackColor = Color.Transparent,
                Location = new Point(x + 40, y - 14), AutoSize = true
            });
            return lblVal;
        }

        private static double ExtractDouble(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += key.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-'))
                end++;
            if (end == idx) return 0;
            double val;
            double.TryParse(json.Substring(idx, end - idx),
                NumberStyles.Any, CultureInfo.InvariantCulture, out val);
            return val;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            _calibrando = false;

            // SIEMPRE enviar stop a ambos motores al cerrar para no dejar
            // el motor trabado en modo manual.
            StopAllMotors();

            if (_mqtt != null)
            {
                var m = _mqtt; _mqtt = null;
                System.Threading.Tasks.Task.Run(() => { try { m.Dispose(); } catch { } });
            }
        }

        private void StopAllMotors()
        {
            if (_mqtt == null) return;
            if (_nodoEntries.Count == 0 || _cboNodo.SelectedIndex < 0
                || _cboNodo.SelectedIndex >= _nodoEntries.Count) return;

            var nodo = _nodoEntries[_cboNodo.SelectedIndex].Nodo;
            if (string.IsNullOrEmpty(nodo.Uid)) return;

            // Stop test mode (ambos motores).
            for (int mi = 0; mi < 2; mi++)
            {
                string topicTest = "agp/quantix/" + nodo.Uid + "/test";
                string payloadTest = "{\"cmd\":\"stop\",\"id\":" + mi + "}";
                try
                {
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(topicTest).WithPayload(payloadTest).Build();
                    _mqtt.PublishAsync(msg).Wait(500);
                }
                catch { }
            }

            // Stop calibración (ambos motores).
            for (int mi = 0; mi < 2; mi++)
            {
                string topicCal = "agp/quantix/" + nodo.Uid + "/cal";
                string payloadCal = "{\"cmd\":\"stop\",\"id\":" + mi + "}";
                try
                {
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(topicCal).WithPayload(payloadCal).Build();
                    _mqtt.PublishAsync(msg).Wait(500);
                }
                catch { }
            }
        }
    }
}
