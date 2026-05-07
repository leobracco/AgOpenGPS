// ============================================================================
// FormPilotXShell.cs - Pantalla "shell" que aparece cuando AOG se cierra
// en modo embebido. Muestra logo + botones (Reabrir / Apagar / Reiniciar /
// Suspender / Salir a Windows). Fullscreen, sin chrome, sin escape.
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.Sistema
{
    public class FormPilotXShell : Form
    {
        public enum ShellAction { Reabrir, Salir }
        public ShellAction Result { get; private set; } = ShellAction.Salir;

        private Image _logo;

        public FormPilotXShell()
        {
            Text = "PilotX";
            FormBorderStyle = FormBorderStyle.None;
            // No usamos WindowState.Maximized porque con FormBorderStyle.None
            // y kiosk a veces queda invisible. Setamos Bounds explícitos.
            StartPosition = FormStartPosition.Manual;
            try
            {
                var b = Screen.PrimaryScreen.Bounds;
                Location = b.Location;
                Size = b.Size;
            }
            catch
            {
                Location = new Point(0, 0);
                Size = new Size(1920, 1080);
            }
            BackColor = Color.FromArgb(8, 10, 12);
            ForeColor = Color.White;
            ShowInTaskbar = true;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;

            try { _logo = TryLoadLogo(); } catch { _logo = null; }
            try { BuildUI(); } catch { /* render mínimo si UI falla */ }
            Paint += DrawBackground;
            Resize += (s, e) => Invalidate();
            Shown += (s, e) =>
            {
                try { Activate(); BringToFront(); } catch { }
            };
        }

        private static Image TryLoadLogo()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                string[] candidates = new[]
                {
                    Path.Combine(exeDir, "Branding", "logo-fondo-blanco.png"),
                    Path.Combine(exeDir, "Branding", "logo.png"),
                    Path.Combine(exeDir, "Branding", "favicon.png")
                };
                foreach (var p in candidates)
                    if (File.Exists(p)) return Image.FromFile(p);
            }
            catch { }
            return null;
        }

        private void DrawBackground(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Gradiente vertical de fondo
            using (var br = new LinearGradientBrush(
                new Rectangle(0, 0, Width, Height),
                Color.FromArgb(15, 20, 18),
                Color.FromArgb(4, 6, 5),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(br, 0, 0, Width, Height);
            }

            // Logo grande centrado, arriba
            int logoH = 0, logoBottom = 0;
            if (_logo != null)
            {
                logoH = Math.Min(180, Height / 5);
                int targetW = (int)(_logo.Width * (logoH / (float)_logo.Height));
                int x = (Width - targetW) / 2;
                int y = (int)(Height * 0.06);
                g.DrawImage(_logo, x, y, targetW, logoH);
                logoBottom = y + logoH;
            }
            else
            {
                logoBottom = (int)(Height * 0.10);
            }

            // Texto bajo el logo
            using (var f = new Font("Segoe UI", 26f, FontStyle.Bold))
            using (var br = new SolidBrush(Color.FromArgb(220, 230, 220)))
            {
                string txt = "PilotX";
                var sz = g.MeasureString(txt, f);
                g.DrawString(txt, f, br, (Width - sz.Width) / 2f, logoBottom + 16);
            }
            using (var f = new Font("Segoe UI", 11f))
            using (var br = new SolidBrush(Color.FromArgb(140, 160, 145)))
            {
                string sub = "Agro Parallel";
                var sz = g.MeasureString(sub, f);
                g.DrawString(sub, f, br, (Width - sz.Width) / 2f, logoBottom + 60);
            }
        }

        private void BuildUI()
        {
            int btnW = 280, btnH = 78, gap = 18;

            var pnlBtns = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 3,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.None,
                AutoSize = true
            };
            pnlBtns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, btnW + gap));
            pnlBtns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, btnW + gap));
            for (int i = 0; i < 3; i++)
                pnlBtns.RowStyles.Add(new RowStyle(SizeType.Absolute, btnH + gap));

            pnlBtns.Controls.Add(MkBtn("▶  Abrir PilotX", Color.FromArgb(113, 181, 40), btnW, btnH,
                () => { Result = ShellAction.Reabrir; Close(); }), 0, 0);
            pnlBtns.Controls.Add(MkBtn("🌐  Sistema (WiFi/IP)", Color.FromArgb(160, 200, 230), btnW, btnH,
                () => OpenSistemaQuickAccess()), 1, 0);
            pnlBtns.Controls.Add(MkBtn("⟳  Reiniciar PC", Color.FromArgb(220, 140, 30), btnW, btnH,
                () => Confirm("¿Reiniciar la PC?", () => Run("shutdown", "/r /t 0"))), 0, 1);
            pnlBtns.Controls.Add(MkBtn("⏻  Apagar PC", Color.FromArgb(200, 60, 60), btnW, btnH,
                () => Confirm("¿Apagar la PC?", () => Run("shutdown", "/s /t 0"))), 1, 1);
            pnlBtns.Controls.Add(MkBtn("🛠  Admin. Tareas", Color.FromArgb(120, 140, 170), btnW, btnH,
                () => Run("taskmgr.exe", "")), 0, 2);
            pnlBtns.Controls.Add(MkBtn("⬆  Actualizar PilotX", Color.FromArgb(80, 150, 200), btnW, btnH,
                () => RunLatestInstaller()), 1, 2);

            // Centrar — los botones quedan en la mitad inferior, debajo del texto
            // PilotX / Agro Parallel (que se pinta en DrawBackground).
            var holder = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            holder.Controls.Add(pnlBtns);
            void Center()
            {
                // Logo ≈ 6%..(6%+20%) del alto, texto ocupa otros ~80px → botones desde ~50%
                int top = Math.Max((int)(holder.Height * 0.50), 320);
                pnlBtns.Location = new Point(
                    Math.Max(0, (holder.Width - pnlBtns.Width) / 2),
                    Math.Min(top, holder.Height - pnlBtns.Height - 40));
            }
            holder.Resize += (s, e) => Center();
            pnlBtns.SizeChanged += (s, e) => Center();
            Controls.Add(holder);

            // Footer pequeño
            var lblFooter = new Label
            {
                Text = "PilotX modo embebido — la pantalla está bloqueada en este menú hasta que abras o apagues.",
                Dock = DockStyle.Bottom,
                Height = 32,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(110, 130, 115),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(lblFooter);
        }

        private Button MkBtn(string text, Color accent, int w, int h, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                Width = w,
                Height = h,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                BackColor = accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Margin = new Padding(12),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(accent, 0.15f);
            b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(accent, 0.15f);
            b.Click += (s, e) => onClick();
            return b;
        }

        private static void Confirm(string msg, Action onYes)
        {
            var r = MessageBox.Show(msg, "Confirmar",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Yes) onYes();
        }

        private static void Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, CreateNoWindow = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
        }

        // Busca PilotX-Setup-x.y.z.exe en Descargas/Escritorio/Documentos y ejecuta el de mayor versión.
        // Útil cuando AOG está cerrado y solo se ve el shell — permite actualizar sin Explorador.
        private void RunLatestInstaller()
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var dirs = new[]
                {
                    Path.Combine(profile, "Downloads"),
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Path.Combine(profile, "Documents")
                };

                var rx = new Regex(@"PilotX-Setup-(\d+)\.(\d+)\.(\d+)\.exe$", RegexOptions.IgnoreCase);
                string best = null;
                Version bestV = null;
                foreach (var d in dirs)
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "PilotX-Setup-*.exe"))
                    {
                        var m = rx.Match(f);
                        if (!m.Success) continue;
                        var v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                        if (bestV == null || v > bestV) { bestV = v; best = f; }
                    }
                }

                if (best == null)
                {
                    MessageBox.Show(
                        "No se encontró ningún instalador PilotX-Setup-*.exe en Descargas / Escritorio / Documentos.\n\n" +
                        "Copialo a la carpeta Descargas y volvé a tocar este botón.",
                        "Actualizar PilotX", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var r = MessageBox.Show(
                    "Instalador encontrado:\n\n" + Path.GetFileName(best) + "\nVersión: " + bestV +
                    "\n\n¿Ejecutar ahora?",
                    "Actualizar PilotX", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (r != DialogResult.Yes) return;

                Process.Start(new ProcessStartInfo(best) { UseShellExecute = true });
                // Cerramos el shell para liberar pantalla mientras corre el setup.
                Result = ShellAction.Salir;
                Close();
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        // Acceso rápido a config WiFi/IP sin tener que reabrir AOG completo.
        private void OpenSistemaQuickAccess()
        {
            try
            {
                var dlg = new Form
                {
                    Text = "Sistema",
                    Size = new Size(900, 620),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    BackColor = Color.FromArgb(10, 12, 10)
                };
                var tab = new TabControl { Dock = DockStyle.Fill };
                AddTab(tab, "WiFi", new FormSistemaWifi());
                AddTab(tab, "Red", new FormSistemaRed());
                AddTab(tab, "AnyDesk", new FormSistemaAnyDesk());
                AddTab(tab, "Brillo", new FormSistemaBrillo());
                dlg.Controls.Add(tab);
                dlg.ShowDialog(this);
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        private static void AddTab(TabControl tc, string title, Form embedded)
        {
            var page = new TabPage(title) { BackColor = Color.FromArgb(10, 12, 10) };
            embedded.TopLevel = false;
            embedded.FormBorderStyle = FormBorderStyle.None;
            embedded.Dock = DockStyle.Fill;
            embedded.Visible = true;
            page.Controls.Add(embedded);
            tc.TabPages.Add(page);
        }

        // Bloquear Alt+F4 / Ctrl+Esc / X para que solo se salga via los botones
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Permitimos salida solo si Result fue seteado a Reabrir o Salir por botón
            // (en cualquier caso seguimos cerrando — la decisión está en Result).
            base.OnFormClosing(e);
        }
    }
}
