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
        card.Padding = new Thickness(4);
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
}
