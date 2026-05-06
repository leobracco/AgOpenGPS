// ============================================================================
// FormSectionXConfig.cs - Configuración de SectionX
// Mapeo visual: cable N del PCA9685 → sección M de AOG + tren (Del/Tras).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AgroParallel.Common;
using AgroParallel.VistaX;

namespace AgroParallel.SectionX
{
    public class FormSectionXConfig : Form
    {
        private readonly SectionXConfig _cfg;
        private FlowLayoutPanel _list;
        private MqttClientWrapper _mqtt;
        private Label _lblDiscovery;

        public FormSectionXConfig(SectionXConfig cfg)
        {
            _cfg = cfg ?? SectionXConfig.Load();
            BuildUI();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); RebuildCards(); StartDiscovery(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_mqtt != null) { var m = _mqtt; _mqtt = null;
                System.Threading.Tasks.Task.Run(() => { try { m.Dispose(); } catch { } }); }
        }

        private void StartDiscovery()
        {
            try
            {
                var v = VistaXConfig.Load();
                if (string.IsNullOrWhiteSpace(v.BrokerAddress)) return;
                var d = new VistaXConfig { Enabled = true, BrokerAddress = v.BrokerAddress,
                    BrokerPort = v.BrokerPort, ClientId = "SX_Disc",
                    TelemetriaTopic = "agp/quantix/announcement", SectionsTopic = "", SpeedTopic = "" };
                _mqtt = new MqttClientWrapper(d);
                _mqtt.MessageReceived += OnDisc;
                _ = _mqtt.ConnectAsync();
            }
            catch { }
        }

        private void OnDisc(string topic, string payload)
        {
            if (!topic.Contains("announcement")) return;
            string uid = ExtractJson(payload, "uid");
            string ip = ExtractJson(payload, "ip");
            if (string.IsNullOrEmpty(uid)) return;
            if (_cfg.Nodos.Any(n => string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase))) return;
            if (_cfg.Ignorados != null && _cfg.Ignorados.Any(i => string.Equals(i, uid, StringComparison.OrdinalIgnoreCase))) return;

