// MapPanel.cs
//
// Placeholder nativo de la vista principal del mapa de guiado. Hoy es un
// canvas 2D que dibuja el lote + tractor a full screen — reutiliza la
// logica de MiniMapView a otra escala. Manana se reemplaza por un control
// basado en OpenGlControlBase (compat GL ES para Linux/Pi/Android) sin
// tocar quien lo consume.
//
// Filosofia "bajo consumo":
//   - NO usa WebView (Chromium ~ 400MB residente). UI 100% Skia nativa.
//   - Solo redibuja en InvalidateVisual(); no hay timer interno. El push
//     de snapshots viene desde fuera (MainWindow.OnHudSnapshot).
//   - Sin bindings: render directo en DrawingContext, evita layout pass.

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

/// <summary>
/// Vista principal del mapa (placeholder nativo). Render 2D con
/// DrawingContext; preparada para upgrade a OpenGL sin cambiar API.
/// </summary>
public sealed class MapPanel : Control
{
    private HudSnapshot? _snap;

    // Bbox del boundary cacheado (idem MiniMapView).
    private double _minE, _maxE, _minN, _maxN;
    private bool _hasBbox;
    private double _cacheKeyE, _cacheKeyN;
    private int _cacheKeyCount;

    private static readonly IBrush BgBrush       = new SolidColorBrush(Color.Parse("#0E1410"));
    private static readonly IBrush GridBrush     = new SolidColorBrush(Color.FromArgb(40, 0x53, 0x5E, 0x54));
    private static readonly IBrush BoundaryBrush = new SolidColorBrush(Color.Parse("#5BC850"));
    private static readonly IBrush IslandFill    = new SolidColorBrush(Color.FromArgb(60, 0x53, 0x5E, 0x54));
    private static readonly IBrush IslandStroke  = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush TractorBrush  = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush TractorEdge   = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush WatermarkBrush= new SolidColorBrush(Color.FromArgb(28, 0xE2, 0xE7, 0xE2));
    private static readonly IBrush HintBrush     = new SolidColorBrush(Color.Parse("#8FA092"));

    private static readonly Pen GridPen     = new Pen(GridBrush, 1);
    private static readonly Pen BoundaryPen = new Pen(BoundaryBrush, 2.5);
    private static readonly Pen IslandPen   = new Pen(IslandStroke, 1.5);
    private static readonly Pen TractorPen  = new Pen(TractorEdge, 1.5);

    /// <summary>
    /// Push de snapshot. Llamar desde UI thread.
    /// </summary>
    public void OnSnapshot(HudSnapshot snap)
    {
        _snap = snap;
        InvalidateBboxIfChanged(snap);
        InvalidateVisual();
    }

    private void InvalidateBboxIfChanged(HudSnapshot snap)
    {
        var b = (snap.Boundaries != null && snap.Boundaries.Count > 0) ? snap.Boundaries[0] : null;
        if (b == null || b.Count < 3) { _hasBbox = false; return; }

        var first = b[0];
        if (_hasBbox && _cacheKeyE == first.E && _cacheKeyN == first.N && _cacheKeyCount == b.Count)
            return;

        _minE = double.MaxValue; _maxE = double.MinValue;
        _minN = double.MaxValue; _maxN = double.MinValue;
        foreach (var p in b)
        {
            if (p.E < _minE) _minE = p.E;
            if (p.E > _maxE) _maxE = p.E;
            if (p.N < _minN) _minN = p.N;
            if (p.N > _maxN) _maxN = p.N;
        }
        _cacheKeyE = first.E; _cacheKeyN = first.N; _cacheKeyCount = b.Count;
        _hasBbox = true;
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.FillRectangle(BgBrush, rect);
        DrawGrid(ctx, rect);
        DrawWatermark(ctx, rect);

        var snap = _snap;
        if (snap == null)
        {
            DrawCenteredHint(ctx, rect, "Esperando PilotX...");
            return;
        }

        if (_hasBbox)
            DrawWithBoundary(ctx, rect, snap);
        else if (snap.IsJobStarted)
            DrawTractorOnly(ctx, rect, snap);
        else
            DrawCenteredHint(ctx, rect, "Sin lote activo - presiona FieldTools para iniciar trabajo");
    }

    // --- helpers --------------------------------------------------------

