// ============================================================================
// FormQuantiXMonitor.cs - Monitor de dosis variable en tiempo real
// Ubicación: SourceCode/GPS/AgroParallel/QuantiX/FormQuantiXMonitor.cs
// Target: net48 (C# 7.3)
//
// Muestra: dosis actual, estado UDP, paquetes enviados, posición dentro/fuera
// del mapa de prescripción, y producto/unidad configurados.
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using AgroParallel.Common;
using AgroParallel.VistaX;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXMonitor : Form
    {
        private readonly QuantiXConfig _cfg;
        private readonly AgOpenGPS.FormGPS _parent;
        private Timer _refreshTimer;
        private Panel _canvas;

        public FormQuantiXMonitor(QuantiXConfig cfg, AgOpenGPS.FormGPS parent)
        {
            _cfg = cfg ?? QuantiXConfig.Load();
            _parent = parent;
            BuildUI();
        }

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            _canvas = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.BgBlack
            };
            _canvas.Paint += PaintMonitor;
            Controls.Add(_canvas);

            // Footer con botón Widget flotante.
            var footer = new Panel
            {
                Dock = DockStyle.Bottom, Height = 50,
                BackColor = Theme.BgToolbar
            };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(Theme.Border))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            var btnWidget = Theme.MkAccentButton("\U0001F4CA  WIDGET FLOTANTE", 200, 34);
            btnWidget.Location = new Point(20, 8);
            btnWidget.Click += (s, ev) =>
            {
                var w2 = new FormQuantiXWidget(_cfg, _parent);
                w2.Show();
            };
            footer.Controls.Add(btnWidget);
            Controls.Add(footer);
            _canvas.BringToFront();

            _refreshTimer = new Timer { Interval = 200 };
            _refreshTimer.Tick += (s, e) => _canvas.Invalidate();
            _refreshTimer.Start();
        }

        private void PaintMonitor(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.BgBlack);

            int w = _canvas.Width;
            int y = 20;

            // Título.
            using (var f = Theme.FontTitle)
            using (var br = new SolidBrush(Theme.Accent))
                g.DrawString("\u25C9  DOSIS VARIABLE", f, br, 24, y);
            y += 36;

            // Estado.
            bool enabled = _cfg.Enabled;
            bool running = false;
            double currentDose = 0;
            bool inside = false;
            int packets = 0;
            string unit = _cfg.DoseUnit ?? "";

            // Intentar leer estado del sender.
            try
            {
                if (_parent != null)
                {
                    var senderField = _parent.GetType().GetField("quantiXSender",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (senderField != null)
                    {
                        var qxSender = senderField.GetValue(_parent) as QuantiXSender;
                        if (qxSender != null)
                        {
                            running = qxSender.IsRunning;
                            packets = qxSender.PacketsSent;
                        }
                    }
                }
            }
            catch { }

            // Leer dosis actual del shapefile layer.
            try
            {
                if (_parent != null)
                {
                    var layerField = _parent.GetType().GetField("shapefileLayer",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (layerField != null)
                    {
                        object layer = layerField.GetValue(_parent);
                        if (layer != null)
                        {
                            var pDose = layer.GetType().GetProperty("CurrentDose");
                            var pInside = layer.GetType().GetProperty("CurrentInside");
                            if (pDose != null) currentDose = (double)pDose.GetValue(layer);
                            if (pInside != null) inside = (bool)pInside.GetValue(layer);
                        }
                    }
                }
            }
            catch { }

            // KPIs.
            int kpiY = y;
            var kpiRect = new Rectangle(20, kpiY, w - 40, 80);
            Theme.FillRoundedRect(g, kpiRect, Theme.BgCard, Theme.BorderRadius);
            Theme.DrawRoundedBorder(g, kpiRect, Theme.Border, Theme.BorderRadius);

            // Dosis grande.
            using (var f = new Font(Theme.FontFamily, 32f, FontStyle.Bold))
            using (var br = new SolidBrush(Theme.TextPrimary))
                g.DrawString(currentDose.ToString("F1", CultureInfo.InvariantCulture), f, br, 34, kpiY + 10);

            using (var f = new Font(Theme.FontFamily, 12f))
            using (var br = new SolidBrush(Theme.TextSecondary))
                g.DrawString(string.IsNullOrEmpty(unit) ? "kg/ha" : unit, f, br, 160, kpiY + 30);

            // Estado dentro/fuera.
            string estadoPos = inside ? "\u25CF  DENTRO DEL MAPA" : "\u25CB  FUERA DEL MAPA";
            Color estadoColor = inside ? Theme.Accent : Theme.Warning;
            using (var f = new Font(Theme.FontFamily, 10f, FontStyle.Bold))
            using (var br = new SolidBrush(estadoColor))
                g.DrawString(estadoPos, f, br, w / 2, kpiY + 16);

            // Paquetes enviados.
            using (var f = new Font(Theme.FontFamily, 10f))
            using (var br = new SolidBrush(Theme.TextSecondary))
                g.DrawString("Paquetes: " + packets.ToString("N0", CultureInfo.InvariantCulture), f, br, w / 2, kpiY + 40);

            // LED de estado UDP.
            string ledText = !enabled ? "\u25CB  DESHABILITADO"
                : running ? "\u25CF  UDP ACTIVO" : "\u25CB  UDP DETENIDO";
            Color ledColor = !enabled ? Theme.TextFaint
                : running ? Theme.Accent : Theme.Error;
            using (var f = new Font(Theme.FontFamily, 10f, FontStyle.Bold))
            using (var br = new SolidBrush(ledColor))
                g.DrawString(ledText, f, br, w / 2, kpiY + 58);

            y += 100;

            // Config summary.
            y += 10;
            DrawConfigRow(g, 24, y, "Host UDP:", _cfg.UdpHost + ":" + _cfg.UdpPort); y += 26;
            DrawConfigRow(g, 24, y, "Sample Rate:", _cfg.SampleRateHz + " Hz"); y += 26;
            DrawConfigRow(g, 24, y, "Valor fuera:", _cfg.OutsideValue.ToString(CultureInfo.InvariantCulture)); y += 26;
            DrawConfigRow(g, 24, y, "Solo en cambio:", _cfg.SendOnlyOnChange ? "S\u00ED" : "No"); y += 26;
            DrawConfigRow(g, 24, y, "Incluir posici\u00F3n:", _cfg.IncludePosition ? "S\u00ED" : "No"); y += 26;
        }

        private void DrawConfigRow(Graphics g, int x, int y, string label, string value)
        {
            using (var f = Theme.FontBody)
            {
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString(label, f, br, x, y);
                using (var br = new SolidBrush(Theme.TextSecondary))
                    g.DrawString(value, f, br, x + 160, y);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_refreshTimer != null) { _refreshTimer.Stop(); _refreshTimer.Dispose(); }
        }
    }
}
