// ============================================================================
// FormVistaXPerfiles.cs - Visor de perfiles de implemento (paso 5 FASE II)
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXPerfiles.cs
// Target: net48 (C# 7.3)
//
// Lista los .json en data/implementos/ con su info (nombre, #surcos,
// #trenes, #sensores) y permite activar uno o duplicarlo. Al activar
// setea VistaXConfig.ImplementoJsonPath al path elegido; el caller
// (FormGPS) hace Cleanup + Init para que el SeedMonitor levante con el
// implemento nuevo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace AgroParallel.VistaX
{
    public class FormVistaXPerfiles : Form
    {
        private static readonly Color CBgDark = Color.FromArgb(0, 0, 0);
        private static readonly Color CBgCard = Color.FromArgb(30, 30, 30);
        private static readonly Color CBgCardActive = Color.FromArgb(0, 60, 28);
        private static readonly Color CAccent = Color.FromArgb(0, 230, 118);
        private static readonly Color CText = Color.FromArgb(235, 235, 235);
        private static readonly Color CTextDim = Color.FromArgb(120, 120, 120);
        private static readonly Color CBorder = Color.FromArgb(40, 40, 40);

        private class Item
        {
            public string Path;
            public ImplementoConfig Config;
            public int CountSurcos;
            public int CountTrenes;
            public int CountSensores;
            public DateTime LastWrite;
        }

        private readonly VistaXConfig _cfg;
        private FlowLayoutPanel _list;
        private readonly List<Item> _items = new List<Item>();

        // Path del implemento activado (null si el usuario cerro sin activar).
        public string ActivatedPath { get; private set; }

        public FormVistaXPerfiles(VistaXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException("cfg");
            BuildUI();
            ReloadList();
        }

        private void BuildUI()
        {
            Text = "VistaX — Perfiles de Implemento";
            Size = new Size(780, 560);
            MinimumSize = new Size(600, 420);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CBgDark;
            ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            var lblTitle = new Label
            {
                Text = "\U0001F4DA PERFILES DE IMPLEMENTO",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CAccent,
                Location = new Point(16, 10),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            var lblSub = new Label
            {
                Text = "Configuraciones predefinidas para cada sembradora del cliente",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = CTextDim,
                Location = new Point(18, 36),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSub);
            Controls.Add(header);

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = CBgDark,
                Padding = new Padding(12)
            };
            Controls.Add(_list);
            _list.BringToFront();

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            var btnReload = MkButton("↻  Recargar", Color.FromArgb(40, 40, 45), CText);
            btnReload.Size = new Size(120, 32);
            btnReload.Location = new Point(14, 8);
            btnReload.Click += (s, e) => ReloadList();
            footer.Controls.Add(btnReload);

            var btnClose = MkButton("Cerrar", Color.FromArgb(60, 60, 65), CText);
            btnClose.Size = new Size(100, 32);
            btnClose.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnClose.Location = new Point(Width - btnClose.Width - 20, 8);
            btnClose.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(btnClose);

            Controls.Add(footer);
            CancelButton = btnClose;
        }

        private void ReloadList()
        {
            _items.Clear();
            _list.Controls.Clear();

            string dir = ResolveImplementosDir();
            if (!Directory.Exists(dir))
            {
                _list.Controls.Add(MkEmptyState("No existe la carpeta " + dir));
                return;
            }

            string[] files = Directory.GetFiles(dir, "*.json");
            if (files.Length == 0)
            {
                _list.Controls.Add(MkEmptyState("No hay perfiles en " + dir));
                return;
            }

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var f in files)
            {
                try
                {
                    string json = File.ReadAllText(f);
                    var cfg = JsonSerializer.Deserialize<ImplementoConfig>(json, opts);
                    if (cfg == null) continue;

                    int surcos = 0, sensores = 0;
                    var trenes = new HashSet<int>();
                    if (cfg.MapeoSensores != null)
                    {
                        sensores = cfg.MapeoSensores.Count;
                        var bajadas = new HashSet<int>();
                        foreach (var s in cfg.MapeoSensores)
                        {
                            if (s == null) continue;
                            if (s.IsActive && string.Equals(s.Tipo, "semilla",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                bajadas.Add(s.Bajada);
                            }
                            trenes.Add(s.Tren);
                        }
                        surcos = bajadas.Count;
                    }
                    _items.Add(new Item
                    {
                        Path = f,
                        Config = cfg,
                        CountSurcos = surcos,
                        CountTrenes = trenes.Count,
                        CountSensores = sensores,
                        LastWrite = File.GetLastWriteTime(f)
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[VistaX] Error leyendo perfil "
                        + f + ": " + ex.Message);
                }
            }

            _items.Sort((a, b) => b.LastWrite.CompareTo(a.LastWrite));
            foreach (var it in _items)
                _list.Controls.Add(MkCard(it));
        }

        private Panel MkCard(Item it)
        {
            bool isActive = !string.IsNullOrEmpty(_cfg.ImplementoJsonPath)
                && string.Equals(Path.GetFullPath(_cfg.ImplementoJsonPath),
                    Path.GetFullPath(it.Path), StringComparison.OrdinalIgnoreCase);

            var card = new Panel
            {
                Size = new Size(_list.ClientSize.Width - 36, 100),
                Margin = new Padding(0, 0, 0, 10),
                BackColor = isActive ? CBgCardActive : CBgCard,
                Cursor = Cursors.Default
            };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(isActive ? CAccent : CBorder, 1))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            var lblName = new Label
            {
                Text = string.IsNullOrEmpty(it.Config.Nombre)
                    ? Path.GetFileNameWithoutExtension(it.Path) : it.Config.Nombre,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(16, 10),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblName);

            if (isActive)
            {
                var badge = new Label
                {
                    Text = " ACTIVO ",
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    ForeColor = CBgDark,
                    BackColor = CAccent,
                    Location = new Point(lblName.Right + 10, 14),
                    AutoSize = true
                };
                card.Controls.Add(badge);
            }

            var lblStats = new Label
            {
                Text = string.Format(CultureInfo.InvariantCulture,
                    "{0} SURCOS    {1} TRENES    {2} SENSORES",
                    it.CountSurcos, it.CountTrenes, it.CountSensores),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = CTextDim,
                Location = new Point(18, 40),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblStats);

            var lblDate = new Label
            {
                Text = "\U0001F551 " + it.LastWrite.ToString("yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture),
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = CTextDim,
                Location = new Point(18, 64),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblDate);

            var btnDuplicate = MkButton("Duplicar", Color.FromArgb(50, 50, 55), CText);
            btnDuplicate.Size = new Size(110, 32);
            btnDuplicate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDuplicate.Location = new Point(card.Width - 240, 34);
            btnDuplicate.Click += (s, e) => DuplicateItem(it);
            card.Controls.Add(btnDuplicate);

            var btnActivate = MkButton(isActive ? "Activo" : "Activar",
                isActive ? Color.FromArgb(30, 30, 30) : CAccent,
                isActive ? CTextDim : CBgDark);
            btnActivate.Size = new Size(110, 32);
            btnActivate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnActivate.Location = new Point(card.Width - 122, 34);
            btnActivate.Enabled = !isActive;
            btnActivate.Click += (s, e) => ActivateItem(it);
            card.Controls.Add(btnActivate);

            return card;
        }

        private Panel MkEmptyState(string msg)
        {
            var p = new Panel
            {
                Size = new Size(_list.ClientSize.Width - 36, 120),
                BackColor = Color.FromArgb(18, 18, 20)
            };
            var lbl = new Label
            {
                Text = msg,
                Font = new Font("Segoe UI", 10f),
                ForeColor = CTextDim,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            p.Controls.Add(lbl);
            return p;
        }

        private void ActivateItem(Item it)
        {
            ActivatedPath = it.Path;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void DuplicateItem(Item it)
        {
            try
            {
                string dir = Path.GetDirectoryName(it.Path);
                string name = Path.GetFileNameWithoutExtension(it.Path);
                string candidate = Path.Combine(dir, name + "_copy.json");
                int n = 2;
                while (File.Exists(candidate))
                {
                    candidate = Path.Combine(dir, name + "_copy" + n + ".json");
                    n++;
                }
                File.Copy(it.Path, candidate, false);
                ReloadList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo duplicar: " + ex.Message,
                    "Perfiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string ResolveImplementosDir()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "data", "implementos");
        }

        private static Button MkButton(string text, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = fg,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = CBorder;
            return b;
        }
    }
}
