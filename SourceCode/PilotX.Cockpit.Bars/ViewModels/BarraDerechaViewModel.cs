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

    // Giro manual (↰/↱) y selector de salto del giro. Solo con el giro
    // automático ACTIVO: sin eso no hay a dónde girar y serían controles
    // muertos ocupando barra.
    [ObservableProperty] private bool _giroManualVisible;

    // Saltear guía (⇤/⇥): correrse a la guía de al lado siguiendo para
    // adelante. NO depende del giro automático — alcanza con tener una guía.
    [ObservableProperty] private bool _lateralVisible;

    /// <summary>0..9, para el desplegable.</summary>
    public int[] SaltosPosibles { get; } = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    private int _saltoDelGiro;   // 0 = va a la contigua (el default del motor)
    private bool _aplicandoSnapshot;

    /// <summary>
    /// Guías que SALTEA el giro: 0 = va a la contigua, 1 = saltea una, etc.
    /// (El motor trabaja en ancho = salteadas + 1; la conversión está en
    /// Apply.) El setter manda el comando al motor, salvo cuando el valor
    /// viene del snapshot: sin ese guard, cada refresco del HUD reenviaría el
    /// comando 10 veces por segundo.
    /// </summary>
    public int SaltoDelGiro
    {
        get => _saltoDelGiro;
        set
        {
            if (!SetProperty(ref _saltoDelGiro, value)) return;
            if (_aplicandoSnapshot) return;
            _ = Send("uturn_skip_" + value);
        }
    }

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

        // Saltear guía solo necesita una guía: es ir derecho corriéndose de
        // línea, no tiene nada que ver con el giro en cabecera.
        LateralVisible = hayGuia && !contour;

        // Se refleja lo que dice el motor sin re-disparar el comando.
        // El motor habla en ANCHO (rowSkipsWidth, 1 = contigua); este menú
        // muestra guías SALTEADAS (0 = contigua): display = ancho − 1.
        _aplicandoSnapshot = true;
        int salteadas = s.YouTurnSkipWidth - 1;
        SaltoDelGiro = salteadas < 0 ? 0 : (salteadas > 9 ? 9 : salteadas);
        _aplicandoSnapshot = false;
    }
}
