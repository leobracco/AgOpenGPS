// ============================================================================
// BanderasPanel.axaml.cs — BANDERAS nativo, reemplazo de pages/banderas.html
// (que ya era el reemplazo de FormFlags + FormEnterFlag).
//
// Por qué el port: es pantalla de LABOR. El operario marca una piedra, un pozo
// o un alambrado caído mientras maneja, y la lista le dice a qué distancia
// está cada bandera EN VIVO. La ventana de Chromium de 350x340 se paraba justo
// encima del mapa, que es donde se ve dónde quedó la bandera. Ahora es una card
// chica flotante con el mapa vivo detrás.
//
// PARIDAD 1:1 con banderas.js — lo que hace la página, hace este panel:
//   · Poll GET /api/flags/state cada 500 ms (el mismo período del timer1 del
//     form): las distancias siguen al tractor.
//   · Toque en una fila  → POST /pick {number}
//   · Notas (al salir del campo o Enter, y SOLO si cambió, igual que el evento
//     `change` del DOM) → POST /notes {notes}
//   · Borrar             → POST /delete        (SIN confirmación: la página
//                                               tampoco la tenía)
//   · Importar/Exportar  → POST /import,/export
//   · Nueva              → vista de alta; los tres colores CREAN la bandera
//     (POST /add). Si el operario NO tocó lat/lon manda {color,use_current:true};
//     si las tocó y son números finitos, {lat,lon,color,use_current:false}.
//   · Cierre del panel   → POST /close (deselecciona + guarda), que es lo que
//     la página mandaba por sendBeacon en el pagehide.
//   · Mismos textos, mismos estados vacío/aviso, mismo orden de botones, mismos
//     colores de bandera (0 roja, 1 verde, 2 amarilla).
//
// LO QUE SE REPLICA TAL CUAL AUNQUE PAREZCA BUG (ver el reporte del port):
//   · El motor arma TODA respuesta con error como {ok:false,...}
//     (EngineFlagsService.Armar: `Ok = error == null`), y el render del JS
//     corta con "Sin conexión con PilotX." apenas ve ok:false. O sea: un
//     rechazo del motor ("sin-lote", "sin-seleccion", "no-disponible-sin-ui")
//     se le muestra al operario como si se hubiera caído la conexión, y el
//     detalle del error nunca se pinta. Se deja EXACTAMENTE así.
//   · Por lo mismo, la vista de alta NO se cierra cuando el /add fue rechazado
//     (el JS solo cierra con ok y sin error) — acá igual.
//
// Emojis: los del HTML (🗑 ➕ 📍) no viajan. En Avalonia dependen de la fuente
// y quedan como tofu; mismo criterio que CabeceraLineasPanel. La función y el
// texto son los mismos.
//
// Wire: el MISMO /api/flags/* de siempre, snake_case, sin cambios de backend
// (ver Services/BanderasClient.cs).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Traductor = PilotX.Cockpit.Bars.Traductor;

namespace PilotX.Desktop.Views;

public partial class BanderasPanel : UserControl
{
    /// <summary>Mismo período que el setInterval de banderas.js (y que el
    /// timer1 del form original): la distancia tiene que seguir al tractor.</summary>
    private const int PeriodoPollMs = 500;

    /// <summary>Mono de la paleta (--agp-font-mono del CSS): distancias.</summary>
    private const string Mono = "Consolas, Courier New, monospace";

    // ---- tokens del theme que usan las filas (armadas en code-behind) -------
    // Son los MISMOS que consume el XAML de la card: acá no se define ni un
    // color, solo se nombran los del theme (PilotXTheme.axaml).
    private const string TokenFondo     = "PilotXPanelSurface";
    private const string TokenFondoSel  = "PilotXPanelSurface2";
    private const string TokenBorde     = "PilotXPanelBorderHigh";
    private const string TokenBordeSel  = "PilotXPanelAccent";
    private const string TokenTexto     = "PilotXPanelText";
    /// <summary>La distancia iba con el verde de marca (--agp-accent del CSS).
    /// Sobre fondo claro ese verde da 2.5:1 y al sol no se lee: va el verde de
    /// TEXTO del theme, que es el mismo rol con contraste real.</summary>
    private const string TokenDistancia = "PilotXPanelAccentText";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- controles de la vista ---------------------------------------------
    private readonly TextBlock _pill;
    private readonly Border _cajaAviso;
    private readonly TextBlock _txtAviso;
    private readonly Grid _paneLista;
    private readonly Grid _paneAlta;
    private readonly TextBlock _msgVacio;
    private readonly StackPanel _listaFilas;
    private readonly TextBox _inpNotas;
    private readonly Button _btnBorrar;
    private readonly Button _btnNueva;
    private readonly Button _btnImportar;
    private readonly Button _btnExportar;
    private readonly TextBox _inpLat;
    private readonly TextBox _inpLon;

