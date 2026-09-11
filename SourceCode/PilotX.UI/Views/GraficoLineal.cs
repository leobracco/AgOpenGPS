// ============================================================================
// GraficoLineal.cs — el LIENZO compartido por las CUATRO pantallas de gráficos
// nativas (dirección, rumbo, XTE y chequeo de roll).
//
// Reemplaza al <canvas> 2D que dibujaban js/grafico-direccion.js,
// js/grafico-rumbo.js, js/grafico-xte.js y js/grafico-correccion.js. Los cuatro
// JS tenían el MISMO dibujo copiado cuatro veces; acá es un solo control y cada
// panel solo lo configura. Las páginas HTML quedan intactas para la PWA.
//
// Lo que se replica del canvas, punto por punto (el dibujo ES la lógica):
//   · buffer rodante de 120 muestras (MAX_POINTS) por serie
//   · x = (i / (MAX_POINTS − 1)) · w  →  con menos de 120 muestras la traza
//     ocupa solo la parte IZQUIERDA del área y se va llenando hacia la derecha
//   · una serie con menos de 2 puntos NO se dibuja (`if (data.length < 2) return`)
//   · grosor de serie 2 px, grilla 1 px, grilla con globalAlpha 0.5
//   · DOS encuadres del eje Y, exactamente los dos que había:
//       - CentradoEnCero (dirección, XTE, corrección):
//             y = h/2 − (v / escala) · (h/2)
//         grilla: línea de CERO a alpha 1 + tercios 0.25 y 0.75 a alpha 0.5
//       - RangoAuto (rumbo, que grafica valores absolutos 0-360°):
//             y = h − ((v − lo) / (hi − lo)) · h
//         grilla: 0.25, 0.5 y 0.75, TODAS a alpha 0.5 (sin línea de cero)
//   · el orden de las series es el orden de DIBUJO (en corrección la serie de
//     corrección va arriba de todo, igual que en el JS)
//
// Lo que NO se porta: fitCanvas()/devicePixelRatio y el listener de resize.
// Avalonia renderiza DPI-aware solo y reinvalida en cada layout.
//
// Esto NO es el mapa GL: es dibujo propio adentro de la card, no pelea con el
// compositor y el mapa vivo sigue detrás (doctrina: el mapa nunca se apaga).
// ============================================================================

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PilotX.Desktop.Views;

/// <summary>Los dos encuadres de eje Y que tenían los cuatro gráficos.</summary>
public enum ModoEjeGrafico
{
    /// <summary>Simétrico ±escala alrededor del cero (dirección, XTE, corrección).</summary>
    CentradoEnCero,

    /// <summary>Encuadrado al rango [lo, hi] de los datos (rumbo, valores absolutos).</summary>
    RangoAuto,
}

/// <summary>
/// Una serie del gráfico: color + buffer rodante de <see cref="GraficoLineal.MaxPuntos"/>
/// muestras. Empujar más allá del tope descarta la más vieja, igual que el
/// `push()` + `shift()` de los JS.
/// </summary>
public sealed class SerieGrafico
{
    private readonly List<double> _datos = new(GraficoLineal.MaxPuntos);

    internal SerieGrafico(Color color) { Color = color; }

    /// <summary>Color del trazo (el mismo que usaba `ctx.strokeStyle`).</summary>
    public Color Color { get; set; }

    /// <summary>Cuántas muestras hay en el buffer (0..120).</summary>
    public int Cantidad => _datos.Count;

    internal IReadOnlyList<double> Datos => _datos;

    /// <summary>Agrega una muestra al final y descarta la más vieja si se pasa de 120.</summary>
    public void Empujar(double v)
    {
        _datos.Add(v);
        if (_datos.Count > GraficoLineal.MaxPuntos) _datos.RemoveAt(0);
    }

    /// <summary>Vacía el buffer (ningún JS lo hacía; queda por si el panel lo necesita).</summary>
    public void Limpiar() => _datos.Clear();
}

