using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Views;

public partial class BarraDerecha : UserControl
{
    public BarraDerecha()
    {
        AvaloniaXamlLoader.Load(this);
        // Cualquier toque dentro de la barra reinicia la cuenta de inactividad,
        // igual que en MenuIzquierda: el minuto se cuenta desde el ÚLTIMO toque
        // y no desde que se desplegó. Sin esto la barra se le cerraba en la mano
        // al operario en medio de una secuencia de toques.
        AddHandler(Button.ClickEvent, OnAnyButtonClick, RoutingStrategies.Bubble);
    }

    private void OnAnyButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BarraDerechaViewModel vm) vm.NotifyActivity();
    }
}
