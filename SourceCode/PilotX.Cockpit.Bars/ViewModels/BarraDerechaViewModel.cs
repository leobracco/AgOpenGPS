using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

/// <summary>
/// Barra lateral derecha (espejo del panelRight nativo): Piloto, U-turn,
/// Secciones auto/manual, ISOBUS, AutoTrack, ciclado de guías, Contorno y
/// candado. Expone rutas de ícono ("barra-derecha/xxx.png") + visibilidades,
/// idénticas a las reglas de barra-derecha.js.
/// </summary>
public sealed partial class BarraDerechaViewModel : BarViewModelBase
{
    public BarraDerechaViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    private const string D = "barra-derecha/";

    [ObservableProperty] private bool _noLoteVisible = true;

    // Piloto (siempre visible; imagen on/off + snap-to-pivot; habilitado si hay guía o contorno)
    [ObservableProperty] private string _pilotoImg = D + "AutoSteerOff.png";
    [ObservableProperty] private bool _pilotoEnabled;

    // U-turn (visible con guía + sin contorno + lindero)
    [ObservableProperty] private bool _uturnVisible;
    [ObservableProperty] private string _uturnImg = D + "YouTurnNo.png";

    // Secciones auto/manual (siempre visibles)
    [ObservableProperty] private string _secAutoImg = D + "SectionMasterOff.png";
    [ObservableProperty] private string _secManualImg = D + "ManualOff.png";

    // ISOBUS (visible con comunicación viva)
    [ObservableProperty] private bool _isobusVisible;
    [ObservableProperty] private string _isobusImg = D + "IsobusSectionControlOff.png";

    // AutoTrack + ciclado (visible con 2+ guías, guía activa, sin contorno)
    [ObservableProperty] private bool _trackNavVisible;
    [ObservableProperty] private string _autoTrackImg = D + "AutoTrackOff.png";

    // Contorno (siempre visible) + candado (visible con contorno activo)
    [ObservableProperty] private string _contourImg = D + "ContourOff.png";
    [ObservableProperty] private bool _contourLockVisible;
    [ObservableProperty] private string _contourLockImg = D + "ColorUnlocked.png";

    // Guía activa "n/total" (visible con guía + sin contorno)
    [ObservableProperty] private bool _numCuVisible;
    [ObservableProperty] private string _numCuText = "";

    // Giro manual (←/→) y salto de guías. Solo con el giro automático ACTIVO:
    // sin eso no hay a dónde girar y serían botones muertos ocupando barra.
    [ObservableProperty] private bool _giroManualVisible;
    [ObservableProperty] private string _skipImg = D + "YouSkipOff.png";
    [ObservableProperty] private string _skipText = "";

    public override void Apply(CockpitSnapshot s)
    {
        bool hayGuia = s.TrackIdx > -1;
        bool contour = s.IsContourOn;

        NoLoteVisible = !s.IsJobStarted;

        PilotoImg = D + (s.IsAutoSteerOn ? "AutoSteerOn" : "AutoSteerOff")
                      + (s.IsAutoSnapToPivot ? "SnapToPivot" : "") + ".png";
        PilotoEnabled = hayGuia || contour;

        UturnVisible = hayGuia && !contour && s.HasBoundary;
        UturnImg = D + (s.IsYouTurnOn ? "YouTurn80.png" : "YouTurnNo.png");

        SecAutoImg = D + (s.IsSectionAutoOn ? "SectionMasterOn.png" : "SectionMasterOff.png");
        SecManualImg = D + (s.IsSectionManualOn ? "ManualOn.png" : "ManualOff.png");

        IsobusVisible = s.IsobusAlive;
        IsobusImg = D + (s.IsobusOn ? "IsobusSectionControlOn.png" : "IsobusSectionControlOff.png");

        TrackNavVisible = s.TracksVisible > 1 && hayGuia && !contour;
        AutoTrackImg = D + (s.IsAutoTrackOn ? "AutoTrack.png" : "AutoTrackOff.png");

        ContourImg = D + (contour ? "ContourOn.png" : "ContourOff.png");
        ContourLockVisible = contour;
        ContourLockImg = D + (s.IsContourLocked ? "ColorLocked.png" : "ColorUnlocked.png");

        NumCuVisible = hayGuia && s.TracksTotal > 0 && !contour;
        NumCuText = NumCuVisible ? $"{s.TrackIdx + 1}/{s.TracksTotal}" : "";

        GiroManualVisible = UturnVisible && s.IsYouTurnOn;
        SkipImg = D + (s.YouTurnSkipMode == "ignora_trabajadas" ? "YouSkipWorkedTracks.png"
                     : s.YouTurnSkipMode == "alternado" ? "YouSkipOn.png"
                     : "YouSkipOff.png");
        // El número va escrito: cuántas guías saltea decide por dónde sigue la
        // máquina, y en el original solo se distinguía por el ícono.
        SkipText = s.YouTurnSkipWidth > 1 ? s.YouTurnSkipWidth.ToString() : "";
    }
}
