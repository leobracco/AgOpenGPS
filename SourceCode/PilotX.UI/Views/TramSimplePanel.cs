// ============================================================================
// TramSimplePanel.cs — TRAMLINES (huellas de rueda) NATIVO, reemplaza
// pages/tramline.html en la pantalla de la cabina (ex WinForms FormTram).
//
// Por qué este port importa: es pantalla de LABOR — se abre con el tractor
// adentro del lote — y la VISTA PREVIA NO VIVE EN LA PANTALLA: las huellas se
// dibujan sobre el mapa principal (tram.displayMode). O sea que la ventana de
// Chromium de 350x340 se paraba justo encima de lo único que hay para mirar
// mientras se ajustan las pasadas. Ahora es una card chica flotante, anclada a
// la derecha como Contorno/Cabecera, con el mapa vivo detrás.
//
// Qué quedó NATIVO: la pantalla entera, con paridad de comportamiento.
//   · Pasadas entre huellas (− / valor / +), piso 1, sin techo (igual que el
//     wire: no se inventa un límite que hoy no existe).
//   · Modo de generación, ciclando Todo → Relleno → Contorno (deshabilitado
//     sin contorno; el back además fuerza FillTracks en ese caso).
//   · Transparencia 0..100 paso 5, con debounce de 120 ms — el POST de alpha
//     NO reaplica el estado devuelto, si no el slider salta bajo el dedo.
//   · Invertir A ↔ B.
//   · Los tres anchos de solo lectura (herramienta / tramline / trocha).
//   · Guardar / Cancelar (commit) y los 8 textos de estado del pill.
//
// Qué SIGUE en HTML: la página wwwroot/pages/tramline.html, intacta, para el
// Hub remoto / celular / Android. El editor multi (pages/tramlines.html,
// comando "tram_multi") es OTRA pantalla y no entra acá.
//
// DOS COSAS QUE NO SON BUGS Y NO HAY QUE "ARREGLAR":
//   1. "Invertir A ↔ B" GUARDA LAS GUÍAS A DISCO en el acto (lo hace el back).
//      "Cancelar" descarta el tram, NO deshace el swap. Es el comportamiento
//      heredado de FormTram.
//   2. Abrir esta pantalla YA prende la preview en el mapa: el GET /state
//      ejecuta Open() del lado del motor (elige modo + construye). Por eso se
//      pide UNA sola vez por apertura y no hay polling.
//
// Salida sin guardar: la página mandaba commit{save:false} en `pagehide` para
// no dejar la preview colgada en el mapa. Acá la semántica vive en Cerrar()
// (fire-and-forget) y en DetachEnCierreDeApp() (esperado y acotado: el motor
// es OTRO proceso y sobrevive a la bajada de la pantalla, así que sin ese POST
// las huellas quedaban dibujadas para siempre). El flag _settled evita el
// doble commit después de Guardar: un discard post-save limpiaría el tram
// recién guardado y lo re-guardaría vacío.
//
// Wire: el MISMO /api/tram-simple/* de siempre (ver TramSimpleClient).
// ============================================================================

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public sealed class TramSimplePanel : Border
{
    // ---- paleta PilotX (idéntica a ContornoPanel/GuiasPanel/CabeceraPanel) --
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#7A857B"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush BgError    = new SolidColorBrush(Color.Parse("#FBECEC"));
    private static readonly IBrush TextoError = new SolidColorBrush(Color.Parse("#B33F3A"));

    private const string Mono = "Consolas, Courier New, monospace";

    // Ciclo del botón Modo, igual que MODES/MODE_LABEL de tramline.js.
    private static readonly string[] Modos = { "All", "FillTracks", "BoundaryTracks" };

    private TramSimpleClient? _cli;
    private CancellationTokenSource? _cts;

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;

    /// <summary>Aviso corto (lo muestra MainWindow como toast, nunca modal).</summary>
    public event Action<string>? Aviso;

    // ---- estado -------------------------------------------------------------
    private int _passes = 1;
    private string _modo = "All";
    private string _units = "m";
    private bool _hayContorno;
    private bool _hayEstado;          // ya llegó al menos un estado bueno
    private bool _settled;            // true tras un commit ok (guardar o cancelar)
    private bool _cerrada = true;     // el panel arranca cerrado
    private bool _arrastrando;        // dedo sobre el slider: el estado no lo pisa
    private bool _aplicando;          // seteo programático del slider (no dispara POST)
    private bool _alphaPendiente;     // hay un alpha esperando el debounce de 120 ms
    private DispatcherTimer? _timerAlpha;

    // ---- controles ----------------------------------------------------------
    private readonly Border _pill;
    private readonly Ellipse _pillDot;
    private readonly TextBlock _pillTxt;
    private readonly Border _warnBox;
    private readonly TextBlock _warnTxt;
    private readonly Button _btnMenos;
    private readonly Button _btnMas;
    private readonly TextBlock _valPasses;
    private readonly Button _btnModo;
    private readonly Slider _slider;
    private readonly TextBlock _valAlpha;
    private readonly Button _btnSwap;
    private readonly TextBlock _valTool;
    private readonly TextBlock _valTram;
    private readonly TextBlock _valTrack;
    private readonly Button _btnCancelar;
    private readonly Button _btnGuardar;

    public TramSimplePanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        // Ancho FIJO y chico, como la ventana HTML (350): cada píxel de la card
        // es mapa tapado, y acá el mapa ES la vista previa de las huellas.
        Width = 380;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;

        // ---------- cabecera: título + subtítulo + pill + ✕ ----------
        var titulo = new TextBlock
        {
            Text = "Tramlines", FontSize = 16, FontWeight = FontWeight.Bold,
            Foreground = Texto,
        };
        var subtitulo = new TextBlock
        {
            Text = "Huellas de rueda por pasadas · vista previa en el mapa",
            FontSize = 10.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var tituloCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tituloCol.Children.Add(titulo);
        tituloCol.Children.Add(subtitulo);

        _pillDot = new Ellipse
        {
            Width = 8, Height = 8, Fill = TextoDim,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
        };
        _pillTxt = new TextBlock
        {
            Text = "—", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pillCont = new StackPanel { Orientation = Orientation.Horizontal };
        pillCont.Children.Add(_pillDot);
        pillCont.Children.Add(_pillTxt);
        _pill = new Border
        {
            Child = pillCont, CornerRadius = new CornerRadius(999),
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 3, 9, 3), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        // La ventana HTML tenía la ✕ del sistema; la card embebida necesita la
        // suya (mínimo 40x40, táctil).
        var btnCerrar = new Button
        {
            Content = "✕", Width = 44, Height = 40, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        btnCerrar.Click += (_, _) => Cerrar();

        var cabecera = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetColumn(tituloCol, 0);
        Grid.SetColumn(_pill, 1);
        Grid.SetColumn(btnCerrar, 2);
        cabecera.Children.Add(tituloCol);
        cabecera.Children.Add(_pill);
        cabecera.Children.Add(btnCerrar);

        // ---------- banner de advertencia ----------
        // El HTML lo pintaba con fondo #5a2c2c (oscuro): va contra la paleta
        // clara de PilotX. Chip rojo suave estándar.
        _warnTxt = new TextBlock
        {
            Text = "", FontSize = 11.5, Foreground = TextoError, TextWrapping = TextWrapping.Wrap,
        };
        _warnBox = new Border
        {
            Child = _warnTxt, Background = BgError, BorderBrush = Rojo,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false,
        };

        // ---------- stepper: pasadas entre huellas ----------
        _btnMenos = BotonStepper("−");
        _btnMenos.Click += async (_, _) => await CambiarPasadasAsync(-1);
        _btnMas = BotonStepper("+");
        _btnMas.Click += async (_, _) => await CambiarPasadasAsync(+1);
        _valPasses = new TextBlock
        {
            Text = "1", FontFamily = new FontFamily(Mono), FontSize = 20, FontWeight = FontWeight.Bold,
            Foreground = Verde, MinWidth = 52, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stepper = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stepper.Children.Add(_btnMenos);
        stepper.Children.Add(_valPasses);
        stepper.Children.Add(_btnMas);

        var filaPasadas = FilaLabel("Pasadas entre huellas", stepper);

        // ---------- modo ----------
        _btnModo = new Button
        {
            Content = "Todo", Height = 52, MinWidth = 150, FontSize = 13.5,
            FontWeight = FontWeight.SemiBold, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Background = BgFila, Foreground = Texto,
            BorderBrush = Borde, HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _btnModo.Click += async (_, _) => await CiclarModoAsync();
        var filaModo = FilaLabel("Modo", _btnModo);

        // ---------- transparencia ----------
        // El "NN%" se crea ANTES del slider: su handler de Value lo escribe, y
        // no puede depender del orden de inicialización de los campos.
        _valAlpha = new TextBlock
        {
            Text = "80%", FontFamily = new FontFamily(Mono), FontSize = 13,
            FontWeight = FontWeight.Bold, Foreground = Verde, MinWidth = 44,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        _slider = new Slider
        {
            Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true,
            Value = 80, Width = 170, VerticalAlignment = VerticalAlignment.Center,
        };
        // El dedo sobre el slider manda: mientras se arrastra, ningún estado que
        // vuelva de otro POST le pisa el valor (si no, salta bajo el dedo).
        _slider.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => _arrastrando = true, RoutingStrategies.Tunnel);
        _slider.AddHandler(InputElement.PointerReleasedEvent,
            (_, _) => _arrastrando = false, RoutingStrategies.Tunnel);
        _slider.AddHandler(InputElement.PointerCaptureLostEvent,
            (_, _) => _arrastrando = false, RoutingStrategies.Tunnel);
        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _valAlpha.Text = ((int)Math.Round(_slider.Value))
                .ToString(CultureInfo.InvariantCulture) + "%";
            if (_aplicando) return;   // lo movió el estado, no el operario
            ProgramarAlpha();
        };
        var sliderCont = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sliderCont.Children.Add(_slider);
        sliderCont.Children.Add(_valAlpha);
        var filaAlpha = FilaLabel("Transparencia", sliderCont);

        // ---------- invertir A ↔ B ----------
        _btnSwap = new Button
        {
            Content = "Invertir A ↔ B", Height = 52, FontSize = 13.5,
            FontWeight = FontWeight.SemiBold, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Background = BgFila, Foreground = Texto,
            BorderBrush = Borde, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _btnSwap.Click += async (_, _) => await SwapAsync();

        // ---------- bloque info (solo lectura) ----------
        _valTool = ValorInfo();
        _valTram = ValorInfo();
        _valTrack = ValorInfo();
        var info = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        info.Children.Add(new Border
        {
            Height = 1, Background = Borde, Margin = new Thickness(0, 0, 0, 6),
        });
        info.Children.Add(FilaInfo("Ancho herramienta", _valTool));
        info.Children.Add(FilaInfo("Ancho tramline", _valTram));
        info.Children.Add(FilaInfo("Trocha vehículo", _valTrack));

        // ---------- pie ----------
        _btnCancelar = BotonPie("Cancelar", acento: false);
        _btnCancelar.Click += async (_, _) => await CommitAsync(false);
        _btnGuardar = BotonPie("Guardar", acento: true);
        _btnGuardar.Click += async (_, _) => await CommitAsync(true);

        var pie = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        _btnCancelar.Margin = new Thickness(0, 0, 4, 0);
        _btnGuardar.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(_btnCancelar, 0);
        Grid.SetColumn(_btnGuardar, 1);
        pie.Children.Add(_btnCancelar);
        pie.Children.Add(_btnGuardar);

        // ---------- árbol ----------
        var root = new StackPanel();
        root.Children.Add(cabecera);
        root.Children.Add(_warnBox);
        root.Children.Add(filaPasadas);
        root.Children.Add(filaModo);
        root.Children.Add(filaAlpha);
        root.Children.Add(_btnSwap);
        root.Children.Add(info);
        root.Children.Add(pie);
        Child = root;
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del canal HTTP (mismo criterio que los otros paneles).</summary>
    public void Attach(TramSimpleClient cli) => _cli = cli;

    /// <summary>
    /// Abre el panel. Un solo GET /state (que del lado del motor ES el Open:
    /// elige el modo y construye la preview sobre el mapa). Nada de polling.
    /// </summary>
    public async void Abrir()
    {
        if (!_cerrada) return;             // ya abierto: no se re-abre (otro Open sería otro rebuild)
        _cerrada = false;
        _settled = false;
        _hayEstado = false;
        _arrastrando = false;
        // Un alpha que quedó a medio debounce en la apertura ANTERIOR no puede
        // sobrevivir a esta: VaciarAlphaAsync() lo mandaría antes del commit con
        // el valor del slider de ahora, que todavía es el default (80) porque el
        // GET /state no volvió — le pisaría al motor la transparencia real.
        _alphaPendiente = false;
        ResetVista();
        IsVisible = true;
        Traductor.Aplicar(this);

        if (_cli == null) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        TramSimpleStateDto? s;
        try { s = await _cli.GetStateAsync(ct).ConfigureAwait(true); }
        // TaskCanceledException HEREDA de OperationCanceledException: sin el
        // `when`, un timeout del motor se confundiría con el cierre del panel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { s = null; }
        if (ct.IsCancellationRequested || _cerrada) return;
        AplicarEstado(s);
    }

    /// <summary>
    /// Cierra el panel. Si no hubo commit, descarta la preview (fire-and-forget)
    /// para no dejar las huellas colgadas en el mapa — es la semántica del
    /// `pagehide` de la página.
    /// </summary>
    public void Cerrar()
    {
        if (_cerrada) { IsVisible = false; return; }
        _cerrada = true;
        PararTimerAlpha();
        _alphaPendiente = false;   // el debounce muere con el panel (igual que el setTimeout en pagehide)
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        if (!_settled) _ = DescartarAsync();
        IsVisible = false;
        Cerrado?.Invoke();
    }

    /// <summary>Alias del cierre para los puntos que usan el patrón Detach().</summary>
    public void Detach() => Cerrar();

    /// <summary>
    /// Bajada de la app con el panel abierto. El motor es OTRO proceso y
    /// sobrevive a la pantalla: sin este POST las huellas quedarían dibujadas
    /// en el mapa para siempre. Por eso el discard se ESPERA (acotado) en vez
    /// de irse fire-and-forget con el proceso ya muriendo.
    /// </summary>
    public void DetachEnCierreDeApp()
    {
        if (_cerrada) return;
        _cerrada = true;
        PararTimerAlpha();
        _alphaPendiente = false;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        if (_settled || _cli == null) return;
        try { _cli.CommitAsync(false).Wait(TimeSpan.FromMilliseconds(800)); } catch { }
    }

    private async Task DescartarAsync()
    {
        var cli = _cli;
        if (cli == null) return;
        try { await cli.CommitAsync(false).ConfigureAwait(false); } catch { }
    }

    // =========================================================================
    //  acciones (todas pintan con el estado que devuelve el POST)
    // =========================================================================

    private async Task CambiarPasadasAsync(int delta)
    {
        if (_cli == null) return;
        // El piso 1 lo clampan cliente Y back; no hay techo en ninguno de los
        // dos y no se inventa acá (cambiaría el comportamiento del wire).
        int destino = Math.Max(1, _passes + delta);
        AplicarEstado(await _cli.SetPassesAsync(destino).ConfigureAwait(true));
    }

    private async Task CiclarModoAsync()
    {
        if (_cli == null) return;
        int i = Array.IndexOf(Modos, _modo);
        // Modo desconocido (p. ej. "None") → arranca el ciclo en "All", igual
        // que el indexOf(-1) del JS.
        string siguiente = Modos[(i + 1) % Modos.Length];
        AplicarEstado(await _cli.SetModeAsync(siguiente).ConfigureAwait(true));
    }

    private async Task SwapAsync()
    {
        if (_cli == null) return;
        SetPill("invirtiendo…", Verde);   // feedback inmediato: el rebuild tarda
        AplicarEstado(await _cli.SwapAsync().ConfigureAwait(true));
    }

    private async Task CommitAsync(bool guardar)
    {
        if (_cli == null) return;
        // Si el operario soltó el slider y tocó Guardar dentro de los 120 ms del
        // debounce, ese alpha TODAVÍA no salió — y el commit persiste el alpha
        // que tiene el motor, no el que muestra la pantalla. Se vacía primero.
        await VaciarAlphaAsync().ConfigureAwait(true);
        bool ok = await _cli.CommitAsync(guardar).ConfigureAwait(true);
        if (!ok)
        {
            // Nunca modal (ShowDialog trababa la cabina): toast y la card se
            // queda abierta para reintentar.
            Aviso?.Invoke(Traductor.T(guardar
                ? "No se pudo guardar el tramline."
                : "No se pudo descartar el tramline."));
            return;
        }
        _settled = true;
        SetPill(guardar ? "guardado" : "cancelado", guardar ? Verde : TextoDim);
        // El HTML dejaba la ventana abierta (la cerraba el operario con la ✕).
        // Acá se cierra sola: quedarse abierta con _settled=true es una trampa
        // — cualquier cambio posterior ya no se commitearía al salir.
        Cerrar();
    }

    // Alpha: no reconstruye geometría, solo redibuja → se manda MIENTRAS se
    // arrastra, con debounce de 120 ms (mismo número que el setTimeout del JS).
    private void ProgramarAlpha()
    {
        _alphaPendiente = true;
        _timerAlpha ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timerAlpha.Tick -= OnTickAlpha;
        _timerAlpha.Tick += OnTickAlpha;
        try { _timerAlpha.Stop(); } catch { }
        _timerAlpha.Start();
    }

    private void OnTickAlpha(object? s, EventArgs e)
    {
        PararTimerAlpha();
        _alphaPendiente = false;
        var cli = _cli;
        if (cli == null) return;
        int pct = (int)Math.Round(_slider.Value);
        // Fire-and-forget A PROPÓSITO: reaplicar el estado devuelto haría saltar
        // el slider bajo el dedo (misma política que tramline.js).
        _ = cli.SetAlphaAsync(pct);
    }

    /// <summary>Manda YA el alpha que quedó esperando el debounce (antes de un commit).</summary>
    private async Task VaciarAlphaAsync()
    {
        PararTimerAlpha();
        if (!_alphaPendiente) return;
        _alphaPendiente = false;
        var cli = _cli;
        if (cli == null) return;
        try { await cli.SetAlphaAsync((int)Math.Round(_slider.Value)).ConfigureAwait(true); }
        catch { }
    }

    private void PararTimerAlpha()
    {
        try { _timerAlpha?.Stop(); } catch { }
    }

    // =========================================================================
    //  estado → UI
    // =========================================================================

    private void AplicarEstado(TramSimpleStateDto? s)
    {
        // Sin respuesta o ok:false → mismo tratamiento (la página tampoco
        // distinguía). Los controles quedan como estaban: el operario puede
        // reintentar el mismo toque en cuanto el motor vuelva.
        if (s == null || !s.Ok)
        {
            SetPill("sin conexión", TextoDim);
            string cod = string.IsNullOrEmpty(s?.Error) ? "AGP-NET-201" : s!.Error!;
            SetWarn(Traductor.T("Sin conexión con PilotX.") + "  (" + cod + ")");
            return;
        }

        if (!s.HasTrack)
        {
            SetWarn(Traductor.T("Activá primero una línea de guía (AB o curva) para armar los tramlines."));
            Habilitar(false);
            SetPill("sin guía", TextoDim);
            return;
        }

        SetWarn(null);
        Habilitar(true);
        _hayEstado = true;
        _passes = s.Passes;
        _modo = string.IsNullOrEmpty(s.Mode) ? "All" : s.Mode!;
        _units = string.IsNullOrEmpty(s.Units) ? "m" : s.Units!;
        _hayContorno = s.HasBoundary;

        _valPasses.Text = _passes.ToString(CultureInfo.InvariantCulture);
        _btnModo.Content = Traductor.T(EtiquetaModo(_modo));
        // Sin contorno los modos de borde no tienen sentido (y el back fuerza
        // FillTracks): el botón queda gris, igual que en FormTram.
        _btnModo.IsEnabled = _hayContorno;

        // El dedo sobre el slider manda: no se le pisa el valor mientras arrastra.
        if (!_arrastrando)
        {
            _aplicando = true;
            _slider.Value = s.AlphaPercent;
            _aplicando = false;
            _valAlpha.Text = s.AlphaPercent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        _valTool.Text = Fmt(s.ToolWidthDisplay);
        _valTram.Text = Fmt(s.TramWidthDisplay);
        _valTrack.Text = Fmt(s.TrackWidthDisplay);

        SetPill(s.IsCurve ? "curva · en vivo" : "AB · en vivo", Verde);
    }

    private void ResetVista()
    {
        _passes = 1;
        _modo = "All";
        _units = "m";
        _hayContorno = false;
        _valPasses.Text = "1";
        _btnModo.Content = Traductor.T("Todo");
        _valTool.Text = "—";
        _valTram.Text = "—";
        _valTrack.Text = "—";
        SetWarn(null);
        SetPill("—", TextoDim);
        Habilitar(true);
        _aplicando = true;
        _slider.Value = 80;
        _aplicando = false;
        _valAlpha.Text = "80%";
    }

    private void SetPill(string texto, IBrush color)
    {
        _pillTxt.Text = Traductor.T(texto);
        _pillTxt.Foreground = color == Verde ? Texto : TextoMuted;
        _pillDot.Fill = color;
    }

    private void SetWarn(string? msg)
    {
        _warnTxt.Text = msg ?? "";
        _warnBox.IsVisible = !string.IsNullOrEmpty(msg);
    }

    // Sin guía activa no hay nada que armar: todo gris menos Cancelar y la ✕
    // (paridad con el HTML — Cancelar sigue siendo la salida limpia).
    private void Habilitar(bool on)
    {
        _btnMenos.IsEnabled = on;
        _btnMas.IsEnabled = on;
        _btnModo.IsEnabled = on && _hayContorno;
        _slider.IsEnabled = on;
        _btnSwap.IsEnabled = on;
        _btnGuardar.IsEnabled = on;
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    /// <summary>Valor C# del modo → etiqueta del botón (fallback: el string crudo).</summary>
    private static string EtiquetaModo(string modo) => modo switch
    {
        "All" => "Todo",
        "FillTracks" => "Relleno",
        "BoundaryTracks" => "Contorno",
        _ => modo,
    };

    /// <summary>2 decimales + unidad, como el fmt() de tramline.js. Antes del primer estado: "—".</summary>
    private string Fmt(double v)
    {
        if (!_hayEstado || double.IsNaN(v) || double.IsInfinity(v)) return "—";
        return v.ToString("0.00", CultureInfo.InvariantCulture) + " " + _units;
    }

    private static Grid FilaLabel(string etiqueta, Control derecha)
    {
        var lbl = new TextBlock
        {
            Text = etiqueta, FontSize = 12.5, Foreground = Texto,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 4, 0, 4),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(derecha, 1);
        g.Children.Add(lbl);
        g.Children.Add(derecha);
        return g;
    }

    private static Grid FilaInfo(string etiqueta, TextBlock valor)
    {
        var lbl = new TextBlock
        {
            Text = etiqueta, FontSize = 12, Foreground = Texto,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(valor, 1);
        g.Children.Add(lbl);
        g.Children.Add(valor);
        return g;
    }

    private static TextBlock ValorInfo() => new()
    {
        Text = "—", FontFamily = new FontFamily(Mono), FontSize = 12,
        Foreground = TextoMuted, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right, MinWidth = 78,
    };

    private static Button BotonStepper(string texto) => new()
    {
        Content = texto, Width = 64, Height = 60, FontSize = 22, FontWeight = FontWeight.Bold,
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = BgFila, Foreground = Texto, BorderBrush = Borde,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button BotonPie(string texto, bool acento) => new()
    {
        Content = texto, Height = 52, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = acento ? Verde : BgFila,
        Foreground = acento ? Brushes.White : Texto,
        BorderBrush = acento ? Verde : Borde,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };
}
