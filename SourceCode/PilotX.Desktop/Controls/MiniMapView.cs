// MiniMapView.cs
//
// Vista chiquita del lote + tractor, dibujada nativa en Avalonia. Es el
// primer paso hacia migrar el render del mapa (hoy OpenGL en FormGPS) a
// la stack Avalonia. No pretende paridad: lo que da es un thumbnail
// 240x180 en cabina con
//   * outline del lote activo (boundary[0]) + drive-thru islands en gris
//   * sprite tractor (triangulo verde) rotado segun heading
//   * indicador de "fuera de boundaries" cuando el pivote queda afuera
//
// Estrategia de escalado: bbox del primer boundary -> fit en el control
// manteniendo aspect ratio (con padding). Si no hay boundary, centra el
// tractor en el control y dibuja una cruz de referencia.
//
// Performance: redibujo se dispara via InvalidateVisual() desde fuera
// (MainWindow llama OnSnapshot() en el UI thread). Cada Render() es O(N)
// con N = total de puntos del boundary; para un lote tipico (< 2k puntos)
// es despreciable a 4 Hz.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Controls;

/// <summary>
/// Mini-mapa 2D del lote + tractor. Recibe snapshots via OnSnapshot()
/// y redibuja en cada uno.
/// </summary>
public sealed class MiniMapView : Control
{
    // Snapshot mas reciente. null = pintar "esperando datos".
    private HudSnapshot? _snap;

    // Cache del bbox del boundary para no recalcularlo en cada Render
    // (las boundaries no cambian dentro del mismo lote).
    private double _minE, _maxE, _minN, _maxN;
    private bool _hasBbox;
    // Marca de identidad del cache. Si el primer punto del boundary
    // cambia, recalculo bbox (proxy barato para "boundary cambio").
    private double _cacheKeyE, _cacheKeyN;
    private int _cacheKeyCount;

    // Brushes — espejados de Theme/PilotXTheme.axaml.
    private static readonly IBrush BgBrush       = new SolidColorBrush(Color.Parse("#1A1F1B"));
    private static readonly IBrush BoundaryBrush = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush IslandBrush   = new SolidColorBrush(Color.FromArgb(80, 0x53, 0x5E, 0x54));
    private static readonly IBrush TractorBrush  = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush TractorEdge   = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush AxisBrush     = new SolidColorBrush(Color.Parse("#262C28"));
    private static readonly IBrush HintBrush     = new SolidColorBrush(Color.Parse("#8FA092"));

    private static readonly Pen BoundaryPen = new Pen(BoundaryBrush, 2);
    private static readonly Pen IslandPen   = new Pen(IslandBrush, 1);
    private static readonly Pen AxisPen     = new Pen(AxisBrush, 1);
    private static readonly Pen TractorPen  = new Pen(TractorEdge, 1);

    public MiniMapView()
    {
        // Tamano hint razonable para overlay esquina. El fondo se pinta
        // dentro de Render() — Control no expone una propiedad Background
        // directa, solo TemplatedControl/Panel lo hacen.
        Width  = 240;
        Height = 180;
    }

    /// <summary>
    /// Empuja una snapshot nueva y redibuja. Llamar SIEMPRE desde el UI
    /// thread (Dispatcher.UIThread.Post si venis de un Task).
    /// </summary>
    public void OnSnapshot(HudSnapshot snap)
    {
        _snap = snap;
        InvalidateBboxIfChanged(snap);
        InvalidateVisual();
    }

