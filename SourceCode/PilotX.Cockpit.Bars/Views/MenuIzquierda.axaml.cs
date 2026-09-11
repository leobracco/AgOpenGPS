using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Views;

public partial class MenuIzquierda : UserControl
{
    public MenuIzquierda()
    {
        AvaloniaXamlLoader.Load(this);
        // Auto-cierre del submenú: solo al tocar una acción de la columna
        // PRINCIPAL (Dirección/CoreX, "mbtn" sin toggle) se cierra el submenú
        // abierto -> la barra vuelve a angosta y deja ver el mapa. Los ítems
        // DENTRO de un submenú ("sbtn", ej. Brillo +/-) NO lo cierran: hay
        // acciones que el operario repite varias veces seguidas (subir/bajar
        // brillo, tilt, etc.) y antes había que reabrir el submenú en cada
        // toque. Se hace por code-behind (handler de Click) para no tocar el
        // binding de Content de los botones.
        AddHandler(Button.ClickEvent, OnAnyButtonClick, RoutingStrategies.Bubble);
    }

    private void OnAnyButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MenuIzquierdaViewModel vm) return;
        vm.NotifyActivity(); // cualquier toque reinicia el auto-repliegue por inactividad
        if (e.Source is Button b
            && !ReferenceEquals(b.Command, vm.ToggleSubmenuCommand)
            && !b.Classes.Contains("sbtn"))
            vm.OpenSubmenu = null;
    }
}
