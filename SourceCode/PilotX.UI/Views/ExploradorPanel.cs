// ============================================================================
// ExploradorPanel.cs — el explorador de archivos PROPIO de PilotX (card nativa
// "Elegir/Guardar archivo"). Reemplaza al StorageProvider de Windows en los
// puntos donde el operario carga firmwares, shapefiles o sonidos, o guarda
// exports: el diálogo del sistema está fuera de branding, no es táctil y en
// kiosko es una vía de escape de la app.
//
// Spec aprobado: docs/superpowers/specs/2026-08-20-explorador-archivos-design.md
//
// Diseño:
//   · Dos columnas: LUGARES (izq) + ARCHIVOS (der). Lugares = los USB
//     enchufados arriba de todo (VolumeLabel + tamaño) y las carpetas de
//     PilotX (Firmwares/Lotes/Documentos/Descargas). NADA de "Este equipo" ni
//     C:\ crudo: el operario no navega el disco entero.
//   · Archivos: solo las extensiones que pide el llamador, carpetas
//     navegables con fila "▸ .." (clampeada a la raíz del lugar), orden
//     carpetas primero y después por fecha desc (lo último copiado al USB
//     arriba). Filas táctiles ≥48 px.
//   · Modo guardar: campo de nombre con el TECLADO PROPIO
//     (POST /api/teclado/abrir|cerrar — jamás osk.exe) y confirmación INLINE
//     (Border, no MessageBox) si el archivo ya existe.
//   · USB en vivo: DispatcherTimer de 2 s SOLO mientras la card está visible
//     (el aviso global de conectar/retirar lo da el watcher de MainWindow).
//
// API (la consumen los paneles vía ExploradorArchivos, el service estático):
//   Task<string[]?> ElegirAsync(titulo, extensiones[], multiple=false)
//   Task<string?>   GuardarAsync(titulo, nombreSugerido, extension)
// Ambas muestran la card, esperan la elección (TaskCompletionSource) y
// devuelven null si se canceló con ✕ (o si la card se cerró desde afuera).
//
// Acople: MainWindow publica la única instancia en `Instancia` (static),
// igual que publica el toast vía el evento Aviso. Se eligió esto y no pasar
// el panel por Attach() porque habría que tocar 4 firmas de Attach + los dos
// caminos de embebido de ConfigPanel para un colaborador que es único por
// ventana. En Android/Linux Instancia queda null y los llamadores caen al
// StorageProvider del sistema (ver ExploradorArchivos.CardDisponible).
//
// NADA de ComboBox/Flyout/MenuFlyout: sobre el mapa GL no se dibujan y ni
// siquiera logean error. Todo es Border + IsVisible. El mapa NUNCA se apaga:
// esta card es un overlay más, con scrim, y el mapa queda vivo detrás.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PilotX.Desktop.Views;

public sealed class ExploradorPanel : Border
{
    // ---- acceso global (lo setea MainWindow al armar la ventana) -----------
    public static ExploradorPanel? Instancia;

    /// <summary>Base del Hub para la señal del teclado nativo (la setea
    /// MainWindow con DeriveOrigin(App.TargetUrl)).</summary>
    public string BaseUrl = "http://127.0.0.1:5180/";

    // ---- paleta (tokens PilotXPanel* del theme, con fallback a los hex
    //      oficiales — mismo helper Rec que CalculadoraSiembraPanel) ---------
    private IBrush _card       = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private IBrush _superficie = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private IBrush _superficie2= new SolidColorBrush(Color.Parse("#EDF1EC"));
    private IBrush _borde      = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private IBrush _bordeAlto  = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private IBrush _texto      = new SolidColorBrush(Color.Parse("#101612"));
    private IBrush _dim        = new SolidColorBrush(Color.Parse("#535E54"));
    private IBrush _acento     = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private IBrush _okTexto    = new SolidColorBrush(Color.Parse("#2F7A26"));
    private IBrush _okSuave    = new SolidColorBrush(Color.Parse("#E8F4E5"));
    private IBrush _err        = new SolidColorBrush(Color.Parse("#C0261F"));
    private IBrush _warnSuave  = new SolidColorBrush(Color.Parse("#FBF1DC"));
    private bool _paletaResuelta;

    private static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    // ---- estado de la sesión de elección -----------------------------------
    private TaskCompletionSource<string[]?>? _tcs;
    private bool _modoGuardar;
    private bool _multiple;
    private string[] _extensiones = Array.Empty<string>();
    private string _extGuardar = "";
    private string _tituloPedido = "";
    private string? _rutaPendiente;                 // guardar: esperando confirmar pisada
    private readonly HashSet<string> _seleccion = new(StringComparer.OrdinalIgnoreCase);

