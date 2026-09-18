// ============================================================================
// TramMultiLienzo.cs — el LIENZO del panel "Tramlines (multi)" nativo
// (reemplaza el <canvas> de pages/tramlines.html).
//
// Qué quedó NATIVO: el dibujo entero en coordenadas de campo E/N (metros, norte
// ARRIBA) y los gestos — tocar para el corte de 3 pasos, arrastrar con un dedo
// para mover, rueda y pinza de dos dedos para acercar. Umbral de 6 px para
// distinguir toque de arrastre, igual que el JS: si un pan fantasma se colara
// como toque, el corte borraría el lado equivocado.
//
// Qué SIGUE en HTML: el canvas 2D de la página, intacto, para el Hub remoto /
// celular / Android. El devicePixelRatio y el resize a mano del JS no se
// portan — Avalonia renderiza DPI-aware solo.
//
// COLORES: los mismos del canvas web (son semánticos y el operario ya los tiene
// aprendidos) — gris el contorno exterior, marrón las islas, dorado las huellas
// perimetrales, rojo las guías AB, verde las curvas, lila los trams guardados,
// rojo/verde los puntos A/B del corte. ÚNICO cambio: el preview de trams sin
// confirmar era #f0f0f0 (blanco), invisible sobre el fondo claro de la card —
// pasa a gris oscuro #535E54 PUNTEADO, que además comunica mejor "todavía no
// está guardado".
//
// RENDIMIENTO: las polilíneas se arman UNA vez por estado, en coordenadas de
// CAMPO, y el pan/zoom se aplica como matriz en el Render. Una curva de
// cientos de puntos por N pasadas re-proyectada punto a punto en cada frame de
// arrastre es lo que hace que el dedo "se despegue" del dibujo. Como la matriz
// escala también el trazo, los lápices se crean por Render con grosor
// dividido por la escala (son ~10 objetos: gratis al lado de reproyectar).
//
// Esto NO es el mapa GL: es dibujo propio adentro de la card, no pelea con el
// compositor y el mapa vivo sigue detrás (regla: el mapa nunca se apaga).
// ============================================================================

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public sealed class TramMultiLienzo : Control
{
    // ---- paleta de la GEOMETRÍA (idéntica a tramlines.js salvo el preview) --
    private static readonly IBrush ColContorno = new SolidColorBrush(Color.Parse("#7d8a80"));  // fence exterior
    private static readonly IBrush ColIsla     = new SolidColorBrush(Color.Parse("#a0764a"));  // islas
    private static readonly IBrush ColOuter    = new SolidColorBrush(Color.Parse("#d4a833"));  // outer/inner tram bnd
    private static readonly IBrush ColAb       = new SolidColorBrush(Color.Parse("#e03333"));  // guía AB
    private static readonly IBrush ColCurva    = new SolidColorBrush(Color.Parse("#33e033"));  // guía curva
    private static readonly IBrush ColGuardado = new SolidColorBrush(Color.Parse("#ba85a2"));  // trams guardados
    private static readonly IBrush ColPreview  = new SolidColorBrush(Color.Parse("#535E54"));  // preview sin confirmar
    private static readonly IBrush ColCorte    = new SolidColorBrush(Color.Parse("#ff4444"));  // recta A→B
    private static readonly IBrush ColPtA      = new SolidColorBrush(Color.Parse("#ff0000"));
    private static readonly IBrush ColPtB      = new SolidColorBrush(Color.Parse("#00ff00"));
    private static readonly IBrush Fondo       = new SolidColorBrush(Color.Parse("#FFFFFF"));

    // Preview punteado: 6 y 4 en unidades de grosor de lápiz (o sea, proporcional
    // al trazo, no a la escala — el patrón se ve igual acercado o alejado).
    private static readonly IDashStyle DashPreview = new DashStyle(new double[] { 6, 4 }, 0);

    private TramMultiState? _st;

    // Geometrías en coordenadas de CAMPO, rearmadas solo cuando llega un estado.
    private readonly List<(StreamGeometry geo, bool isla)> _geoFences = new();
    private readonly List<(StreamGeometry geo, bool ab, bool sel)> _geoTracks = new();
    private readonly List<StreamGeometry> _geoGuardados = new();
    private readonly List<StreamGeometry> _geoPreview = new();
    private StreamGeometry? _geoOuter;
    private StreamGeometry? _geoInner;

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

    /// <summary>
    /// Sin contorno el motor no acepta toques de corte: el lienzo directamente
    /// no los emite (misma guarda que `state.has_boundary` en el JS).
    /// </summary>
    public bool AceptaToques { get; set; }

    public TramMultiLienzo()
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
    /// el /open inicial (ahí se encuadra el lote); en toda acción posterior la
    /// vista se respeta, como hacía la página con su `keepView`.
    /// </summary>
    public void SetState(TramMultiState? s, bool conservarVista)
    {
        _st = s;
        AceptaToques = s?.HasBoundary ?? false;
        Rearmar();
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

    /// <summary>Acerca (&gt;1) o aleja (&lt;1) desde el centro del lienzo.</summary>
    public void Zoom(double factor)
    {
        double mx = Bounds.Width / 2, my = Bounds.Height / 2;
        AplicarZoom(factor, mx, my);
        InvalidateVisual();
    }

    // ---- transformación campo ↔ lienzo -------------------------------------

    private (double e, double n) ACampo(Point p)
        => (_escala <= 0 ? 0 : (p.X - _ox) / _escala, _escala <= 0 ? 0 : -(p.Y - _oy) / _escala);

    /// <summary>Zoom centrado en un punto del lienzo, con la escala acotada para que nunca degenere.</summary>
    private void AplicarZoom(double factor, double mx, double my)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0) return;
        double nueva = _escala * factor;
        // Sin este tope, una pinza torpe (dos dedos casi juntos) manda la escala
        // a 0 o a infinito y el lienzo queda en blanco para siempre.
        if (nueva < 1e-4 || nueva > 1e6) return;
        _ox = mx - (mx - _ox) * factor;
        _oy = my - (my - _oy) * factor;
        _escala = nueva;
    }

    private void Encuadrar()
    {
        double w = Bounds.Width, h = Bounds.Height;
        // Sin lienzo todavía: NO se marca encuadrado, así el primer layout real
        // lo hace (el JS lo marcaba igual y se quedaba sin encuadrar nunca).
        if (w < 2 || h < 2) return;

        double minE = double.MaxValue, maxE = double.MinValue;
        double minN = double.MaxValue, maxN = double.MinValue;
        var fences = _st?.Fences;
        if (fences != null)
        {
            foreach (var anillo in fences)
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
        // Sin contorno no hay qué encuadrar (el JS hacía lo mismo). Igual el
        // motor no deja construir tramlines sin contorno.
        if (minE == double.MaxValue) { _encuadrado = true; return; }

        double anchoM = Math.Max(1, maxE - minE);
        double altoM  = Math.Max(1, maxN - minN);
        _escala = Math.Min(w / anchoM, h / altoM) * 0.9;   // margen 0.9, igual que el JS
        double cE = (minE + maxE) / 2, cN = (minN + maxN) / 2;
        _ox = w / 2 - cE * _escala;
        _oy = h / 2 + cN * _escala;
        _encuadrado = true;
    }

    // ---- armado de geometrías (una vez por estado) --------------------------

    private void Rearmar()
    {
        _geoFences.Clear();
        _geoTracks.Clear();
        _geoGuardados.Clear();
        _geoPreview.Clear();
        _geoOuter = null;
        _geoInner = null;

        var s = _st;
        if (s == null) return;

        if (s.Fences != null)
            for (int j = 0; j < s.Fences.Count; j++)
            {
                var g = Armar(s.Fences[j], cerrado: true);
                if (g != null) _geoFences.Add((g, j > 0));
            }

        _geoOuter = Armar(s.OuterBnd, cerrado: true);
        _geoInner = Armar(s.InnerBnd, cerrado: true);

        if (s.Tracks != null)
            for (int t = 0; t < s.Tracks.Count; t++)
            {
                var g = Armar(s.Tracks[t]?.Points, cerrado: false);
                if (g != null) _geoTracks.Add((g, s.Tracks[t]?.Mode == "ab", t == s.SelIdx));
            }

        if (s.SavedTrams != null)
            foreach (var linea in s.SavedTrams)
            {
                var g = Armar(linea, cerrado: false);
                if (g != null) _geoGuardados.Add(g);
            }

        if (s.NewTrams != null)
            foreach (var linea in s.NewTrams)
            {
                var g = Armar(linea, cerrado: false);
                if (g != null) _geoPreview.Add(g);
            }
    }

    private static StreamGeometry? Armar(double[][]? pts, bool cerrado)
    {
        if (pts == null || pts.Length < 2) return null;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            bool abierta = false;
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                if (p == null || p.Length < 2) continue;
                var q = new Point(p[0], p[1]);
                if (!abierta) { gc.BeginFigure(q, false); abierta = true; }
                else gc.LineTo(q);
            }
            if (!abierta) return null;
            gc.EndFigure(cerrado);
        }
        return geo;
    }

    // ---- dibujo -------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        // Fondo propio: sin él el control no es hit-testable donde no hay
        // geometría y el toque "al costado de los trams" no llegaría nunca.
        ctx.FillRectangle(Fondo, new Rect(Bounds.Size));
        var s = _st;
        if (s == null || _escala <= 0) return;

        // Matriz campo→lienzo: x = e·escala + ox, y = −n·escala + oy (norte ARRIBA).
        var m = new Matrix(_escala, 0, 0, -_escala, _ox, _oy);
        double k = _escala;   // la matriz también escala el trazo: se compensa acá

        using (ctx.PushTransform(m))
        {
            // 1) Contornos del lote (exterior gris, islas marrón).
            var penFence = new Pen(ColContorno, 2 / k);
            var penIsla  = new Pen(ColIsla, 2 / k);
            foreach (var (geo, isla) in _geoFences)
                ctx.DrawGeometry(null, isla ? penIsla : penFence, geo);

            // 2) Huellas perimetrales (outer + inner).
            var penOuter = new Pen(ColOuter, 2 / k);
            if (_geoOuter != null) ctx.DrawGeometry(null, penOuter, _geoOuter);
            if (_geoInner != null) ctx.DrawGeometry(null, penOuter, _geoInner);

            // 3) Guías: la seleccionada va con el doble de grosor.
            foreach (var (geo, ab, sel) in _geoTracks)
                ctx.DrawGeometry(null, new Pen(ab ? ColAb : ColCurva, (sel ? 4 : 2) / k), geo);

            // 4) Trams guardados, con la opacidad que eligió el operario.
            if (_geoGuardados.Count > 0)
            {
                double alpha = s.Alpha <= 0 || s.Alpha > 1 ? 1 : s.Alpha;
                var brochaGuardado = new SolidColorBrush(Color.Parse("#ba85a2")) { Opacity = alpha };
                var penGuardado = new Pen(brochaGuardado, 3 / k);
                foreach (var geo in _geoGuardados) ctx.DrawGeometry(null, penGuardado, geo);
            }

            // 5) Preview sin confirmar: punteado, para que se lea "todavía no
            //    está guardado" sin depender del color.
            if (_geoPreview.Count > 0)
            {
                var penPreview = new Pen(ColPreview, 1.5 / k) { DashStyle = DashPreview };
                foreach (var geo in _geoPreview) ctx.DrawGeometry(null, penPreview, geo);
            }

            // 6) Corte de 3 toques: la recta A→B recién cuando están los dos.
            if (s.CutStep == 2 && Valido(s.PtA) && Valido(s.PtB))
                ctx.DrawLine(new Pen(ColCorte, 3 / k),
                             new Point(s.PtA![0], s.PtA[1]), new Point(s.PtB![0], s.PtB[1]));

            // 7) Puntos A (rojo) y B (verde), radio 6 px de pantalla.
            Punto(ctx, s.PtA, 6 / k, ColPtA);
            Punto(ctx, s.PtB, 6 / k, ColPtB);
        }
    }

    private static bool Valido(double[]? p) => p != null && p.Length >= 2;

    private static void Punto(DrawingContext ctx, double[]? pt, double radio, IBrush brocha)
    {
        if (!Valido(pt)) return;
        ctx.DrawEllipse(brocha, null, new Point(pt![0], pt[1]), radio, radio);
    }

    // ---- gestos --------------------------------------------------------------
    // Misma mecánica que el JS: 1 dedo = mover, 2 dedos = pinza, toque sin
    // moverse más de 6 px = tap (que alimenta el corte de 3 pasos).

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
            // Pinzas de menos de 4 px de apertura inicial se ignoran (el JS
            // hacía lo mismo): el factor sale disparado.
            if (pz.d > 4)
            {
                double factor = Distancia(a, b) / pz.d;
                _escala = pz.escala;
                _ox = pz.ox;
                _oy = pz.oy;
                AplicarZoom(factor, pz.centro.X, pz.centro.Y);
            }
            _movio = true;
            InvalidateVisual();
            return;
        }

        if (_arrastre is { } ar)
        {
            double dx = p.X - ar.origen.X, dy = p.Y - ar.origen.Y;
            // El umbral de 6 px es LO que distingue tap de pan: si se afloja, un
            // pan fantasma entra como toque y el corte borra el lado equivocado.
            if (Math.Abs(dx) > 6 || Math.Abs(dy) > 6) _movio = true;
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

        bool eraArrastre = _arrastre != null;
        _arrastre = null;
        // El toque sale solo cuando se levantó el ÚLTIMO dedo: soltar uno de los
        // dos de una pinza no es un tap.
        if (eraArrastre && !_movio && _punteros.Count == 0 && AceptaToques)
        {
            var (fe, fn) = ACampo(p);
            Tocado?.Invoke(fe, fn);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // En Windows los punteros táctiles se pierden acá sin pasar por Released:
        // si no se limpia el diccionario, el pan queda pegado al dedo fantasma.
        _punteros.Remove(e.Pointer.Id);
        _arrastre = null;
        _pinza = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        AplicarZoom(e.Delta.Y > 0 ? 1.1 : 1 / 1.1, p.X, p.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    private static double Distancia(Point a, Point b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
