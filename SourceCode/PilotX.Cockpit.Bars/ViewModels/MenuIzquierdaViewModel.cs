using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class MenuIzquierdaViewModel : BarViewModelBase
{
    public MenuIzquierdaViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    // Cuál submenú está expandido (null = ninguno). Reemplaza el resize del WebView2.
    [ObservableProperty] private string? _openSubmenu;

    // Menú plegado a una pestaña angosta (solo el handle de despliegue), para
    // no ocupar lugar del mapa cuando el operario no lo necesita.
    [ObservableProperty] private bool _isCollapsed;

    [RelayCommand]
    private void ToggleSubmenu(string name) =>
        OpenSubmenu = OpenSubmenu == name ? null : name;

    [RelayCommand]
    private void ToggleCollapsed()
    {
        IsCollapsed = !IsCollapsed;
        if (IsCollapsed) OpenSubmenu = null; // no dejar un submenú abierto atrás del handle
    }

    // El auto-cierre del submenú al ejecutar una acción lo maneja el code-behind
    // de MenuIzquierda (handler de Click), para no meter comandos async en los
    // botones (rompían el render del Content del botón).

    public override void Apply(CockpitSnapshot s) { /* no hace polling de estado */ }
}
