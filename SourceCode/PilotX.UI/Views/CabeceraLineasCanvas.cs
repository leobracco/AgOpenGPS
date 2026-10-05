// ============================================================================
// CabeceraLineasCanvas.cs — el LIENZO del panel "Cabecera por líneas" nativo
// (reemplaza el <canvas> de pages/cabecera-lineas.html).
//
// Qué quedó NATIVO: el dibujo entero en coordenadas de campo E/N (metros, norte
// arriba) y los gestos — tap para marcar A/B sobre el contorno, arrastre de un
// dedo para mover, rueda y pinch de dos dedos para acercar.
//
// Qué SIGUE en HTML: el canvas 2D de la página, intacto, para el Hub remoto /
// celular / Android.
//
// Los COLORES de la geometría son los mismos del canvas web (que a su vez son
// los del form nativo viejo): el operario ya los tiene aprendidos — marrón el
// contorno, gris el contorno agarrado, amarillo las rectas, verde las curvas,
// magenta la seleccionada, naranja A y azul B, amarillo grueso la cabecera
// armada. Los colores de la CARD sí son los del design system claro de PilotX.
//
// Esto NO es el mapa GL: es dibujo propio adentro de la card, no compite con
// el compositor y el mapa vivo sigue detrás (regla: el mapa nunca se apaga).
// El devicePixelRatio y el resize a mano del JS no se portan — Avalonia
// renderiza DPI-aware solo.
// ============================================================================

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public sealed class CabeceraLineasCanvas : Control
{
    // ---- paleta de la GEOMETRÍA (idéntica a cabecera-lineas.js) -------------
    private static readonly IPen PenContorno    = new Pen(new SolidColorBrush(Color.Parse("#a0764a")), 2);
    private static readonly IPen PenContornoSel = new Pen(new SolidColorBrush(Color.Parse("#7d8a80")), 2);
    private static readonly IPen PenRecta       = new Pen(new SolidColorBrush(Color.Parse("#c9a227")), 1.5);
    private static readonly IPen PenCurva       = new Pen(new SolidColorBrush(Color.Parse("#3f9e35")), 1.5);
    private static readonly IPen PenSeleccion   = new Pen(new SolidColorBrush(Color.Parse("#c026d3")), 4);
    private static readonly IPen PenCabecera    = new Pen(new SolidColorBrush(Color.Parse("#d4a017")), 5);

    private static readonly IBrush Negro  = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush PuntoA = new SolidColorBrush(Color.Parse("#e8963c"));   // naranja = A
    private static readonly IBrush PuntoB = new SolidColorBrush(Color.Parse("#5a8fd6"));   // azul = B
    private static readonly IBrush Fondo  = new SolidColorBrush(Color.Parse("#FFFFFF"));

    private CabLinState? _st;

    // Vista (solo dibujo: el pan/zoom NUNCA viaja al motor).
    private double _escala = 1, _ox, _oy;
    private bool _encuadrado;

    // Gestos: id de puntero → posición en coordenadas del control.
    private readonly Dictionary<int, Point> _punteros = new();
    private (Point origen, double ox, double oy)? _arrastre;
    private (double d, double escala, double ox, double oy, Point centro)? _pinza;
    private bool _movio;

    /// <summary>Toque sobre el lienzo, ya convertido a campo (easting, northing).</summary>
    public event Action<double, double>? Tocado;

    public CabeceraLineasCanvas()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        // El primer layout llega con Bounds en cero: recién ahí se puede
        // encuadrar de verdad, así que se reintenta con cada cambio de tamaño
        // mientras no se haya encuadrado nunca.
        SizeChanged += (_, _) =>
        {
            if (!_encuadrado) Encuadrar();
            InvalidateVisual();
        };
    }

    /// <summary>
    /// Estado nuevo del motor. <paramref name="conservarVista"/> = false solo en
    /// el /open inicial (ahí se encuadra); en toda acción posterior la vista se
    /// respeta, como hacía la página.
    /// </summary>
    public void SetState(CabLinState? s, bool conservarVista)
    {
        _st = s;
        if (!conservarVista) _encuadrado = false;
        if (!_encuadrado) Encuadrar();
        InvalidateVisual();
    }

    /// <summary>Encuadre a pedido (botón del operario) — la pantalla táctil no tiene rueda.</summary>
    public void EncuadrarAhora()
    {
        _encuadrado = false;
        Encuadrar();
        InvalidateVisual();
    }

    /// <summary>Acerca (>1) o aleja (&lt;1) desde el centro del lienzo.</summary>
    public void Zoom(double factor)
    {
        double mx = Bounds.Width / 2, my = Bounds.Height / 2;
        _ox = mx - (mx - _ox) * factor;
        _oy = my - (my - _oy) * factor;
        _escala *= factor;
        InvalidateVisual();
    }

    // ---- transformación campo ↔ lienzo -------------------------------------

    private Point ALienzo(double e, double n) => new(e * _escala + _ox, -n * _escala + _oy);

    private (double e, double n) ACampo(Point p)
        => (_escala <= 0 ? 0 : (p.X - _ox) / _escala, _escala <= 0 ? 0 : -(p.Y - _oy) / _escala);

    private void Encuadrar()
    {
        double w = Bounds.Width, h = Bounds.Height;
        // Sin lienzo todavía: NO se marca encuadrado, así el primer layout real
        // lo hace (el JS lo marcaba igual y se quedaba sin encuadrar nunca).
        if (w < 2 || h < 2) return;

        var f = _st?.Fences;
        double minE = double.MaxValue, maxE = double.MinValue;
        double minN = double.MaxValue, maxN = double.MinValue;
        if (f != null)
        {
            foreach (var anillo in f)
            {
                if (anillo == null) continue;
                foreach (var p in anillo)
                {
                    if (p == null || p.Length < 2) continue;
                    if (p[0] < minE) minE = p[0];
                    if (p[0] > maxE) maxE = p[0];
                    if (p[1] < minN) minN = p[1];
                    if (p[1] > maxN) maxN = p[1];
                }
            }
        }
        if (minE == double.MaxValue) { _encuadrado = true; return; }   // sin contorno: no hay qué encuadrar

        double anchoM = Math.Max(1, maxE - minE);
        double altoM  = Math.Max(1, maxN - minN);
        _escala = Math.Min(w / anchoM, h / altoM) * 0.9;               // margen 0.9, igual que el JS
        double cE = (minE + maxE) / 2, cN = (minN + maxN) / 2;
        _ox = w / 2 - cE * _escala;
        _oy = h / 2 + cN * _escala;
        _encuadrado = true;
    }

    // ---- dibujo -------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        // Fondo propio: sin él el control no es hit-testable donde no hay
        // geometría y el toque "al lado del contorno" no llegaría nunca.
        ctx.FillRectangle(Fondo, new Rect(Bounds.Size));
        var s = _st;
        if (s == null) return;

        // Contornos del lote: el agarrado por el primer tap va en gris.
        var fences = s.Fences;
        if (fences != null)
            for (int j = 0; j < fences.Count; j++)
                Trazo(ctx, fences[j], j == s.BndSelect ? PenContornoSel : PenContorno, cerrado: true);

        // Líneas construidas: recta amarilla, curva verde; la seleccionada
        // después y en magenta grueso, para que quede arriba de las demás.
        var tracks = s.Tracks;
        if (tracks != null)
        {
            for (int t = 0; t < tracks.Count; t++)
            {
                if (t == s.SelIdx) continue;
                Trazo(ctx, tracks[t]?.Points, tracks[t]?.Mode == "ab" ? PenRecta : PenCurva, cerrado: false);
            }
            if (s.SelIdx > -1 && s.SelIdx < tracks.Count)
            {
                var sel = tracks[s.SelIdx]?.Points;
                Trazo(ctx, sel, PenSeleccion, cerrado: false);
                if (sel != null && sel.Length > 0)
                {
                    Punto(ctx, sel[0], 9, Negro);
                    Punto(ctx, sel[0], 6, PuntoA);                      // punta A
                    Punto(ctx, sel[sel.Length - 1], 9, Negro);
                    Punto(ctx, sel[sel.Length - 1], 6, PuntoB);         // punta B
                }
            }
        }

        // Cabecera ya armada.
        Trazo(ctx, s.HdLine, PenCabecera, cerrado: false);

        // Puntos A/B tocados sobre el contorno.
        if (s.APoint != null) { Punto(ctx, s.APoint, 10, Negro); Punto(ctx, s.APoint, 7, PuntoA); }
        if (s.BPoint != null) { Punto(ctx, s.BPoint, 10, Negro); Punto(ctx, s.BPoint, 7, PuntoB); }
    }

    private void Trazo(DrawingContext ctx, double[][]? pts, IPen pen, bool cerrado)
    {
        if (pts == null || pts.Length < 2) return;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            bool abierta = false;
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                if (p == null || p.Length < 2) continue;
                var q = ALienzo(p[0], p[1]);
                if (!abierta) { gc.BeginFigure(q, false); abierta = true; }
                else gc.LineTo(q);
            }
            if (!abierta) return;
            gc.EndFigure(cerrado);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private void Punto(DrawingContext ctx, double[]? pt, double radio, IBrush brush)
    {
        if (pt == null || pt.Length < 2) return;
        ctx.DrawEllipse(brush, null, ALienzo(pt[0], pt[1]), radio, radio);
    }

    // ---- gestos --------------------------------------------------------------
    // Misma mecánica que el JS: 1 dedo = mover, 2 dedos = pinza, toque sin
    // moverse más de 6 px = marcar punto.

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        _punteros[e.Pointer.Id] = p;

        if (_punteros.Count == 1)
        {
            _arrastre = (p, _ox, _oy);
            _movio = false;
        }
        else if (_punteros.Count == 2)
        {
            _arrastre = null;                       // el segundo dedo cancela el arrastre
            var it = _punteros.Values.GetEnumerator();
            it.MoveNext(); var a = it.Current;
            it.MoveNext(); var b = it.Current;
            _pinza = (Distancia(a, b), _escala, _ox, _oy,
                      new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2));
        }
        try { e.Pointer.Capture(this); } catch { }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_punteros.ContainsKey(e.Pointer.Id)) return;
        var p = e.GetPosition(this);
        _punteros[e.Pointer.Id] = p;

        if (_pinza is { } pz && _punteros.Count == 2)
        {
            var it = _punteros.Values.GetEnumerator();
            it.MoveNext(); var a = it.Current;
            it.MoveNext(); var b = it.Current;
            double factor = Distancia(a, b) / (pz.d <= 0 ? 1 : pz.d);
            _escala = pz.escala * factor;
            _ox = pz.centro.X - (pz.centro.X - pz.ox) * factor;
            _oy = pz.centro.Y - (pz.centro.Y - pz.oy) * factor;
            _movio = true;
            InvalidateVisual();
            return;
        }

        if (_arrastre is { } ar)
        {
            double dx = p.X - ar.origen.X, dy = p.Y - ar.origen.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 6) _movio = true;
            _ox = ar.ox + dx;
            _oy = ar.oy + dy;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        _punteros.Remove(e.Pointer.Id);
        if (_punteros.Count < 2) _pinza = null;

        if (_arrastre != null && !_movio)
        {
            _arrastre = null;
            var (fe, fn) = ACampo(p);
            Tocado?.Invoke(fe, fn);
            return;
        }
        _arrastre = null;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _punteros.Remove(e.Pointer.Id);
        _arrastre = null;
        _pinza = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        _ox = p.X - (p.X - _ox) * factor;
        _oy = p.Y - (p.Y - _oy) * factor;
        _escala *= factor;
        InvalidateVisual();
        e.Handled = true;
    }

    private static double Distancia(Point a, Point b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
