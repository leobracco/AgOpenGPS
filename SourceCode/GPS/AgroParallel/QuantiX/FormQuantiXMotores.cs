// ============================================================================
// FormQuantiXMotores.cs - Configuración de nodos QuantiX (ESP32 + 2 motores)
// Ubicación: SourceCode/GPS/AgroParallel/QuantiX/FormQuantiXMotores.cs
// Target: net48 (C# 7.3)
//
// Arquitectura real:
//   - Cada NODO es un ESP32 con UID único (MAC address)
//   - Cada nodo tiene 2 MOTORES (M0: semilla/producto1, M1: fertilizante/producto2)
//   - Cada motor controla hasta 7 CORTES (secciones AOG asignadas)
//   - Comunicación MQTT: agp/quantix/{UID}/target, /config, /test, /cal
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.QuantiX
{
    // Un motor dentro de un nodo (M0 o M1).
    public class QxMotorConfig
    {
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        // Dosis fija (kg/ha o L/ha). Si > 0, se usa en vez del shapefile.
        // Si = 0, usa la dosis del shapefile (variable).
        [JsonPropertyName("dosis_fija")]
        public double DosisFija { get; set; }

        // Campo del shapefile (DBF) que tiene la dosis para este motor.
        // Ej: "SEMILLA", "FERTI", "DOSIS_N", etc.
        // Si está vacío, usa el StyleField global del shapefile.
        [JsonPropertyName("campo_dosis")]
        public string CampoDosis { get; set; }

        [JsonPropertyName("kp")]
        public double Kp { get; set; }

        [JsonPropertyName("ki")]
        public double Ki { get; set; }

        [JsonPropertyName("kd")]
        public double Kd { get; set; }

        [JsonPropertyName("pwm_min")]
        public int PwmMin { get; set; }

        [JsonPropertyName("pwm_max")]
        public int PwmMax { get; set; }

        [JsonPropertyName("meter_cal")]
        public double MeterCal { get; set; }

        [JsonPropertyName("max_integral")]
        public double MaxIntegral { get; set; }

        [JsonPropertyName("deadband")]
        public int Deadband { get; set; }

        [JsonPropertyName("slew_rate")]
        public int SlewRate { get; set; }

        // Dientes del engranaje donde lee el sensor (= pulsos por vuelta).
        [JsonPropertyName("dientes_engranaje")]
        public int DientesEngranaje { get; set; }

        // Tipo de motor: 0=Eléctrico, 1=Hidráulico
        [JsonPropertyName("motor_type")]
        public int MotorType { get; set; }

        // Feedforward: Hz medidos a PWM máximo
        [JsonPropertyName("max_hz")]
        public double MaxHz { get; set; }

        // Ganancia feedforward (default 1.0)
        [JsonPropertyName("ff_gain")]
        public double FFGain { get; set; }

        // Coeficiente filtro exponencial sensor (0.4 eléctrico, 0.2 hidráulico)
        [JsonPropertyName("alpha")]
        public double Alpha { get; set; }

        // Rampa PWM/segundo (independiente de PIDtime)
        [JsonPropertyName("slew_rate_per_sec")]
        public double SlewRatePerSec { get; set; }

        // Intervalo PID en ms (50 eléctrico, 200 hidráulico)
        [JsonPropertyName("pid_time")]
        public int PIDTime { get; set; }

        // Cortes (secciones AOG) que controla este motor (1-based).
        [JsonPropertyName("cortes")]
        public List<int> Cortes { get; set; }

        public QxMotorConfig()
        {
            Nombre = "Motor";
            MotorType = 0;
            Kp = 80; Ki = 30; Kd = 0;
            PwmMin = 600; PwmMax = 4095;
            MeterCal = 50;
            MaxIntegral = 1200;
            Deadband = 2; SlewRate = 40;
            MaxHz = 40; FFGain = 1.0; Alpha = 0.4;
            SlewRatePerSec = 5000; PIDTime = 50;
            DientesEngranaje = 20;
            Cortes = new List<int>();
        }
    }

    // Un nodo ESP32 con 2 motores.
    public class QxNodoConfig
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("habilitado")]
        public bool Habilitado { get; set; }

        [JsonPropertyName("motores")]
        public QxMotorConfig[] Motores { get; set; }

        public QxNodoConfig()
        {
            Uid = "";
            Nombre = "Nodo QuantiX";
            Habilitado = true;
            Motores = new QxMotorConfig[]
            {
                new QxMotorConfig { Nombre = "Producto 1", Cortes = new List<int> { 1,2,3,4,5,6,7 } },
                new QxMotorConfig { Nombre = "Producto 2", Cortes = new List<int>() }
            };
        }
    }

    // Archivo de persistencia.
    public class MotoresConfig
    {
        [JsonPropertyName("nodos")]
        public List<QxNodoConfig> Nodos { get; set; }

        // UIDs ignorados (borrados manualmente — no volver a auto-registrar).
        [JsonPropertyName("ignorados")]
        public List<string> Ignorados { get; set; }

        public MotoresConfig()
        {
            Nodos = new List<QxNodoConfig>();
            Ignorados = new List<string>();
        }

        private static readonly string FileName = "quantiX_motores.json";

        public static MotoresConfig Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path)) return new MotoresConfig();
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<MotoresConfig>(File.ReadAllText(path), opts)
                    ?? new MotoresConfig();
            }
            catch { return new MotoresConfig(); }
        }

        public void Save()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
        }
    }

    // =========================================================================
    // UI
    // =========================================================================

    public class FormQuantiXMotores : Form
    {
        private readonly QuantiXConfig _cfg;
        private MotoresConfig _motores;
        private FlowLayoutPanel _list;
        private MqttClientWrapper _mqtt;
        private Label _lblDiscovery;

        public FormQuantiXMotores(QuantiXConfig cfg)
        {
            _cfg = cfg ?? QuantiXConfig.Load();
            _motores = MotoresConfig.Load();
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RebuildCards();
            StartDiscovery();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_mqtt != null)
            {
                var m = _mqtt;
                _mqtt = null;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { m.Dispose(); } catch { }
                });
            }
        }

        // ── Discovery: escucha agp/quantix/announcement ─────────────
        private void StartDiscovery()
        {
            try
            {
                var vistaXCfg = VistaXConfig.Load();
                if (string.IsNullOrWhiteSpace(vistaXCfg.BrokerAddress)) return;

                // Crear un config temporal para suscribirse al topic de announcement.
                var discoveryCfg = new VistaXConfig
                {
                    Enabled = true,
                    BrokerAddress = vistaXCfg.BrokerAddress,
                    BrokerPort = vistaXCfg.BrokerPort,
                    ClientId = "QX_Discovery",
                    TelemetriaTopic = "agp/quantix/announcement",
                    SectionsTopic = "",
                    SpeedTopic = ""
                };

                _mqtt = new MqttClientWrapper(discoveryCfg);
                _mqtt.MessageReceived += OnDiscoveryMessage;
                _ = _mqtt.ConnectAsync();

                if (_lblDiscovery != null)
                {
                    _lblDiscovery.Text = "\U0001F4E1 Escuchando nodos...";
                    _lblDiscovery.ForeColor = Theme.Accent;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[QX-Discovery] " + ex.Message);
            }
        }

        private void OnDiscoveryMessage(string topic, string payload)
        {
            if (!topic.Contains("announcement")) return;

            try
            {
                // Payload: {"uid":"QX-XXXXXXXX","ip":"192.168.1.50","type":"MOTOR"}
                string uid = ExtractJsonString(payload, "uid");
                string ip = ExtractJsonString(payload, "ip");
                string type = ExtractJsonString(payload, "type");

                if (string.IsNullOrEmpty(uid)) return;

                // Verificar si ya existe o fue ignorado (borrado).
                bool skip = false;
                foreach (var nodo in _motores.Nodos)
                {
                    if (string.Equals(nodo.Uid, uid, StringComparison.OrdinalIgnoreCase))
                    { skip = true; break; }
                }
                if (!skip && _motores.Ignorados != null)
                {
                    foreach (var ign in _motores.Ignorados)
                    {
                        if (string.Equals(ign, uid, StringComparison.OrdinalIgnoreCase))
                        { skip = true; break; }
                    }
                }

                if (!skip)
                {
                    // Auto-registrar nodo nuevo.
                    int n = _motores.Nodos.Count + 1;
                    int baseCorte = (n - 1) * 7 + 1;
                    var cortes = new List<int>();
                    for (int i = 0; i < 7; i++) cortes.Add(baseCorte + i);

                    _motores.Nodos.Add(new QxNodoConfig
                    {
                        Uid = uid,
                        Nombre = "Nodo " + n + " (" + ip + ")",
                        Motores = new QxMotorConfig[]
                        {
                            new QxMotorConfig { Nombre = "Producto 1", Cortes = cortes },
                            new QxMotorConfig { Nombre = "Producto 2", Cortes = new List<int>() }
                        }
                    });

                    _motores.Save();
                    System.Diagnostics.Debug.WriteLine("[QX-Discovery] Nodo registrado: " + uid + " IP=" + ip);

                    // Rebuild UI en el hilo de UI.
                    try
                    {
                        if (InvokeRequired)
                            BeginInvoke(new Action(() =>
                            {
                                RebuildCards();
                                if (_lblDiscovery != null)
                                    _lblDiscovery.Text = "\u2705 Nodo detectado: " + uid;
                            }));
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string ExtractJsonString(string json, string key)
        {
            string search = "\"" + key + "\":\"";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return "";
            idx += search.Length;
            int end = json.IndexOf('"', idx);
            if (end < 0) return "";
            return json.Substring(idx, end - idx);
        }

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true,
                BackColor = Theme.BgBlack,
                Padding = new Padding(20, 18, 20, 18)
            };
            _list.Resize += (s, ev) =>
            {
                int w = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
                foreach (Control c in _list.Controls)
                    if (c.Width != w) c.Width = w;
            };
            Controls.Add(_list);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = Theme.BgToolbar };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(Theme.Border))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            var btnAdd = Theme.MkAccentButton("+  AGREGAR NODO", 160, 34);
            btnAdd.Location = new Point(20, 8);
            btnAdd.Click += (s, ev) => AddNodo();
            footer.Controls.Add(btnAdd);

            var btnSave = Theme.MkAccentButton("\u2713  GUARDAR", 120, 34);
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Click += (s, ev) => { _motores.Save(); };
            footer.Controls.Add(btnSave);

            var btnSendAll = Theme.MkSecondaryButton("\U0001F4E1  ENVIAR TODO", 160, 34);
            btnSendAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSendAll.Click += (s, ev) => SendAllConfigs();
            footer.Controls.Add(btnSendAll);

            var btnUdpCfg = Theme.MkSecondaryButton("\u2699  CONFIG UDP", 140, 34);
            btnUdpCfg.Location = new Point(190, 8);
            btnUdpCfg.Click += (s, ev) =>
            {
                using (var frm = new FormQuantiXConfig(QuantiXConfig.Load()))
                    frm.ShowDialog(this);
            };
            footer.Controls.Add(btnUdpCfg);

            _lblDiscovery = new Label
            {
                Text = "\U0001F4E1 Buscando nodos...",
                Font = Theme.FontSmall, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Location = new Point(340, 18), AutoSize = true
            };
            footer.Controls.Add(_lblDiscovery);

            footer.Resize += (s, ev) =>
            {
                btnSave.Location = new Point(footer.Width - btnSave.Width - 20, 8);
                btnSendAll.Location = new Point(btnSave.Left - btnSendAll.Width - 10, 8);
            };

            Controls.Add(footer);
            _list.BringToFront();
        }

        private void RebuildCards()
        {
            _list.SuspendLayout();
            _list.Controls.Clear();

            if (_motores.Nodos.Count == 0)
            {
                _list.Controls.Add(new Label
                {
                    Text = "No hay nodos QuantiX configurados.\n\n"
                        + "Cada nodo es un ESP32 que controla 2 motores\n"
                        + "(ej: semilla + fertilizante) con hasta 7 cortes cada uno.",
                    Font = Theme.FontBody, ForeColor = Theme.TextFaint,
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
                });
                _list.ResumeLayout();
                return;
            }

            foreach (var nodo in _motores.Nodos)
                _list.Controls.Add(MkNodoCard(nodo));

            _list.ResumeLayout();
        }

        private Panel MkNodoCard(QxNodoConfig nodo)
        {
            int cardW = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
            var card = new Panel
            {
                Size = new Size(cardW, 480),
                Margin = new Padding(0, 0, 0, 10),
                Padding = new Padding(Theme.CardPadding),
                BackColor = Theme.BgCard
            };
            Color accent = nodo.Habilitado ? Theme.Accent : Theme.TextFaint;
            card.Paint += (s, ev) =>
            {
                var g = ev.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRoundedRect(g, new Rectangle(0, 0, card.Width - 1, card.Height - 1),
                    Theme.BgCard, Theme.BorderRadius);
                Theme.DrawRoundedBorder(g, new Rectangle(0, 0, card.Width - 1, card.Height - 1),
                    Theme.Border, Theme.BorderRadius);
                using (var b = new SolidBrush(accent))
                    g.FillRectangle(b, 0, Theme.BorderRadius, 3, card.Height - Theme.BorderRadius * 2);
            };

            int y = 12;

            // ── Header: Nombre + UID + Habilitado ───────────────────────
            var txtNombre = Theme.MkTextBox(200);
            txtNombre.Text = nodo.Nombre ?? "";
            txtNombre.Font = new Font(Theme.FontFamily, 12f, FontStyle.Bold);
            txtNombre.Location = new Point(18, y);
            txtNombre.TextChanged += (s, ev) => nodo.Nombre = txtNombre.Text;
            card.Controls.Add(txtNombre);

            card.Controls.Add(new Label
            {
                Text = "UID:", Font = Theme.FontSmall, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Location = new Point(230, y + 2), AutoSize = true
            });
            var txtUid = Theme.MkTextBox(160);
            txtUid.Text = nodo.Uid ?? "";
            txtUid.Font = Theme.FontMono;
            txtUid.ForeColor = Theme.Accent;
            txtUid.Location = new Point(260, y);
            txtUid.TextChanged += (s, ev) => nodo.Uid = txtUid.Text.Trim();
            card.Controls.Add(txtUid);

            var chk = Theme.MkCheck("Hab.", nodo.Habilitado);
            chk.Location = new Point(440, y + 2);
            chk.CheckedChanged += (s, ev) => { nodo.Habilitado = chk.Checked; card.Invalidate(); };
            card.Controls.Add(chk);

            // Topic info.
            card.Controls.Add(new Label
            {
                Text = "MQTT: agp/quantix/" + (string.IsNullOrEmpty(nodo.Uid) ? "???" : nodo.Uid) + "/...",
                Font = Theme.FontSmall, ForeColor = Theme.TextFaint, BackColor = Color.Transparent,
                Location = new Point(18, y + 28), AutoSize = true
            });

            y += 52;

            // ── Motor 0 y Motor 1 ───────────────────────────────────────
            for (int mi = 0; mi < 2; mi++)
            {
                if (mi >= nodo.Motores.Length) break;
                var motor = nodo.Motores[mi];
                int mx = 18;

                // Separador + label.
                Color mColor = mi == 0 ? Theme.Accent : Color.FromArgb(230, 160, 30);
                card.Controls.Add(new Label
                {
                    Text = "M" + mi + " \u2014 " + (motor.Nombre ?? "Motor " + mi),
                    Font = new Font(Theme.FontFamily, 10f, FontStyle.Bold),
                    ForeColor = mColor, BackColor = Color.Transparent,
                    Location = new Point(mx, y), AutoSize = true
                });

                var txtMNombre = Theme.MkTextBox(140);
                txtMNombre.Text = motor.Nombre ?? "";
                txtMNombre.Font = Theme.FontBody;
                txtMNombre.Location = new Point(200, y - 2);
                txtMNombre.TextChanged += (s, ev) => motor.Nombre = txtMNombre.Text;
                card.Controls.Add(txtMNombre);
                y += 26;

                // Campo dosis del shapefile (combo con campos disponibles).
                AddLabel(card, "Insumo (campo shapefile):", mx, y);
                var cboCampo = Theme.MkCombo(160);
                cboCampo.DropDownStyle = ComboBoxStyle.DropDown;
                cboCampo.Font = Theme.FontMono;
                cboCampo.ForeColor = Theme.Accent;
                cboCampo.Location = new Point(mx, y + 14);
                // Cargar campos disponibles del shapefile.
                var campos = GetShapefileFields();
                cboCampo.Items.Add("(global)");
                foreach (var c in campos) cboCampo.Items.Add(c);
                // Seleccionar el actual.
                if (string.IsNullOrEmpty(motor.CampoDosis))
                    cboCampo.SelectedIndex = 0;
                else
                {
                    int idx = cboCampo.Items.IndexOf(motor.CampoDosis);
                    if (idx >= 0) cboCampo.SelectedIndex = idx;
                    else cboCampo.Text = motor.CampoDosis;
                }
                cboCampo.SelectedIndexChanged += (s2, ev2) =>
                {
                    if (cboCampo.SelectedIndex == 0)
                        motor.CampoDosis = "";
                    else
                        motor.CampoDosis = cboCampo.Text.Trim();
                };
                cboCampo.Leave += (s2, ev2) =>
                {
                    string val = cboCampo.Text.Trim();
                    motor.CampoDosis = val == "(global)" ? "" : val;
                };
                card.Controls.Add(cboCampo);

                // Dosis fija + MeterCal + PID.
                AddNum(card, "Dosis fija (0=mapa):", mx + 180, y, 0, 10000, motor.DosisFija, 1,
                    v => motor.DosisFija = v);
                AddNum(card, "MeterCal:", mx + 340, y, 0.1, 10000, motor.MeterCal, 1, v => motor.MeterCal = v);
                y += 38;
                AddNum(card, "Kp:", mx, y, 0, 100, motor.Kp, 2, v => motor.Kp = v);
                AddNum(card, "Ki:", mx + 110, y, 0, 100, motor.Ki, 2, v => motor.Ki = v);
                y += 38;
                AddNum(card, "PWM min:", mx, y, 0, 4095, motor.PwmMin, 0, v => motor.PwmMin = (int)v);
                AddNum(card, "PWM max:", mx + 140, y, 0, 4095, motor.PwmMax, 0, v => motor.PwmMax = (int)v);
                y += 38;

                // Cortes (secciones AOG).
                card.Controls.Add(new Label
                {
                    Text = "Cortes (secciones AOG):", Font = Theme.FontSmall,
                    ForeColor = Theme.TextFaint, BackColor = Color.Transparent,
                    Location = new Point(mx, y), AutoSize = true
                });

                string cortesStr = motor.Cortes != null
                    ? string.Join(",", motor.Cortes) : "";
                int capturedMi = mi;
                var txtCortes = Theme.MkTextBox(200);
                txtCortes.Text = cortesStr;
                txtCortes.Font = Theme.FontMono;
                txtCortes.ForeColor = mColor;
                txtCortes.Location = new Point(mx + 170, y - 2);
                txtCortes.Leave += (s, ev) =>
                {
                    motor.Cortes = new List<int>();
                    foreach (var p in txtCortes.Text.Split(','))
                    {
                        int n;
                        if (int.TryParse(p.Trim(), out n) && n > 0) motor.Cortes.Add(n);
                    }
                };
                card.Controls.Add(txtCortes);

                card.Controls.Add(new Label
                {
                    Text = "(ej: 1,2,3,4,5,6,7)", Font = Theme.FontSmall,
                    ForeColor = Theme.TextDisabled, BackColor = Color.Transparent,
                    Location = new Point(mx + 380, y), AutoSize = true
                });

                y += 28;
            }

            // ── Buttons ─────────────────────────────────────────────────
            y += 4;
            var btnSend = Theme.MkSecondaryButton("\U0001F4E1  ENVIAR CONFIG", 170, 28);
            btnSend.Location = new Point(18, y);
            btnSend.Click += (s, ev) => SendNodoConfig(nodo);
            card.Controls.Add(btnSend);

            var btnDebug = Theme.MkSecondaryButton("\U0001F50D DEBUG", 100, 28);
            btnDebug.Location = new Point(200, y);
            btnDebug.Click += (s, ev) => ToggleDebug(nodo);
            card.Controls.Add(btnDebug);

            var btnDel = Theme.MkDangerButton("\U0001F5D1", 40, 28);
            btnDel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDel.Location = new Point(cardW - 60, y);
            btnDel.Click += (s, ev) =>
            {
                // Agregar a ignorados para que no vuelva por discovery.
                if (!string.IsNullOrEmpty(nodo.Uid))
                {
                    if (_motores.Ignorados == null) _motores.Ignorados = new List<string>();
                    if (!_motores.Ignorados.Contains(nodo.Uid))
                        _motores.Ignorados.Add(nodo.Uid);
                }
                _motores.Nodos.Remove(nodo);
                _motores.Save();
                RebuildCards();
            };
            card.Controls.Add(btnDel);

            return card;
        }

        private List<string> GetShapefileFields()
        {
            var result = new List<string>();
            try
            {
                // Buscar FormGPS en las forms abiertas.
                foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms)
                {
                    if (!(f is AgOpenGPS.FormGPS gps)) continue;

                    var layerField = gps.GetType().GetField("shapefileLayer",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (layerField == null) break;

                    object layer = layerField.GetValue(gps);
                    if (layer == null) break;

                    // FieldNames: string[] con los nombres de las columnas DBF.
                    var pFields = layer.GetType().GetProperty("FieldNames");
                    if (pFields != null)
                    {
                        var names = pFields.GetValue(layer) as System.Collections.Generic.IReadOnlyList<string>;
                        if (names != null)
                            foreach (var n in names) result.Add(n);
                    }
                    break;
                }
            }
            catch { }
            return result;
        }

        private void AddLabel(Panel parent, string text, int x, int y)
        {
            parent.Controls.Add(new Label
            {
                Text = text, Font = Theme.FontSmall, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent, Location = new Point(x, y), AutoSize = true
            });
        }

        private void AddNum(Panel parent, string label, int x, int y,
            double min, double max, double value, int decimals, Action<double> onChange)
        {
            parent.Controls.Add(new Label
            {
                Text = label, Font = Theme.FontSmall, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent, Location = new Point(x, y), AutoSize = true
            });
            decimal inc = decimals > 0 ? (decimal)Math.Pow(10, -decimals) : 1;
            var num = Theme.MkNumeric((decimal)min, (decimal)max,
                (decimal)Math.Max(min, Math.Min(max, value)), decimals, inc, 86);
            num.Font = new Font(Theme.FontFamily, 8.5f, FontStyle.Bold);
            num.Location = new Point(x, y + 14);
            num.ValueChanged += (s, ev) => onChange((double)num.Value);
            parent.Controls.Add(num);
        }

        private void AddNodo()
        {
            int n = _motores.Nodos.Count + 1;
            int baseCorte = (n - 1) * 7 + 1;
            var cortes = new List<int>();
            for (int i = 0; i < 7; i++) cortes.Add(baseCorte + i);

            // UID placeholder — el real viene del MAC del ESP32 (se muestra en Serial al arrancar).
            string uidPlaceholder = "QX-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();

            _motores.Nodos.Add(new QxNodoConfig
            {
                Uid = uidPlaceholder,
                Nombre = "Nodo " + n,
                Motores = new QxMotorConfig[]
                {
                    new QxMotorConfig { Nombre = "Producto 1", Cortes = cortes },
                    new QxMotorConfig { Nombre = "Producto 2", Cortes = new List<int>() }
                }
            });
            RebuildCards();
        }

        // =====================================================================
        // MQTT — topics alineados al firmware: agp/quantix/{UID}/config
        // =====================================================================

        private async void SendNodoConfig(QxNodoConfig nodo)
        {
            if (string.IsNullOrWhiteSpace(nodo.Uid))
            {
                MessageBox.Show(this, "Ingres\u00E1 el UID del nodo primero.\n"
                    + "Lo pod\u00E9s ver en el Serial del ESP32 al arrancar.",
                    "UID requerido", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var vistaXCfg = VistaXConfig.Load();
                var factory = new MqttFactory();
                using (var client = factory.CreateMqttClient())
                {
                    var opts = new MqttClientOptionsBuilder()
                        .WithTcpServer(vistaXCfg.BrokerAddress ?? "127.0.0.1",
                            vistaXCfg.BrokerPort > 0 ? vistaXCfg.BrokerPort : 1883)
                        .WithClientId("QX_Cfg_" + Guid.NewGuid().ToString("N").Substring(0, 6))
                        .WithCleanSession(true)
                        .Build();

                    await client.ConnectAsync(opts);

                    // Payload matchea el formato del firmware MQTT_Custom.cpp.
                    // Topic: agp/quantix/{UID}/config
                    var sb = new StringBuilder();
                    sb.Append("{\"configs\":[");

                    for (int mi = 0; mi < nodo.Motores.Length; mi++)
                    {
                        var m = nodo.Motores[mi];
                        if (mi > 0) sb.Append(',');
                        sb.Append("{\"idx\":").Append(mi);
                        sb.Append(",\"config_pid\":{");
                        sb.Append("\"kp\":").Append(m.Kp.ToString(CultureInfo.InvariantCulture));
                        sb.Append(",\"ki\":").Append(m.Ki.ToString(CultureInfo.InvariantCulture));
                        sb.Append("}");
                        sb.Append(",\"calibracion\":{");
                        sb.Append("\"pwm_min\":").Append(m.PwmMin);
                        sb.Append(",\"pwm_max\":").Append(m.PwmMax);
                        sb.Append("}");
                        sb.Append(",\"meter_cal\":").Append(m.MeterCal.ToString(CultureInfo.InvariantCulture));
                        sb.Append('}');
                    }
                    sb.Append("]}");

                    string topic = "agp/quantix/" + nodo.Uid + "/config";
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(sb.ToString())
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(true)
                        .Build();
                    await client.PublishAsync(msg);

                    await client.DisconnectAsync(
                        new MqttClientDisconnectOptionsBuilder().Build());

                    MessageBox.Show(this,
                        "Config enviada a " + topic + "\n\n"
                        + "M0: Kp=" + nodo.Motores[0].Kp + " Ki=" + nodo.Motores[0].Ki
                        + " MeterCal=" + nodo.Motores[0].MeterCal + "\n"
                        + (nodo.Motores.Length > 1
                            ? "M1: Kp=" + nodo.Motores[1].Kp + " Ki=" + nodo.Motores[1].Ki
                              + " MeterCal=" + nodo.Motores[1].MeterCal
                            : ""),
                        "Config MQTT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error MQTT: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void ToggleDebug(QxNodoConfig nodo)
        {
            if (string.IsNullOrWhiteSpace(nodo.Uid)) return;

            try
            {
                var vistaXCfg = VistaXConfig.Load();
                var factory = new MqttFactory();
                using (var client = factory.CreateMqttClient())
                {
                    var opts = new MqttClientOptionsBuilder()
                        .WithTcpServer(vistaXCfg.BrokerAddress ?? "127.0.0.1",
                            vistaXCfg.BrokerPort > 0 ? vistaXCfg.BrokerPort : 1883)
                        .WithClientId("QX_Dbg_" + Guid.NewGuid().ToString("N").Substring(0, 4))
                        .WithCleanSession(true)
                        .Build();

                    await client.ConnectAsync(opts);

                    string topic = "agp/quantix/" + nodo.Uid + "/debug";
                    string payload = "{\"debug\":true}";

                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(payload)
                        .Build();
                    await client.PublishAsync(msg);
                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());

                    MessageBox.Show(this,
                        "Debug activado en " + nodo.Uid + "\n\n"
                        + "Abr\u00ED el monitor Serial del ESP32 para ver los mensajes.\n"
                        + "Se va a loguear cada mensaje MQTT que recibe con su payload completo.\n\n"
                        + "Para desactivar envi\u00E1 {\"debug\":false} al mismo topic.",
                        "Debug MQTT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SendAllConfigs()
        {
            _motores.Save();
            foreach (var nodo in _motores.Nodos)
            {
                if (nodo.Habilitado && !string.IsNullOrWhiteSpace(nodo.Uid))
                    SendNodoConfig(nodo);
            }
        }
    }
}