            var nodo = new SxNodoConfig { Uid = uid, Nombre = "Nodo (" + ip + ")", DistanciaEntreTrenes = 1.5 };
            // Default: 4 secciones, delantero cables 1-4, trasero cables 5-8.
            for (int i = 1; i <= 4; i++)
                nodo.Cables.Add(new SxCableMap { Cable = i, SeccionAOG = i, Tren = 0 });
            for (int i = 1; i <= 4; i++)
                nodo.Cables.Add(new SxCableMap { Cable = 4 + i, SeccionAOG = i, Tren = 1 });
            _cfg.Nodos.Add(nodo);
            _cfg.Save();
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(() =>
                {
                    RebuildCards();
                    try
                    {
                        foreach (Form f in Application.OpenForms)
                            if (f is AgOpenGPS.FormGPS gps)
                            { gps.ReloadSectionXBridge(); break; }
                    }
                    catch { }
                }));
            }
            catch { }
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

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true,
                BackColor = Theme.BgBlack, Padding = new Padding(20, 14, 20, 14)
            };
            _list.Resize += (s, ev) =>
            {
                int w = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
                foreach (Control c in _list.Controls) if (c.Width != w) c.Width = w;
            };
            Controls.Add(_list);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = Theme.BgToolbar };
            footer.Paint += (s, ev) => { using (var p = new Pen(Theme.Border)) ev.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };

            var btnAdd = Theme.MkAccentButton("+  AGREGAR NODO", 160, 34);
            btnAdd.Location = new Point(20, 8);
            btnAdd.Click += (s, ev) => AddNodo();
            footer.Controls.Add(btnAdd);

            _lblDiscovery = new Label { Text = "\U0001F4E1 Buscando...", Font = Theme.FontSmall,
                ForeColor = Theme.TextFaint, BackColor = Color.Transparent,
                Location = new Point(200, 18), AutoSize = true };
            footer.Controls.Add(_lblDiscovery);

            var btnSave = Theme.MkAccentButton("\u2713  GUARDAR", 120, 34);
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Click += (s, ev) =>
            {
                _cfg.Save();
                // Recargar el bridge en caliente.
                try
                {
                    foreach (Form f in Application.OpenForms)
                        if (f is AgOpenGPS.FormGPS gps)
                        { gps.ReloadSectionXBridge(); break; }
                }
                catch { }
            };
            footer.Controls.Add(btnSave);
            footer.Resize += (s, ev) => btnSave.Location = new Point(footer.Width - 140, 8);

            Controls.Add(footer);
            _list.BringToFront();
        }

        // ── Obtener cantidad de secciones AOG ──────────────────────────
        private int GetAOGSectionCount()
        {
            try
            {
                foreach (Form f in Application.OpenForms)
                    if (f is AgOpenGPS.FormGPS gps && gps.tool != null)
                        return gps.tool.numOfSections;
            }
            catch { }
            return 8;
        }

        private void RebuildCards()
        {
            _list.SuspendLayout();
            _list.Controls.Clear();
            int cardW = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;

            // Global.
            var gc = new Panel { Size = new Size(cardW, 50), Margin = new Padding(0, 0, 0, 10), BackColor = Theme.BgCard };
            gc.Paint += (s, ev) => {
                Theme.FillRoundedRect(ev.Graphics, new Rectangle(0, 0, gc.Width - 1, gc.Height - 1), Theme.BgCard, Theme.BorderRadius);
                Theme.DrawRoundedBorder(ev.Graphics, new Rectangle(0, 0, gc.Width - 1, gc.Height - 1), Theme.Border, Theme.BorderRadius);
            };
            var chkEn = new CheckBox { Text = "  SECTIONX HABILITADO", Checked = _cfg.Enabled,
                Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
                ForeColor = _cfg.Enabled ? Theme.Accent : Theme.Error, BackColor = Color.Transparent,
                Location = new Point(16, 12), AutoSize = true };
            chkEn.CheckedChanged += (s, ev) => { _cfg.Enabled = chkEn.Checked; chkEn.ForeColor = _cfg.Enabled ? Theme.Accent : Theme.Error; };
            gc.Controls.Add(chkEn);
            AddNum(gc, "Vel m\u00EDn:", 300, 6, 0, 20, _cfg.VelMinima, 1, v => _cfg.VelMinima = v);
            _list.Controls.Add(gc);

            foreach (var nodo in _cfg.Nodos)
                _list.Controls.Add(MkNodoCard(nodo, cardW));

            _list.ResumeLayout();
        }

        private Panel MkNodoCard(SxNodoConfig nodo, int cardW)
        {
            int numCables = nodo.Cables.Count;
            int cardH = 90 + numCables * 30;
            var card = new Panel { Size = new Size(cardW, cardH), Margin = new Padding(0, 0, 0, 10), BackColor = Theme.BgCard };
            Color accent = nodo.Habilitado ? Theme.Herramienta : Theme.TextFaint;
            card.Paint += (s, ev) =>
            {
                Theme.FillRoundedRect(ev.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1), Theme.BgCard, Theme.BorderRadius);
                Theme.DrawRoundedBorder(ev.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1), Theme.Border, Theme.BorderRadius);
                using (var b = new SolidBrush(accent))
                    ev.Graphics.FillRectangle(b, 0, Theme.BorderRadius, 3, card.Height - Theme.BorderRadius * 2);
            };

            int y = 8;
            // Header.
            var txtN = new TextBox { Text = nodo.Nombre ?? "", Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary, BackColor = Theme.BgInput, BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(16, y), Size = new Size(160, 24) };
            txtN.TextChanged += (s, ev) => nodo.Nombre = txtN.Text;
            card.Controls.Add(txtN);

            card.Controls.Add(new Label { Text = "UID:", Font = Theme.FontSmall, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent, Location = new Point(190, y + 4), AutoSize = true });
            var txtUid = new TextBox { Text = nodo.Uid ?? "", Font = Theme.FontMono, ForeColor = Theme.Accent,
                BackColor = Theme.BgInput, BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(220, y), Size = new Size(130, 24) };
            txtUid.TextChanged += (s, ev) => nodo.Uid = txtUid.Text.Trim();
            card.Controls.Add(txtUid);

            AddNum(card, "Dist trenes (m):", 370, y - 4, 0, 30, nodo.DistanciaEntreTrenes, 2,
                v => nodo.DistanciaEntreTrenes = v);

            var btnDel = Theme.MkButton("\U0001F5D1", Color.FromArgb(50, 20, 20), Theme.Error, 24, 22);
            btnDel.Location = new Point(cardW - 40, y);
            btnDel.Click += (s, ev) =>
            {
                if (!string.IsNullOrEmpty(nodo.Uid))
                { if (_cfg.Ignorados == null) _cfg.Ignorados = new List<string>(); _cfg.Ignorados.Add(nodo.Uid); }
                _cfg.Nodos.Remove(nodo); _cfg.Save(); RebuildCards();
            };
            card.Controls.Add(btnDel);

            y += 32;

            // Header de tabla.
            card.Controls.Add(new Label { Text = "CABLE", Font = new Font(Theme.FontFamily, 8f, FontStyle.Bold),
                ForeColor = Theme.TextFaint, BackColor = Color.Transparent,
                Location = new Point(24, y), AutoSize = true });
            card.Controls.Add(new Label { Text = "SECCI\u00D3N AOG", Font = new Font(Theme.FontFamily, 8f, FontStyle.Bold),
                ForeColor = Theme.TextFaint, BackColor = Color.Transparent,
                Location = new Point(120, y), AutoSize = true });
            card.Controls.Add(new Label { Text = "TREN", Font = new Font(Theme.FontFamily, 8f, FontStyle.Bold),
                ForeColor = Theme.TextFaint, BackColor = Color.Transparent,
                Location = new Point(300, y), AutoSize = true });
            y += 18;

            int aogSections = GetAOGSectionCount();

            // Filas de cables.
            for (int ci = 0; ci < nodo.Cables.Count; ci++)
            {
                var cable = nodo.Cables[ci];

                // Cable label.
                card.Controls.Add(new Label
                {
                    Text = "SA" + cable.Cable,
                    Font = new Font(Theme.FontFamily, 10f, FontStyle.Bold),
                    ForeColor = Theme.Herramienta, BackColor = Color.Transparent,
                    Location = new Point(24, y + 2), AutoSize = true
                });

                // Combo sección AOG.
                var cboSec = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = Theme.FontBody, ForeColor = Theme.TextPrimary, BackColor = Theme.BgInput,
                    Location = new Point(120, y), Size = new Size(150, 22)
                };
                cboSec.Items.Add("(sin asignar)");
                for (int s = 1; s <= aogSections; s++)
                    cboSec.Items.Add("Secci\u00F3n " + s);
                cboSec.SelectedIndex = cable.SeccionAOG >= 1 && cable.SeccionAOG <= aogSections
                    ? cable.SeccionAOG : 0;
                int capturedCi = ci;
                cboSec.SelectedIndexChanged += (s, ev) =>
                    nodo.Cables[capturedCi].SeccionAOG = cboSec.SelectedIndex;
                card.Controls.Add(cboSec);

                // Combo tren.
                var cboTren = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = Theme.FontBody, ForeColor = Theme.TextPrimary, BackColor = Theme.BgInput,
                    Location = new Point(300, y), Size = new Size(120, 22)
                };
                cboTren.Items.AddRange(new object[] { "Delantero", "Trasero" });
                cboTren.SelectedIndex = cable.Tren >= 0 && cable.Tren <= 1 ? cable.Tren : 0;
                cboTren.SelectedIndexChanged += (s, ev) =>
                    nodo.Cables[capturedCi].Tren = cboTren.SelectedIndex;
                card.Controls.Add(cboTren);

                y += 28;
            }

            // Botón agregar cable.
            var btnAddCable = Theme.MkButton("+ CABLE", Color.FromArgb(40, 40, 45), Theme.TextPrimary, 90, 22);
            btnAddCable.Location = new Point(24, y);
            btnAddCable.Click += (s, ev) =>
            {
                int next = nodo.Cables.Count + 1;
                nodo.Cables.Add(new SxCableMap { Cable = next, SeccionAOG = 0, Tren = 0 });
                RebuildCards();
            };
            card.Controls.Add(btnAddCable);

            return card;
        }

        private void AddNodo()
        {
            int secCount = GetAOGSectionCount();
            var nodo = new SxNodoConfig
            {
                Uid = "", Nombre = "Nodo " + (_cfg.Nodos.Count + 1),
                DistanciaEntreTrenes = 1.5
            };

            // Default: tren delantero en cables 1..N, trasero en cables N+1..2N.
            // Ambos trenes usan las MISMAS secciones AOG (1..N).
            for (int i = 1; i <= secCount && i <= 7; i++)
                nodo.Cables.Add(new SxCableMap { Cable = i, SeccionAOG = i, Tren = 0 });
            for (int i = 1; i <= secCount && i <= 7; i++)
                nodo.Cables.Add(new SxCableMap { Cable = secCount + i, SeccionAOG = i, Tren = 1 });

            _cfg.Nodos.Add(nodo);
            RebuildCards();
        }

        private void AddNum(Panel parent, string label, int x, int y,
            double min, double max, double value, int decimals, Action<double> onChange)
        {
            parent.Controls.Add(new Label { Text = label, Font = Theme.FontSmall, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent, Location = new Point(x, y), AutoSize = true });
            var num = new NumericUpDown
            {
                Minimum = (decimal)min, Maximum = (decimal)max, DecimalPlaces = decimals,
                Value = (decimal)Math.Max(min, Math.Min(max, value)),
                Increment = decimals > 0 ? (decimal)Math.Pow(10, -decimals) : 1,
                Font = new Font(Theme.FontFamily, 9f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Theme.BgInput, BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(x, y + 14), Size = new Size(86, 22)
            };
            num.ValueChanged += (s, ev) => onChange((double)num.Value);
            parent.Controls.Add(num);
        }
    }
}
