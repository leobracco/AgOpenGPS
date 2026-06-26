// ============================================================================
// FormVistaXSensores.cs - Mapeo de sensores a surcos (Config – Sensores)
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXSensores.cs
// Target: net48 (C# 7.3)
//
// Grilla visual donde cada celda = 1 surco. Muestra nodo asignado (últimos
// 4 del UID) y canal (c1-c8). Verde oscuro = asignado, gris = vacío.
// Botón "Autonumerar Tren" para asignación rápida.
// Tabs: Siembra | Otros Sensores | Cables Libres.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.VistaX
{
    public class FormVistaXSensores : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CBgCard { get { return Theme.BgCard; } }
        private static Color CBgCellAssigned { get { return Theme.AccentDark; } }
        private static Color CBgCellEmpty { get { return Color.FromArgb(28, 28, 32); } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CAccentDim { get { return Theme.AccentDim; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CTextFaint { get { return Theme.TextFaint; } }
        private static Color CBorder { get { return Theme.Border; } }
        private static Color CRed { get { return Theme.Error; } }
        private static Color CYellow { get { return Theme.Warning; } }
        private static Color CFertiLinea { get { return Theme.FertiLinea; } }
        private static Color CFertiCostado { get { return Theme.FertiCostado; } }
        private static Color CHerramienta { get { return Theme.Herramienta; } }

        private readonly VistaXConfig _cfg;
        private ImplementoConfig _implemento;
        private bool _dirty;

        private TabControl _tabs;
        private Panel _gridPanel;        // Siembra tab content
        private Panel _otrosPanel;       // Otros Sensores tab
        private Panel _libresPanel;      // Cables Libres tab
        private Label _lblStats;

        public bool Changed { get; private set; }

        public FormVistaXSensores(VistaXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadData();
            RebuildAll();
        }

        // =====================================================================
        // UI
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "VistaX — Sensores";
            Size = new Size(1100, 720);
            MinimumSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CBgDark;
            ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, ev) => { if (ev.KeyCode == Keys.Escape) Close(); };
            Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // ── Sub-header ──────────────────────────────────────────────
            var subHeader = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = CBgDark };
            subHeader.Controls.Add(new Label
            {
                Text = "\U0001F50C",
                Font = new Font("Segoe UI Emoji", 20f, FontStyle.Bold),
                ForeColor = CAccent, Location = new Point(22, 20),
                Size = new Size(40, 40), BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            });
            subHeader.Controls.Add(new Label
            {
                Text = "SENSORES",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CText, Location = new Point(74, 18),
                AutoSize = true, BackColor = Color.Transparent
            });
            subHeader.Controls.Add(new Label
            {
                Text = "Mapeo de sensores a surcos \u2014 cada celda = 1 bajada de semilla",
                Font = new Font("Segoe UI", 9f), ForeColor = CTextDim,
                Location = new Point(74, 46), AutoSize = true, BackColor = Color.Transparent
            });
            _lblStats = new Label
            {
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.Transparent,
                AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            subHeader.Controls.Add(_lblStats);
            subHeader.Resize += (s, ev) =>
                _lblStats.Location = new Point(subHeader.Width - _lblStats.Width - 24, 46);
            Controls.Add(subHeader);

            // ── Top bar ─────────────────────────────────────────────────
            var topBar = MkTopBar();
            Controls.Add(topBar);

            // ── Tabs ────────────────────────────────────────────────────
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            // WinForms TabControl doesn't support dark themes easily — we
            // use OwnerDrawFixed for a dark tab strip.
            _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            _tabs.DrawItem += PaintTab;
            _tabs.ItemSize = new Size(160, 34);
            _tabs.SizeMode = TabSizeMode.Fixed;

            var tabSiembra = new TabPage("Siembra") { BackColor = CBgDark };
            _gridPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = CBgDark };
            tabSiembra.Controls.Add(_gridPanel);
            _tabs.TabPages.Add(tabSiembra);

            var tabOtros = new TabPage("Otros Sensores") { BackColor = CBgDark };
            _otrosPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = CBgDark };
            tabOtros.Controls.Add(_otrosPanel);
            _tabs.TabPages.Add(tabOtros);

            var tabLibres = new TabPage("Cables Libres") { BackColor = CBgDark };
            _libresPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = CBgDark };
            tabLibres.Controls.Add(_libresPanel);
            _tabs.TabPages.Add(tabLibres);

            _tabs.SelectedIndexChanged += (s, ev) => RebuildAll();

            Controls.Add(_tabs);
            _tabs.BringToFront();

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = CBgPanel };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            footer.Controls.Add(new Label
            {
                Text = "VistaX nativo \u00B7 Sensores",
                Font = new Font("Segoe UI", 8.5f), ForeColor = CTextFaint,
                Location = new Point(18, 17), AutoSize = true, BackColor = Color.Transparent
            });

            var btnAuto = MkPillButton("\u26A1  AUTONUMERAR TREN", CAccent, CBgDark);
            btnAuto.Size = new Size(200, 34);
            btnAuto.Location = new Point(180, 8);
            btnAuto.Click += (s, ev) => AutonumerarTren();
            footer.Controls.Add(btnAuto);

            var btnSave = MkPillButton("\u2713  GUARDAR", CAccent, CBgDark);
            btnSave.Size = new Size(120, 34);
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Click += (s, ev) => SaveAndClose();
            footer.Controls.Add(btnSave);

            var btnCancel = new Button
            {
                Text = "\u2715 CERRAR", FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand, Size = new Size(110, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 30, 30);
            footer.Controls.Add(btnCancel);
            footer.Resize += (s, ev) =>
            {
                btnCancel.Location = new Point(footer.Width - btnCancel.Width - 18, 8);
                btnSave.Location = new Point(btnCancel.Left - btnSave.Width - 10, 8);
            };
            Controls.Add(footer);
            CancelButton = btnCancel;
        }

        private Panel MkTopBar()
        {
            var topBar = new Panel
            {
                Dock = DockStyle.Top, Height = 56, BackColor = CBgPanel, Cursor = Cursors.SizeAll
            };
            topBar.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };
            topBar.MouseDown += (s, ev) => { if (ev.Button == MouseButtons.Left) { _dragging = true; _dragStart = ev.Location; } };
            topBar.MouseMove += (s, ev) => { if (_dragging) Location = new Point(Location.X + ev.X - _dragStart.X, Location.Y + ev.Y - _dragStart.Y); };
            topBar.MouseUp += (s, ev) => _dragging = false;

            topBar.Controls.Add(new Label { Text = "VistaX", Font = new Font("Segoe UI", 18f, FontStyle.Bold), ForeColor = CText, Location = new Point(24, 14), AutoSize = true, BackColor = Color.Transparent });
            topBar.Controls.Add(new Label { Text = "CONFIGURACION", Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = CTextDim, Location = new Point(116, 20), AutoSize = true, BackColor = Color.Transparent });

            // Cerrar: chrome nativo del Form.
            return topBar;
        }

        private void PaintTab(object sender, DrawItemEventArgs e)
        {
            var tab = _tabs.TabPages[e.Index];
            bool selected = _tabs.SelectedIndex == e.Index;
            Color bg = selected ? CBgDark : CBgPanel;
            Color fg = selected ? CAccent : CTextDim;

            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);
            using (var f = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (var br = new SolidBrush(fg))
            {
                var sz = e.Graphics.MeasureString(tab.Text, f);
                e.Graphics.DrawString(tab.Text, f, br,
                    e.Bounds.X + (e.Bounds.Width - sz.Width) / 2f,
                    e.Bounds.Y + (e.Bounds.Height - sz.Height) / 2f);
            }
            if (selected)
            {
                using (var pen = new Pen(CAccent, 2))
                    e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }
        }

        // =====================================================================
        // Data
        // =====================================================================

        private void LoadData()
        {
            try { _implemento = _cfg.LoadImplemento(); }
            catch { _implemento = new ImplementoConfig(); }
        }

        // =====================================================================
        // Rebuild grids
        // =====================================================================

        private void RebuildAll()
        {
            int idx = _tabs.SelectedIndex;
            if (idx == 0) RebuildSiembraGrid();
            else if (idx == 1) RebuildOtrosGrid();
            else if (idx == 2) RebuildLibresGrid();
            UpdateStats();
        }

        private void UpdateStats()
        {
            if (_implemento == null || _implemento.MapeoSensores == null)
            {
                _lblStats.Text = "";
                return;
            }
            int total = _implemento.MapeoSensores.Count;
            int semilla = 0, otros = 0;
            foreach (var s in _implemento.MapeoSensores)
            {
                if (s == null) continue;
                if (string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                    semilla++;
                else
                    otros++;
            }
            _lblStats.Text = semilla + " semilla \u00B7 " + otros + " otros \u00B7 " + total + " total";
        }

        // ── Siembra tab ─────────────────────────────────────────────────

        private void RebuildSiembraGrid()
        {
            _gridPanel.SuspendLayout();
            _gridPanel.Controls.Clear();

            if (_implemento == null || _implemento.MapeoSensores == null)
            {
                _gridPanel.Controls.Add(MkEmptyLabel("No hay sensores configurados"));
                _gridPanel.ResumeLayout();
                return;
            }

            // Collect all tren IDs from TrenConfig AND from sensors.
            var trenConfigs = new Dictionary<int, TrenConfig>();
            if (_implemento.Trenes != null)
                foreach (var t in _implemento.Trenes)
                    if (t != null) trenConfigs[t.Id] = t;

            // Group sensors by tren, filter semilla.
            var byTren = new SortedDictionary<int, List<SensorConfig>>();

            // Ensure all configured trenes appear even if they have no sensors yet.
            foreach (var tc in trenConfigs.Values)
            {
                if (!byTren.ContainsKey(tc.Id))
                    byTren[tc.Id] = new List<SensorConfig>();
            }

            foreach (var s in _implemento.MapeoSensores)
            {
                if (s == null) continue;
                if (!string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase)) continue;
                int tren = s.Tren > 0 ? s.Tren : 1;
                List<SensorConfig> list;
                if (!byTren.TryGetValue(tren, out list))
                {
                    list = new List<SensorConfig>();
                    byTren[tren] = list;
                }
                list.Add(s);
            }

            if (byTren.Count == 0)
            {
                _gridPanel.Controls.Add(MkEmptyLabel("No hay trenes ni sensores de semilla configurados"));
                _gridPanel.ResumeLayout();
                return;
            }

            int yOffset = 10;
            int gridWidth = _gridPanel.ClientSize.Width - 40;

            foreach (var kv in byTren)
            {
                int trenId = kv.Key;
                var sensors = kv.Value;
                sensors.Sort((a, b) => a.Bajada.CompareTo(b.Bajada));

                TrenConfig tc;
                string trenName = trenConfigs.TryGetValue(trenId, out tc)
                    ? (tc.Nombre ?? "Tren " + trenId).ToUpperInvariant()
                    : (trenId == 1 ? "DELANTERO" : trenId == 2 ? "TRASERO" : "TREN " + trenId);

                int maxBajada = 0;
                foreach (var s in sensors)
                    if (s.Bajada > maxBajada) maxBajada = s.Bajada;
                if (tc != null && tc.Surcos > maxBajada) maxBajada = tc.Surcos;
                if (maxBajada <= 0) maxBajada = 1;

                // Tren label.
                var lblTren = new Label
                {
                    Text = trenName + "  (" + sensors.Count + " sensores, " + maxBajada + " surcos)",
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = CAccent, BackColor = Color.Transparent,
                    Location = new Point(14, yOffset), AutoSize = true
                };
                _gridPanel.Controls.Add(lblTren);
                yOffset += 28;

                // Build indexed lookup.
                var indexed = new Dictionary<int, SensorConfig>();
                foreach (var s in sensors) indexed[s.Bajada] = s;

                // Grid cells — adapt columns to available width.
                int cellW = 62, cellH = 56, gap = 4;
                int cols = Math.Max(1, Math.Min(maxBajada, gridWidth / (cellW + gap)));
                int rows = (int)Math.Ceiling((double)maxBajada / cols);

                for (int bajada = 1; bajada <= maxBajada; bajada++)
                {
                    int idx = bajada - 1;
                    int col = idx % cols;
                    int row = idx / cols;
                    int cx = 14 + col * (cellW + gap);
                    int cy = yOffset + row * (cellH + gap);

                    SensorConfig sensor;
                    bool assigned = indexed.TryGetValue(bajada, out sensor);

                    var cell = new Panel
                    {
                        Size = new Size(cellW, cellH),
                        Location = new Point(cx, cy),
                        BackColor = assigned ? CBgCellAssigned : CBgCellEmpty,
                        Tag = new CellTag { Tren = trenId, Bajada = bajada, Sensor = sensor }
                    };
                    cell.Paint += PaintSensorCell;
                    cell.Cursor = Cursors.Hand;
                    cell.Click += OnCellClick;
                    _gridPanel.Controls.Add(cell);
                }

                yOffset += rows * (cellH + gap) + 16;
            }

            // Spacer to ensure AutoScroll covers all content.
            _gridPanel.Controls.Add(new Panel
            {
                Location = new Point(0, yOffset),
                Size = new Size(1, 10),
                BackColor = Color.Transparent
            });

            _gridPanel.ResumeLayout();
        }

        private class CellTag
        {
            public int Tren;
            public int Bajada;
            public SensorConfig Sensor;
        }

        private void PaintSensorCell(object sender, PaintEventArgs e)
        {
            var cell = (Panel)sender;
            var tag = (CellTag)cell.Tag;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Border.
            Color borderColor = tag.Sensor != null ? CAccentDim : CBorder;
            using (var pen = new Pen(borderColor))
                g.DrawRectangle(pen, 0, 0, cell.Width - 1, cell.Height - 1);

            // Surco number (top-left).
            using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var br = new SolidBrush(tag.Sensor != null ? CAccent : CTextFaint))
                g.DrawString(tag.Bajada.ToString(CultureInfo.InvariantCulture), f, br, 4, 3);

            if (tag.Sensor != null)
            {
                // Node UID (last 4 chars).
                string uid4 = tag.Sensor.Uid ?? "";
                if (uid4.Length > 4) uid4 = uid4.Substring(uid4.Length - 4);
                using (var f = new Font("Consolas", 9f, FontStyle.Bold))
                using (var br = new SolidBrush(CText))
                    g.DrawString(uid4, f, br, 4, 20);

                // Channel.
                string ch = "c" + tag.Sensor.Cable.ToString(CultureInfo.InvariantCulture);
                using (var f = new Font("Segoe UI", 7.5f))
                using (var br = new SolidBrush(CTextDim))
                    g.DrawString(ch, f, br, 4, 38);
            }
            else
            {
                // Empty cell.
                using (var f = new Font("Segoe UI", 8f, FontStyle.Italic))
                using (var br = new SolidBrush(Color.FromArgb(50, 50, 55)))
                    g.DrawString("vac\u00EDo", f, br, 4, 22);
            }
        }

        private void OnCellClick(object sender, EventArgs e)
        {
            var cell = (Panel)sender;
            var tag = (CellTag)cell.Tag;

            using (var dlg = new FormEditSensor(tag.Tren, tag.Bajada, tag.Sensor, GetNodeUids(),
                _implemento != null ? _implemento.MapeoSensores : null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                if (dlg.Deleted && tag.Sensor != null)
                {
                    _implemento.MapeoSensores.Remove(tag.Sensor);
                    _dirty = true;
                }
                else if (dlg.ResultSensor != null)
                {
                    if (tag.Sensor != null)
                    {
                        // Update existing — copiar todos los campos editables.
                        tag.Sensor.Uid = dlg.ResultSensor.Uid;
                        tag.Sensor.Cable = dlg.ResultSensor.Cable;
                        tag.Sensor.Pin = dlg.ResultSensor.Pin;
                        tag.Sensor.IsActive = dlg.ResultSensor.IsActive;
                        tag.Sensor.Tipo = dlg.ResultSensor.Tipo;
                        tag.Sensor.Nombre = dlg.ResultSensor.Nombre;
                        tag.Sensor.SurcoDesde = dlg.ResultSensor.SurcoDesde;
                        tag.Sensor.SurcoHasta = dlg.ResultSensor.SurcoHasta;
                        tag.Sensor.SeccionAOG = dlg.ResultSensor.SeccionAOG;
                    }
                    else
                    {
                        // Add new.
                        _implemento.MapeoSensores.Add(dlg.ResultSensor);
                    }
                    _dirty = true;
                }
            }

            RebuildAll();
        }

        private List<string> GetNodeUids()
        {
            var uids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_implemento != null && _implemento.MapeoSensores != null)
                foreach (var s in _implemento.MapeoSensores)
                    if (s != null && !string.IsNullOrEmpty(s.Uid))
                        uids.Add(s.Uid);
            var list = uids.ToList();
            list.Sort();
            return list;
        }

        // ── Otros Sensores tab ──────────────────────────────────────────

        private void RebuildOtrosGrid()
        {
            _otrosPanel.SuspendLayout();
            _otrosPanel.Controls.Clear();

            if (_implemento == null || _implemento.MapeoSensores == null)
            {
                _otrosPanel.Controls.Add(MkEmptyLabel("No hay sensores configurados"));
                _otrosPanel.ResumeLayout();
                return;
            }

            var otros = new List<SensorConfig>();
            foreach (var s in _implemento.MapeoSensores)
            {
                if (s == null) continue;
                if (!string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                    otros.Add(s);
            }

            if (otros.Count == 0)
            {
                _otrosPanel.Controls.Add(MkEmptyLabel("No hay sensores auxiliares (ferti, herramienta, etc.)"));
                _otrosPanel.ResumeLayout();
                return;
            }

            otros.Sort((a, b) =>
            {
                int c = (a.Tren > 0 ? a.Tren : 1).CompareTo(b.Tren > 0 ? b.Tren : 1);
                if (c != 0) return c;
                c = a.Bajada.CompareTo(b.Bajada);
                if (c != 0) return c;
                return string.Compare(a.Tipo ?? "", b.Tipo ?? "", StringComparison.OrdinalIgnoreCase);
            });

            int y = 10;
            foreach (var sensor in otros)
            {
                var card = MkOtroSensorCard(sensor, y);
                _otrosPanel.Controls.Add(card);
                y += 50;
            }

            _otrosPanel.ResumeLayout();
        }

        private Panel MkOtroSensorCard(SensorConfig sensor, int y)
        {
            Color tipoColor = GetTipoColor(sensor.Tipo);
            bool isGlobal = TipoSensor.EsGlobal(sensor.Tipo);
            var card = new Panel
            {
                Size = new Size(_otrosPanel.Width - 40, 42),
                Location = new Point(14, y),
                BackColor = CBgCard,
                Cursor = Cursors.Hand,
                Tag = sensor
            };
            card.Paint += (s, ev) =>
            {
                using (var acc = new SolidBrush(tipoColor))
                    ev.Graphics.FillRectangle(acc, 0, 0, 4, card.Height);
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            card.Click += OnOtroSensorClick;

            string location = isGlobal ? "GLOBAL" : "T" + (sensor.Tren > 0 ? sensor.Tren : 1) + " S" + sensor.Bajada;
            card.Controls.Add(new Label
            {
                Text = location,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CText, Location = new Point(14, 10),
                AutoSize = true, BackColor = Color.Transparent
            });

            card.Controls.Add(new Label
            {
                Text = TipoSensor.NombreAmigable(sensor.Tipo),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = tipoColor, Location = new Point(100, 13),
                AutoSize = true, BackColor = Color.Transparent
            });

            string uid4 = sensor.Uid ?? "";
            if (uid4.Length > 4) uid4 = uid4.Substring(uid4.Length - 4);
            string asignacion = string.IsNullOrEmpty(sensor.Uid) || sensor.Uid == "UNASSIGNED"
                ? "SIN ASIGNAR" : uid4 + " c" + sensor.Cable;
            card.Controls.Add(new Label
            {
                Text = asignacion,
                Font = new Font("Consolas", 9f),
                ForeColor = asignacion == "SIN ASIGNAR" ? Color.FromArgb(255, 234, 0) : CTextDim,
                Location = new Point(300, 12),
                AutoSize = true, BackColor = Color.Transparent
            });

            return card;
        }

        private static Color GetTipoColor(string tipo)
        {
            return TipoSensor.GetColor(tipo);
        }

        private void OnOtroSensorClick(object sender, EventArgs e)
        {
            var card = (Panel)sender;
            var sensor = (SensorConfig)card.Tag;
            bool isGlobal = TipoSensor.EsGlobal(sensor.Tipo);

            // Para sensores por surco (ferti), usar el editor normal.
            // Para globales (turbina/tolva/herramienta), editor simplificado.
            if (!isGlobal)
            {
                using (var dlg = new FormEditSensor(
                    sensor.Tren > 0 ? sensor.Tren : 1,
                    sensor.Bajada, sensor, GetNodeUids(),
                    _implemento != null ? _implemento.MapeoSensores : null))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (dlg.Deleted)
                    {
                        _implemento.MapeoSensores.Remove(sensor);
                        _dirty = true;
                    }
                    else if (dlg.ResultSensor != null)
                    {
                        sensor.Uid = dlg.ResultSensor.Uid;
                        sensor.Cable = dlg.ResultSensor.Cable;
                        sensor.Pin = dlg.ResultSensor.Pin;
                        sensor.Bajada = dlg.ResultSensor.Bajada;
                        sensor.Tren = dlg.ResultSensor.Tren;
                        sensor.IsActive = dlg.ResultSensor.IsActive;
                        sensor.SurcoDesde = dlg.ResultSensor.SurcoDesde;
                        sensor.SurcoHasta = dlg.ResultSensor.SurcoHasta;
                        _dirty = true;
                    }
                }
            }
            else
            {
                // Editor simplificado para sensores globales: solo nodo + cable.
                using (var dlg = new FormEditGlobalSensor(sensor, GetNodeUids(),
                    _implemento != null ? _implemento.MapeoSensores : null))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (dlg.Deleted)
                    {
                        _implemento.MapeoSensores.Remove(sensor);
                        _dirty = true;
                    }
                    else
                    {
                        _dirty = true;
                    }
                }
            }
            RebuildAll();
        }

        // ── Cables Libres tab ───────────────────────────────────────────

        private void RebuildLibresGrid()
        {
            _libresPanel.SuspendLayout();
            _libresPanel.Controls.Clear();

            if (_implemento == null || _implemento.MapeoSensores == null)
            {
                _libresPanel.Controls.Add(MkEmptyLabel("No hay sensores configurados"));
                _libresPanel.ResumeLayout();
                return;
            }

            // Find all node UIDs and their used cables.
            var usedCables = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _implemento.MapeoSensores)
            {
                if (s == null || string.IsNullOrEmpty(s.Uid)) continue;
                HashSet<int> set;
                if (!usedCables.TryGetValue(s.Uid, out set))
                {
                    set = new HashSet<int>();
                    usedCables[s.Uid] = set;
                }
                set.Add(s.Cable);
            }

            if (usedCables.Count == 0)
            {
                _libresPanel.Controls.Add(MkEmptyLabel("No hay nodos configurados"));
                _libresPanel.ResumeLayout();
                return;
            }

            int y = 10;
            bool anyFree = false;
            foreach (var kv in usedCables.OrderBy(x => x.Key))
            {
                string uid = kv.Key;
                var used = kv.Value;
                var free = new List<int>();
                for (int c = 1; c <= 8; c++)
                    if (!used.Contains(c)) free.Add(c);

                if (free.Count == 0) continue;
                anyFree = true;

                string uid4 = uid.Length > 4 ? uid.Substring(uid.Length - 4) : uid;
                var lbl = new Label
                {
                    Text = uid + "  \u2014  " + free.Count + " cable(s) libre(s): "
                        + string.Join(", ", free.Select(c => "c" + c).ToArray()),
                    Font = new Font("Segoe UI", 10f),
                    ForeColor = CText, BackColor = CBgCard,
                    Location = new Point(14, y),
                    Size = new Size(_libresPanel.Width - 40, 36),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(12, 0, 0, 0)
                };
                _libresPanel.Controls.Add(lbl);
                y += 44;
            }

            if (!anyFree)
                _libresPanel.Controls.Add(MkEmptyLabel("Todos los cables (c1-c8) est\u00E1n asignados en todos los nodos"));

            _libresPanel.ResumeLayout();
        }

        // =====================================================================
        // Autonumerar
        // =====================================================================

        private void AutonumerarTren()
        {
            if (_implemento == null) return;
            if (_implemento.Trenes == null || _implemento.Trenes.Count == 0)
            {
                MessageBox.Show(this, "Configur\u00E1 los trenes primero desde 'Trenes de siembra'.",
                    "Sensores", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Pick which tren to auto-number.
            var names = new List<string>();
            foreach (var t in _implemento.Trenes)
                names.Add("Tren " + t.Id + " - " + (t.Nombre ?? "?") + " (" + t.Surcos + " surcos)");

            int selectedTren = 0;
            if (_implemento.Trenes.Count == 1)
            {
                selectedTren = _implemento.Trenes[0].Id;
            }
            else
            {
                // Simple selection dialog.
                using (var dlg = new FormSelectTren(_implemento.Trenes))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    selectedTren = dlg.SelectedTrenId;
                }
            }

            if (selectedTren <= 0) return;

            // Find the tren config.
            TrenConfig tc = null;
            foreach (var t in _implemento.Trenes)
                if (t.Id == selectedTren) { tc = t; break; }
            if (tc == null || tc.Surcos <= 0) return;

            // Get all distinct UIDs from the implemento.
            var uids = GetNodeUids();
            if (uids.Count == 0)
            {
                MessageBox.Show(this, "No hay nodos configurados. Agreg\u00E1 nodos primero.",
                    "Sensores", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Remove existing semilla sensors for this tren.
            _implemento.MapeoSensores.RemoveAll(s =>
                s != null && (s.Tren > 0 ? s.Tren : 1) == selectedTren
                && string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase));

            // Auto-assign: round-robin across nodes, cables 1-8.
            int uidIdx = 0, cable = 1;
            for (int bajada = 1; bajada <= tc.Surcos; bajada++)
            {
                _implemento.MapeoSensores.Add(new SensorConfig
                {
                    Uid = uids[uidIdx],
                    Cable = cable,
                    Pin = cable - 1,
                    Bajada = bajada,
                    Tipo = "semilla",
                    Nombre = "Surco " + bajada,
                    Tren = selectedTren,
                    IsActive = true
                });

                cable++;
                if (cable > 8)
                {
                    cable = 1;
                    uidIdx++;
                    if (uidIdx >= uids.Count) uidIdx = 0;
                }
            }

            _dirty = true;
            RebuildAll();
        }

        // =====================================================================
        // Save
        // =====================================================================

        private void SaveAndClose()
        {
            if (!_dirty) { Close(); return; }

            try
            {
                string path = _cfg.ImplementoJsonPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var opts = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };
                    File.WriteAllText(path, JsonSerializer.Serialize(_implemento, opts));
                    Changed = true;
                }
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error al guardar: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static Button MkPillButton(string text, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat,
                BackColor = bg, ForeColor = fg,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private Label MkEmptyLabel(string msg)
        {
            return new Label
            {
                Text = msg, Font = new Font("Segoe UI", 10f),
                ForeColor = CTextDim, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
        }
    }

    // =========================================================================
    // FormEditSensor - Edit a single sensor assignment
    // =========================================================================

    internal class FormEditSensor : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CBorder { get { return Theme.Border; } }
        private static Color CRed { get { return Theme.Error; } }

        public SensorConfig ResultSensor { get; private set; }
        public bool Deleted { get; private set; }
        private readonly List<SensorConfig> _allSensors;
        private readonly SensorConfig _existing;

        public FormEditSensor(int tren, int bajada, SensorConfig existing, List<string> knownUids)
            : this(tren, bajada, existing, knownUids, null) { }

        public FormEditSensor(int tren, int bajada, SensorConfig existing, List<string> knownUids,
            List<SensorConfig> allSensors)
        {
            _allSensors = allSensors;
            _existing = existing;
            Text = "Editar Sensor";
            Size = new Size(420, 340);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = CBgDark; ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            bool isNew = existing == null;
            string title = isNew ? "ASIGNAR SENSOR" : "EDITAR SENSOR";

            Controls.Add(new Label
            {
                Text = "\U0001F50C  " + title,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = CText, Location = new Point(24, 16),
                AutoSize = true, BackColor = Color.Transparent
            });
            Controls.Add(new Label
            {
                Text = "Tren " + tren + " \u2014 Surco " + bajada,
                Font = new Font("Segoe UI", 10f), ForeColor = CTextDim,
                Location = new Point(24, 48), AutoSize = true, BackColor = Color.Transparent
            });

            // UID combo.
            Controls.Add(new Label
            {
                Text = "UID del nodo:", Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim, Location = new Point(24, 82), AutoSize = true
            });
            var cboUid = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = CBgPanel, ForeColor = CAccent,
                Font = new Font("Consolas", 11f),
                Location = new Point(24, 104), Size = new Size(Width - 48, 28)
            };
            if (knownUids != null)
                foreach (var u in knownUids) cboUid.Items.Add(u);
            if (existing != null && !string.IsNullOrEmpty(existing.Uid))
                cboUid.Text = existing.Uid;
            else if (cboUid.Items.Count > 0)
                cboUid.SelectedIndex = 0;
            Controls.Add(cboUid);

            // Label de cables ocupados para este nodo.
            var lblOcupados = new Label
            {
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(85, 85, 90),
                BackColor = Color.Transparent,
                Location = new Point(24, 132), AutoSize = true
            };
            Controls.Add(lblOcupados);
            Action updateOcupados = () =>
            {
                string uid = cboUid.Text.Trim();
                if (_allSensors == null || string.IsNullOrEmpty(uid))
                { lblOcupados.Text = ""; return; }
                var usados = new List<int>();
                foreach (var s in _allSensors)
                {
                    if (s == null || s == _existing) continue;
                    if (string.Equals(s.Uid, uid, StringComparison.OrdinalIgnoreCase) && s.Cable > 0)
                        usados.Add(s.Cable);
                }
                usados.Sort();
                lblOcupados.Text = usados.Count > 0
                    ? "Cables ocupados: " + string.Join(", ", usados.ConvertAll(c => "c" + c).ToArray())
                    : "Todos los cables libres";
            };
            cboUid.TextChanged += (s2, ev2) => updateOcupados();
            cboUid.SelectedIndexChanged += (s2, ev2) => updateOcupados();
            updateOcupados();

            // Cable.
            Controls.Add(new Label
            {
                Text = "Cable (1-8):", Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim, Location = new Point(24, 150), AutoSize = true
            });
            var numCable = new NumericUpDown
            {
                Minimum = 1, Maximum = 8,
                Value = existing != null ? Math.Max(1, Math.Min(8, existing.Cable)) : 1,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(24, 172), Size = new Size(80, 28)
            };
            Controls.Add(numCable);

            // Tipo de sensor.
            Controls.Add(new Label
            {
                Text = "Tipo:", Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim, Location = new Point(140, 150), AutoSize = true
            });
            var cboTipo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CBgPanel, ForeColor = CText,
                Font = new Font("Segoe UI", 10f),
                Location = new Point(140, 172), Size = new Size(230, 26)
            };
            foreach (var t in TipoSensor.Todos)
                cboTipo.Items.Add(TipoSensor.NombreAmigable(t));
            string tipoActual = existing != null ? existing.Tipo : TipoSensor.Semilla;
            int tipoIdx = Array.IndexOf(TipoSensor.Todos, tipoActual);
            cboTipo.SelectedIndex = tipoIdx >= 0 ? tipoIdx : 0;
            Controls.Add(cboTipo);

            // Active checkbox.
            var chkActive = new CheckBox
            {
                Text = "Activo", Checked = existing == null || existing.IsActive,
                Font = new Font("Segoe UI", 9.5f), ForeColor = CText,
                BackColor = Color.Transparent,
                Location = new Point(24, 200), AutoSize = true
            };
            Controls.Add(chkActive);

            // Rango de surcos (para 1:N).
            Controls.Add(new Label
            {
                Text = "Rango de surcos (1:N, vac\u00EDo = solo este):",
                Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(85, 85, 90),
                Location = new Point(140, 200), AutoSize = true, BackColor = Color.Transparent
            });
            var numDesde = new NumericUpDown
            {
                Minimum = 0, Maximum = 200,
                Value = existing != null ? Math.Max(0, existing.SurcoDesde) : 0,
                Font = new Font("Segoe UI", 9f), ForeColor = CAccent,
                BackColor = Color.FromArgb(15, 15, 15), BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(140, 218), Size = new Size(60, 22)
            };
            Controls.Add(numDesde);
            Controls.Add(new Label
            {
                Text = "a", Font = new Font("Segoe UI", 9f), ForeColor = CTextDim,
                Location = new Point(205, 220), AutoSize = true, BackColor = Color.Transparent
            });
            var numHasta = new NumericUpDown
            {
                Minimum = 0, Maximum = 200,
                Value = existing != null ? Math.Max(0, existing.SurcoHasta) : 0,
                Font = new Font("Segoe UI", 9f), ForeColor = CAccent,
                BackColor = Color.FromArgb(15, 15, 15), BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(222, 218), Size = new Size(60, 22)
            };
            Controls.Add(numHasta);

            // Buttons.
            Size = new Size(420, 390);
            var btnOk = MkBtn("\u2713  " + (isNew ? "ASIGNAR" : "GUARDAR"), CAccent, CBgDark);
            btnOk.Size = new Size(130, 36);
            btnOk.Location = new Point(Width - 154, 310);
            btnOk.Click += (s, ev) =>
            {
                string uid = cboUid.Text.Trim();
                if (string.IsNullOrEmpty(uid)) return;
                int cable = (int)numCable.Value;

                // Validar que el cable no esté ocupado en este nodo.
                if (_allSensors != null)
                {
                    foreach (var ss in _allSensors)
                    {
                        if (ss == null || ss == _existing) continue;
                        if (string.Equals(ss.Uid, uid, StringComparison.OrdinalIgnoreCase)
                            && ss.Cable == cable)
                        {
                            MessageBox.Show(this,
                                "El cable c" + cable + " del nodo " + uid + " ya est\u00E1 asignado a:\n"
                                + TipoSensor.NombreAmigable(ss.Tipo) + " T" + ss.Tren + " S" + ss.Bajada
                                + "\n\nEleg\u00ED otro cable.",
                                "Cable Ocupado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                }

                string selectedTipo = cboTipo.SelectedIndex >= 0 && cboTipo.SelectedIndex < TipoSensor.Todos.Length
                    ? TipoSensor.Todos[cboTipo.SelectedIndex] : TipoSensor.Semilla;

                ResultSensor = new SensorConfig
                {
                    Uid = uid,
                    Cable = (int)numCable.Value,
                    Pin = (int)numCable.Value - 1,
                    Bajada = bajada,
                    Tipo = selectedTipo,
                    Nombre = TipoSensor.NombreAmigable(selectedTipo) + " S" + bajada,
                    Tren = tren,
                    IsActive = chkActive.Checked,
                    SurcoDesde = (int)numDesde.Value,
                    SurcoHasta = (int)numHasta.Value
                };
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnOk);

            if (!isNew)
            {
                var btnDel = MkBtn("\U0001F5D1  QUITAR", Color.FromArgb(50, 25, 25), CRed);
                btnDel.Size = new Size(110, 36);
                btnDel.Location = new Point(24, 310);
                btnDel.Click += (s, ev) =>
                {
                    Deleted = true;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                Controls.Add(btnDel);
            }

            var btnCancel = MkBtn("CANCELAR", CBgPanel, CTextDim);
            btnCancel.Size = new Size(100, 36);
            btnCancel.Location = new Point(btnOk.Left - 110, 260);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
            AcceptButton = btnOk;
        }

        private static Button MkBtn(string text, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat,
                BackColor = bg, ForeColor = fg,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }

    // =========================================================================
    // FormSelectTren - Pick a tren for autonumerar
    // =========================================================================

    internal class FormSelectTren : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CBorder { get { return Theme.Border; } }

        public int SelectedTrenId { get; private set; }

        public FormSelectTren(List<TrenConfig> trenes)
        {
            Text = "Seleccionar Tren";
            Size = new Size(380, 220);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = CBgDark; ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            Controls.Add(new Label
            {
                Text = "\u26A1  AUTONUMERAR \u2014 Seleccion\u00E1 un tren",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = CText, Location = new Point(24, 16),
                AutoSize = true, BackColor = Color.Transparent
            });

            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CBgPanel, ForeColor = CText,
                Font = new Font("Segoe UI", 11f),
                Location = new Point(24, 56), Size = new Size(Width - 48, 30)
            };
            foreach (var t in trenes)
                combo.Items.Add("Tren " + t.Id + " \u2014 " + (t.Nombre ?? "?") + " (" + t.Surcos + " surcos)");
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            Controls.Add(combo);

            Controls.Add(new Label
            {
                Text = "Esto reemplazar\u00E1 todos los sensores de semilla del tren seleccionado.",
                Font = new Font("Segoe UI", 8.5f), ForeColor = CTextDim,
                Location = new Point(24, 96), AutoSize = true, BackColor = Color.Transparent
            });

            var btnOk = new Button
            {
                Text = "\u2713  AUTONUMERAR", FlatStyle = FlatStyle.Flat,
                BackColor = CAccent, ForeColor = CBgDark,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Size = new Size(160, 36), Location = new Point(Width - 184, 150), Cursor = Cursors.Hand
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, ev) =>
            {
                if (combo.SelectedIndex >= 0 && combo.SelectedIndex < trenes.Count)
                    SelectedTrenId = trenes[combo.SelectedIndex].Id;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnOk);

            var btnCancel = new Button
            {
                Text = "CANCELAR", FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Size = new Size(100, 36), Location = new Point(btnOk.Left - 110, 150),
                Cursor = Cursors.Hand, DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
            AcceptButton = btnOk;
        }
    }

    // =========================================================================
    // FormEditGlobalSensor - Editor para sensores globales (turbina/tolva/herramienta)
    // Solo pide nodo + cable. No pide surco.
    // =========================================================================

    internal class FormEditGlobalSensor : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CBorder { get { return Theme.Border; } }
        private static Color CRed { get { return Theme.Error; } }

        public bool Deleted { get; private set; }
        private readonly List<SensorConfig> _allSensors;
        private readonly SensorConfig _sensor;

        public FormEditGlobalSensor(SensorConfig sensor, List<string> knownUids)
            : this(sensor, knownUids, null) { }

        public FormEditGlobalSensor(SensorConfig sensor, List<string> knownUids,
            List<SensorConfig> allSensors)
        {
            Text = "Editar Sensor Global";
            _allSensors = allSensors;
            _sensor = sensor;

            Size = new Size(420, 280);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = CBgDark; ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            Color tipoColor = TipoSensor.GetColor(sensor.Tipo);

            Controls.Add(new Label
            {
                Text = TipoSensor.NombreAmigable(sensor.Tipo).ToUpperInvariant(),
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = tipoColor, Location = new Point(24, 16),
                AutoSize = true, BackColor = Color.Transparent
            });
            Controls.Add(new Label
            {
                Text = "Sensor global \u2014 se asigna a un nodo y cable, no a un surco",
                Font = new Font("Segoe UI", 9f), ForeColor = CTextDim,
                Location = new Point(24, 46), AutoSize = true, BackColor = Color.Transparent
            });

            // UID.
            Controls.Add(new Label
            {
                Text = "UID del nodo:", Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim, Location = new Point(24, 78), AutoSize = true
            });
            var cboUid = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = CBgPanel, ForeColor = CAccent,
                Font = new Font("Consolas", 11f),
                Location = new Point(24, 100), Size = new Size(Width - 48, 28)
            };
            if (knownUids != null)
                foreach (var u in knownUids) cboUid.Items.Add(u);
            if (!string.IsNullOrEmpty(sensor.Uid) && sensor.Uid != "UNASSIGNED")
                cboUid.Text = sensor.Uid;
            else if (cboUid.Items.Count > 0)
                cboUid.SelectedIndex = 0;
            Controls.Add(cboUid);

            // Cable.
            Controls.Add(new Label
            {
                Text = "Cable (1-8):", Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim, Location = new Point(24, 138), AutoSize = true
            });
            var numCable = new NumericUpDown
            {
                Minimum = 1, Maximum = 8,
                Value = Math.Max(1, Math.Min(8, sensor.Cable > 0 ? sensor.Cable : 1)),
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = CAccent, BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(24, 160), Size = new Size(80, 28)
            };
            Controls.Add(numCable);

            // Active.
            var chkActive = new CheckBox
            {
                Text = "Activo", Checked = sensor.IsActive,
                Font = new Font("Segoe UI", 9.5f), ForeColor = CText,
                BackColor = Color.Transparent,
                Location = new Point(140, 162), AutoSize = true
            };
            Controls.Add(chkActive);

            // OK.
            var btnOk = new Button
            {
                Text = "\u2713  GUARDAR", FlatStyle = FlatStyle.Flat,
                BackColor = CAccent, ForeColor = CBgDark,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Size = new Size(130, 36), Location = new Point(Width - 154, 218),
                Cursor = Cursors.Hand
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, ev) =>
            {
                string uid = cboUid.Text.Trim();
                if (string.IsNullOrEmpty(uid)) return;
                int cable = (int)numCable.Value;

                // Validar cable no duplicado.
                if (_allSensors != null)
                {
                    foreach (var ss in _allSensors)
                    {
                        if (ss == null || ss == _sensor) continue;
                        if (string.Equals(ss.Uid, uid, StringComparison.OrdinalIgnoreCase)
                            && ss.Cable == cable)
                        {
                            MessageBox.Show(this,
                                "El cable c" + cable + " del nodo " + uid + " ya est\u00E1 asignado a:\n"
                                + TipoSensor.NombreAmigable(ss.Tipo) + " T" + ss.Tren + " S" + ss.Bajada
                                + "\n\nEleg\u00ED otro cable.",
                                "Cable Ocupado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                }

                sensor.Uid = uid;
                sensor.Cable = cable;
                sensor.Pin = cable - 1;
                sensor.IsActive = chkActive.Checked;

                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnOk);

            // Delete.
            var btnDel = new Button
            {
                Text = "\U0001F5D1  QUITAR", FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 25, 25), ForeColor = CRed,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Size = new Size(110, 36), Location = new Point(24, 218),
                Cursor = Cursors.Hand
            };
            btnDel.FlatAppearance.BorderSize = 0;
            btnDel.Click += (s, ev) =>
            {
                Deleted = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnDel);

            var btnCancel = new Button
            {
                Text = "CANCELAR", FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel, ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Size = new Size(100, 36), Location = new Point(btnOk.Left - 110, 218),
                Cursor = Cursors.Hand, DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
            AcceptButton = btnOk;
        }
    }
}
