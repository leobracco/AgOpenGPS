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
        // Aliases al tema centralizado.
        private static Color CBgDark { get { return Theme.BgBlack; } }
        private static Color CBgPanel { get { return Theme.BgHeader; } }
        private static Color CBgCard { get { return Theme.BgCard; } }
        private static Color CBgCardActive { get { return Theme.AccentDark; } }
        private static Color CAccent { get { return Theme.Accent; } }
        private static Color CAccentDim { get { return Theme.AccentDim; } }
        private static Color CText { get { return Theme.TextPrimary; } }
        private static Color CTextDim { get { return Theme.TextSecondary; } }
        private static Color CTextFaint { get { return Theme.TextFaint; } }
        private static Color CBorder { get { return Theme.Border; } }

        private class Item
        {
            public string Path;
            public ImplementoConfig Config;
            public int CountSurcos;
            public int CountTrenes;
            public int CountSensores;
            public DateTime LastWrite;
            public bool IsLocked;
        }

        // Lock file convention: same name + ".lock" = locked.
        private static bool IsProfileLocked(string path)
        {
            return File.Exists(path + ".lock");
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
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Diferir hasta que el FlowLayoutPanel tenga su ClientSize real;
            // sino los cards se crean con ancho default.
            ReloadList();
        }

        private bool _dragging;
        private Point _dragStart;

        private void BuildUI()
        {
            Text = "VistaX — Perfiles de Implemento";
            Size = new Size(980, 640);
            MinimumSize = new Size(760, 460);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CBgDark;
            ForeColor = CText;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;
            // Sin chrome de Windows — VistaX-Core no lo tiene tampoco.
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            // Borde sutil alrededor del form (reemplaza el chrome nativo).
            Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // Ojo con el orden en WinForms: al agregar Dock=Top, el ULTIMO
            // agregado queda arriba. Por eso agregamos primero lo que va abajo
            // (subHeader) y despues lo que va arriba (topBar).

            // Sub-header de la pagina: icono + titulo + subtitulo.
            var subHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 76,
                BackColor = CBgDark
            };
            var lblIcon = new Label
            {
                Text = "\U0001F4DA",
                Font = new Font("Segoe UI Emoji", 20f, FontStyle.Bold),
                ForeColor = CAccent,
                Location = new Point(22, 20),
                Size = new Size(40, 40),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };
            subHeader.Controls.Add(lblIcon);

            var lblTitle = new Label
            {
                Text = "PERFILES DE IMPLEMENTO",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(74, 18),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            subHeader.Controls.Add(lblTitle);

            var lblSub = new Label
            {
                Text = "Configuraciones predefinidas para cada sembradora del cliente",
                Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim,
                Location = new Point(74, 46),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            subHeader.Controls.Add(lblSub);

            Controls.Add(subHeader);

            // Top bar de branding "VistaX CONFIGURACION  |  Perfil activo: NAME".
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = CBgPanel,
                Cursor = Cursors.SizeAll
            };
            topBar.Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };
            // Drag handle (el top bar se usa para mover la ventana).
            topBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _dragging = true;
                    _dragStart = e.Location;
                }
            };
            topBar.MouseMove += (s, e) =>
            {
                if (_dragging)
                {
                    Location = new Point(
                        Location.X + e.X - _dragStart.X,
                        Location.Y + e.Y - _dragStart.Y);
                }
            };
            topBar.MouseUp += (s, e) => _dragging = false;

            var lblBrand = new Label
            {
                Text = "VistaX",
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(24, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            topBar.Controls.Add(lblBrand);
            var lblBrandSub = new Label
            {
                Text = "CONFIGURACION",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = CTextDim,
                Location = new Point(116, 20),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            topBar.Controls.Add(lblBrandSub);

            // "Perfil activo: NAME" — posicionado post-layout para no clippear.
            var lblActivoLabel = new Label
            {
                Name = "lblActivoLabel",
                Text = "Perfil activo:",
                Font = new Font("Segoe UI", 9f),
                ForeColor = CTextDim,
                BackColor = Color.Transparent,
                AutoSize = true
            };
            topBar.Controls.Add(lblActivoLabel);

            string activoName = TryGetActiveProfileName();
            var lblActivo = new Label
            {
                Name = "lblActivo",
                Text = (activoName ?? "—").ToUpperInvariant(),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = CAccent,
                BackColor = Color.Transparent,
                AutoSize = true
            };
            topBar.Controls.Add(lblActivo);

            // Boton X de cerrar (reemplaza el chrome nativo).
            var btnX = new Button
            {
                Text = "✕",
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
            btnX.Click += (s, e) => Close();
            topBar.Controls.Add(btnX);

            // Posicionamiento dinamico de los controles alineados a la derecha.
            topBar.Resize += (s, e) =>
            {
                btnX.Location = new Point(topBar.Width - btnX.Width - 4, 12);
                lblActivo.Location = new Point(
                    btnX.Left - lblActivo.Width - 14, 20);
                lblActivoLabel.Location = new Point(
                    lblActivo.Left - lblActivoLabel.Width - 8, 22);
            };

            Controls.Add(topBar);

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = CBgDark,
                Padding = new Padding(20, 14, 20, 14)
            };
            // Al redimensionar la ventana, estirar cada card al ancho del list.
            _list.Resize += (s, e) =>
            {
                int w = _list.ClientSize.Width - _list.Padding.Left - _list.Padding.Right;
                foreach (Control c in _list.Controls)
                {
                    if (c.Width != w) c.Width = w;
                }
            };
            Controls.Add(_list);
            _list.BringToFront();

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = CBgPanel
            };
            footer.Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            var lblVer = new Label
            {
                Text = "VistaX nativo · Perfiles",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = CTextFaint,
                Location = new Point(18, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            footer.Controls.Add(lblVer);

            var btnNew = MkPillButton("+  CREAR NUEVO", CAccent, CBgDark);
            btnNew.Size = new Size(130, 30);
            btnNew.Location = new Point(150, 7);
            btnNew.Click += (s, e) => CreateNewProfile();
            footer.Controls.Add(btnNew);

            var btnReload = MkPillButton("\u21BB  Recargar",
                Color.FromArgb(40, 40, 45), CText);
            btnReload.Size = new Size(100, 30);
            btnReload.Location = new Point(290, 7);
            btnReload.Click += (s, e) => ReloadList();
            footer.Controls.Add(btnReload);

            var btnClose = new Button
            {
                Text = "✕ CERRAR",
                FlatStyle = FlatStyle.Flat,
                BackColor = CBgPanel,
                ForeColor = CTextDim,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Size = new Size(110, 30),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                DialogResult = DialogResult.Cancel
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 30, 30);
            btnClose.Location = new Point(Width - btnClose.Width - 18, 7);
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
                        LastWrite = File.GetLastWriteTime(f),
                        IsLocked = IsProfileLocked(f)
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
                Size = new Size(_list.ClientSize.Width - 36, 130),
                Margin = new Padding(0, 0, 0, 12),
                BackColor = isActive ? CBgCardActive : CBgCard,
                Cursor = Cursors.Default
            };
            // Borde externo + barra de acento de 4px a la izquierda (como el
            // ModuleCard de FormAgroParallel y el card de VistaX-Core).
            card.Paint += (s, e) =>
            {
                using (var acc = new SolidBrush(isActive ? CAccent : Color.FromArgb(50, 50, 55)))
                    e.Graphics.FillRectangle(acc, 0, 0, 4, card.Height);
                using (var pen = new Pen(isActive ? CAccent : CBorder, 1))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            // Icono del implemento (chip redondeado con tractor).
            var iconChip = new Panel
            {
                Size = new Size(52, 52),
                Location = new Point(22, 28),
                BackColor = Color.FromArgb(45, 45, 50)
            };
            iconChip.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(isActive ? CAccent : Color.FromArgb(55, 55, 62)))
                    e.Graphics.FillEllipse(b, 0, 0, iconChip.Width - 1, iconChip.Height - 1);
                using (var f = new Font("Segoe UI Emoji", 16f))
                using (var br = new SolidBrush(isActive ? CBgDark : CTextDim))
                {
                    var txt = "\U0001F69C"; // tractor
                    var sz = e.Graphics.MeasureString(txt, f);
                    e.Graphics.DrawString(txt, f, br,
                        (iconChip.Width - sz.Width) / 2f,
                        (iconChip.Height - sz.Height) / 2f);
                }
            };
            card.Controls.Add(iconChip);

            // Nombre.
            var lblName = new Label
            {
                Text = string.IsNullOrEmpty(it.Config.Nombre)
                    ? Path.GetFileNameWithoutExtension(it.Path) : it.Config.Nombre,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(92, 18),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblName);

            // Badge ACTIVO (verde) inline al lado del nombre.
            if (isActive)
            {
                var badge = new Label
                {
                    Text = " ACTIVO ",
                    Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                    ForeColor = CBgDark,
                    BackColor = CAccent,
                    Location = new Point(lblName.Right + 8, 23),
                    AutoSize = true,
                    Padding = new Padding(4, 1, 4, 1)
                };
                card.Controls.Add(badge);
            }

            // Stats: NUMERO grande blanco + LABEL chico gris, columnas alineadas.
            AddStatTriplet(card, 92, 50, it.CountSurcos, "SURCOS");
            AddStatTriplet(card, 180, 50, it.CountTrenes, "TRENES");
            AddStatTriplet(card, 272, 50, it.CountSensores, "SENSORES");

            var lblDate = new Label
            {
                Text = "\U0001F551  " + it.LastWrite.ToString("yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture),
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = CTextFaint,
                Location = new Point(92, 84),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblDate);

            // Botones de accion a la derecha (compactos, estilo pill).
            int btnTop = (card.Height - 36) / 2;
            int btnRight = card.Width - 20;

            var btnActivate = MkPillButton(isActive ? "Activo" : "Activar",
                isActive ? Color.FromArgb(30, 30, 30) : CAccent,
                isActive ? CTextDim : CBgDark);
            btnActivate.Size = new Size(110, 36);
            btnActivate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnActivate.Location = new Point(btnRight - btnActivate.Width, btnTop);
            btnActivate.Enabled = !isActive;
            btnActivate.Click += (s, e) => ActivateItem(it);
            card.Controls.Add(btnActivate);

            var btnDuplicate = MkPillButton("\U0001F4CB  Duplicar",
                Color.FromArgb(50, 50, 55), CText);
            btnDuplicate.Size = new Size(100, 32);
            btnDuplicate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDuplicate.Location = new Point(btnActivate.Left - btnDuplicate.Width - 6, btnTop);
            btnDuplicate.Click += (s, e) => DuplicateItem(it);
            card.Controls.Add(btnDuplicate);

            // Segunda fila de botones: Bloquear, Exportar, Borrar.
            int btn2Top = btnTop + 40;

            var btnLock = MkPillButton(
                it.IsLocked ? "\U0001F512 Desbloq." : "\U0001F513 Bloquear",
                Color.FromArgb(40, 40, 45), it.IsLocked ? CAccent : CTextDim);
            btnLock.Size = new Size(100, 28);
            btnLock.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnLock.Location = new Point(btnRight - btnLock.Width, btn2Top);
            btnLock.Click += (s, e) => ToggleLock(it);
            card.Controls.Add(btnLock);

            var btnExport = MkPillButton("\U0001F4BE Exportar", Color.FromArgb(40, 40, 45), CText);
            btnExport.Size = new Size(90, 28);
            btnExport.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnExport.Location = new Point(btnLock.Left - btnExport.Width - 4, btn2Top);
            btnExport.Click += (s, e) => ExportItem(it);
            card.Controls.Add(btnExport);

            if (!isActive && !it.IsLocked)
            {
                var btnDelete = MkPillButton("\U0001F5D1 Borrar", Color.FromArgb(50, 20, 20),
                    Color.FromArgb(255, 23, 68));
                btnDelete.Size = new Size(80, 28);
                btnDelete.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                btnDelete.Location = new Point(btnExport.Left - btnDelete.Width - 4, btn2Top);
                btnDelete.Click += (s, e) => DeleteItem(it);
                card.Controls.Add(btnDelete);
            }

            if (it.IsLocked)
            {
                var lblLocked = new Label
                {
                    Text = "\U0001F512 BLOQUEADO",
                    Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(255, 234, 0),
                    BackColor = Color.Transparent,
                    Location = new Point(lblName.Right + 8, 23),
                    AutoSize = true, Padding = new Padding(4, 1, 4, 1)
                };
                card.Controls.Add(lblLocked);
            }

            return card;
        }

        private void AddStatTriplet(Control parent, int x, int y, int number, string label)
        {
            var lblN = new Label
            {
                Text = number.ToString(CultureInfo.InvariantCulture),
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = CText,
                Location = new Point(x, y),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lblN);
            var lblL = new Label
            {
                Text = label,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = CTextFaint,
                Location = new Point(x, y + 24),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lblL);
        }

        private string TryGetActiveProfileName()
        {
            try
            {
                if (string.IsNullOrEmpty(_cfg.ImplementoJsonPath)) return null;
                if (!File.Exists(_cfg.ImplementoJsonPath)) return null;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var json = File.ReadAllText(_cfg.ImplementoJsonPath);
                var c = JsonSerializer.Deserialize<ImplementoConfig>(json, opts);
                return c != null && !string.IsNullOrEmpty(c.Nombre)
                    ? c.Nombre
                    : Path.GetFileNameWithoutExtension(_cfg.ImplementoJsonPath);
            }
            catch { return null; }
        }

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

        private void DeleteItem(Item it)
        {
            if (it.IsLocked)
            {
                MessageBox.Show(this, "Este perfil est\u00E1 bloqueado. Desbloquealo primero.",
                    "Perfiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var r = MessageBox.Show(this,
                "Eliminar \"" + (it.Config.Nombre ?? System.IO.Path.GetFileNameWithoutExtension(it.Path)) + "\"?\n\nEsta acci\u00F3n no se puede deshacer.",
                "Eliminar Perfil", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            try
            {
                File.Delete(it.Path);
                // Delete lock file if exists.
                if (File.Exists(it.Path + ".lock"))
                    File.Delete(it.Path + ".lock");
                ReloadList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo eliminar: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ToggleLock(Item it)
        {
            string lockFile = it.Path + ".lock";

            if (it.IsLocked)
            {
                // Pedir password para desbloquear.
                string pass = PromptPassword("Ingres\u00E1 la contrase\u00F1a para desbloquear:");
                if (pass == null) return;

                try
                {
                    string stored = File.ReadAllText(lockFile).Trim();
                    if (stored != pass)
                    {
                        MessageBox.Show(this, "Contrase\u00F1a incorrecta.",
                            "Perfiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    File.Delete(lockFile);
                    ReloadList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Error: " + ex.Message,
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                // Pedir password para bloquear.
                string pass = PromptPassword("Defin\u00ED una contrase\u00F1a para bloquear este perfil:");
                if (string.IsNullOrEmpty(pass)) return;

                try
                {
                    File.WriteAllText(lockFile, pass);
                    ReloadList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Error: " + ex.Message,
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private string PromptPassword(string message)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Contrase\u00F1a";
                dlg.Size = new Size(360, 160);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.None;
                dlg.BackColor = CBgDark;
                dlg.ForeColor = CText;
                dlg.Font = new Font("Segoe UI", 10f);
                dlg.Paint += (s, e) =>
                {
                    using (var pen = new Pen(CBorder))
                        e.Graphics.DrawRectangle(pen, 0, 0, dlg.Width - 1, dlg.Height - 1);
                };

                dlg.Controls.Add(new Label
                {
                    Text = message, Font = new Font("Segoe UI", 10f),
                    ForeColor = CText, Location = new Point(20, 16),
                    AutoSize = true, BackColor = Color.Transparent
                });

                var txt = new TextBox
                {
                    UseSystemPasswordChar = true,
                    Font = new Font("Segoe UI", 12f),
                    BackColor = Color.FromArgb(15, 15, 15),
                    ForeColor = CAccent, BorderStyle = BorderStyle.FixedSingle,
                    Location = new Point(20, 50), Size = new Size(dlg.Width - 40, 28)
                };
                dlg.Controls.Add(txt);

                string result = null;
                var btnOk = MkPillButton("\u2713  OK", CAccent, CBgDark);
                btnOk.Size = new Size(80, 30);
                btnOk.Location = new Point(dlg.Width - 104, 100);
                btnOk.Click += (s, e) => { result = txt.Text; dlg.Close(); };
                dlg.Controls.Add(btnOk);

                var btnCancel = MkPillButton("Cancelar", Color.FromArgb(40, 40, 45), CTextDim);
                btnCancel.Size = new Size(80, 30);
                btnCancel.Location = new Point(btnOk.Left - 88, 100);
                btnCancel.Click += (s, e) => dlg.Close();
                dlg.Controls.Add(btnCancel);

                dlg.AcceptButton = btnOk;
                dlg.ShowDialog(this);
                return result;
            }
        }

        private void ExportItem(Item it)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Seleccion\u00E1 la unidad USB o carpeta de destino";
                if (fbd.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string destName = System.IO.Path.GetFileName(it.Path);
                    string destPath = System.IO.Path.Combine(fbd.SelectedPath, destName);
                    File.Copy(it.Path, destPath, true);
                    MessageBox.Show(this,
                        "Perfil exportado a:\n" + destPath,
                        "Exportar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Error exportando: " + ex.Message,
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CreateNewProfile()
        {
            string dir = ResolveImplementosDir();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string baseName = "nuevo_implemento";
            string candidate = System.IO.Path.Combine(dir, baseName + ".json");
            int n = 2;
            while (File.Exists(candidate))
            {
                candidate = System.IO.Path.Combine(dir, baseName + "_" + n + ".json");
                n++;
            }

            var impl = new ImplementoConfig
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Nombre = "Nuevo Implemento",
                Setup = new ImplementoSetup(),
                Trenes = new List<TrenConfig>
                {
                    new TrenConfig { Id = 1, Nombre = "Delantero", Surcos = 0 }
                },
                MapeoSensores = new List<SensorConfig>()
            };

            try
            {
                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                File.WriteAllText(candidate, JsonSerializer.Serialize(impl, opts));
                ReloadList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error creando perfil: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string ResolveImplementosDir()
        {
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "data", "implementos");
        }

    }
}
