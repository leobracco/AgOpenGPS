// ============================================================================
// FormCamarasConfig.cs - Tab Config: editar lista de cámaras Hikvision
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AgroParallel.Common;
using AgroParallel.OrbitX;

namespace AgroParallel.Camaras
{
    public class FormCamarasConfig : Form
    {
        private readonly CamarasConfig _cfg;
        private OrbitXConfig _ox;
        private DataGridView _grid;
        private NumericUpDown _refrescoMs;
        private CheckBox _chkStreaming;

        public FormCamarasConfig(CamarasConfig cfg)
        {
            _cfg = cfg ?? CamarasConfig.Load();
            _ox  = OrbitXConfig.Load();
            Text = "Cámaras - Config";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.BgBlack;
            ForeColor = Theme.TextPrimary;
            Theme.ApplyToForm(this);
            BuildUI();
            LoadGrid();
        }

        private void BuildUI()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Theme.BgBlack,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            // Top: refresco
            var top = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgCard };
            var lblR = new Label
            {
                Text = "Refresco (ms):",
                Font = new Font(Theme.FontFamily, 10f),
                ForeColor = Theme.TextSecondary,
                Location = new Point(12, 16),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            top.Controls.Add(lblR);
            _refrescoMs = new NumericUpDown
            {
                Location = new Point(120, 12),
                Size = new Size(120, 28),
                Minimum = 250,
                Maximum = 10000,
                Increment = 250,
                Value = _cfg.refrescoMs,
                Font = new Font(Theme.FontFamily, 10f),
                BackColor = Theme.BgInput,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };
            top.Controls.Add(_refrescoMs);
            var lblHint = new Label
            {
                Text = "(1000 = 1 segundo)",
                Font = new Font(Theme.FontFamily, 9f),
                ForeColor = Theme.TextDisabled,
                Location = new Point(252, 16),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            top.Controls.Add(lblHint);

            // Checkbox: Streaming remoto al cloud (push RTSP a OrbitX/MediaMTX)
            // Va en una segunda fila para que sea visible aún cuando el panel del
            // hub sea angosto.
            _chkStreaming = new CheckBox
            {
                Text = "Streaming remoto (publicar al cloud)",
                Checked = _ox.CamarasStreamingEnabled,
                Font = new Font(Theme.FontFamily, 10f, FontStyle.Bold),
                ForeColor = Theme.Accent,
                BackColor = Color.Transparent,
                Location = new Point(12, 52),
                AutoSize = true,
                FlatStyle = FlatStyle.Standard,
                Cursor = Cursors.Hand
            };
            top.Controls.Add(_chkStreaming);
            root.Controls.Add(top, 0, 0);

            // Grid
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Theme.BgCard,
                BorderStyle = BorderStyle.None,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Theme.BgHeader,
                    ForeColor = Theme.TextPrimary,
                    Font = new Font(Theme.FontFamily, 9.5f, FontStyle.Bold)
                },
                EnableHeadersVisualStyles = false,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Theme.BgInput,
                    ForeColor = Theme.TextPrimary,
                    SelectionBackColor = Theme.Accent,
                    SelectionForeColor = Color.White,
                    Font = new Font(Theme.FontFamily, 10f)
                },
                GridColor = Theme.Border,
                RowHeadersVisible = false
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "nombre", HeaderText = "Nombre", FillWeight = 18 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ip", HeaderText = "IP", FillWeight = 18 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "puerto", HeaderText = "Puerto", FillWeight = 8 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "canal", HeaderText = "Canal", FillWeight = 8 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "usuario", HeaderText = "Usuario", FillWeight = 14 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "clave", HeaderText = "Clave", FillWeight = 14 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "activa", HeaderText = "Activa", FillWeight = 8 });

