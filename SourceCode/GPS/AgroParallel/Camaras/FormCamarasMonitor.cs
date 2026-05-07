// ============================================================================
// FormCamarasMonitor.cs - Tab Vivo: grid adaptativa con snapshots Hikvision
// Refresca via HTTP GET a /ISAPI/Streaming/channels/N01/picture cada N ms.
// Auth: Basic + Digest fallback (Hikvision usa Digest desde 2017+).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.Camaras
{
    public class FormCamarasMonitor : Form
    {
        private readonly CamarasConfig _cfg;
        private TableLayoutPanel _grid;
        private readonly List<PictureBox> _boxes = new List<PictureBox>();
        private readonly List<Label> _labels = new List<Label>();
        private System.Windows.Forms.Timer _timer;

        public FormCamarasMonitor(CamarasConfig cfg)
        {
            _cfg = cfg ?? CamarasConfig.Load();
            Text = "Cámaras - Vivo";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.BgBlack;
            ForeColor = Theme.TextPrimary;
            Theme.ApplyToForm(this);
            ServicePointManager.ServerCertificateValidationCallback = (a, b, c, d) => true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            BuildGrid();

            _timer = new System.Windows.Forms.Timer { Interval = Math.Max(300, _cfg.refrescoMs) };
            _timer.Tick += (s, e) => RefreshAll();
            Load += (s, e) => { _timer.Start(); RefreshAll(); };
            FormClosed += (s, e) => { try { _timer.Stop(); } catch { } };
        }

        private void BuildGrid()
        {
            if (_grid != null) Controls.Remove(_grid);
            _boxes.Clear();
            _labels.Clear();

            var activas = new List<Camara>();
            if (_cfg.camaras != null)
                foreach (var c in _cfg.camaras) if (c != null && c.activa) activas.Add(c);

            int n = activas.Count;
            int cols, rows;
            if (n <= 1) { cols = 1; rows = 1; }
            else if (n == 2) { cols = 2; rows = 1; }
            else if (n <= 4) { cols = 2; rows = 2; }
            else if (n <= 6) { cols = 3; rows = 2; }
            else if (n <= 9) { cols = 3; rows = 3; }
            else if (n <= 12) { cols = 4; rows = 3; }
            else { cols = 4; rows = 4; }

            _grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = cols,
                RowCount = rows,
                BackColor = Theme.BgBlack,
                Padding = new Padding(4),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            for (int i = 0; i < cols; i++)
                _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            for (int i = 0; i < rows; i++)
                _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));

            if (n == 0)
            {
                var lbl = new Label
                {
                    Text = "No hay cámaras configuradas.\nIr a la pestaña Config.",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Theme.TextDisabled,
                    BackColor = Theme.BgBlack,
                    Font = new Font(Theme.FontFamily, 14f)
                };
                Controls.Add(lbl);
                return;
            }

            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var cell = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(2) };
                var pb = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Black
                };
                var lbl = new Label
                {
                    Text = activas[i].nombre,
                    Font = new Font(Theme.FontFamily, 9f, FontStyle.Bold),
                    ForeColor = Color.White,
                    BackColor = Color.FromArgb(180, 0, 0, 0),
                    Dock = DockStyle.Top,
                    Height = 22,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 0, 0, 0)
                };
                cell.Controls.Add(pb);
                cell.Controls.Add(lbl);
                _grid.Controls.Add(cell, i % cols, i / cols);
                _boxes.Add(pb);
                _labels.Add(lbl);

                pb.Tag = activas[i];
            }
            Controls.Add(_grid);
        }

        private void RefreshAll()
        {
            for (int i = 0; i < _boxes.Count; i++)
            {
                var pb = _boxes[i];
                var c = pb.Tag as Camara;
                if (c == null) continue;
                int idx = i;
                ThreadPool.QueueUserWorkItem(_ => FetchAndApply(c, pb, _labels[idx]));
            }
        }

        private void FetchAndApply(Camara c, PictureBox pb, Label lbl)
        {
            try
            {
                string url = _cfg.SnapshotUrl(c);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Credentials = new NetworkCredential(c.usuario ?? "", c.clave ?? "");
                req.PreAuthenticate = true;
                req.Timeout = 4000;
                req.ReadWriteTimeout = 4000;
                req.UserAgent = "PilotX/1.0";

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                {
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    var img = Image.FromStream(ms);

                    if (pb.IsDisposed) return;
                    if (pb.InvokeRequired)
                    {
                        try
                        {
                            pb.BeginInvoke((Action)(() =>
                            {
                                try
                                {
                                    var old = pb.Image; pb.Image = img;
                                    if (old != null) old.Dispose();
                                    if (lbl != null && !lbl.IsDisposed) lbl.Text = c.nombre + "  ●";
                                }
                                catch { }
                            }));
                        }
                        catch { }
                    }
                    else
                    {
                        var old = pb.Image; pb.Image = img;
                        if (old != null) old.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (lbl == null || lbl.IsDisposed) return;
                try
                {
                    if (lbl.InvokeRequired)
                        lbl.BeginInvoke((Action)(() => { try { lbl.Text = c.nombre + "  ✕ " + Trim(ex.Message); } catch { } }));
                    else lbl.Text = c.nombre + "  ✕";
                }
                catch { }
            }
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 50 ? s.Substring(0, 50) + "…" : s;
        }
    }
}
