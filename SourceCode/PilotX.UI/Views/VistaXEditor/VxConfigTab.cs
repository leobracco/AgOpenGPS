// ============================================================================
// VxConfigTab.cs — pantalla "Config" del editor nativo de VistaX.
//
// QUÉ QUEDÓ NATIVO: loadConfig/paintConfig/readConfigFromForm/saveConfig de
// vistax.js — archivos, comportamiento del monitoreo y la descarga del ZIP de
// sesiones del lote.
// QUÉ SIGUE EN HTML: vistax.js, intacto, para la PWA del celular.
//
// Los campos MQTT (broker, puerto, credenciales, TLS, tópicos) NO se muestran:
// la conexión con los nodos la gestiona CoreX con su broker embebido. Viajan
// igual en el PUT porque el DTO se guarda entero (merge), así un guardado desde
// acá no los borra.
//
// La descarga del ZIP en cabina no es una descarga de browser: el archivo se
// guarda en Documentos\AgOpenGPS\Exportes y el aviso dice la ruta.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.VistaXEditor;

public sealed class VxConfigTab : VxTab
{
    private TextBox?   _txtPath;
    private TextBox?   _txtUiMs;
    private TextBox?   _txtTimeout;
    private CheckBox?  _chkLogField;
    private ComboBox?  _cboMetodo;
    private TextBox?   _txtUmbral;
    private TextBox?   _txtTConf;
    private CheckBox?  _chkMuted;
    private TextBox?   _txtDrive;
    private TextBlock? _estado;
    private Border?    _chipError;
    private TextBlock? _chipErrorTexto;
    private Button?    _btnZip;

    /// <summary>Mismo orden que el <select> del HTML: el índice mapea al valor
    /// del wire, que NO se traduce.</summary>
    private static readonly string[] METODOS = { "sensores", "pintando", "manual", "velocidad" };

    public VxConfigTab(VxCtx c) : base(c) { }

    public override async Task AlEntrarAsync()
    {
        // Lazy-load: el GET recién al entrar a la pantalla (igual que el HTML).
        if (C.Cfg == null) await CargarAsync().ConfigureAwait(true);
        else Rebuild();
    }

    private async Task CargarAsync()
    {
        var cfg = await C.Client.GetConfigAsync().ConfigureAwait(true);
        if (cfg != null) C.Cfg = cfg;
        Rebuild();
        VxUi.SetEstado(_estado, cfg != null ? "Cargada" : "No se pudo cargar la configuración",
                       cfg != null ? "" : "err");
    }