    // ---- estado ------------------------------------------------------------
    private BanderasClient? _cli;
    private CancellationTokenSource? _cts;

    /// <summary>El panel arranca cerrado (lo muestra Abrir()).</summary>
    private bool _cerrada = true;

    /// <summary>Vista de alta visible (el `addOpen` del JS).</summary>
    private bool _altaAbierta;

    /// <summary>El operario editó lat/lon: no pisar con la posición del tractor.</summary>
    private bool _latLonTocado;

    /// <summary>Último estado pintado (el `lastState` del JS).</summary>
    private BanderasEstadoDto? _ultimoEstado;

    /// <summary>Valor de Notas al entrar al campo: el POST sale solo si cambió
    /// (es lo que hace el evento `change` del DOM, no el `input`).</summary>
    private string _notasAlEnfocar = "";

    /// <summary>true mientras el panel escribe en un TextBox: el TextChanged que
    /// dispara la escritura programática NO es "el operario tocó el campo"
    /// (en el DOM, setear .value tampoco levanta el evento `input`).</summary>
    private bool _pintando;

    /// <summary>Filas vivas de la lista, en el mismo orden que llegan del motor.</summary>
    private readonly List<Fila> _filas = new();

    /// <summary>El operario cerró el panel (✕). Lo engancha el host.</summary>
    public event Action? Cerrado;

    public BanderasPanel()
    {
        InitializeComponent();

        _pill        = this.FindControl<TextBlock>("PillContador")!;
        _cajaAviso   = this.FindControl<Border>("CajaAviso")!;
        _txtAviso    = this.FindControl<TextBlock>("TxtAviso")!;
        _paneLista   = this.FindControl<Grid>("PaneLista")!;
        _paneAlta    = this.FindControl<Grid>("PaneAlta")!;
        _msgVacio    = this.FindControl<TextBlock>("MsgVacio")!;
        _listaFilas  = this.FindControl<StackPanel>("ListaFilas")!;
        _inpNotas    = this.FindControl<TextBox>("InpNotas")!;
        _btnBorrar   = this.FindControl<Button>("BtnBorrar")!;
        _btnNueva    = this.FindControl<Button>("BtnNueva")!;
        _btnImportar = this.FindControl<Button>("BtnImportar")!;
        _btnExportar = this.FindControl<Button>("BtnExportar")!;
        _inpLat      = this.FindControl<TextBox>("InpLat")!;
        _inpLon      = this.FindControl<TextBox>("InpLon")!;

        // ---- Notas: teclado nativo + POST al salir del campo ----
        _inpNotas.GotFocus += (_, _) =>
        {
            _notasAlEnfocar = _inpNotas.Text ?? "";
            // El título del teclado nativo también se traduce (mismo criterio
            // que PerfilesPanel): con la pantalla en en/pt quedaba en castellano.
            _ = TecladoAsync(true, numerico: false, titulo: Traductor.T("Notas"));
        };
        _inpNotas.LostFocus += (_, _) =>
        {
            _ = TecladoAsync(false);
            ConfirmarNotas();
        };
        _inpNotas.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ConfirmarNotas();
        };

        // ---- lat/lon: teclado numérico + marca de "lo tocó el operario" ----
        _inpLat.GotFocus  += (_, _) => _ = TecladoAsync(true, numerico: true, titulo: Traductor.T("Latitud"));
        _inpLat.LostFocus += (_, _) => _ = TecladoAsync(false);
        _inpLon.GotFocus  += (_, _) => _ = TecladoAsync(true, numerico: true, titulo: Traductor.T("Longitud"));
        _inpLon.LostFocus += (_, _) => _ = TecladoAsync(false);
        _inpLat.TextChanged += (_, _) => { if (!_pintando) _latLonTocado = true; };
        _inpLon.TextChanged += (_, _) => { if (!_pintando) _latLonTocado = true; };

