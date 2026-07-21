using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

/// <summary>
/// Barra inferior (espejo del panelBottom nativo): elegir guía, centrar/mover
/// guía, bandera, cabecera, secciones por cabecera, hidráulico, tramlines,
/// reset herramienta, color de mapeo y skips de U-turn. Expone rutas de ícono
/// ("barra-abajo/xxx.png") + visibilidades, idénticas a barra-abajo.js.
/// </summary>
public sealed partial class BarraAbajoViewModel : BarViewModelBase
{
    public BarraAbajoViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    private const string B = "barra-abajo/";

    [ObservableProperty] private bool _noLoteVisible = true;

    // Centrar/mover guía (visible con guía + nudge)
    [ObservableProperty] private bool _nudgeVisible;

    // Bandera (siempre visible; color 0=roja/1=verde/2=amarilla)
    [ObservableProperty] private string _flagImg = B + "FlagRed.png";

    // Cabecera + secciones por cabecera (visibles con cabecera creada)
    [ObservableProperty] private bool _headlandVisible;
    [ObservableProperty] private string _headlandImg = B + "HeadlandOff.png";
    [ObservableProperty] private string _hdlSecImg = B + "HeadlandSectionOff.png";

    // Hidráulico (visible con módulo + cabecera; opera solo con cabecera activa)
    [ObservableProperty] private bool _hydVisible;
    [ObservableProperty] private bool _hydEnabled;
    [ObservableProperty] private string _hydImg = B + "HydraulicLiftOff.png";

    // Tramlines (visible con tram creado; imagen por modo de vista 0..3)
    [ObservableProperty] private bool _tramVisible;
    [ObservableProperty] private string _tramImg = B + "TramOff.png";

    // Salteo de U-turn (visible con guía; imagen por modo 0..2) + selector 1..10
    [ObservableProperty] private bool _youSkipVisible;
    [ObservableProperty] private string _youSkipImg = B + "YouSkipOff.png";
    [ObservableProperty] private bool _skipsVisible;
    [ObservableProperty] private int _skipsValue = 1;

    public override void Apply(CockpitSnapshot s)
    {
        bool hayGuia = s.TrackIdx > -1;
        bool hayHdl = s.HasHeadland;

        NoLoteVisible = !s.IsJobStarted;
        NudgeVisible = hayGuia && s.IsNudgeOn;

        FlagImg = B + (s.FlagColor switch { 1 => "FlagGrn.png", 2 => "FlagYel.png", _ => "FlagRed.png" });

        HeadlandVisible = hayHdl;
        HeadlandImg = B + (s.IsHeadlandOn ? "HeadlandOn.png" : "HeadlandOff.png");
        HdlSecImg = B + (s.IsSectionControlledByHeadland ? "HeadlandSectionOn.png" : "HeadlandSectionOff.png");

        HydVisible = s.HasHydLift && hayHdl;
        HydEnabled = s.IsHeadlandOn;
        HydImg = B + (s.IsHydLiftOn ? "HydraulicLiftOn.png" : "HydraulicLiftOff.png");

        TramVisible = s.HasTram;
        TramImg = B + (s.TramDisplayMode switch { 1 => "TramAll.png", 2 => "TramLines.png", 3 => "TramOuter.png", _ => "TramOff.png" });

        YouSkipVisible = hayGuia;
        YouSkipImg = B + (s.YouSkipMode switch { 1 => "YouSkipOn.png", 2 => "YouSkipWorkedTracks.png", _ => "YouSkipOff.png" });
        SkipsVisible = hayGuia;
        SkipsValue = s.RowSkipsWidth;
    }
}