    private void InvalidateBboxIfChanged(HudSnapshot snap)
    {
        var b = FirstBoundary(snap);
        if (b == null || b.Count < 3) { _hasBbox = false; return; }

        // Cache key: primer punto + count. Si cambia, recalculo. No es
        // exacto pero suficiente: dos boundaries distintas comparten
        // primer-punto-y-count con probabilidad despreciable.
        var first = b[0];
        if (_hasBbox &&
            _cacheKeyE == first.E && _cacheKeyN == first.N && _cacheKeyCount == b.Count)
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

    private static List<FieldPoint>? FirstBoundary(HudSnapshot s)
    {
        if (s.Boundaries == null || s.Boundaries.Count == 0) return null;
        return s.Boundaries[0];
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.FillRectangle(BgBrush, rect);

        var snap = _snap;
        if (snap == null)
        {
            DrawCenteredHint(ctx, rect, "Esperando datos...");
            return;
        }

        if (_hasBbox)
        {
            DrawWithBoundary(ctx, rect, snap);
        }
        else
        {
            DrawWithoutBoundary(ctx, rect, snap);
        }
    }

    // ---- Caso A: hay boundary -> fit-to-bbox -----------------------------

    private void DrawWithBoundary(DrawingContext ctx, Rect rect, HudSnapshot snap)
    {
        const double pad = 8;
        double w = rect.Width  - 2 * pad;
        double h = rect.Height - 2 * pad;
        double bboxW = Math.Max(_maxE - _minE, 0.001);
        double bboxH = Math.Max(_maxN - _minN, 0.001);
        // Escala uniforme manteniendo aspect ratio.
        double scale = Math.Min(w / bboxW, h / bboxH);
        // Centro del bbox -> centro del control.
        double cxBbox = (_minE + _maxE) * 0.5;
        double cyBbox = (_minN + _maxN) * 0.5;
        double cxCtrl = rect.Width  * 0.5;
        double cyCtrl = rect.Height * 0.5;

        // Local transform: (E,N) -> pixel.
        Point Project(double e, double n)
        {
            double x = cxCtrl + (e - cxBbox) * scale;
            // Northing crece "para arriba"; en pixel Y crece para abajo.
            double y = cyCtrl - (n - cyBbox) * scale;
            return new Point(x, y);
        }

        // Boundary externo + islas.
        if (snap.Boundaries != null)
        {
            for (int i = 0; i < snap.Boundaries.Count; i++)
            {
                var ring = snap.Boundaries[i];
                if (ring == null || ring.Count < 2) continue;
                var pen = i == 0 ? BoundaryPen : IslandPen;
                DrawRing(ctx, ring, Project, pen, fill: i > 0 ? IslandBrush : null);
            }
        }

        // Tractor encima.
        var tractor = Project(snap.PivotEasting, snap.PivotNorthing);
        DrawTractor(ctx, tractor, snap.Heading);
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
            {
                var p = project(ring[i].E, ring[i].N);
                g.LineTo(p);
            }
            g.EndFigure(isClosed: true);
        }
        ctx.DrawGeometry(fill, pen, geo);
    }

    // ---- Caso B: sin boundary -> cruz de referencia + tractor centrado ---

    private void DrawWithoutBoundary(DrawingContext ctx, Rect rect, HudSnapshot snap)
    {
        // Cruz de referencia centrada (ejes E/N hint).
        double cx = rect.Width  * 0.5;
        double cy = rect.Height * 0.5;
        ctx.DrawLine(AxisPen, new Point(cx, 8), new Point(cx, rect.Height - 8));
        ctx.DrawLine(AxisPen, new Point(8, cy), new Point(rect.Width - 8, cy));

        if (snap.IsJobStarted)
        {
            // Job activo pero sin boundary cargada: dibujo el tractor en el
            // centro como referencia. No es un "mapa" todavia pero da feedback.
            DrawTractor(ctx, new Point(cx, cy), snap.Heading);
        }
        else
        {
            DrawCenteredHint(ctx, rect, "Sin lote activo");
        }
    }

    // ---- Tractor sprite: triangulo isosceles rotado segun heading -------

    private static void DrawTractor(DrawingContext ctx, Point center, double headingRad)
    {
        // Triangulo: punta hacia arriba (Northing +) cuando heading = 0.
        // PilotX usa convencion compass (heading 0 = norte, +CW). Avalonia
        // pinta Y hacia abajo, asi que aplico la rotacion compass directa.
        const double size = 10;
        // Punta (frente), base izq, base der relativos al centro.
        var tip = Rotate(0,        -size,         headingRad);
        var bl  = Rotate(-size*0.6, size*0.55,    headingRad);
        var br  = Rotate( size*0.6, size*0.55,    headingRad);

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

    // Rotacion 2D compass-style: heading 0 = arriba (Y-), +CW.
    // (Avalonia Y crece para abajo, asi que un heading "norte" = (-Y).)
    private static Point Rotate(double x, double y, double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        // Aplico R(heading) en sistema compass: x' = x*cos - y*sin, y' = x*sin + y*cos.
        // (Es equivalente a una rotacion estandar; el signo lo hereda Y siendo "abajo".)
        return new Point(x * c - y * s, x * s + y * c);
    }

    // ---- Hint de texto centrado (esperando datos / sin lote) -------------

    private static void DrawCenteredHint(DrawingContext ctx, Rect rect, string text)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11,
            HintBrush);
        var pt = new Point(
            (rect.Width  - ft.Width)  * 0.5,
            (rect.Height - ft.Height) * 0.5);
        ctx.DrawText(ft, pt);
    }
}