        // Traductor.Aplicar() cachea el PRIMER texto de cada TextBlock y lo
        // vuelve a escribir en cada pasada: sin esto, un cambio de idioma con el
        // panel abierto dejaría el contador en "0" y las distancias congeladas.
        // El Post corre DESPUÉS del Aplicar global del host y repinta lo vivo.
        Traductor.IdiomaCambio += () => Dispatcher.UIThread.Post(() =>
        {
            // Con _ultimoEstado null todavía no llegó nada del motor: repintar
            // ahí pondría el cartel de "sin conexión" sin que se haya caído
            // nada. Se deja como está y lo arregla el próximo tick del poll.
            if (_cerrada || _ultimoEstado == null) return;
            Render(_ultimoEstado);
        });
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyección tardía del client (mismo criterio que el resto de los paneles).</summary>
    public void Attach(BanderasClient client) => _cli = client;

    /// <summary>
    /// Abre el panel. <paramref name="enAlta"/> = arrancar directo en la vista
    /// de alta por lat/lon: es el equivalente del `?add=1` de la página, o sea
    /// el comando `bandera_latlon`. El comando `bandera` abre en la lista.
    /// </summary>
    public void Abrir(bool enAlta = false)
    {
        if (!_cerrada) return;         // ya abierto: no se re-abre
        _cerrada = false;
        _ultimoEstado = null;

        // Estado de arranque: la pantalla nunca aparece con datos viejos de la
        // sesión anterior (el JS arrancaba con el DOM limpio).
        LimpiarFilas();
        _msgVacio.IsVisible = true;
        _pill.Text = "0";
        _btnBorrar.IsEnabled = false;
        _inpNotas.IsEnabled = false;
        EscribirTexto(_inpNotas, "");
        EscribirTexto(_inpLat, "");
        EscribirTexto(_inpLon, "");
        Avisar("");
        MostrarAlta(enAlta);

        IsVisible = true;
        // Aplicar ANTES de traer nada: si corriera después, el Traductor se
        // guardaría los datos vivos como "texto original" del control.
        Traductor.Aplicar(this);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = BuclePollAsync(_cts.Token);
    }

    /// <summary>
    /// Cierra el panel y manda POST /close (deselecciona + guarda), que es
    /// exactamente lo que la página hacía en el pagehide. El /close viaja con
    /// token propio: si usara el del panel (ya cancelado) no saldría nunca.
    /// </summary>
    public void Cerrar()
    {
        if (_cerrada) { IsVisible = false; return; }
        _cerrada = true;

        try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
        _cts = null;

        IsVisible = false;

        var cli = _cli;
        if (cli != null)
        {
            // Si el operario cerró con un campo enfocado, el teclado nativo
            // quedaría abierto arriba del mapa.
            _ = cli.TecladoAsync(false);
            _ = cli.CerrarSesionAsync();
        }

        Cerrado?.Invoke();
    }

    /// <summary>Alias del cierre para los puntos que usan el patrón Detach().</summary>
    public void Detach() => Cerrar();

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => Cerrar();

    // =========================================================================
    //  poll (500 ms)
    // =========================================================================

    private async Task BuclePollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cli = _cli;
            BanderasEstadoDto? s = null;
            if (cli != null)
            {
                try { s = await cli.GetEstadoAsync(ct).ConfigureAwait(false); }
                // TaskCanceledException HEREDA de OperationCanceledException:
                // sin el `when`, el corte de 3 s se confundiría con el cierre
                // del panel y la pantalla quedaría congelada en sus valores.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { s = null; }
            }

