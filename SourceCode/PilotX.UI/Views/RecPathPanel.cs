// ============================================================================
// RecPathPanel.cs — RUTAS GRABADAS nativo, reemplaza pages/recpath.html en la
// pantalla de la cabina (ex WinForms FormRecordPicker + FormRecordName).
//
// Por qué importa este port: es pantalla de LABOR — se abre con el tractor
// adentro del lote para elegir el camino que la máquina va a repetir sola. La
// ventana HTML de 460x470 levantaba Chromium y se paraba encima del mapa justo
// cuando lo único que sirve para saber si la ruta elegida es la que se quería
// es VERLA dibujada sobre el lote. Ahora es una card chica flotante con el mapa
// vivo detrás.
//
// Qué quedó NATIVO: la pantalla entera, con paridad de comportamiento.
//   · PICKER: lista de .rec del lote (una carga al abrir, sin polling — la
//     página tampoco poleaba), selección única, "Usar seleccionada", "Borrar"
//     y "Apagar ruta grabada" (este último siempre habilitado y separado, como
//     en el HTML: no es cosmético, limpia recList y persiste RecPath.txt vacío).
//   · SALVAR: nombre + "Agregar fecha" + "Agregar hora" (ambos tildados por
//     defecto) y Guardar/Descartar, con el MISMO armado de sufijos que el JS.
//     Entra por Abrir(modoSalvar: true) en vez del ?mode=save del WebView.
//
// Qué SIGUE en HTML: la página wwwroot/pages/recpath.html + js/recpath.js,
// intactas, para el Hub remoto / celular / Android (regla dura del repo).
//
// Diferencias DELIBERADAS con la página (todas a favor del operario):
//   · La página cerraba la ventana aunque el POST fallara (fetch con catch →
//     null y closeWidget igual): el operario creía que la ruta se cargó o se
//     guardó y no había pasado nada. Acá {ok:false}/sin respuesta ⇒ toast y el
//     panel QUEDA abierto.
//   · Tres estados de lista en vez de uno: "El Hub no responde" (nadie
//     atiende), chip de error (el motor dijo que no) y "No hay rutas grabadas
//     en este lote". El HTML mezclaba los tres en el último y mentía.
//   · "Borrar" pide confirmación INLINE (fila Border + IsVisible, jamás modal:
//     ShowDialog trababa la cabina). Un .rec borrado = volver a manejar el
//     camino entero; el toque perdido del HTML no valía ese precio.
//   · El nombre se sanea contra Path.GetInvalidFileNameChars: el back hace
//     name + ".rec" sin validar y hoy un "/" hace fallar el guardado MUDO.
//
// Wire: el MISMO /api/recpath/* que usaba la página (ver RecPathClient), sin
// un endpoint ni un campo nuevo. El teclado del nombre es la ventana nativa de
// PilotX (api/teclado/abrir|cerrar), no el teclado HTML.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public sealed class RecPathPanel : Border
{
    // ---- paleta PilotX (idéntica a GuiasPanel/CorregirPosicionPanel) --------
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgFilaSel  = new SolidColorBrush(Color.Parse("#DCEFD8"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#8A958B"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Err        = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush BgError    = new SolidColorBrush(Color.Parse("#FBECEC"));
    private static readonly IBrush TextoError = new SolidColorBrush(Color.Parse("#B33F3A"));

    private enum Pantalla { Picker, Salvar }
    private enum EstadoLista { Cargando, Ok, SinHub, ErrorMotor }

    private RecPathClient? _client;
    private CancellationTokenSource? _cts;

    /// <summary>El operario cerró el panel.</summary>
    public event Action? Cerrado;

    /// <summary>Aviso corto (lo muestra MainWindow como toast, nunca modal).</summary>
    public event Action<string>? Aviso;

    // ---- estado -------------------------------------------------------------
    private readonly List<string> _rutas = new();
    private int _seleccion = -1;
    private EstadoLista _estado = EstadoLista.Cargando;
    private string _errorMotor = "";
    private bool _ocupado;          // hay un POST en vuelo (anti doble-tap)
    private bool _cerrada = true;

    // ---- controles ----------------------------------------------------------
    private readonly StackPanel _scPicker;
    private readonly StackPanel _scSalvar;

    private readonly StackPanel _listaFilas;
    private readonly ScrollViewer _listaScroll;
    private readonly TextBlock _msgVacio;
    private readonly Border _chipError;
    private readonly TextBlock _chipErrorTxt;

    private readonly Button _btnUsar;
    private readonly Button _btnBorrar;
    private readonly Button _btnApagar;

    private readonly Border _confirmBorrar;
    private readonly TextBlock _confirmTxt;

    private readonly TextBox _txtNombre;
    private readonly CheckBox _chkFecha;
    private readonly CheckBox _chkHora;
    private readonly Button _btnGuardar;
    private readonly Button _btnDescartar;

    public RecPathPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(14);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        // Ancho fijo y acotado, con techo de alto: es un diálogo chico, no un
        // dashboard. Cada píxel de más es mapa tapado.
        Width = 420;
        MaxHeight = 560;
        IsVisible = false;

        // ================= PANTALLA PICKER =================
        _listaFilas = new StackPanel { Spacing = 4 };
        _listaScroll = new ScrollViewer
        {
            Content = _listaFilas,
            MaxHeight = 280,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        _msgVacio = new TextBlock
        {
            Text = "No hay rutas grabadas en este lote.",
            FontSize = 13, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 14),
            IsVisible = false,
        };

        _chipErrorTxt = new TextBlock
        {
            Text = "", FontSize = 11, Foreground = TextoError, TextWrapping = TextWrapping.Wrap,
        };
        _chipError = new Border
        {
            Child = _chipErrorTxt, Background = BgError, BorderBrush = Err,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 0, 0),
            IsVisible = false,
        };

        _btnUsar = Boton("Usar seleccionada", Verde, Verde);
        _btnUsar.Click += async (_, _) => await UsarAsync();
        _btnBorrar = Boton("Borrar", Err, Err);
        _btnBorrar.Click += (_, _) => PedirConfirmacionBorrar();

        var filaAcciones = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        _btnUsar.Margin = new Thickness(0, 0, 4, 0);
        _btnBorrar.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(_btnUsar, 0);
        Grid.SetColumn(_btnBorrar, 1);
        filaAcciones.Children.Add(_btnUsar);
        filaAcciones.Children.Add(_btnBorrar);

        // Confirmación de borrado INLINE (Border + IsVisible). Nada de modales:
        // el ShowDialog trababa todos los botones de la cabina.
        _confirmTxt = new TextBlock
        {
            Text = "", FontSize = 12, Foreground = Texto, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        var btnConfirmSi = Boton("Sí, borrar", Err, Err);
        btnConfirmSi.Width = 108;
        btnConfirmSi.Click += async (_, _) => await BorrarAsync();
        var btnConfirmNo = Boton("No", Borde, Texto);
        btnConfirmNo.Width = 68;
        btnConfirmNo.Margin = new Thickness(6, 0, 0, 0);
        btnConfirmNo.Click += (_, _) => OcultarConfirmacion();

        var confirmGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(_confirmTxt, 0);
        Grid.SetColumn(btnConfirmSi, 1);
        Grid.SetColumn(btnConfirmNo, 2);
        confirmGrid.Children.Add(_confirmTxt);
        confirmGrid.Children.Add(btnConfirmSi);
        confirmGrid.Children.Add(btnConfirmNo);

        _confirmBorrar = new Border
        {
            Child = confirmGrid, Background = BgError, BorderBrush = Err,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };

        // "Apagar" va SEPARADO de Usar/Borrar, como en el HTML: no requiere
        // selección y no es cosmético — limpia la ruta activa en memoria y
        // persiste RecPath.txt vacío.
        _btnApagar = Boton("Apagar ruta grabada", Err, Err);
        _btnApagar.HorizontalAlignment = HorizontalAlignment.Stretch;
        _btnApagar.Margin = new Thickness(0, 8, 0, 0);
        _btnApagar.Click += async (_, _) => await ApagarAsync();

        _scPicker = new StackPanel();
        _scPicker.Children.Add(Cabecera("Rutas grabadas",
            "Caminos que el tractor puede repetir solo"));
        _scPicker.Children.Add(_listaScroll);
        _scPicker.Children.Add(_msgVacio);
        _scPicker.Children.Add(_chipError);
        _scPicker.Children.Add(filaAcciones);
        _scPicker.Children.Add(_confirmBorrar);
        _scPicker.Children.Add(_btnApagar);

        // ================= PANTALLA SALVAR =================
        _txtNombre = new TextBox
        {
            FontSize = 15, MinHeight = 46, Watermark = "Nombre",
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 4, 0, 0),
        };
        // El teclado se pide a mano, igual que en Guías: la ventana del teclado
        // es NOACTIVATE, así que el TextBox no pierde el foco y las teclas
        // entran solas.
        _txtNombre.GotFocus  += (_, _) => _ = _client?.TecladoAsync(true);
        _txtNombre.LostFocus += (_, _) => _ = _client?.TecladoAsync(false);
        _txtNombre.TextChanged += (_, _) => ActualizarBotones();

        _chkFecha = new CheckBox
        {
            Content = "Agregar fecha", IsChecked = true, MinHeight = 40, FontSize = 13,
            Foreground = Texto,
        };
        _chkHora = new CheckBox
        {
            Content = "Agregar hora", IsChecked = true, MinHeight = 40, FontSize = 13,
            Foreground = Texto,
        };

        _btnGuardar = Boton("Guardar", Verde, Verde);
        _btnGuardar.Click += async (_, _) => await GuardarAsync();
        _btnDescartar = Boton("Descartar", Borde, Texto);
        _btnDescartar.Click += async (_, _) => await DescartarAsync();

        var filaSalvar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        _btnGuardar.Margin = new Thickness(0, 0, 4, 0);
        _btnDescartar.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(_btnGuardar, 0);
        Grid.SetColumn(_btnDescartar, 1);
        filaSalvar.Children.Add(_btnGuardar);
        filaSalvar.Children.Add(_btnDescartar);

        _scSalvar = new StackPanel { IsVisible = false };
        _scSalvar.Children.Add(Cabecera("Guardar ruta grabada",
            "La grabación se guarda como un .rec del lote"));
        _scSalvar.Children.Add(new TextBlock
        {
            Text = "Nombre de la ruta grabada:", FontSize = 13, Foreground = TextoMuted,
        });
        _scSalvar.Children.Add(_txtNombre);
        _scSalvar.Children.Add(_chkFecha);
        _scSalvar.Children.Add(_chkHora);
        _scSalvar.Children.Add(filaSalvar);

        var raiz = new Panel();
        raiz.Children.Add(_scPicker);
        raiz.Children.Add(_scSalvar);
        Child = raiz;

        // Traductor.Aplicar() cachea el PRIMER texto de cada control y lo
        // reescribe en cada pasada: si el idioma cambia con el panel abierto,
        // las FILAS (nombres de rutas = dato del operario) pasarían por el
        // diccionario. Por eso se limpian, se traduce el chrome y recién ahí se
        // vuelven a construir.
        Traductor.IdiomaCambio += () => Dispatcher.UIThread.Post(() =>
        {
            if (_cerrada) return;
            OcultarConfirmacion();
            _listaFilas.Children.Clear();
            Traductor.Aplicar(this);
            RenderLista();
        });
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del client (mismo criterio que el resto de los paneles).</summary>
    public void Attach(RecPathClient client) => _client = client;

    /// <summary>
    /// Abre el panel. modoSalvar = la pantalla de nombre (ex ?mode=save del
    /// WebView): hoy no la dispara nadie porque el botón de parar grabación se
    /// perdió con las WinForms, pero el flujo back (/save, /discard) está vivo y
    /// el parámetro queda listo para cuando se recupere el botón.
    /// </summary>
    public void Abrir(bool modoSalvar = false)
    {
        if (!_cerrada) return;      // ya abierto: no se re-abre
        _cerrada = false;
        _ocupado = false;
        _rutas.Clear();
        _seleccion = -1;
        _errorMotor = "";
        _estado = EstadoLista.Cargando;
        OcultarConfirmacion();

        if (modoSalvar)
        {
            _txtNombre.Text = "";
            _chkFecha.IsChecked = true;
            _chkHora.IsChecked = true;
        }

        Mostrar(modoSalvar ? Pantalla.Salvar : Pantalla.Picker);
        IsVisible = true;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        if (!modoSalvar) _ = CargarListaAsync(_cts.Token);
    }

    /// <summary>
    /// Cierra el panel. No manda nada al motor: cerrar NO descarta la grabación
    /// (paridad con el HTML, donde cerrar la ventana la dejaba en memoria).
    /// </summary>
    public void Cerrar()
    {
        if (_cerrada) { IsVisible = false; return; }
        _cerrada = true;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        // Si el panel se cierra con el campo enfocado, el teclado quedaría
        // flotando sobre el mapa.
        _ = _client?.TecladoAsync(false);
        OcultarConfirmacion();
        IsVisible = false;
        Cerrado?.Invoke();
    }

    /// <summary>Alias del cierre para los puntos que usan el patrón Detach().</summary>
    public void Detach() => Cerrar();

    private void Mostrar(Pantalla cual)
    {
        _scPicker.IsVisible = cual == Pantalla.Picker;
        _scSalvar.IsVisible = cual == Pantalla.Salvar;
        if (cual != Pantalla.Salvar) _ = _client?.TecladoAsync(false);
        ActualizarBotones();
        // Se aplica con la lista VACÍA (las filas se construyen después): los
        // nombres de las rutas no pasan nunca por el diccionario.
        Traductor.Aplicar(this);
        if (cual == Pantalla.Picker) RenderLista();
    }

    // =========================================================================
    //  PICKER
    // =========================================================================

    private async Task CargarListaAsync(CancellationToken ct)
    {
        var client = _client;
        if (client == null)
        {
            AplicarEstadoEnUi(EstadoLista.SinHub, null, "");
            return;
        }

        RecPathListDto? dto;
        try { dto = await client.GetListAsync(ct).ConfigureAwait(false); }
        // TaskCanceledException HEREDA de OperationCanceledException: sin el
        // `when`, un timeout del Hub se confundiría con el cierre del panel y la
        // lista quedaría clavada en "Cargando…".
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { dto = null; }

        if (ct.IsCancellationRequested || _cerrada) return;

        if (dto == null) AplicarEstadoEnUi(EstadoLista.SinHub, null, "");
        else if (!dto.Ok) AplicarEstadoEnUi(EstadoLista.ErrorMotor, null, dto.Error ?? "");
        else AplicarEstadoEnUi(EstadoLista.Ok, dto.Paths, "");
    }

    private void AplicarEstadoEnUi(EstadoLista estado, List<string>? rutas, string error)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_cerrada) return;
            _estado = estado;
            _errorMotor = error ?? "";
            _rutas.Clear();
            if (rutas != null) _rutas.AddRange(rutas.Where(r => !string.IsNullOrWhiteSpace(r)));
            RenderLista();
        });

    /// <summary>
    /// Rebuild completo de la lista. SIEMPRE resetea la selección, igual que el
    /// renderList() del JS: después de borrar, el índice viejo apuntaría a otra
    /// ruta — y el siguiente toque de "Borrar" se llevaría la equivocada.
    /// </summary>
    private void RenderLista()
    {
        _listaFilas.Children.Clear();
        _seleccion = -1;
        OcultarConfirmacion();

        for (int i = 0; i < _rutas.Count; i++)
        {
            int idx = i;
            // El NOMBRE de la ruta es dato del operario: no se traduce jamás.
            var txt = new TextBlock
            {
                Text = _rutas[i], FontSize = 14, Foreground = Texto,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var fila = new Border
            {
                Child = txt, Background = BgFila, BorderBrush = Borde,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6), MinHeight = 44,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            fila.Tapped += (_, _) => Seleccionar(idx);
            _listaFilas.Children.Add(fila);
        }

        _listaScroll.IsVisible = _rutas.Count > 0;
        // Tres estados donde el HTML tenía uno solo ("No hay rutas grabadas"
        // también cuando el Hub estaba caído).
        _msgVacio.IsVisible = _rutas.Count == 0 && _estado != EstadoLista.ErrorMotor;
        _msgVacio.Text = _estado switch
        {
            EstadoLista.Cargando => Traductor.T("Buscando rutas grabadas…"),
            EstadoLista.SinHub   => Traductor.T("El Hub no responde"),
            _                    => Traductor.T("No hay rutas grabadas en este lote."),
        };
        _msgVacio.Foreground = _estado == EstadoLista.SinHub ? TextoDim : TextoMuted;

        _chipError.IsVisible = _estado == EstadoLista.ErrorMotor;
        if (_estado == EstadoLista.ErrorMotor)
            _chipErrorTxt.Text = Traductor.T("No se pudo leer la lista de rutas")
                               + (string.IsNullOrWhiteSpace(_errorMotor) ? "" : " · " + _errorMotor);

        ActualizarBotones();
    }

    private void Seleccionar(int idx)
    {
        _seleccion = idx;
        OcultarConfirmacion();
        for (int i = 0; i < _listaFilas.Children.Count; i++)
        {
            if (_listaFilas.Children[i] is not Border b) continue;
            bool sel = i == idx;
            b.Background = sel ? BgFilaSel : BgFila;
            b.BorderBrush = sel ? Verde : Borde;
        }
        ActualizarBotones();
    }

    private bool SeleccionValida => _seleccion >= 0 && _seleccion < _rutas.Count;

    private void ActualizarBotones()
    {
        _btnUsar.IsEnabled = SeleccionValida && !_ocupado;
        _btnBorrar.IsEnabled = SeleccionValida && !_ocupado;
        _btnApagar.IsEnabled = !_ocupado;   // no necesita selección (paridad HTML)
        // Guardar: con el nombre vacío queda deshabilitado. El HTML hacía
        // `return` mudo — el operario tocaba y no pasaba nada, sin explicación.
        _btnGuardar.IsEnabled = !_ocupado && !string.IsNullOrWhiteSpace(_txtNombre.Text);
        _btnDescartar.IsEnabled = !_ocupado;
    }

    private void PedirConfirmacionBorrar()
    {
        if (!SeleccionValida) return;
        _confirmTxt.Text = Traductor.T("¿Borrar") + " «" + _rutas[_seleccion] + "»?";
        _confirmBorrar.IsVisible = true;
    }

    private void OcultarConfirmacion()
    {
        _confirmBorrar.IsVisible = false;
        _confirmTxt.Text = "";
    }

    private async Task UsarAsync()
    {
        if (!SeleccionValida || _client == null) return;
        string nombre = _rutas[_seleccion];
        var r = await EjecutarAsync(ct => _client.LoadAsync(nombre, ct)).ConfigureAwait(true);
        if (_cerrada) return;
        if (r != null && r.Ok)
        {
            // Cierra y listo: la ruta cargada se ve dibujada en el mapa vivo,
            // que es el feedback que el motor sí da.
            Cerrar();
            return;
        }
        Aviso?.Invoke(Traductor.T("No se pudo cargar la ruta grabada."));
    }

    private async Task BorrarAsync()
    {
        if (!SeleccionValida || _client == null) return;
        string nombre = _rutas[_seleccion];
        OcultarConfirmacion();
        var r = await EjecutarAsync(ct => _client.DeleteAsync(nombre, ct)).ConfigureAwait(true);
        if (_cerrada) return;
        if (r == null)
        {
            Aviso?.Invoke(Traductor.T("El Hub no responde: la ruta no se borró."));
            return;
        }
        // /delete devuelve la lista NUEVA: se repinta con eso, sin GET extra.
        if (r.Paths != null)
        {
            _estado = EstadoLista.Ok;
            _rutas.Clear();
            _rutas.AddRange(r.Paths.Where(p => !string.IsNullOrWhiteSpace(p)));
            RenderLista();
        }
        if (!r.Ok) Aviso?.Invoke(Traductor.T("No se pudo borrar la ruta grabada."));
    }

    private async Task ApagarAsync()
    {
        if (_client == null) return;
        var r = await EjecutarAsync(ct => _client.OffAsync(ct)).ConfigureAwait(true);
        if (_cerrada) return;
        if (r != null && r.Ok) { Cerrar(); return; }
        Aviso?.Invoke(Traductor.T("No se pudo apagar la ruta grabada."));
    }

    // =========================================================================
    //  SALVAR
    // =========================================================================

    private async Task GuardarAsync()
    {
        if (_client == null) return;
        string nombre = ComponerNombre();
        if (string.IsNullOrWhiteSpace(nombre))
        {
            Aviso?.Invoke(Traductor.T("Poné un nombre para la ruta grabada."));
            return;
        }
        var r = await EjecutarAsync(ct => _client.SaveAsync(nombre, ct)).ConfigureAwait(true);
        if (_cerrada) return;
        if (r != null && r.Ok) { Cerrar(); return; }
        Aviso?.Invoke(Traductor.T("No se pudo guardar la ruta grabada."));
    }

    private async Task DescartarAsync()
    {
        if (_client == null) return;
        var r = await EjecutarAsync(ct => _client.DiscardAsync(ct)).ConfigureAwait(true);
        if (_cerrada) return;
        if (r != null && r.Ok) { Cerrar(); return; }
        Aviso?.Invoke(Traductor.T("No se pudo descartar la grabación."));
    }

    /// <summary>
    /// Nombre final, con los MISMOS sufijos que armaba el JS: " yyyy-MM-dd" y
    /// " HH-mm" (guion en vez de ':' porque va a nombre de archivo).
    ///
    /// La fecha va en hora LOCAL, no en UTC: el JS usaba toISOString() para la
    /// fecha y toTimeString() para la hora, o sea que de noche (UTC−3) mezclaba
    /// el día de mañana con la hora de hoy — "2026-08-17 22-30" grabado el 16.
    /// El nombre lo lee un operario, no un servidor.
    /// </summary>
    private string ComponerNombre()
    {
        var sb = new StringBuilder((_txtNombre.Text ?? "").Trim());
        if (sb.Length == 0) return "";
        var ahora = DateTime.Now;
        if (_chkFecha.IsChecked == true) sb.Append(' ').Append(ahora.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (_chkHora.IsChecked == true) sb.Append(' ').Append(ahora.ToString("HH-mm", CultureInfo.InvariantCulture));
        return Sanear(sb.ToString());
    }

    /// <summary>
    /// Saca los caracteres que Windows no acepta en un nombre de archivo. El
    /// back concatena name + ".rec" sin validar nada: hoy un "/" en el nombre
    /// hace que el guardado falle MUDO y la ruta manejada se pierda.
    /// </summary>
    private static string Sanear(string nombre)
    {
        var invalidos = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(nombre.Length);
        foreach (char c in nombre)
            if (Array.IndexOf(invalidos, c) < 0) sb.Append(c);
        return sb.ToString().Trim();
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    /// <summary>
    /// Corre un POST con los botones bloqueados (anti doble-tap: dos toques
    /// seguidos en "Borrar" se llevarían dos rutas) y con el token del panel.
    /// </summary>
    private async Task<RecPathOkDto?> EjecutarAsync(Func<CancellationToken, Task<RecPathOkDto?>> accion)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        _ocupado = true;
        ActualizarBotones();
        RecPathOkDto? r;
        try { r = await accion(ct).ConfigureAwait(true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch { r = null; }
        finally
        {
            _ocupado = false;
            if (!_cerrada) ActualizarBotones();
        }
        return r;
    }

    /// <summary>Cabecera de una pantalla: título + subtítulo + ✕ táctil.</summary>
    private Grid Cabecera(string titulo, string subtitulo)
    {
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Texto,
        });
        col.Children.Add(new TextBlock
        {
            Text = subtitulo, FontSize = 10.5, Foreground = TextoMuted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
        });

        var cerrar = new Button
        {
            Content = "✕", Width = 44, Height = 44, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Background = BgFila, Foreground = TextoMuted, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        cerrar.Click += (_, _) => Cerrar();

        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetColumn(col, 0);
        Grid.SetColumn(cerrar, 1);
        g.Children.Add(col);
        g.Children.Add(cerrar);
        return g;
    }

    /// <summary>
    /// Botón táctil (48 de alto). El acento y el destructivo van en BORDE y
    /// TEXTO sobre fondo blanco: el verde jamás de fondo, y el rojo oscuro del
    /// HTML (#5a2c2c) está fuera de paleta.
    /// </summary>
    private static Button Boton(string texto, IBrush borde, IBrush texto2) => new()
    {
        Content = texto, Height = 48, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
        Background = BgFila, Foreground = texto2, BorderBrush = borde,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };
}
