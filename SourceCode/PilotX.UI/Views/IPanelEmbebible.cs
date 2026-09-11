using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PilotX.Desktop.Views;

/// <summary>
/// Panel de módulo que sabe achicarse para vivir ADENTRO de la Configuración.
///
/// Los paneles nativos (Hub, QuantiX, VistaX, …) nacieron como TARJETAS
/// FLOTANTES: marco propio (borde + sombra + esquinas redondeadas), cabecera
/// con título grande y ✕, y MaxWidth pensado para toda la pantalla. Cuando
/// ConfigPanel los monta en su área de contenido (~700 px), eso queda
/// "tarjeta adentro de tarjeta": doble borde, doble cabecera, dos ✕ y todo
/// gigante (reporte 2026-08-18).
///
/// ModoEmbebido() se llama UNA vez, al crear la instancia embebida, y solo
/// cambia lo visual: suelta el marco de tarjeta (el shell ya pone el suyo),
/// esconde el título grande y el ✕ propio (el shell ya tiene subtítulo y ✕),
/// libera MaxWidth/MaxHeight para llenar el hueco, y compacta los márgenes
/// grandes. Lo ÚTIL de la cabecera queda: pills de estado/conexión y botones
/// de acción siguen visibles en una fila compacta arriba del contenido.
///
/// Las instancias flotantes (overlays de MainWindow) NUNCA llaman a este
/// método: por defecto todo queda exactamente como está.
/// </summary>
public interface IPanelEmbebible
{
    /// <summary>Pasa el panel a modo embebido (solo visual, irreversible
    /// para esta instancia — la Configuración cachea la suya aparte).</summary>
    void ModoEmbebido();

    /// <summary>Las pills de estado y los botones de acción de la cabecera del
    /// panel, DESPRENDIDOS de su árbol para que el shell los muestre en su
    /// barra de contexto (siempre en el mismo lugar, para todas las entradas).
    /// Se llama UNA vez, después de ModoEmbebido(), solo en la instancia
    /// embebida. Null = esta entrada no tiene pills (la barra muestra solo el
    /// nombre). El panel sigue actualizando esos controles por referencia:
    /// reubicarlos no corta el cableado de FindControl.</summary>
    Control? PillsDeContexto() => null;
}

/// <summary>Recetario compartido de ModoEmbebido(): las dos operaciones que
/// repiten los 12 paneles, para que cada uno quede en tres líneas.</summary>
public static class PanelEmbebido
{
    /// <summary>Le suelta el marco de tarjeta al Border raíz: sin borde, sin
    /// sombra, sin esquinas, padding mínimo, y libre de MaxWidth/MaxHeight
    /// para llenar el hueco que le da el shell (que ya pone su propio marco).</summary>
    public static void SoltarMarco(Border? card)
    {
        if (card == null) return;
        card.BorderThickness = new Thickness(0);
        card.BoxShadow = default;
        card.CornerRadius = new CornerRadius(0);
        // Padding 0: el padding del área de contenido lo pone el SHELL, igual
        // para todas las entradas — así el esqueleto no cambia al navegar.
        card.Padding = new Thickness(0);
        card.MaxWidth = double.PositiveInfinity;
        card.MaxHeight = double.PositiveInfinity;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.VerticalAlignment = VerticalAlignment.Stretch;
        card.Margin = new Thickness(0);
    }

    /// <summary>Esconde lo redundante de la cabecera (título grande, ✕ propio):
    /// el shell de la Configuración ya muestra subtítulo y ✕.</summary>
    public static void Ocultar(Control? c)
    {
        if (c != null) c.IsVisible = false;
    }

    /// <summary>Desprende un control de su padre (Panel/Decorator/Content)
    /// para poder reubicarlo — p. ej. mandarlo a la barra de contexto del
    /// shell. Devuelve el mismo control (o null si no había nada).</summary>
    public static Control? Desprender(Control? c)
    {
        if (c == null) return null;
        switch (c.Parent)
        {
            case Panel p: p.Children.Remove(c); break;
            case Decorator d when ReferenceEquals(d.Child, c): d.Child = null; break;
            case ContentControl cc when ReferenceEquals(cc.Content, c): cc.Content = null; break;
        }
        return c;
    }

    /// <summary>Arma la fila para la barra de contexto del shell: desprende
    /// cada control y los apila horizontales. Además ESCONDE la cabecera vieja
    /// (el padre del primero), que queda vacía y solo sumaría un hueco.
    /// Null si no había nada que mostrar.</summary>
    public static Control? FilaDeContexto(params Control?[] items)
    {
        Control? cabeceraVieja = null;
        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var c in items)
        {
            if (c == null) continue;
            cabeceraVieja ??= c.Parent as Control;
            Desprender(c);
            fila.Children.Add(c);
        }
        if (fila.Children.Count == 0) return null;
        if (cabeceraVieja != null) cabeceraVieja.IsVisible = false;
        return fila;
    }
}
