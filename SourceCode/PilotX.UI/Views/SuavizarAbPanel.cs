// ============================================================================
// SuavizarAbPanel.cs — SUAVIZAR AB nativo, reemplaza pages/suavizar-ab.html en
// la pantalla de la cabina (ex WinForms FormSmoothAB).
//
// Por qué este port importa: es pantalla de LABOR — se abre con el tractor
// adentro del lote — y la VISTA PREVIA NO VIVE EN LA PANTALLA: la curva
// suavizada se dibuja sobre el mapa principal (curve.isSmoothWindowOpen +
// curve.smooList, ver GuidanceDrawExtensions). O sea que la ventana de
// Chromium se paraba justo encima de lo único que hay para mirar mientras se
// sube y baja el nivel. Ahora es una card chica flotante, anclada a la derecha
// como Contorno/Cabecera/Tramlines, con el mapa vivo detrás.
//
// Qué quedó NATIVO: el diálogo entero, con paridad de comportamiento.
//   · Nivel 2..100 con clamp, arranca en 20 en cada apertura (igual que el
//     formulario original: NO hay estado que leer del motor).
//   · − / + mandan un smooth_ab_set_<n> por toque, sin debounce ni auto-repeat
//     (mismo criterio que la página; ver riesgo de ráfaga más abajo).
//   · "Por ahora" (aplica en memoria), "A archivo" (aplica y persiste),
//     "Cancelar" (descarta la preview y NO cierra el panel — paridad).
//   · El pill de estado con sus textos ("—", "en vivo", "sin conexión",
//     "aplicado", "guardado", "cancelado").
//
// Qué SIGUE en HTML: la página wwwroot/pages/suavizar-ab.html, intacta, para el
// Hub remoto / celular / Android. No hay pestaña "Configurar": este diálogo no
// configura nada, es la pantalla entera.
//
// MEJORA sobre la página (M1): cuando el motor rechaza el comando, la página
// solo hacía console.warn — invisible en cabina, el operario apretaba ± y no
// pasaba nada. Acá sale un toast (nunca modal). Para no convertirlo en una
// lluvia de avisos, los rechazos del stepper avisan SOLO en el flanco (el
// primero después de un ok); los tres botones del pie, que son acciones
// deliberadas, avisan siempre.
//
// Salida sin aplicar: la página mandaba smooth_ab_cancel en `pagehide` para no
// dejar la preview colgada dibujada en el mapa. Acá la semántica vive en
// Cerrar() (fire-and-forget) y en DetachEnCierreDeApp() (esperado y acotado: el
// motor es OTRO proceso y sobrevive a la bajada de la pantalla, así que sin ese
// POST la curva suavizada quedaría pintada para siempre).
//
// Wire: el MISMO POST /api/aog/guidance/command de siempre, body {"cmd": "..."}
// (snake_case, sin cambios de contrato). Un solo endpoint ⇒ no hay client
// aparte: sería un archivo para una función. El canal entra por Attach().
//
// OJO — el motor todavía no conoce estos comandos: en esta rama
// GuidanceEngineHost.ExecuteCommand no tiene handlers smooth_ab_*, así que
// devuelve {ok:false} (la página HTML tampoco funciona hoy contra el engine
// headless). Es carril back-end (ver COORDINACION-SESIONES.md); hasta que
// estén, el panel avisa por toast en vez de quedarse mudo.
// ============================================================================

