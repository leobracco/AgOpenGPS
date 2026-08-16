// ============================================================================
// CorregirPosicionPanel.cs — CORREGIR POSICIÓN nativo, reemplaza
// pages/corregir-posicion.html en la pantalla de la cabina (ex WinForms
// FormShiftPos).
//
// Por qué este port importa: es pantalla de LABOR — se abre con el tractor
// adentro del lote — y lo que hace es MOVER la posición percibida de la
// máquina (corrimiento de deriva GPS norte/este). El operario la toca mirando
// el mapa para ver si la pasada quedó donde tiene que quedar; la ventana de
// Chromium se paraba justo encima de eso. Ahora es una card chica flotante,
// anclada a la derecha como Contorno/Tramlines/Suavizar AB, con el mapa vivo
// detrás.
//
// Qué quedó NATIVO: la pantalla entera, con paridad de comportamiento.
//   · Dos ejes (Norte/Sur y Este/Oeste) en cm, con los MISMOS 4 pasos por eje
//     que la página: −10, −1, +1, +10. Clamp ±9999 (LIMIT del JS).
//   · Modelo de escritura ABSOLUTO, igual que el JS: el botón computa el total
//     nuevo local y manda shift_north_<cm> / shift_east_<cm> — nunca el paso.
//     Es idempotente: si se pierde un POST intermedio, el último que llega
//     deja el motor en el valor que muestra la pantalla.
//   · Pintado OPTIMISTA (el número cambia antes de que conteste el motor) y
//     SIN revert, tal cual el HTML. Un revert peleando con toques rápidos
//     (+1 +1 +1) sería peor que la desincronización.
//   · "Poner en cero" (shift_zero) y el toggle "Mantener corrimiento"
//     (offsets_on / offsets_off), con su Off/On y el verde de fondo del HTML.
//   · Estado inicial por GET /api/aog/shift-pos, con el mismo redondeo y
//     clamp que hacía loadState().
//   · El pill con sus textos: "—", "en vivo", "sin conexión".
//
// Qué SIGUE en HTML: la página wwwroot/pages/corregir-posicion.html, intacta,
// para el Hub remoto / celular / Android. No hay pestaña "Configurar": acá no
// se configura nada, es 100% en vivo.
//
// SIN POLLING, a propósito (la página tampoco poleaba). Un re-GET periódico
// hoy sería PEOR que no tenerlo: mientras el motor no implemente los
// shift_*, el GET devuelve la deriva real (0) y le borraría al operario el
// valor que acaba de marcar, cada medio segundo, sin explicación.
//
// MEJORA sobre la página: cuando el motor RECHAZA el comando, el HTML solo
// hacía console.warn — invisible en cabina: el operario apretaba y el número
// se movía igual, creyendo que la máquina se corrió. Acá el pill pasa a ámbar
// "comando rechazado" y sale UN toast (el primero después de un ok, para no
// tapar el mapa con una lluvia de avisos).
//
// OJO — el motor todavía no conoce estos comandos: en esta rama
// GuidanceEngineHost.ExecuteCommand no tiene handlers shift_north_/shift_east_/
// shift_zero/offsets_on/offsets_off, así que devuelve {ok:false} (la página
// HTML tampoco funciona hoy contra el engine headless). Es carril back-end
// (ver COORDINACION-SESIONES.md); hasta que estén, el panel lo dice en la cara
// en vez de mentir.
//
// Wire: el MISMO GET /api/aog/shift-pos + POST /api/aog/guidance/command de
// siempre (snake_case, sin cambios de contrato) — ver ShiftPosClient.
// ============================================================================

using System;
using System.Globalization;
using System.Threading;
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

public sealed class CorregirPosicionPanel : Border
{
    // ---- paleta PilotX (idéntica a SuavizarAbPanel/ContornoPanel/GuiasPanel) --
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#7A857B"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ok         = new SolidColorBrush(Color.Parse("#3D9A33"));
    private static readonly IBrush Warn       = new SolidColorBrush(Color.Parse("#B98A2E"));
    private static readonly IBrush Err        = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush Dim        = new SolidColorBrush(Color.Parse("#8A958B"));
    // Azul del eje ESTE: única desviación de paleta, y es funcional — distingue
    // de un vistazo qué número es cuál. Viene tal cual del CSS de la página.
    private static readonly IBrush AzulEste   = new SolidColorBrush(Color.Parse("#3D87C6"));

