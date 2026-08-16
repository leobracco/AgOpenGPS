// ============================================================================
// CabeceraPanel.cs — la CABECERA del lote, NATIVA (reemplaza pages/cabecera.html
// en la pantalla de la cabina).
//
// Por qué el port: cabecera se abre EN EL LOTE, con el tractor adentro, y era
// un diálogo WebView de 350x340 que además TAPABA el mapa — justo la pantalla
// donde lo único que el operario quiere ver es la franja verde dibujándose
// contra el lindero. Ahora es una card chica flotante a la derecha y el mapa GL
// vivo de atrás hace de preview: dibuja lindero y cabecera y se refresca solo
// con el próximo /api/aog/state (después de build/reset/off), como dice el
// comentario del propio EngineHeadlandEditService.
//
// Qué quedó NATIVO — la pantalla vigente entera:
//   · distancia (con la precarga del ancho de herramienta),
//   · "Ancho de herramienta" (pone el ancho en el campo),
//   · Construir (offset Build Around) con el fallback al ancho cuando el campo
//     quedó en 0 — construir con 0 hacía "una cabecera de cero metros, sin
//     error y sin nada visible",
//   · Reset al contorno (+ cancel-touch, igual que la página),
//   · Apagar cabecera,
//   · toggle "Secciones controladas en cabecera",
//   · el estado en el pill y el banner de aviso.
//
// Qué NO se portó y por qué:
//   · el CANVAS de preview con pan/zoom/pinch/fit (~180 líneas del JS): el mapa
//     GL de atrás ya lo hace y mejor. Duplicarlo sería un segundo mapa peor;
//   · el flujo de "editar borde" (slice A/B, curva/recta, cortar/extender/
//     deshacer): la UI lo sacó el 2026-08-05 a pedido del usuario y el HTML
//     vigente tampoco lo llama. /tap /extend /clip /undo siguen vivos en el
//     back "por si vuelve"; acá no se consumen.
//
// Qué SIGUE en HTML: pages/cabecera.html + js/cabecera.js, INTACTOS, para el
// Hub remoto / celular / Android. "Cabecera avanzada" (cabecera-lineas.html)
// es otra pantalla y este porteo no la toca.
//
// OJO con el CIERRE: POST /api/headland/close no es cosmética — suaviza la
// cabecera, la PERSISTE y refresca paneles. La página lo aseguraba con
// sendBeacon + pagehide; acá hay más caminos de cierre (la ✕, "solo un overlay
// a la vez", el apagado de la ventana), así que el /close vive en Detach() con
// flag idempotente y MainWindow lo llama también en su Closed.
//
// Wire: el MISMO /api/headland/* de siempre (ver HeadlandClient).
// ============================================================================

