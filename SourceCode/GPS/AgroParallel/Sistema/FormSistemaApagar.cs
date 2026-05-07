// ============================================================================
// FormSistemaApagar.cs - Tab Apagar / Reiniciar / Cerrar PilotX
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.Sistema
{
    public class FormSistemaApagar : Form
    {
        public FormSistemaApagar()
        {
            Text = "Apagar";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.BgBlack;
            ForeColor = Theme.TextPrimary;
            Theme.ApplyToForm(this);
            BuildUI();
        }

        private void BuildUI()
        {
            var pnl = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBlack };

            var lbl = new Label
            {
                Text = "Energía",
                Font = new Font(Theme.FontFamily, 14f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                Location = new Point(40, 30),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            pnl.Controls.Add(lbl);

            int y = 70, h = 78, w = 320, gap = 8;
            // Columna izquierda: energía
            pnl.Controls.Add(MkAction("⏻  Apagar la PC", "Detiene PilotX y apaga el sistema.",
                Color.FromArgb(200, 60, 60), 40, y, w, h,
                () => Confirm("¿Apagar la PC?", () => Run("shutdown", "/s /t 0"))));

            pnl.Controls.Add(MkAction("⟳  Reiniciar la PC", "Detiene PilotX y reinicia el sistema.",
                Color.FromArgb(220, 140, 30), 40, y + (h + gap), w, h,
                () => Confirm("¿Reiniciar la PC?", () => Run("shutdown", "/r /t 0"))));

            pnl.Controls.Add(MkAction("✕  Cerrar PilotX", "Cierra solo la aplicación, sin apagar la PC.",
                Color.FromArgb(110, 110, 130), 40, y + 2 * (h + gap), w, h,
                () => Confirm("¿Cerrar PilotX?", () => Application.Exit())));

            pnl.Controls.Add(MkAction("⏸  Suspender", "Pone la PC en suspensión (S3).",
                Color.FromArgb(60, 130, 200), 40, y + 3 * (h + gap), w, h,
                () => Confirm("¿Suspender la PC?", () => Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"))));

            // Columna derecha: mantenimiento / escape kiosko
            pnl.Controls.Add(MkAction("🔒  Cerrar sesión", "Cierra la sesión Windows actual.",
                Color.FromArgb(100, 100, 200), 380, y, w, h,
                () => Confirm("¿Cerrar sesión Windows?", () => Run("shutdown", "/l"))));

            pnl.Controls.Add(MkAction("🛠  Administrador de Tareas", "Abre Task Manager (taskmgr.exe).",
                Color.FromArgb(160, 200, 230), 380, y + (h + gap), w, h,
                () => Run("taskmgr.exe", "")));

            pnl.Controls.Add(MkAction("📁  Explorador de Archivos", "Abre carpeta Descargas para ejecutar instalador.",
                Color.FromArgb(140, 180, 100), 380, y + 2 * (h + gap), w, h,
                () => OpenDownloads()));

            pnl.Controls.Add(MkAction("⬆  Buscar e instalar actualización", "Busca PilotX-Setup-*.exe en Descargas y lo ejecuta.",
                Color.FromArgb(113, 181, 40), 380, y + 3 * (h + gap), w, h,
                () => RunLatestInstaller()));

            Controls.Add(pnl);
        }

        private Panel MkAction(string text, string desc, Color accent, int x, int y, int w, int h, Action onClick)
        {
            var card = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = Theme.BgCard,
                Cursor = Cursors.Hand
            };
            var bar = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(4, h),
                BackColor = accent
            };
            var lbl1 = new Label
            {
                Text = text,
                Font = new Font(Theme.FontFamily, 12f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                Location = new Point(20, 14),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            var lbl2 = new Label
            {
                Text = desc,
                Font = new Font(Theme.FontFamily, 9f),
                ForeColor = Theme.TextSecondary,
                Location = new Point(20, 48),
                Size = new Size(w - 30, 32),
                BackColor = Color.Transparent
            };
            card.Controls.Add(bar);
            card.Controls.Add(lbl1);
            card.Controls.Add(lbl2);

            void Hover(bool h2) => card.BackColor = h2 ? Theme.BgCardHover : Theme.BgCard;
            card.MouseEnter += (s, e) => Hover(true);
            card.MouseLeave += (s, e) => Hover(false);
            lbl1.MouseEnter += (s, e) => Hover(true);
            lbl2.MouseEnter += (s, e) => Hover(true);
            lbl1.MouseLeave += (s, e) => Hover(false);
            lbl2.MouseLeave += (s, e) => Hover(false);
            EventHandler ch = (s, e) => onClick();
            card.Click += ch; lbl1.Click += ch; lbl2.Click += ch;
            return card;
        }

        private static void Confirm(string msg, Action onYes)
        {
            var r = MessageBox.Show(msg, "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
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

        // Abre el Explorador en %USERPROFILE%\Downloads (o Desktop si no existe).
        private static void OpenDownloads()
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dl = Path.Combine(profile, "Downloads");
                if (!Directory.Exists(dl))
                    dl = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dl + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        // Busca PilotX-Setup-x.y.z.exe en Descargas/Escritorio y ejecuta el de mayor versión.
        private static void RunLatestInstaller()
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
                        "No se encontró ningún instalador PilotX-Setup-*.exe en Descargas/Escritorio/Documentos.\n\n" +
                        "Copialo a la carpeta Descargas y volvé a tocar este botón.",
                        "Actualizar PilotX", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var r = MessageBox.Show(
                    "Instalador encontrado:\n\n" + Path.GetFileName(best) + "\nVersión: " + bestV +
                    "\n\n¿Ejecutar ahora? PilotX se cerrará durante la instalación.",
                    "Actualizar PilotX", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (r != DialogResult.Yes) return;

                Process.Start(new ProcessStartInfo(best) { UseShellExecute = true });
                // Cerrar PilotX para que el setup pueda reemplazar los .exe
                Application.Exit();
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }
    }
}
