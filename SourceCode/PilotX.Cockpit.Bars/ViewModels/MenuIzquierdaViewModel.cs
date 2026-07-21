using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class MenuIzquierdaViewModel : BarViewModelBase
{
    public MenuIzquierdaViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    // Cuál submenú está expandido (null = ninguno). Reemplaza el resize del WebView2.
    [ObservableProperty] private string? _openSubmenu;

    [RelayCommand]
    private void ToggleSubmenu(string name) =>
        OpenSubmenu = OpenSubmenu == name ? null : name;

    public override void Apply(CockpitSnapshot s) { /* no hace polling de estado */ }
}
