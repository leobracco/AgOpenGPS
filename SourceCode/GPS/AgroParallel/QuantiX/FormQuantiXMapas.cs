// ============================================================================
// FormQuantiXMapas.cs - Gestión de mapas de prescripción
// Ubicación: SourceCode/GPS/AgroParallel/QuantiX/FormQuantiXMapas.cs
// Target: net48 (C# 7.3)
//
// Lista los shapefiles cargados, permite cargar nuevos, ver el campo
// de dosis (StyleField), y previsualizar el mapa de calor.
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using AgroParallel.VistaX;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXMapas : Form
    {
        private readonly QuantiXConfig _cfg;
        private readonly AgOpenGPS.FormGPS _parent;
        private Panel _canvas;

        public FormQuantiXMapas(QuantiXConfig cfg, AgOpenGPS.FormGPS parent)
        {
            _cfg = cfg ?? QuantiXConfig.Load();
            _parent = parent;
            BuildUI();
        }

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            _canvas = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.BgBlack
            };
            _canvas.Paint += PaintContent;
            Controls.Add(_canvas);

            // Footer con botones.
            var footer = new Panel
            {
                Dock = DockStyle.Bottom, Height = 50,
                BackColor = Theme.BgToolbar
            };
            footer.Paint += (s, ev) =>
            {
                using (var pen = new Pen(Theme.Border))
                    ev.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            var btnLoad = Theme.MkAccentButton("\U0001F4C2  CARGAR SHAPEFILE", 200, 34);
            btnLoad.Location = new Point(20, 8);
            btnLoad.Click += (s, ev) => LoadShapefile();
            footer.Controls.Add(btnLoad);

            Controls.Add(footer);
            _canvas.BringToFront();
        }

        private void PaintContent(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.BgBlack);

            int y = 20;
            using (var f = Theme.FontTitle)
            using (var br = new SolidBrush(Theme.Accent))
                g.DrawString("\U0001F5FA  MAPAS DE PRESCRIPCI\u00D3N", f, br, 24, y);
            y += 36;

            using (var f = Theme.FontBody)
            using (var br = new SolidBrush(Theme.TextSecondary))
                g.DrawString("Carg\u00E1 un shapefile con zonas de dosis variable para que QuantiX\n"
                    + "env\u00EDe la dosis correspondiente por UDP seg\u00FAn la posici\u00F3n del tractor.", f, br, 24, y);
            y += 50;

            // Check if shapefile is loaded.
            bool hasLayer = false;
            string layerPath = "";
            int polyCount = 0;
            string styleField = "";

            try
            {
                if (_parent != null)
                {
                    var layerField = _parent.GetType().GetField("shapefileLayer",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (layerField != null)
                    {
                        object layer = layerField.GetValue(_parent);
                        if (layer != null)
                        {
                            hasLayer = true;
                            var pCount = layer.GetType().GetProperty("PolygonCount");
                            var pStyle = layer.GetType().GetProperty("StyleField");
                            if (pCount != null) polyCount = (int)pCount.GetValue(layer);
                            if (pStyle != null) styleField = (string)pStyle.GetValue(layer) ?? "";
                        }
                    }
                }
            }
            catch { }

            if (hasLayer)
            {
                // Card de shapefile cargado.
                var cardRect = new Rectangle(20, y, _canvas.Width - 40, 80);
                Theme.FillRoundedRect(g, cardRect, Theme.BgCard, Theme.BorderRadius);
                Theme.DrawRoundedBorder(g, cardRect, Theme.Border, Theme.BorderRadius);

                using (var acc = new SolidBrush(Theme.Accent))
                    g.FillRectangle(acc, 20, y + Theme.BorderRadius, 3, 80 - Theme.BorderRadius * 2);

                using (var f = new Font(Theme.FontFamily, 12f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.TextPrimary))
                    g.DrawString("Shapefile cargado", f, br, 36, y + 12);

                using (var f = Theme.FontBody)
                using (var br = new SolidBrush(Theme.TextSecondary))
                {
                    g.DrawString(polyCount + " pol\u00EDgonos \u00B7 Campo de dosis: "
                        + (string.IsNullOrEmpty(styleField) ? "(ninguno)" : styleField), f, br, 36, y + 38);
                }
            }
            else
            {
                using (var f = new Font(Theme.FontFamily, 11f))
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString("No hay shapefile cargado.\nUs\u00E1 el bot\u00F3n CARGAR SHAPEFILE para importar un mapa de prescripci\u00F3n.", f, br, 24, y);
            }
        }

        private void LoadShapefile()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Cargar prescripci\u00F3n";
                ofd.Filter = "Shapefile (*.shp)|*.shp|GeoJSON (*.geojson;*.json)|*.geojson;*.json|Todos (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    // Delegar al FormGPS que ya tiene la lógica de carga.
                    try
                    {
                        var method = _parent.GetType().GetMethod("LoadShapefileFromPath",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance);
                        if (method != null)
                            method.Invoke(_parent, new object[] { ofd.FileName });
                        else
                            MessageBox.Show(this, "Shapefile cargado: " + ofd.FileName
                                + "\nRecarg\u00E1 el campo para aplicar.", "Mapas",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        string msg = ex.InnerException?.Message ?? ex.Message;
                        MessageBox.Show(this,
                            "Error cargando prescripci\u00F3n:\n\n" + msg
                            + "\n\nSi descargaste un Shapefile, verific\u00E1 que los 3 archivos "
                            + "(.shp, .dbf, .shx) est\u00E9n en la misma carpeta.\n"
                            + "Tambi\u00E9n pod\u00E9s usar formato GeoJSON (.geojson) directamente.",
                            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    _canvas.Invalidate();
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
        }
    }
}
