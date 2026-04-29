// ============================================================================
// FormVistaXTrenes.cs - Editor de trenes de siembra (Config – Trenes)
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXTrenes.cs
// Target: net48 (C# 7.3)
//
// Define la estructura física de la sembradora: cantidad de trenes, nombre
// (Delantero/Trasero/Custom), surcos por tren, orden de numeración. Vista
// previa en vivo de rangos de surco. Alerta de sensores huérfanos si la
// estructura no coincide con mapeo existente.
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

namespace AgroParallel.VistaX
{
    public class FormVistaXTrenes : Form
    {
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CBgCard { get { return Theme.BgCard; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CAccentDim { get { return Theme.AccentDim; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CTextFaint { get { return Theme.TextFaint; } }
        private static Color CBorder { get { return Theme.Border; } }
        private static Color CRed { get { return Theme.Error; } }
        private static Color CYellow { get { return Theme.Warning; } }

        private readonly VistaXConfig _cfg;
        private ImplementoConfig _implemento;
        private List<TrenConfig> _trenes;

        private FlowLayoutPanel _list;
        private Panel _previewPanel;
        private Label _lblWarning;
        private bool _dirty;

        public bool Changed { get; private set; }

        public FormVistaXTrenes(VistaXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadData();
            RebuildCards();
            UpdatePreview();
        }

        // =====================================================================
        // UI Construction
        // =====================================================================

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "VistaX — Trenes de Siembra";
            Size = new Size(1020, 680);
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CBgDark;
            ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            KeyDown += (s, ev) => { if (ev.KeyCode == Keys.Escape) Close(); };
            Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // ── Sub-header ──────────────────────────────────────────────
            var subHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 76,
                BackColor = CBgDark
            };
            subHeader.Controls.Add(new Label
            {
                Text = "\U0001F69C",
                Font = new Font("Segoe UI Emoji", 20f, FontStyle.Bold),
                ForeColor = CAccent,
                Location = new Point(22, 20),
                Size = new Size(40, 40),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            });
            subHeader.Controls.Add(new Label
            {
                Text = "TRENES DE SIEMBRA",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(74, 18),
                AutoSize = true,
                BackColor = Color.Transparent
            });
            subHeader.Controls.Add(new Label
            {
                Text = "Estructura f\u00EDsica de la sembradora: trenes, surcos y orden de numeraci\u00F3n",
                Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim,
                Location = new Point(74, 46),
                AutoSize = true,
                BackColor = Color.Transparent
            });
            Controls.Add(subHeader);

            // ── Top bar ─────────────────────────────────────────────────
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = CBgPanel,
                Cursor = Cursors.SizeAll
            };
            topBar.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };
            topBar.MouseDown += (s, ev) =>
            {
                if (ev.Button == MouseButtons.Left) { _dragging = true; _dragStart = ev.Location; }
            };
            topBar.MouseMove += (s, ev) =>
            {
                if (_dragging)
                    Location = new Point(Location.X + ev.X - _dragStart.X, Location.Y + ev.Y - _dragStart.Y);
            };
            topBar.MouseUp += (s, ev) => _dragging = false;

            topBar.Controls.Add(new Label
            {
                Text = "VistaX",
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(24, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            });
            topBar.Controls.Add(new Label
            {
                Text = "CONFIGURACION",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CTextDim,
                Location = new Point(116, 20),
                AutoSize = true,
                BackColor = Color.Transparent
            });

            var btnX = new Button
            {
                Text = "\u2715",
                FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel,
                ForeColor = CTextDim,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Size = new Size(40, 32),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnX.FlatAppearance.BorderSize = 0;
            btnX.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 40, 40);
            btnX.Click += (s, ev) => Close();
            topBar.Controls.Add(btnX);
            topBar.Resize += (s, ev) => btnX.Location = new Point(topBar.Width - btnX.Width - 4, 12);

            Controls.Add(topBar);

            // ── Preview panel (right side) ──────────────────────────────
            _previewPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 320,
                BackColor = Color.FromArgb(14, 14, 16)
            };
            _previewPanel.Paint += PaintPreview;
            Controls.Add(_previewPanel);

            // ── Warning label (above card list) ─────────────────────────
            _lblWarning = new Label
            {
                Dock = DockStyle.Top,
                Height = 0,
                BackColor = Color.FromArgb(60, 40, 0),
                ForeColor = CYellow,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(20, 0, 20, 0),
                Visible = false
            };
            Controls.Add(_lblWarning);

            // ── Card list ───────────────────────────────────────────────
            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = CBgDark,
                Padding = new Padding(20, 14, 20, 14)
            };
            _list.Resize += (s, ev) =>
            {
                int w = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
                foreach (Control c in _list.Controls)
                    if (c.Width != w) c.Width = w;
            };
            Controls.Add(_list);
            _list.BringToFront();

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = CBgPanel
            };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            footer.Controls.Add(new Label
            {
                Text = "VistaX nativo \u00B7 Trenes",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = CTextFaint,
                Location = new Point(18, 17),
                AutoSize = true,
                BackColor = Color.Transparent
            });

