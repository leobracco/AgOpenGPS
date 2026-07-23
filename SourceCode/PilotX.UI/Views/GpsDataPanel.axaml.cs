// GpsDataPanel.axaml.cs
//
// Reemplazo nativo de pages/datos-gps.html. Consume HudSnapshot y
// muestra velocidad, rumbo, cardinal, lat/lon, easting/northing locales
// y datos del equipo. Sin red propia: lee del HudPoller que ya esta
// corriendo a 4 Hz.
//
// API: OnSnapshot(HudSnapshot) - invocado por MainWindow desde el
// HudPoller (Dispatcher.UIThread.Post ya garantizado por el caller).

using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class GpsDataPanel : UserControl
{
    // Brushes para la pill de estado (mismo criterio que FieldDataPanel).
    private static readonly IBrush _brushOk   = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushErr  = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushDim  = new SolidColorBrush(Color.Parse("#8FA092"));

    // Cardinales en castellano (8 sectores de 45 grados). Coincide con
    // datos-gps.js para que la traduccion sea identica en ambos shells.
    private static readonly string[] _cardinals =
        { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };

    public GpsDataPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Recibe un snapshot del HUD y refresca toda la vista. Asume que
    /// el caller hizo marshalling al UI thread (MainWindow lo hace via
    /// Dispatcher.UIThread.Post).
    /// </summary>
    public void OnSnapshot(HudSnapshot s)
    {
        // ---- Estado pill ------------------------------------------------
        bool hasLat = s.Latitude != 0.0 || s.Longitude != 0.0;
        var dot = this.FindControl<Ellipse>("EstadoDot");
        var txt = this.FindControl<TextBlock>("EstadoText");
        if (dot != null && txt != null)
        {
            if (s.IsJobStarted && hasLat) { dot.Fill = _brushOk;   txt.Text = "En trabajo"; }
            else if (hasLat)              { dot.Fill = _brushOk;   txt.Text = "Fix OK"; }
            else                          { dot.Fill = _brushDim;  txt.Text = "Sin fix"; }
        }

        // ---- KPIs principales -------------------------------------------
        SetText("KpiVel", s.AvgSpeed.ToString("0.0", CultureInfo.InvariantCulture));

        // heading viene en radianes; lo paso a grados [0..360).
        double deg = (s.Heading * 180.0 / Math.PI) % 360.0;
        if (deg < 0) deg += 360.0;
        SetText("KpiHeading", deg.ToString("0", CultureInfo.InvariantCulture));

        int idx = ((int)Math.Round(deg / 45.0)) % 8;
        if (idx < 0) idx += 8;
        SetText("KpiCardinal", hasLat ? _cardinals[idx] : "--");

        // ---- Coordenadas ------------------------------------------------
        // 7 decimales ~= 11mm de precision, suficiente para auto-guiado.
        if (hasLat)
        {
            SetText("KpiLat", s.Latitude.ToString("0.0000000", CultureInfo.InvariantCulture) + " \u00b0");
            SetText("KpiLon", s.Longitude.ToString("0.0000000", CultureInfo.InvariantCulture) + " \u00b0");
        }
        else
        {
            SetText("KpiLat", "--");
            SetText("KpiLon", "--");
        }

        SetText("KpiEast",  s.PivotEasting.ToString("0.00", CultureInfo.InvariantCulture)  + " m");
        SetText("KpiNorth", s.PivotNorthing.ToString("0.00", CultureInfo.InvariantCulture) + " m");

        // ---- Equipo -----------------------------------------------------
        string veh = string.Join(" ",
            (s.VehicleBrand ?? string.Empty).Trim(),
            (s.VehicleType  ?? string.Empty).Trim()).Trim();
        SetText("DetVehiculo", string.IsNullOrEmpty(veh) ? "--" : veh);

        SetText("DetAncho", s.ToolWidth > 0
            ? s.ToolWidth.ToString("0.00", CultureInfo.InvariantCulture) + " m"
            : "-- m");

        int onCount = 0;
        if (s.SectionOnRequest != null)
        {
            for (int i = 0; i < s.SectionOnRequest.Length; i++)
                if (s.SectionOnRequest[i]) onCount++;
        }
        int total = s.NumSections > 0 ? s.NumSections
                                       : (s.SectionOnRequest?.Length ?? 0);
        SetText("DetSec", total > 0
            ? (onCount + " activas de " + total)
            : "--");

        SetText("DetJob", s.IsJobStarted ? "Si" : "No");
    }

    private void SetText(string name, string value)
    {
        var t = this.FindControl<TextBlock>(name);
        if (t != null) t.Text = value;
    }
}
