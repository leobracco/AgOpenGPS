using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class BarraSuperiorViewModel : BarViewModelBase
{
    public BarraSuperiorViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    /// <summary>Modo kiosko (PILOTX_KIOSKO=1, lo setea la sesión de cabina):
    /// los botones de ventana (−/☐/✕) desaparecen — el operario no puede
    /// minimizar, restaurar ni cerrar PilotX. En escritorio quedan como
    /// siempre.</summary>
    public bool BotonesVentanaVisibles { get; } =
        System.Environment.GetEnvironmentVariable("PILOTX_KIOSKO") != "1";


    [ObservableProperty] private string _speedText = "0,0";
    [ObservableProperty] private string _gpsText = "SIN FIX";
    [ObservableProperty] private string _gpsDotColor = "#E15A5A";
    [ObservableProperty] private string _haText = "0,0";
    [ObservableProperty] private bool _loteEnabled;
    /// <summary>Nombre del lote abierto, o "SIN LOTE". Antes la pestaña decía
    /// siempre "LOTE", que no informaba nada: con varios lotes parecidos el
    /// operario no tenía forma de confirmar cuál estaba trabajando.</summary>
    [ObservableProperty] private string _loteText = "SIN LOTE";
    /// <summary>Ritmo de trabajo en ha/h. Reemplaza al contador de guías
    /// ("1/2"), que ocupaba el lugar central sin decirle nada útil al
    /// operario: qué guía está siguiendo ya lo ve en el mapa y en la barra
    /// derecha, mientras que a qué ritmo avanza no estaba en ningún lado.</summary>
    [ObservableProperty] private string _haHoraText = "0,0";
    [ObservableProperty] private bool _haHoraVisible;
    [ObservableProperty] private string _fechaText = "";
    // Debug de guiado (rumbo tractor/guía, Δ, índice de paralela, cm a la guía).
    // Lo compone MainWindow.UpdateHeadingDebug() (combina HUD + guidance poller)
    // y lo empuja acá, porque esos datos no están en CockpitSnapshot.
    [ObservableProperty] private string _debugText = "";

    // Batería de la pantalla. La lectura viene de PilotX.UI (BateriaLector,
    // GetSystemPowerStatus local) — acá solo se decide qué mostrar.
    // Sin batería (PC de escritorio) el chip no existe. La alerta es SOLO
    // color (regla: nada de popups ni sonidos sobre el mapa).
    [ObservableProperty] private bool _bateriaVisible;
    [ObservableProperty] private string _bateriaText = "--";
    [ObservableProperty] private string _bateriaColorTexto = "#101612";
    [ObservableProperty] private string _bateriaColorBorde = "#D9E0D9";

    public void AplicarBateria(bool tiene, int pct, bool cargando, bool enchufada)
    {
        BateriaVisible = tiene;
        if (!tiene) return;
        BateriaText = cargando ? $"{pct}%⚡" : $"{pct}%";
        if (enchufada)
        {
            (BateriaColorTexto, BateriaColorBorde) = ("#101612", "#D9E0D9");
        }
        else if (pct < 15)
        {
            (BateriaColorTexto, BateriaColorBorde) = ("#C0261F", "#C0261F");
        }
        else
        {
            (BateriaColorTexto, BateriaColorBorde) = ("#B36A00", "#E2B53E");
        }
    }

    public override void Apply(CockpitSnapshot s)
    {

        SpeedText = Coma(s.AvgSpeed, 1);
        HaText = Coma(s.WorkedAreaTotalM2 * 0.0001, 1);
        LoteEnabled = s.IsJobStarted;
        // El motor manda la carpeta del lote; puede venir vacía aunque el job
        // figure iniciado (por ejemplo justo mientras abre), así que manda el
        // nombre y no la bandera.
        // Estos textos los escribe el ViewModel, no el XAML: el traductor que
        // recorre el árbol los pisaría y el próximo Apply los volvería a poner
        // en castellano. Se traducen acá, en el origen. El NOMBRE del lote
        // nunca se traduce: es un dato que cargó el operario.
        var lote = (s.CurrentFieldDirectory ?? "").Trim();
        LoteText = lote.Length > 0 ? lote.ToUpperInvariant() : Traductor.T("SIN LOTE");
        (GpsText, GpsDotColor) = s.FixQuality switch
        {
            4 => (Traductor.T("RTK FIJO"),  "#4ABA3E"),
            5 => ("RTK FLOAT", "#E2B53E"),
            2 => ("DGPS",      "#E2B53E"),
            1 => ("GPS",       "#E2B53E"),
            8 => (Traductor.T("SIMULADOR"), "#8FA092"),
            _ => (Traductor.T("SIN FIX"),   "#E15A5A"),
        };
        // Ritmo instantáneo: ancho de labor (m) x velocidad (km/h) x 0,1.
        // Es la MISMA cuenta que AgOpenGPS ya usaba en CFieldData.
        // WorkRateHectares — no se inventa una fórmula nueva para que los dos
        // lugares no muestren números distintos del mismo trabajo.
        //
        // Es instantáneo a propósito, no un promedio de la jornada: sirve para
        // decidir AHORA si conviene acelerar, que es cuando el operario mira.
        //
        // Solo con lote abierto y andando: parado da 0,0 y con el lote cerrado
        // no significa nada, así que se oculta en vez de mostrar un cero fijo.
        double haHora = s.ToolWidth * s.AvgSpeed * 0.1;
        HaHoraVisible = s.IsJobStarted && s.ToolWidth > 0;
        HaHoraText = HaHoraVisible ? Coma(haHora, 1) : "0,0";
        FechaText = System.DateTime.Now.ToString("HH:mm");
    }
}
