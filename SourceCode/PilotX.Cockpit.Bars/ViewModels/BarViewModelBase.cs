using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public abstract partial class BarViewModelBase : ObservableObject
{
    protected readonly GuidanceCommandClient _cmd;
    protected BarViewModelBase(GuidanceCommandClient cmd) => _cmd = cmd;

    /// <summary>Todos los botones bindean a este comando con su verbo como
    /// parámetro (CommandParameter="autosteer", etc.).</summary>
    [RelayCommand]
    protected Task Send(string cmd) => _cmd.SendAsync(cmd);

    /// <summary>Actualiza las propiedades observables desde el snapshot.</summary>
    public abstract void Apply(CockpitSnapshot s);

    // Formato con coma decimal, determinístico (compatible con
    // InvariantGlobalization=true): formateo invariante + swap '.'→','.
    protected static string Coma(double v, int dec) =>
        v.ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
}
