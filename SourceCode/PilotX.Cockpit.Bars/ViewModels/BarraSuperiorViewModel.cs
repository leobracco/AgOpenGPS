using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class BarraSuperiorViewModel : BarViewModelBase
{
    public BarraSuperiorViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    [ObservableProperty] private string _speedText = "0,0";
    [ObservableProperty] private string _gpsText = "SIN FIX";
    [ObservableProperty] private string _gpsDotColor = "#E15A5A";
    [ObservableProperty] private string _haText = "0,0";
    [ObservableProperty] private bool _loteEnabled;
    [ObservableProperty] private string _lineBadge = "";
    [ObservableProperty] private bool _lineVisible;
    [ObservableProperty] private string _fechaText = "";

    public override void Apply(CockpitSnapshot s)
    {
        SpeedText = Coma(s.AvgSpeed, 1);
        HaText = Coma(s.WorkedAreaTotalM2 * 0.0001, 1);
        LoteEnabled = s.IsJobStarted;
        (GpsText, GpsDotColor) = s.FixQuality switch
        {
            4 => ("RTK FIJO",  "#4ABA3E"),
            5 => ("RTK FLOAT", "#E2B53E"),
            2 => ("DGPS",      "#E2B53E"),
            1 => ("GPS",       "#E2B53E"),
            8 => ("SIMULADOR", "#8FA092"),
            _ => ("SIN FIX",   "#E15A5A"),
        };
        LineVisible = s.TrackIdx > -1 && s.TracksTotal > 0;
        LineBadge = LineVisible ? $"{s.TrackIdx + 1}/{s.TracksTotal}" : "";
        FechaText = System.DateTime.Now.ToString("HH:mm");
    }
}
