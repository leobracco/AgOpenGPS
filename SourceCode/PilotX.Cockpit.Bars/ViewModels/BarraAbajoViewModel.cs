using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class BarraAbajoViewModel : BarViewModelBase
{
    public BarraAbajoViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    [ObservableProperty] private bool _noLoteVisible = true;
    [ObservableProperty] private bool _nudgeVisible;
    [ObservableProperty] private bool _headlandVisible;
    [ObservableProperty] private bool _hydVisible;
    [ObservableProperty] private bool _hydEnabled;
    [ObservableProperty] private bool _tramVisible;
    [ObservableProperty] private bool _youSkipVisible;
    [ObservableProperty] private bool _skipsVisible;
    [ObservableProperty] private string _flagColorHex = "#E15A5A";
    [ObservableProperty] private int _skipsValue = 1;

    public override void Apply(CockpitSnapshot s)
    {
        NoLoteVisible = !s.IsJobStarted;
        NudgeVisible = s.TrackIdx > -1 && s.IsNudgeOn;
        HeadlandVisible = s.HasHeadland;
        HydVisible = s.HasHydLift && s.HasHeadland;
        HydEnabled = s.IsHeadlandOn;
        TramVisible = s.HasTram;
        YouSkipVisible = s.TrackIdx > -1;
        SkipsVisible = s.TrackIdx > -1;
        FlagColorHex = s.FlagColor switch { 1 => "#4ABA3E", 2 => "#E2B53E", _ => "#E15A5A" };
        SkipsValue = s.RowSkipsWidth;
    }
}