public sealed class GraficoLineal : Control
{
    /// <summary>MAX_POINTS de los cuatro JS. También es el divisor del eje X:
    /// cambiarlo cambia la escala de tiempo del gráfico.</summary>
    public const int MaxPuntos = 120;

    private readonly List<SerieGrafico> _series = new();

    public GraficoLineal()
    {
        // El <canvas> recortaba lo que se salía; sin esto una muestra fuera de
        // escala se dibujaría por arriba del borde de la tarjeta.
        ClipToBounds = true;
    }

    /// <summary>Encuadre del eje Y.</summary>
    public ModoEjeGrafico Modo { get; set; } = ModoEjeGrafico.CentradoEnCero;

    /// <summary>Semiamplitud del eje en modo <see cref="ModoEjeGrafico.CentradoEnCero"/>
    /// (el eje va de −Escala a +Escala). La calcula el panel, igual que
    /// `currentScale()` en el JS.</summary>
    public double Escala { get; set; } = 1.0;

    /// <summary>Extremo inferior del eje en modo <see cref="ModoEjeGrafico.RangoAuto"/>.</summary>
    public double RangoLo { get; set; }

    /// <summary>Extremo superior del eje en modo <see cref="ModoEjeGrafico.RangoAuto"/>.</summary>
    public double RangoHi { get; set; } = 1.0;

    /// <summary>Línea de cero a media altura, a opacidad plena (la tenían
    /// dirección, XTE y corrección; rumbo NO).</summary>
    public bool LineaCero { get; set; } = true;

    /// <summary>Fracciones de la altura donde va la grilla suave (alpha 0.5).</summary>
    public double[] FraccionesGrilla { get; set; } = { 0.25, 0.75 };

    /// <summary>Color de la grilla. Los JS leían `--agp-border` con fallback
    /// '#c5cfc5'; el panel lo carga del token PilotXPanelBorderHighColor, que
    /// vale exactamente ese #C5CFC5.</summary>
    public Color ColorGrilla { get; set; } = Color.Parse("#C5CFC5");

    /// <summary>Grosor del trazo de serie (`ctx.lineWidth = 2`).</summary>
    public double GrosorSerie { get; set; } = 2.0;

    /// <summary>Series en orden de DIBUJO (la última queda arriba).</summary>
    public IReadOnlyList<SerieGrafico> Series => _series;

    /// <summary>Da de alta una serie. El orden de alta es el orden de dibujo.</summary>
    public SerieGrafico NuevaSerie(Color color)
    {
        var s = new SerieGrafico(color);
        _series.Add(s);
        return s;
    }

    /// <summary>Equivale al `draw()` del JS: repinta con lo que haya.</summary>
    public void Redibujar() => InvalidateVisual();

    /// <summary>
    /// Mayor magnitud absoluta presente en TODAS las series, con un piso.
    /// Es el `var m = piso; for(...) m = Math.max(m, Math.abs(v));` que los tres
    /// gráficos centrados usan para la escala automática.
    /// </summary>
    public double MaxAbsoluto(double piso)
    {
        double m = piso;
        foreach (var s in _series)
        {
            var d = s.Datos;
            for (int i = 0; i < d.Count; i++)
            {
                double a = Math.Abs(d[i]);
                if (a > m) m = a;
            }
        }
        return m;
    }

