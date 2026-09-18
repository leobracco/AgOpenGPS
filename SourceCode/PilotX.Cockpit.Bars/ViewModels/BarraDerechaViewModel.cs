using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    // Auto-repliegue por inactividad, igual que el menú izquierdo: el operario
    // no tiene por qué acordarse de cerrar la barra, y el mapa recupera el
    // lugar solo. Mismo minuto que MenuIzquierdaViewModel — si las dos barras
    // se replegaran con tiempos distintos parecería que una está fallada.
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(1);
    private readonly DispatcherTimer _inactivityTimer;

    public BarraDerechaViewModel(GuidanceCommandClient cmd) : base(cmd)
    {
        _inactivityTimer = new DispatcherTimer { Interval = InactivityTimeout };
        _inactivityTimer.Tick += (_, _) =>
        {
            _inactivityTimer.Stop();
            if (IsCollapsed) return;
            IsCollapsed = true;
        };
        _inactivityTimer.Start();
    }

    /// <summary>Barra plegada a la pestaña del handle, para no comerle lugar
    /// al mapa. El handle queda SIEMPRE visible: es la única forma de
    /// volver a desplegarla.</summary>
    [ObservableProperty] private bool _isCollapsed;

    [RelayCommand]
    private void ToggleCollapsed()
    {
        IsCollapsed = !IsCollapsed;
        _inactivityTimer.Stop();
        if (!IsCollapsed) _inactivityTimer.Start();
    }

    /// <summary>La llama el code-behind ante CUALQUIER toque dentro de la
    /// barra: así el minuto se cuenta desde el último toque, no desde que se
    /// desplegó.</summary>
    public void NotifyActivity()
    {
        if (IsCollapsed) return;
        _inactivityTimer.Stop();
        _inactivityTimer.Start();
    }

    private const string D = "barra-derecha/";
    // Bajada de la barra de abajo: el color de la bandera es estado.
    // Bajados de la barra de abajo junto con sus botones: el ícono es estado.
    private const string BAbajo = "barra-abajo/";

    // ---- ESTADO EN EL BOTÓN, no solo en el dibujo -----------------------
    // Los "…On" son el MISMO booleano que elige la imagen; existen aparte
    // para que la vista pueda pintar el BOTÓN entero (Classes.on) y no solo
    // el PNG de 26 px de adentro. "¿Está puesto el piloto?" es la pregunta
    // que más se hace por pasada: a 8 km/h, resolverla mirando el dibujito
    // cuesta casi 4 metros sin mirar el lote.
    [ObservableProperty] private bool _tramVisible;
    [ObservableProperty] private string _tramImg = BAbajo + "TramOff.png";
    [ObservableProperty] private bool _hydVisible;
    [ObservableProperty] private bool _hydEnabled;
    [ObservableProperty] private bool _hydOn;
    [ObservableProperty] private string _hydImg = BAbajo + "HydraulicLiftOff.png";
    [ObservableProperty] private bool _nudgeVisible;
    [ObservableProperty] private bool _youSkipVisible;
    [ObservableProperty] private bool _youSkipOn;
    [ObservableProperty] private string _youSkipImg = BAbajo + "YouSkipOff.png";
    [ObservableProperty] private bool _headlandVisible;
    [ObservableProperty] private bool _headlandOn;
    [ObservableProperty] private string _headlandImg = BAbajo + "HeadlandOff.png";
    [ObservableProperty] private bool _hdlSecOn;
    [ObservableProperty] private string _hdlSecImg = BAbajo + "HeadlandSectionOff.png";
    [ObservableProperty] private string _flagImg = "barra-abajo/FlagRed.png";


    [ObservableProperty] private bool _noLoteVisible = true;

    // Piloto (siempre visible; imagen on/off + snap-to-pivot; habilitado si hay guía o contorno)
    [ObservableProperty] private string _pilotoImg = D + "AutoSteerOff.png";
    [ObservableProperty] private bool _pilotoEnabled;
    [ObservableProperty] private bool _pilotoOn;

    // U-turn (visible con guía + sin contorno + lindero)
    [ObservableProperty] private bool _uturnVisible;
    [ObservableProperty] private bool _uturnOn;
    [ObservableProperty] private string _uturnImg = D + "YouTurnNo.png";

    // Marcas de "Marcar giro" (turn_marks del HUD): controla la visibilidad
    // del botón "Borrar" del overlay de la pasada — sin marcas no hay nada
    // que borrar y el botón sería ruido.
    [ObservableProperty] private bool _hayMarcasGiro;

    // Secciones auto/manual (siempre visibles)
    [ObservableProperty] private bool _secAutoOn;
    [ObservableProperty] private string _secAutoImg = D + "SectionMasterOff.png";
    [ObservableProperty] private bool _secManualOn;
    [ObservableProperty] private string _secManualImg = D + "ManualOff.png";

    // ISOBUS (visible con comunicación viva)
    [ObservableProperty] private bool _isobusVisible;
    [ObservableProperty] private bool _isobusOn;
    [ObservableProperty] private string _isobusImg = D + "IsobusSectionControlOff.png";

    // AutoTrack + ciclado (visible con 2+ guías, guía activa, sin contorno)
    [ObservableProperty] private bool _trackNavVisible;
    [ObservableProperty] private bool _autoTrackOn;
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
        TramVisible = s.HasTram;
        TramImg = BAbajo + (s.TramDisplayMode switch { 1 => "TramAll.png", 2 => "TramLines.png", 3 => "TramOuter.png", _ => "TramOff.png" });
        HydVisible = s.HasHydLift && s.HasHeadland;
        HydEnabled = s.IsHeadlandOn;
        HydOn = s.IsHydLiftOn;
        HydImg = BAbajo + (s.IsHydLiftOn ? "HydraulicLiftOn.png" : "HydraulicLiftOff.png");
        // Con guía activa alcanza: exigir además IsNudgeOn (un modo que se
        // prende en otro lado) los dejaba grises justo cuando hacían falta.
        NudgeVisible = s.TrackIdx > -1;

        YouSkipVisible = s.TrackIdx > -1;   // hay guía activa
        YouSkipOn = s.YouSkipMode != 0;
        YouSkipImg = BAbajo + (s.YouSkipMode switch { 1 => "YouSkipOn.png", 2 => "YouSkipWorkedTracks.png", _ => "YouSkipOff.png" });
        HeadlandVisible = s.HasHeadland;
        HeadlandOn = s.IsHeadlandOn;
        HeadlandImg = BAbajo + (s.IsHeadlandOn ? "HeadlandOn.png" : "HeadlandOff.png");
        HdlSecOn = s.IsSectionControlledByHeadland;
        HdlSecImg = BAbajo + (s.IsSectionControlledByHeadland ? "HeadlandSectionOn.png" : "HeadlandSectionOff.png");

        FlagImg = "barra-abajo/" + (s.FlagColor switch { 1 => "FlagGrn.png", 2 => "FlagYel.png", _ => "FlagRed.png" });

        bool hayGuia = s.TrackIdx > -1;
        bool contour = s.IsContourOn;

        NoLoteVisible = !s.IsJobStarted;

        PilotoOn = s.IsAutoSteerOn;
        PilotoImg = D + (s.IsAutoSteerOn ? "AutoSteerOn" : "AutoSteerOff")
                      + (s.IsAutoSnapToPivot ? "SnapToPivot" : "") + ".png";
        PilotoEnabled = hayGuia || contour;

        UturnVisible = hayGuia && !contour && s.HasBoundary;
        UturnOn = s.IsYouTurnOn;
        UturnImg = D + (s.IsYouTurnOn ? "YouTurn80.png" : "YouTurnNo.png");

        SecAutoOn = s.IsSectionAutoOn;
        SecAutoImg = D + (s.IsSectionAutoOn ? "SectionMasterOn.png" : "SectionMasterOff.png");
        SecManualOn = s.IsSectionManualOn;
        SecManualImg = D + (s.IsSectionManualOn ? "ManualOn.png" : "ManualOff.png");

        IsobusVisible = s.IsobusAlive;
        IsobusOn = s.IsobusOn;
        IsobusImg = D + (s.IsobusOn ? "IsobusSectionControlOn.png" : "IsobusSectionControlOff.png");

        TrackNavVisible = s.TracksVisible > 1 && hayGuia && !contour;
        AutoTrackOn = s.IsAutoTrackOn;
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