            if (ct.IsCancellationRequested) return;
            var pintar = s;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_cerrada) Render(pintar);
            });

            try { await Task.Delay(PeriodoPollMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    private void OnBorrarClick(object? sender, RoutedEventArgs e)
        // Sin confirmación: la página tampoco la tenía (btnDelete → POST /delete
        // directo). No se "mejora" acá.
        => _ = EjecutarAsync((c, ct) => c.BorrarAsync(ct));

    private void OnNuevaClick(object? sender, RoutedEventArgs e) => MostrarAlta(true);

    private void OnImportarClick(object? sender, RoutedEventArgs e)
        => _ = EjecutarAsync((c, ct) => c.ImportarAsync(ct));

    private void OnExportarClick(object? sender, RoutedEventArgs e)
        => _ = EjecutarAsync((c, ct) => c.ExportarAsync(ct));

    private void OnVolverClick(object? sender, RoutedEventArgs e) => MostrarAlta(false);

    /// <summary>
    /// "Usar posición actual": vuelve a prender el prefill y copia la posición
    /// del último estado recibido, con los MISMOS 7 decimales del JS.
    /// </summary>
    private void OnUsarActualClick(object? sender, RoutedEventArgs e)
    {
        _latLonTocado = false;
        var s = _ultimoEstado;
        if (s == null) return;
        EscribirTexto(_inpLat, Coord7(s.CurLat));
        EscribirTexto(_inpLon, Coord7(s.CurLon));
    }

    private void OnAgregarRojaClick(object? sender, RoutedEventArgs e)     => _ = AgregarAsync(0);
    private void OnAgregarVerdeClick(object? sender, RoutedEventArgs e)    => _ = AgregarAsync(1);
    private void OnAgregarAmarillaClick(object? sender, RoutedEventArgs e) => _ = AgregarAsync(2);

    /// <summary>
    /// Alta de bandera. Misma decisión que addFlag() del JS: si el operario tocó
    /// lat/lon y los dos son números finitos, va por lat/lon; si no, por la
    /// posición actual del tractor.
    /// </summary>
    private async Task AgregarAsync(int color)
    {
        var cli = _cli;
        var ct = _cts?.Token ?? CancellationToken.None;
        if (cli == null) { Avisar("Sin conexión con PilotX."); return; }

        double lat = ParseFloatJs(_inpLat.Text);
        double lon = ParseFloatJs(_inpLon.Text);
        bool porLatLon = _latLonTocado && Finito(lat) && Finito(lon);

        BanderasEstadoDto? s;
        try
        {
            s = porLatLon
                ? await cli.AgregarEnLatLonAsync(lat, lon, color, ct).ConfigureAwait(true)
                : await cli.AgregarEnPosicionActualAsync(color, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { s = null; }

        if (_cerrada) return;

        // Igual que el JS: la vista de alta se cierra SOLO si el motor aceptó.
        if (s != null && s.Ok && string.IsNullOrEmpty(s.Error)) MostrarAlta(false);
        Render(s);
    }

    /// <summary>POST + pintado con la respuesta (todos los endpoints devuelven
    /// el estado nuevo, así que no hace falta esperar el próximo poll).</summary>
    private async Task EjecutarAsync(Func<BanderasClient, CancellationToken, Task<BanderasEstadoDto?>> accion)
    {
        var cli = _cli;
        var ct = _cts?.Token ?? CancellationToken.None;
        if (cli == null) { Avisar("Sin conexión con PilotX."); return; }

        BanderasEstadoDto? s;
        try { s = await accion(cli, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { s = null; }

        if (_cerrada) return;
        Render(s);
    }

    /// <summary>Notas: sale el POST solo si el texto cambió desde que se entró
    /// al campo (es lo que hace el evento `change` del DOM).</summary>
    private void ConfirmarNotas()
    {
        if (!_inpNotas.IsEnabled) return;
        string ahora = _inpNotas.Text ?? "";
        if (ahora == _notasAlEnfocar) return;
        _notasAlEnfocar = ahora;
        _ = EjecutarAsync((c, ct) => c.NotasAsync(ahora, ct));
    }

    private Task TecladoAsync(bool abrir, bool numerico = true, string titulo = "")
        => _cli?.TecladoAsync(abrir, numerico, titulo) ?? Task.CompletedTask;

    // =========================================================================
    //  pintado (el render() del JS, línea por línea)
    // =========================================================================

    private void Render(BanderasEstadoDto? s)
    {
        // `if (!s || s.ok === false) { warn('Sin conexión con PilotX.'); return; }`
        // OJO: el motor manda ok:false en CUALQUIER rechazo (sin-lote,
        // sin-seleccion, no-disponible-sin-ui), así que el operario ve
        // "Sin conexión" también ahí. Es el comportamiento de la página: se
        // replica tal cual.
        if (s == null || !s.Ok) { Avisar("Sin conexión con PilotX."); return; }
        _ultimoEstado = s;

        var flags = s.Flags ?? new List<BanderaItemDto>();
        _pill.Text = flags.Count.ToString(Inv);
        Avisar(!s.HasField ? "Abrí primero un lote para usar banderas." : (s.Error ?? ""));

        RenderFilas(flags, s.Picked);

        // Notas de la seleccionada (no pisar mientras se edita).
        BanderaItemDto? sel = null;
        foreach (var f in flags) if (f.Number == s.Picked) { sel = f; break; }
        bool haySel = sel != null;
        _btnBorrar.IsEnabled = haySel;
        _inpNotas.IsEnabled = haySel;
        if (!_inpNotas.IsFocused)
        {
            string txt = haySel ? (sel!.Notes ?? "") : "";
            EscribirTexto(_inpNotas, txt);
            _notasAlEnfocar = txt;
        }

        _btnNueva.IsEnabled = s.HasField;
        _btnImportar.IsEnabled = s.HasField;
        _btnExportar.IsEnabled = flags.Count > 0;

        // Prefill lat/lon con la posición actual hasta que el operario la toque.
        if (_altaAbierta && !_latLonTocado && !_inpLat.IsFocused && !_inpLon.IsFocused)
        {
            EscribirTexto(_inpLat, Coord7(s.CurLat));
            EscribirTexto(_inpLon, Coord7(s.CurLon));
        }
    }

    /// <summary>
    /// Filas de la lista. El JS reconstruía el DOM entero en cada tick; acá se
    /// reconstruye SOLO si cambió la cantidad y el resto se actualiza en el
    /// lugar. Motivo táctil: a 500 ms, reemplazar el control entre el apoyar y
    /// el levantar el dedo se come el toque, y el toque es el que selecciona la
    /// bandera. Lo que se ve es idéntico.
    /// </summary>
    private void RenderFilas(List<BanderaItemDto> flags, int picked)
    {
        _msgVacio.IsVisible = flags.Count == 0;

        if (_filas.Count != flags.Count)
        {
            LimpiarFilas();
            for (int i = 0; i < flags.Count; i++)
            {
                var fila = CrearFila();
                _filas.Add(fila);
                _listaFilas.Children.Add(fila.Raiz);
            }
        }

        for (int i = 0; i < flags.Count; i++)
        {
            var f = flags[i];
            var fila = _filas[i];
            bool sel = f.Number == picked;

            fila.Number = f.Number;
            fila.Punto.Fill = PincelDeColor(f.Color);
            fila.Nombre.Text = string.IsNullOrEmpty(f.Notes)
                ? "#" + f.Id.ToString(Inv)
                : f.Notes!;
            fila.Dist.Text = FmtDist(f.DistanceM);
            fila.Raiz.Background = Pincel(sel ? TokenFondoSel : TokenFondo);
            fila.Raiz.BorderBrush = Pincel(sel ? TokenBordeSel : TokenBorde);
            fila.Raiz.BorderThickness = new Thickness(sel ? 2 : 1);
        }
    }

    private void LimpiarFilas()
    {
        _filas.Clear();
        _listaFilas.Children.Clear();
    }

    private Fila CrearFila()
    {
        var fila = new Fila();

        fila.Punto = new Ellipse
        {
            Width = 12, Height = 12,
            Stroke = Pincel(TokenBorde), StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        fila.Nombre = new TextBlock
        {
            FontSize = 14, Foreground = Pincel(TokenTexto),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        fila.Dist = new TextBlock
        {
            FontFamily = new FontFamily(Mono), FontSize = 14, FontWeight = FontWeight.Bold,
            Foreground = Pincel(TokenDistancia),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right, MinWidth = 78,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var celda = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(fila.Punto, 0);
        Grid.SetColumn(fila.Nombre, 1);
        Grid.SetColumn(fila.Dist, 2);
        celda.Children.Add(fila.Punto);
        celda.Children.Add(fila.Nombre);
        celda.Children.Add(fila.Dist);

        fila.Raiz = new Border
        {
            Child = celda,
            Background = Pincel(TokenFondo),
            BorderBrush = Pincel(TokenBorde),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            // 48 de alto útil: piso táctil para el dedo con guante.
            Padding = new Thickness(9, 11, 9, 11),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        fila.Raiz.Tapped += (_, _) =>
        {
            int n = fila.Number;
            if (n <= 0) return;
            _ = EjecutarAsync((c, ct) => c.PickAsync(n, ct));
        };

        return fila;
    }

    /// <summary>El warn() del JS: mismo texto, misma regla de visibilidad.</summary>
    private void Avisar(string? mensaje)
    {
        bool hay = !string.IsNullOrEmpty(mensaje);
        _txtAviso.Text = hay ? Traductor.T(mensaje) : "";
        _cajaAviso.IsVisible = hay;
    }

    /// <summary>showAdd() del JS: cambia de vista y resetea el "lo tocó el operario".</summary>
    private void MostrarAlta(bool on)
    {
        _altaAbierta = on;
        _latLonTocado = false;
        _paneLista.IsVisible = !on;
        _paneAlta.IsVisible = on;
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    /// <summary>Escritura programática en un TextBox: no cuenta como "el
    /// operario tocó el campo" (setear .value en el DOM tampoco levanta el
    /// evento `input`).</summary>
    private void EscribirTexto(TextBox caja, string texto)
    {
        if ((caja.Text ?? "") == texto) return;
        _pintando = true;
        caja.Text = texto;
        _pintando = false;
    }

    /// <summary>
    /// Pincel por clave de recurso: primero los propios de esta card (los tres
    /// colores de bandera), después los tokens del theme que viven en las
    /// Resources de la Application. Nunca tira: si faltara la clave, la fila se
    /// dibuja sin ese color en vez de voltear la pantalla del tractor.
    /// </summary>
    private IBrush Pincel(string clave)
    {
        if (this.TryFindResource(clave, out var propio) && propio is IBrush b) return b;
        if (Application.Current is { } app
            && app.TryGetResource(clave, null, out var global) && global is IBrush g) return g;
        return Brushes.Transparent;
    }

    /// <summary>0 = roja, 1 = verde, 2 = amarilla. Cualquier otro código cae en
    /// roja, igual que el `c` + (f.color || 0) del HTML (que sin clase conocida
    /// dejaba el punto sin fondo; acá se prefiere mostrar algo).</summary>
    private IBrush PincelDeColor(int color) => color switch
    {
        1 => Pincel("BanderaVerde"),
        2 => Pincel("BanderaAmarilla"),
        _ => Pincel("BanderaRoja"),
    };

    /// <summary>fmtDist() del JS, con los mismos cortes y decimales.</summary>
    private static string FmtDist(double m)
    {
        if (double.IsNaN(m) || double.IsInfinity(m)) return "—";
        if (m >= 1000) return (m / 1000).ToString("F2", Inv) + " km";
        return m.ToString("F1", Inv) + " m";
    }

    /// <summary>`(valor || 0).toFixed(7)` del JS: 7 decimales, punto decimal
    /// SIEMPRE (con es-AR un ToString() suelto pondría coma).</summary>
    private static string Coord7(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;   // NaN es falsy en JS
        return v.ToString("F7", Inv);
    }

    private static bool Finito(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>
    /// `parseFloat(String(x).replace(',', '.'))` del JS, al pie de la letra:
    ///   · replace de String reemplaza SOLO la primera coma;
    ///   · parseFloat saltea espacios de la izquierda y se queda con el prefijo
    ///     numérico ("12,5 m" → 12.5), donde double.TryParse fallaría.
    /// Devuelve NaN si no hay número, que es lo que hace caer el alta en
    /// "usar posición actual".
    /// </summary>
    private static double ParseFloatJs(string? texto)
    {
        string s = texto ?? "";
        int coma = s.IndexOf(',');
        if (coma >= 0) s = s.Substring(0, coma) + "." + s.Substring(coma + 1);
        s = s.TrimStart();

        int i = 0, n = s.Length, digitos = 0;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        while (i < n && char.IsAsciiDigit(s[i])) { i++; digitos++; }
        if (i < n && s[i] == '.')
        {
            i++;
            while (i < n && char.IsAsciiDigit(s[i])) { i++; digitos++; }
        }
        if (digitos == 0) return double.NaN;

        int fin = i;
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
            int dexp = 0;
            while (j < n && char.IsAsciiDigit(s[j])) { j++; dexp++; }
            if (dexp > 0) fin = j;
        }

        return double.TryParse(s.Substring(0, fin), NumberStyles.Float, Inv, out var v)
            ? v : double.NaN;
    }

    /// <summary>Una fila de la lista, con sus partes vivas.</summary>
    private sealed class Fila
    {
        public Border Raiz = null!;
        public Ellipse Punto = null!;
        public TextBlock Nombre = null!;
        public TextBlock Dist = null!;

        /// <summary>Number 1-based de la bandera que está mostrando la fila.</summary>
        public int Number;
    }
}