    // ---- lugares -----------------------------------------------------------
    private sealed class Lugar
    {
        public string Nombre = "";
        public string Detalle = "";      // "14,2 GB" en USBs; "" en carpetas
        public string Raiz = "";
        public bool EsUsb;
    }

    private readonly List<Lugar> _lugares = new();
    private Lugar? _lugarActivo;
    private string _dirActual = "";

    // ---- controles ---------------------------------------------------------
    private readonly TextBlock _titulo;
    private readonly TextBlock _subtitulo;
    private readonly StackPanel _lugaresHost;
    private readonly StackPanel _archivosHost;
    private readonly TextBlock _rutaLabel;
    private readonly TextBlock _lblLugares;
    private readonly Border _zonaGuardar;
    private readonly TextBox _nombreBox;
    private readonly TextBlock _extLabel;
    private readonly Button _btnGuardar;
    private readonly Border _confirmBox;
    private readonly TextBlock _confirmTexto;
    private readonly Button _btnReemplazar;
    private readonly Button _btnConfirmCancelar;
    private readonly Border _zonaMultiple;
    private readonly Button _btnUsarSeleccion;

    // ---- USB en vivo -------------------------------------------------------
    private DispatcherTimer? _pollUsb;

    // ---- idioma ------------------------------------------------------------
    private bool _idiomaEnganchado;

    // ---- teclado nativo ----------------------------------------------------
    // HttpClient propio y estático: una sola señal chica, sin dueño externo.
    private static readonly HttpClient Http = new HttpClient();

    public ExploradorPanel()
    {
        // Scrim: el mapa se ve detrás pero atenuado, y el toque no atraviesa.
        Background = new SolidColorBrush(Color.Parse("#66101612"));
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // ------------------- header -------------------
        _titulo = new TextBlock
        {
            Text = "", Foreground = _texto,
            FontSize = 22, FontWeight = FontWeight.SemiBold
        };
        _subtitulo = new TextBlock
        {
            Text = "", Foreground = _dim, FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var headerTextos = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        headerTextos.Children.Add(_titulo);
        headerTextos.Children.Add(_subtitulo);

        var btnCerrar = new Button
        {
            Content = "✕",
            Background = _superficie,
            Foreground = _dim,
            BorderBrush = _bordeAlto,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Width = 48, Height = 48,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        btnCerrar.Click += (_, __) => Cancelar();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 0, 2, 14) };
        Grid.SetColumn(headerTextos, 0);
        Grid.SetColumn(btnCerrar, 1);
        header.Children.Add(headerTextos);
        header.Children.Add(btnCerrar);

        // ------------------- columna LUGARES -------------------
        _lblLugares = new TextBlock
        {
            Text = T("LUGARES"), Foreground = _dim, FontSize = 11,
            FontWeight = FontWeight.Medium, LetterSpacing = 1.1,
            Margin = new Thickness(2, 0, 0, 8)
        };
        _lugaresHost = new StackPanel { Spacing = 6 };
        var lugaresScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _lugaresHost
        };
        var colLugares = new DockPanel();
        DockPanel.SetDock(_lblLugares, Dock.Top);
        colLugares.Children.Add(_lblLugares);
        colLugares.Children.Add(lugaresScroll);

