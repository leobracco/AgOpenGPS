// ============================================================================
// FormSistemaAnyDesk.cs - Tab AnyDesk del módulo Sistema
// Lee el ID de AnyDesk desde C:\ProgramData\AnyDesk\system.conf  (línea ad.anynet.id=…)
// y permite copiarlo al portapapeles + abrir AnyDesk.
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.Sistema
{
    public class FormSistemaAnyDesk : Form
    {
        private Label _lblId;
        private Label _lblHint;
        private Button _btnCopy, _btnOpen, _btnRefresh;

        public FormSistemaAnyDesk()
        {
            Text = "AnyDesk";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.BgBlack;
            ForeColor = Theme.TextPrimary;
            Theme.ApplyToForm(this);
            BuildUI();
            Load += (s, e) => RefreshId();
        }

        private void BuildUI()
        {
            var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBlack };

            var card = new Panel
            {
                BackColor = Theme.BgCard,
                Width = 540,
                Height = 240,
                Anchor = AnchorStyles.None
            };
            card.Resize += (s, e) => CenterCard(center, card);
            center.Resize += (s, e) => CenterCard(center, card);

            var lblTitle = new Label
            {
                Text = "ID AnyDesk",
                Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
                ForeColor = Theme.TextSecondary,
                Location = new Point(20, 18),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblTitle);

            _lblId = new Label
            {
                Text = "—",
                Font = new Font("Consolas", 32f, FontStyle.Bold),
                ForeColor = Theme.Accent,
                Location = new Point(20, 50),
                Size = new Size(500, 60),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            card.Controls.Add(_lblId);

            _lblHint = new Label
            {
                Text = "Si AnyDesk no está instalado, este ID no estará disponible.",
                Font = new Font(Theme.FontFamily, 9f),
                ForeColor = Theme.TextDisabled,
                Location = new Point(20, 120),
                Size = new Size(500, 36),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            card.Controls.Add(_lblHint);

            _btnCopy = MkBtn("Copiar ID", 140);
            _btnCopy.Location = new Point(40, 175);
            _btnCopy.Click += (s, e) =>
            {
                var t = (_lblId.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(t) && t != "—")
                {
                    try { Clipboard.SetText(t); _lblHint.Text = "Copiado al portapapeles."; } catch { }
                }
            };
            card.Controls.Add(_btnCopy);

            _btnOpen = MkBtn("Abrir AnyDesk", 160);
            _btnOpen.Location = new Point(190, 175);
            _btnOpen.Click += (s, e) => OpenAnyDesk();
            card.Controls.Add(_btnOpen);

            _btnRefresh = MkBtn("Refrescar", 140);
            _btnRefresh.Location = new Point(360, 175);
            _btnRefresh.Click += (s, e) => RefreshId();
            card.Controls.Add(_btnRefresh);

            center.Controls.Add(card);
            Controls.Add(center);
        }

        private static void CenterCard(Panel host, Panel card)
        {
            card.Location = new Point((host.Width - card.Width) / 2, Math.Max(40, (host.Height - card.Height) / 2));
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

        private void RefreshId()
        {
            string id = TryReadId();
            if (string.IsNullOrEmpty(id))
            {
                _lblId.Text = "—";
                _lblHint.Text = "No se encontró el archivo de configuración de AnyDesk.";
                return;
            }
            // Formato típico: 9 dígitos. Mostrarlo separado en grupos de 3.
            string pretty = id;
            if (id.Length == 9)
                pretty = id.Substring(0, 3) + " " + id.Substring(3, 3) + " " + id.Substring(6, 3);
            _lblId.Text = pretty;
            _lblHint.Text = "Compartilo con soporte para asistencia remota.";
        }

        private static string TryReadId()
        {
            string[] candidates = new[]
            {
                @"C:\ProgramData\AnyDesk\system.conf",
                @"C:\ProgramData\AnyDesk\service.conf",
                Environment.ExpandEnvironmentVariables(@"%APPDATA%\AnyDesk\user.conf"),
                Environment.ExpandEnvironmentVariables(@"%APPDATA%\AnyDesk\system.conf")
            };
            foreach (var path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    foreach (var line in File.ReadAllLines(path))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("ad.anynet.id", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = t.IndexOf('=');
                            if (idx > 0) return t.Substring(idx + 1).Trim();
                        }
                    }
                }
                catch { }
            }
            return "";
        }

        private static void OpenAnyDesk()
        {
            string[] candidates = new[]
            {
                @"C:\Program Files (x86)\AnyDesk\AnyDesk.exe",
                @"C:\Program Files\AnyDesk\AnyDesk.exe"
            };
            foreach (var p in candidates)
            {
                if (File.Exists(p))
                {
                    try { Process.Start(p); return; } catch { }
                }
            }
            try { Process.Start("anydesk:"); } catch { }
        }
    }
}
