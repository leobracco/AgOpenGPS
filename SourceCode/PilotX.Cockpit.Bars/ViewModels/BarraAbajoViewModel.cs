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

    // Botonera de secciones individuales (espejo de los btnSectionXMan nativos).
    // Cada botón cicla Off → Auto → On mandando "seccion_<n>" al motor.
    public System.Collections.ObjectModel.ObservableCollection<SeccionBotonViewModel> Secciones { get; }
        = new();
    [ObservableProperty] private bool _seccionesVisible;

    public override void Apply(CockpitSnapshot s)
    {
        AplicarSecciones(s);
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

    /// <summary>
    /// Arma/actualiza los botones de sección. Sin lote abierto no se muestran:
    /// el control de secciones no aplica (misma regla que el nativo, donde
    /// isJobStarted es la compuerta de todo el control de secciones).
    ///
    /// Los botones se REUSAN entre ticks: recrear la lista 4 veces por segundo
    /// haría parpadear la barra entera.
    /// </summary>
    private void AplicarSecciones(CockpitSnapshot s)
    {
        if (s.IsSectionsNotZones) AplicarModoSecciones(s);
        else AplicarModoZonas(s);
    }

    /// <summary>Un botón por sección (≤16), comando <c>seccion_&lt;n&gt;</c>.</summary>
    private void AplicarModoSecciones(CockpitSnapshot s)
    {
        int n = s.NumSections;
        if (n < 0) n = 0;
        if (n > 16) n = 16;   // el nativo tiene 16 botones como máximo

        Reconstruir(n, i => new SeccionBotonViewModel(i));
        SeccionesVisible = s.IsJobStarted && n > 0;

        var estados = s.SectionStates;
        for (int i = 0; i < Secciones.Count; i++)
        {
            // Backend sin section_states (o array corto): todo Off, sin romper.
            Secciones[i].SetEstado(EstadoDe(estados, i));
        }
    }

    /// <summary>
    /// Un botón por ZONA (≤8), comando <c>zona_&lt;n&gt;</c>. El color sale de la
    /// ÚLTIMA sección de la zona, que es de donde lo lee el handler nativo
    /// (<c>section[zoneRanges[zona]-1]</c>): todas las secciones de una zona se
    /// mueven juntas, así que cualquiera sirve, pero se respeta la del nativo
    /// para no divergir si alguna vez quedan desparejas.
    /// </summary>
    private void AplicarModoZonas(CockpitSnapshot s)
    {
        var rangos = s.ZoneRanges;
        // Modo zonas declarado pero sin rangos (backend viejo): mejor no mostrar
        // nada que ofrecer botones que no van a hacer lo que el operario espera.
        int zonas = 0;
        if (rangos != null)
            for (int i = 0; i < rangos.Length && i < 8 && rangos[i] > 0; i++) zonas++;

        Reconstruir(zonas, i => new SeccionBotonViewModel(i, "zona_" + i));
        SeccionesVisible = s.IsJobStarted && zonas > 0;

        if (rangos == null) return;   // sin rangos no hay botones que pintar

        var estados = s.SectionStates;
        for (int z = 0; z < Secciones.Count && z < rangos.Length; z++)
        {
            int ultimaSeccion = rangos[z] - 1;   // rangos[z] = corte de la zona z+1
            Secciones[z].SetEstado(EstadoDe(estados, ultimaSeccion));
        }
    }

    private static int EstadoDe(int[]? estados, int idx)
        => (estados != null && idx >= 0 && idx < estados.Length) ? estados[idx] : 0;

    /// <summary>Rearma la lista solo si cambió la cantidad: recrearla en cada
    /// tick haría parpadear la barra 8 veces por segundo.</summary>
    private void Reconstruir(int cantidad, System.Func<int, SeccionBotonViewModel> crear)
    {
        if (Secciones.Count == cantidad) return;
        Secciones.Clear();
        for (int i = 1; i <= cantidad; i++) Secciones.Add(crear(i));
    }
}
