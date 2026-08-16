// ============================================================================
// CabeceraLineasPanel.cs — "Cabecera por líneas" NATIVA (reemplaza
// pages/cabecera-lineas.html en la pantalla de la cabina; ex FormHeadAche).
//
// Por qué el port: es la cabecera de los lotes que NO son un rectángulo — se
// usa con el lote abierto y el tractor adentro, o sea en plena labor, y hasta
// hoy era una ventana de Chromium de 460x470 encima del mapa. Ahora es una card
// clara flotante con su propio lienzo, y el mapa GL sigue vivo detrás.
//
// Qué quedó NATIVO — la pantalla entera, sin recortes:
//   · el lienzo interactivo (contornos, líneas construidas, cabecera armada,
//     puntos A/B) con mover, acercar (rueda y pinza) y tocar para marcar,
//   · tipo de línea Curva/Recta, distancia hacia adentro, "× ancho" (1×/2×/3×),
//   · ciclar líneas (◀ ▶), borrar la seleccionada, A± / B± para que las puntas
//     se crucen, "Descartar toque",
//   · Construir cabecera, Reiniciar, Secciones controladas, Apagar cabecera.
//
// Qué se AGREGÓ (no estaba en el HTML, y no cambia ningún contrato): botones de
// acercar/alejar/encuadrar en el lienzo. En la pantalla de cabina no hay rueda
// de mouse: sin pinza multitáctil el operario se quedaba sin zoom.
//
// Qué NO se portó y por qué: el teclado HTML (acá va el TecladoWindow nativo
// por api/teclado), el devicePixelRatio y el resize a mano del canvas (Avalonia
// es DPI-aware solo), el sendBeacon de `pagehide` (lo reemplaza el /close con
// flag en el cierre del panel) y el postMessage 'close-hub' (acá el host decide
// con el evento Cerrado).
//
// Qué SIGUE en HTML: pages/cabecera-lineas.html + js/cabecera-lineas.js,
// INTACTOS, para el Hub remoto / celular / Android (MainView los sigue
// ruteando).
//
// TAMAÑO: la card es 640x540 — más grande que la ventana HTML (460x470) porque
// el lienzo necesita área para tocar el contorno con el dedo, y bastante más
// chica que la pantalla: en la de 10" (1080x720) queda mapa a la vista por los
// cuatro costados. La regla manda: el mapa no se apaga ni se tapa entero.
//
// OJO con el CIERRE: POST /api/cabecera-lineas/close equivale a cerrar el form
// nativo — guarda las líneas (FileSaveHeadLines) y recalcula isHeadlandOn. Sale
// EXACTAMENTE UNA VEZ por sesión (flag _cerrada) y por TODOS los caminos: ✕,
// Salir, Apagar, "solo un overlay a la vez" y el apagado de la ventana.
//
// Wire: el MISMO /api/cabecera-lineas/* de siempre (ver CabeceraLineasClient).
// Sin polling, a propósito: la geometría solo cambia cuando el operario toca, y
// cada POST vuelve con el estado completo.
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

public sealed class CabeceraLineasPanel : Border
{
    // ---- paleta PilotX (idéntica a Contorno/Cabecera/Guías) ----------------
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush BgError    = new SolidColorBrush(Color.Parse("#FBECEC"));
    private static readonly IBrush TextoError = new SolidColorBrush(Color.Parse("#B33F3A"));

    private const string Mono = "Consolas, Courier New, monospace";

    private CabeceraLineasClient? _cli;

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;

    // ---- estado -------------------------------------------------------------
    private CabLinState? _st;
    private string _modo = "curve";     // "curve" | "ab" — local, solo afecta el próximo toque
    private int _multAncho;             // 0 = sin usar; después cicla 1→2→3→1…
    private bool _cerrada;              // /close ya salió (tiene que salir UNA sola vez)
    private bool _enVuelo;              // hay un POST en curso: se bloquea el doble toque

    // ---- controles ----------------------------------------------------------
    private readonly CabeceraLineasCanvas _lienzo;
    private readonly Border _avisoBox;
    private readonly TextBlock _avisoTxt;

