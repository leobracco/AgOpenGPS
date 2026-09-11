using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Views;

public partial class BarraAbajo : UserControl
{
    public BarraAbajo() => AvaloniaXamlLoader.Load(this);

    // El selector de skips no tiene lugar en el VM (mantenerlo UI-free): el
    // indice 0-based del ComboBox se traduce aca al verbo "skips_<n>" (1-based)
    // y se manda por el mismo SendCommand que usan los botones.
    private void OnSkipsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not BarraAbajoViewModel vm) return;
        if (sender is not ComboBox combo || combo.SelectedIndex < 0) return;
        int value = combo.SelectedIndex + 1;
        // El binding SelectedIndex es OneWay, pero Avalonia igual dispara
        // SelectionChanged cuando Apply() actualiza SkipsValue desde el polling
        // de estado (~250ms). Si el valor coincide con el del VM, no fue el
        // operario tocando el combo: no reenviar el comando al backend.
        if (value == vm.SkipsValue) return;
        vm.SendCommand.Execute("skips_" + value);
    }
}
