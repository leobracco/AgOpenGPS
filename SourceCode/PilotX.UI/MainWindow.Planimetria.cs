// ============================================================================
// MainWindow.Planimetria.cs — capa de alturas sobre el mapa (planimetría fase
// 3): arranca el poller y pinta la leyenda.
//
// El poller (Services/PlanimetriaPoller) arma la textura y las curvas en su
// hilo y entrega por Dispatcher; acá solo se empuja al mapa y se actualiza la
// leyenda (Border "LeyendaPlani" de MainWindow.axaml: arriba a la izquierda,
// sin toques, nunca un popup sobre el mapa GL).
//
// Solo con el mapa GL: la capa es una textura de GPU.
// ============================================================================

using System;
using System.Globalization;
using Avalonia.Controls;
using PilotX.Desktop.Services;

namespace PilotX.Desktop;

public partial class MainWindow
{
    private PlanimetriaPoller? _planiPoller;
    private PlanimetriaMapSnapshot? _planiSnap;
    private PlaniEstado? _planiEstado;

    private static readonly CultureInfo InvPlani = CultureInfo.InvariantCulture;

    private void ArrancarPlanimetria()
    {
        _planiPoller = new PlanimetriaPoller(DeriveOrigin(App.TargetUrl),
            snap =>
            {
                _planiSnap = snap;
                _mapHost?.OnPlanimetria(snap);
                PintarLeyendaPlani();
            },
            est =>
            {
                _planiEstado = est;
                PintarLeyendaPlani();
            });
        Closed += (_, _) => _planiPoller?.Dispose();
    }

    private static string MetrosPlani(double v) => v.ToString("0.00", InvPlani).Replace('.', ',') + " m";

    private static string T(string s) => PilotX.Cockpit.Bars.Traductor.T(s);

    private void PintarLeyendaPlani()
    {
        var caja = this.FindControl<Border>("LeyendaPlani");
        if (caja == null) return;
        var snap = _planiSnap;
        var est = _planiEstado;
        bool visible = snap != null && est != null && est.Habilitada && est.CapaVisible;
        caja.IsVisible = visible;
        if (!visible || snap == null) return;

        bool ambientes = snap.Modo == "ambientes";
        var titulo = this.FindControl<TextBlock>("LeyendaPlaniTitulo");
        if (titulo != null)
            titulo.Text = T(ambientes ? "Ambientes por altura" : "Alturas del lote")
                + (est!.Estado == "calculando" ? " · " + T("recalculando…") : "");
        var alt = this.FindControl<StackPanel>("LeyendaPlaniAlturas");
        var amb = this.FindControl<StackPanel>("LeyendaPlaniAmbientes");
        if (alt != null) alt.IsVisible = !ambientes;
        if (amb != null) amb.IsVisible = ambientes;

        var tMin = this.FindControl<TextBlock>("LeyendaPlaniMin");
        var tMax = this.FindControl<TextBlock>("LeyendaPlaniMax");
        if (tMin != null) tMin.Text = MetrosPlani(snap.ZMin);
        if (tMax != null) tMax.Text = MetrosPlani(snap.ZMax);

        var a = est!.Ambientes;
        var tLoma = this.FindControl<TextBlock>("LeyendaPlaniLoma");
        var tMedia = this.FindControl<TextBlock>("LeyendaPlaniMedia");
        var tBajo = this.FindControl<TextBlock>("LeyendaPlaniBajo");
        if (tLoma != null) tLoma.Text = T("Loma") + (a.CotaLomaM.HasValue ? "  > " + MetrosPlani(a.CotaLomaM.Value) : "");
        if (tMedia != null) tMedia.Text = T("Media loma");
        if (tBajo != null) tBajo.Text = T("Bajo") + (a.CotaBajoM.HasValue ? "  < " + MetrosPlani(a.CotaBajoM.Value) : "");

        var tCurvas = this.FindControl<TextBlock>("LeyendaPlaniCurvas");
        if (tCurvas != null)
            tCurvas.Text = snap.Intervalo > 0 ? T("Curvas cada") + " " + MetrosPlani(snap.Intervalo) : "";

        var filaGuia = this.FindControl<StackPanel>("LeyendaPlaniGuiaFila");
        var tGuia = this.FindControl<TextBlock>("LeyendaPlaniGuia");
        bool hayGuia = snap.CotaGuia.HasValue && snap.RangosGuia.Count > 0;
        if (filaGuia != null) filaGuia.IsVisible = hayGuia;
        if (tGuia != null && hayGuia) tGuia.Text = T("Curva elegida") + " " + MetrosPlani(snap.CotaGuia!.Value);
    }
}
