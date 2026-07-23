// FieldDataPanel.axaml.cs
//
// Reemplazo nativo de pages/datos-lote.html. Consume HudSnapshot y
// computa todos los KPIs derivados (repintado, ahorro insumo, ahorro $).
// Sin red, sin JS, sin HTML — solo controles Avalonia + Skia.
//
// API: OnSnapshot(HudSnapshot) — invocado por MainWindow desde el
// HudPoller (Dispatcher.UIThread.Post ya garantizado por el caller).
//
// Persistencia de dosis/precio: estaticos en proceso por ahora. Cuando
// QuantiX/FlowX expongan endpoints para auto-cargar, se enchufa aca.

using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class FieldDataPanel : UserControl
{
    // Modo de dosis: "kg" (semilla/fertilizante) o "l" (liquido).
    // Permite al operario cambiar las unidades sin reiniciar la app.
    private string _doseMode = "kg";
    private double _dose      = 0.0;
    private double _price     = 0.0;

    // Cache del ultimo snapshot para recomputar ahorros cuando cambia
    // la dosis/precio/modo sin tener que esperar al proximo HUD push.
    private HudSnapshot? _last;

    // Brushes para la pill de estado (mismo criterio que el HUD chip).
    private static readonly IBrush _brushOk   = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _brushWarn = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush _brushErr  = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush _brushDim  = new SolidColorBrush(Color.Parse("#8FA092"));
    private static readonly IBrush _activeBg  = new SolidColorBrush(Color.Parse("#262C28"));
    private static readonly IBrush _activeBd  = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush _idleBd    = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush _textHi    = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush _textMid   = new SolidColorBrush(Color.Parse("#C5CFC5"));

    public FieldDataPanel()
    {
        InitializeComponent();
        SyncDoseToggleUi();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Recibe un snapshot del HUD y refresca toda la vista. Asume que
    /// el caller hizo marshalling al UI thread (MainWindow lo hace via
    /// Dispatcher.UIThread.Post).
    /// </summary>
    public void OnSnapshot(HudSnapshot s)
    {
        _last = s;
        RenderAll();
    }

    private void RenderAll()
    {
        var s = _last;
        if (s == null) return;

        // ---- Estado pill ------------------------------------------------
        bool hasGps = s.Latitude != 0 || s.Longitude != 0;
        var pill = this.FindControl<Border>("EstadoPill");
        var dot  = this.FindControl<Ellipse>("EstadoDot");
        var txt  = this.FindControl<TextBlock>("EstadoText");
        if (dot != null && txt != null)
        {
            if (s.IsJobStarted && hasGps) { dot.Fill = _brushOk;   txt.Text = "Trabajo activo"; }
            else if (s.IsJobStarted)      { dot.Fill = _brushErr;  txt.Text = "Sin GPS fix"; }
            else                          { dot.Fill = _brushWarn; txt.Text = "Sin trabajo"; }
        }

        // ---- KPIs -------------------------------------------------------
        // En HudSnapshot:
        //   WorkedAreaTotalM2     = todo lo pintado (incluye solapamientos)
        //   ActualAreaCoveredM2   = pintado descontando solapamientos
        // overlap = total - neto (clamp a >=0 por jitter floating).
        double netoHa    = s.ActualAreaCoveredM2 / 10000.0;
        double totalHa   = s.WorkedAreaTotalM2  / 10000.0;
        double overlapHa = Math.Max(0.0, totalHa - netoHa);
        double overlapPct = totalHa > 0.001 ? (overlapHa / totalHa) * 100.0 : 0.0;

        SetText("KpiAreaNeta",    FmtHa(netoHa));
        SetText("KpiAreaTotal",   FmtHa(totalHa));
        SetText("KpiAreaOverlap", FmtHa(overlapHa));
        SetText("KpiOverlapPct",  totalHa > 0.001
            ? overlapPct.ToString("0.0", CultureInfo.InvariantCulture) + " %"
            : "-- %");

        SetText("KpiVel", s.AvgSpeed.ToString("0.0", CultureInfo.InvariantCulture));

        // Secciones activas: cuenta true en SectionOnRequest.
        int onCount = 0;
        if (s.SectionOnRequest != null)
        {
            for (int i = 0; i < s.SectionOnRequest.Length; i++)
                if (s.SectionOnRequest[i]) onCount++;
        }
        int total = s.NumSections > 0 ? s.NumSections
                                       : (s.SectionOnRequest?.Length ?? 0);
        SetText("KpiSec", total > 0
            ? (onCount + " de " + total)
            : "-- de --");

        // ---- Ahorro -----------------------------------------------------
        // saved_insumo = overlap_ha * dose (kg o L)
        // saved_money  = saved_insumo * price
        double savedInsumo = overlapHa * _dose;
        double savedMoney  = savedInsumo * _price;

        SetText("KpiSavedInsumo", _dose > 0 ? FmtAmount(savedInsumo) : "--");
        SetText("KpiSavedMoney",  (_dose > 0 && _price > 0) ? FmtAmount(savedMoney) : "--");
        SetText("KpiSavedHa",     overlapHa > 0 ? FmtHa(overlapHa) : "--");

        // ---- Detalles --------------------------------------------------
        SetText("DetLote", string.IsNullOrEmpty(s.CurrentFieldDirectory)
            ? "--"
            : System.IO.Path.GetFileName(s.CurrentFieldDirectory.TrimEnd('/', '\\')));
        SetText("DetAncho", s.ToolWidth > 0
            ? s.ToolWidth.ToString("0.00", CultureInfo.InvariantCulture) + " m"
            : "-- m");
        string veh = string.Join(" ",
            (s.VehicleBrand ?? string.Empty).Trim(),
            (s.VehicleType  ?? string.Empty).Trim()).Trim();
        SetText("DetVehiculo", string.IsNullOrEmpty(veh) ? "--" : veh);
        // detTrack: el snapshot no lo expone hoy. Lo dejo placeholder.
        SetText("DetTrack", s.IsJobStarted ? "AB activa" : "Ninguna");
        // Dosis del shapefile en el punto actual:
        if (s.ShapeIsInside)
        {
            SetText("DetShape", s.ShapeCurrentDose.ToString("0.0", CultureInfo.InvariantCulture)
                + " " + (_doseMode == "l" ? "L/ha" : "kg/ha"));
        }
        else
        {
            SetText("DetShape", "fuera de zona");
        }
    }

    private static string FmtHa(double ha)
    {
        if (ha >= 100) return ha.ToString("0", CultureInfo.InvariantCulture);
        return ha.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FmtAmount(double v)
    {
        if (v >= 1000) return v.ToString("0", CultureInfo.InvariantCulture);
        if (v >= 10)   return v.ToString("0.0", CultureInfo.InvariantCulture);
        return v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void SetText(string name, string value)
    {
        var t = this.FindControl<TextBlock>(name);
        if (t != null) t.Text = value;
    }

    // ---------- Toggle kg / L ------------------------------------------

    private void OnDoseKgClick(object? sender, RoutedEventArgs e)
    {
        if (_doseMode == "kg") return;
        _doseMode = "kg";
        SyncDoseToggleUi();
        RenderAll();
    }

    private void OnDoseLClick(object? sender, RoutedEventArgs e)
    {
        if (_doseMode == "l") return;
        _doseMode = "l";
        SyncDoseToggleUi();
        RenderAll();
    }

    private void SyncDoseToggleUi()
    {
        var btnKg = this.FindControl<Button>("BtnDoseKg");
        var btnL  = this.FindControl<Button>("BtnDoseL");
        var doseUnit  = this.FindControl<TextBlock>("DoseUnit");
        var priceUnit = this.FindControl<TextBlock>("PriceUnit");
        var savedUnit = this.FindControl<TextBlock>("KpiSavedUnit");

        bool kg = _doseMode == "kg";
        if (btnKg != null)
        {
            btnKg.Background  = kg ? _activeBg : _activeBg;
            btnKg.BorderBrush = kg ? _activeBd : _idleBd;
            btnKg.Foreground  = kg ? _textHi   : _textMid;
        }
        if (btnL != null)
        {
            btnL.Background  = !kg ? _activeBg : _activeBg;
            btnL.BorderBrush = !kg ? _activeBd : _idleBd;
            btnL.Foreground  = !kg ? _textHi   : _textMid;
        }
        if (doseUnit  != null) doseUnit.Text  = kg ? "kg/ha"  : "L/ha";
        if (priceUnit != null) priceUnit.Text = kg ? "USD/kg" : "USD/L";
        if (savedUnit != null) savedUnit.Text = kg ? "kg"     : "L";
    }

    // ---------- Inputs editables ---------------------------------------

    private void OnDoseChanged(object? sender, TextChangedEventArgs e)
    {
        var tb = sender as TextBox;
        if (tb == null) return;
        if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            _dose = Math.Max(0.0, v);
        }
        else
        {
            _dose = 0.0;
        }
        RenderAll();
    }

    private void OnPriceChanged(object? sender, TextChangedEventArgs e)
    {
        var tb = sender as TextBox;
        if (tb == null) return;
        if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            _price = Math.Max(0.0, v);
        }
        else
        {
            _price = 0.0;
        }
        RenderAll();
    }
}
