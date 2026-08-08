// ============================================================================
// DireccionPanel.cs — panel de DIRECCIÓN nativo (17vo port), CHICO, para la
// tablet de 10". Reemplaza el uso diario de la ventana HTML "Dirección —
// Autoguiado" (1040px de WebView): lo que el operario toca en cabina son
// cuatro cosas — probar el motor, el sensor de ángulo, la fuerza y guardar.
// Todo lo demás (PP/Stanley/avanzado/velocidades/barra guía) sigue en la
// página HTML completa, detrás del botón "Todo…".
//
// Patrón GuiasPanel/LotePanel: Border flotante sobre el mapa VIVO, paleta
// clara PilotX, HTTP contra los MISMOS endpoints que usa la página:
//   GET/POST /api/steer/config      (objeto completo — el POST NO es merge:
//                                    siempre se reenvía el JSON entero leído,
//                                    con solo los campos tocados cambiados)
//   POST     /api/steer/zero-was
//   GET/POST /api/steer/freedrive[/angle|/zero]
//   GET      /api/aog/graph-steer   (Objetivo/Actual en vivo, 2 Hz)
//
// Seguridad: cerrar el panel (✕, u otro panel que lo pise) APAGA el manejo
// libre si quedó prendido — nunca queda el volante bajo control manual sin
// nadie mirándolo (misma doctrina que la página HTML).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PilotX.Desktop.Views;

