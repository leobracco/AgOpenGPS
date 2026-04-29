// ============================================================================
// FormQuantiXShapefile.cs - Gestión de shapefiles (cargar, estilo, exportar)
// Ubicación: SourceCode/GPS/AgroParallel/QuantiX/FormQuantiXShapefile.cs
// Target: net48 (C# 7.3)
//
// Centraliza las acciones de shapefile que estaban en el menú dropdown:
// cargar, aplicar estilo, mostrar/ocultar, inspeccionar, exportar.
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AgroParallel.VistaX;

namespace AgroParallel.QuantiX
{
    public class FormQuantiXShapefile : Form
    {
        private readonly AgOpenGPS.FormGPS _parent;
        private Panel _canvas;

        public FormQuantiXShapefile(AgOpenGPS.FormGPS parent)
        {
            _parent = parent;
            BuildUI();
        }

        private void BuildUI()
        {
            Theme.ApplyToForm(this);
            FormBorderStyle = FormBorderStyle.None;

            _canvas = new Panel
            {
                Dock = DockStyle.Fill, AutoScroll = true,
                BackColor = Theme.BgBlack
            };
            _canvas.Paint += PaintContent;
            Controls.Add(_canvas);

            // Botones de acción.
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
            var btnLoad = Theme.MkAccentButton("\U0001F4C2  CARGAR", 130, 36);
            btnLoad.Location = new Point(bx, 9);
            btnLoad.Click += (s, ev) => CallFormGPS("OpenShapefileLoadDialog");
            footer.Controls.Add(btnLoad);
            bx += 140;

            var btnStyle = Theme.MkButton("\U0001F3A8  ESTILO", Theme.BgCard, Theme.TextPrimary, 120, 36);
            btnStyle.Location = new Point(bx, 9);
            btnStyle.Click += (s, ev) => CallFormGPS("OpenShapefileStyleDialog");
            footer.Controls.Add(btnStyle);
            bx += 130;

            var btnExport = Theme.MkButton("\U0001F4BE  EXPORTAR", Theme.BgCard, Theme.TextPrimary, 130, 36);
            btnExport.Location = new Point(bx, 9);
            btnExport.Click += (s, ev) => CallFormGPS("OpenShapefileExportDialog");
            footer.Controls.Add(btnExport);
            bx += 140;

            var btnToggle = Theme.MkButton("\U0001F441  MOSTRAR/OCULTAR", Theme.BgCard, Theme.TextPrimary, 170, 36);
            btnToggle.Location = new Point(bx, 9);
            btnToggle.Click += (s, ev) => ToggleVisibility();
            footer.Controls.Add(btnToggle);

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

            // Check shapefile state via reflection.
            bool hasLayer = false;
            bool isVisible = false;
            int polyCount = 0;
            int lineCount = 0;
            int pointCount = 0;
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
                        var pLine = t.GetProperty("LineCount");
                        var pPt = t.GetProperty("PointCount");
                        var pStyle = t.GetProperty("StyleField");

                        if (pVis != null) isVisible = (bool)pVis.GetValue(layer);
                        if (pPoly != null) polyCount = (int)pPoly.GetValue(layer);
                        if (pLine != null) lineCount = (int)pLine.GetValue(layer);
                        if (pPt != null) pointCount = (int)pPt.GetValue(layer);
                        if (pStyle != null) styleField = (string)pStyle.GetValue(layer) ?? "";
                    }
                }
            }
            catch { }

            if (hasLayer)
            {
                var cardRect = new Rectangle(20, y, _canvas.Width - 40, 100);
                Theme.FillRoundedRect(g, cardRect, Theme.BgCard, Theme.BorderRadius);
                Theme.DrawRoundedBorder(g, cardRect, Theme.Border, Theme.BorderRadius);
                using (var acc = new SolidBrush(Theme.Accent))
                    g.FillRectangle(acc, 20, y + Theme.BorderRadius, 3, 100 - Theme.BorderRadius * 2);

                using (var f = new Font(Theme.FontFamily, 12f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.TextPrimary))
                    g.DrawString("Shapefile cargado", f, br, 36, y + 10);

                string estado = isVisible ? "\u25CF Visible" : "\u25CB Oculto";
                Color estColor = isVisible ? Theme.Accent : Theme.TextFaint;
                using (var f = Theme.FontBody)
                using (var br = new SolidBrush(estColor))
                    g.DrawString(estado, f, br, 36, y + 34);

                using (var f = Theme.FontBody)
                using (var br = new SolidBrush(Theme.TextSecondary))
                {
                    string info = polyCount + " pol\u00EDgonos";
                    if (lineCount > 0) info += " \u00B7 " + lineCount + " l\u00EDneas";
                    if (pointCount > 0) info += " \u00B7 " + pointCount + " puntos";
                    info += " \u00B7 Campo: " + (string.IsNullOrEmpty(styleField) ? "(ninguno)" : styleField);
                    g.DrawString(info, f, br, 36, y + 54);
                }

                using (var f = Theme.FontSmall)
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString("Us\u00E1 ESTILO para elegir el campo de dosis, EXPORTAR para guardar a SHP/KML.", f, br, 36, y + 76);
            }
            else
            {
                using (var f = new Font(Theme.FontFamily, 11f))
                using (var br = new SolidBrush(Theme.TextFaint))
                    g.DrawString("No hay shapefile cargado.\n\n"
                        + "Us\u00E1 CARGAR para importar un .shp con zonas de prescripci\u00F3n.\n"
                        + "QuantiX leer\u00E1 la dosis de cada zona seg\u00FAn la posici\u00F3n del tractor.",
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
                else
                    MessageBox.Show(this, "M\u00E9todo no encontrado: " + methodName,
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _canvas.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error: " + ex.InnerException?.Message ?? ex.Message,
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
                _canvas.Invalidate();
            }
            catch { }
        }
    }
}
