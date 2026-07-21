using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class BarraDerechaViewModel : BarViewModelBase
{
    public BarraDerechaViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    [ObservableProperty] private bool _noLoteVisible = true;
    [ObservableProperty] private bool _uturnVisible;
    [ObservableProperty] private bool _isobusVisible;
    [ObservableProperty] private bool _trackNavVisible;
    [ObservableProperty] private bool _contourLockVisible;
    [ObservableProperty] private bool _pilotoActive;
    [ObservableProperty] private bool _secAutoActive;
    [ObservableProperty] private bool _secManualActive;
    [ObservableProperty] private bool _uturnActive;
    [ObservableProperty] private bool _autoTrackActive;
    [ObservableProperty] private bool _contourActive;
    [ObservableProperty] private bool _contourLockActive;

    public override void Apply(CockpitSnapshot s)
    {
        NoLoteVisible = !s.IsJobStarted;
        UturnVisible = s.TrackIdx > -1 && !s.IsContourOn && s.HasBoundary;
        IsobusVisible = s.IsobusAlive;
        TrackNavVisible = s.TracksVisible > 1 && s.TrackIdx > -1 && !s.IsContourOn;
        ContourLockVisible = s.IsContourOn;
        PilotoActive = s.IsAutoSteerOn;
        SecAutoActive = s.IsSectionAutoOn;
        SecManualActive = s.IsSectionManualOn;
        UturnActive = s.IsYouTurnOn;
        AutoTrackActive = s.IsAutoTrackOn;
        ContourActive = s.IsContourOn;
        ContourLockActive = s.IsContourLocked;
    }
}