    private readonly Button _btnCurva;
    private readonly Button _btnRecta;
    private readonly TextBlock _lblDist;
    private readonly TextBox _txtDist;
    private readonly Button _btnAncho;
    private readonly TextBlock _lblTool;
    private readonly TextBlock _lblSel;
    private readonly Button _btnPrev;
    private readonly Button _btnNext;
    private readonly Button _btnBorrarLinea;
    private readonly Button _btnAMas;
    private readonly Button _btnAMenos;
    private readonly Button _btnBMas;
    private readonly Button _btnBMenos;
    private readonly Button _btnDescartar;
    private readonly Button _btnConstruir;
    private readonly Button _btnReiniciar;
    private readonly Button _btnSecciones;
    private readonly Button _btnApagar;

    public CabeceraLineasPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        Width = 640;
        Height = 540;
        IsVisible = false;

        // ---------- cabecera: título + ayuda de 3 pasos + ✕ ----------
        var titulo = new TextBlock
        {
            Text = "Cabecera por líneas", FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
        };
        var hint = new TextBlock
        {
            Text = "1· distancia · 2· tocá A y B en el contorno · 3· Construir",
            FontSize = 11, Foreground = TextoMuted, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0),
        };
        var btnCerrar = new Button
        {
            Content = "✕", Width = 44, Height = 40, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // La ✕ es el mismo camino que "Salir": /close y afuera.
        btnCerrar.Click += (_, _) => Cerrar();

        var cabecera = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetColumn(titulo, 0);
        Grid.SetColumn(hint, 1);
        Grid.SetColumn(btnCerrar, 2);
        cabecera.Children.Add(titulo);
        cabecera.Children.Add(hint);
        cabecera.Children.Add(btnCerrar);

        // ---------- lienzo + banner + botones de zoom ----------
        _lienzo = new CabeceraLineasCanvas();
        _lienzo.Tocado += (e, n) => _ = EjecutarAsync(c => c.TapAsync(e, n, _modo, DistanciaPedida()));

        _avisoTxt = new TextBlock
        {
            Text = "", FontSize = 12, Foreground = TextoError, TextWrapping = TextWrapping.Wrap,
        };
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

        // En la pantalla de la cabina no hay rueda de mouse: sin estos botones,
        // el zoom depende de que la pantalla reporte pinza multitáctil.
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
        _btnCurva = BotonSegmento("Curva");
        _btnCurva.Click += (_, _) => PonerModo("curve");
        _btnRecta = BotonSegmento("Recta");
        _btnRecta.Click += (_, _) => PonerModo("ab");

        var segGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(_btnCurva, 0);
        Grid.SetColumn(_btnRecta, 1);
        segGrid.Children.Add(_btnCurva);
        segGrid.Children.Add(_btnRecta);
        var segmento = new Border
        {
            Child = segGrid, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), ClipToBounds = true,
        };

        _lblDist = Etiqueta("Distancia hacia adentro (m)");
        _txtDist = new TextBox
        {
            Text = "0", FontFamily = new FontFamily(Mono), FontSize = 14, Height = 40, MinWidth = 60,
            TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0),
        };
        // El teclado nativo no es automático por foco: se pide a mano con la
        // misma señal HTTP que mandan las páginas.
        _txtDist.GotFocus  += (_, _) => _ = _cli?.TecladoAsync(true);
        _txtDist.LostFocus += (_, _) => _ = _cli?.TecladoAsync(false);

        _btnAncho = BotonChico("× ancho");
        _btnAncho.MinWidth = 78;
        _btnAncho.Click += (_, _) => CiclarAncho();

        var filaDist = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _txtDist.Margin = new Thickness(0, 0, 5, 0);
        Grid.SetColumn(_txtDist, 0);
        Grid.SetColumn(_btnAncho, 1);
        filaDist.Children.Add(_txtDist);
        filaDist.Children.Add(_btnAncho);

        _lblTool = Etiqueta("Implemento: —");

        _lblSel = new TextBlock
        {
            Text = "Sin línea seleccionada", FontFamily = new FontFamily(Mono), FontSize = 11,
            Foreground = TextoMuted, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _btnPrev = BotonChico("◀");
        _btnPrev.Click += (_, _) => _ = EjecutarAsync(c => c.CycleAsync(-1));
        _btnNext = BotonChico("▶");
        _btnNext.Click += (_, _) => _ = EjecutarAsync(c => c.CycleAsync(1));
        // El 🗑 del HTML sale: en Avalonia el emoji depende de la font. La
        // función es la misma — borra la línea seleccionada.
        _btnBorrarLinea = BotonChico("Borrar");
        _btnBorrarLinea.Background = Rojo;
        _btnBorrarLinea.Foreground = Brushes.White;
        _btnBorrarLinea.BorderBrush = Rojo;
        _btnBorrarLinea.Click += (_, _) => _ = EjecutarAsync(c => c.DeleteTrackAsync());

        var filaCiclo = FilaBotones(_btnPrev, _btnNext, _btnBorrarLinea);

        _btnAMas   = BotonChico("A +");
        _btnAMas.Click   += (_, _) => _ = EjecutarAsync(c => c.ExtendAsync("a", true));
        _btnAMenos = BotonChico("A −");
        _btnAMenos.Click += (_, _) => _ = EjecutarAsync(c => c.ExtendAsync("a", false));
        _btnBMas   = BotonChico("B +");
        _btnBMas.Click   += (_, _) => _ = EjecutarAsync(c => c.ExtendAsync("b", true));
        _btnBMenos = BotonChico("B −");
        _btnBMenos.Click += (_, _) => _ = EjecutarAsync(c => c.ExtendAsync("b", false));

        var filaExtender = FilaBotones(_btnAMas, _btnAMenos, _btnBMas, _btnBMenos);

        _btnDescartar = BotonAccion("Descartar toque");
        _btnDescartar.Click += (_, _) => _ = EjecutarAsync(c => c.CancelTouchAsync());

        _btnConstruir = BotonAccion("Construir cabecera", acento: true);
        _btnConstruir.Click += (_, _) => _ = EjecutarAsync(c => c.BuildAsync());

        _btnReiniciar = BotonAccion("Reiniciar");
        _btnReiniciar.Click += (_, _) => _ = EjecutarAsync(c => c.ResetAsync());

        // Pill Sí/No en vez del checkbox del HTML: más dedo, misma información
        // (mismo criterio que el "Cruzar" de Contorno y el toggle de Cabecera).
        var lblSec = new TextBlock
        {
            Text = "Secciones controladas", FontSize = 11.5, Foreground = Texto,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        _btnSecciones = new Button
        {
            Content = "No", Height = 34, MinWidth = 54, FontSize = 12, FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1),
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _btnSecciones.Click += (_, _) => _ = CambiarSeccionesAsync();

        var filaSec = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetColumn(lblSec, 0);
        Grid.SetColumn(_btnSecciones, 1);
        filaSec.Children.Add(lblSec);
        filaSec.Children.Add(_btnSecciones);

        var listaCtrl = new StackPanel { Spacing = 4 };
        listaCtrl.Children.Add(Etiqueta("Tipo de línea"));
        listaCtrl.Children.Add(segmento);
        listaCtrl.Children.Add(_lblDist);
        listaCtrl.Children.Add(filaDist);
        listaCtrl.Children.Add(_lblTool);
        listaCtrl.Children.Add(Separador());
        listaCtrl.Children.Add(_lblSel);
        listaCtrl.Children.Add(filaCiclo);
        listaCtrl.Children.Add(filaExtender);
        listaCtrl.Children.Add(_btnDescartar);
        listaCtrl.Children.Add(Separador());
        listaCtrl.Children.Add(_btnConstruir);
        listaCtrl.Children.Add(_btnReiniciar);
        listaCtrl.Children.Add(filaSec);

        var scroll = new ScrollViewer
        {
            Content = listaCtrl,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 2, 0),
        };

        // "Apagar" y "Salir" van SIEMPRE abajo de todo, fuera del scroll: son
        // las salidas y no se buscan (el margin-top:auto del HTML).
        _btnApagar = BotonAccion("Apagar cabecera", peligro: true);
        // "Apagar cabecera" no entra en media columna en una sola línea: el
        // texto va en un TextBlock que envuelve, no recortado a "Apagar cabec…".
        _btnApagar.Content = new TextBlock
        {
            Text = "Apagar cabecera", FontSize = 11.5, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        _btnApagar.Click += (_, _) => _ = ApagarAsync();
        var btnSalir = BotonAccion("Salir");
        btnSalir.Click += (_, _) => Cerrar();

        // Las dos salidas van en UNA fila: cada píxel de alto que se ahorra
        // acá es un ítem menos que hay que scrollear arriba.
        var pie = FilaBotones(_btnApagar, btnSalir);
        pie.Margin = new Thickness(0, 6, 0, 0);

        var lateral = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(pie, 1);
        lateral.Children.Add(scroll);
        lateral.Children.Add(pie);

        var cuerpo = new Grid { ColumnDefinitions = new ColumnDefinitions("*,214") };
        Grid.SetColumn(marcoLienzo, 0);
        Grid.SetColumn(lateral, 1);
        cuerpo.Children.Add(marcoLienzo);
        cuerpo.Children.Add(lateral);

        var raiz = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(cabecera, 0);
        Grid.SetRow(cuerpo, 1);
        raiz.Children.Add(cabecera);
        raiz.Children.Add(cuerpo);
        Child = raiz;

        PintarModo();
        ActualizarHabilitados();
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del canal HTTP (mismo criterio que los otros paneles).</summary>
    public void Attach(CabeceraLineasClient cli) => _cli = cli;

    /// <summary>
    /// Abre la sesión de edición (POST /open) y encuadra el lote. Los ajustes
    /// locales (tipo de línea, distancia, × ancho) arrancan como en la página:
    /// curva, 0 y sin multiplicador.
    /// </summary>
    public async void Abrir()
    {
        _cerrada = false;
        _enVuelo = false;
        _st = null;
        _modo = "curve";
        _multAncho = 0;
        _txtDist.Text = "0";
        _btnAncho.Content = Traductor.T("× ancho");
        _lblSel.Text = Traductor.T("Sin línea seleccionada");
        _lblTool.Text = Traductor.T("Implemento: —");
        PintarModo();
        PintarSecciones();
        Avisar("");
        _lienzo.SetState(null, conservarVista: false);
        IsVisible = true;
        ActualizarHabilitados();
        Traductor.Aplicar(this);

        if (_cli == null) return;
        var s = await _cli.OpenAsync().ConfigureAwait(true);
        // Con el motor caído el /open tarda hasta 3 s: si en el medio el
        // operario cerró la card (o la cerró otro panel), lo que vuelve se tira.
        if (!IsVisible) return;
        Aplicar(s, conservarVista: false);
        Traductor.Aplicar(this);
    }

    /// <summary>
    /// Cierra el panel: ✕, "Salir" y también los cierres que dispara el host
    /// (abrir otro overlay). El /close sale UNA sola vez — del lado del motor
    /// guarda las líneas y recalcula si la cabecera queda prendida.
    /// </summary>
    public void Cerrar()
    {
        _ = _cli?.TecladoAsync(false);
        if (!_cerrada)
        {
            _cerrada = true;
            _ = _cli?.CloseAsync();
        }
        IsVisible = false;
        Cerrado?.Invoke();
    }

    /// <summary>Alias del cierre para los puntos que usan el patrón Detach().</summary>
    public void Detach() => Cerrar();

    /// <summary>
    /// Cierre con la app bajando: acá el /close se ESPERA (acotado). Un
    /// fire-and-forget sale con el proceso ya muriendo y las líneas recién
    /// dibujadas no llegan a guardarse.
    /// </summary>
    public void DetachEnCierreDeApp()
    {
        if (_cerrada) return;
        _cerrada = true;
        try { _cli?.CloseAsync().Wait(TimeSpan.FromMilliseconds(800)); } catch { }
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    /// <summary>
    /// Corre un POST del wire y pinta con lo que vuelve. Mientras está en vuelo
    /// los botones quedan apagados: la página web dejaba pasar el doble toque y
    /// dos /tap o dos /build seguidos le crean al operario líneas de más.
    /// </summary>
    private async Task EjecutarAsync(Func<CabeceraLineasClient, Task<CabLinState?>> accion)
    {
        var cli = _cli;
        if (cli == null || _enVuelo || !IsVisible) return;
        _enVuelo = true;
        ActualizarHabilitados();
        try
        {
            var s = await accion(cli).ConfigureAwait(true);
            if (!IsVisible) return;
            Aplicar(s, conservarVista: true);
        }
        catch { Avisar(Traductor.T("Sin conexión con PilotX.")); }
        finally
        {
            _enVuelo = false;
            ActualizarHabilitados();
        }
    }

    /// <summary>Apagar cabecera: /off y después el cierre normal (con su /close).</summary>
    private async Task ApagarAsync()
    {
        await EjecutarAsync(c => c.OffAsync()).ConfigureAwait(true);
        Cerrar();
    }

    /// <summary>
    /// Secciones controladas por la cabecera. Se pinta el valor EFECTIVO que
    /// devuelve el motor, no el que se intentó: el editor puede rechazar el
    /// cambio (el JS de la página no lo miraba y quedaba mintiendo hasta el
    /// próximo estado).
    /// </summary>
    private async Task CambiarSeccionesAsync()
    {
        var cli = _cli;
        if (cli == null || _enVuelo) return;
        _enVuelo = true;
        ActualizarHabilitados();
        try
        {
            bool? efectivo = await cli.SetSectionControlledAsync(!(_st?.IsSectionControlled ?? false))
                                      .ConfigureAwait(true);
            if (efectivo == null) { Avisar(Traductor.T("Sin conexión con PilotX.")); return; }
            if (_st != null) _st.IsSectionControlled = efectivo.Value;
            PintarSecciones();
        }
        catch { Avisar(Traductor.T("Sin conexión con PilotX.")); }
        finally
        {
            _enVuelo = false;
            ActualizarHabilitados();
        }
    }

    /// <summary>
    /// "× ancho": cicla 1×, 2×, 3× y carga la distancia con el ancho útil del
    /// implemento (en unidades display, redondeado a un decimal).
    /// </summary>
    private void CiclarAncho()
    {
        if (_st == null) return;
        _multAncho = (_multAncho % 3) + 1;
        double d = Math.Round(_st.ToolWidthDisplay * _multAncho * 10, MidpointRounding.AwayFromZero) / 10;
        _txtDist.Text = d.ToString(CultureInfo.InvariantCulture);
        _btnAncho.Content = Traductor.T("× ancho") + " (" + _multAncho.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>Distancia del campo, con el mismo parseo del JS: coma→punto, y 0 si no es número.</summary>
    private double DistanciaPedida()
    {
        var txt = (_txtDist.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            || double.IsNaN(d) || double.IsInfinity(d)) return 0;
        return d;
    }

    private void PonerModo(string m)
    {
        _modo = m == "ab" ? "ab" : "curve";
        PintarModo();
    }

    // =========================================================================
    //  estado → UI
    // =========================================================================

    private void Aplicar(CabLinState? s, bool conservarVista)
    {
        // Sin respuesta: banner y NADA más — el dibujo que ya estaba sigue
        // siendo la mejor foto que hay del lote.
        if (s == null) { Avisar(Traductor.T("Sin conexión con PilotX.")); return; }
        // ok:false tampoco pisa el estado (el motor dijo por qué no pudo: el
        // punto A y el B eran el mismo, las puntas no se cruzan…). Pisarlo
        // vaciaría el lienzo entero por un error de una acción.
        if (!s.Ok) { Avisar(Amigable(s.Error)); return; }

        _st = s;

        Avisar(!s.JobStarted ? Amigable("sin-lote")
             : !s.HasBoundary ? Amigable("sin-contorno")
             : Amigable(s.Error));

        string unidad = string.IsNullOrEmpty(s.Units) ? "m" : s.Units!;
        _lblDist.Text = Traductor.T("Distancia hacia adentro") + " (" + unidad + ")";
        _lblTool.Text = Traductor.T("Implemento") + ": " +
                        s.ToolWidthDisplay.ToString("F1", CultureInfo.InvariantCulture) + " " + unidad;

        int n = s.Tracks?.Count ?? 0;
        bool haySel = s.SelIdx > -1 && s.Tracks != null && s.SelIdx < n;
        _lblSel.Text = haySel
            ? Traductor.T("Línea") + " " + (s.SelIdx + 1).ToString(CultureInfo.InvariantCulture) + "/" +
              n.ToString(CultureInfo.InvariantCulture) + " · " +
              Traductor.T(s.Tracks![s.SelIdx].Mode == "ab" ? "recta" : "curva")
            : (n > 0
                ? n.ToString(CultureInfo.InvariantCulture) + " " + Traductor.T("líneas · ninguna seleccionada")
                : Traductor.T("Sin líneas todavía"));

        PintarSecciones();
        _lienzo.SetState(s, conservarVista);
        ActualizarHabilitados();
    }

    private void ActualizarHabilitados()
    {
        int n = _st?.Tracks?.Count ?? 0;
        // Con el lote cerrado el motor manda sel_idx = 0 y tracks vacío: el JS
        // habilitaba igual borrar/extender (apuntando a una línea que no
        // existe). Acá se exige que la línea exista de verdad.
        bool haySel = (_st?.SelIdx ?? -1) > -1 && (_st?.SelIdx ?? -1) < n;
        bool libre = !_enVuelo;

        _btnPrev.IsEnabled = _btnNext.IsEnabled = libre && n > 0;
        _btnBorrarLinea.IsEnabled = libre && haySel;
        _btnAMas.IsEnabled = _btnAMenos.IsEnabled = libre && haySel;
        _btnBMas.IsEnabled = _btnBMenos.IsEnabled = libre && haySel;
        _btnDescartar.IsEnabled = libre;
        _btnConstruir.IsEnabled = libre && n >= 2;
        _btnReiniciar.IsEnabled = libre;
        _btnSecciones.IsEnabled = libre;
        _btnApagar.IsEnabled = libre;
        _btnAncho.IsEnabled = libre && _st != null;
    }

    private void PintarModo()
    {
        bool curva = _modo != "ab";
        _btnCurva.Background = curva ? Verde : BgFila;
        _btnCurva.Foreground = curva ? Brushes.White : Texto;
        _btnRecta.Background = curva ? BgFila : Verde;
        _btnRecta.Foreground = curva ? Texto : Brushes.White;
    }

    private void PintarSecciones()
    {
        bool on = _st?.IsSectionControlled ?? false;
        _btnSecciones.Content = Traductor.T(on ? "Sí" : "No");
        _btnSecciones.Background = on ? Verde : BgFila;
        _btnSecciones.Foreground = on ? Brushes.White : TextoMuted;
        _btnSecciones.BorderBrush = on ? Verde : Borde;
    }

    private void Avisar(string msg)
    {
        _avisoTxt.Text = msg ?? "";
        _avisoBox.IsVisible = !string.IsNullOrEmpty(msg);
    }

    /// <summary>Código del wire → texto que el operario entiende (friendly() del JS, tal cual).</summary>
    private static string Amigable(string? err) => err switch
    {
        null or "" => "",
        "sin-lote" => Traductor.T("Abrí primero un lote."),
        "sin-contorno" => Traductor.T("El lote no tiene contorno todavía."),
        "mismo-punto" => Traductor.T("El punto A y el B son el mismo: tocá dos lugares distintos."),
        "una-sola-linea" => Traductor.T("Hace falta más de una línea para construir la cabecera."),
        "cruces" => Traductor.T("Las puntas tienen que cruzarse entre sí una sola vez. Extendé o acortá con A±/B±."),
        "ui-error" or "no-state" or "service-unavailable" => Traductor.T("Sin conexión con PilotX."),
        _ => err,   // código crudo (error-interno, body-invalido): peor sería tragárselo
    };

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

    private static Button BotonSegmento(string texto) => new()
    {
        Content = texto, Height = 40, FontSize = 12.5, FontWeight = FontWeight.SemiBold,
        CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0),
        Background = BgFila, Foreground = Texto,
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