    private const string Mono = "Consolas, Courier New, monospace";

    /// <summary>Mismo LIMIT que corregir-posicion.js.</summary>
    private const int Limite = 9999;

    private ShiftPosClient? _client;
    private CancellationTokenSource? _cts;

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;

    /// <summary>Aviso corto (lo muestra MainWindow como toast, nunca modal).</summary>
    public event Action<string>? Aviso;

    // ---- estado local (espejo del motor, igual que las 3 variables del JS) ----
    private int _north;
    private int _east;
    private bool _offsets;
    private bool _lastOk = true;     // para mover el pill solo en los flancos
    private bool _avisoRechazo;      // ya se avisó del rechazo (anti-lluvia de toasts)
    private bool _cerrada = true;    // el panel arranca cerrado

    // ---- controles ----------------------------------------------------------
    private readonly Ellipse _pillDot;
    private readonly TextBlock _pillTxt;
    private readonly TextBlock _valNorte;
    private readonly TextBlock _valEste;
    private readonly Button _btnOffsets;

    public CorregirPosicionPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(14);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        // Ancho FIJO y acotado: cada píxel de la card es mapa tapado, y esta
        // pantalla se abre justamente PARA mirar el mapa mientras se corrige.
        Width = 420;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;

        // ---------- cabecera: título + subtítulo + pill + ✕ ----------
        var titulo = new TextBlock
        {
            Text = "Corregir posición", FontSize = 16, FontWeight = FontWeight.Bold,
            Foreground = Texto,
        };
        var subtitulo = new TextBlock
        {
            Text = "Corrimiento de deriva GPS · aplica en vivo",
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

        // La página vivía en una ventana con la ✕ del sistema; la card embebida
        // necesita la suya (mínimo 40x40, táctil).
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

        // ---------- los dos ejes ----------
        // Apilados (no lado a lado como en la página ancha del Hub): con 420 de
        // card, dos columnas dejarían los botones de paso por debajo del piso
        // táctil. Acá cada eje se lleva la fila entera y los 4 pasos quedan
        // grandes y separados — es un control que MUEVE la máquina.
        _valNorte = ValorEje(Verde);
        _valEste = ValorEje(AzulEste);

        var cardNorte = CardEje("NORTE / SUR", _valNorte, paso => MoverEje(true, paso));
        var cardEste = CardEje("ESTE / OESTE", _valEste, paso => MoverEje(false, paso));
        cardEste.Margin = new Thickness(0, 10, 0, 0);

        // ---------- pie: cero + toggle ----------
        var btnCero = new Button
        {
            Content = "Poner en cero", Height = 52, FontSize = 13, FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = BgFila, Foreground = Texto, BorderBrush = Borde,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnCero.Click += async (_, _) => await PonerEnCeroAsync();

        var lblToggle = new TextBlock
        {
            Text = "Mantener corrimiento", FontSize = 11.5, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _btnOffsets = new Button
        {
            Content = "Off", Width = 72, Height = 40, FontSize = 13, FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = BgFila, Foreground = Texto, BorderBrush = Borde,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _btnOffsets.Click += async (_, _) => await AlternarOffsetsAsync();

        var toggleCont = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(lblToggle, 0);
        Grid.SetColumn(_btnOffsets, 1);
        toggleCont.Children.Add(lblToggle);
        toggleCont.Children.Add(_btnOffsets);
        var toggle = new Border
        {
            Child = toggleCont, Background = BgFila, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
        };

        var pie = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 12, 0, 0),
        };
        btnCero.Margin = new Thickness(0, 0, 8, 0);
        btnCero.Width = 150;
        Grid.SetColumn(btnCero, 0);
        Grid.SetColumn(toggle, 1);
        pie.Children.Add(btnCero);
        pie.Children.Add(toggle);

        // ---------- árbol ----------
        var root = new StackPanel();
        root.Children.Add(cabecera);
        root.Children.Add(cardNorte);
        root.Children.Add(cardEste);
        root.Children.Add(pie);
        Child = root;

        // Traductor.Aplicar() guarda el PRIMER texto de cada TextBlock y lo
        // vuelve a escribir en cada pasada: si el idioma cambia con el panel
        // abierto, los números y el pill volverían a "0"/"—". El Post corre
        // DESPUÉS del Aplicar global de MainWindow y repinta lo vivo.
        Traductor.IdiomaCambio += () => Dispatcher.UIThread.Post(() =>
        {
            if (!_cerrada) Render();
        });
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del client (mismo criterio que el resto de los paneles).</summary>
    public void Attach(ShiftPosClient client) => _client = client;

    /// <summary>
    /// Abre el panel. Arranca en 0/0/Off y pinta ANTES de pedir nada (la
    /// pantalla nunca aparece vacía, igual que el render() previo al fetch del
    /// JS), y después trae el estado real con un solo GET.
    /// </summary>
    public void Abrir()
    {
        if (!_cerrada) return;      // ya abierto: no se re-abre
        _cerrada = false;
        _lastOk = true;
        _avisoRechazo = false;
        _north = 0;
        _east = 0;
        _offsets = false;
        SetPill("—", Dim);
        Render();
        IsVisible = true;
        // Aplicar ANTES de traer los valores: si corriera después, el Traductor
        // se guardaría los números vivos como "texto original" del control.
        Traductor.Aplicar(this);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = CargarEstadoAsync(_cts.Token);
    }

    /// <summary>
    /// Cierra el panel. No manda nada al motor: el corrimiento aplicado tiene
    /// que QUEDAR aplicado (es una corrección de deriva, no una vista previa).
    /// </summary>
    public void Cerrar()
    {
        if (_cerrada) { IsVisible = false; return; }
        _cerrada = true;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        IsVisible = false;
        Cerrado?.Invoke();
    }

    /// <summary>Alias del cierre para los puntos que usan el patrón Detach().</summary>
    public void Detach() => Cerrar();

    /// <summary>
    /// Estado inicial (el loadState del JS): un solo GET, con el mismo redondeo
    /// y clamp. Si falla, los valores quedan en 0/0/off y el pill dice
    /// "sin conexión" — exactamente lo que hacía la página.
    /// </summary>
    private async Task CargarEstadoAsync(CancellationToken ct)
    {
        var client = _client;
        if (client == null) { SetPillEnUi("sin conexión", Err); _lastOk = false; return; }

        ShiftPosDto? d;
        try { d = await client.GetAsync(ct).ConfigureAwait(false); }
        // TaskCanceledException HEREDA de OperationCanceledException: sin el
        // `when`, un timeout del motor se confundiría con el cierre del panel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { d = null; }

        if (ct.IsCancellationRequested || _cerrada) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_cerrada) return;
            if (d == null)
            {
                SetPill("sin conexión", Err);
                _lastOk = false;
                return;
            }
            _north = Clamp((int)Math.Round(d.NorthCm ?? 0));
            _east = Clamp((int)Math.Round(d.EastCm ?? 0));
            _offsets = d.OffsetsOn ?? false;
            SetPill("en vivo", Ok);
            _lastOk = true;
            Render();
        });
    }

    // =========================================================================
    //  acciones (modelo ABSOLUTO, igual que el JS)
    // =========================================================================

    private async Task MoverEje(bool norte, int paso)
    {
        string cmd;
        if (norte)
        {
            _north = Clamp(_north + paso);
            cmd = "shift_north_" + _north.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            _east = Clamp(_east + paso);
            cmd = "shift_east_" + _east.ToString(CultureInfo.InvariantCulture);
        }
        Render();   // optimista: el número se mueve antes de la respuesta
        await EnviarAsync(cmd).ConfigureAwait(true);
    }

    private async Task PonerEnCeroAsync()
    {
        _north = 0;
        _east = 0;
        Render();
        await EnviarAsync("shift_zero").ConfigureAwait(true);
    }

    private async Task AlternarOffsetsAsync()
    {
        _offsets = !_offsets;
        Render();
        await EnviarAsync(_offsets ? "offsets_on" : "offsets_off").ConfigureAwait(true);
    }

    /// <summary>
    /// Manda el comando y mueve el pill igual que el send() del JS, más el
    /// estado ámbar de rechazo que el HTML escondía en la consola.
    /// </summary>
    private async Task EnviarAsync(string cmd)
    {
        var client = _client;
        var ct = _cts?.Token ?? CancellationToken.None;
        if (client == null)
        {
            if (_lastOk) { SetPill("sin conexión", Err); _lastOk = false; }
            return;
        }

        bool? r;
        try { r = await client.SendCommandAsync(cmd, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { r = null; }

        if (_cerrada) return;

        if (r == null)
        {
            if (_lastOk) { SetPill("sin conexión", Err); _lastOk = false; }
            return;
        }
        if (!_lastOk) { SetPill("en vivo", Ok); _lastOk = true; }

        if (r == true)
        {
            SetPill("en vivo", Ok);
            _avisoRechazo = false;   // el próximo rechazo vuelve a avisar
            return;
        }

        // Rechazado por el motor: el número YA se movió en pantalla y la máquina
        // no se movió. Decirlo, que es justo lo que la página se comía.
        SetPill("comando rechazado", Warn);
        if (_avisoRechazo) return;
        _avisoRechazo = true;
        Aviso?.Invoke(Traductor.T(
            "PilotX no aplicó el corrimiento: el motor rechazó el comando. El número de la pantalla no es la posición real."));
    }

    // =========================================================================
    //  pintado
    // =========================================================================

    private void Render()
    {
        _valNorte.Text = _north.ToString(CultureInfo.InvariantCulture);
        _valEste.Text = _east.ToString(CultureInfo.InvariantCulture);
        // Off/On con el semáforo del HTML (#btnOffsets.on = fondo acento): es el
        // único verde de fondo del panel y replica el original.
        _btnOffsets.Content = Traductor.T(_offsets ? "On" : "Off");
        _btnOffsets.Background = _offsets ? Verde : BgFila;
        _btnOffsets.Foreground = _offsets ? Brushes.White : Texto;
        _btnOffsets.BorderBrush = _offsets ? Verde : Borde;
    }

    private void SetPill(string texto, IBrush color)
    {
        _pillTxt.Text = Traductor.T(texto);
        _pillTxt.Foreground = ReferenceEquals(color, Ok) ? Texto : TextoMuted;
        _pillDot.Fill = color;
    }

    private void SetPillEnUi(string texto, IBrush color)
        => Dispatcher.UIThread.Post(() => { if (!_cerrada) SetPill(texto, color); });

    // =========================================================================
    //  helpers
    // =========================================================================

    private static int Clamp(int v) => Math.Max(-Limite, Math.Min(Limite, v));

    private static TextBlock ValorEje(IBrush color) => new()
    {
        Text = "0", FontFamily = new FontFamily(Mono), FontSize = 38, FontWeight = FontWeight.Bold,
        Foreground = color, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right, MinWidth = 92,
    };

    /// <summary>Card de un eje: rótulo, valor grande en cm y los 4 pasos.</summary>
    private static Border CardEje(string rotulo, TextBlock valor, Func<int, Task> onPaso)
    {
        var lbl = new TextBlock
        {
            Text = rotulo, FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = TextoDim, VerticalAlignment = VerticalAlignment.Center,
        };
        var unidad = new TextBlock
        {
            Text = "cm", FontSize = 11.5, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 0, 7),
        };

        var valorFila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        valorFila.Children.Add(valor);
        valorFila.Children.Add(unidad);

        var filaTop = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(valorFila, 1);
        filaTop.Children.Add(lbl);
        filaTop.Children.Add(valorFila);

        // Los 4 pasos del HTML, con el mismo signo menos tipográfico (U+2212) en
        // el texto. El número que viaja al motor se arma aparte, con "-" ASCII.
        var pasos = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        (string txt, int paso)[] defs =
        {
            ("−10", -10), ("−1", -1), ("+1", 1), ("+10", 10),
        };
        for (int i = 0; i < defs.Length; i++)
        {
            var b = BotonPaso(defs[i].txt);
            b.Margin = new Thickness(i == 0 ? 0 : 3, 0, i == defs.Length - 1 ? 0 : 3, 0);
            int paso = defs[i].paso;
            b.Click += async (_, _) => await onPaso(paso);
            Grid.SetColumn(b, i);
            pasos.Children.Add(b);
        }

        var col = new StackPanel();
        col.Children.Add(filaTop);
        col.Children.Add(pasos);

        return new Border
        {
            Child = col, Background = BgFila, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 10),
        };
    }

    // 56 de alto y ~86 de ancho: piso táctil de la guía (64x56) con aire entre
    // botones — acá un toque de más corre la máquina 10 cm.
    private static Button BotonPaso(string texto) => new()
    {
        Content = texto, Height = 56, FontSize = 17, FontWeight = FontWeight.Bold,
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = BgPanel, Foreground = Texto, BorderBrush = Borde,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };
}