public sealed class DireccionPanel : Border
{
    // ---- paleta PilotX (misma que GuiasPanel/LotePanel) --------------------
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgCard     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgSel      = new SolidColorBrush(Color.Parse("#DCEFD8"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush Ambar      = new SolidColorBrush(Color.Parse("#B9802E"));

    private HttpClient? _http;
    private string _base = "";

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;

    // ---- estado -------------------------------------------------------------
    private JsonObject? _cfg;           // config completa del GET (se reenvía entera)
    private bool _sucio;
    private bool _fdOn;                 // manejo libre prendido (dice el motor)
    private DispatcherTimer? _timer;    // 500 ms: graph-steer siempre + freedrive si Probar

    // header en vivo
    private readonly TextBlock _liveObj = Num("—");
    private readonly TextBlock _liveAct = Num("—");
    private readonly TextBlock _liveErr = Num("—");

    // pantallas
    private readonly StackPanel _scProbar;
    private readonly StackPanel _scSensor;
    private readonly StackPanel _scFuerza;
    private readonly StackPanel _scGuiado;
    private readonly StackPanel _scModulo;
    private readonly StackPanel _scPantalla;
    private readonly ScrollViewer _scAyuda;
    private readonly Dictionary<string, Button> _tabs = new();

    // Refresco de las filas construidas con los helpers genéricos: cada fila
    // registra cómo repintarse desde _cfg (PintarConfig las corre todas).
    private readonly List<Action> _refrescos = new();

    // Probar
    private readonly Button _fdPower;
    private readonly Button _fdIzq;
    private readonly Button _fdDer;
    private readonly Button _fdCero;
    private readonly TextBlock _fdAngulo = new()
    {
        Text = "0°", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Texto,
        MinWidth = 86, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _fdNota;

    // Sensor
    private readonly Button _segRty;
    private readonly Button _segEncoder;
    private readonly TextBlock _valCuentas = Num("—");
    private readonly Button _tglInvWas;
    private readonly Button _tglInvMotor;

    // Fuerza
    private readonly TextBlock _valPwmMin  = Num("—");
    private readonly TextBlock _valPwmAlto = Num("—");
    private readonly TextBlock _valGanP    = Num("—");

    // pie
    private readonly Button _btnGuardar;
    private readonly TextBlock _estado;

    public DireccionPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        Width = 560;
        IsVisible = false;

        // ---------- header: título + Obj/Act/Err + Todo… + ✕ ----------
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(4, 0, 0, 8) };
        var titulo = new TextBlock
        {
            Text = "Dirección", FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titulo, 0);

        var live = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        live.Children.Add(LiveCelda("OBJ", _liveObj));
        live.Children.Add(LiveCelda("ACT", _liveAct));
        live.Children.Add(LiveCelda("ERR", _liveErr));
        Grid.SetColumn(live, 1);

        var btnAyuda = BotonChico("?");
        btnAyuda.Width = 44;
        btnAyuda.FontSize = 16;
        btnAyuda.Click += (_, _) => ToggleAyuda();
        Grid.SetColumn(btnAyuda, 2);
        btnAyuda.Margin = new Thickness(0, 0, 6, 0);

        var btnCerrar = BotonChico("✕");
        btnCerrar.Width = 44;
        btnCerrar.Click += (_, _) => Cerrar();
        Grid.SetColumn(btnCerrar, 3);

        header.Children.Add(titulo);
        header.Children.Add(live);
        header.Children.Add(btnAyuda);
        header.Children.Add(btnCerrar);

        // ---------- tabs (2 filas de 3 — TODO el FormSteer vive acá) ----------
        var tabs = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (id, txt) in new[]
        {
            ("probar", "Probar"), ("sensor", "Sensor"), ("fuerza", "Fuerza"),
            ("guiado", "Guiado"), ("modulo", "Módulo"), ("pantalla", "Pantalla"),
        })
        {
            var b = new Button
            {
                Content = txt, Height = 50, FontSize = 14, FontWeight = FontWeight.SemiBold,
                Background = BgCard, Foreground = TextoMuted,
                BorderBrush = Borde, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            string mio = id;
            b.Click += (_, _) => MostrarTab(mio);
            _tabs[id] = b;
            tabs.Children.Add(b);
        }

        // ---------- pantalla PROBAR ----------
        _fdPower = new Button
        {
            Content = "Prender", Height = 58, MinWidth = 108, FontSize = 14, FontWeight = FontWeight.Bold,
            Background = BgCard, Foreground = Texto, BorderBrush = Borde, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10), HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _fdPower.Click += async (_, _) => await FdPost("/api/steer/freedrive", "{\"on\":" + (!_fdOn ? "true" : "false") + "}");

        _fdIzq  = BotonFd("−1°");
        _fdDer  = BotonFd("+1°");
        _fdCero = BotonFd("0↔5°");
        _fdCero.MinWidth = 84;
        _fdIzq.Click  += async (_, _) => await FdPost("/api/steer/freedrive/angle", "{\"dir\":-1}");
        _fdDer.Click  += async (_, _) => await FdPost("/api/steer/freedrive/angle", "{\"dir\":1}");
        _fdCero.Click += async (_, _) => await FdPost("/api/steer/freedrive/zero", "{}");

        var fdFila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fdFila.Children.Add(_fdPower);
        fdFila.Children.Add(_fdIzq);
        fdFila.Children.Add(_fdAngulo);
        fdFila.Children.Add(_fdDer);
        fdFila.Children.Add(_fdCero);

        _fdNota = new TextBlock
        {
            Text = "Con el tractor parado. Se apaga solo si el tractor arranca.",
            FontSize = 12.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 8, 0, 0),
        };

        _scProbar = new StackPanel { Spacing = 4 };
        _scProbar.Children.Add(SubTitulo("Manejo libre — flecha derecha = ruedas a la derecha"));
        _scProbar.Children.Add(fdFila);
        _scProbar.Children.Add(_fdNota);

        // ---------- pantalla SENSOR ----------
        _segRty     = BotonSeg("Sensor RTY (simple)");
        _segEncoder = BotonSeg("Encoder del motor");
        _segRty.Click     += (_, _) => { SetSeg(rty: true);  MarcarSucio(); };
        _segEncoder.Click += (_, _) => { SetSeg(rty: false); MarcarSucio(); };
        var segFila = new UniformGrid { Columns = 2 };
        segFila.Children.Add(Envolver(_segRty, 0, 4));
        segFila.Children.Add(Envolver(_segEncoder, 4, 0));

        var filaCuentas = FilaNumerica("Cuentas por grado", _valCuentas,
            () => Nudge("counts_per_degree", -1, 1, 255),
            () => Nudge("counts_per_degree", +1, 1, 255));

        var btnCero = new Button
        {
            Content = "⭕ Poner el sensor de ángulo en cero (ruedas derechas)",
            Height = 52, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
            Background = new SolidColorBrush(Color.Parse("#EAF6E8")),
            Foreground = new SolidColorBrush(Color.Parse("#1C5E18")),
            BorderBrush = Verde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        btnCero.Click += async (_, _) => await ZeroWas();

        _tglInvWas   = BotonSeg("Invertir sensor (WAS)");
        _tglInvMotor = BotonSeg("Invertir motor");
        _tglInvWas.Click   += (_, _) => { ToggleBool("invert_was", _tglInvWas); };
        _tglInvMotor.Click += (_, _) => { ToggleBool("invert_steer", _tglInvMotor); };
        var invFila = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
        invFila.Children.Add(Envolver(_tglInvWas, 0, 4));
        invFila.Children.Add(Envolver(_tglInvMotor, 4, 0));

        _scSensor = new StackPanel { Spacing = 4, IsVisible = false };
        _scSensor.Children.Add(SubTitulo("Qué sensor mide el ángulo de las ruedas"));
        _scSensor.Children.Add(segFila);
        _scSensor.Children.Add(filaCuentas);
        _scSensor.Children.Add(btnCero);
        _scSensor.Children.Add(invFila);

        // ---------- pantalla FUERZA ----------
        _scFuerza = new StackPanel { Spacing = 4, IsVisible = false };
        _scFuerza.Children.Add(SubTitulo("Fuerza del motor de dirección"));
        _scFuerza.Children.Add(FilaNumerica("Mínima para mover (PWM mín)", _valPwmMin,
            () => Nudge("min_pwm", -1, 0, 255), () => Nudge("min_pwm", +1, 0, 255)));
        _scFuerza.Children.Add(FilaNumerica("Máxima (PWM alto)", _valPwmAlto,
            () => Nudge("high_steer_pwm", -5, 20, 255), () => Nudge("high_steer_pwm", +5, 20, 255)));
        _scFuerza.Children.Add(FilaNumerica("Fuerza de corrección (Ganancia P)", _valGanP,
            () => Nudge("proportional_gain", -5, 0, 200), () => Nudge("proportional_gain", +5, 0, 200)));

        // ---------- pantalla GUIADO (PP + Stanley + general + avanzado) ----------
        // El wire guarda los ENTEROS crudos del slider original; la escala es
        // solo de display (hold_look_ahead=29 → 2,9 s). Igual que la página.
        _scGuiado = new StackPanel { Spacing = 2, IsVisible = false };
        _scGuiado.Children.Add(SubTitulo("Modo suave (Pure Pursuit)"));
        _scGuiado.Children.Add(FilaAjuste("Qué tan adelante mira", "hold_look_ahead", 10, 70, 0.1, 1, "s"));
        _scGuiado.Children.Add(FilaAjuste("Multiplicador por velocidad", "look_ahead_mult", 5, 60, 0.1, 1));
        _scGuiado.Children.Add(FilaAjuste("Entrada a la línea", "acquire_factor", 20, 300, 0.01, 2));
        _scGuiado.Children.Add(FilaAjuste("Integral (PP)", "integral_pp", 0, 100));
        _scGuiado.Children.Add(SubTituloSep("Modo firme (Stanley)"));
        _scGuiado.Children.Add(FilaAjuste("Ganancia Stanley", "stanley_gain", 1, 40, 0.1, 1));
        _scGuiado.Children.Add(FilaAjuste("Ganancia de rumbo", "heading_error_gain", 1, 15, 0.1, 1));
        _scGuiado.Children.Add(FilaAjuste("Integral (Stanley)", "integral_stanley", 0, 100));
        _scGuiado.Children.Add(FilaToggle("Usar siempre Stanley (puro)", "stanley_pure"));
        _scGuiado.Children.Add(SubTituloSep("General"));
        _scGuiado.Children.Add(FilaAjuste("Ángulo máximo de giro", "max_steer_angle", 10, 80, 1, 0, "°"));
        _scGuiado.Children.Add(FilaAjuste("Ackerman", "ackerman", 1, 200, 1, 0, "%"));
        _scGuiado.Children.Add(FilaToggle("Guiar en marcha atrás", "steer_in_reverse"));
        _scGuiado.Children.Add(SubTituloSep("Avanzado"));
        _scGuiado.Children.Add(FilaAjusteD("Zona muerta de rumbo", "dead_zone_heading", 0.1, 0, 5, 1, "°"));
        _scGuiado.Children.Add(FilaAjuste("Demora de zona muerta", "dead_zone_delay", 1, 50));
        _scGuiado.Children.Add(FilaAjuste("Compensación en cabecera (U)", "u_turn_comp", 2, 20));
        _scGuiado.Children.Add(FilaAjuste("Compensación de ladera", "side_hill_comp", 0, 30));

        // ---------- pantalla MÓDULO (placa + corte por volante) ----------
        _scModulo = new StackPanel { Spacing = 2, IsVisible = false };
        _scModulo.Children.Add(SubTitulo("Placa de dirección"));
        _scModulo.Children.Add(FilaSeg("Driver del motor", "motor_drive",
            new[] { ("Cytron", "Cytron"), ("IBT2", "IBT2") }));
        _scModulo.Children.Add(FilaSeg("Activación del piloto", "steer_enable",
            new[] { ("None", "Ninguno"), ("Switch", "Interruptor"), ("Button", "Botón") }));
        _scModulo.Children.Add(FilaSeg("Eje del IMU", "imu_axis",
            new[] { ("X", "X"), ("Y", "Y") }));
        _scModulo.Children.Add(FilaToggle("Válvula Danfoss", "danfoss"));
        _scModulo.Children.Add(FilaToggle("Invertir relés", "invert_relays"));
        _scModulo.Children.Add(SubTituloSep("Corte al agarrar el volante (uno solo)"));
        _scModulo.Children.Add(FilaSensoresCorte());
        _scModulo.Children.Add(FilaAjuste("Cuentas máximas (encoder)", "max_counts", 1, 255));
        _scModulo.Children.Add(FilaAjuste("Límite presión/corriente", "sensor_limit", 0, 255, 100.0 / 255, 0, "%"));

        // ---------- pantalla PANTALLA (velocidades + barra guía) ----------
        _scPantalla = new StackPanel { Spacing = 2, IsVisible = false };
        _scPantalla.Children.Add(SubTitulo("Velocidades de guiado"));
        _scPantalla.Children.Add(FilaAjusteD("Velocidad mínima", "min_steer_speed", 0.5, 0, 10, 1, "km/h"));
        _scPantalla.Children.Add(FilaAjusteD("Velocidad máxima", "max_steer_speed", 1, 1, 40, 0, "km/h"));
        _scPantalla.Children.Add(FilaAjusteD("Límite de funciones de guiado", "guidance_speed_limit", 1, 1, 40, 0, "km/h"));
        _scPantalla.Children.Add(SubTituloSep("Barra de guiado"));
        _scPantalla.Children.Add(FilaSeg("Tipo de barra", "guidance_bar",
            new[] { ("lightbar", "Lightbar"), ("steerbar", "Steer Bar") }));
        _scPantalla.Children.Add(FilaToggle("Mostrar barra en pantalla", "display_lightbar"));
        _scPantalla.Children.Add(FilaAjuste("Grosor de línea", "line_width", 1, 8, 1, 0, "px"));
        _scPantalla.Children.Add(FilaAjusteD("Distancia de enganche", "snap_distance", 1, 1, 100, 0));
        _scPantalla.Children.Add(FilaAjusteD("Mirada de la barra", "guidance_look_ahead", 0.1, 0.1, 5, 1, "s"));
        _scPantalla.Children.Add(FilaAjuste("Sensibilidad de la barra", "cm_per_pixel", 2, 20, 1, 0, "cm/px"));

        // ---------- pantalla AYUDA (botón "?" del header) ----------
        // Ayuda en la MISMA card (nada de Flyouts: no se dibujan sobre el
        // mapa GL). Una línea por botón, en criollo.
        var ayudaLista = new StackPanel { Spacing = 6 };
        foreach (var (term, desc) in new[]
        {
            ("OBJ / ACT / ERR", "El ángulo pedido, el que mide el sensor y la diferencia. En vivo."),
            ("Prender (Probar)", "Manejo libre: mover las ruedas sin guía, con el tractor parado. Se apaga solo si el tractor arranca."),
            ("−1° / +1°", "Suma o resta un grado al objetivo. El motor va hasta ahí y FRENA."),
            ("0↔5°", "Alterna el objetivo entre 0° y 5° — sirve para ver la respuesta de un salto."),
            ("Sensor RTY / Encoder del motor", "Cuál sensor mide el ángulo de las ruedas: el RTY en el eje, o el encoder interno del motor Keya. Es uno o el otro."),
            ("Cuentas por grado", "La escala del sensor: cuántas cuentas equivalen a 1° de rueda. RTY: 59. Encoder: se mide tope a tope."),
            ("Poner en cero", "Con las ruedas bien derechas, le dice al sistema \"esto es 0°\"."),
            ("Invertir sensor (WAS)", "Si girás a la derecha y el ángulo va para la izquierda, prendé esto. Con encoder también apaga la corrección por GPS (para el banco)."),
            ("Invertir motor", "Si la flecha derecha mueve las ruedas a la izquierda, prendé esto."),
            ("PWM mín", "La fuerza mínima para que el motor arranque sin quedarse clavado. Subir de a poco."),
            ("PWM alto", "El tope de fuerza del motor. Moderado hasta probar en el lote."),
            ("Ganancia P", "Qué tan fuerte corrige el error de ángulo. Mucho = nervioso, poco = vago."),
            ("Guardar", "Manda TODO al módulo de dirección y queda grabado."),
            ("Guiado", "Cómo sigue la línea: modo suave (PP), modo firme (Stanley), ángulo máximo, Ackerman y ajustes finos."),
            ("Módulo", "La placa: driver del motor, cómo se activa el piloto, eje del IMU, y el corte al agarrar el volante."),
            ("Pantalla", "Velocidades de guiado (mínima/máxima/límite) y la barra de guiado en pantalla."),
        })
        {
            var fila = new StackPanel { Spacing = 1 };
            fila.Children.Add(new TextBlock
            {
                Text = term, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Texto,
            });
            fila.Children.Add(new TextBlock
            {
                Text = desc, FontSize = 12.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            });
            ayudaLista.Children.Add(fila);
        }
        _scAyuda = new ScrollViewer
        {
            Content = ayudaLista, MaxHeight = 340, IsVisible = false,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        // ---------- pie: estado + Guardar ----------
        _estado = new TextBlock
        {
            Text = "", FontSize = 12.5, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _btnGuardar = new Button
        {
            Content = "Sin cambios", Height = 54, MinWidth = 150, FontSize = 14, FontWeight = FontWeight.Bold,
            Background = BgCard, Foreground = Texto, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = false,
        };
        _btnGuardar.Click += async (_, _) => await Guardar();

        var pie = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(_estado, 0);
        Grid.SetColumn(_btnGuardar, 1);
        pie.Children.Add(_estado);
        pie.Children.Add(_btnGuardar);

        // ---------- árbol ----------
        var stage = new Panel();
        stage.Children.Add(_scProbar);
        stage.Children.Add(_scSensor);
        stage.Children.Add(_scFuerza);
        stage.Children.Add(_scGuiado);
        stage.Children.Add(_scModulo);
        stage.Children.Add(_scPantalla);
        stage.Children.Add(_scAyuda);
        // Las pantallas largas (Guiado) scrollean adentro: la card no crece
        // más allá de lo que entra en la tablet de 10".
        var stageScroll = new ScrollViewer
        {
            Content = stage, MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(tabs);
        root.Children.Add(stageScroll);
        root.Children.Add(pie);
        Child = root;

        MostrarTab("probar");
    }

    // ---- ciclo de vida -------------------------------------------------------

    public void Attach(HttpClient http, string baseUrl)
    {
        _http = http;
        _base = (baseUrl ?? "").TrimEnd('/');
    }

    public async void Abrir()
    {
        IsVisible = true;
        MostrarTab("probar");
        _estado.Text = "";
        await CargarConfig();
        await FdRefrescar();
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += async (_, _) => await Latido();
        }
        _timer.Start();
    }

    public void Cerrar()
    {
        // Nunca dejar el volante bajo control manual sin nadie mirándolo.
        if (_fdOn) _ = FdPost("/api/steer/freedrive", "{\"on\":false}");
        _timer?.Stop();
        IsVisible = false;
        Cerrado?.Invoke();
    }

    // ---- HTTP ------------------------------------------------------------------

    private async Task CargarConfig()
    {
        try
        {
            var json = await _http!.GetStringAsync(_base + "/api/steer/config").ConfigureAwait(false);
            var node = JsonNode.Parse(json.TrimStart('﻿')) as JsonObject;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cfg = node;
                _sucio = false;
                PintarConfig();
                PintarGuardar();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _estado.Text = "Sin módulo de dirección: " + ex.Message);
        }
    }

    private async Task Guardar()
    {
        if (_cfg == null || _http == null) return;
        try
        {
            // SIEMPRE el objeto entero: el POST /steer/config NO es merge — un
            // body parcial pone en default todo lo ausente (visto 2026-08-08).
            var copia = (JsonObject)_cfg.DeepClone();
            copia.Remove("ok");
            var body = new StringContent(copia.ToJsonString(), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(_base + "/api/steer/config", body).ConfigureAwait(false);
            bool ok = resp.IsSuccessStatusCode;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _sucio = !ok;
                _estado.Text = ok ? "Guardado y enviado al módulo ✔" : "No se pudo guardar (HTTP " + (int)resp.StatusCode + ")";
                _estado.Foreground = ok ? Verde : Rojo;
                PintarGuardar();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _estado.Text = "Sin conexión: " + ex.Message;
                _estado.Foreground = Rojo;
            });
        }
    }

    private async Task ZeroWas()
    {
        if (_http == null) return;
        try
        {
            var resp = await _http.PostAsync(_base + "/api/steer/zero-was",
                new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _estado.Text = resp.IsSuccessStatusCode ? "Sensor en cero ✔" : "No se pudo poner en cero";
                _estado.Foreground = resp.IsSuccessStatusCode ? Verde : Rojo;
                // el cero cambia was_offset en el módulo: releer para no pisarlo
                await CargarConfig();
            });
        }
        catch { }
    }

