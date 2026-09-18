// PanelArrastrable.cs
//
// Hace que una card flotante (los overlays nativos que se dibujan sobre el
// mapa) se pueda CORRER con el dedo, agarrandola del header.
//
// Por que: la card queda centrada y tapa justo la parte del mapa donde esta
// el tractor. Cerrarla para mirar y volver a abrirla es la unica salida hoy
// (pedido 2026-08-18: "el overlay se debe poder mover"). Corriendola, el
// operario ve las dos cosas a la vez.
//
// Como: TranslateTransform sobre el UserControl host. NO se toca el layout
// (la card sigue centrada por Alignment), asi que no hay reflow ni pelea con
// el ScrollViewer de adentro. El area vacia del host no tiene Background, no
// recibe hits: el mapa se sigue tocando alrededor de la card.
//
// El agarre es el Grid del header, marcado con Name="DragHandle". Los
// controles de adentro (el ✕, la pill) marcan el PointerPressed como Handled
// antes de que burbujee, asi que tocar el ✕ NO arrastra.
//
// Doble toque en el header = vuelve al centro, por si el operario la corrio
// fuera de la pantalla o se perdio.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PilotX.Desktop.Views;

public static class PanelArrastrable
{
    // Cuanto se puede alejar del centro, como fraccion del contenedor. 0.42
    // deja correrla casi hasta el borde sin que se escape entera de la
    // pantalla (siempre queda el header a la vista para traerla de vuelta).
    private const double MaxFraccion = 0.42;

    /// <summary>
    /// Habilita el arrastre de <paramref name="panel"/> agarrandolo del
    /// control llamado <paramref name="nombreAgarre"/>. Si el panel no tiene
    /// ese control, no hace nada (queda fijo, como antes).
    /// </summary>
    public static void Habilitar(Control? panel, string nombreAgarre = "DragHandle")
    {
        if (panel == null) return;
        var agarre = panel.FindControl<Control>(nombreAgarre);
        if (agarre == null) return;

        var mover = new TranslateTransform();
        panel.RenderTransform = mover;

        agarre.Cursor = new Cursor(StandardCursorType.SizeAll);

        bool arrastrando = false;
        Point origen = default;
        double x0 = 0, y0 = 0;

        agarre.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(panel).Properties;
            // Touch no reporta boton izquierdo: se acepta si no es un boton
            // secundario del mouse.
            if (props.IsRightButtonPressed || props.IsMiddleButtonPressed) return;

            arrastrando = true;
            origen = e.GetPosition(Contenedor(panel));
            x0 = mover.X;
            y0 = mover.Y;
            e.Pointer.Capture(agarre);
        };

        agarre.PointerMoved += (_, e) =>
        {
            if (!arrastrando) return;
            var p = e.GetPosition(Contenedor(panel));
            mover.X = x0 + (p.X - origen.X);
            mover.Y = y0 + (p.Y - origen.Y);
            Limitar(panel, mover);
        };

        agarre.PointerReleased += (_, e) =>
        {
            if (!arrastrando) return;
            arrastrando = false;
            e.Pointer.Capture(null);
        };

        // Perder la captura (otra ventana, alarma que roba el foco) tiene que
        // soltar el arrastre igual, si no la card sigue pegada al puntero.
        agarre.PointerCaptureLost += (_, _) => arrastrando = false;

        agarre.DoubleTapped += (_, _) =>
        {
            mover.X = 0;
            mover.Y = 0;
        };
    }

    /// <summary>Vuelve a poner la card en el centro.</summary>
    public static void Centrar(Control? panel)
    {
        if (panel?.RenderTransform is TranslateTransform t)
        {
            t.X = 0;
            t.Y = 0;
        }
    }

    private static Visual Contenedor(Control panel)
        => (panel.Parent as Visual) ?? panel;

    private static void Limitar(Control panel, TranslateTransform t)
    {
        if (panel.Parent is not Control host) return;
        double maxX = Math.Max(0, host.Bounds.Width  * MaxFraccion);
        double maxY = Math.Max(0, host.Bounds.Height * MaxFraccion);
        t.X = Math.Clamp(t.X, -maxX, maxX);
        t.Y = Math.Clamp(t.Y, -maxY, maxY);
    }
}
