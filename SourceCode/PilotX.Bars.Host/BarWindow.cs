using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace PilotX.Bars.Host;

public enum BarEdge { Top, Right, Bottom, Left }

/// <summary>Ventana sin decoraciones anclada a un borde de la pantalla
/// primaria. Hostea un UserControl de PilotX.Cockpit.Bars.</summary>
public sealed class BarWindow : Window
{
    private readonly BarEdge _edge;
    private readonly double _thickness; // alto (top/bottom) o ancho (left/right), en px logicos
    // Insets verticales SOLO para Left/Right: hay que dejar libre el alto real
    // de las barras Top/Bottom (no el propio thickness, que es el ANCHO de
    // estas barras laterales). Sin esto quedan huecos de 10-30px en las 4
    // esquinas donde el mapa se ve por detras de las barras.
    private readonly double _topInset;
    private readonly double _bottomInset;

    public BarWindow(BarEdge edge, double thickness, Control content, double topInset = 0, double bottomInset = 0)
    {
        _edge = edge;
        _thickness = thickness;
        _topInset = topInset;
        _bottomInset = bottomInset;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        Background = null;
        Content = content;
        Opened += (_, _) =>
        {
            var h = TryGetPlatformHandle();
            if (h != null) Win32NoActivate.Apply(h.Handle);
            Reposition();
        };
    }

    public void Reposition()
    {
        var screen = Screens.Primary ?? Screens.All[0];
        var wa = screen.WorkingArea; // px fisicos
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        int ownPx = (int)(_thickness * scale);
        switch (_edge)
        {
            case BarEdge.Top:
                Position = wa.Position; Width = wa.Width / scale; Height = _thickness; break;
            case BarEdge.Bottom:
                Position = new PixelPoint(wa.X, wa.Y + wa.Height - ownPx);
                Width = wa.Width / scale; Height = _thickness; break;
            case BarEdge.Left:
            {
                // El ancho/X de la barra sale del thickness PROPIO; el alto y
                // el offset Y salen de los insets de Top/Bottom (thickness de
                // ESAS barras), no del propio, para no dejar huecos.
                int topPx = (int)(_topInset * scale);
                int botPx = (int)(_bottomInset * scale);
                Position = new PixelPoint(wa.X, wa.Y + topPx);
                Width = _thickness; Height = (wa.Height - topPx - botPx) / scale;
                break;
            }
            case BarEdge.Right:
            {
                int topPx = (int)(_topInset * scale);
                int botPx = (int)(_bottomInset * scale);
                Position = new PixelPoint(wa.X + wa.Width - ownPx, wa.Y + topPx);
                Width = _thickness; Height = (wa.Height - topPx - botPx) / scale;
                break;
            }
        }
    }
}
