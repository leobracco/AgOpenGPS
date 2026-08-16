// ============================================================================
// ContornoPanel.cs — el LINDERO del lote, NATIVO (reemplaza pages/contorno.html
// en la pantalla de la cabina).
//
// Por qué este port importa: contorno era el ÚNICO diálogo que se abría con el
// mapa VIVO detrás, y el más chico de todos (330x290) — se abre justamente
// PARA mirar los puntos del lindero dibujándose mientras se maneja. Aun así
// era una ventana de Chromium encima del mapa, en plena labor. Ahora es una
// card chica flotante: mismo mapa, sin WebView.
//
// Qué quedó NATIVO: las dos vistas de la página.
//   · LISTA      — contornos con área/puntos, "Cruzar" (drive-thru) de los
//                  internos, borrar (doble toque) y "Crear manejando".
//   · GRABACIÓN  — puntos/ha en vivo, Pausa/Grabar, Punto manual y Deshacer
//                  (solo en pausa), Ajustes plegados (offset cm, lado, antena
//                  o implemento, solo con secciones) y Reiniciar/Cancelar/
//                  Terminar con doble toque.
//
// Qué NO se portó y por qué: los otros cuatro caminos de creación (KML,
// Google Earth, dibujar sobre el mapa, cerco desde guías) ya habían salido de
// la UI (pedido 2026-08-05: "contorno simple, se crea manejando y listo") y
// abren ventanas WinForms — contra el motor headless devuelven
// "no-disponible-sin-ui". Sus endpoints siguen vivos en el back, sin botón.
// "delete-all" tampoco tiene botón: no se le inventa uno.
//
// La página HTML NO se toca ni se borra: sigue sirviendo el Hub remoto, el
// celular y el host Android (MainView.axaml.cs la sigue ruteando).
//
// DECISIÓN de cierre (la del port): el panel NO cancela la grabación al
// cerrarse. La página lo hacía en `pagehide` (workaround de browser para no
// dejar el modo grabación prendido sin UI), pero acá el panel se cierra
// también SOLO — abrir Guías, Lote o Configuración lo tapan — y eso le
// volaría al operario una vuelta entera de lindero ya manejada. La red de
// seguridad es la del propio wire: `state.recording` hace que al reabrir el
// panel entre DIRECTO a la grabación, y al cerrar con REC prendido se avisa
// por toast que sigue grabándose. Cancelar solo cancela el botón "Cancelar".
//
// Wire: el MISMO /api/contorno/* que usaba contorno.js (ver ContornoClient).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public sealed class ContornoPanel : Border
{
    // ---- paleta PilotX (idéntica a GuiasPanel/LotePanel/DireccionPanel) -----
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgFilaSel  = new SolidColorBrush(Color.Parse("#DCEFD8"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#7A857B"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush BgError    = new SolidColorBrush(Color.Parse("#FBECEC"));
    private static readonly IBrush TextoError = new SolidColorBrush(Color.Parse("#B33F3A"));

    private const string Mono = "Consolas, Courier New, monospace";

    private ContornoClient? _cli;
    private CancellationTokenSource? _cts;

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;

    /// <summary>Aviso corto (lo muestra MainWindow como toast, nunca modal).</summary>
    public event Action<string>? Aviso;

    // ---- estado -------------------------------------------------------------
    private string _vista = "lista";          // "lista" | "rec"
    private int _seleccion = -1;              // índice de contorno seleccionado (local, no viaja al wire)
    private ContornoEstadoDto? _estado;
    private ContornoGrabacionDto? _rec;
    private bool _sinConexion;
    // Error de una acción puntual (ej. "Crear" sin lote). En la página el
    // mensaje lo borraba el siguiente tick del poll: duraba 500 ms y el
    // operario no llegaba a leerlo. Acá vive 6 s y después se limpia solo.
    private string _avisoTransitorio = "";
    private DispatcherTimer? _timerAviso;

    // ---- controles ----------------------------------------------------------
    private readonly Border _pill;
    private readonly TextBlock _pillTxt;
    private readonly Border _avisoBox;
    private readonly TextBlock _avisoTxt;

    private readonly StackPanel _paneLista;
    private readonly StackPanel _filas;
    private readonly TextBlock _vacio;
    private readonly Button _btnBorrar;
    private readonly Button _btnCrear;

    private readonly StackPanel _paneRec;
    private readonly TextBlock _valPuntos;
    private readonly TextBlock _valArea;
    private readonly Button _btnPausa;
    private readonly Button _btnPunto;
    private readonly Button _btnDeshacer;
    private readonly Button _btnAjustes;
    private readonly TextBlock _flechaAjustes;
    private readonly StackPanel _ajustes;
    private readonly TextBox _txtOffset;
    private readonly Button _btnLado;
    private readonly Button _btnAntena;
    private readonly Button _btnSecciones;

    // Doble toque de confirmación: botón → (timer de 3 s, etiqueta original).
    private readonly Dictionary<Button, (DispatcherTimer Timer, string Etiqueta)> _confirmando = new();
    // R2: la fila apuntada cuando se armó el "¿Seguro?" del borrado. Si cambia
    // entre toques, el segundo toque borraría OTRO contorno.
    private int _selAlArmarBorrado = -1;

    public ContornoPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        // Ancho FIJO y chico, como la ventana HTML (330 px): cada píxel de esta
        // card es mapa tapado, y se abre para mirar el mapa.
        Width = 340;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;

        // ---------- cabecera: título + pill + ✕ ----------
        _pillTxt = new TextBlock
        {
            Text = "0", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pill = new Border
        {
            Child = _pillTxt, CornerRadius = new CornerRadius(999),
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var titulo = new TextBlock
        {
            Text = "Contorno", FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
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
        };
        btnCerrar.Click += (_, _) => Cerrar();

        var cabecera = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetColumn(titulo, 0);
        Grid.SetColumn(_pill, 1);
        Grid.SetColumn(btnCerrar, 3);
        cabecera.Children.Add(titulo);
        cabecera.Children.Add(_pill);
        cabecera.Children.Add(btnCerrar);

        // ---------- banner de aviso ----------
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

        // ---------- vista LISTA ----------
        _filas = new StackPanel();
        _vacio = new TextBlock
        {
            Text = "El lote no tiene contorno todavía.", FontSize = 12, Foreground = TextoMuted,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 14, 4, 14),
        };
        var listaCont = new StackPanel();
        listaCont.Children.Add(_vacio);
        listaCont.Children.Add(_filas);
        var scroll = new ScrollViewer
        {
            Content = listaCont, MaxHeight = 190,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _btnBorrar = BotonAccion("Borrar", peligro: true);
        _btnBorrar.Click += async (_, _) => await BorrarAsync();
        _btnCrear = BotonAccion("Crear manejando", acento: true);
        _btnCrear.Click += async (_, _) => await CrearAsync();

        var pieLista = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        _btnBorrar.Margin = new Thickness(0, 0, 4, 0);
        _btnCrear.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(_btnBorrar, 0);
        Grid.SetColumn(_btnCrear, 1);
        pieLista.Children.Add(_btnBorrar);
        pieLista.Children.Add(_btnCrear);

        _paneLista = new StackPanel();
        _paneLista.Children.Add(scroll);
        _paneLista.Children.Add(pieLista);

        // ---------- vista GRABACIÓN ----------
        _valPuntos = ValorMono("0");
        _valArea = ValorMono("0.00");
        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var cPuntos = TarjetaStat(_valPuntos, "puntos");
        var cArea = TarjetaStat(_valArea, "ha");
        cPuntos.Margin = new Thickness(0, 0, 4, 0);
        cArea.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(cPuntos, 0);
        Grid.SetColumn(cArea, 1);
        stats.Children.Add(cPuntos);
        stats.Children.Add(cArea);

        _btnPausa = BotonAccion("Grabar", acento: true);
        _btnPausa.Click += async (_, _) => await RecAsync("api/contorno/record/pause");
        _btnPunto = BotonAccion("Punto");
        _btnPunto.Click += async (_, _) => await RecAsync("api/contorno/record/add-point");
        _btnDeshacer = BotonAccion("Deshacer");
        _btnDeshacer.Click += async (_, _) => await RecAsync("api/contorno/record/undo");

        var filaRec = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        _btnPausa.Margin = new Thickness(0, 0, 3, 0);
        _btnPunto.Margin = new Thickness(3, 0, 3, 0);
        _btnDeshacer.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(_btnPausa, 0);
        Grid.SetColumn(_btnPunto, 1);
        Grid.SetColumn(_btnDeshacer, 2);
        filaRec.Children.Add(_btnPausa);
        filaRec.Children.Add(_btnPunto);
        filaRec.Children.Add(_btnDeshacer);

        // "Ajustes": se tocan una vez al empezar y no se miran más, así que van
        // PLEGADOS — no le roban lugar a puntos/ha ni a los botones de grabar.
        // Nada de Expander/Flyout sobre el mapa GL: Border + IsVisible.
        _flechaAjustes = new TextBlock
        {
            Text = "▸", FontSize = 11, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var lblAjustes = new TextBlock
        {
            Text = "Ajustes", FontSize = 11, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var contAjustes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        contAjustes.Children.Add(_flechaAjustes);
        contAjustes.Children.Add(lblAjustes);
        _btnAjustes = new Button
        {
            Content = contAjustes, Height = 30, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _btnAjustes.Click += (_, _) =>
        {
            _ajustes.IsVisible = !_ajustes.IsVisible;
            _flechaAjustes.Text = _ajustes.IsVisible ? "▾" : "▸";
            if (!_ajustes.IsVisible) _ = _cli?.TecladoAsync(false);
        };

        _txtOffset = new TextBox
        {
            FontFamily = new FontFamily(Mono), FontSize = 14, Height = 40, MinWidth = 64,
            TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0),
        };
        // El teclado nativo de PilotX no es automático por foco: se pide a mano
        // con la misma señal HTTP que mandan las páginas. El valor se manda al
        // motor al SALIR del campo (no tecla a tecla), como el 'change' del HTML.
        _txtOffset.GotFocus += (_, _) => _ = _cli?.TecladoAsync(true);
        _txtOffset.LostFocus += async (_, _) =>
        {
            _ = _cli?.TecladoAsync(false);
            await CommitOffsetAsync();
        };

        _btnLado = BotonChico("Derecha");
        _btnLado.Click += async (_, _) =>
            await RecAsync("api/contorno/record/set", new { right_side = !(_rec?.RightSide ?? false) });
        _btnAntena = BotonChico("Antena");
        _btnAntena.Click += async (_, _) =>
            await RecAsync("api/contorno/record/set", new { at_pivot = !(_rec?.AtPivot ?? false) });
        _btnSecciones = BotonChico("Solo con secciones: No");
        _btnSecciones.Click += async (_, _) =>
            await RecAsync("api/contorno/record/set", new { section_rec = !(_rec?.SectionRec ?? false) });

        var filaOffset = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
        };
        var lblOffset = new TextBlock
        {
            Text = "Offset", FontSize = 11, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        };
        var lblCm = new TextBlock
        {
            Text = "cm", FontSize = 11, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
        };
        Grid.SetColumn(lblOffset, 0);
        Grid.SetColumn(_txtOffset, 1);
        Grid.SetColumn(lblCm, 2);
        Grid.SetColumn(_btnLado, 3);
        filaOffset.Children.Add(lblOffset);
        filaOffset.Children.Add(_txtOffset);
        filaOffset.Children.Add(lblCm);
        filaOffset.Children.Add(_btnLado);

        var filaToggles = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        _btnAntena.Margin = new Thickness(0, 0, 3, 0);
        _btnSecciones.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(_btnAntena, 0);
        Grid.SetColumn(_btnSecciones, 1);
        filaToggles.Children.Add(_btnAntena);
        filaToggles.Children.Add(_btnSecciones);

        _ajustes = new StackPanel { IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        _ajustes.Children.Add(filaOffset);
        _ajustes.Children.Add(filaToggles);

        var btnReiniciar = BotonAccion("Reiniciar");
        btnReiniciar.Click += async (_, _) =>
        {
            if (!PedirConfirmacion(btnReiniciar, "Reiniciar")) return;
            await RecAsync("api/contorno/record/restart");
        };
        var btnCancelarRec = BotonAccion("Cancelar", peligro: true);
        btnCancelarRec.Click += async (_, _) =>
        {
            if (!PedirConfirmacion(btnCancelarRec, "Cancelar")) return;
            await CancelarGrabacionAsync();
        };
        var btnTerminar = BotonAccion("Terminar", acento: true);
        btnTerminar.Click += async (_, _) =>
        {
            if (!PedirConfirmacion(btnTerminar, "Terminar")) return;
            await TerminarAsync();
        };

        var pieRec = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        btnReiniciar.Margin = new Thickness(0, 0, 3, 0);
        btnCancelarRec.Margin = new Thickness(3, 0, 3, 0);
        btnTerminar.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(btnReiniciar, 0);
        Grid.SetColumn(btnCancelarRec, 1);
        Grid.SetColumn(btnTerminar, 2);
        pieRec.Children.Add(btnReiniciar);
        pieRec.Children.Add(btnCancelarRec);
        pieRec.Children.Add(btnTerminar);

        _paneRec = new StackPanel { IsVisible = false };
        _paneRec.Children.Add(stats);
        _paneRec.Children.Add(filaRec);
        _paneRec.Children.Add(_btnAjustes);
        _paneRec.Children.Add(_ajustes);
        _paneRec.Children.Add(pieRec);

        // ---------- árbol ----------
        var stage = new Panel();
        stage.Children.Add(_paneLista);
        stage.Children.Add(_paneRec);

        var root = new StackPanel();
        root.Children.Add(cabecera);
        root.Children.Add(_avisoBox);
        root.Children.Add(stage);
        Child = root;
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del canal HTTP (mismo criterio que los otros paneles).</summary>
    public void Attach(ContornoClient cli) => _cli = cli;

    /// <summary>
    /// Abre el panel. Arranque igual al de la página: se pide el estado y, si
    /// PilotX YA está grabando (panel reabierto), se entra DIRECTO a la vista
    /// de grabación en vez de mostrar la lista.
    /// </summary>
    public async void Abrir()
    {
        _seleccion = -1;
        LimpiarAviso();
        CancelarConfirmaciones();
        Mostrar("lista");
        IsVisible = true;
        Render();

        if (_cli != null)
        {
            var st = await _cli.GetEstadoAsync().ConfigureAwait(true);
            AplicarEstado(st);
            if (st != null && st.Ok && st.Recording)
            {
                AplicarGrabacion(await _cli.GetGrabacionAsync().ConfigureAwait(true));
                Mostrar("rec");
                Render();
            }
        }
        IniciarLoop();
        Traductor.Aplicar(this);
    }

    /// <summary>
    /// Cierra el panel. OJO: NO cancela la grabación (ver la nota de cabecera).
    /// Si quedó grabando se avisa por toast, porque el pill "REC" vive adentro
    /// de esta card y al cerrarla el operario ya no lo ve.
    /// </summary>
    public void Cerrar()
    {
        PararLoop();
        _ = _cli?.TecladoAsync(false);
        CancelarConfirmaciones();
        LimpiarAviso();
        bool grabando = _vista == "rec" || (_estado?.Recording ?? false);
        IsVisible = false;
        if (grabando)
            Aviso?.Invoke(Traductor.T("El contorno se sigue grabando — volvé a Lindero para terminarlo"));
        Cerrado?.Invoke();
    }

    /// <summary>Alias del cierre para los puntos que usan el patrón Detach().</summary>
    public void Detach() => PararLoop();

    private void IniciarLoop()
    {
        if (_cts != null) return;              // guard anti doble-Attach/Abrir
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    private void PararLoop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    // El poll pega a UN endpoint u otro según la vista activa, igual que el
    // setInterval de contorno.js.
    private async Task TickAsync(CancellationToken ct)
    {
        var cli = _cli;
        if (cli == null) return;
        bool enRec = _vista == "rec";
        try
        {
            if (enRec)
            {
                var r = await cli.GetGrabacionAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() => { AplicarGrabacion(r); });
            }
            else
            {
                var s = await cli.GetEstadoAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() => { AplicarEstado(s); });
            }
        }
        // TaskCanceledException HEREDA de OperationCanceledException: sin el
        // `when` un timeout del Hub se confundiría con la cancelación del panel
        // y dejaría la UI clavada en lo último que pintó.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
    }

    // =========================================================================
    //  acciones (todas pintan con la respuesta del POST, sin esperar el poll)
    // =========================================================================

    private async Task BorrarAsync()
    {
        // R2: si la selección cambió entre el primer y el segundo toque, el
        // "¿Seguro?" pendiente apuntaría a OTRO contorno. Se desarma.
        if (_confirmando.ContainsKey(_btnBorrar) && _selAlArmarBorrado != _seleccion)
        {
            CancelarConfirmacion(_btnBorrar);
            return;
        }
        _selAlArmarBorrado = _seleccion;
        if (!PedirConfirmacion(_btnBorrar, "Borrar")) return;
        if (_cli == null || _seleccion < 0) return;

        LimpiarAviso();
        var s = await _cli.PostEstadoAsync("api/contorno/delete", new { index = _seleccion }).ConfigureAwait(true);
        _seleccion = -1;
        AplicarEstado(s);
    }

    private async Task CrearAsync()
    {
        if (_cli == null) return;
        LimpiarAviso();
        var r = await _cli.PostGrabacionAsync("api/contorno/record/start").ConfigureAwait(true);
        if (r != null && r.Ok && string.IsNullOrEmpty(r.Error))
        {
            _rec = r;
            _sinConexion = false;
            Mostrar("rec");
            Render();
            return;
        }
        // No arrancó: el motivo va al banner y el panel se queda en la lista
        // (sin-lote, herramienta-angosta…).
        if (r == null) { _sinConexion = true; Render(); return; }
        MostrarAvisoTransitorio(Amigable(r.Error));
    }

    private async Task RecAsync(string ruta, object? body = null)
    {
        if (_cli == null) return;
        LimpiarAviso();
        AplicarGrabacion(await _cli.PostGrabacionAsync(ruta, body).ConfigureAwait(true));
    }

    private async Task CancelarGrabacionAsync()
    {
        if (_cli == null) return;
        LimpiarAviso();
        var r = await _cli.PostGrabacionAsync("api/contorno/record/cancel").ConfigureAwait(true);
        // Sin respuesta no se sabe si canceló: quedarse en la grabación es lo
        // seguro (volver a la lista haría creer que la grabación terminó).
        if (r == null) { _sinConexion = true; Render(); return; }
        Mostrar("lista");
        AplicarEstado(await _cli.GetEstadoAsync().ConfigureAwait(true));
    }

    private async Task TerminarAsync()
    {
        if (_cli == null) return;
        LimpiarAviso();
        var r = await _cli.PostGrabacionAsync("api/contorno/record/save").ConfigureAwait(true);
        // R6: si no pudo guardar (pocos-puntos, interno-mas-grande) se QUEDA en
        // la grabación mostrando el motivo — volver a la lista tiraría la vuelta
        // que el operario acaba de manejar. Ídem si no hubo respuesta: no se
        // sabe si guardó, y suponer que sí es lo caro.
        if (r == null) { _sinConexion = true; Render(); return; }
        if (!string.IsNullOrEmpty(r.Error) || !r.Ok)
        {
            MostrarAvisoTransitorio(Amigable(r.Error));
            if (r.Ok) _rec = r;
            Render();
            return;
        }
        Mostrar("lista");
        AplicarEstado(await _cli.GetEstadoAsync().ConfigureAwait(true));
    }

    private async Task CommitOffsetAsync()
    {
        if (_cli == null) return;
        // Coma decimal aceptada: el teclado de la cabina y el operario escriben
        // "12,5", no "12.5".
        var txt = (_txtOffset.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out double cm)
            || double.IsNaN(cm) || double.IsInfinity(cm))
        {
            Render();   // valor no numérico: se repinta el que tiene el motor
            return;
        }
        AplicarGrabacion(await _cli.PostGrabacionAsync(
            "api/contorno/record/set", new { offset_cm = cm }).ConfigureAwait(true));
    }

    // =========================================================================
    //  estado → UI
    // =========================================================================

    // ok:false NO es "sin conexión": el motor CONTESTÓ y dijo por qué no pudo
    // ("sin-lote", "service-unavailable"…). La página mostraba "Sin conexión
    // con PilotX." incluso con la respuesta en la mano — acá se muestra el
    // motivo, que es lo que el operario necesita para destrabarse.
    private void AplicarEstado(ContornoEstadoDto? s)
    {
        if (s == null) { _sinConexion = true; Render(); return; }
        _sinConexion = false;
        if (!s.Ok) { MostrarAvisoTransitorio(Amigable(s.Error)); return; }
        _estado = s;
        Render();
    }

    private void AplicarGrabacion(ContornoGrabacionDto? r)
    {
        if (r == null) { _sinConexion = true; Render(); return; }
        _sinConexion = false;
        if (!r.Ok) { MostrarAvisoTransitorio(Amigable(r.Error)); return; }
        _rec = r;
        Render();
    }

    private void Mostrar(string vista)
    {
        _vista = vista;
        _paneLista.IsVisible = vista == "lista";
        _paneRec.IsVisible = vista == "rec";
        CancelarConfirmaciones();
        LimpiarAviso();
        if (vista != "rec") _ = _cli?.TecladoAsync(false);
        Traductor.Aplicar(this);
    }

    private void Render()
    {
        // ---- banner de aviso ----
        string msg;
        if (_sinConexion) msg = Traductor.T("Sin conexión con PilotX.");
        else if (!string.IsNullOrEmpty(_avisoTransitorio)) msg = _avisoTransitorio;
        else if (_vista == "rec") msg = Amigable(_rec?.Error);
        else if (_estado != null && !_estado.JobStarted) msg = Traductor.T("Abrí primero un lote.");
        else msg = Amigable(_estado?.Error);
        _avisoTxt.Text = msg;
        _avisoBox.IsVisible = !string.IsNullOrEmpty(msg);

        // ---- pill: cantidad de contornos, o REC mientras se graba ----
        var items = _estado?.Boundaries ?? new List<ContornoItemDto>();
        bool grabando = _vista == "rec" || (_estado?.Recording ?? false);
        _pillTxt.Text = grabando ? "REC" : items.Count.ToString(CultureInfo.InvariantCulture);
        _pillTxt.Foreground = grabando ? Brushes.White : TextoMuted;
        _pill.Background = grabando ? Rojo : BgFila;
        _pill.BorderBrush = grabando ? Rojo : Borde;

        if (_vista == "lista") RenderLista(items);
        else RenderGrabacion();
    }

    private void RenderLista(List<ContornoItemDto> items)
    {
        // Rebuild completo: con dos o tres filas el costo es despreciable y no
        // hay estado que se desincronice.
        if (_seleccion >= items.Count) _seleccion = -1;
        _filas.Children.Clear();
        _vacio.IsVisible = items.Count == 0;

        foreach (var b in items)
        {
            var it = b;
            bool sel = it.Index == _seleccion;

            var nombre = new TextBlock
            {
                Text = it.IsOuter ? "Exterior" : Traductor.T("Interno") + " " + it.Index,
                FontSize = 13, Foreground = Texto, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var pts = new TextBlock
            {
                Text = "(" + it.Points.ToString(CultureInfo.InvariantCulture) + " pts)",
                FontSize = 10.5, Foreground = TextoDim, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
            };
            var izq = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            izq.Children.Add(nombre);
            izq.Children.Add(pts);
            Grid.SetColumn(izq, 0);

            var area = new TextBlock
            {
                Text = it.AreaHa.ToString("F2", CultureInfo.InvariantCulture) + " ha",
                FontFamily = new FontFamily(Mono), FontSize = 12.5, FontWeight = FontWeight.Bold,
                Foreground = Verde, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right, MinWidth = 62,
            };
            Grid.SetColumn(area, 2);

            var celda = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            celda.Children.Add(izq);
            celda.Children.Add(area);

            // "Cruzar" (drive-thru) es SOLO de los internos: se puede manejar
            // por encima y el giro en cabecera no lo esquiva.
            if (!it.IsOuter)
            {
                var thru = new Button
                {
                    Content = Traductor.T("Cruzar") + ": " + Traductor.T(it.IsDriveThru ? "Sí" : "No"),
                    Height = 30, FontSize = 10.5, CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 6, 0),
                    Background = it.IsDriveThru ? Verde : BgFila,
                    Foreground = it.IsDriveThru ? Brushes.White : TextoMuted,
                    BorderBrush = it.IsDriveThru ? Verde : Borde, BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                // El toque en "Cruzar" NO selecciona la fila (stopPropagation
                // del HTML).
                thru.Tapped += (_, e) => e.Handled = true;
                thru.Click += async (_, e) =>
                {
                    e.Handled = true;
                    if (_cli == null) return;
                    LimpiarAviso();
                    AplicarEstado(await _cli.PostEstadoAsync("api/contorno/drive-thru",
                        new { index = it.Index, value = !it.IsDriveThru }).ConfigureAwait(true));
                };
                Grid.SetColumn(thru, 1);
                celda.Children.Add(thru);
            }

            var fila = new Border
            {
                Child = celda, Background = sel ? BgFilaSel : BgFila,
                BorderBrush = sel ? Verde : Borde, BorderThickness = new Thickness(sel ? 2 : 1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 4), Cursor = new Cursor(StandardCursorType.Hand),
            };
            fila.Tapped += (_, _) =>
            {
                // Tocar la fila seleccionada la deselecciona (igual que el HTML).
                _seleccion = _seleccion == it.Index ? -1 : it.Index;
                Render();
            };
            _filas.Children.Add(fila);
        }

        // Regla del player nativo: el exterior solo se borra si es el único
        // (con internos adentro habría que borrarlos primero).
        bool sePuedeBorrar = _seleccion > 0 || (_seleccion == 0 && items.Count == 1);
        _btnBorrar.IsEnabled = sePuedeBorrar;
        _btnCrear.IsEnabled = _estado?.JobStarted ?? false;

        // Un borrado o un cambio de fila desarma el "¿Seguro?" pendiente.
        if (_confirmando.ContainsKey(_btnBorrar) && (!sePuedeBorrar || _selAlArmarBorrado != _seleccion))
            CancelarConfirmacion(_btnBorrar);

        Traductor.Aplicar(_filas);
    }

    private void RenderGrabacion()
    {
        var r = _rec;
        _valPuntos.Text = (r?.Points ?? 0).ToString(CultureInfo.InvariantCulture);
        _valArea.Text = (r?.AreaHa ?? 0).ToString("F2", CultureInfo.InvariantCulture);

        // R3: mientras el operario está tipeando, el poll NO le pisa el campo.
        if (!_txtOffset.IsFocused)
            _txtOffset.Text = Math.Round(r?.OffsetCm ?? 0).ToString("F0", CultureInfo.InvariantCulture);

        _btnLado.Content = Traductor.T((r?.RightSide ?? false) ? "Derecha" : "Izquierda");
        _btnAntena.Content = Traductor.T((r?.AtPivot ?? false) ? "Antena" : "Implemento");
        _btnSecciones.Content = Traductor.T("Solo con secciones") + ": " +
                                Traductor.T((r?.SectionRec ?? false) ? "Sí" : "No");

        bool grabando = !(r?.Paused ?? true);
        _btnPausa.Content = Traductor.T(grabando ? "Pausa" : "Grabar");
        // Punto manual y deshacer SOLO en pausa (regla del player nativo).
        _btnPunto.IsEnabled = !grabando;
        _btnDeshacer.IsEnabled = !grabando;
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    /// <summary>
    /// Código del wire → texto que el operario entiende. Se portan TODOS los
    /// casos de friendly() de contorno.js, incluso los de caminos que este
    /// panel no dispara (KML, Google Earth, cerco desde guías): son baratos y
    /// blindan contra un error inesperado del back — mejor texto que código.
    /// </summary>
    private static string Amigable(string? err) => err switch
    {
        null or "" => "",
        "sin-lote" => Traductor.T("Abrí primero un lote."),
        "herramienta-angosta" => Traductor.T("El implemento es demasiado angosto."),
        "borrar-internos-primero" => Traductor.T("Borrá primero los contornos internos."),
        "pocos-puntos" => Traductor.T("Muy pocos puntos: manejá el borde antes de terminar."),
        "interno-mas-grande" => Traductor.T("OJO: este contorno es MÁS GRANDE que el exterior. Así el lote queda con área negativa y el giro en cabecera no va a funcionar. Borralo o borrá el exterior."),
        "kml-invalido" => Traductor.T("No se pudo leer el KML."),
        "google-earth-error" => Traductor.T("No se pudo abrir Google Earth."),
        "sin-grabacion" => Traductor.T("No hay grabación en curso."),
        "indice-invalido" => Traductor.T("Contorno inexistente."),
        "se-necesitan-2-tracks" => Traductor.T("Se necesitan al menos 2 guías en el lote (los lados del cerco)."),
        "sin-cerco-valido" => Traductor.T("Las guías no cierran un polígono. Marcá los 4 lados del lote como guías y probá de nuevo."),
        "cerco-aplicado-pero-no-guardado" => Traductor.T("El cerco se armó pero no se pudo guardar en el lote — reintentá."),
        _ => err,   // código crudo: peor sería tragárselo
    };

    /// <summary>
    /// Muestra el motivo de una acción que no se pudo hacer. Dura 6 s: lo
    /// suficiente para leerlo manejando, y después el banner vuelve a decir lo
    /// que dice el estado (que es la verdad de cada momento).
    /// </summary>
    private void MostrarAvisoTransitorio(string msg)
    {
        if (string.IsNullOrEmpty(msg)) msg = Traductor.T("PilotX no pudo completar la acción (AGP-SYS-009).");
        _avisoTransitorio = msg;
        try { _timerAviso?.Stop(); } catch { }
        _timerAviso ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timerAviso.Tick -= OnTimerAviso;
        _timerAviso.Tick += OnTimerAviso;
        _timerAviso.Start();
        Render();
    }

    private void OnTimerAviso(object? s, EventArgs e)
    {
        LimpiarAviso();
        Render();
    }

    private void LimpiarAviso()
    {
        _avisoTransitorio = "";
        try { _timerAviso?.Stop(); } catch { }
    }

    /// <summary>
    /// Doble toque de confirmación, sin modales (regla de cabina): el primer
    /// toque cambia el texto a "¿Seguro?"; el segundo dentro de 3 s confirma.
    /// Devuelve true cuando hay que ejecutar la acción.
    /// </summary>
    private bool PedirConfirmacion(Button b, string etiqueta)
    {
        if (_confirmando.ContainsKey(b))
        {
            CancelarConfirmacion(b);
            return true;
        }
        b.Content = Traductor.T("¿Seguro?");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _confirmando[b] = (t, etiqueta);
        t.Tick += (_, _) => CancelarConfirmacion(b);
        t.Start();
        return false;
    }

    private void CancelarConfirmacion(Button b)
    {
        if (!_confirmando.TryGetValue(b, out var c)) return;
        try { c.Timer.Stop(); } catch { }
        _confirmando.Remove(b);
        b.Content = Traductor.T(c.Etiqueta);
    }

    private void CancelarConfirmaciones()
    {
        foreach (var b in new List<Button>(_confirmando.Keys)) CancelarConfirmacion(b);
        _selAlArmarBorrado = -1;
    }

    private static TextBlock ValorMono(string t) => new()
    {
        Text = t, FontFamily = new FontFamily(Mono), FontSize = 20, FontWeight = FontWeight.Bold,
        Foreground = Texto, TextAlignment = TextAlignment.Center,
    };

    private static Border TarjetaStat(TextBlock valor, string etiqueta)
    {
        var sp = new StackPanel();
        sp.Children.Add(valor);
        sp.Children.Add(new TextBlock
        {
            Text = etiqueta.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = TextoMuted, TextAlignment = TextAlignment.Center,
        });
        return new Border
        {
            Child = sp, Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 6, 4, 6),
        };
    }

    private static Button BotonAccion(string texto, bool acento = false, bool peligro = false)
    {
        var b = new Button
        {
            Content = texto, Height = 44, FontSize = 13, FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = acento ? Verde : peligro ? Rojo : BgFila,
            Foreground = acento || peligro ? Brushes.White : Texto,
            BorderBrush = acento ? Verde : peligro ? Rojo : Borde,
        };
        return b;
    }

    private static Button BotonChico(string texto) => new()
    {
        Content = texto, Height = 40, FontSize = 11.5, FontWeight = FontWeight.SemiBold,
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = BgFila, Foreground = Texto, BorderBrush = Borde,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
}