using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public sealed class CabeceraPanel : Border
{
    // ---- paleta PilotX (idéntica a ContornoPanel/GuiasPanel) ----------------
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ok         = new SolidColorBrush(Color.Parse("#3D9A33"));
    private static readonly IBrush Warn       = new SolidColorBrush(Color.Parse("#B98A2E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush Dim        = new SolidColorBrush(Color.Parse("#8A958B"));
    private static readonly IBrush BgError    = new SolidColorBrush(Color.Parse("#FBECEC"));
    private static readonly IBrush TextoError = new SolidColorBrush(Color.Parse("#B33F3A"));

    private const string Mono = "Consolas, Courier New, monospace";

    private HeadlandClient? _cli;

    /// <summary>El operario cerró el panel.</summary>
    // Sin evento Aviso: todo lo que esta pantalla tiene para decir entra en el
    // banner de la card (que está a la vista mientras se toca). Los toasts son
    // para lo que pasa con el panel YA cerrado.
    public event Action? Cerrado;

    // ---- estado -------------------------------------------------------------
    private HeadlandStateDto? _estado;
    private string _unidades = "m";
    private double _anchoHerrM;
    private bool _sinConexion;
    private bool _construyendo;
    /// <summary>Flag idempotente del /close (el `closed` del JS).</summary>
    private bool _cerrada = true;
    private string _status = "—";
    private string _statusTipo = "idle";     // ok | warn | bad | idle
    private string _banner = "";
    /// <summary>Reintento suave del /open mientras no haya estado (la página no reintentaba).</summary>
    private DispatcherTimer? _timerReintento;

    // ---- controles ----------------------------------------------------------
    private readonly Border _pill;
    private readonly Ellipse _pillDot;
    private readonly TextBlock _pillTxt;
    private readonly Border _avisoBox;
    private readonly TextBlock _avisoTxt;
    private readonly TextBox _txtDist;
    private readonly TextBlock _lblUnidad;
    private readonly Button _btnAncho;
    private readonly Button _btnConstruir;
    private readonly Button _btnReset;
    private readonly Button _btnSecciones;
    private readonly Button _btnApagar;

    public CabeceraPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        // Ancho FIJO y chico, en el espíritu de la ventana HTML (350x340): esta
        // pantalla se abre para MIRAR el mapa, cada píxel de card es mapa tapado.
        Width = 320;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;

        // ---------- cabecera: título + pill + ✕ ----------
        var titulo = new TextBlock
        {
            Text = "Cabecera", FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
        };

        _pillDot = new Ellipse
        {
            Width = 8, Height = 8, Fill = Dim,
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
            Margin = new Thickness(8, 0, 0, 0),
        };

        // La ventana HTML tenía la ✕ del sistema y un botón "Salir"; la card
        // embebida los junta en una sola ✕ táctil (44x40).
        var btnCerrar = new Button
        {
            Content = "✕", Width = 44, Height = 40, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(btnCerrar, "Salir");
        btnCerrar.Click += (_, _) => Cerrar();

        var cabecera = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(titulo, 0);
        Grid.SetColumn(_pill, 1);
        Grid.SetColumn(btnCerrar, 3);
        cabecera.Children.Add(titulo);
        cabecera.Children.Add(_pill);
        cabecera.Children.Add(btnCerrar);

        var subtitulo = new TextBlock
        {
            Text = "Construir cabecera por distancia", FontSize = 11, Foreground = TextoMuted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8),
        };

        // ---------- banner de aviso (Border + IsVisible: NADA de Flyout sobre el mapa GL) ----------
        _avisoTxt = new TextBlock
        {
            Text = "", FontSize = 12, Foreground = TextoError, TextWrapping = TextWrapping.Wrap,
        };
        _avisoBox = new Border
        {
            Child = _avisoTxt, Background = BgError, BorderBrush = Rojo,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false,
        };

        // ---------- distancia ----------
        var lblDist = new TextBlock
        {
            Text = "DISTANCIA", FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = TextoMuted, Margin = new Thickness(2, 0, 0, 4),
        };

        _txtDist = new TextBox
        {
            Text = "0",
            FontFamily = new FontFamily(Mono), FontSize = 17, Height = 44,
            TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 0, 8, 0),
        };
        // El teclado nativo de PilotX no es automático por foco: se pide a mano
        // con la misma señal HTTP que mandaba keyboard.js en la página.
        _txtDist.GotFocus  += (_, _) => _ = _cli?.TecladoAsync(true);
        _txtDist.LostFocus += (_, _) => _ = _cli?.TecladoAsync(false);

        _lblUnidad = new TextBlock
        {
            Text = "m", FontFamily = new FontFamily(Mono), FontSize = 13, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 18,
        };

        var filaDist = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_txtDist, 0);
        Grid.SetColumn(_lblUnidad, 1);
        filaDist.Children.Add(_txtDist);
        filaDist.Children.Add(_lblUnidad);

        _btnAncho = BotonAccion("Ancho de herramienta");
        _btnAncho.Height = 40;
        _btnAncho.FontSize = 12;
        _btnAncho.Margin = new Thickness(0, 6, 0, 0);
        _btnAncho.Click += (_, _) => PonerAnchoHerramienta();

        _btnConstruir = BotonAccion("Construir", acento: true);
        _btnConstruir.Height = 50;
        _btnConstruir.FontSize = 15;
        _btnConstruir.Margin = new Thickness(0, 8, 0, 0);
        _btnConstruir.Click += async (_, _) => await ConstruirAsync();

        _btnReset = BotonAccion("Reset al contorno");
        _btnReset.Margin = new Thickness(0, 6, 0, 0);
        _btnReset.Click += async (_, _) => await ResetAsync();

        // ---------- opciones ----------
        var sep = new Border
        {
            Height = 1, Background = Borde, Margin = new Thickness(0, 10, 0, 8),
        };

        var lblSec = new TextBlock
        {
            Text = "Secciones controladas en cabecera", FontSize = 11.5, Foreground = Texto,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        // Pill Sí/No en vez de checkbox: más dedo, misma información (mismo
        // criterio que el "Cruzar" de Contorno).
        _btnSecciones = new Button
        {
            Content = "No", Height = 34, MinWidth = 58, FontSize = 12, FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1),
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(8, 0, 0, 0),
        };
        _btnSecciones.Click += async (_, _) => await ToggleSeccionesAsync();

        var filaSec = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(lblSec, 0);
        Grid.SetColumn(_btnSecciones, 1);
        filaSec.Children.Add(lblSec);
        filaSec.Children.Add(_btnSecciones);

        _btnApagar = BotonAccion("Apagar cabecera");
        _btnApagar.Margin = new Thickness(0, 8, 0, 0);
        _btnApagar.Click += async (_, _) => await ApagarAsync();

        // ---------- árbol ----------
        var root = new StackPanel();
        root.Children.Add(cabecera);
        root.Children.Add(subtitulo);
        root.Children.Add(_avisoBox);
        root.Children.Add(lblDist);
        root.Children.Add(filaDist);
        root.Children.Add(_btnAncho);
        root.Children.Add(_btnConstruir);
        root.Children.Add(_btnReset);
        root.Children.Add(sep);
        root.Children.Add(filaSec);
        root.Children.Add(_btnApagar);
        Child = root;
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del canal HTTP (mismo criterio que los otros paneles).</summary>
    public void Attach(HeadlandClient cli) => _cli = cli;

    /// <summary>
    /// Abre el panel. Arranque igual al de la página: POST /open (que además
    /// inicializa la cabecera con el contorno si estaba vacía) y se pinta el
    /// estado que devuelve.
    /// </summary>
    public async void Abrir()
    {
        _cerrada = false;          // el /close vuelve a estar pendiente
        _construyendo = false;
        _sinConexion = false;
        _estado = null;
        _banner = "";
        Status("—", "idle");
        Render();
        IsVisible = true;
        Traductor.Aplicar(this);

        if (_cli == null) return;
        var s = await _cli.OpenAsync().ConfigureAwait(true);
        // Si el operario cerró la card mientras volaba el /open (hasta 3 s con
        // el Hub caído), lo que venga después se descarta.
        if (!IsVisible) return;
        AplicarEstado(s);
    }

    /// <summary>
    /// Cierra el panel. El POST /close va SIEMPRE (suaviza + persiste la
    /// cabecera): es el equivalente del sendBeacon de la página.
    /// </summary>
    public void Cerrar()
    {
        Detach();
        IsVisible = false;
        Cerrado?.Invoke();
    }

    /// <summary>
    /// Cierre "por otra vía" (regla de un solo overlay, apagado de la ventana).
    /// Idempotente: el /close se manda una sola vez por sesión abierta.
    /// </summary>
    public void Detach()
    {
        PararReintento();
        _ = _cli?.TecladoAsync(false);
        if (_cerrada) return;
        _cerrada = true;
        // Fire-and-forget: el panel ya no está en pantalla, pero la persistencia
        // tiene que salir igual (el HTML usaba sendBeacon justamente por esto).
        _ = _cli?.CloseAsync();
    }

    // Reintento suave del /open: la página no reintentaba nunca (se cerraba y
    // listo), pero el panel nativo puede quedarse abierto mientras el motor
    // termina de levantar. Solo corre mientras NO haya estado.
    private void ArrancarReintento()
    {
        if (_timerReintento != null || !IsVisible) return;
        _timerReintento = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timerReintento.Tick += async (_, _) =>
        {
            if (!IsVisible || _estado != null) { PararReintento(); return; }
            if (_cli == null) return;
            var s = await _cli.OpenAsync().ConfigureAwait(true);
            if (!IsVisible) return;
            AplicarEstado(s);
        };
        _timerReintento.Start();
    }

    private void PararReintento()
    {
        try { _timerReintento?.Stop(); } catch { }
        _timerReintento = null;
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    private void PonerAnchoHerramienta()
    {
        if (_anchoHerrM <= 0) return;   // no-op, igual que el HTML
        _txtDist.Text = ADisplay(_anchoHerrM);
    }

    private async Task ConstruirAsync()
    {
        if (_cli == null || _construyendo) return;

        double d = LeerDistancia();
        if (!(d > 0))
        {
            // Construir con 0 hace una cabecera de CERO metros: sin error y sin
            // nada visible ("no la crea"). Fallback al ancho de herramienta,
            // reflejado en el campo; sin herramienta, se avisa y se aborta.
            if (_anchoHerrM > 0)
            {
                _txtDist.Text = ADisplay(_anchoHerrM);
                d = LeerDistancia();
            }
            else
            {
                Status("poné una distancia en metros primero", "idle");
                Render();
                return;
            }
        }

        // El HTML dejaba doble-clickear el botón mientras volaba el POST.
        _construyendo = true;
        Status("construyendo…", "warn");
        Render();
        var r = await _cli.BuildAsync(d).ConfigureAwait(true);
        _construyendo = false;
        AplicarResultado(r);
    }

    private async Task ResetAsync()
    {
        if (_cli == null) return;
        AplicarResultado(await _cli.ResetAsync().ConfigureAwait(true));
        // Igual que la página: después del reset se descarta cualquier línea de
        // corte residual y se pinta el estado completo que devuelve.
        var s = await _cli.CancelTouchAsync().ConfigureAwait(true);
        if (s != null) AplicarEstado(s);
    }

    private async Task ApagarAsync()
    {
        if (_cli == null) return;
        AplicarResultado(await _cli.OffAsync().ConfigureAwait(true));
    }

    private async Task ToggleSeccionesAsync()
    {
        if (_cli == null) return;
        bool actual = _estado?.IsSectionControlled ?? false;
        var r = await _cli.SetSectionControlledAsync(!actual).ConfigureAwait(true);
        if (r == null)
        {
            // Sin respuesta el toggle NO se mueve: el HTML dejaba el tilde
            // cambiado aunque el POST hubiera fallado (mentía).
            _sinConexion = true;
            Render();
            return;
        }
        _sinConexion = false;
        // La verdad es el bool DEVUELTO, no el enviado.
        if (_estado != null) _estado.IsSectionControlled = r.IsSectionControlled;
        Render();
    }

    // =========================================================================
    //  estado → UI
    // =========================================================================

    private void AplicarEstado(HeadlandStateDto? s)
    {
        if (s == null)
        {
            _sinConexion = true;
            _estado = null;
            Status("sin conexión", "bad");
            _banner = Traductor.T("Sin conexión con PilotX.");
            Render();
            ArrancarReintento();
            return;
        }

        // El motor todavía sin el servicio inyectado (o un build viejo) responde
        // {"ok":false,"error":"service-unavailable"}, que deserializa como un
        // estado con has_field=false: sin este caso, el panel mostraba "creá un
        // contorno" cuando el problema era otro.
        if (s.Error == "service-unavailable")
        {
            _sinConexion = true;
            _estado = null;
            Status("sin conexión", "bad");
            _banner = Amigable("ui-error");
            Render();
            ArrancarReintento();
            return;
        }

        _sinConexion = false;
        PararReintento();

        if (!s.HasField)
        {
            _estado = s;
            _banner = Traductor.T("Primero creá un contorno del lote para poder construir la cabecera.");
            Status("sin contorno", "idle");
            Render();
            return;
        }

        _estado = s;
        _unidades = string.IsNullOrEmpty(s.Units) ? "m" : s.Units!;
        _anchoHerrM = s.ToolWidthM ?? 0;

        // Distancia precargada con el ancho de herramienta: arrancaba en 0 y
        // "Construir" con 0 no dibujaba nada. Solo se precarga si el campo
        // sigue en 0/vacío — nunca se pisa lo que el operario ya tipeó.
        if (_anchoHerrM > 0 && !(LeerDistancia() > 0) && !_txtDist.IsFocused)
            _txtDist.Text = ADisplay(_anchoHerrM);

        _banner = !string.IsNullOrEmpty(s.Error)
            ? Amigable(s.Error)
            : (s.HasBoundary ? "" : Traductor.T("Primero creá un contorno del lote para poder construir la cabecera."));

        Status(s.IsHeadlandOn ? "cabecera activa" : "sin cabecera", s.IsHeadlandOn ? "ok" : "idle");
        Render();
    }

    private void AplicarResultado(HeadlandResultDto? r)
    {
        if (r == null)
        {
            _sinConexion = true;
            Status("sin conexión", "bad");
            _banner = Traductor.T("Sin conexión con PilotX.");
            Render();
            return;
        }
        _sinConexion = false;
        if (_estado != null) _estado.IsHeadlandOn = r.IsHeadlandOn;
        Status(r.IsHeadlandOn ? "cabecera activa" : "sin cabecera", r.IsHeadlandOn ? "ok" : "idle");
        _banner = (!r.Ok && !string.IsNullOrEmpty(r.Error)) ? Amigable(r.Error) : "";
        Render();
    }

    private void Status(string txt, string tipo)
    {
        _status = txt;
        _statusTipo = tipo;
    }

    private void Render()
    {
        // ---- pill de estado ----
        _pillTxt.Text = Traductor.T(_status);
        var color = _statusTipo switch
        {
            "ok"   => Ok,
            "warn" => Warn,
            "bad"  => Rojo,
            _      => Dim,
        };
        _pillDot.Fill = color;
        _pillTxt.Foreground = _statusTipo == "idle" ? TextoMuted : color;

        // ---- banner ----
        _avisoTxt.Text = _banner;
        _avisoBox.IsVisible = !string.IsNullOrEmpty(_banner);

        // ---- unidad ----
        _lblUnidad.Text = _unidades;

        // ---- toggle de secciones ----
        bool sec = _estado?.IsSectionControlled ?? false;
        _btnSecciones.Content = Traductor.T(sec ? "Sí" : "No");
        _btnSecciones.Background = sec ? Verde : BgFila;
        _btnSecciones.Foreground = sec ? Brushes.White : TextoMuted;
        _btnSecciones.BorderBrush = sec ? Verde : Borde;

        // ---- habilitación: sin contorno no hay nada que construir ----
        bool hayContorno = !_sinConexion && (_estado?.HasField ?? false) && (_estado?.HasBoundary ?? false);
        _btnConstruir.IsEnabled = hayContorno && !_construyendo;
        _btnAncho.IsEnabled = hayContorno;
        _btnReset.IsEnabled = hayContorno;
        _btnApagar.IsEnabled = hayContorno;
        // El toggle queda operable siempre (igual que el checkbox del HTML):
        // es una preferencia de secciones, no depende de que haya cabecera.
        _btnSecciones.IsEnabled = !_sinConexion;

        Traductor.Aplicar(this);
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    /// <summary>
    /// Código del wire → texto que el operario entiende. Se portan TODOS los
    /// casos de friendly() de cabecera.js aunque dos sean del flujo slice que
    /// no se porta (cuestan cero y blindan contra una respuesta inesperada), y
    /// se agrega el que el HTML mostraba CRUDO: "offset-collapsed", el error más
    /// probable de esta pantalla (poner una distancia enorme).
    /// </summary>
    private static string Amigable(string? code) => code switch
    {
        null or "" => "",
        "sin-contorno"        => Traductor.T("Primero creá un contorno del lote."),
        "distancia-cero"      => Traductor.T("Con línea curva la distancia no puede ser 0."),
        "cruces"              => Traductor.T("La línea no cruza la cabecera en 2 puntos. Extendé los extremos e intentá de nuevo."),
        "ui-error"            => Traductor.T("PilotX no pudo procesar la acción."),
        "offset-collapsed"    => Traductor.T("La distancia es demasiado grande: la cabecera se come todo el lote. Probá con menos."),
        "service-unavailable" => Traductor.T("PilotX no pudo procesar la acción."),
        "error-interno"       => Traductor.T("PilotX no pudo procesar la acción."),
        _ => code,   // código crudo: peor sería tragárselo
    };

    /// <summary>
    /// Lee el campo de distancia. Invariante + coma decimal: el operario de acá
    /// escribe "12,5" y el parseFloat del HTML lo truncaba en 12 sin avisar.
    /// </summary>
    private double LeerDistancia()
    {
        var txt = (_txtDist.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            || double.IsNaN(d) || double.IsInfinity(d))
            return 0;
        return d;
    }

    /// <summary>Metros → texto en unidades de pantalla, 1 decimal (como el JS).</summary>
    private string ADisplay(double metros)
    {
        double disp = _unidades == "ft" ? metros * 3.28084 : metros;
        return (Math.Round(disp * 10) / 10).ToString(CultureInfo.InvariantCulture);
    }

    private static Button BotonAccion(string texto, bool acento = false)
    {
        return new Button
        {
            Content = texto, Height = 44, FontSize = 13, FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = acento ? Verde : BgFila,
            Foreground = acento ? Brushes.White : Texto,
            BorderBrush = acento ? Verde : Borde,
        };
    }
}
