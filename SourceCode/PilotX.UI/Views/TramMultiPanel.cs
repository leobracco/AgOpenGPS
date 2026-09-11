// ============================================================================
// TramMultiPanel.cs — "TRAMLINES (multi)" NATIVO, reemplaza pages/tramlines.html
// en la pantalla de la cabina (ex WinForms FormTramLine).
//
// Por qué el port: es pantalla de LABOR — se abre con el lote abierto y el
// tractor adentro — y hasta hoy era una ventana de Chromium de 460x470 encima
// del mapa, con el lienzo del tamaño de un sello justo cuando lo que se hace
// acá es TOCAR el dibujo (el corte de 3 pasos se marca con el dedo sobre las
// huellas). Ahora es una card clara flotante con su propio lienzo y el mapa GL
// sigue vivo detrás.
//
// Qué quedó NATIVO — la pantalla entera, con paridad de comportamiento:
//   · el lienzo interactivo (contornos, huellas perimetrales, guías, trams
//     guardados y de preview, puntos A/B) con mover, acercar y tocar,
//   · elegir la guía (◀ ▶), cambiar de lado (⇄ Lado),
//   · pasadas (piso 1) y pasada de inicio (piso 0),
//   · tram exterior (crea/borra las huellas perimetrales),
//   · Agregar líneas (pasa el preview a guardados),
//   · el corte de 3 toques (A · B · lado a eliminar) y "Descartar toque",
//   · opacidad de los guardados (paso 0.1, rango 0.2 … 1),
//   · Borrar todos, Guardar y salir, Salir sin tramlines,
//   · los tres anchos de solo lectura (trocha / tram / implemento).
//
// Qué se AGREGÓ (no estaba en el HTML, y no cambia ningún contrato):
//   · botones acercar / alejar / encuadrar en el lienzo: en la pantalla de la
//     cabina no hay rueda de mouse y sin pinza multitáctil no había zoom;
//   · CONFIRMACIÓN de dos toques en las dos acciones destructivas (ver abajo);
//   · la opacidad se muestra en % (el HTML la cambiaba a ciegas).
//
// LAS DOS ACCIONES DESTRUCTIVAS — y por qué llevan confirmación:
//   1. "Cancelar" del HTML NO era un deshacer. Tram_CancelSession() borra el
//      preview Y LOS TRAMS GUARDADOS (los que el operario ya tenía de ANTES de
//      entrar), apaga el displayMode y PERSISTE el borrado. Un operario que
//      entraba a mirar y tocaba "Cancelar" perdía el trabajo hecho. El contrato
//      del motor NO se toca en este porteo (sería otro cambio, y este es un
//      port): se rotula con honestidad — "Salir sin tramlines" — y se pide
//      confirmación explicando qué se pierde.
//   2. "Borrar todos" tampoco confirmaba nada. Mismo tratamiento.
//   Las dos confirmaciones son un Border con IsVisible ADENTRO de la card:
//   nunca un Flyout (no se dibujan sobre el mapa GL) ni un ShowDialog modal
//   (traba la cabina).
//
// Qué NO se portó y por qué: keyboard.js (esta pantalla no tiene NINGÚN campo
// de texto — solo botones y steppers), i18n.js (lo reemplaza Traductor), el
// sendBeacon de `pagehide` con su flag `closed` (en nativo el ciclo de vida es
// determinístico; la INTENCIÓN se conserva: los cierres laterales mandan
// /close best-effort) y el postMessage 'close-hub' (acá el host escucha el
// evento Cerrado).
//
// Qué SIGUE en HTML: pages/tramlines.html + js/tramlines.js, INTACTOS, para el
// Hub remoto / celular / Android. OJO: el editor del motor NO es thread-safe y
// hay UNA sola sesión global — abrir la página desde el celular con el panel
// nativo abierto se pisa (ya pasaba entre dos browsers; no es del porteo).
//
// TAMAÑO: 720x560. Más grande que la ventana HTML (460x470) porque el lienzo
// necesita área para tocar con el dedo, y bastante más chica que la pantalla:
// en la de 10" (1080x720) queda mapa a la vista por los cuatro costados.
//
// SIN POLLING, a propósito: cada POST devuelve el estado COMPLETO reconstruido.
// Y la única fuente de verdad es ese DTO — /swap, /outer y /delete-all resetean
// passes/start del lado del motor, así que un eco local optimista haría mentir
// a los steppers.
//
// Wire: el MISMO /api/tramlines/* de siempre (ver TramMultiClient).
// ============================================================================