using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public sealed class SuavizarAbPanel : Border
{
    // ---- paleta PilotX (idéntica a TramSimplePanel/ContornoPanel/GuiasPanel) --
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#7A857B"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ok         = new SolidColorBrush(Color.Parse("#3D9A33"));
    private static readonly IBrush Err        = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush Dim        = new SolidColorBrush(Color.Parse("#8A958B"));

    private const string Mono = "Consolas, Courier New, monospace";

    // Mismos límites que MIN/MAX de suavizar-ab.js.
    private const int Min = 2;
    private const int Max = 100;
    private const int Default = 20;

    private static readonly JsonDocumentOptions JsonOpts = new() { AllowTrailingCommas = true };

    private HttpClient? _http;
    private string _base = "";
    private CancellationTokenSource? _cts;

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;

    /// <summary>Aviso corto (lo muestra MainWindow como toast, nunca modal).</summary>
    public event Action<string>? Aviso;

    // ---- estado -------------------------------------------------------------
    private int _level = Default;
    private bool _settled;             // ya hubo apply/save/cancel ok: no re-mandar cancel al salir
    private bool _lastOk = true;       // para no repintar el pill en cada toque (solo flancos)
    private bool _avisoRechazo;        // ya se avisó del rechazo del stepper (anti-lluvia de toasts)
    private bool _cerrada = true;      // el panel arranca cerrado

    // ---- controles ----------------------------------------------------------
    private readonly Ellipse _pillDot;
    private readonly TextBlock _pillTxt;
    private readonly TextBlock _valNivel;
    private readonly Button _btnMenos;
    private readonly Button _btnMas;
    private readonly Button _btnCancelar;
    private readonly Button _btnAplicar;
    private readonly Button _btnGuardar;

    public SuavizarAbPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(14);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        // Ancho FIJO y chico (como la card de 420 del HTML): cada píxel de la
        // card es mapa tapado, y acá el mapa ES la vista previa del suavizado.
        Width = 380;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;

        // ---------- cabecera: título + subtítulo + pill + ✕ ----------
        var titulo = new TextBlock
        {
            Text = "Suavizar AB", FontSize = 16, FontWeight = FontWeight.Bold,
            Foreground = Texto,
        };
        var subtitulo = new TextBlock
        {
            Text = "Suaviza la curva AB activa · vista previa en el mapa",
            FontSize = 10.5, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var tituloCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tituloCol.Children.Add(titulo);
        tituloCol.Children.Add(subtitulo);

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
        var pill = new Border
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
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnCerrar.Click += (_, _) => Cerrar();

        var cabecera = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetColumn(tituloCol, 0);
        Grid.SetColumn(pill, 1);
        Grid.SetColumn(btnCerrar, 2);
        cabecera.Children.Add(tituloCol);
        cabecera.Children.Add(pill);
        cabecera.Children.Add(btnCerrar);

        // ---------- stepper del nivel ----------
        _btnMenos = BotonStepper("−");
        _btnMenos.Click += async (_, _) => await SetNivelAsync(_level - 1);
        _btnMas = BotonStepper("+");
        _btnMas.Click += async (_, _) => await SetNivelAsync(_level + 1);
        _valNivel = new TextBlock
        {
            Text = Default.ToString(CultureInfo.InvariantCulture),
            FontFamily = new FontFamily(Mono), FontSize = 40, FontWeight = FontWeight.Bold,
            Foreground = Verde, MinWidth = 96, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stepper = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stepper.Children.Add(_btnMenos);
        stepper.Children.Add(_valNivel);
        stepper.Children.Add(_btnMas);

        var hint = new TextBlock
        {
            Text = "Más nivel = curva más suave (2 a 100)",
            FontSize = 11.5, Foreground = TextoDim, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        };

        // ---------- pie ----------
        _btnCancelar = BotonPie("Cancelar", acento: false);
        _btnCancelar.Click += async (_, _) => await ResolverAsync("smooth_ab_cancel");
        _btnAplicar = BotonPie("Por ahora", acento: false);
        _btnAplicar.Click += async (_, _) => await ResolverAsync("smooth_ab_apply");
        _btnGuardar = BotonPie("A archivo", acento: true);
        _btnGuardar.Click += async (_, _) => await ResolverAsync("smooth_ab_save");

        var pie = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Margin = new Thickness(0, 14, 0, 0),
        };
        _btnCancelar.Margin = new Thickness(0, 0, 3, 0);
        _btnAplicar.Margin = new Thickness(3, 0, 3, 0);
        _btnGuardar.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(_btnCancelar, 0);
        Grid.SetColumn(_btnAplicar, 1);
        Grid.SetColumn(_btnGuardar, 2);
        pie.Children.Add(_btnCancelar);
        pie.Children.Add(_btnAplicar);
        pie.Children.Add(_btnGuardar);

        // ---------- árbol ----------
        var root = new StackPanel();
        root.Children.Add(cabecera);
        root.Children.Add(stepper);
        root.Children.Add(hint);
        root.Children.Add(pie);
        Child = root;
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del canal HTTP (mismo criterio que Attach de los otros paneles).</summary>
    public void Attach(HttpClient http, string baseUrl)
    {
        _http = http;
        _base = (baseUrl ?? "").TrimEnd('/');
    }

    /// <summary>
    /// Abre el panel. Resetea el nivel a 20 (paridad con FormSmoothAB: no hay
    /// estado que leer) y manda smooth_ab_open, que del lado del motor prende
    /// la preview sobre el mapa. Nada de polling.
    /// </summary>
    public void Abrir()
    {
        if (!_cerrada) return;      // ya abierto: otro open sería otro rebuild de la preview
        _cerrada = false;
        _settled = false;
        _lastOk = true;
        _avisoRechazo = false;
        _level = Default;
        _valNivel.Text = _level.ToString(CultureInfo.InvariantCulture);
        SetPill("—", Dim);
        IsVisible = true;
        Traductor.Aplicar(this);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = AbrirEnMotorAsync();
    }

    private async Task AbrirEnMotorAsync()
    {
        bool? r = await EnviarAsync("smooth_ab_open").ConfigureAwait(true);
        if (_cerrada) return;
        // Rechazo en la apertura = casi siempre "no hay curva AB activa": es el
        // aviso más útil de toda la pantalla, y el que la página se comía.
        if (r == false) AvisarRechazo();
    }

    /// <summary>
    /// Cierra el panel. Si no hubo apply/save/cancel, descarta la preview
    /// (fire-and-forget) para no dejar la curva suavizada colgada en el mapa —
    /// es la semántica del `pagehide` de la página.
    /// </summary>
    public void Cerrar()
    {
        if (_cerrada) { IsVisible = false; return; }
        _cerrada = true;
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
    /// sobrevive a la pantalla: sin este POST la curva suavizada quedaría
    /// dibujada en el mapa para siempre. Por eso el cancel se ESPERA (acotado)
    /// en vez de irse fire-and-forget con el proceso ya muriendo.
    /// </summary>
    public void DetachEnCierreDeApp()
    {
        if (_cerrada) return;
        _cerrada = true;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        if (_settled || _http == null) return;
        try { PostAsync("smooth_ab_cancel", CancellationToken.None).Wait(TimeSpan.FromMilliseconds(800)); }
        catch { }
    }

    private async Task DescartarAsync()
    {
        var http = _http;
        if (http == null) return;
        try { await PostAsync("smooth_ab_cancel", CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    // Un comando por toque, sin debounce ni auto-repeat: paridad exacta con la
    // página. Toques rápidos encolan varios set_<n> y SmoothAB recalcula sobre
    // la curva entera; es el mismo riesgo que ya se acepta en el HTML. Si en
    // cabina molesta, el debounce va acá (nunca en el wire).
    private async Task SetNivelAsync(int destino)
    {
        // Contra el tope el valor no cambia pero el comando SALE igual: es lo
        // que hace setLevel() del JS (clamp + send incondicional). No se
        // inventa acá un "no mandes si no cambió".
        _level = Math.Clamp(destino, Min, Max);
        _valNivel.Text = _level.ToString(CultureInfo.InvariantCulture);

        bool? r = await EnviarAsync("smooth_ab_set_"
            + _level.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);
        if (_cerrada) return;
        // Rechazo del stepper: solo el PRIMERO avisa. Con toques repetidos, un
        // toast por toque taparía el mapa entero.
        if (r == false) AvisarRechazo();
    }

    // "Por ahora" / "A archivo" / "Cancelar": los tres cierran el ciclo del
    // suavizado. Paridad: NINGUNO cierra el panel (lo cierra el operario con la
    // ✕) — el HTML tampoco lo hacía.
    private async Task ResolverAsync(string cmd)
    {
        bool? r = await EnviarAsync(cmd).ConfigureAwait(true);
        if (_cerrada) return;

        if (r == true)
        {
            _settled = true;
            _avisoRechazo = false;
            switch (cmd)
            {
                case "smooth_ab_apply":  SetPill("aplicado", Ok); break;
                case "smooth_ab_save":   SetPill("guardado", Ok); break;
                default:                 SetPill("cancelado", Dim); break;
            }
            return;
        }

        // Acción deliberada del operario: acá el aviso sale SIEMPRE (no es la
        // lluvia del stepper). Nunca modal — ShowDialog traba la cabina.
        if (r == null)
        {
            Aviso?.Invoke(Traductor.T("Sin conexión con PilotX.") + "  (AGP-NET-201)");
            return;
        }
        Aviso?.Invoke(Traductor.T(cmd switch
        {
            "smooth_ab_apply" => "No se pudo aplicar el suavizado: revisá que haya una guía curva activa.",
            "smooth_ab_save"  => "No se pudo guardar el suavizado: revisá que haya una guía curva activa.",
            _                 => "No se pudo cancelar el suavizado.",
        }));
    }

    private void AvisarRechazo()
    {
        if (_avisoRechazo) return;
        _avisoRechazo = true;
        Aviso?.Invoke(Traductor.T(
            "No se pudo suavizar: activá primero una guía curva (AB curva) con puntos suficientes."));
    }

    // =========================================================================
    //  wire
    // =========================================================================

    /// <summary>
    /// Manda un comando y actualiza el pill igual que send() del JS:
    /// true = ok, false = el motor lo rechazó, null = sin conexión.
    /// </summary>
    private async Task<bool?> EnviarAsync(string cmd)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        bool? r;
        try { r = await PostAsync(cmd, ct).ConfigureAwait(true); }
        // TaskCanceledException HEREDA de OperationCanceledException: sin el
        // `when`, un timeout del motor se confundiría con el cierre del panel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch { r = null; }

        if (r == null)
        {
            // Solo en el flanco, igual que lastOk en el JS.
            if (_lastOk) { SetPill("sin conexión", Err); _lastOk = false; }
            return null;
        }
        if (!_lastOk) { SetPill("en vivo", Ok); _lastOk = true; }
        if (r == true) _avisoRechazo = false;   // el próximo rechazo vuelve a avisar
        return r;
    }

    /// <summary>
    /// POST /api/aog/guidance/command con body {"cmd": "..."} — el MISMO
    /// contrato que usa la página (snake_case, clave única). Devuelve el `ok`
    /// del JSON; null si no hubo respuesta usable (sin conexión / HTTP feo).
    /// </summary>
    private async Task<bool?> PostAsync(string cmd, CancellationToken ct)
    {
        var http = _http;
        if (http == null) return null;
        // El comando es un identificador ASCII fijo (smooth_ab_*) + un entero:
        // el JSON se arma a mano sin riesgo de escape.
        string body = "{\"cmd\":\"" + cmd + "\"}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(_base + "/api/aog/guidance/command", content, ct)
                                   .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        // El WebHost del motor devuelve el JSON CON BOM (verificado a mano:
        // `{"ok":false,...}` viene precedido de U+FEFF). Un BOM adelante hace
        // explotar JsonDocument.Parse, y el catch de abajo lo convertiría en
        // "sin conexión" con el motor perfectamente vivo. Se saca acá.
        json = json.TrimStart('\uFEFF', '\u200B').Trim();
        try
        {
            using var doc = JsonDocument.Parse(json, JsonOpts);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("ok", out var ok)) return null;
            return ok.ValueKind == JsonValueKind.True;
        }
        catch { return null; }
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    private void SetPill(string texto, IBrush color)
    {
        _pillTxt.Text = Traductor.T(texto);
        _pillTxt.Foreground = ReferenceEquals(color, Ok) ? Texto : TextoMuted;
        _pillDot.Fill = color;
    }

    private static Button BotonStepper(string texto) => new()
    {
        Content = texto, Width = 88, Height = 88, FontSize = 34, FontWeight = FontWeight.Bold,
        CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
        Background = BgFila, Foreground = Texto, BorderBrush = Borde,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button BotonPie(string texto, bool acento) => new()
    {
        Content = texto, Height = 56, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
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
