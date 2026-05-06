// ============================================================================
// FormNodos.cs - Vista unificada de nodos conectados (MQTT discovery)
// Escucha agp/quantix/announcement y vistax/nodos/telemetria para listar
// todos los dispositivos con UID, tipo, versión, IP, uptime.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgroParallel.VistaX;
// Theme is in same namespace AgroParallel.Common — no using needed

namespace AgroParallel.Common
{
    public class FormNodos : Form
    {
        private FlowLayoutPanel _list;
        private MqttClientWrapper _mqtt;
        private Timer _refreshTimer;

        private class NodoInfo
        {
            public string Uid;
            public string Ip;
            public string Type;
            public string Firmware;
            public int Motors;
            public long Uptime;
            public DateTime LastSeen;
            public bool Online;
        }

        private readonly Dictionary<string, NodoInfo> _nodos =
            new Dictionary<string, NodoInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        public FormNodos()
        {
            BuildUI();
            StartDiscovery();
        }

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            // Header
            var header = new Panel
            {
                Dock = DockStyle.Top, Height = 50,
                BackColor = Theme.BgHeader
            };
            header.Paint += (s, ev) =>
            {
                var g = ev.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var f = Theme.FontTitle)
                using (var br = new SolidBrush(Theme.Accent))
                    g.DrawString("\U0001F4E1  NODOS CONECTADOS", f, br, 20, 14);

                int count;
                lock (_lock) count = _nodos.Count;
                using (var f = Theme.FontBody)
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString(count + " dispositivo" + (count != 1 ? "s" : ""), f, br, header.Width - 160, 18);
            };
            header.Resize += (s, ev) => header.Invalidate();
            Controls.Add(header);