        // ------------------- columna ARCHIVOS -------------------
        _rutaLabel = new TextBlock
        {
            Text = "", Foreground = _dim, FontSize = 12, FontFamily = Mono,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 0, 8)
        };
        _archivosHost = new StackPanel { Spacing = 0 };
        var archivosScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _archivosHost
        };
        var marcoArchivos = new Border
        {
            Background = _superficie,
            BorderBrush = _borde,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = archivosScroll
        };
        var colArchivos = new DockPanel();
        DockPanel.SetDock(_rutaLabel, Dock.Top);
        colArchivos.Children.Add(_rutaLabel);
        colArchivos.Children.Add(marcoArchivos);

        var cuerpo = new Grid { ColumnDefinitions = new ColumnDefinitions("280,16,*") };
        Grid.SetColumn(colLugares, 0);
        Grid.SetColumn(colArchivos, 2);
        cuerpo.Children.Add(colLugares);
        cuerpo.Children.Add(colArchivos);

        // ------------------- zona GUARDAR (dock abajo, solo en modo guardar) --
        _nombreBox = new TextBox
        {
            Text = "", Watermark = T("Nombre del archivo"),
            MinHeight = 48, FontSize = 15,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = _superficie,
            Foreground = _texto,
            BorderBrush = _bordeAlto
        };
        // Teclado propio de PilotX por foco — misma señal HTTP que mandan las
        // páginas del Hub (patrón BanderasPanel). Nunca osk.exe.
        _nombreBox.GotFocus  += (_, __) => _ = TecladoAsync(true, T("Nombre del archivo"));
        _nombreBox.LostFocus += (_, __) => _ = TecladoAsync(false);
        _nombreBox.TextChanged += (_, __) =>
        {
            OcultarConfirmacion();
            ActualizarBotonGuardar();
        };

        _extLabel = new TextBlock
        {
            Text = "", Foreground = _dim, FontSize = 15, FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };

        _btnGuardar = new Button
        {
            Content = T("Guardar"),
            Background = _acento,
            Foreground = _texto,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            MinHeight = 48, MinWidth = 140,
            FontSize = 15, FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = false
        };
        _btnGuardar.Click += (_, __) => PedirGuardar();

        var filaGuardar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(_nombreBox, 0);
        Grid.SetColumn(_extLabel, 1);
        Grid.SetColumn(_btnGuardar, 2);
        filaGuardar.Children.Add(_nombreBox);
        filaGuardar.Children.Add(_extLabel);
        filaGuardar.Children.Add(_btnGuardar);

        // Confirmación inline de pisada (Border + IsVisible, no MessageBox).
        _confirmTexto = new TextBlock
        {
            Text = "", Foreground = _texto, FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        _btnReemplazar = new Button
        {
            Content = T("Reemplazar"),
            Background = _err, Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            MinHeight = 44, Padding = new Thickness(18, 0, 18, 0),
            FontWeight = FontWeight.SemiBold
        };
        _btnReemplazar.Click += (_, __) =>
        {
            var ruta = _rutaPendiente;
            if (ruta != null) Resolver(new[] { ruta });
        };
        _btnConfirmCancelar = new Button
        {
            Content = T("Cancelar"),
            Background = _superficie, Foreground = _texto,
            BorderBrush = _bordeAlto, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinHeight = 44, Padding = new Thickness(18, 0, 18, 0)
        };
        _btnConfirmCancelar.Click += (_, __) => OcultarConfirmacion();

        var confirmBotones = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        confirmBotones.Children.Add(_btnConfirmCancelar);
        confirmBotones.Children.Add(_btnReemplazar);

        var confirmGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_confirmTexto, 0);
        Grid.SetColumn(confirmBotones, 1);
        confirmGrid.Children.Add(_confirmTexto);
        confirmGrid.Children.Add(confirmBotones);

        _confirmBox = new Border
        {
            IsVisible = false,
            Background = _warnSuave,
            BorderBrush = _bordeAlto,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = confirmGrid
        };

        var pilaGuardar = new StackPanel { Spacing = 0 };
        pilaGuardar.Children.Add(filaGuardar);
        pilaGuardar.Children.Add(_confirmBox);

        _zonaGuardar = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 14, 0, 0),
            Child = pilaGuardar
        };

        // ------------------- zona MÚLTIPLE (dock abajo) -------------------
        _btnUsarSeleccion = new Button
        {
            Content = "",
            Background = _acento,
            Foreground = _texto,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            MinHeight = 48, MinWidth = 220,
            FontSize = 15, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = false
        };
        _btnUsarSeleccion.Click += (_, __) =>
        {
            if (_seleccion.Count == 0) return;
            var rutas = new string[_seleccion.Count];
            _seleccion.CopyTo(rutas);
            Resolver(rutas);
        };
        _zonaMultiple = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 14, 0, 0),
            Child = _btnUsarSeleccion
        };

        // ------------------- card -------------------
        var pilaAbajo = new StackPanel { Spacing = 0 };
        pilaAbajo.Children.Add(_zonaGuardar);
        pilaAbajo.Children.Add(_zonaMultiple);

        var raiz = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(pilaAbajo, Dock.Bottom);
        raiz.Children.Add(header);
        raiz.Children.Add(pilaAbajo);
        raiz.Children.Add(cuerpo);

        Child = new Border
        {
            Background = _card,
            BorderBrush = _bordeAlto,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612"),
            Padding = new Thickness(20, 16, 20, 18),
            MaxWidth = 1040, MaxHeight = 660,
            MinHeight = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 8),
            Child = raiz
        };
    }

    // =========================================================================
    //  API pública
    // =========================================================================

    /// <summary>Card en modo ELEGIR. Devuelve la(s) ruta(s) elegida(s) o null
    /// si el operario canceló con ✕ (o algo cerró la card desde afuera).</summary>
    public Task<string[]?> ElegirAsync(string titulo, string[] extensiones, bool multiple = false)
        => AbrirAsync(titulo, extensiones, multiple, guardar: false, nombreSugerido: "", extension: "");

    /// <summary>Card en modo GUARDAR. Devuelve la ruta destino (con la
    /// extensión puesta) o null si canceló. La confirmación de pisar un archivo
    /// existente ya pasó acá adentro: si devuelve ruta, se puede escribir.</summary>
    public async Task<string?> GuardarAsync(string titulo, string nombreSugerido, string extension)
    {
        var r = await AbrirAsync(titulo, new[] { extension }, multiple: false,
                                 guardar: true, nombreSugerido: nombreSugerido, extension: extension)
                .ConfigureAwait(true);
        return (r != null && r.Length > 0) ? r[0] : null;
    }

    /// <summary>Cierra la card resolviendo null (lo usan el ✕, Escape y la
    /// flecha ← de MainWindow).</summary>
    public void Cancelar() => Resolver(null);

    // =========================================================================
    //  apertura / cierre
    // =========================================================================

    private Task<string[]?> AbrirAsync(string titulo, string[] extensiones, bool multiple,
                                       bool guardar, string nombreSugerido, string extension)
    {
        // Si quedó una espera colgada (no debería: la card es modal), se suelta
        // como cancelada antes de arrancar la nueva.
        _tcs?.TrySetResult(null);
        _tcs = new TaskCompletionSource<string[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        ResolverPaleta();

        _modoGuardar = guardar;
        _multiple = multiple && !guardar;
        _extGuardar = guardar ? NormalizarExt(extension) : "";
        _extensiones = NormalizarExts(extensiones);
        _tituloPedido = titulo ?? "";
        _seleccion.Clear();
        _rutaPendiente = null;

        _zonaGuardar.IsVisible = _modoGuardar;
        _zonaMultiple.IsVisible = _multiple;
        OcultarConfirmacion();

        if (_modoGuardar)
        {
            _extLabel.Text = _extGuardar;
            _nombreBox.Text = LimpiarNombre(QuitarExtSugerida(nombreSugerido));
            ActualizarBotonGuardar();
        }

        RepintarTextosFijos();

        // Lugares + primer listado. El lugar inicial es el primer USB si hay
        // uno enchufado (a eso vino el operario), si no la primera carpeta.
        ArmarLugares();
        _lugarActivo = _lugares.Count > 0 ? _lugares[0] : null;
        _dirActual = _lugarActivo?.Raiz ?? "";
        RenderLugares();
        RenderArchivos();

        EngancharIdioma();
        ArrancarPollUsb();
        IsVisible = true;

        return _tcs.Task;
    }

    private void Resolver(string[]? resultado)
    {
        // _tcs se vacía ANTES de ocultar: OnPropertyChanged(IsVisible=false)
        // resuelve null cualquier espera pendiente, y acá la respuesta es otra.
        var tcs = _tcs;
        _tcs = null;
        IsVisible = false;
        tcs?.TrySetResult(resultado);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            // Red de seguridad: si ALGO ocultó la card desde afuera (cambio de
            // pantalla, cierre de la ventana) la espera no puede quedar viva —
            // el panel llamador se colgaría esperando un archivo que no viene.
            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(null);
            PararPollUsb();
            SoltarIdioma();
            _ = TecladoAsync(false);
        }
    }

    // =========================================================================
    //  lugares
    // =========================================================================

    private void ArmarLugares()
    {
        _lugares.Clear();

        // USB primero — a eso vino el operario. DriveInfo, sin WMI/registro.
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Removable) continue;
                bool listo; string etiqueta = ""; long tam = 0; string raiz = "";
                try
                {
                    listo = d.IsReady;
                    if (listo) { etiqueta = d.VolumeLabel; tam = d.TotalSize; raiz = d.RootDirectory.FullName; }
                }
                catch { listo = false; }
                if (!listo) continue;

                if (string.IsNullOrWhiteSpace(etiqueta))
                    etiqueta = "USB " + d.Name.TrimEnd('\\', '/');
                _lugares.Add(new Lugar
                {
                    Nombre = etiqueta,
                    Detalle = FmtTamano(tam),
                    Raiz = raiz,
                    EsUsb = true
                });
            }
        }
        catch { /* GetDrives puede fallar con un lector a medio enumerar */ }

        // Carpetas de PilotX. Solo las que EXISTEN: un lugar que tira error al
        // entrar es peor que no ofrecerlo.
        AgregarCarpeta(T("Firmwares"), DirFirmwares());
        AgregarCarpeta(T("Lotes"), DirSiExiste(Path.Combine(DocsAgOpenGPS(), "Fields")));
        // Documentos es NUESTRA carpeta: en modo guardar se crea si falta,
        // porque es el destino por defecto de los exports.
        string docs = DocsAgOpenGPS();
        if (_modoGuardar && !string.IsNullOrEmpty(docs))
        {
            try { Directory.CreateDirectory(docs); } catch { }
        }
        AgregarCarpeta(T("Documentos"), DirSiExiste(docs));
        AgregarCarpeta(T("Descargas"), DirSiExiste(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")));
    }

    private void AgregarCarpeta(string nombre, string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        _lugares.Add(new Lugar { Nombre = nombre, Raiz = dir!, EsUsb = false });
    }

    private static string? DirSiExiste(string dir)
    {
        try { return Directory.Exists(dir) ? dir : null; } catch { return null; }
    }

    /// <summary>Documentos\AgOpenGPS — la misma raíz de datos que usa el motor
    /// (RegistrySettings: MyDocuments\AgOpenGPS).</summary>
    private static string DocsAgOpenGPS()
    {
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(docs) ? "" : Path.Combine(docs, "AgOpenGPS");
        }
        catch { return ""; }
    }

    /// <summary>Cache local de firmwares (lo que llena FirmwareMirror y sirve
    /// FirmwareLanServer). Vive junto al ENGINE, no junto al Desktop: mismo
    /// layout &lt;install&gt;\Engine\ que usa App.SupervisarEngine para encontrar
    /// el exe. El fallback junto al Desktop cubre el layout dev (todo junto).</summary>
    private static string? DirFirmwares()
    {
        try
        {
            string d = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "Engine", "firmware-cache"));
            if (Directory.Exists(d)) return d;
            d = Path.Combine(AppContext.BaseDirectory, "firmware-cache");
            if (Directory.Exists(d)) return d;
        }
        catch { }
        return null;
    }

    private void RenderLugares()
    {
        _lugaresHost.Children.Clear();

        if (_lugares.Count == 0)
        {
            _lugaresHost.Children.Add(new TextBlock
            {
                Text = T("Conectá un USB o creá las carpetas de PilotX."),
                Foreground = _dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 0)
            });
            return;
        }

        foreach (var l in _lugares)
        {
            var l2 = l;
            bool activo = ReferenceEquals(l, _lugarActivo);

            // Glifos BMP seguros (▮ ▸), nunca emoji astral: en la fuente de
            // cabina el emoji sale como tofu.
            var icono = new TextBlock
            {
                Text = l.EsUsb ? "▮" : "▸",
                Foreground = l.EsUsb ? (activo ? _okTexto : _acento) : _dim,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            var textos = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textos.Children.Add(new TextBlock
            {
                Text = l.Nombre, Foreground = _texto, FontSize = 14,
                FontWeight = activo ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrEmpty(l.Detalle))
            {
                textos.Children.Add(new TextBlock
                {
                    Text = l.Detalle, Foreground = _dim, FontSize = 11
                });
            }

            var fila = new StackPanel { Orientation = Orientation.Horizontal };
            fila.Children.Add(icono);
            fila.Children.Add(textos);

            var b = new Button
            {
                Content = fila,
                Background = activo ? _okSuave : _superficie,
                BorderBrush = activo ? _okTexto : _bordeAlto,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinHeight = 52,
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            b.Click += (_, __) => IrALugar(l2);
            _lugaresHost.Children.Add(b);
        }
    }

    private void IrALugar(Lugar l)
    {
        _lugarActivo = l;
        _dirActual = l.Raiz;
        OcultarConfirmacion();
        RenderLugares();
        RenderArchivos();
    }

    // =========================================================================
    //  USB en vivo (poll de 2 s SOLO con la card visible)
    // =========================================================================

    private void ArrancarPollUsb()
    {
        if (_pollUsb != null) return;
        _pollUsb = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollUsb.Tick += (_, __) => RefrescarLugares();
        _pollUsb.Start();
    }

    private void PararPollUsb()
    {
        _pollUsb?.Stop();
        _pollUsb = null;
    }

    private void RefrescarLugares()
    {
        string raizActiva = _lugarActivo?.Raiz ?? "";
        ArmarLugares();

        // Reencontrar el lugar activo por raíz (los objetos son nuevos).
        _lugarActivo = null;
        foreach (var l in _lugares)
            if (string.Equals(l.Raiz, raizActiva, StringComparison.OrdinalIgnoreCase))
            { _lugarActivo = l; break; }

        if (_lugarActivo == null)
        {
            // El lugar donde estaba parado desapareció (sacaron el USB):
            // se cae al primer lugar disponible y se avisa re-listando.
            _lugarActivo = _lugares.Count > 0 ? _lugares[0] : null;
            _dirActual = _lugarActivo?.Raiz ?? "";
            _seleccion.Clear();
            ActualizarBotonSeleccion();
            RenderArchivos();
        }
        else if (!Directory.Exists(_dirActual))
        {
            // Mismo USB pero la subcarpeta ya no está (re-enchufe, formateo).
            _dirActual = _lugarActivo.Raiz;
            RenderArchivos();
        }
        RenderLugares();
    }

    // =========================================================================
    //  archivos
    // =========================================================================

    private void RenderArchivos()
    {
        _archivosHost.Children.Clear();

        if (_lugarActivo == null)
        {
            _rutaLabel.Text = "—";
            _archivosHost.Children.Add(CajaAviso(T("No hay lugares disponibles"),
                T("Conectá un USB — la lista se actualiza sola.")));
            return;
        }

        _rutaLabel.Text = RutaVisible();

        List<DirectoryInfo> dirs = new();
        List<FileInfo> archivos = new();
        try
        {
            var di = new DirectoryInfo(_dirActual);
            foreach (var d in di.GetDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                dirs.Add(d);
            }
            foreach (var f in di.GetFiles())
            {
                if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (!ExtensionPedida(f.Name)) continue;
                archivos.Add(f);
            }
        }
        catch (Exception)
        {
            _archivosHost.Children.Add(CajaAviso(T("No se pudo leer la carpeta"),
                T("Probá de nuevo o elegí otro lugar.")));
            return;
        }

        // Carpetas primero y todo por fecha DESC: lo último copiado al USB
        // queda arriba, que es lo que el operario está buscando.
        dirs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        archivos.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

        bool enRaiz = RutasIguales(_dirActual, _lugarActivo.Raiz);
        bool hayFilas = false;
        if (!enRaiz)
        {
            var subir = Fila("▸", "..", "", "", negrita: false);
            subir.Click += (_, __) => Subir();
            _archivosHost.Children.Add(subir);
            hayFilas = true;
        }

        if (dirs.Count == 0 && archivos.Count == 0)
        {
            _archivosHost.Children.Add(CajaAviso(
                T("Vacío"),
                string.Format(CultureInfo.InvariantCulture,
                    T("No hay archivos {0} en esta carpeta."), string.Join(" ", _extensiones))));
            return;
        }

        foreach (var d in dirs)
        {
            if (hayFilas) _archivosHost.Children.Add(Separador());
            hayFilas = true;
            var d2 = d;
            var fila = Fila("▸", d.Name, FmtFecha(d.LastWriteTime), "—", negrita: false);
            fila.Click += (_, __) => Entrar(d2.FullName);
            _archivosHost.Children.Add(fila);
        }

        foreach (var f in archivos)
        {
            if (hayFilas) _archivosHost.Children.Add(Separador());
            hayFilas = true;
            var f2 = f;
            var fila = Fila("▪", f.Name, FmtFecha(f.LastWriteTime), FmtTamano(f.Length), negrita: true);
            if (_multiple && _seleccion.Contains(f.FullName))
                MarcarFila(fila, true);
            fila.Click += (_, __) => TocarArchivo(f2.FullName, f2.Name, fila);
            _archivosHost.Children.Add(fila);
        }
    }

    private void TocarArchivo(string ruta, string nombre, Button fila)
    {
        if (_modoGuardar)
        {
            // Tocar un archivo existente pone su nombre en el campo — mismo
            // gesto que el diálogo de guardar clásico. Confirmación al Guardar.
            _nombreBox.Text = QuitarExtSugerida(nombre);
            ActualizarBotonGuardar();
            return;
        }

        if (_multiple)
        {
            if (!_seleccion.Add(ruta)) _seleccion.Remove(ruta);
            MarcarFila(fila, _seleccion.Contains(ruta));
            ActualizarBotonSeleccion();
            return;
        }

        Resolver(new[] { ruta });
    }

    private void MarcarFila(Button fila, bool marcada)
    {
        fila.Background = marcada ? _okSuave : _superficie;
        var g = fila.Content as Grid;
        if (g != null && g.Children.Count > 0 && g.Children[0] is TextBlock ic)
            ic.Foreground = marcada ? _okTexto : _dim;
    }

    private void ActualizarBotonSeleccion()
    {
        _btnUsarSeleccion.IsEnabled = _seleccion.Count > 0;
        _btnUsarSeleccion.Content = string.Format(CultureInfo.InvariantCulture,
            T("Usar seleccionados ({0})"), _seleccion.Count);
    }

    private void Entrar(string dir)
    {
        _dirActual = dir;
        RenderArchivos();
    }

    private void Subir()
    {
        if (_lugarActivo == null) return;
        // Clamp a la raíz del lugar: el ".." nunca escapa a C:\ crudo.
        if (RutasIguales(_dirActual, _lugarActivo.Raiz)) return;
        try
        {
            var padre = Directory.GetParent(_dirActual);
            _dirActual = (padre == null) ? _lugarActivo.Raiz : padre.FullName;
        }
        catch { _dirActual = _lugarActivo.Raiz; }
        RenderArchivos();
    }

    /// <summary>Lugar + ruta relativa adentro del lugar ("KINGSTON › fw › v2").</summary>
    private string RutaVisible()
    {
        if (_lugarActivo == null) return "—";
        string rel = "";
        try
        {
            rel = Path.GetRelativePath(_lugarActivo.Raiz, _dirActual);
            if (rel == ".") rel = "";
        }
        catch { }
        string cabeza = _lugarActivo.Nombre;
        return rel.Length == 0
            ? cabeza
            : cabeza + " › " + rel.Replace(Path.DirectorySeparatorChar.ToString(), " › ");
    }

    // ---- filas -------------------------------------------------------------

    private Button FilaBase()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*,118,92") };

        var icono = new TextBlock
        {
            Text = "", Foreground = _dim, FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icono, 0);

        var nombre = new TextBlock
        {
            Text = "", Foreground = _texto, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(nombre, 1);

        var fecha = new TextBlock
        {
            Text = "", Foreground = _dim, FontSize = 12, FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(fecha, 2);

        var tam = new TextBlock
        {
            Text = "", Foreground = _dim, FontSize = 12, FontFamily = Mono,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(tam, 3);

        g.Children.Add(icono);
        g.Children.Add(nombre);
        g.Children.Add(fecha);
        g.Children.Add(tam);

        return new Button
        {
            Content = g,
            Background = _superficie,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            MinHeight = 48,
            Padding = new Thickness(14, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private Button Fila(string glifo, string nombre, string fecha, string tam, bool negrita)
    {
        var b = FilaBase();
        var g = (Grid)b.Content!;
        ((TextBlock)g.Children[0]).Text = glifo;
        var n = (TextBlock)g.Children[1];
        n.Text = nombre;
        if (negrita) n.FontWeight = FontWeight.Medium;
        ((TextBlock)g.Children[2]).Text = fecha;
        ((TextBlock)g.Children[3]).Text = tam;
        return b;
    }

    private Control Separador() => new Border { Height = 1, Background = _borde };

    private Control CajaAviso(string titulo, string detalle)
    {
        var pila = new StackPanel { Spacing = 6, Margin = new Thickness(16, 28, 16, 28) };
        pila.Children.Add(new TextBlock
        {
            Text = titulo, Foreground = _texto, FontSize = 14,
            FontWeight = FontWeight.Medium,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        pila.Children.Add(new TextBlock
        {
            Text = detalle, Foreground = _dim, FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        return pila;
    }

    // =========================================================================
    //  guardar
    // =========================================================================

    private void PedirGuardar()
    {
        if (_lugarActivo == null) return;
        string nombre = LimpiarNombre(_nombreBox.Text ?? "");
        if (nombre.Length == 0) return;

        string ruta;
        try { ruta = Path.Combine(_dirActual, nombre + _extGuardar); }
        catch { return; }

        bool existe;
        try { existe = File.Exists(ruta); } catch { existe = false; }
        if (existe)
        {
            _rutaPendiente = ruta;
            _confirmTexto.Text = string.Format(CultureInfo.InvariantCulture,
                T("Ya existe \"{0}\" acá. ¿Reemplazarlo?"), nombre + _extGuardar);
            _confirmBox.IsVisible = true;
            return;
        }
        Resolver(new[] { ruta });
    }

    private void OcultarConfirmacion()
    {
        _rutaPendiente = null;
        _confirmBox.IsVisible = false;
    }

    private void ActualizarBotonGuardar()
        => _btnGuardar.IsEnabled = LimpiarNombre(_nombreBox.Text ?? "").Length > 0;

    /// <summary>Saca caracteres inválidos para nombre de archivo y espacios de
    /// punta. No valida "bonito": valida que Windows lo acepte.</summary>
    private static string LimpiarNombre(string nombre)
    {
        var sb = new StringBuilder(nombre.Length);
        foreach (char c in nombre)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0) sb.Append(c);
        return sb.ToString().Trim().TrimEnd('.');
    }

    /// <summary>Si el nombre sugerido (o tipeado tocando un archivo) ya trae la
    /// extensión del modo guardar, se la saca — la extensión es fija y visible
    /// al lado del campo, duplicarla daría "log.log".</summary>
    private string QuitarExtSugerida(string nombre)
    {
        nombre ??= "";
        if (_extGuardar.Length > 0 &&
            nombre.EndsWith(_extGuardar, StringComparison.OrdinalIgnoreCase))
            return nombre.Substring(0, nombre.Length - _extGuardar.Length);
        return nombre;
    }

    // =========================================================================
    //  idioma
    // =========================================================================

    private void OnIdiomaCambio() => Dispatcher.UIThread.Post(() =>
    {
        RepintarTextosFijos();
        RenderLugares();
        RenderArchivos();
    });

    private void EngancharIdioma()
    {
        if (_idiomaEnganchado) return;
        // Se engancha al ABRIR y se suelta al cerrar (patrón de los paneles de
        // gráfico): IdiomaCambio es estático y una lambda eterna en el ctor
        // dejaría al panel repintando cerrado, para siempre.
        PilotX.Cockpit.Bars.Traductor.IdiomaCambio += OnIdiomaCambio;
        _idiomaEnganchado = true;
    }

    private void SoltarIdioma()
    {
        if (!_idiomaEnganchado) return;
        PilotX.Cockpit.Bars.Traductor.IdiomaCambio -= OnIdiomaCambio;
        _idiomaEnganchado = false;
    }

    private void RepintarTextosFijos()
    {
        _titulo.Text = _tituloPedido;
        _subtitulo.Text = (_modoGuardar
                ? T("Elegí dónde guardar")
                : (_multiple ? T("Tocá los archivos que necesitás") : T("Tocá el archivo que necesitás")))
            + " · " + string.Join(" ", _extensiones);
        _lblLugares.Text = T("LUGARES");
        _btnGuardar.Content = T("Guardar");
        _btnReemplazar.Content = T("Reemplazar");
        _btnConfirmCancelar.Content = T("Cancelar");
        _nombreBox.Watermark = T("Nombre del archivo");
        ActualizarBotonSeleccion();
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    private bool ExtensionPedida(string nombre)
    {
        if (_extensiones.Length == 0) return true;
        string ext = "";
        try { ext = Path.GetExtension(nombre) ?? ""; } catch { }
        foreach (var e in _extensiones)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string[] NormalizarExts(string[]? exts)
    {
        var salida = new List<string>();
        if (exts != null)
            foreach (var e in exts)
            {
                string n = NormalizarExt(e);
                if (n.Length > 1) salida.Add(n);
            }
        return salida.ToArray();
    }

    /// <summary>Acepta "bin", ".bin" o "*.bin" y devuelve ".bin".</summary>
    private static string NormalizarExt(string ext)
    {
        string e = (ext ?? "").Trim().TrimStart('*');
        if (!e.StartsWith(".", StringComparison.Ordinal)) e = "." + e;
        return e.ToLowerInvariant();
    }

    private static bool RutasIguales(string a, string b)
        => string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string FmtFecha(DateTime t)
        => t.ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Tamaño para HUMANOS, con la coma del idioma de la cabina
    /// ("14,2 GB" en castellano). Distinto a propósito del FmtBytes invariante
    /// de FirmwaresPanel, que refleja datos de wire.</summary>
    internal static string FmtTamano(long n)
    {
        var cu = CultureInfo.CurrentCulture;
        if (n < 1024) return n.ToString(cu) + " B";
        if (n < 1024L * 1024) return (n / 1024.0).ToString("0.0", cu) + " KB";
        if (n < 1024L * 1024 * 1024) return (n / 1048576.0).ToString("0.0", cu) + " MB";
        return (n / 1073741824.0).ToString("0.0", cu) + " GB";
    }

    /// <summary>Resuelve la paleta desde los tokens del theme la primera vez
    /// que la card se abre (en el ctor el control no está en el árbol y el
    /// lookup caería siempre en los fallbacks).</summary>
    private void ResolverPaleta()
    {
        if (_paletaResuelta) return;
        _paletaResuelta = true;
        _card        = Rec("PilotXPanelCard",        _card);
        _superficie  = Rec("PilotXPanelSurface",     _superficie);
        _superficie2 = Rec("PilotXPanelSurface2",    _superficie2);
        _borde       = Rec("PilotXPanelBorder",      _borde);
        _bordeAlto   = Rec("PilotXPanelBorderHigh",  _bordeAlto);
        _texto       = Rec("PilotXPanelText",        _texto);
        _dim         = Rec("PilotXPanelTextDim",     _dim);
        _acento      = Rec("PilotXPanelAccent",      _acento);
        _okTexto     = Rec("PilotXPanelOk",          _okTexto);
        _okSuave     = Rec("PilotXPanelOkSoft",      _okSuave);
        _err         = Rec("PilotXPanelErr",         _err);
        _warnSuave   = Rec("PilotXPanelWarnSoft",    _warnSuave);
    }

    private IBrush Rec(string clave, IBrush fallback)
    {
        try
        {
            if (this.TryFindResource(clave, out var v) && v is IBrush b) return b;
        }
        catch { }
        return fallback;
    }

    // =========================================================================
    //  teclado nativo (POST /api/teclado/abrir|cerrar — patrón BanderasClient)
    // =========================================================================

    private async Task TecladoAsync(bool abrir, string titulo = "")
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string cuerpo = abrir
                ? "{\"numerico\":false,\"titulo\":" + JsonSerializer.Serialize(titulo ?? "") + "}"
                : "{}";
            using var contenido = new StringContent(cuerpo, Encoding.UTF8, "application/json");
            string baseUrl = BaseUrl.EndsWith("/") ? BaseUrl : BaseUrl + "/";
            using var _ = await Http.PostAsync(baseUrl + "api/teclado/" + (abrir ? "abrir" : "cerrar"),
                                               contenido, cts.Token).ConfigureAwait(false);
        }
        catch { /* sin teclado en pantalla el campo sigue editable con uno físico */ }
    }
}