using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public sealed class TramMultiPanel : Border
{
    // ---- paleta PilotX (idéntica a Contorno/Cabecera/Guías/TramSimple) ------
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush BgError    = new SolidColorBrush(Color.Parse("#FBECEC"));
    private static readonly IBrush TextoError = new SolidColorBrush(Color.Parse("#B33F3A"));
    // Velo del cartel de confirmación: la card se ve abajo, pero apagada.
    private static readonly IBrush Velo       = new SolidColorBrush(Color.Parse("#E8F5F7F4"));

    private const string Mono = "Consolas, Courier New, monospace";

    private TramMultiClient? _cli;

    /// <summary>El panel se cerró (por cualquier vía).</summary>
    public event Action? Cerrado;

    /// <summary>Aviso corto (lo muestra MainWindow como toast, nunca modal).</summary>
    public event Action<string>? Aviso;

    // ---- estado -------------------------------------------------------------
    private TramMultiState? _st;
    private bool _abierta;        // hay sesión viva en el motor
    private bool _salio;          // /close o /cancel ya salió (tiene que salir UNA vez)
    private bool _enVuelo;        // POST en curso: se ignoran toques nuevos (no desordenar el 3-tap)
    private Action? _confirmar;   // qué ejecuta el cartel de confirmación si dice que sí

    // ---- controles ----------------------------------------------------------
    private readonly TextBlock _hint;
    private readonly TramMultiLienzo _lienzo;
    private readonly Border _avisoBox;
    private readonly TextBlock _avisoTxt;

    private readonly TextBlock _lblSel;
    private readonly Button _btnPrev;
    private readonly Button _btnNext;
    private readonly Button _btnSwap;
    private readonly Button _btnPassesDn;
    private readonly Button _btnPassesUp;
    private readonly TextBlock _valPasses;
    private readonly Button _btnStartDn;
    private readonly Button _btnStartUp;
    private readonly TextBlock _valStart;
    private readonly Button _btnOuter;
    private readonly Button _btnAgregar;
    private readonly Button _btnDescartarToque;
    private readonly TextBlock _lblAlpha;
    private readonly Button _btnAlphaDn;
    private readonly Button _btnAlphaUp;
    private readonly Button _btnBorrarTodos;
    private readonly TextBlock _valTrocha;
    private readonly TextBlock _valTram;
    private readonly TextBlock _valImpl;
    private readonly Button _btnSalirSinTram;
    private readonly Button _btnGuardar;

    private readonly Border _confirmBox;
    private readonly TextBlock _confirmTitulo;
    private readonly TextBlock _confirmTexto;
    private readonly Button _confirmSi;

    public TramMultiPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        Width = 720;
        Height = 560;
        IsVisible = false;

        // ---------- cabecera: título + hint dinámico + ✕ ----------
        var titulo = new TextBlock
        {
            Text = "Tramlines", FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
        };
        _hint = new TextBlock
        {
            Text = "Elegí una guía · ajustá pasadas · Agregar",
            FontSize = 11, Foreground = TextoMuted, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0),
        };
        // La ✕ es la salida que GUARDA, igual que el `pagehide` de la página
        // (mandaba /close, no /cancel). La salida destructiva es el botón
        // rotulado, abajo, y encima confirma.
        var btnCerrar = new Button
        {
            Content = "✕", Width = 44, Height = 40, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnCerrar.Click += (_, _) => _ = GuardarYSalirAsync();

        var cabecera = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetColumn(titulo, 0);
        Grid.SetColumn(_hint, 1);
        Grid.SetColumn(btnCerrar, 2);
        cabecera.Children.Add(titulo);
        cabecera.Children.Add(_hint);
        cabecera.Children.Add(btnCerrar);

        // ---------- lienzo + banner + botones de zoom ----------
        _lienzo = new TramMultiLienzo();
        _lienzo.Tocado += (e, n) => _ = EjecutarAsync(c => c.TapAsync(e, n));

        _avisoTxt = new TextBlock
        {
            Text = "", FontSize = 12, Foreground = TextoError, TextWrapping = TextWrapping.Wrap,
        };
        // El HTML pintaba el aviso con fondo #5a2c2c (banner oscuro): va contra
        // la paleta clara de PilotX. Chip rojo suave estándar.
        _avisoBox = new Border
        {
            Child = _avisoTxt, Background = BgError, BorderBrush = Rojo,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(6, 6, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,          // el banner no se come los toques del lienzo
            IsVisible = false,
        };

        var btnMas = BotonLienzo("+");
        btnMas.Click += (_, _) => _lienzo.Zoom(1.25);
        var btnMenos = BotonLienzo("−");
        btnMenos.Click += (_, _) => _lienzo.Zoom(1 / 1.25);
        var btnEncuadrar = BotonLienzo("Todo");
        btnEncuadrar.Width = 44;
        btnEncuadrar.FontSize = 11;
        btnEncuadrar.Click += (_, _) => _lienzo.EncuadrarAhora();

        var zoomBox = new StackPanel
        {
            Orientation = Orientation.Vertical, Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 6),
        };
        zoomBox.Children.Add(btnMas);
        zoomBox.Children.Add(btnMenos);
        zoomBox.Children.Add(btnEncuadrar);

        var capas = new Panel();
        capas.Children.Add(_lienzo);
        capas.Children.Add(zoomBox);
        capas.Children.Add(_avisoBox);

        var marcoLienzo = new Border
        {
            Child = capas, Background = BgFila, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0),
        };

        // ---------- columna de controles ----------
        _lblSel = new TextBlock
        {
            Text = "Sin líneas", FontFamily = new FontFamily(Mono), FontSize = 11,
            Foreground = TextoMuted, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _btnPrev = BotonChico("◀");
        _btnPrev.Click += (_, _) => _ = EjecutarAsync(c => c.CycleAsync(-1));
        _btnNext = BotonChico("▶");
        _btnNext.Click += (_, _) => _ = EjecutarAsync(c => c.CycleAsync(1));
        _btnSwap = BotonChico("⇄ Lado");
        _btnSwap.Click += (_, _) => _ = EjecutarAsync(c => c.SwapAsync());
        var filaGuia = FilaBotones(_btnPrev, _btnNext, _btnSwap);

        _btnPassesDn = BotonStepper("−");
        _btnPassesDn.Click += (_, _) =>
            _ = EjecutarAsync(c => c.SetPassesAsync(Math.Max(1, (_st?.Passes ?? 2) - 1)));
        _btnPassesUp = BotonStepper("+");
        _btnPassesUp.Click += (_, _) =>
            _ = EjecutarAsync(c => c.SetPassesAsync((_st?.Passes ?? 2) + 1));
        _valPasses = ValorStepper("2");
        var filaPasadas = FilaStepper("Pasadas", _btnPassesDn, _valPasses, _btnPassesUp);

        _btnStartDn = BotonStepper("−");
        _btnStartDn.Click += (_, _) =>
            _ = EjecutarAsync(c => c.SetStartPassAsync(Math.Max(0, (_st?.StartPass ?? 0) - 1)));
        _btnStartUp = BotonStepper("+");
        _btnStartUp.Click += (_, _) =>
            _ = EjecutarAsync(c => c.SetStartPassAsync((_st?.StartPass ?? 0) + 1));
        _valStart = ValorStepper("0");
        var filaInicio = FilaStepper("Inicio", _btnStartDn, _valStart, _btnStartUp);

        // Pill Sí/No en vez del checkbox de 18 px del HTML: mismo dato, mucho
        // más dedo (mismo criterio que "Secciones controladas" en Cabecera).
        var lblOuter = new TextBlock
        {
            Text = "Tram exterior", FontSize = 11.5, Foreground = Texto,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        _btnOuter = new Button
        {
            // 40 de alto como todo lo que se toca en cabina (era 34: con guante
            // y el tractor moviéndose, ese pill se erraba).
            Content = "No", Height = 40, MinWidth = 56, FontSize = 12, FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1),
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _btnOuter.Click += (_, _) =>
            _ = EjecutarAsync(c => c.SetOuterAsync(!(_st?.IsOuter ?? false)));
        var filaOuter = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetColumn(lblOuter, 0);
        Grid.SetColumn(_btnOuter, 1);
        filaOuter.Children.Add(lblOuter);
        filaOuter.Children.Add(_btnOuter);

        _btnAgregar = BotonAccion("Agregar líneas", acento: true);
        _btnAgregar.Click += (_, _) => _ = EjecutarAsync(c => c.AddAsync());

        _btnDescartarToque = BotonAccion("Descartar toque");
        _btnDescartarToque.Click += (_, _) => _ = EjecutarAsync(c => c.CancelTouchAsync());

        _lblAlpha = Etiqueta("Opacidad de los guardados: —");
        _btnAlphaDn = BotonChico("Opac −");
        _btnAlphaDn.Click += (_, _) => _ = EjecutarAsync(
            c => c.SetAlphaAsync(Math.Max(0.2, (_st?.Alpha ?? 1) - 0.1)));
        _btnAlphaUp = BotonChico("Opac +");
        _btnAlphaUp.Click += (_, _) => _ = EjecutarAsync(
            c => c.SetAlphaAsync(Math.Min(1.0, (_st?.Alpha ?? 1) + 0.1)));
        var filaAlpha = FilaBotones(_btnAlphaDn, _btnAlphaUp);

        _btnBorrarTodos = BotonAccion("Borrar todos", peligro: true);
        _btnBorrarTodos.Click += (_, _) => PedirConfirmacion(
            "Borrar todos los tramlines",
            "Se borran las huellas guardadas, las del preview y el tram exterior. No se puede deshacer.",
            "Sí, borrar todos",
            () => _ = EjecutarAsync(c => c.DeleteAllAsync()));

        _valTrocha = ValorInfo();
        _valTram = ValorInfo();
        _valImpl = ValorInfo();
        // El HTML metía los tres anchos en UNA línea monoespaciada de 11 px
        // ("Track: … · Tram: … · Impl: …") que en una columna angosta se cortaba
        // con puntos suspensivos. Mismo dato, una línea cada uno, y "Track"
        // pasa a llamarse lo que es: la trocha del vehículo.
        var info = new StackPanel();
        info.Children.Add(FilaInfo("Trocha", _valTrocha));
        info.Children.Add(FilaInfo("Tram", _valTram));
        info.Children.Add(FilaInfo("Implemento", _valImpl));

        var listaCtrl = new StackPanel { Spacing = 4 };
        listaCtrl.Children.Add(Etiqueta("Línea de guiado"));
        listaCtrl.Children.Add(_lblSel);
        listaCtrl.Children.Add(filaGuia);
        listaCtrl.Children.Add(Separador());
        listaCtrl.Children.Add(filaPasadas);
        listaCtrl.Children.Add(filaInicio);
        listaCtrl.Children.Add(filaOuter);
        listaCtrl.Children.Add(_btnAgregar);
        listaCtrl.Children.Add(Separador());
        listaCtrl.Children.Add(Etiqueta("Cortar: 1· A · 2· B · 3· lado a eliminar"));
        listaCtrl.Children.Add(_btnDescartarToque);
        listaCtrl.Children.Add(Separador());
        listaCtrl.Children.Add(_lblAlpha);
        listaCtrl.Children.Add(filaAlpha);
        listaCtrl.Children.Add(_btnBorrarTodos);
        listaCtrl.Children.Add(Separador());
        listaCtrl.Children.Add(info);

        var scroll = new ScrollViewer
        {
            Content = listaCtrl,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 2, 0),
        };

        // Las salidas van SIEMPRE abajo de todo, fuera del scroll (el
        // margin-top:auto del HTML): no se buscan, se encuentran.
        _btnSalirSinTram = BotonAccion("Salir sin tramlines", peligro: true);
        _btnSalirSinTram.Content = new TextBlock
        {
            Text = "Salir sin tramlines", FontSize = 11.5, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        _btnSalirSinTram.Click += (_, _) => PedirConfirmacion(
            "Salir sin tramlines",
            "Esto NO deshace los cambios: borra TODAS las huellas del lote, incluso las que ya estaban guardadas antes de entrar. Para salir conservando lo que hay, usá Guardar.",
            "Sí, salir sin tramlines",
            () => _ = CancelarYSalirAsync());

        _btnGuardar = BotonAccion("Guardar", acento: true);
        _btnGuardar.Click += (_, _) => _ = GuardarYSalirAsync();

        var pie = FilaBotones(_btnSalirSinTram, _btnGuardar);
        pie.Margin = new Thickness(0, 6, 0, 0);

        var lateral = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(pie, 1);
        lateral.Children.Add(scroll);
        lateral.Children.Add(pie);

        var cuerpo = new Grid { ColumnDefinitions = new ColumnDefinitions("*,248") };
        Grid.SetColumn(marcoLienzo, 0);
        Grid.SetColumn(lateral, 1);
        cuerpo.Children.Add(marcoLienzo);
        cuerpo.Children.Add(lateral);

        var raiz = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(cabecera, 0);
        Grid.SetRow(cuerpo, 1);
        raiz.Children.Add(cabecera);
        raiz.Children.Add(cuerpo);

        // ---------- cartel de confirmación (Border + IsVisible, NUNCA modal) ----------
        _confirmTitulo = new TextBlock
        {
            Text = "", FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Texto,
            TextWrapping = TextWrapping.Wrap,
        };
        _confirmTexto = new TextBlock
        {
            Text = "", FontSize = 12, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        };
        var confirmNo = BotonAccion("No, volver");
        confirmNo.Click += (_, _) => CerrarConfirmacion();
        _confirmSi = BotonAccion("Sí", peligro: true);
        _confirmSi.Click += (_, _) =>
        {
            var accion = _confirmar;
            CerrarConfirmacion();
            accion?.Invoke();
        };
        var confirmPie = FilaBotones(confirmNo, _confirmSi);

        var confirmCol = new StackPanel();
        confirmCol.Children.Add(_confirmTitulo);
        confirmCol.Children.Add(_confirmTexto);
        confirmCol.Children.Add(confirmPie);

        var confirmCard = new Border
        {
            Child = confirmCol, Background = BgFila, BorderBrush = Rojo,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14), Width = 380,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 6 20 0 #33101612"),
        };
        _confirmBox = new Border
        {
            Child = confirmCard, Background = Velo, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var pila = new Panel();
        pila.Children.Add(raiz);
        pila.Children.Add(_confirmBox);
        Child = pila;

        ResetVista();
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del canal HTTP (mismo criterio que los otros paneles).</summary>
    public void Attach(TramMultiClient cli) => _cli = cli;

    /// <summary>
    /// Abre la sesión de edición (POST /open) y encuadra el lote. El /open del
    /// motor guarda copias temporales de las guías: va SIEMPRE apareado con un
    /// /close o un /cancel, que es lo que garantiza Cerrar().
    /// </summary>
    public async void Abrir()
    {
        // Guard anti doble-Attach: un segundo /open reconstruye la sesión desde
        // cero y se lleva puesto el preview que el operario venía cortando.
        if (_abierta) { IsVisible = true; return; }
        _abierta = true;
        _salio = false;
        _enVuelo = false;
        _st = null;
        CerrarConfirmacion();
        ResetVista();
        _lienzo.SetState(null, conservarVista: false);
        IsVisible = true;
        // OJO CON EL ORDEN — Traductor.Aplicar CACHEA el texto de cada control
        // la primera vez que lo ve y en CADA pasada siguiente le vuelve a
        // escribir ESE texto. Por eso va ACÁ y una sola vez: traduce lo FIJO
        // (títulos, etiquetas, botones) y lo VIVO (la guía elegida, pasadas,
        // inicio, los tres anchos, la opacidad y el Sí/No del tram exterior) lo
        // escribe Aplicar() DESPUÉS. Al revés, el panel abría mintiendo: la
        // pasada de inicio clavada en 0 con el motor en 1, los anchos en "—" y
        // el pill del tram exterior verde pero rotulado "No".
        Traductor.Aplicar(this);

        if (_cli == null) return;
        var s = await _cli.OpenAsync().ConfigureAwait(true);
        // Con el motor caído el /open tarda hasta 6 s: si en el medio el
        // operario cerró la card (o la cerró otro panel), lo que vuelve se tira.
        if (!IsVisible || !_abierta) return;
        Aplicar(s, conservarVista: false);
    }

    /// <summary>
    /// Cierre LATERAL (✕ del host, otro overlay que se abre, cambio de lote):
    /// manda /close best-effort. /close es la salida que GUARDA — la
    /// destructiva es siempre un toque explícito del operario, jamás un efecto
    /// colateral de abrir otra pantalla.
    /// </summary>
    public void Cerrar()
    {
        if (_abierta && !_salio)
        {
            _salio = true;
            var cli = _cli;
            if (cli != null) _ = CerrarSesionAsync(cli, guardar: true);
        }
        _abierta = false;
        _enVuelo = false;
        CerrarConfirmacion();
        IsVisible = false;
        Cerrado?.Invoke();
    }

    /// <summary>Alias del cierre para los puntos que usan el patrón Detach().</summary>
    public void Detach() => Cerrar();

    /// <summary>
    /// Cierre con la app bajando: acá el /close se ESPERA (acotado). El motor es
    /// OTRO proceso y sobrevive a la bajada de la pantalla — un fire-and-forget
    /// sale con el proceso ya muriendo y el Tram.txt recién armado no se guarda,
    /// además de dejar la sesión colgada del lado del motor.
    /// </summary>
    public void DetachEnCierreDeApp()
    {
        if (!_abierta || _salio) return;
        _salio = true;
        _abierta = false;
        try { _cli?.CloseAsync().Wait(TimeSpan.FromMilliseconds(800)); } catch { }
    }

    /// <summary>Guardar y salir: POST /close (persiste Tram.txt y la opacidad).</summary>
    public async Task GuardarYSalirAsync()
    {
        var cli = _cli;
        if (cli != null && _abierta && !_salio)
        {
            _salio = true;
            bool ok = await CerrarSesionAsync(cli, guardar: true).ConfigureAwait(true);
            if (!ok) Aviso?.Invoke(Traductor.T("No se pudo guardar los tramlines."));
        }
        _abierta = false;
        _enVuelo = false;
        CerrarConfirmacion();
        IsVisible = false;
        Cerrado?.Invoke();
    }

    /// <summary>
    /// Salir sin tramlines: POST /cancel. DESTRUCTIVO — borra también las
    /// huellas guardadas de antes y persiste el borrado. Solo se llega acá
    /// después del cartel de confirmación.
    /// </summary>
    public async Task CancelarYSalirAsync()
    {
        var cli = _cli;
        if (cli != null && _abierta && !_salio)
        {
            _salio = true;
            bool ok = await CerrarSesionAsync(cli, guardar: false).ConfigureAwait(true);
            if (!ok) Aviso?.Invoke(Traductor.T("No se pudo cerrar la sesión de tramlines."));
        }
        _abierta = false;
        _enVuelo = false;
        CerrarConfirmacion();
        IsVisible = false;
        Cerrado?.Invoke();
    }

    private static async Task<bool> CerrarSesionAsync(TramMultiClient cli, bool guardar)
    {
        try { return guardar ? await cli.CloseAsync().ConfigureAwait(false)
                             : await cli.CancelAsync().ConfigureAwait(false); }
        catch { return false; }
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    /// <summary>
    /// Corre un POST del wire y pinta con lo que vuelve. Mientras hay uno en
    /// vuelo se ignora todo lo demás: dos /tap seguidos desordenan el corte de
    /// 3 pasos (y el tercero borra el lado equivocado del lote).
    /// </summary>
    private async Task EjecutarAsync(Func<TramMultiClient, Task<TramMultiState?>> accion)
    {
        var cli = _cli;
        if (cli == null || _enVuelo || !IsVisible || !_abierta) return;
        _enVuelo = true;
        ActualizarHabilitados();
        try
        {
            var s = await accion(cli).ConfigureAwait(true);
            if (!IsVisible || !_abierta) return;
            Aplicar(s, conservarVista: true);
        }
        catch { Avisar(Traductor.T("Sin conexión con PilotX.") + "  (AGP-NET-201)"); }
        finally
        {
            _enVuelo = false;
            ActualizarHabilitados();
        }
    }

    private void PedirConfirmacion(string titulo, string texto, string etiquetaSi, Action accion)
    {
        if (_enVuelo) return;
        _confirmar = accion;
        _confirmTitulo.Text = Traductor.T(titulo);
        _confirmTexto.Text = Traductor.T(texto);
        _confirmSi.Content = new TextBlock
        {
            Text = Traductor.T(etiquetaSi), FontSize = 11.5, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        _confirmBox.IsVisible = true;
    }

    private void CerrarConfirmacion()
    {
        _confirmar = null;
        _confirmBox.IsVisible = false;
    }

    // =========================================================================
    //  estado → UI
    // =========================================================================

    private void Aplicar(TramMultiState? s, bool conservarVista)
    {
        // Sin respuesta: banner y NADA más — el dibujo que ya estaba sigue
        // siendo la mejor foto que hay del lote.
        if (s == null) { Avisar(Traductor.T("Sin conexión con PilotX.") + "  (AGP-NET-201)"); return; }
        // ok:false tampoco pisa el estado: el motor dijo que no pudo con ESA
        // acción (bad-json, service-unavailable). Pisarlo vaciaría el lienzo
        // entero por un error puntual — que es lo que hacía el JS.
        if (!s.Ok) { Avisar(Amigable(s.Error)); return; }

        _st = s;
        Avisar(Amigable(s.Error));

        var tracks = s.Tracks;
        int n = tracks?.Count ?? 0;
        bool haySel = s.SelIdx >= 0 && s.SelIdx < n;
        // El NOMBRE de la guía es dato del operario: no se traduce nunca. El
        // modo sí ("ab"/"curve" son tokens del wire, no castellano).
        _lblSel.Text = haySel
            ? (s.SelIdx + 1).ToString(CultureInfo.InvariantCulture) + "/" +
              n.ToString(CultureInfo.InvariantCulture) + " · " + (tracks![s.SelIdx].Name ?? "") +
              " (" + Traductor.T(tracks[s.SelIdx].Mode == "ab" ? "AB" : "curva") + ")"
            : Traductor.T("Sin líneas");

        _valPasses.Text = s.Passes.ToString(CultureInfo.InvariantCulture);
        _valStart.Text = s.StartPass.ToString(CultureInfo.InvariantCulture);
        PintarOuter();
        PintarAlpha();

        string unidad = string.IsNullOrEmpty(s.Units) ? "m" : s.Units!;
        _valTrocha.Text = Fmt(s.TrackWidthDisplay, unidad);
        _valTram.Text   = Fmt(s.TramWidthDisplay, unidad);
        _valImpl.Text   = Fmt(s.ToolWidthDisplay, unidad);

        _hint.Text = s.CutStep switch
        {
            1 => Traductor.T("Tocá el punto B de corte"),
            2 => Traductor.T("Tocá el lado a eliminar"),
            _ => Traductor.T("Elegí una guía · ajustá pasadas · Agregar"),
        };

        _lienzo.SetState(s, conservarVista);
        ActualizarHabilitados();
    }

    private void ResetVista()
    {
        _lblSel.Text = Traductor.T("Sin líneas");
        _valPasses.Text = "2";
        _valStart.Text = "0";
        _valTrocha.Text = "—";
        _valTram.Text = "—";
        _valImpl.Text = "—";
        _lblAlpha.Text = Traductor.T("Opacidad de los guardados") + ": —";
        _hint.Text = Traductor.T("Elegí una guía · ajustá pasadas · Agregar");
        PintarOuter();
        Avisar("");
        ActualizarHabilitados();
    }

    private void ActualizarHabilitados()
    {
        bool libre = !_enVuelo;
        bool hayEstado = _st != null;
        int n = _st?.Tracks?.Count ?? 0;

        // Con UNA sola guía ciclar igual sirve: Tram_CycleTrack limpia el
        // preview y lo vuelve a construir, que es como el operario se saca de
        // encima un corte mal marcado sin tener que cambiar de lado.
        _btnPrev.IsEnabled = _btnNext.IsEnabled = libre && n > 0;
        _btnSwap.IsEnabled = libre && n > 0;
        _btnPassesDn.IsEnabled = _btnPassesUp.IsEnabled = libre && hayEstado;
        _btnStartDn.IsEnabled = _btnStartUp.IsEnabled = libre && hayEstado;
        // Sin contorno el motor no arma NADA (ni el tram exterior ni el preview):
        // los botones que dependen de la geometría quedan grises.
        _btnOuter.IsEnabled = libre && (_st?.HasBoundary ?? false);
        _btnAgregar.IsEnabled = libre && n > 0;
        _btnDescartarToque.IsEnabled = libre && hayEstado;
        _btnAlphaDn.IsEnabled = _btnAlphaUp.IsEnabled = libre && hayEstado;
        _btnBorrarTodos.IsEnabled = libre && hayEstado;
        // Las salidas NUNCA se apagan: son la única forma de cerrar la sesión
        // del motor, y con la sesión colgada el próximo /open arranca sucio.
        _btnSalirSinTram.IsEnabled = true;
        _btnGuardar.IsEnabled = true;
    }

    private void PintarOuter()
    {
        bool on = _st?.IsOuter ?? false;
        _btnOuter.Content = Traductor.T(on ? "Sí" : "No");
        _btnOuter.Background = on ? Verde : BgFila;
        _btnOuter.Foreground = on ? Brushes.White : TextoMuted;
        _btnOuter.BorderBrush = on ? Verde : Borde;
    }

    private void PintarAlpha()
    {
        double a = _st?.Alpha ?? 1;
        if (double.IsNaN(a) || double.IsInfinity(a)) a = 1;
        int pct = (int)Math.Round(a * 100);
        _lblAlpha.Text = Traductor.T("Opacidad de los guardados") + ": " +
                         pct.ToString(CultureInfo.InvariantCulture) + "%";
    }

    private void Avisar(string msg)
    {
        _avisoTxt.Text = msg ?? "";
        _avisoBox.IsVisible = !string.IsNullOrEmpty(msg);
    }

    /// <summary>Código del wire → texto que el operario entiende (el friendly() del JS, ampliado).</summary>
    private static string Amigable(string? err) => err switch
    {
        null or "" => "",
        "sin-contorno" => Traductor.T("Necesitás un contorno para construir tramlines."),
        "sin-guias" => Traductor.T("No hay líneas de guiado AB/Curva visibles."),
        // El JS mostraba estos códigos crudos. Se traducen, pero el código queda
        // a la vista: es lo que sirve para diagnosticar por teléfono.
        "no-state" or "service-unavailable" or "error-interno" or "bad-json"
            => Traductor.T("Sin conexión con PilotX.") + "  (" + err + ")",
        _ => err,   // código desconocido: peor sería tragárselo
    };

    /// <summary>Un decimal + unidad, como el toFixed(1) del JS. Sin estado todavía: "—".</summary>
    private static string Fmt(double v, string unidad)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
        return v.ToString("0.0", CultureInfo.InvariantCulture) + " " + unidad;
    }

    // =========================================================================
    //  helpers de construcción
    // =========================================================================

    private static TextBlock Etiqueta(string texto) => new()
    {
        Text = texto, FontSize = 11, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
    };

    private static Border Separador() => new()
    {
        Height = 1, Background = Borde, Margin = new Thickness(0, 3, 0, 3),
    };

    private static Grid FilaBotones(params Button[] botones)
    {
        var cols = new string[botones.Length];
        for (int i = 0; i < botones.Length; i++) cols[i] = "*";
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", cols)) };
        for (int i = 0; i < botones.Length; i++)
        {
            botones[i].Margin = new Thickness(i == 0 ? 0 : 2, 0, i == botones.Length - 1 ? 0 : 2, 0);
            Grid.SetColumn(botones[i], i);
            g.Children.Add(botones[i]);
        }
        return g;
    }

    /// <summary>Etiqueta a la izquierda y el stepper −/valor/+ a la derecha (mismo patrón que TramSimple).</summary>
    private static Grid FilaStepper(string etiqueta, Button menos, TextBlock valor, Button mas)
    {
        var lbl = new TextBlock
        {
            Text = etiqueta, FontSize = 12, Foreground = Texto,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        var caja = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        caja.Children.Add(menos);
        caja.Children.Add(valor);
        caja.Children.Add(mas);

        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(caja, 1);
        g.Children.Add(lbl);
        g.Children.Add(caja);
        return g;
    }

    private static Grid FilaInfo(string etiqueta, TextBlock valor)
    {
        var lbl = new TextBlock
        {
            Text = etiqueta, FontSize = 11, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 1, 0, 1),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(valor, 1);
        g.Children.Add(lbl);
        g.Children.Add(valor);
        return g;
    }

    private static TextBlock ValorInfo() => new()
    {
        Text = "—", FontFamily = new FontFamily(Mono), FontSize = 11,
        Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right, MinWidth = 70,
    };

    private static TextBlock ValorStepper(string texto) => new()
    {
        Text = texto, FontFamily = new FontFamily(Mono), FontSize = 16, FontWeight = FontWeight.Bold,
        Foreground = Verde, MinWidth = 34, TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button BotonStepper(string texto) => new()
    {
        Content = texto, Width = 44, Height = 42, FontSize = 18, FontWeight = FontWeight.Bold,
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = BgFila, Foreground = Texto, BorderBrush = Borde,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button BotonAccion(string texto, bool acento = false, bool peligro = false) => new()
    {
        Content = texto, Height = 40, FontSize = 12.5, FontWeight = FontWeight.SemiBold,
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = acento ? Verde : peligro ? Rojo : BgFila,
        Foreground = acento || peligro ? Brushes.White : Texto,
        BorderBrush = acento ? Verde : peligro ? Rojo : Borde,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button BotonChico(string texto) => new()
    {
        Content = texto, Height = 40, FontSize = 12, FontWeight = FontWeight.SemiBold,
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = BgFila, Foreground = Texto, BorderBrush = Borde,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button BotonLienzo(string texto) => new()
    {
        Content = texto, Width = 40, Height = 40, FontSize = 16, FontWeight = FontWeight.Bold,
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = BgFila, Foreground = Texto, BorderBrush = Borde,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };
}
