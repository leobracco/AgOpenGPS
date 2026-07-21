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

    public BarWindow(BarEdge edge, double thickness, Control content)
    {
        _edge = edge;
        _thickness = thickness;
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
        int t = (int)(_thickness * scale);
        switch (_edge)
        {
            case BarEdge.Top:
                Position = wa.Position; Width = wa.Width / scale; Height = _thickness; break;
            case BarEdge.Bottom:
                Position = new PixelPoint(wa.X, wa.Y + wa.Height - t);
                Width = wa.Width / scale; Height = _thickness; break;
            case BarEdge.Left:
                Position = new PixelPoint(wa.X, wa.Y + t);
                Width = _thickness; Height = (wa.Height - 2 * t) / scale; break;
            case BarEdge.Right:
                Position = new PixelPoint(wa.X + wa.Width - t, wa.Y + t);
                Width = _thickness; Height = (wa.Height - 2 * t) / scale; break;
        }
    }
}