    private static void DrawGrid(DrawingContext ctx, Rect rect)
    {
        // Grilla decorativa: 8 lineas verticales + 6 horizontales. No
        // representa unidades reales en este placeholder, da textura
        // de "mapa" para que la cabina no se vea vacia en idle.
        const int vDivs = 8;
        const int hDivs = 6;
        for (int i = 1; i < vDivs; i++)
        {
            double x = rect.Width * i / vDivs;
            ctx.DrawLine(GridPen, new Point(x, 0), new Point(x, rect.Height));
        }
        for (int i = 1; i < hDivs; i++)
        {
            double y = rect.Height * i / hDivs;
            ctx.DrawLine(GridPen, new Point(0, y), new Point(rect.Width, y));
        }
    }

    private static void DrawWatermark(DrawingContext ctx, Rect rect)
    {
        // Marca "PilotX" como watermark grande en el centro. Sera reemplazada
        // por el render OpenGL del mapa cuando se haga la migracion.
        var ft = new FormattedText(
            "PilotX",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            120,
            WatermarkBrush);
        ft.SetFontWeight(FontWeight.Bold);
        var pt = new Point(
            (rect.Width  - ft.Width)  * 0.5,
            (rect.Height - ft.Height) * 0.5);
        ctx.DrawText(ft, pt);
    }

    private void DrawWithBoundary(DrawingContext ctx, Rect rect, HudSnapshot snap)
    {
        // Fit-to-bbox con padding generoso (la vista principal puede dejar
        // mas aire que el mini-mapa).
        const double pad = 40;
        double w = rect.Width  - 2 * pad;
        double h = rect.Height - 2 * pad;
        double bboxW = Math.Max(_maxE - _minE, 0.001);
        double bboxH = Math.Max(_maxN - _minN, 0.001);
        double scale = Math.Min(w / bboxW, h / bboxH);
        double cxBbox = (_minE + _maxE) * 0.5;
        double cyBbox = (_minN + _maxN) * 0.5;
        double cxCtrl = rect.Width  * 0.5;
        double cyCtrl = rect.Height * 0.5;

        Point Project(double e, double n) =>
            new Point(cxCtrl + (e - cxBbox) * scale,
                      cyCtrl - (n - cyBbox) * scale);

        if (snap.Boundaries != null)
        {
            for (int i = 0; i < snap.Boundaries.Count; i++)
            {
                var ring = snap.Boundaries[i];
                if (ring == null || ring.Count < 2) continue;
                DrawRing(ctx, ring, Project,
                    pen:  i == 0 ? BoundaryPen : IslandPen,
                    fill: i > 0 ? IslandFill   : null);
            }
        }

        var pos = Project(snap.PivotEasting, snap.PivotNorthing);
        DrawTractor(ctx, pos, snap.Heading, scaleFactor: 1.8);
    }

    private static void DrawTractorOnly(DrawingContext ctx, Rect rect, HudSnapshot snap)
    {
        DrawTractor(ctx,
            new Point(rect.Width * 0.5, rect.Height * 0.5),
            snap.Heading,
            scaleFactor: 2.4);
    }

    private static void DrawRing(
        DrawingContext ctx,
        List<FieldPoint> ring,
        Func<double, double, Point> project,
        IPen pen,
        IBrush? fill)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            var first = project(ring[0].E, ring[0].N);
            g.BeginFigure(first, isFilled: fill != null);
            for (int i = 1; i < ring.Count; i++)
                g.LineTo(project(ring[i].E, ring[i].N));
            g.EndFigure(isClosed: true);
        }
        ctx.DrawGeometry(fill, pen, geo);
    }

    private static void DrawTractor(DrawingContext ctx, Point center, double headingRad, double scaleFactor)
    {
        double size = 12 * scaleFactor;
        var tip = Rotate(0,            -size,         headingRad);
        var bl  = Rotate(-size * 0.65,  size * 0.55,  headingRad);
        var br  = Rotate( size * 0.65,  size * 0.55,  headingRad);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(center.X + tip.X, center.Y + tip.Y), isFilled: true);
            g.LineTo     (new Point(center.X + bl.X,  center.Y + bl.Y));
            g.LineTo     (new Point(center.X + br.X,  center.Y + br.Y));
            g.EndFigure(isClosed: true);
        }
        ctx.DrawGeometry(TractorBrush, TractorPen, geo);
    }

    private static Point Rotate(double x, double y, double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return new Point(x * c - y * s, x * s + y * c);
    }

    private static void DrawCenteredHint(DrawingContext ctx, Rect rect, string text)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            14,
            HintBrush);
        var pt = new Point(
            (rect.Width  - ft.Width)  * 0.5,
            rect.Height * 0.5 + 100);
        ctx.DrawText(ft, pt);
    }
}
