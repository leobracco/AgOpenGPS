using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class MenuIzquierdaViewModel : BarViewModelBase
{
    // Tras este tiempo sin tocar el menú (ningún submenú/botón), se repliega
    // solo a la pestaña angosta -> no hace falta que el operario se acuerde
    // de guardarlo/cerrarlo, y el mapa recupera el lugar automáticamente.
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(1);
    private readonly DispatcherTimer _inactivityTimer;

    public MenuIzquierdaViewModel(GuidanceCommandClient cmd) : base(cmd)
    {
        _inactivityTimer = new DispatcherTimer { Interval = InactivityTimeout };
        _inactivityTimer.Tick += (_, _) =>
        {
            _inactivityTimer.Stop();
            if (IsCollapsed) return;
            OpenSubmenu = null;
            IsCollapsed = true;
        };
        _inactivityTimer.Start();
    }

    // Cuál submenú está expandido (null = ninguno). Reemplaza el resize del WebView2.
    [ObservableProperty] private string? _openSubmenu;

    // Menú plegado a una pestaña angosta (solo el handle de despliegue), para
    // no ocupar lugar del mapa cuando el operario no lo necesita.
    [ObservableProperty] private bool _isCollapsed;

    [RelayCommand]
    private void ToggleSubmenu(string name)
    {
        OpenSubmenu = OpenSubmenu == name ? null : name;
        NotifyActivity();
    }

    [RelayCommand]
    private void ToggleCollapsed()
    {
        IsCollapsed = !IsCollapsed;
        if (IsCollapsed)
        {
            OpenSubmenu = null;
            _inactivityTimer.Stop();
        }
        else
        {
            _inactivityTimer.Stop();
            _inactivityTimer.Start();
        }
    }

    // Reinicia la cuenta de inactividad; la llama el code-behind de
    // MenuIzquierda ante CUALQUIER click dentro del menú (toggles, ítems de
    // submenú, Dirección/CoreX), para que 1 minuto sin tocar nada — no 1
    // minuto desde que se abrió — sea lo que dispare el repliegue.
    public void NotifyActivity()
    {
        if (IsCollapsed) return;
        _inactivityTimer.Stop();
        _inactivityTimer.Start();
    }

    // El auto-cierre del submenú al ejecutar una acción lo maneja el code-behind
    // de MenuIzquierda (handler de Click), para no meter comandos async en los
    // botones (rompían el render del Content del botón).

    public override void Apply(CockpitSnapshot s) { /* no hace polling de estado */ }
}
