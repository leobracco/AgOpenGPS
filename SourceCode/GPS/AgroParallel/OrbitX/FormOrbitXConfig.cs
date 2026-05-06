// ============================================================================
// FormOrbitXConfig.cs - Configuración de OrbitX Cloud en el Hub
// ============================================================================

using System;
using System.Drawing;
using System.Windows.Forms;
using AgroParallel.Common;
using AgroParallel.VistaX;

namespace AgroParallel.OrbitX
{
    public class FormOrbitXConfig : Form
    {
        private readonly OrbitXConfig _cfg;
        private readonly OrbitXSync _sync;
        private Timer _statusTimer;

        public FormOrbitXConfig(OrbitXConfig cfg, OrbitXSync sync)
        {
            _cfg = cfg ?? OrbitXConfig.Load();
            _sync = sync;
            BuildUI();
        }

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.BgBlack };
            Controls.Add(body);

            int lx = 24, fx = 200, y = 16;

            // ── Habilitado ──────────────────────────────────────────────
            var chkEnabled = new CheckBox
            {
                Text = "  ORBITX CLOUD HABILITADO", Checked = _cfg.Enabled,
                Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
                ForeColor = _cfg.Enabled ? Theme.Accent : Theme.Error,
                BackColor = Color.Transparent,
                Location = new Point(lx, y), AutoSize = true
            };
            chkEnabled.CheckedChanged += (s, ev) =>
            {
                _cfg.Enabled = chkEnabled.Checked;
                chkEnabled.ForeColor = _cfg.Enabled ? Theme.Accent : Theme.Error;
            };
            body.Controls.Add(chkEnabled);
            y += 36;

            // ── Conexión ────────────────────────────────────────────────
            AddSection(body, "\U0001F310  CONEXI\u00D3N", lx, ref y);

            AddRow(body, "Server URL:", lx, fx, ref y, _cfg.ServerUrl,
                v => _cfg.ServerUrl = v, 340);
            AddRow(body, "Device Token:", lx, fx, ref y, _cfg.DeviceToken,
                v => _cfg.DeviceToken = v, 340, true);

            // Device ID (read only).
            body.Controls.Add(new Label
            {
                Text = "Device ID:", Font = Theme.FontBody, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent, Location = new Point(lx, y + 2), AutoSize = true
            });
            body.Controls.Add(new Label
            {
                Text = _cfg.DeviceId, Font = Theme.FontMono, ForeColor = Theme.Accent,
                BackColor = Color.Transparent, Location = new Point(fx, y + 2), AutoSize = true
            });
            y += 24;

            body.Controls.Add(new Label
            {
                Text = "Establecimiento:", Font = Theme.FontBody, ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent, Location = new Point(lx, y + 2), AutoSize = true
            });
            body.Controls.Add(new Label
            {
                Text = string.IsNullOrEmpty(_cfg.EstabSlug) ? "(sin asignar)" : _cfg.EstabSlug,
                Font = Theme.FontMono,
                ForeColor = string.IsNullOrEmpty(_cfg.EstabSlug) ? Theme.Warning : Theme.Accent,
                BackColor = Color.Transparent, Location = new Point(fx, y + 2), AutoSize = true
            });
            y += 30;

            // ── Módulos a sincronizar ────────────────────────────────────
            AddSection(body, "\U0001F4E6  M\u00D3DULOS A SINCRONIZAR", lx, ref y);

            AddCheck(body, "AgOpenGPS (campos, l\u00EDmites, gu\u00EDas)", lx, ref y,
                _cfg.SyncAOG, v => _cfg.SyncAOG = v);
            AddCheck(body, "VistaX (perfiles, logs, mapas de siembra)", lx, ref y,
                _cfg.SyncVistaX, v => _cfg.SyncVistaX = v);
            AddCheck(body, "QuantiX (motores, calibraci\u00F3n, dosis)", lx, ref y,
                _cfg.SyncQuantiX, v => _cfg.SyncQuantiX = v);
            AddCheck(body, "SectionX (secciones, trenes)", lx, ref y,
                _cfg.SyncSectionX, v => _cfg.SyncSectionX = v);
            y += 8;

