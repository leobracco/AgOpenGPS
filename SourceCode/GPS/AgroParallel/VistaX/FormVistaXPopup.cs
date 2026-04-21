// ============================================================================
// FormVistaXPopup.cs - Ventana popup nativa de VistaX (sin CefSharp)
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/FormVistaXPopup.cs
// Target: net48 (C# 7.3)
//
// Wraps VistaXNativePanel en un Form resizable. Si FormGPS ya tiene un
// SeedMonitor activo, nos enganchamos al mismo; sino creamos uno propio
// asi el popup funciona aun si el panel embebido esta apagado.
// El FormGPS mantiene una unica instancia (singleton) via OpenAgroParallelModulePopup.
// ============================================================================

using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgroParallel.VistaX
{
    public class FormVistaXPopup : Form
    {
        private readonly VistaXNativePanel _panel;
        private readonly SeedMonitor _attachedMonitor;
        private readonly SeedMonitor _ownMonitor;

        public FormVistaXPopup(VistaXConfig cfg, SeedMonitor existingMonitor)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");

            Text = "VistaX";
            Size = new Size(900, 400);
            MinimumSize = new Size(480, 220);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(0, 0, 0);
            ShowInTaskbar = true;
            KeyPreview = true;

            _panel = new VistaXNativePanel(cfg);
            _panel.Dock = DockStyle.Fill;
            Controls.Add(_panel);

            if (existingMonitor != null && existingMonitor.IsRunning)
            {
                _attachedMonitor = existingMonitor;
                existingMonitor.SnapshotUpdated += _panel.SetSnapshot;
            }
            else
            {
                _ownMonitor = new SeedMonitor(null, cfg);
                _ownMonitor.SnapshotUpdated += _panel.SetSnapshot;
                _ = _ownMonitor.StartAsync();
            }

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                if (_attachedMonitor != null)
                    _attachedMonitor.SnapshotUpdated -= _panel.SetSnapshot;
            }
            catch { }

            // Dispose del monitor en background con timeout — MQTTnet puede
            // bloquear el Stop si esta en un backoff de reconexion, y si eso
            // corre en el hilo UI el Close() del Form nunca vuelve.
            if (_ownMonitor != null)
            {
                var m = _ownMonitor;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var stop = m.StopAsync();
                        stop.Wait(2000);
                    }
                    catch { }
                    try { m.Dispose(); } catch { }
                });
            }

            base.OnFormClosed(e);
        }
    }
}
