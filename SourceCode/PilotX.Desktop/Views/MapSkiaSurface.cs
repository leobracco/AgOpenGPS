// MapSkiaSurface.cs
//
// Render Skia 2D del mapa de guiado (LEGACY). Es el contenido del antiguo
// MapPanel.cs antes del Stage 1 de la migracion OpenGL. Se conserva como
// fallback: si `App.UseGl == false` (default actual mientras maduramos GL,
// o `--gl=off` explicito), MapPanel hostea esta surface y la cabina
// renderea con DrawingContext (mismo comportamiento previo).
//
// Cuando MapGlSurface llegue a paridad visual con esta clase + las stages
// 2..6 (sections / guidance / tool / youturn / camera), MapSkiaSurface se
// retira definitivamente.
//
// Paridad parcial con MapGlSurface (Stages 3+4): si MapPanel le envia
// snapshots de guidance / tool / tram (via OnGuidance/OnTool/OnTram), el
// render Skia pinta esas capas tambien -- asi el toggle --gl=off no pierde
// visualmente la linea cian, la barra del implemento y las tramlines.
// Coverage (Stage 2) sigue siendo solo-GL (volumen de triangulos no compite
// con un DrawingContext).

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

/// <summary>
/// Surface Skia 2D del mapa (legacy). Hosteada por <see cref="MapPanel"/>
/// cuando GL esta deshabilitado.
/// </summary>
public sealed class MapSkiaSurface : Control
{
    private HudSnapshot? _snap;
    private GuidanceGeometrySnapshot? _guidance;
    private ToolGeometrySnapshot? _tool;
    private TramGeometrySnapshot? _tram;

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
    // Capas Stage 3+4: mismos colores que MapGlSurface para que el toggle
    // gl on/off no cambie semantica visual.
    private static readonly IBrush GuidanceBrush = new SolidColorBrush(Color.Parse("#4DD8FF"));
    private static readonly IBrush ToolOffBrush  = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush ToolAutoOn    = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush ToolAutoOff   = new SolidColorBrush(Color.Parse("#ED4848"));
    private static readonly IBrush ToolManual    = new SolidColorBrush(Color.Parse("#F8CE3F"));
    private static readonly IBrush TramBrush     = new SolidColorBrush(Color.FromArgb(0xA6, 0xED, 0xB8, 0xBC));

    private static readonly Pen GridPen     = new Pen(GridBrush, 1);
    private static readonly Pen BoundaryPen = new Pen(BoundaryBrush, 2.5);
    private static readonly Pen IslandPen   = new Pen(IslandStroke, 1.5);
    private static readonly Pen TractorPen  = new Pen(TractorEdge, 1.5);
    private static readonly Pen GuidancePen = new Pen(GuidanceBrush, 2.0);
    private static readonly Pen TramPen     = new Pen(TramBrush, 2.0);

    public void OnSnapshot(HudSnapshot snap)
    {
        _snap = snap;
        InvalidateBboxIfChanged(snap);
        InvalidateVisual();
    }

    public void OnGuidance(GuidanceGeometrySnapshot snap)
    {
        _guidance = snap;
        InvalidateVisual();
    }

    public void OnTool(ToolGeometrySnapshot snap)
    {
        _tool = snap;
        InvalidateVisual();
    }

    public void OnTram(TramGeometrySnapshot snap)
    {
        _tram = snap;
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

    private static void DrawGrid(DrawingContext ctx, Rect rect)
    {
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

        // Orden coincide con MapGlSurface: tram -> guidance -> boundary ->
        // tool (secciones del implemento por arriba del contorno para que
        // se vean) -> tractor.
        DrawTram(ctx, Project);
        DrawGuidance(ctx, Project);

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

        DrawTool(ctx, Project);

        var pos = Project(snap.PivotEasting, snap.PivotNorthing);
        DrawTractor(ctx, pos, snap.Heading, scaleFactor: 1.8);
    }

    private void DrawGuidance(DrawingContext ctx, Func<double, double, Point> project)
    {
        var g = _guidance;
        if (g == null || g.Points == null || g.Points.Count < 2) return;
        if (string.IsNullOrEmpty(g.Mode) || g.Mode == "Off") return;

        var geo = new StreamGeometry();
        using (var s = geo.Open())
        {
            s.BeginFigure(project(g.Points[0].E, g.Points[0].N), isFilled: false);
            for (int i = 1; i < g.Points.Count; i++)
                s.LineTo(project(g.Points[i].E, g.Points[i].N));
            s.EndFigure(isClosed: false);
        }
        ctx.DrawGeometry(null, GuidancePen, geo);
    }

    private void DrawTool(DrawingContext ctx, Func<double, double, Point> project)
    {
        var t = _tool;
        if (t == null || !t.IsValid || t.Sections == null || t.Sections.Count == 0) return;

        for (int i = 0; i < t.Sections.Count; i++)
        {
            var s = t.Sections[i];
            IBrush brush;
            if (s.BtnState == 0)      brush = ToolOffBrush;
            else if (s.BtnState == 2) brush = s.IsOn ? ToolManual : ToolOffBrush;
            else                      brush = s.IsMapping ? ToolAutoOn : ToolAutoOff;

            var pen = new Pen(brush, 3.0);
            var p0 = project(s.LeftE, s.LeftN);
            var p1 = project(s.RightE, s.RightN);
            ctx.DrawLine(pen, p0, p1);
        }
    }

    private void DrawTram(DrawingContext ctx, Func<double, double, Point> project)
    {
        var t = _tram;
        if (t == null) return;
        string mode = t.DisplayMode ?? "None";
        if (mode == "None") return;
        bool drawLines = (mode == "All" || mode == "FillTracks");
        bool drawBnd   = (mode == "All" || mode == "BoundaryTracks");

        if (drawLines && t.Lines != null)
        {
            foreach (var line in t.Lines)
            {
                if (line?.Points == null || line.Points.Count < 2) continue;
                var geo = new StreamGeometry();
                using (var s = geo.Open())
                {
                    s.BeginFigure(project(line.Points[0].E, line.Points[0].N), isFilled: false);
                    for (int i = 1; i < line.Points.Count; i++)
                        s.LineTo(project(line.Points[i].E, line.Points[i].N));
                    s.EndFigure(isClosed: false);
                }
                ctx.DrawGeometry(null, TramPen, geo);
            }
        }

        if (drawBnd)
        {
            DrawClosedRing(ctx, t.OuterBoundary, project, TramPen);
            DrawClosedRing(ctx, t.InnerBoundary, project, TramPen);
        }
    }

    private static void DrawClosedRing(
        DrawingContext ctx,
        System.Collections.Generic.List<TramFieldPoint>? pts,
        Func<double, double, Point> project,
        IPen pen)
    {
        if (pts == null || pts.Count < 2) return;
        var geo = new StreamGeometry();
        using (var s = geo.Open())
        {
            s.BeginFigure(project(pts[0].E, pts[0].N), isFilled: false);
            for (int i = 1; i < pts.Count; i++)
                s.LineTo(project(pts[i].E, pts[i].N));
            s.EndFigure(isClosed: true);
        }
        ctx.DrawGeometry(null, pen, geo);
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
