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
    /// <summary>Pidió la pantalla completa HTML ("Todo…").</summary>
    public event Action? TodoPedido;

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
    private readonly ScrollViewer _scAyuda;
    private readonly Dictionary<string, Button> _tabs = new();

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
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(4, 0, 0, 8) };
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

        var btnTodo = BotonChico("Todo…");
        btnTodo.Click += (_, _) => TodoPedido?.Invoke();
        Grid.SetColumn(btnTodo, 3);
        btnTodo.Margin = new Thickness(0, 0, 6, 0);

        var btnCerrar = BotonChico("✕");
        btnCerrar.Width = 44;
        btnCerrar.Click += (_, _) => Cerrar();
        Grid.SetColumn(btnCerrar, 4);

        header.Children.Add(titulo);
        header.Children.Add(live);
        header.Children.Add(btnAyuda);
        header.Children.Add(btnTodo);
        header.Children.Add(btnCerrar);

        // ---------- tabs ----------
        var tabs = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (id, txt) in new[] { ("probar", "Probar"), ("sensor", "Sensor"), ("fuerza", "Fuerza") })
        {
            var b = new Button
            {
                Content = txt, Height = 50, FontSize = 14, FontWeight = FontWeight.SemiBold,
                Background = BgCard, Foreground = TextoMuted,
                BorderBrush = Borde, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 4, 0),
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
            ("Todo…", "Abre la pantalla completa (modos de guiado, avanzado, velocidades, barra guía)."),
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
        stage.Children.Add(_scAyuda);

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(tabs);
        root.Children.Add(stage);
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
}