    /// <summary>
    /// Mínimo y máximo de TODAS las series. Si no hay ninguna muestra devuelve
    /// (+∞, −∞), igual que el `lo = Infinity, hi = -Infinity` del JS de rumbo —
    /// el panel decide qué hacer con eso.
    /// </summary>
    public (double lo, double hi) MinMax()
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var s in _series)
        {
            var d = s.Datos;
            for (int i = 0; i < d.Count; i++)
            {
                if (d[i] < lo) lo = d[i];
                if (d[i] > hi) hi = d[i];
            }
        }
        return (lo, hi);
    }

    // ---- dibujo -------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // El <canvas> no tenía fondo propio (lo ponía .g-chart-wrap): acá el
        // fondo también lo pone el Border de la tarjeta, así que no se pinta
        // nada — solo se limpia por el ciclo de render de Avalonia.

        // ---- grilla ---------------------------------------------------------
        var penLleno = new Pen(new SolidColorBrush(ColorGrilla), 1);
        if (LineaCero)
            ctx.DrawLine(penLleno, new Point(0, h / 2), new Point(w, h / 2));

        // globalAlpha = 0.5 sobre el MISMO color: se replica con un pincel al
        // 50% en vez de un push de opacidad (mismo resultado para trazos).
        var penSuave = new Pen(new SolidColorBrush(ColorGrilla) { Opacity = 0.5 }, 1);
        var fracs = FraccionesGrilla;
        if (fracs != null)
            for (int i = 0; i < fracs.Length; i++)
                ctx.DrawLine(penSuave, new Point(0, h * fracs[i]), new Point(w, h * fracs[i]));

        // ---- series (en orden de alta = orden de dibujo) --------------------
        foreach (var s in _series) DibujarSerie(ctx, s, w, h);
    }

    private void DibujarSerie(DrawingContext ctx, SerieGrafico serie, double w, double h)
    {
        var d = serie.Datos;
        // `if (data.length < 2) return` — con una sola muestra el JS no pintaba
        // nada (no hay segmento que trazar).
        if (d.Count < 2) return;

        var geo = new StreamGeometry();
        bool algo = false;
        using (var gc = geo.Open())
        {
            bool abierta = false;
            for (int i = 0; i < d.Count; i++)
            {
                double x = (i / (double)(MaxPuntos - 1)) * w;
                double y = Y(d[i], h);
                // El canvas ignoraba en silencio un punto no finito; acá un NaN
                // en la geometría rompe el render entero, así que se corta la
                // figura y se sigue en el próximo punto válido.
                if (!double.IsFinite(x) || !double.IsFinite(y))
                {
                    if (abierta) { gc.EndFigure(false); abierta = false; }
                    continue;
                }
                var p = new Point(x, y);
                if (!abierta) { gc.BeginFigure(p, false); abierta = true; algo = true; }
                else gc.LineTo(p);
            }
            if (abierta) gc.EndFigure(false);
        }
        if (!algo) return;

        // Canvas 2D por defecto: lineCap 'butt' (Flat) y lineJoin 'miter'.
        var pen = new Pen(new SolidColorBrush(serie.Color), GrosorSerie)
        {
            LineCap = PenLineCap.Flat,
            LineJoin = PenLineJoin.Miter,
        };
        ctx.DrawGeometry(null, pen, geo);
    }

    private double Y(double v, double h)
    {
        if (Modo == ModoEjeGrafico.CentradoEnCero)
            return h / 2 - (v / Escala) * (h / 2);

        double span = RangoHi - RangoLo;
        return h - ((v - RangoLo) / span) * h;
    }
}

// La aritmética "a la JS" (toFixed, Math.round, String(v)) vive en NumeroJs.cs:
// es UNA sola copia, compartida con la calculadora de siembra. Ninguno de los
// dos formatos de .NET reproduce toFixed; el porqué está documentado allá.

/// <summary>
/// Lectura de un color del diccionario de recursos (tokens PilotXPanel*), con
/// fallback al literal del CSS original por si el tema no está cargado. Los
/// cuatro paneles lo usan para no hardcodear la paleta en la vista.
/// </summary>
internal static class RecursoColor
{
    public static Color Buscar(Control host, string clave, Color fallback)
    {
        try
        {
            if (host.TryFindResource(clave, out var v))
            {
                if (v is Color c) return c;
                if (v is ISolidColorBrush b) return b.Color;
            }
        }
        catch { }
        return fallback;
    }
}