            var btnAdd = MkPillButton("+  AGREGAR TREN", CAccent, CBgDark);
            btnAdd.Size = new Size(160, 34);
            btnAdd.Location = new Point(180, 8);
            btnAdd.Click += (s, ev) => AddTren();
            footer.Controls.Add(btnAdd);

            var btnSave = MkPillButton("\u2713  GUARDAR", CAccent, CBgDark);
            btnSave.Size = new Size(120, 34);
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            footer.Controls.Add(btnSave);
            btnSave.Click += (s, ev) => SaveAndClose();

            var btnCancel = new Button
            {
                Text = "\u2715 CERRAR",
                FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel,
                ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Size = new Size(110, 34),
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

        // =====================================================================
        // Data
        // =====================================================================

        private void LoadData()
        {
            try
            {
                _implemento = _cfg.LoadImplemento();
            }
            catch
            {
                _implemento = new ImplementoConfig();
            }

            // Clone trenes list for editing.
            _trenes = new List<TrenConfig>();
            if (_implemento.Trenes != null)
            {
                foreach (var t in _implemento.Trenes)
                    _trenes.Add(new TrenConfig { Id = t.Id, Nombre = t.Nombre, Surcos = t.Surcos });
            }

            // If no trenes defined, derive from mapeo_sensores.
            if (_trenes.Count == 0 && _implemento.MapeoSensores != null)
            {
                var groups = new Dictionary<int, int>();
                foreach (var s in _implemento.MapeoSensores)
                {
                    if (s == null || !s.IsActive) continue;
                    int tren = s.Tren > 0 ? s.Tren : 1;
                    if (!groups.ContainsKey(tren)) groups[tren] = 0;
                    // Count distinct bajadas of tipo semilla per tren.
                    if (string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                        groups[tren] = Math.Max(groups[tren], s.Bajada);
                }
                foreach (var kv in groups.OrderBy(g => g.Key))
                {
                    _trenes.Add(new TrenConfig
                    {
                        Id = kv.Key,
                        Nombre = kv.Key == 1 ? "Delantero" : kv.Key == 2 ? "Trasero" : "Tren " + kv.Key,
                        Surcos = kv.Value
                    });
                }
            }

            // Ensure at least one tren.
            if (_trenes.Count == 0)
            {
                _trenes.Add(new TrenConfig { Id = 1, Nombre = "Delantero", Surcos = 0 });
            }
        }

        // =====================================================================
        // Card rendering
        // =====================================================================

        private void RebuildCards()
        {
            _list.SuspendLayout();
            _list.Controls.Clear();

            foreach (var tren in _trenes)
                _list.Controls.Add(MkTrenCard(tren));

            _list.ResumeLayout();
        }

        private Panel MkTrenCard(TrenConfig tren)
        {
            int cardW = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
            var card = new Panel
            {
                Size = new Size(cardW, 110),
                Margin = new Padding(0, 0, 0, 10),
                BackColor = CBgCard,
                Tag = tren
            };

            card.Paint += (s, ev) =>
            {
                using (var acc = new SolidBrush(CAccent))
                    ev.Graphics.FillRectangle(acc, 0, 0, 4, card.Height);
                using (var pen = new Pen(CBorder))
                    ev.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            // ── Tren ID badge ───────────────────────────────────────────
            var idPanel = new Panel
            {
                Size = new Size(46, 46),
                Location = new Point(20, 32),
                BackColor = Color.Transparent
            };
            idPanel.Paint += (s, ev) =>
            {
                ev.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(CAccentDim))
                    ev.Graphics.FillEllipse(b, 0, 0, 45, 45);
                string id = tren.Id.ToString(CultureInfo.InvariantCulture);
                using (var f = new Font("Segoe UI", 16f, FontStyle.Bold))
                using (var br = new SolidBrush(CText))
                {
                    var sz = ev.Graphics.MeasureString(id, f);
                    ev.Graphics.DrawString(id, f, br, (45 - sz.Width) / 2f, (45 - sz.Height) / 2f);
                }
            };
            card.Controls.Add(idPanel);

            // ── Nombre (editable) ───────────────────────────────────────
            var lblNomLbl = new Label
            {
                Text = "NOMBRE",
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = CTextFaint,
                Location = new Point(82, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblNomLbl);

            var txtNombre = new TextBox
            {
                Text = tren.Nombre ?? "",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = CText,
                BackColor = Color.FromArgb(22, 22, 26),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(82, 32),
                Size = new Size(180, 28)
            };
            txtNombre.TextChanged += (s, ev) =>
            {
                tren.Nombre = txtNombre.Text;
                _dirty = true;
                UpdatePreview();
            };
            card.Controls.Add(txtNombre);

            // ── Surcos (editable numeric) ───────────────────────────────
            var lblSurcosLbl = new Label
            {
                Text = "SURCOS",
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = CTextFaint,
                Location = new Point(282, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblSurcosLbl);

            var numSurcos = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 200,
                Value = Math.Max(0, Math.Min(200, tren.Surcos)),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CAccent,
                BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(282, 30),
                Size = new Size(90, 32),
                TextAlign = HorizontalAlignment.Center
            };
            numSurcos.ValueChanged += (s, ev) =>
            {
                tren.Surcos = (int)numSurcos.Value;
                _dirty = true;
                UpdatePreview();
            };
            card.Controls.Add(numSurcos);

            // ── Surco range preview ─────────────────────────────────────
            var lblRange = new Label
            {
                Name = "lblRange",
                Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim,
                Location = new Point(282, 68),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblRange);
            UpdateRangeLabel(lblRange, tren);
            numSurcos.ValueChanged += (s2, ev2) => UpdateRangeLabel(lblRange, tren);

            // ── ID (editable) ───────────────────────────────────────────
            var lblIdLbl = new Label
            {
                Text = "ID TREN",
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = CTextFaint,
                Location = new Point(392, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblIdLbl);

            var numId = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 10,
                Value = Math.Max(1, Math.Min(10, tren.Id)),
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = CText,
                BackColor = Color.FromArgb(15, 15, 15),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(392, 32),
                Size = new Size(60, 28),
                TextAlign = HorizontalAlignment.Center
            };
            numId.ValueChanged += (s, ev) =>
            {
                tren.Id = (int)numId.Value;
                _dirty = true;
                idPanel.Invalidate();
                UpdatePreview();
            };
            card.Controls.Add(numId);

            // ── Delete button ───────────────────────────────────────────
            var btnDel = MkPillButton("\U0001F5D1  ELIMINAR", Color.FromArgb(50, 25, 25), CRed);
            btnDel.Size = new Size(120, 34);
            btnDel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDel.Location = new Point(cardW - 140, 38);
            btnDel.Click += (s, ev) =>
            {
                if (_trenes.Count <= 1)
                {
                    MessageBox.Show(this, "Debe haber al menos un tren.",
                        "Trenes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _trenes.Remove(tren);
                _dirty = true;
                RebuildCards();
                UpdatePreview();
            };
            card.Controls.Add(btnDel);

            return card;
        }

        private void UpdateRangeLabel(Label lbl, TrenConfig tren)
        {
            // Calculate range based on position in list.
            int start = 1;
            foreach (var t in _trenes)
            {
                if (t == tren) break;
                start += t.Surcos;
            }
            int end = start + tren.Surcos - 1;
            lbl.Text = tren.Surcos > 0
                ? "Surcos " + start + " \u2013 " + end
                : "Sin surcos";
        }

        // =====================================================================
        // Preview panel (visual representation)
        // =====================================================================

        private void UpdatePreview()
        {
            CheckOrphanSensors();
            _previewPanel.Invalidate();
        }

        private void PaintPreview(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(14, 14, 16));

            int pw = _previewPanel.Width;
            int ph = _previewPanel.Height;

            // Title.
            using (var f = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var b = new SolidBrush(CTextDim))
                g.DrawString("VISTA PREVIA", f, b, 16, 12);

            if (_trenes == null || _trenes.Count == 0) return;

            int totalSurcos = 0;
            foreach (var t in _trenes) totalSurcos += t.Surcos;

            // Summary.
            using (var f = new Font("Segoe UI", 9f))
            using (var b = new SolidBrush(CTextFaint))
            {
                string summary = _trenes.Count + " tren(es) \u00B7 " + totalSurcos + " surcos totales";
                g.DrawString(summary, f, b, 16, 34);
            }

            // Draw each train as a horizontal bar with numbered surcos.
            int marginX = 16;
            int barY = 60;
            int barH = 0;
            int availableH = ph - barY - 20;

            if (_trenes.Count > 0)
                barH = Math.Min(80, Math.Max(30, availableH / _trenes.Count - 12));

            int surcoStart = 1;
            foreach (var tren in _trenes)
            {
                // Train label.
                using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (var b = new SolidBrush(CAccent))
                {
                    string label = (tren.Nombre ?? "Tren " + tren.Id).ToUpperInvariant();
                    g.DrawString(label, f, b, marginX, barY);
                }

                int barTop = barY + 18;
                int barWidth = pw - marginX * 2;

                if (tren.Surcos > 0 && barWidth > 0)
                {
                    // Draw surco cells.
                    float cellW = Math.Min(24f, (float)barWidth / tren.Surcos);
                    float totalW = cellW * tren.Surcos;
                    float startX = marginX;

                    for (int i = 0; i < tren.Surcos; i++)
                    {
                        float cx = startX + i * cellW;
                        var rect = new RectangleF(cx, barTop, cellW - 1, barH - 4);

                        using (var fill = new SolidBrush(Color.FromArgb(0, 60, 30)))
                            g.FillRectangle(fill, rect);
                        using (var border = new Pen(CAccentDim, 0.5f))
                            g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);

                        // Number label (only if cell is wide enough).
                        if (cellW >= 14)
                        {
                            string num = (surcoStart + i).ToString(CultureInfo.InvariantCulture);
                            using (var f = new Font("Segoe UI", cellW >= 18 ? 7f : 6f))
                            using (var b = new SolidBrush(CTextDim))
                            {
                                var sz = g.MeasureString(num, f);
                                g.DrawString(num, f, b,
                                    cx + (cellW - 1 - sz.Width) / 2f,
                                    barTop + (barH - 4 - sz.Height) / 2f);
                            }
                        }
                    }

                    // Range label below bar.
                    using (var f = new Font("Segoe UI", 7.5f))
                    using (var b = new SolidBrush(CTextFaint))
                    {
                        int end = surcoStart + tren.Surcos - 1;
                        g.DrawString(surcoStart + " \u2013 " + end, f, b, marginX, barTop + barH - 2);
                    }
                }
                else
                {
                    using (var f = new Font("Segoe UI", 8f, FontStyle.Italic))
                    using (var b = new SolidBrush(CTextFaint))
                        g.DrawString("(sin surcos)", f, b, marginX, barTop + 4);
                }

                surcoStart += tren.Surcos;
                barY = barTop + barH + 14;
            }
        }

        // =====================================================================
        // Orphan sensor detection
        // =====================================================================

        private void CheckOrphanSensors()
        {
            if (_implemento == null || _implemento.MapeoSensores == null)
            {
                HideWarning();
                return;
            }

            // Build set of valid (tren, bajada) pairs from current tren config.
            var validPairs = new HashSet<string>();
            int surcoStart = 1;
            foreach (var tren in _trenes)
            {
                for (int b = 1; b <= tren.Surcos; b++)
                    validPairs.Add(tren.Id + "-" + (surcoStart + b - 1));
                surcoStart += tren.Surcos;
            }

            // Also allow by tren ID + bajada within tren range.
            foreach (var tren in _trenes)
            {
                for (int b = 1; b <= tren.Surcos; b++)
                    validPairs.Add(tren.Id + "-" + b);
            }

            int orphans = 0;
            foreach (var sensor in _implemento.MapeoSensores)
            {
                if (sensor == null || !sensor.IsActive) continue;
                int trenId = sensor.Tren > 0 ? sensor.Tren : 1;
                string key = trenId + "-" + sensor.Bajada;
                // Check if this tren ID exists at all.
                bool trenExists = false;
                foreach (var t in _trenes)
                {
                    if (t.Id == trenId) { trenExists = true; break; }
                }
                if (!trenExists) { orphans++; continue; }
                // Check if bajada fits within tren surcos.
                foreach (var t in _trenes)
                {
                    if (t.Id == trenId && sensor.Bajada > t.Surcos)
                    { orphans++; break; }
                }
            }

            if (orphans > 0)
                ShowWarning("\u26A0  " + orphans + " sensor(es) hu\u00E9rfano(s): est\u00E1n mapeados a un tren/surco que no existe en esta configuraci\u00F3n");
            else
                HideWarning();
        }

        private void ShowWarning(string msg)
        {
            if (_lblWarning == null) return;
            _lblWarning.Text = msg;
            _lblWarning.Height = 32;
            _lblWarning.Visible = true;
        }

        private void HideWarning()
        {
            if (_lblWarning == null) return;
            _lblWarning.Visible = false;
            _lblWarning.Height = 0;
        }

        // =====================================================================
        // Actions
        // =====================================================================

        private void AddTren()
        {
            int newId = 1;
            foreach (var t in _trenes)
                if (t.Id >= newId) newId = t.Id + 1;

            string nombre = newId == 1 ? "Delantero" : newId == 2 ? "Trasero" : "Tren " + newId;

            _trenes.Add(new TrenConfig { Id = newId, Nombre = nombre, Surcos = 0 });
            _dirty = true;
            RebuildCards();
            UpdatePreview();
        }

        private void SaveAndClose()
        {
            if (!_dirty)
            {
                Close();
                return;
            }

            try
            {
                // Validate: no duplicate IDs.
                var ids = new HashSet<int>();
                foreach (var t in _trenes)
                {
                    if (!ids.Add(t.Id))
                    {
                        MessageBox.Show(this,
                            "Hay trenes con ID duplicado (" + t.Id + "). Correg\u00ED los IDs antes de guardar.",
                            "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                // Apply to implemento.
                _implemento.Trenes = new List<TrenConfig>();
                foreach (var t in _trenes)
                    _implemento.Trenes.Add(new TrenConfig { Id = t.Id, Nombre = t.Nombre, Surcos = t.Surcos });

                // Save implemento JSON.
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
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = fg,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}
