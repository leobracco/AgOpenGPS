// ============================================================================
// ShapePreviewControl.cs — vista previa de la capa de prescripción activa.
//
// QUÉ QUEDÓ NATIVO: el SVG que dibujaba quantix.js (shapePreviewHtml +
// shapeBindTap), con el mismo criterio: metros locales, norte arriba (Y
// invertida), relleno con el color que manda el server y borde #535E54.
// QUÉ SIGUE EN HTML: la versión SVG, que usa la PWA del celular.
//
// El toque resuelve la zona con point-in-polygon LOCAL (ray casting, even-odd
// para respetar agujeros, recorriendo de arriba hacia abajo) — no hace falta
// georreferenciar el toque ni preguntarle al motor.
//
// CUIDADO CON EL ÍNDICE: la zona que se devuelve trae `Fi`, el índice de la
// FEATURE DEL ARCHIVO. Ese es el que viaja al server al editar la dosis. El
// índice de dibujo NO sirve: los MultiPolygon se parten en piezas y corren la
// numeración — con el de dibujo se editaba OTRA zona.
// ============================================================================

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.Controls;

public sealed class ShapePreviewControl : Control
{
    private static readonly IPen PenBorde = new Pen(new SolidColorBrush(Color.Parse("#535E54")), 1);
    private static readonly IPen PenMarca = new Pen(new SolidColorBrush(Color.Parse("#101612")), 2);
    private static readonly IBrush FondoLienzo = new SolidColorBrush(Color.Parse("#FFFFFF"));

    private QxShapeLayer? _capa;
    private double _minX, _minY, _maxX, _maxY;
    private Point? _marca;   // en coordenadas del control

    /// <summary>Ancho/alto del lote en metros (para el pie de la ficha).</summary>
    public double AnchoM => Math.Max(1, _maxX - _minX);
    public double AltoM  => Math.Max(1, _maxY - _minY);

    /// <summary>Zona tocada (null = fuera de todas las zonas).</summary>
    public event Action<QxShapeZona?>? ZonaTocada;

    public ShapePreviewControl()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        MinHeight = 180;
    }

    public void SetCapa(QxShapeLayer? capa)
    {
        _capa = capa;
        _marca = null;
        _minX = _minY = double.MaxValue;
        _maxX = _maxY = double.MinValue;
        if (capa != null)
        {
            foreach (var z in capa.Polygons)
                foreach (var a in z.Rings)
                    for (int i = 0; i + 1 < a.Length; i += 2)
                    {
                        if (a[i] < _minX) _minX = a[i];
                        if (a[i] > _maxX) _maxX = a[i];
                        if (a[i + 1] < _minY) _minY = a[i + 1];
                        if (a[i + 1] > _maxY) _maxY = a[i + 1];
                    }
        }
        if (_minX == double.MaxValue) { _minX = _minY = 0; _maxX = _maxY = 1; }
        InvalidateVisual();
    }

    public bool HayGeometria => _capa != null && _capa.Polygons.Count > 0 && _maxX > _minX;

    // ---- transformación metros ↔ control ----------------------------------

    private (double escala, double dx, double dy) Ajuste()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return (1, 0, 0);
        double escala = Math.Min(w / AnchoM, h / AltoM);
        double dx = (w - AnchoM * escala) / 2.0;
        double dy = (h - AltoM * escala) / 2.0;
        return (escala, dx, dy);
    }

    private Point AControl(double x, double y)
    {
        var (e, dx, dy) = Ajuste();
        // El norte crece hacia arriba y la Y del control hacia abajo: se invierte.
        return new Point(dx + (x - _minX) * e, dy + (_maxY - y) * e);
    }

    private (double x, double y) AMetros(Point p)
    {
        var (e, dx, dy) = Ajuste();
        if (e <= 0) return (0, 0);
        return (_minX + (p.X - dx) / e, _maxY - (p.Y - dy) / e);
    }

    // ---- render -----------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        // Fondo propio: sin él, el control no es hit-testable fuera de los
        // polígonos y el toque "fuera de las zonas" no llegaría nunca.
        ctx.FillRectangle(FondoLienzo, new Rect(Bounds.Size));
        if (_capa == null) return;

        foreach (var z in _capa.Polygons)
        {
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                foreach (var a in z.Rings)
                {
                    if (a.Length < 6) continue;   // menos de 3 puntos no es polígono
                    var p0 = AControl(a[0], a[1]);
                    gc.BeginFigure(p0, true);
                    for (int i = 2; i + 1 < a.Length; i += 2)
                        gc.LineTo(AControl(a[i], a[i + 1]));
                    gc.EndFigure(true);
                }
            }
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(z.A, 0, 255), (byte)Math.Clamp(z.R, 0, 255),
                (byte)Math.Clamp(z.G, 0, 255), (byte)Math.Clamp(z.B, 0, 255)));
            ctx.DrawGeometry(brush, PenBorde, geo);
        }

        if (_marca is Point m)
            ctx.DrawEllipse(null, PenMarca, m, 7, 7);
    }

    // ---- toque ------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_capa == null) return;
        var p = e.GetPosition(this);
        _marca = p;
        var (x, y) = AMetros(p);
        var zona = ZonaEn(x, y);
        InvalidateVisual();
        ZonaTocada?.Invoke(zona);
        e.Handled = true;
    }

    /// <summary>Zona bajo (x,y) en metros locales, de arriba hacia abajo.</summary>
    public QxShapeZona? ZonaEn(double x, double y)
    {
        if (_capa == null) return null;
        var pol = _capa.Polygons;
        for (int i = pol.Count - 1; i >= 0; i--)
        {
            int cont = 0;
            foreach (var a in pol[i].Rings)
                if (PuntoEnAnillo(a, x, y)) cont++;
            if (cont % 2 == 1) return pol[i];   // even-odd: respeta agujeros
        }
        return null;
    }

    /// <summary>Ray casting sobre el anillo plano [x0,y0,x1,y1,…].</summary>
    public static bool PuntoEnAnillo(IReadOnlyList<double> a, double x, double y)
    {
        bool dentro = false;
        int n = a.Count;
        if (n < 6) return false;
        for (int i = 0, j = n - 2; i + 1 < n; j = i, i += 2)
        {
            double xi = a[i], yi = a[i + 1], xj = a[j], yj = a[j + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                dentro = !dentro;
        }
        return dentro;
    }
}
