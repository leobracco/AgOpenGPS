// ============================================================================
// FormCamarasFloat.cs - Ventana flotante con grid de cámaras Hikvision.
// Se abre desde botón flotante en FormGPS. Draggable + resizable. Persiste
// posición/tamaño en Documents\AgOpenGPS\camaras_float.json (ventana).
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.Camaras
{
    public class FormCamarasFloat : Form
    {
        private readonly CamarasConfig _cfg;
        private FormCamarasMonitor _monitor;

        // Persistencia mínima de geometría
        private class Geom { public int x = 60, y = 60, w = 760, h = 520; }
        private static string GeomPath
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string dir = Path.Combine(baseDir, "AgOpenGPS");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "camaras_float.json");
            }
        }

        public FormCamarasFloat()
        {
            _cfg = CamarasConfig.Load();

            Text = "Cámaras";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            BackColor = Color.Black;
            ForeColor = Color.White;
            ShowInTaskbar = false;
            TopMost = true;
            MinimumSize = new Size(380, 260);
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;

            var g = LoadGeom();
            // Validar que la posición caiga dentro de algún screen
            try
            {
                var sb = Screen.GetWorkingArea(new Point(g.x, g.y));
                if (g.x < sb.X - 50) g.x = sb.X + 20;
                if (g.y < sb.Y - 50) g.y = sb.Y + 20;
            }
            catch { }
            Location = new Point(g.x, g.y);
            Size = new Size(g.w, g.h);

            _monitor = new FormCamarasMonitor(_cfg);
            _monitor.TopLevel = false;
            _monitor.FormBorderStyle = FormBorderStyle.None;
            _monitor.Dock = DockStyle.Fill;
            _monitor.Visible = true;
            Controls.Add(_monitor);

            FormClosing += (s, e) =>
            {
                SaveGeom(new Geom { x = Location.X, y = Location.Y, w = Size.Width, h = Size.Height });
                try { _monitor.Close(); } catch { }
            };

            // Esc minimiza, no cierra (para no perder accidentalmente)
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { WindowState = FormWindowState.Minimized; e.Handled = true; }
            };
        }

        private static Geom LoadGeom()
        {
            try
            {
                if (File.Exists(GeomPath))
                {
                    var opts = new JsonSerializerOptions { IncludeFields = true };
                    var g = JsonSerializer.Deserialize<Geom>(File.ReadAllText(GeomPath), opts);
                    if (g != null) return g;
                }
            }
            catch { }
            return new Geom();
        }

        private static void SaveGeom(Geom g)
        {
            try
            {
                var opts = new JsonSerializerOptions { IncludeFields = true };
                File.WriteAllText(GeomPath, JsonSerializer.Serialize(g, opts));
            }
            catch { }
        }
    }
}