    public override void Rebuild()
    {
        Children.Clear();
        var c = C.Cfg ?? new VxConfig();

        Children.Add(VxUi.Sub(
            "La conexión MQTT con los nodos VistaX la gestiona CoreX (broker embebido) "
            + "y no se configura desde acá."));

        // ---- Archivos ------------------------------------------------------
        _txtPath = VxUi.Entrada(C.Client, c.ImplementoJsonPath ?? "", false, "Implemento JSON", 420);
        var archivos = new StackPanel { Spacing = 8 };
        archivos.Children.Add(VxUi.Titulo("Archivos"));
        archivos.Children.Add(VxUi.Campo("Implemento JSON", _txtPath, 420));
        Children.Add(VxUi.Card(archivos));

        // ---- Comportamiento ------------------------------------------------
        _txtUiMs     = VxUi.Entrada(C.Client, c.UiUpdateIntervalMs.ToString(), true, "UI update (ms)", 130);
        _txtTimeout  = VxUi.Entrada(C.Client, c.SensorTimeoutMs.ToString(), true, "Timeout sensor (ms)", 130);
        _txtUmbral   = VxUi.Entrada(C.Client, c.UmbralSensoresActivos.ToString(), true, "Umbral sensores activos", 130);
        _txtTConf    = VxUi.Entrada(C.Client, c.TiempoConfirmacionMs.ToString(), true, "Tiempo confirmación (ms)", 130);
        _txtDrive    = VxUi.Entrada(C.Client, c.LogOutputDrive ?? "", false, "Drive de logs", 200);
        _chkLogField = VxUi.Check("Log a Field Record", c.LogToFieldRecord);
        _chkMuted    = VxUi.Check("Alarmas silenciadas", c.AlarmMuted);

        _cboMetodo = VxUi.Combo(320);
        _cboMetodo.ItemsSource = new List<string>
        {
            PilotX.Cockpit.Bars.Traductor.T("Caída de semilla (umbral N sensores)"),
            PilotX.Cockpit.Bars.Traductor.T("Pintado de secciones PilotX"),
            PilotX.Cockpit.Bars.Traductor.T("Manual"),
            PilotX.Cockpit.Bars.Traductor.T("Por velocidad"),
        };
        int mi = Array.IndexOf(METODOS, c.MetodoInicio ?? "sensores");
        _cboMetodo.SelectedIndex = mi < 0 ? 0 : mi;

        var comportamiento = new StackPanel { Spacing = 8 };
        comportamiento.Children.Add(VxUi.Titulo("Comportamiento"));
        var campos = VxUi.Grilla();
        campos.Children.Add(VxUi.Campo("UI update (ms)", _txtUiMs, 130));
        campos.Children.Add(VxUi.Campo("Timeout sensor (ms)", _txtTimeout, 130));
        campos.Children.Add(VxUi.Campo("Umbral sensores activos", _txtUmbral, 130));
        campos.Children.Add(VxUi.Campo("Tiempo confirmación (ms)", _txtTConf, 130));
        campos.Children.Add(VxUi.Campo("Método inicio monitoreo", _cboMetodo, 320));
        campos.Children.Add(VxUi.Campo("Drive de logs", _txtDrive, 200));
        comportamiento.Children.Add(campos);

        var checks = VxUi.Grilla();
        _chkLogField.Margin = new Thickness(0, 0, 16, 0);
        checks.Children.Add(_chkLogField);
        checks.Children.Add(_chkMuted);
        comportamiento.Children.Add(checks);

        comportamiento.Children.Add(VxUi.Sub(
            "Caída de semilla: arranca cuando N sensores detectan flujo + velocidad ≥ 1 km/h. "
            + "Pintado de secciones: arranca cuando PilotX abre al menos una sección + "
            + "velocidad ≥ 1 km/h. Si con el monitoreo activo no cae semilla, se dispara la "
            + "alarma de flujo. Surcos cuya sección PilotX esté apagada quedan grises (no "
            + "sensan ni alarman) hasta que la sección vuelva."));
        Children.Add(VxUi.Card(comportamiento));

        // ---- Botonera ------------------------------------------------------
        _estado = VxUi.Estado();
        _chipErrorTexto = new TextBlock
        {
            Text = "", Foreground = VxUi.Err, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
        };
        _chipError = new Border
        {
            Background = VxUi.BgError, BorderBrush = VxUi.Err, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
            IsVisible = false, Child = _chipErrorTexto,
        };
        var botones = VxUi.Fila();
        botones.Children.Add(VxUi.Boton("Guardar config", () => _ = GuardarAsync(), primario: true));
        botones.Children.Add(VxUi.Boton("Recargar", () => _ = CargarAsync()));
        botones.Children.Add(_estado);
        Children.Add(botones);
        Children.Add(_chipError);

        // ---- Datos del lote ------------------------------------------------
        var lote = new StackPanel { Spacing = 8 };
        lote.Children.Add(VxUi.Titulo("Datos del lote"));
        lote.Children.Add(VxUi.Sub(
            "NDJSON + shapefiles de puntos y heatmap del lote abierto. Se guarda en "
            + "Documentos\\AgOpenGPS\\Exportes."));
        _btnZip = VxUi.Boton("⬇ Descargar sesiones del lote (ZIP)", () => _ = DescargarZipAsync());
        lote.Children.Add(_btnZip);
        Children.Add(VxUi.Card(lote));

        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    /// <summary>Merge: se parte del DTO que vino del GET y se pisan SOLO los
    /// campos visibles. Los tópicos/credenciales MQTT que la UI no muestra se
    /// preservan.</summary>
    private async Task GuardarAsync()
    {
        var c = C.Cfg ?? new VxConfig();
        if (_chipError != null) _chipError.IsVisible = false;
        VxUi.SetEstado(_estado, "Guardando…", "");

        c.ImplementoJsonPath    = _txtPath?.Text ?? "";
        c.UiUpdateIntervalMs    = VxUi.LeerInt(_txtUiMs, 500);
        c.SensorTimeoutMs       = VxUi.LeerInt(_txtTimeout, 3000);
        c.LogToFieldRecord      = _chkLogField?.IsChecked == true;
        c.MetodoInicio          = METODOS[Math.Max(0, Math.Min(METODOS.Length - 1,
                                                              _cboMetodo?.SelectedIndex ?? 0))];
        c.UmbralSensoresActivos = VxUi.LeerInt(_txtUmbral, 3);
        c.TiempoConfirmacionMs  = VxUi.LeerInt(_txtTConf, 500);
        c.AlarmMuted            = _chkMuted?.IsChecked == true;
        c.LogOutputDrive        = _txtDrive?.Text ?? "";
        C.Cfg = c;

        var r = await C.Client.PutConfigAsync(c).ConfigureAwait(true);
        if (!r.Ok)
        {
            if (!string.IsNullOrEmpty(r.Error) && _chipError != null && _chipErrorTexto != null)
            {
                _chipErrorTexto.Text = r.Texto();
                _chipError.IsVisible = true;
                VxUi.SetEstado(_estado, "", "");
            }
            else
            {
                VxUi.SetEstado(_estado, "Error al guardar: " + r.Texto(), "err");
            }
            return;
        }
        VxUi.SetEstado(_estado, "Guardada ✓", "ok");
    }

    private async Task DescargarZipAsync()
    {
        if (_btnZip != null) _btnZip.IsEnabled = false;
        VxUi.SetEstado(_estado, "Preparando el ZIP del lote…", "");
        var r = await C.Client.DescargarZipLoteAsync().ConfigureAwait(true);
        if (_btnZip != null) _btnZip.IsEnabled = true;
        if (!r.Ok)
        {
            VxUi.SetEstado(_estado, "", "");
            C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo descargar el ZIP")
                            + ": " + r.Error);
            return;
        }
        VxUi.SetEstado(_estado, "", "");
        C.Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("ZIP guardado en") + " " + r.Ruta);
    }
}