            // List
            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.BgBlack,
                Padding = new Padding(16, 12, 16, 12)
            };
            _list.Resize += (s, ev) =>
            {
                int w = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
                foreach (Control c in _list.Controls)
                    if (c.Width != w) c.Width = w;
            };
            Controls.Add(_list);
            _list.BringToFront();

            // Refresh timer
            _refreshTimer = new Timer { Interval = 2000 };
            _refreshTimer.Tick += (s, ev) => RebuildCards();
            _refreshTimer.Start();
        }

        private void StartDiscovery()
        {
            try
            {
                var cfg = VistaXConfig.Load();
                if (string.IsNullOrWhiteSpace(cfg.BrokerAddress)) return;

                var mqttCfg = new VistaXConfig
                {
                    Enabled = true,
                    BrokerAddress = cfg.BrokerAddress,
                    BrokerPort = cfg.BrokerPort,
                    ClientId = "Nodos_Hub",
                    TelemetriaTopic = "agp/quantix/announcement",
                    SectionsTopic = "agp/+/announcement",
                    SpeedTopic = "vistax/nodos/announcement"
                };
                _mqtt = new MqttClientWrapper(mqttCfg);
                _mqtt.MessageReceived += OnMessage;
                _ = _mqtt.ConnectAsync();
            }
            catch { }
        }

        private void OnMessage(string topic, string payload)
        {
            try
            {
                // Parse JSON manually (no System.Text.Json dependency issues).
                string uid = ExtractJson(payload, "uid");
                if (string.IsNullOrEmpty(uid)) return;

                string ip = ExtractJson(payload, "ip");
                string type = ExtractJson(payload, "type");
                string fw = ExtractJson(payload, "fw");
                int motors = ExtractJsonInt(payload, "motors");
                long uptime = ExtractJsonLong(payload, "uptime");

                if (string.IsNullOrEmpty(type)) type = "Desconocido";

                lock (_lock)
                {
                    if (!_nodos.ContainsKey(uid))
                        _nodos[uid] = new NodoInfo();

                    var n = _nodos[uid];
                    n.Uid = uid;
                    n.Ip = ip;
                    n.Type = type;
                    if (!string.IsNullOrEmpty(fw)) n.Firmware = fw;
                    if (motors > 0) n.Motors = motors;
                    if (uptime > 0) n.Uptime = uptime;
                    n.LastSeen = DateTime.Now;
                    n.Online = true;
                }
            }
            catch { }
        }

        private void RebuildCards()
        {
            List<NodoInfo> nodes;
            lock (_lock)
            {
                // Mark offline if not seen in 30s
                foreach (var n in _nodos.Values)
                    n.Online = (DateTime.Now - n.LastSeen).TotalSeconds < 30;

                nodes = new List<NodoInfo>(_nodos.Values);
            }

            // Sort: online first, then by type, then UID
            nodes.Sort((a, b) =>
            {
                int c = b.Online.CompareTo(a.Online);
                if (c != 0) return c;
                c = string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Uid, b.Uid, StringComparison.OrdinalIgnoreCase);
            });

            _list.SuspendLayout();
            _list.Controls.Clear();

            int cardW = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
            if (cardW < 100) cardW = 700;

            if (nodes.Count == 0)
            {
                _list.Controls.Add(new Label
                {
                    Text = "Buscando nodos en la red MQTT...\n\n"
                        + "Los nodos se anuncian autom\u00E1ticamente al conectarse.\n"
                        + "Verific\u00E1 que el broker MQTT est\u00E9 corriendo (AgIO).",
                    Font = new Font(Theme.FontFamily, 10f),
                    ForeColor = Theme.TextFaint,
                    BackColor = Color.Transparent,
                    Size = new Size(cardW, 100),
                    Padding = new Padding(12, 12, 0, 0)
                });
            }
            else
            {
                foreach (var n in nodes)
                    _list.Controls.Add(MkCard(n, cardW));
            }

            _list.ResumeLayout();

            // Refresh header count
            foreach (Control c in Controls)
                if (c is Panel p && p.Dock == DockStyle.Top)
                { p.Invalidate(); break; }
        }

        private Panel MkCard(NodoInfo n, int cardW)
        {
            var card = new Panel
            {
                Size = new Size(cardW, 70),
                Margin = new Padding(0, 0, 0, 6),
                BackColor = Theme.BgCard
            };

            Color accent = GetTypeColor(n.Type);
            string icon = GetTypeIcon(n.Type);

            card.Paint += (s, ev) =>
            {
                var g = ev.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Theme.FillRoundedRect(g, new Rectangle(0, 0, card.Width - 1, card.Height - 1),
                    Theme.BgCard, 8);
                Theme.DrawRoundedBorder(g, new Rectangle(0, 0, card.Width - 1, card.Height - 1),
                    Theme.Border, 8);

                // Accent bar
                using (var br = new SolidBrush(accent))
                    g.FillRectangle(br, 0, 8, 3, card.Height - 16);

                // Online LED
                Color led = n.Online ? Color.LimeGreen : Color.FromArgb(80, 80, 80);
                using (var br = new SolidBrush(led))
                    g.FillEllipse(br, 14, 10, 10, 10);

                // Icon + Type
                using (var f = new Font(Theme.FontFamily, 13f))
                using (var br = new SolidBrush(accent))
                    g.DrawString(icon, f, br, 30, 6);

                using (var f = new Font(Theme.FontFamily, 11f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.TextPrimary))
                    g.DrawString(n.Type, f, br, 52, 8);

                // UID
                using (var f = Theme.FontMono)
                using (var br = new SolidBrush(accent))
                    g.DrawString(n.Uid, f, br, 160, 10);

                // Firmware version
                string fw = string.IsNullOrEmpty(n.Firmware) ? "" : "v" + n.Firmware;
                if (!string.IsNullOrEmpty(fw))
                {
                    using (var f = new Font(Theme.FontFamily, 9f, FontStyle.Bold))
                    using (var br = new SolidBrush(Theme.TextSecondary))
                        g.DrawString(fw, f, br, 320, 11);
                }

                // Second row: IP, Motors, Uptime, Last seen
                int rx = 52;
                using (var f = Theme.FontSmall)
                using (var br = new SolidBrush(Theme.TextFaint))
                {
                    string ip = string.IsNullOrEmpty(n.Ip) ? "" : "IP: " + n.Ip;
                    g.DrawString(ip, f, br, rx, 38);

                    if (n.Motors > 0)
                        g.DrawString(n.Motors + " motor" + (n.Motors > 1 ? "es" : ""),
                            f, br, rx + 160, 38);

                    if (n.Uptime > 0)
                    {
                        var ts = TimeSpan.FromSeconds(n.Uptime);
                        string up = string.Format("Up: {0:D2}:{1:D2}:{2:D2}",
                            (int)ts.TotalHours, ts.Minutes, ts.Seconds);
                        g.DrawString(up, f, br, rx + 260, 38);
                    }

                    string ago = n.Online ? "en l\u00EDnea" :
                        "visto " + (int)(DateTime.Now - n.LastSeen).TotalSeconds + "s atr\u00E1s";
                    Color agoColor = n.Online ? Theme.Accent : Theme.TextFaint;
                    using (var br2 = new SolidBrush(agoColor))
                        g.DrawString(ago, f, br2, rx + 400, 38);
                }

                // Third row: status
                using (var f = Theme.FontSmall)
                {
                    string status = n.Online ? "\u25CF Online" : "\u25CB Offline";
                    Color stColor = n.Online ? Theme.Accent : Theme.Error;
                    using (var br = new SolidBrush(stColor))
                        g.DrawString(status, f, br, rx, 52);
                }
            };

            return card;
        }

        private static Color GetTypeColor(string type)
        {
            if (type == null) return Theme.TextFaint;
            string t = type.ToUpperInvariant();
            if (t.Contains("QUANTIX") || t.Contains("MOTOR")) return Color.FromArgb(230, 160, 30);
            if (t.Contains("VISTAX") || t.Contains("SIEMBRA")) return Theme.Accent;
            if (t.Contains("SECTION")) return Theme.Herramienta;
            if (t.Contains("ORBIT")) return Color.FromArgb(100, 180, 255);
            return Theme.TextSecondary;
        }

        private static string GetTypeIcon(string type)
        {
            if (type == null) return "\u2753";
            string t = type.ToUpperInvariant();
            if (t.Contains("QUANTIX") || t.Contains("MOTOR")) return "\U0001F4CA";
            if (t.Contains("VISTAX") || t.Contains("SIEMBRA")) return "\U0001F33F";
            if (t.Contains("SECTION")) return "\U0001F3AF";
            if (t.Contains("ORBIT")) return "\u2601";
            return "\U0001F4E1";
        }

        private static string ExtractJson(string json, string key)
        {
            string s = "\"" + key + "\":\"";
            int i = json.IndexOf(s, StringComparison.Ordinal);
            if (i < 0) return "";
            i += s.Length;
            int e = json.IndexOf('"', i);
            return e < 0 ? "" : json.Substring(i, e - i);
        }

        private static int ExtractJsonInt(string json, string key)
        {
            string s = "\"" + key + "\":";
            int i = json.IndexOf(s, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += s.Length;
            int e = i;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            int val;
            return int.TryParse(json.Substring(i, e - i), out val) ? val : 0;
        }

        private static long ExtractJsonLong(string json, string key)
        {
            string s = "\"" + key + "\":";
            int i = json.IndexOf(s, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += s.Length;
            int e = i;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            long val;
            return long.TryParse(json.Substring(i, e - i), out val) ? val : 0;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_refreshTimer != null) { _refreshTimer.Stop(); _refreshTimer.Dispose(); }
            if (_mqtt != null)
            {
                var m = _mqtt; _mqtt = null;
                Task.Run(() => { try { m.Dispose(); } catch { } });
            }
        }
    }
}