    private async Task FdPost(string path, string json)
    {
        if (_http == null) return;
        try
        {
            var resp = await _http.PostAsync(_base + path,
                new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var j = JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
            await Dispatcher.UIThread.InvokeAsync(() => FdRender(j));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _fdNota.Text = "Sin conexión con PilotX: " + ex.Message;
                _fdNota.Foreground = Rojo;
            });
        }
    }

    private async Task FdRefrescar()
    {
        if (_http == null) return;
        try
        {
            var body = await _http.GetStringAsync(_base + "/api/steer/freedrive").ConfigureAwait(false);
            var j = JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
            await Dispatcher.UIThread.InvokeAsync(() => FdRender(j));
        }
        catch { }
    }

    private async Task Latido()
    {
        if (!IsVisible || _http == null) return;
        // Obj/Act/Err del header, siempre (misma fuente que el gráfico).
        // SIN ConfigureAwait(false): el tick del DispatcherTimer corre en el
        // hilo de UI y la continuación tiene que VOLVER ahí — tocar TextBlocks
        // desde el pool mata el proceso entero (crash 2026-08-08, mismo caso
        // que el RequestNextFrameRendering del mapa).
        try
        {
            var body = await _http.GetStringAsync(_base + "/api/aog/graph-steer");
            var j = JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
            double set = j?["set_steer_deg"]?.GetValue<double>() ?? double.NaN;
            double act = j?["actual_steer_deg"]?.GetValue<double>() ?? double.NaN;
            _liveObj.Text = double.IsNaN(set) ? "—" : set.ToString("F1", CultureInfo.InvariantCulture) + "°";
            _liveAct.Text = double.IsNaN(act) ? "—" : act.ToString("F1", CultureInfo.InvariantCulture) + "°";
            _liveErr.Text = (double.IsNaN(set) || double.IsNaN(act)) ? "—"
                : (set - act).ToString("F1", CultureInfo.InvariantCulture) + "°";
        }
        catch { _liveObj.Text = _liveAct.Text = _liveErr.Text = "—"; }

        // Estado del manejo libre mientras está prendido (ve el apagado solo).
        if (_fdOn) await FdRefrescar();
    }

    // ---- render ------------------------------------------------------------------

    private void FdRender(JsonObject? j)
    {
        bool on = j?["on"]?.GetValue<bool>() ?? false;
        double ang = j?["angle"]?.GetValue<double>() ?? 0;
        string? error = j?["error"]?.GetValue<string>();

        bool seApagoSolo = _fdOn && !on && error == null;
        _fdOn = on;
        _fdPower.Content = on ? "Prendido" : "Prender";
        _fdPower.BorderBrush = on ? Verde : Borde;
        _fdPower.Background = on ? BgSel : BgCard;
        _fdIzq.IsEnabled = _fdDer.IsEnabled = _fdCero.IsEnabled = on;
        _fdAngulo.Text = (ang > 0 ? "+" : "") + ang.ToString("F0", CultureInfo.InvariantCulture) + "°";

        string? motivo = error switch
        {
            "velocidad"       => "No se puede prender: el tractor está andando.",
            "sin-velocidad"   => "No se puede prender: PilotX no informa velocidad.",
            "apagado"         => "Prendé el manejo libre antes de mover el ángulo.",
            "service-unavailable" => "Sin módulo de dirección conectado.",
            null              => seApagoSolo ? "Se apagó solo: el tractor superó el límite de velocidad." : null,
            _                 => "No se pudo completar la acción (AGP-SYS-009).",
        };
        _fdNota.Text = motivo ?? "Con el tractor parado. Se apaga solo si el tractor arranca.";
        _fdNota.Foreground = motivo != null ? Rojo : TextoMuted;
    }

    private void PintarConfig()
    {
        if (_cfg == null) return;
        SetSeg(rty: !string.Equals(Str("conv_type"), "Differential", StringComparison.OrdinalIgnoreCase), pintar: true);
        _valCuentas.Text = Entero("counts_per_degree").ToString(CultureInfo.InvariantCulture);
        PintarToggle(_tglInvWas, Bool("invert_was"));
        PintarToggle(_tglInvMotor, Bool("invert_steer"));
        _valPwmMin.Text  = Entero("min_pwm").ToString(CultureInfo.InvariantCulture);
        _valPwmAlto.Text = Entero("high_steer_pwm").ToString(CultureInfo.InvariantCulture);
        _valGanP.Text    = Entero("proportional_gain").ToString(CultureInfo.InvariantCulture);
        foreach (var r in _refrescos) r();
    }

    private void PintarGuardar()
    {
        _btnGuardar.IsEnabled = _sucio;
        _btnGuardar.Content = _sucio ? "Guardar" : "Sin cambios";
        _btnGuardar.Background = _sucio ? Verde : BgCard;
        _btnGuardar.Foreground = _sucio ? Brushes.White : Texto;
        _btnGuardar.BorderBrush = _sucio ? Verde : Borde;
    }

    private void MostrarTab(string id)
    {
        _scAyuda.IsVisible = false;
        _scProbar.IsVisible = id == "probar";
        _scSensor.IsVisible = id == "sensor";
        _scFuerza.IsVisible = id == "fuerza";
        _scGuiado.IsVisible = id == "guiado";
        _scModulo.IsVisible = id == "modulo";
        _scPantalla.IsVisible = id == "pantalla";
        foreach (var (k, b) in _tabs)
        {
            bool sel = k == id;
            b.Background = sel ? BgSel : BgCard;
            b.Foreground = sel ? Texto : TextoMuted;
            b.BorderBrush = sel ? Verde : Borde;
        }
        _tabActual = id;
    }

    private string _tabActual = "probar";

    private void ToggleAyuda()
    {
        if (_scAyuda.IsVisible) { MostrarTab(_tabActual); return; }
        _scProbar.IsVisible = _scSensor.IsVisible = _scFuerza.IsVisible = false;
        _scGuiado.IsVisible = _scModulo.IsVisible = _scPantalla.IsVisible = false;
        _scAyuda.IsVisible = true;
    }

    // ---- edición de config ---------------------------------------------------

    private void SetSeg(bool rty, bool pintar = false)
    {
        if (!pintar && _cfg != null) _cfg["conv_type"] = rty ? "Single" : "Differential";
        PintarToggle(_segRty, rty);
        PintarToggle(_segEncoder, !rty);
    }

    private void ToggleBool(string clave, Button tgl)
    {
        if (_cfg == null) return;
        bool nuevo = !Bool(clave);
        _cfg[clave] = nuevo;
        PintarToggle(tgl, nuevo);
        MarcarSucio();
    }

    private void Nudge(string clave, int paso, int min, int max)
    {
        if (_cfg == null) return;
        int v = Entero(clave) + paso;
        if (v < min) v = min;
        if (v > max) v = max;
        _cfg[clave] = v;
        PintarConfig();
        MarcarSucio();
    }

    private void MarcarSucio()
    {
        _sucio = true;
        _estado.Text = "";
        _estado.Foreground = TextoMuted;
        PintarGuardar();
    }

    // ---- helpers de lectura ----------------------------------------------------

    private string Str(string k)  => _cfg?[k]?.GetValue<string>() ?? "";
    private bool Bool(string k)   => _cfg?[k]?.GetValue<bool>() ?? false;
    private int Entero(string k)
    {
        var n = _cfg?[k];
        if (n == null) return 0;
        try { return n.GetValue<int>(); }
        catch { try { return (int)Math.Round(n.GetValue<double>()); } catch { return 0; } }
    }

    // ---- fábrica de controles ----------------------------------------------------

    private static TextBlock Num(string t) => new()
    {
        Text = t, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Texto,
        MinWidth = 64, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Control LiveCelda(string cap, TextBlock val)
    {
        var sp = new StackPanel { Spacing = 0 };
        sp.Children.Add(new TextBlock
        {
            Text = cap, FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = TextoMuted, TextAlignment = TextAlignment.Center,
        });
        val.FontSize = 17;
        sp.Children.Add(val);
        return sp;
    }

    private static Button BotonChico(string txt) => new()
    {
        Content = txt, Height = 44, MinWidth = 64, FontSize = 13, FontWeight = FontWeight.SemiBold,
        Background = BgCard, Foreground = TextoMuted, BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static Button BotonFd(string txt) => new()
    {
        Content = txt, Width = 58, Height = 58, FontSize = 15, FontWeight = FontWeight.Bold,
        Background = BgCard, Foreground = Texto, BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = false,
    };

    private static Button BotonSeg(string txt) => new()
    {
        Content = txt, Height = 50, FontSize = 13, FontWeight = FontWeight.SemiBold,
        Background = BgCard, Foreground = TextoMuted, BorderBrush = Borde, BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static void PintarToggle(Button b, bool on)
    {
        b.Background = on ? BgSel : BgCard;
        b.Foreground = on ? Texto : TextoMuted;
        b.BorderBrush = on ? Verde : Borde;
    }

    private static Control Envolver(Control c, double izq, double der)
    {
        return new Border { Child = c, Padding = new Thickness(izq, 0, der, 0), Background = Brushes.Transparent };
    }

    private Control FilaNumerica(string etiqueta, TextBlock valor, Action menos, Action mas)
    {
        var fila = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var lbl = new TextBlock
        {
            Text = etiqueta, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(lbl, 0);

        var bMenos = BotonChico("−");
        bMenos.Width = 52; bMenos.Height = 52; bMenos.FontSize = 22; bMenos.Foreground = Texto;
        bMenos.Click += (_, _) => menos();
        Grid.SetColumn(bMenos, 1);

        valor.MinWidth = 72;
        Grid.SetColumn(valor, 2);

        var bMas = BotonChico("+");
        bMas.Width = 52; bMas.Height = 52; bMas.FontSize = 22; bMas.Foreground = Texto;
        bMas.Click += (_, _) => mas();
        Grid.SetColumn(bMas, 3);

        fila.Children.Add(lbl);
        fila.Children.Add(bMenos);
        fila.Children.Add(valor);
        fila.Children.Add(bMas);
        return fila;
    }

    private TextBlock SubTitulo(string t) => new()
    {
        Text = t, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = TextoMuted,
        Margin = new Thickness(2, 0, 0, 6),
    };

    private TextBlock SubTituloSep(string t)
    {
        var tb = SubTitulo(t);
        tb.Margin = new Thickness(2, 14, 0, 6);
        return tb;
    }

    // ---- helpers genéricos de filas (registran su repintado en _refrescos) ----

    /// <summary>Fila de ajuste sobre un ENTERO crudo del wire, con escala solo
    /// de display (igual que los sliders del FormSteer: 29 → "2,9 s").</summary>
    private Control FilaAjuste(string etiqueta, string clave, int min, int max,
                               double escala = 1, int dec = 0, string unidad = "")
    {
        var val = Num("—");
        Action refrescar = () =>
        {
            double v = Entero(clave) * escala;
            val.Text = v.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                     + (unidad.Length > 0 ? " " + unidad : "");
        };
        _refrescos.Add(refrescar);
        return FilaNumerica(etiqueta, val,
            () => { NudgeCrudo(clave, -1, min, max); refrescar(); MarcarSucio(); },
            () => { NudgeCrudo(clave, +1, min, max); refrescar(); MarcarSucio(); });
    }

    /// <summary>Fila de ajuste sobre un DOUBLE real del wire (km/h, segundos).</summary>
    private Control FilaAjusteD(string etiqueta, string clave, double paso,
                                double min, double max, int dec, string unidad = "")
    {
        var val = Num("—");
        Action refrescar = () =>
        {
            val.Text = Doble(clave).ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                     + (unidad.Length > 0 ? " " + unidad : "");
        };
        _refrescos.Add(refrescar);
        Action<double> mover = d =>
        {
            if (_cfg == null) return;
            double v = Math.Round(Doble(clave) + d, 2);
            if (v < min) v = min;
            if (v > max) v = max;
            _cfg[clave] = v;
            refrescar();
            MarcarSucio();
        };
        return FilaNumerica(etiqueta, val, () => mover(-paso), () => mover(+paso));
    }

    /// <summary>Toggle de un bool del wire, ancho completo.</summary>
    private Control FilaToggle(string etiqueta, string clave)
    {
        var b = BotonSeg(etiqueta);
        b.Margin = new Thickness(0, 6, 0, 0);
        Action refrescar = () => PintarToggle(b, Bool(clave));
        _refrescos.Add(refrescar);
        b.Click += (_, _) =>
        {
            if (_cfg == null) return;
            _cfg[clave] = !Bool(clave);
            refrescar();
            MarcarSucio();
        };
        return b;
    }

    /// <summary>Segmentado de un string del wire (valor exacto que espera el módulo).</summary>
    private Control FilaSeg(string etiqueta, string clave, (string Valor, string Texto)[] opciones)
    {
        var cont = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        cont.Children.Add(new TextBlock
        {
            Text = etiqueta, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Texto,
        });
        var fila = new UniformGrid { Columns = opciones.Length };
        var botones = new List<(string Valor, Button Btn)>();
        foreach (var (valor, texto) in opciones)
        {
            var b = BotonSeg(texto);
            string mio = valor;
            b.Click += (_, _) =>
            {
                if (_cfg == null) return;
                _cfg[clave] = mio;
                foreach (var (v2, b2) in botones) PintarToggle(b2, v2 == mio);
                MarcarSucio();
            };
            botones.Add((valor, b));
            fila.Children.Add(Envolver(b, 0, 4));
        }
        _refrescos.Add(() =>
        {
            var actual = Str(clave);
            foreach (var (v2, b2) in botones)
                PintarToggle(b2, string.Equals(v2, actual, StringComparison.OrdinalIgnoreCase));
        });
        cont.Children.Add(fila);
        return cont;
    }

    /// <summary>Los 3 sensores de corte al agarrar el volante son EXCLUYENTES
    /// (misma regla que la página y el FormSteer): prender uno apaga los otros.</summary>
    private Control FilaSensoresCorte()
    {
        var claves = new[] { ("encoder", "Encoder"), ("pressure_sensor", "Presión"), ("current_sensor", "Corriente") };
        var fila = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
        var botones = new List<(string Clave, Button Btn)>();
        foreach (var (clave, texto) in claves)
        {
            var b = BotonSeg(texto);
            string mia = clave;
            b.Click += (_, _) =>
            {
                if (_cfg == null) return;
                bool nuevo = !Bool(mia);
                _cfg[mia] = nuevo;
                if (nuevo)
                    foreach (var (c2, _) in botones)
                        if (c2 != mia) _cfg[c2] = false;
                foreach (var (c2, b2) in botones) PintarToggle(b2, Bool(c2));
                MarcarSucio();
            };
            botones.Add((clave, b));
            fila.Children.Add(Envolver(b, 0, 4));
        }
        _refrescos.Add(() => { foreach (var (c2, b2) in botones) PintarToggle(b2, Bool(c2)); });
        return fila;
    }

    private void NudgeCrudo(string clave, int paso, int min, int max)
    {
        if (_cfg == null) return;
        int v = Entero(clave) + paso;
        if (v < min) v = min;
        if (v > max) v = max;
        _cfg[clave] = v;
    }

    private double Doble(string k)
    {
        var n = _cfg?[k];
        if (n == null) return 0;
        try { return n.GetValue<double>(); }
        catch { try { return n.GetValue<int>(); } catch { return 0; } }
    }
}
