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
        // Auto-cierre del submenú: al tocar cualquier botón que NO sea un toggle
        // de la columna principal (o sea, un ítem de acción de submenú, o
        // Dirección/CoreX), se cierra el submenú -> la barra vuelve a angosta y
        // deja ver el mapa. Se hace por code-behind (handler de Click) para no
        // tocar el binding de Content de los botones.
        AddHandler(Button.ClickEvent, OnAnyButtonClick, RoutingStrategies.Bubble);
    }

    private void OnAnyButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MenuIzquierdaViewModel vm) return;
        if (e.Source is Button b && !ReferenceEquals(b.Command, vm.ToggleSubmenuCommand))
            vm.OpenSubmenu = null;
    }
}