            // ── Intervalo ───────────────────────────────────────────────
            AddSection(body, "\u23F1  INTERVALO", lx, ref y);
            body.Controls.Add(new Label
            {
                Text = "Sync cada (seg):", Font = Theme.FontBody, ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent, Location = new Point(lx, y + 2), AutoSize = true
            });
            var numInterval = new NumericUpDown
            {
                Minimum = 5, Maximum = 300, Value = _cfg.SyncIntervalSec,
                Font = new Font(Theme.FontFamily, 10f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Theme.BgInput,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(fx, y), Size = new Size(80, 24)
            };
            numInterval.ValueChanged += (s, ev) => _cfg.SyncIntervalSec = (int)numInterval.Value;
            body.Controls.Add(numInterval);
            y += 34;

            // ── Estado ──────────────────────────────────────────────────
            AddSection(body, "\U0001F4CA  ESTADO", lx, ref y);

            var lblStatus = new Label
            {
                Name = "lblStatus",
                Text = "...",
                Font = Theme.FontBody, ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                Location = new Point(lx, y), Size = new Size(400, 50)
            };
            body.Controls.Add(lblStatus);

            // Timer de status.
            _statusTimer = new Timer { Interval = 1000 };
            _statusTimer.Tick += (s, ev) =>
            {
                // Buscar sync vivo en FormGPS.
                OrbitXSync liveSync = _sync;
                if (liveSync == null)
                {
                    try
                    {
                        foreach (Form f in Application.OpenForms)
                            if (f is AgOpenGPS.FormGPS gps && gps.orbitXSync != null)
                            { liveSync = gps.orbitXSync; break; }
                    }
                    catch { }
                }
                if (liveSync == null) { lblStatus.Text = "Sync no iniciado. Guard\u00E1 la config para activar."; return; }
                string status = liveSync.IsRunning ? "\u25CF Activo" : "\u25CB Detenido";
                status += " \u2014 " + liveSync.FilesSynced + " archivos sincronizados";
                if (liveSync.LastSyncTime.HasValue)
                    status += "\n\u00DAltimo sync: " + liveSync.LastSyncTime.Value.ToString("HH:mm:ss");
                if (!string.IsNullOrEmpty(liveSync.LastError))
                    status += "\n\u26A0 " + liveSync.LastError;
                lblStatus.Text = status;
                lblStatus.ForeColor = liveSync.IsRunning ? Theme.Accent : Theme.TextSecondary;
            };
            _statusTimer.Start();

            // ── Footer ──────────────────────────────────────────────────
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = Theme.BgToolbar };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(Theme.Border))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            var btnSave = Theme.MkAccentButton("\u2713  GUARDAR", 120, 34);
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Click += (s, ev) =>
            {
                _cfg.Save();
                // Recargar el sync en caliente.
                try
                {
                    foreach (Form f in Application.OpenForms)
                        if (f is AgOpenGPS.FormGPS gps)
                        { gps.ReloadOrbitXSync(); break; }
                }
                catch { }
            };
            footer.Controls.Add(btnSave);

            var btnSync = Theme.MkButton("\U0001F504  SYNC AHORA", Color.FromArgb(40, 40, 45),
                Theme.TextPrimary, 140, 34);
            btnSync.Location = new Point(20, 8);
            btnSync.Click += (s, ev) =>
            {
                _cfg.Save();
                // Asegurar que el sync esté corriendo.
                try
                {
                    foreach (Form f in Application.OpenForms)
                        if (f is AgOpenGPS.FormGPS gps)
                        {
                            if (gps.orbitXSync == null || !gps.orbitXSync.IsRunning)
                                gps.ReloadOrbitXSync();
                            break;
                        }
                }
                catch { }
            };
            footer.Controls.Add(btnSync);

            footer.Resize += (s, ev) =>
                btnSave.Location = new Point(footer.Width - btnSave.Width - 20, 8);

            Controls.Add(footer);
            body.BringToFront();
        }

        private void AddSection(Control parent, string text, int x, ref int y)
        {
            parent.Controls.Add(new Label
            {
                Text = text, Font = new Font(Theme.FontFamily, 9.5f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = true
            });
            y += 22;
        }

        private void AddRow(Control parent, string label, int lx, int fx, ref int y,
            string value, Action<string> onChange, int w, bool isPassword = false)
        {
            parent.Controls.Add(new Label
            {
                Text = label, Font = Theme.FontBody, ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent, Location = new Point(lx, y + 2), AutoSize = true
            });
            var txt = new TextBox
            {
                Text = value ?? "", Font = Theme.FontMono, ForeColor = Theme.TextPrimary,
                BackColor = Theme.BgInput, BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = isPassword,
                Location = new Point(fx, y), Size = new Size(w, 24)
            };
            txt.Leave += (s, ev) => onChange(txt.Text.Trim());
            parent.Controls.Add(txt);
            y += 28;
        }

        private void AddCheck(Control parent, string text, int x, ref int y,
            bool initial, Action<bool> onChange)
        {
            var chk = new CheckBox
            {
                Text = "  " + text, Checked = initial,
                Font = Theme.FontBody, ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent,
                Location = new Point(x + 16, y), AutoSize = true
            };
            chk.CheckedChanged += (s, ev) => onChange(chk.Checked);
            parent.Controls.Add(chk);
            y += 26;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_statusTimer != null) { _statusTimer.Stop(); _statusTimer.Dispose(); }
        }
    }
}
