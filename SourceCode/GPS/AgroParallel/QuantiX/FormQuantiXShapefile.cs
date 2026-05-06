// ============================================================================
// FormQuantiXShapefile.cs - Gestión de mapas de prescripción
// Tab "Mapas" unificado: lista de archivos guardados + shapefile activo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using AgroParallel.Common;
using AgroParallel.VistaX;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXShapefile : Form
    {
        private readonly AgOpenGPS.FormGPS _parent;
        private Panel _canvas;
        private FlowLayoutPanel _fileList;

        public FormQuantiXShapefile(AgOpenGPS.FormGPS parent)
        {
            _parent = parent;
            BuildUI();
        }

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            // ── Footer con botones de acción ──
            var footer = new Panel
            {
                Dock = DockStyle.Bottom, Height = 54,
                BackColor = Theme.BgToolbar
            };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(Theme.Border))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            int bx = 20;
            var btnLoad = Theme.MkAccentButton("\U0001F4C2  CARGAR", 120, 36);
            btnLoad.Location = new Point(bx, 9);
            btnLoad.Click += (s, ev) =>
            {
                CallFormGPS("OpenShapefileLoadDialog");
                RebuildFileList();
            };
            footer.Controls.Add(btnLoad);
            bx += 130;

            var btnStyle = Theme.MkButton("\U0001F3A8  ESTILO", Theme.BgCard, Theme.TextPrimary, 110, 36);
            btnStyle.Location = new Point(bx, 9);
            btnStyle.Click += (s, ev) => CallFormGPS("OpenShapefileStyleDialog");
            footer.Controls.Add(btnStyle);
            bx += 120;

            var btnToggle = Theme.MkButton("\U0001F441  VER/OCULTAR", Theme.BgCard, Theme.TextPrimary, 140, 36);
            btnToggle.Location = new Point(bx, 9);
            btnToggle.Click += (s, ev) => { ToggleVisibility(); _canvas.Invalidate(); };
            footer.Controls.Add(btnToggle);
            bx += 150;

            var btnExport = Theme.MkButton("\U0001F4BE  EXPORTAR", Theme.BgCard, Theme.TextPrimary, 120, 36);
            btnExport.Location = new Point(bx, 9);
            btnExport.Click += (s, ev) => CallFormGPS("OpenShapefileExportDialog");
            footer.Controls.Add(btnExport);

            Controls.Add(footer);

            // ── Panel superior: shapefile activo ──
            _canvas = new Panel
            {
                Dock = DockStyle.Top, Height = 140,
                BackColor = Theme.BgBlack
            };
            _canvas.Paint += PaintActiveLayer;
            Controls.Add(_canvas);

            // ── Separador + título ──
            var lblTitle = new Label
            {
                Dock = DockStyle.Top, Height = 30,
                Text = "   MAPAS DISPONIBLES",
                Font = new Font(Theme.FontFamily, 9f, FontStyle.Bold),
                ForeColor = Theme.TextFaint,
                BackColor = Color.FromArgb(6, 6, 8),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(lblTitle);

            // ── Lista de archivos ──
            _fileList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.BgBlack,
                Padding = new Padding(12, 8, 12, 8)
            };
            _fileList.Resize += (s, ev) =>
            {
                int w = _fileList.ClientSize.Width - _fileList.Padding.Left - _fileList.Padding.Right;
                foreach (Control c in _fileList.Controls)
                    if (c.Width != w) c.Width = w;
            };
            Controls.Add(_fileList);

            // Order: fill first, then top controls
            _fileList.BringToFront();

            RebuildFileList();
        }

        // ── Listar archivos disponibles ──
        private void RebuildFileList()
        {
            _fileList.SuspendLayout();
            _fileList.Controls.Clear();
            int cardW = _fileList.ClientSize.Width - _fileList.Padding.Left - _fileList.Padding.Right;
            if (cardW < 100) cardW = 600;

            var files = new List<FileInfo>();

            // 1. Prescripciones sincronizadas por OrbitX.
            string prescDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "prescripciones");
            if (Directory.Exists(prescDir))
            {
                foreach (var f in Directory.GetFiles(prescDir, "*.geojson"))
                    files.Add(new FileInfo(f));
                foreach (var f in Directory.GetFiles(prescDir, "*.json"))
                    if (!f.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase))
                        files.Add(new FileInfo(f));
            }

            // 2. Shapefiles en el directorio de Fields.
            try
            {
                var regType = typeof(AgOpenGPS.RegistrySettings);
                var fieldsDirField = regType.GetField("fieldsDirectory",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (fieldsDirField != null)
                {
                    string fieldsDir = fieldsDirField.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(fieldsDir) && Directory.Exists(fieldsDir))
                    {
                        foreach (var shp in Directory.GetFiles(fieldsDir, "*.shp", SearchOption.AllDirectories))
                            files.Add(new FileInfo(shp));
                        foreach (var gj in Directory.GetFiles(fieldsDir, "*.geojson", SearchOption.AllDirectories))
                            files.Add(new FileInfo(gj));
                    }
                }
            }
            catch { }

            // 3. Directorio base de la app.
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var shp in Directory.GetFiles(appDir, "*.shp"))
                files.Add(new FileInfo(shp));
            foreach (var gj in Directory.GetFiles(appDir, "*.geojson"))
                files.Add(new FileInfo(gj));

            // Deduplicar por full path.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<FileInfo>();
            foreach (var f in files)
            {
                if (seen.Add(f.FullName))
                    unique.Add(f);
            }

            // Ordenar por fecha (más reciente primero).
            unique.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));

            if (unique.Count == 0)
            {
                var lbl = new Label
                {
                    Text = "No se encontraron mapas.\n\n"
                        + "Us\u00E1 CARGAR para importar un .shp o .geojson,\n"
                        + "o sincroniz\u00E1 prescripciones desde OrbitX.",
                    Font = new Font(Theme.FontFamily, 10f),
                    ForeColor = Theme.TextFaint,
                    BackColor = Color.Transparent,
                    Size = new Size(cardW, 80),
                    Padding = new Padding(12, 8, 0, 0)
                };
                _fileList.Controls.Add(lbl);
            }
            else
            {
                foreach (var fi in unique)
                    _fileList.Controls.Add(MkFileCard(fi, cardW));
            }

            _fileList.ResumeLayout();
        }

        private Panel MkFileCard(FileInfo fi, int cardW)
        {
            var card = new Panel
            {
                Size = new Size(cardW, 56),
                Margin = new Padding(0, 0, 0, 4),
                BackColor = Theme.BgCard,
                Cursor = Cursors.Hand
            };

            bool isOrbitX = fi.FullName.IndexOf("prescripciones", StringComparison.OrdinalIgnoreCase) >= 0;
            string ext = fi.Extension.ToUpperInvariant().TrimStart('.');
            string icon = isOrbitX ? "\u2601" : "\U0001F5FA";
            string source = isOrbitX ? "OrbitX" : ext;
            Color accent = isOrbitX ? Color.FromArgb(100, 180, 255) : Theme.Accent;

            card.Paint += (s, ev) =>
            {
                var g = ev.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Theme.FillRoundedRect(g, new Rectangle(0, 0, card.Width - 1, card.Height - 1),
                    Theme.BgCard, 6);
                Theme.DrawRoundedBorder(g, new Rectangle(0, 0, card.Width - 1, card.Height - 1),
                    Theme.Border, 6);

                // Accent bar.
                using (var br = new SolidBrush(accent))
                    g.FillRectangle(br, 0, 6, 3, card.Height - 12);

                // Icon.
                using (var f = new Font(Theme.FontFamily, 14f))
                using (var br = new SolidBrush(accent))
                    g.DrawString(icon, f, br, 12, 14);

                // Filename.
                string name = Path.GetFileNameWithoutExtension(fi.Name);
                using (var f = new Font(Theme.FontFamily, 10.5f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.TextPrimary))
                    g.DrawString(name, f, br, 38, 8);

                // Details.
                string details = source + "  \u00B7  "
                    + (fi.Length / 1024) + " KB  \u00B7  "
                    + fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm");
                using (var f = Theme.FontSmall)
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString(details, f, br, 38, 30);
            };

            string fullPath = fi.FullName;
            card.Click += (s, ev) => LoadFile(fullPath);
            // Make child labels also clickable.
            foreach (Control c in card.Controls)
                c.Click += (s, ev) => LoadFile(fullPath);

            return card;
        }

        private void LoadFile(string path)
        {
            try
            {
                var method = _parent.GetType().GetMethod("LoadShapefileFromPath",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    // LoadShapefileFromPath(string, bool showSummary, bool promptStyle, ...)
                    var parms = method.GetParameters();
                    if (parms.Length >= 3)
                        method.Invoke(_parent, new object[] { path, true, true, null, true, true, true });
                    else if (parms.Length == 1)
                        method.Invoke(_parent, new object[] { path });
                    else
                        method.Invoke(_parent, new object[] { path, true, true });
                }
                _canvas.Invalidate();
            }
            catch (Exception ex)
            {
                string msg = ex.InnerException?.Message ?? ex.Message;
                MessageBox.Show(this, "Error cargando mapa:\n\n" + msg,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── Shapefile activo ──
        private void PaintActiveLayer(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.BgBlack);

            int y = 14;
            using (var f = Theme.FontTitle)
            using (var br = new SolidBrush(Theme.Accent))
                g.DrawString("\U0001F5FA  MAPAS DE PRESCRIPCI\u00D3N", f, br, 24, y);
            y += 32;

            bool hasLayer = false;
            bool isVisible = false;
            int polyCount = 0;
            string styleField = "";

            try
            {
                var layerField = _parent.GetType().GetField("shapefileLayer",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (layerField != null)
                {
                    object layer = layerField.GetValue(_parent);
                    if (layer != null)
                    {
                        hasLayer = true;
                        var t = layer.GetType();
                        var pVis = t.GetProperty("IsVisible");
                        var pPoly = t.GetProperty("PolygonCount");
                        var pStyle = t.GetProperty("StyleField");

                        if (pVis != null) isVisible = (bool)pVis.GetValue(layer);
                        if (pPoly != null) polyCount = (int)pPoly.GetValue(layer);
                        if (pStyle != null) styleField = (string)pStyle.GetValue(layer) ?? "";
                    }
                }
            }
            catch { }

            if (hasLayer)
            {
                var cardRect = new Rectangle(20, y, _canvas.Width - 40, 76);
                Theme.FillRoundedRect(g, cardRect, Theme.BgCard, Theme.BorderRadius);
                Theme.DrawRoundedBorder(g, cardRect, Theme.Border, Theme.BorderRadius);
                using (var acc = new SolidBrush(Theme.Accent))
                    g.FillRectangle(acc, 20, y + 6, 3, 64);

                string estado = isVisible ? "\u25CF Activo" : "\u25CB Oculto";
                Color estColor = isVisible ? Theme.Accent : Theme.TextFaint;

                using (var f = new Font(Theme.FontFamily, 11f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.TextPrimary))
                    g.DrawString("Mapa cargado", f, br, 36, y + 10);

                using (var f = Theme.FontBody)
                using (var br = new SolidBrush(estColor))
                    g.DrawString(estado, f, br, 160, y + 12);

                using (var f = Theme.FontBody)
                using (var br = new SolidBrush(Theme.TextSecondary))
                    g.DrawString(polyCount + " zonas  \u00B7  Campo: "
                        + (string.IsNullOrEmpty(styleField) ? "(ninguno)" : styleField),
                        f, br, 36, y + 36);

                using (var f = Theme.FontSmall)
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString("ESTILO para cambiar campo de dosis, VER/OCULTAR para toggle en piloto.",
                        f, br, 36, y + 56);
            }
            else
            {
                using (var f = new Font(Theme.FontFamily, 10f))
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString("Ning\u00FAn mapa activo. Seleccion\u00E1 uno de la lista o us\u00E1 CARGAR.",
                        f, br, 24, y);
            }
        }

        private void CallFormGPS(string methodName)
        {
            try
            {
                var method = _parent.GetType().GetMethod(methodName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (method != null)
                    method.Invoke(_parent, null);
                _canvas.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error: " + (ex.InnerException?.Message ?? ex.Message),
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ToggleVisibility()
        {
            try
            {
                var layerField = _parent.GetType().GetField("shapefileLayer",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (layerField != null)
                {
                    object layer = layerField.GetValue(_parent);
                    if (layer != null)
                    {
                        var pVis = layer.GetType().GetProperty("IsVisible");
                        if (pVis != null)
                        {
                            bool current = (bool)pVis.GetValue(layer);
                            pVis.SetValue(layer, !current);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