            // Mostrar la clave enmascarada (asteriscos) en celdas no editadas.
            _grid.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex < 0 || e.RowIndex < 0) return;
                if (_grid.Columns[e.ColumnIndex].Name != "clave") return;
                if (e.Value == null) return;
                string raw = e.Value.ToString();
                if (string.IsNullOrEmpty(raw)) return;
                e.Value = new string('\u2022', Math.Min(raw.Length, 12)); // bullets, máx 12
                e.FormattingApplied = true;
            };

            // En edición de la clave, ocultar lo tipeado con la mask del sistema.
            _grid.EditingControlShowing += (s, e) =>
            {
                if (_grid.CurrentCell == null) return;
                if (_grid.Columns[_grid.CurrentCell.ColumnIndex].Name != "clave") return;
                var tb = e.Control as TextBox;
                if (tb != null) tb.UseSystemPasswordChar = true;
            };

            root.Controls.Add(_grid, 0, 1);

            // Bottom: botones
            var bot = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBlack };
            var btnSave = MkBtn("Guardar", 140);
            btnSave.Location = new Point(0, 8);
            btnSave.Click += (s, e) => SaveAll();
            var btnAdd = MkBtnAlt("Agregar fila", 140);
            btnAdd.Location = new Point(150, 8);
            btnAdd.Click += (s, e) => _grid.Rows.Add("Nueva", "192.168.1.64", "80", "1", "admin", "", true);
            var btnDel = MkBtnAlt("Quitar fila", 140);
            btnDel.Location = new Point(300, 8);
            btnDel.Click += (s, e) =>
            {
                if (_grid.SelectedRows.Count > 0 && !_grid.SelectedRows[0].IsNewRow)
                    _grid.Rows.RemoveAt(_grid.SelectedRows[0].Index);
            };
            var btnTest = MkBtnAlt("Probar snapshot", 160);
            btnTest.Location = new Point(450, 8);
            btnTest.Click += (s, e) => TestSnapshot();
            bot.Controls.Add(btnSave); bot.Controls.Add(btnAdd); bot.Controls.Add(btnDel); bot.Controls.Add(btnTest);
            root.Controls.Add(bot, 0, 2);

            Controls.Add(root);
        }

        private Button MkBtn(string text, int w)
        {
            var b = new Button
            {
                Text = text,
                Width = w,
                Height = 38,
                Font = new Font(Theme.FontFamily, 10f, FontStyle.Bold),
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
        private Button MkBtnAlt(string text, int w)
        {
            var b = new Button
            {
                Text = text,
                Width = w,
                Height = 38,
                Font = new Font(Theme.FontFamily, 10f, FontStyle.Bold),
                BackColor = Theme.BgCard,
                ForeColor = Theme.TextPrimary,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = Theme.Border;
            return b;
        }

        private void LoadGrid()
        {
            _grid.Rows.Clear();
            foreach (var c in _cfg.camaras ?? new List<Camara>())
            {
                _grid.Rows.Add(c.nombre, c.ip, c.puerto.ToString(), c.canal.ToString(),
                    c.usuario, c.clave, c.activa);
            }
        }

        private void SaveAll()
        {
            var list = new List<Camara>();
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (r.IsNewRow) continue;
                var c = new Camara();
                c.nombre = Conv(r.Cells["nombre"].Value, "Cámara");
                c.ip = Conv(r.Cells["ip"].Value, "");
                int.TryParse(Conv(r.Cells["puerto"].Value, "80"), out c.puerto);
                int.TryParse(Conv(r.Cells["canal"].Value, "1"), out c.canal);
                c.usuario = Conv(r.Cells["usuario"].Value, "admin");
                c.clave = Conv(r.Cells["clave"].Value, "");
                c.activa = (r.Cells["activa"].Value as bool?) ?? true;
                if (c.puerto <= 0) c.puerto = 80;
                if (c.canal <= 0) c.canal = 1;
                if (!string.IsNullOrEmpty(c.ip)) list.Add(c);
            }
            _cfg.camaras = list;
            _cfg.refrescoMs = (int)_refrescoMs.Value;
            _cfg.Save();

            // Persistir flag de streaming remoto en OrbitXConfig
            _ox.CamarasStreamingEnabled = _chkStreaming.Checked;
            _ox.Save();

            // Reiniciar el relay para que la nueva lista se reporte al cloud
            // (panel) y se ajusten los workers de streaming.
            try
            {
                foreach (Form f in Application.OpenForms)
                {
                    if (f is AgOpenGPS.FormGPS gps) { gps.ReloadCamarasRelay(); break; }
                }
            }
            catch { }

            MessageBox.Show("Configuración guardada (" + list.Count + " cámaras). La pestaña Vivo aplica al reabrir.",
                "Cámaras", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string Conv(object v, string def)
        {
            if (v == null) return def;
            string s = v.ToString();
            return string.IsNullOrEmpty(s) ? def : s;
        }

        private void TestSnapshot()
        {
            if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].IsNewRow)
            {
                MessageBox.Show("Seleccioná una fila."); return;
            }
            var r = _grid.SelectedRows[0];
            var c = new Camara
            {
                ip = Conv(r.Cells["ip"].Value, ""),
                puerto = int.TryParse(Conv(r.Cells["puerto"].Value, "80"), out int p) ? p : 80,
                canal = int.TryParse(Conv(r.Cells["canal"].Value, "1"), out int ch) ? ch : 1,
                usuario = Conv(r.Cells["usuario"].Value, "admin"),
                clave = Conv(r.Cells["clave"].Value, "")
            };
            string url = _cfg.SnapshotUrl(c);
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Credentials = new System.Net.NetworkCredential(c.usuario, c.clave);
                req.PreAuthenticate = true;
                req.Timeout = 4000;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                {
                    var ms = new System.IO.MemoryStream();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    var img = System.Drawing.Image.FromStream(ms);
                    var f = new Form { Text = "Test snapshot: " + c.ip, Size = new Size(800, 600), StartPosition = FormStartPosition.CenterParent };
                    var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = img, BackColor = Color.Black };
                    f.Controls.Add(pb);
                    f.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error:\n" + ex.Message + "\n\nURL: " + url, "Test snapshot", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
