// FirmwaresPanel.axaml.cs
//
// Reemplazo nativo de pages/firmwares.html + js/firmwares.js. Administra el
// cache LOCAL de firmwares: lo que el Hub le sirve a los nodos por LAN cuando
// no hay internet (el técnico llega con el .bin en un pendrive).
//
// Esta pantalla toca hardware real: el .bin que queda acá es el que después se
// flashea. Por eso el port es literal, sin "mejoras":
//   · MISMOS endpoints y verbos (GET /api/firmwares, POST /api/firmwares/upload
//     con bytes crudos + headers X-AP-*, DELETE /api/firmwares/{prod}/{ver}).
//   · MISMOS guards: cap de 8 MB, mínimo de 1 KB, regex de producto y de
//     versión, y confirmación obligatoria antes de borrar.
//   · MISMOS textos, mismos estados (cargando / vacío / error de lectura /
//     error de red) y el mismo orden de validaciones del upload.
//   · MISMO auto-completado de producto+versión a partir del nombre del
//     archivo, con el mismo regex.
//
// Qué NO hace esta pantalla (ni la hacía el HTML): NO dispara OTA a los nodos,
// no compara versiones ni decide si hay update. Eso vive en el detalle del nodo
// (pages/nodo-detalle.html) y en FirmwareOtaClient — el SHA-256 y el guard
// anti-downgrade se verifican allá y en el firmware. Acá el SHA-256 se calcula
// en el server al recibir el .bin (FirmwaresController) y se MUESTRA por
// versión; el panel no lo recalcula ni lo edita.
//
// API: Attach(FirmwaresClient) carga el catálogo; Detach() corta las llamadas
// en curso y cierra diálogos. Sin polling — el original tampoco lo tiene: se
// refresca a mano, después de subir y después de borrar.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class FirmwaresPanel : UserControl, IPanelEmbebible
{
    // ---- wire / estado -----------------------------------------------------
    private FirmwaresClient? _client;
    private CancellationTokenSource? _cts;
    private bool _subiendo;
    private bool _unaColumna;          // layout actual (@media max-width:1100px)
    private double _ultimaEscala = -1; // ancho pintado de la barra de progreso

    // ---- flasheo por USB (Task 11) ------------------------------------------
    // Cliente propio: mismo baseUrl que FirmwaresClient (mismo Hub, otro
    // controller — UsbFlashController). El catálogo NO se vuelve a pedir: se
    // reusa el mismo que ya bajó RefrescarAsync (_catalogo), filtrado a las
    // versiones que están LOCAL (con .bin en disco — lo único flasheable).
    private UsbFlashClient? _usbClient;
    private FirmwareCatalogoWire? _catalogo;
    private string _usbProducto = "";
    private string _usbVersion = "";
    private bool _usbVersionHasFactory;
    private IReadOnlyList<UsbPuertoDto> _usbPuertos = Array.Empty<UsbPuertoDto>();
    private string _usbPuerto = "";
    private bool _usbModoCompleto = true;   // true=factory.bin@0x0, false=firmware.bin@0x10000
    private bool _usbFlasheando;
    private DispatcherTimer? _usbPollTimer;
    private double _usbUltimaEscala = -1;
    // "producto" | "version" | "puerto" — qué lista está mostrando UsbPickerOverlay.
    private string _usbPickerModo = "";

    /// <summary>Mensajes amigables AGP-USB-001..007, DUPLICADOS a propósito de
    /// AgpErrorMapper.FriendlyForCode (PilotX.UI es portable, sin referencia a
    /// AgroParallel.Services — ver cabecera de UsbFlashClient.cs). Solo entra
    /// acá cuando el polling de EstadoAsync trae un `codigo` PELADO sin
    /// `mensaje` (el POST /api/usb/flash SÍ trae el mensaje armado por el
    /// server). AGP-USB-001 trae el placeholder "{port}" — se interpola en
    /// MostrarErrorUsb, nunca se muestra literal.</summary>
    private static readonly Dictionary<string, string> UsbFriendly = new()
    {
        ["AGP-USB-001"] = "El puerto {port} está en uso o no se puede abrir. ¿Otra app lo tiene abierto?",
        ["AGP-USB-002"] = "El módulo no respondió. Mantené BOOT apretado y reintentá, o revisá el cable.",
        ["AGP-USB-003"] = "Falló la escritura del firmware. Reintentá; si sigue, cambiá el cable/puerto.",
        ["AGP-USB-004"] = "No encontré el firmware a flashear en el cache.",
        ["AGP-USB-005"] = "Falta esptool en la instalación (build incompleto).",
        ["AGP-USB-006"] = "No se pudo instalar el driver USB (¿se rechazó el permiso de administrador?).",
        ["AGP-USB-007"] = "Ya hay un flasheo en curso. Esperá a que termine.",
    };

    // Archivo elegido. -1 en el tamaño = no se pudo leer la metadata (el
    // browser siempre la tiene; acá el tamaño real se mide al leer los bytes).
    // Dos formas excluyentes: _archivoRuta cuando vino del explorador PROPIO
    // de PilotX (ruta local — el tamaño sale de FileInfo.Length, que es sobre
    // lo que validan los guards de 8 MB/1 KB), _archivo cuando vino del
    // StorageProvider del sistema (fallback no-Windows) o del drag & drop.
    private IStorageFile? _archivo;
    private string? _archivoRuta;
    private long _archivoTamano = -1;

    private string _producto = "";     // value del <select> (vacío = sin elegir)

    // ---- guards (idénticos a firmwares.js y a FirmwaresController) ---------
    // La validación local repite la del backend a propósito: adelanta el
    // feedback al operario antes de mandar el .bin entero. El guión es
    // necesario para "corex-ecu" (módulo de pilotaje CoreX-ECU).
    private static readonly Regex RxProd = new Regex("^[a-zA-Z][a-zA-Z0-9-]{1,31}$");
    private static readonly Regex RxVer  = new Regex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,31}$");
    // "<producto>[-_ ]?v?<version>.<ext>" donde producto puede tener guiones
    // internos (corex-ecu) y ext ∈ {bin, hex, zip}.
    private static readonly Regex RxNombre = new Regex(
        @"^([a-zA-Z][a-zA-Z0-9-]*?)[-_ ]?[vV]?([0-9][0-9A-Za-z.\-]*)\.(bin|hex|zip)$",
        RegexOptions.IgnoreCase);

    /// <summary>Cap del .bin: 8 MB. Sobra para ESP32 (partición OTA típica
    /// 1,3-1,9 MB) y evita que un archivo equivocado llene el disco.</summary>
    private const long MaxBinBytes = 8L * 1024 * 1024;
    /// <summary>Piso: menos de 1 KB no es un firmware.</summary>
    private const long MinBinBytes = 1024;

    // ---- catálogo de productos (mismo orden y mismos grupos del <select>) --
    private sealed class OpcionProducto
    {
        public string Valor = "";
        public string Etiqueta = "";
        public bool EsGrupo;
    }

    private static readonly OpcionProducto[] Opciones =
    {
        new OpcionProducto { Valor = "",          Etiqueta = "— Elegir producto —" },
        new OpcionProducto { EsGrupo = true,      Etiqueta = "Nodos (.bin)" },
        new OpcionProducto { Valor = "quantix",   Etiqueta = "QuantiX" },
        new OpcionProducto { Valor = "vistax",    Etiqueta = "VistaX" },
        new OpcionProducto { Valor = "sectionx",  Etiqueta = "SectionX" },
        new OpcionProducto { Valor = "flowx",     Etiqueta = "FlowX" },
        new OpcionProducto { Valor = "stormx",    Etiqueta = "StormX" },
        new OpcionProducto { Valor = "soilx",     Etiqueta = "SoilX" },
        new OpcionProducto { Valor = "signalx",   Etiqueta = "SignalX" },
        new OpcionProducto { Valor = "cowx",      Etiqueta = "CowX" },
        new OpcionProducto { Valor = "linex",     Etiqueta = "LineX" },
        new OpcionProducto { EsGrupo = true,      Etiqueta = "Pilotaje (.hex)" },
        new OpcionProducto { Valor = "corex-ecu", Etiqueta = "CoreX-ECU" },
    };

    // ---- paleta clara (tokens PilotXPanel* del theme) ----------------------
    private static readonly IBrush Superficie  = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Superficie2 = new SolidColorBrush(Color.Parse("#EDF1EC"));
    private static readonly IBrush Fondo       = new SolidColorBrush(Color.Parse("#F5F7F4"));
    private static readonly IBrush Borde       = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush BordeAlto   = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto       = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoTenue  = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde       = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush VerdeTexto  = new SolidColorBrush(Color.Parse("#2F7A26"));
    private static readonly IBrush VerdeSuave  = new SolidColorBrush(Color.Parse("#E8F4E5"));
    private static readonly IBrush Rojo        = new SolidColorBrush(Color.Parse("#C0261F"));
    private static readonly IBrush RojoSuave   = new SolidColorBrush(Color.Parse("#FBE6E6"));

    private static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    // ---- callbacks al host -------------------------------------------------

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Aviso corto para el operario (el host lo muestra como toast).</summary>
    public event Action<string>? Aviso;

    // ---- diálogo interno ---------------------------------------------------
    // _modalOnOk    → confirm(): corre SOLO si el operario toca el botón OK.
    // _alertaCierre → alert(): corre con CUALQUIER cierre (OK o tocar afuera),
    //                 igual que la promesa de AgpModal.alert, que resuelve en
    //                 los dos casos. Sin esto, cerrar el aviso de "no se pudo
    //                 borrar" tocando afuera dejaba la lista sin refrescar.
    private Action? _modalOnOk;
    private Action? _alertaCierre;

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    public FirmwaresPanel()
    {
        InitializeComponent();

        ArmarPicker();
        AplicarArchivo(null, 0);
        MostrarCargando();

        // El teclado nativo NO es automático por foco: cada campo lo pide con la
        // misma señal HTTP que manda keyboard.js. "Versión" abre el teclado
        // numérico (el input del original va con inputmode="decimal");
        // "Changelog" abre el qwerty (es un <textarea>).
        var ver = this.FindControl<TextBox>("TxtVersion");
        if (ver != null)
        {
            ver.GotFocus  += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, T("Versión"), true); };
            ver.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
        }
        var chg = this.FindControl<TextBox>("TxtChangelog");
        if (chg != null)
        {
            chg.GotFocus  += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, T("Changelog"), false); };
            chg.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
        }

        // Drag & drop sobre la dropzone (el original lo soporta con
        // dataTransfer). En cabina se usa el toque, pero con mouse/pendrive en
        // el escritorio arrastrar el .bin funciona igual que en el Hub.
        var drop = this.FindControl<Panel>("DropZone");
        if (drop != null)
        {
            DragDrop.SetAllowDrop(drop, true);
            drop.AddHandler(DragDrop.DragOverEvent, OnDropZoneDragOver);
            drop.AddHandler(DragDrop.DropEvent, OnDropZoneDrop);
        }

        // Corte a UNA columna por debajo de 1100 px de ancho útil: el mismo
        // breakpoint del @media de la página.
        var grid = this.FindControl<Grid>("CuerpoGrid");
        if (grid != null) grid.SizeChanged += (_, e) => AplicarAncho(e.NewSize.Width);

        // El diccionario se aplica UNA SOLA VEZ, al construir: Aplicar guarda el
        // primer texto de cada control y se lo reescribe encima en cada pasada —
        // llamarlo en los render congelaría la pill de LAN y el resultado del
        // upload en su primer valor (lección de NodosPanel). Todo lo que escribe
        // el código pasa por T().
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    /// <summary>La pill de LAN y el botón "Refrescar" van a la barra de
    /// contexto del shell; la fila de cabecera vieja queda oculta.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<StackPanel>("HeaderPills"));

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public void Attach(FirmwaresClient client)
    {
        _client = client;
        _usbClient = new UsbFlashClient(client.BaseUrl);
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = RefrescarAsync(_cts.Token);
        _ = RefrescarPuertosUsbAsync();
        _ = ReconciliarUsbFlasheoAsync();
    }

    /// <summary>El panel se cachea y reutiliza (ConfigPanel._modPaneles): si el
    /// operario cierra este panel con un flasheo en curso, Detach() para el
    /// poller pero antes NO tocaba <see cref="_usbFlasheando"/> ni
    /// rehabilitaba BtnUsbFlashear — al reabrir, OnUsbFlashearClick cortaba
    /// por _usbFlasheando==true y el botón quedaba muerto para siempre (había
    /// que reiniciar PilotX). Acá se reconcilia contra el estado REAL del
    /// Hub, una vez, al re-mostrar el panel: si el flasheo sigue en curso se
    /// retoma el poller (no se pierde el progreso); si ya terminó (o el Hub
    /// no tiene registro), se limpia el latch y se rehabilita el botón. En el
    /// caso normal (sin flasheo pendiente) no pega ni un request de más.</summary>
    private async Task ReconciliarUsbFlasheoAsync()
    {
        if (!_usbFlasheando || _usbClient == null) return;

        var ct = _cts?.Token ?? CancellationToken.None;
        var estado = await _usbClient.EstadoAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        if (estado != null && estado.EnCurso)
        {
            // Sigue flasheando del otro lado: retomamos el poller donde
            // quedó, con el último progreso conocido.
            SetTexto("UsbFaseTexto", FaseTexto(estado.Fase));
            SetProgresoUsb(estado.Pct);
            MostrarProgresoUsb(true);
            IniciarPollUsb();
            return;
        }

        // Terminó mientras el panel estaba cerrado (o el Hub no tiene
        // registro del flasheo): nunca dejamos el botón deshabilitado.
        _usbFlasheando = false;
        SetEnabled("BtnUsbFlashear", true);
        MostrarProgresoUsb(false);

        if (estado != null && estado.Resultado == "ok")
        {
            SetTexto("UsbFaseTexto", T("Listo."));
            MostrarResultadoUsb("ok", T("Flasheo terminado."));
            await RefrescarPuertosUsbAsync().ConfigureAwait(true);
        }
        else if (estado != null && estado.Resultado != null)
        {
            MostrarErrorUsb(estado.Codigo, null, estado.Log);
        }
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        DetenerPollUsb();
        // Al cerrar el panel no se dispara el "refrescar después del aviso":
        // el token ya está cancelado y no hay a quién mostrarle el resultado.
        _alertaCierre = null;
        CerrarModal();
        CerrarPicker();
        CerrarUsbPicker();
        _ = _client?.TecladoAsync(false);
    }

    // =========================================================================
    //  catálogo (refresh() del JS)
    // =========================================================================

    private async void OnRefrescarClick(object? sender, RoutedEventArgs e)
    {
        if (_cts == null) _cts = new CancellationTokenSource();
        await RefrescarAsync(_cts.Token);
    }

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    private async Task RefrescarAsync(CancellationToken ct)
    {
        if (_client == null) return;
        MostrarCargando();
        var r = await _client.GetCatalogoAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Render(r));
    }

    private void MostrarCargando()
    {
        var lista = this.FindControl<StackPanel>("FwList");
        if (lista == null) return;
        lista.Children.Clear();
        lista.Children.Add(CajaVacia(T("Cargando catálogo…"),
                                     T("Leyendo el cache local de firmwares."), false));
    }

    private void Render(FirmwareCatalogoResultado r)
    {
        var lista = this.FindControl<StackPanel>("FwList");
        if (lista == null) return;
        lista.Children.Clear();

        // Excepción de red: el JS ni toca la pill ni el meta del cache.
        if (r.ErrorRed != null)
        {
            lista.Children.Add(CajaVacia(T("Error de red"), r.ErrorRed, true));
            return;
        }

        var d = r.Datos;
        if (d == null)
        {
            // ct cancelado (panel cerrándose): no hay nada que pintar.
            return;
        }

        if (!d.Ok)
        {
            lista.Children.Add(CajaVacia(T("No se pudo leer el cache"),
                                         string.IsNullOrEmpty(d.Error) ? T("desconocido") : d.Error!, true));
            return;
        }

        // Pill LAN + meta del cache.
        PintarPillLan(d.LanIp, d.HttpPort);
        SetTexto("MetaCache", string.IsNullOrEmpty(d.CacheDir)
            ? "—"
            : T("Carpeta") + " · " + d.CacheDir);

        // Catálogo para la sección USB (pickers de producto/versión) + revalida
        // la selección vigente por si el catálogo cambió (se borró la versión
        // elegida, etc.).
        _catalogo = d;
        RevalidarSeleccionUsb();

        var prods = d.Productos ?? new List<FirmwareProductoWire>();
        if (prods.Count == 0)
        {
            lista.Children.Add(CajaVacia(T("Cache vacío"),
                T("Sincronizá desde OrbitX o subí un firmware a la derecha."), false));
            return;
        }

        for (int i = 0; i < prods.Count; i++)
            lista.Children.Add(CajaProducto(prods[i], i == prods.Count - 1));
    }

    private void PintarPillLan(string? lanIp, int puerto)
    {
        var box = this.FindControl<Border>("LanPillBox");
        var txt = this.FindControl<TextBlock>("LanPill");
        if (box == null || txt == null) return;

        if (!string.IsNullOrEmpty(lanIp))
        {
            txt.Text = "LAN " + lanIp + ":" + puerto.ToString(CultureInfo.InvariantCulture);
            txt.Foreground = VerdeTexto;
            box.Background = VerdeSuave;
            box.BorderBrush = VerdeTexto;
        }
        else
        {
            txt.Text = T("LAN sin IP");
            txt.Foreground = Rojo;
            box.Background = RojoSuave;
            box.BorderBrush = Rojo;
        }
    }

    /// <summary>Un producto del cache con todas sus versiones (.fw-prod).</summary>
    private Control CajaProducto(FirmwareProductoWire p, bool ultimo)
    {
        var pila = new StackPanel { Spacing = 0 };

        int n = p.Versiones?.Count ?? 0;
        var cabecera = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Fondo
        };
        var nombre = new TextBlock
        {
            // text-transform: uppercase + letter-spacing del CSS.
            Text = (p.Producto ?? "").ToUpperInvariant(),
            Foreground = Texto,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            LetterSpacing = 1.0,
            Margin = new Thickness(12, 9, 6, 9),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nombre, 0);
        var cuenta = new TextBlock
        {
            Text = n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " " + T("versión") : " " + T("versiones")),
            Foreground = TextoTenue,
            FontSize = 11,
            Margin = new Thickness(6, 9, 12, 9),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(cuenta, 1);
        cabecera.Children.Add(nombre);
        cabecera.Children.Add(cuenta);

        var separador = new Border { Height = 1, Background = Borde };
        pila.Children.Add(cabecera);
        pila.Children.Add(separador);

        if (p.Versiones != null)
        {
            for (int i = 0; i < p.Versiones.Count; i++)
            {
                if (i > 0) pila.Children.Add(new Border { Height = 1, Background = Borde });
                pila.Children.Add(FilaVersion(p.Producto ?? "", p.Versiones[i]));
            }
        }

        return new Border
        {
            Background = Superficie2,
            BorderBrush = Borde,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, ultimo ? 0 : 14),
            Child = pila
        };
    }

    /// <summary>Una versión (.fw-ver): versión + badge, hash, tamaño, acciones,
    /// y —cuando existen— changelog y fecha ocupando toda la fila.</summary>
    private Control FilaVersion(string producto, FirmwareVersionWire v)
    {
        var pila = new StackPanel { Spacing = 0, Margin = new Thickness(12, 9, 12, 9) };

        var fila = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,92,*") };

        // --- versión + badge local/cloud ---
        var celdaV = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        celdaV.Children.Add(new TextBlock
        {
            Text = v.Version ?? "",
            Foreground = Texto,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center
        });
        celdaV.Children.Add(Badge(v.Local));
        Grid.SetColumn(celdaV, 0);

        // --- hash SHA-256 recortado (el completo, en el tooltip) ---
        string hashCompleto = v.HashSha256 ?? "";
        string hashCorto = hashCompleto.Length > 0
            ? hashCompleto.Substring(0, Math.Min(12, hashCompleto.Length)) + "…"
            : "—";
        var celdaH = new TextBlock
        {
            Text = hashCorto,
            Foreground = TextoTenue,
            FontSize = 11,
            FontFamily = Mono,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        ToolTip.SetTip(celdaH, hashCompleto);
        Grid.SetColumn(celdaH, 1);

        // --- tamaño ---
        var celdaT = new TextBlock
        {
            Text = FmtBytes(v.TamanoBytes),
            Foreground = TextoTenue,
            FontSize = 12,
            FontFamily = Mono,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(celdaT, 2);

        // --- acciones: Borrar SOLO si el .bin está en disco ---
        var celdaA = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        if (v.Local)
        {
            string ver = v.Version ?? "";
            var btn = new Button
            {
                Content = T("Borrar"),
                Background = Superficie,
                Foreground = Texto,
                BorderBrush = BordeAlto,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinHeight = 40,
                Padding = new Thickness(16, 0, 16, 0),
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            btn.Click += (_, __) => PedirBorrar(producto, ver);
            celdaA.Children.Add(btn);
        }
        else
        {
            celdaA.Children.Add(new TextBlock
            {
                Text = "—",
                Foreground = TextoTenue,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        Grid.SetColumn(celdaA, 3);

        fila.Children.Add(celdaV);
        fila.Children.Add(celdaH);
        fila.Children.Add(celdaT);
        fila.Children.Add(celdaA);
        pila.Children.Add(fila);

        if (!string.IsNullOrEmpty(v.Changelog))
        {
            pila.Children.Add(new TextBlock
            {
                Text = v.Changelog,
                Foreground = TextoTenue,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        if (v.Ts != 0)
        {
            pila.Children.Add(new TextBlock
            {
                Text = FmtTs(v.Ts),
                Foreground = TextoTenue,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        return pila;
    }

    /// <summary>Badge "local" (el .bin está en disco) / "cloud" (solo figura en
    /// el catálogo de OrbitX). El original los pinta verde y azul sobre fondo
    /// oscuro; en la paleta clara del panel el cloud va gris — el verde queda
    /// reservado al acento, como en el resto de las pantallas.</summary>
    private static Control Badge(bool local)
    {
        return new Border
        {
            Background = local ? VerdeSuave : Superficie2,
            BorderBrush = local ? VerdeTexto : BordeAlto,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 1, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = local ? T("LOCAL") : T("CLOUD"),
                Foreground = local ? VerdeTexto : TextoTenue,
                FontSize = 10,
                FontWeight = FontWeight.Medium,
                LetterSpacing = 0.8,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>Estado vacío / de error del catálogo (.fw-empty): caja punteada
    /// con título y detalle.</summary>
    private static Control CajaVacia(string titulo, string detalle, bool esError)
    {
        var pila = new StackPanel { Spacing = 6, Margin = new Thickness(16, 24, 16, 24) };
        pila.Children.Add(new TextBlock
        {
            Text = titulo,
            Foreground = esError ? Rojo : Texto,
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        pila.Children.Add(new TextBlock
        {
            Text = detalle,
            Foreground = TextoTenue,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var marco = new Rectangle
        {
            Fill = Fondo,
            Stroke = BordeAlto,
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            RadiusX = 6,
            RadiusY = 6
        };
        var caja = new Panel();
        caja.Children.Add(marco);
        caja.Children.Add(pila);
        return caja;
    }

    // =========================================================================
    //  borrar (onDelete del JS)
    // =========================================================================

    private void PedirBorrar(string producto, string version)
    {
        if (string.IsNullOrEmpty(producto) || string.IsNullOrEmpty(version)) return;
        AbrirModal(
            T("Borrar firmware"),
            string.Format(CultureInfo.InvariantCulture,
                T("¿Borrar {0} {1} del cache local?"), producto, version) + "\n\n" +
            T("El .bin se elimina del disco. Los nodos ya actualizados no se ven afectados."),
            T("Aceptar"), true,
            () => _ = BorrarAsync(producto, version));
    }

    private async Task BorrarAsync(string producto, string version)
    {
        if (_client == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        var r = await _client.BorrarAsync(producto, version, ct).ConfigureAwait(true);
        if (!r.Ok)
        {
            string msg = r.ErrorRed != null
                ? T("Error") + ": " + r.ErrorRed
                : T("No se pudo borrar") + ": " + (string.IsNullOrEmpty(r.Error) ? T("desconocido") : r.Error!);
            // alert() del original: un solo botón, y recién después se refresca.
            AbrirAlerta(T("Borrar firmware"), msg, () => _ = RefrescarAsync(ct));
            return;
        }
        await RefrescarAsync(ct);
    }

    // =========================================================================
    //  archivo (applyFile / dropzone del JS)
    // =========================================================================

    private async void OnDropZonePressed(object? sender, PointerPressedEventArgs e)
    {
        // Explorador PROPIO de PilotX (card nativa táctil, con los USB
        // arriba de todo). Solo si no está (Linux/Android) se cae al
        // StorageProvider del sistema, que era el comportamiento anterior.
        if (ExploradorArchivos.CardDisponible)
        {
            var rutas = await ExploradorArchivos.ElegirAsync(
                T("Elegí un archivo de firmware"),
                new[] { ".bin", ".hex", ".zip" });
            if (rutas == null || rutas.Length == 0) return;
            TomarRuta(rutas[0]);
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        IReadOnlyList<IStorageFile> elegidos;
        try
        {
            elegidos = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Elegí un archivo de firmware"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    // accept=".bin,.hex,.zip,application/octet-stream"
                    new FilePickerFileType(T("Firmware")) { Patterns = new[] { "*.bin", "*.hex", "*.zip" } }
                }
            });
        }
        catch { return; }
        if (elegidos == null || elegidos.Count == 0) return;

        await TomarArchivoAsync(elegidos[0]);
    }

    private void OnDropZoneDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDropZoneDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        IStorageFile? f = null;
        try
        {
            var items = e.Data.GetFiles();
            if (items != null)
                foreach (var it in items) { f = it as IStorageFile; if (f != null) break; }
        }
        catch { }
        if (f == null) return;
        await TomarArchivoAsync(f);
    }

    /// <summary>Guarda el archivo elegido y refleja nombre+tamaño en la
    /// dropzone. Igual que applyFile(): auto-completa producto y versión cuando
    /// el nombre del archivo tiene el patrón previsible.</summary>
    private async Task TomarArchivoAsync(IStorageFile f)
    {
        long tam = -1;
        try
        {
            var props = await f.GetBasicPropertiesAsync().ConfigureAwait(true);
            if (props?.Size != null) tam = (long)props.Size.Value;
        }
        catch { }

        _archivo = f;
        _archivoRuta = null;
        AplicarArchivo(f.Name, tam);
        AutoCompletar(f.Name);
    }

    /// <summary>Variante para el explorador propio: entra una RUTA local. El
    /// tamaño sale de FileInfo.Length — la metadata acá siempre está, así que
    /// los guards de 8 MB/1 KB cortan ANTES de leer los bytes.</summary>
    private void TomarRuta(string ruta)
    {
        long tam = -1;
        string nombre = ruta;
        try
        {
            var fi = new FileInfo(ruta);
            nombre = fi.Name;
            tam = fi.Length;
        }
        catch { try { nombre = System.IO.Path.GetFileName(ruta); } catch { } }

        _archivo = null;
        _archivoRuta = ruta;
        AplicarArchivo(nombre, tam);
        AutoCompletar(nombre);
    }

    private void AplicarArchivo(string? nombre, long tamano)
    {
        var meta = this.FindControl<TextBlock>("FileMeta");
        var marco = this.FindControl<Rectangle>("DropMarco");
        var icono = this.FindControl<TextBlock>("DropIcono");
        var texto = this.FindControl<TextBlock>("DropTexto");

        if (string.IsNullOrEmpty(nombre))
        {
            _archivo = null;
            _archivoRuta = null;
            _archivoTamano = -1;
            if (meta != null) meta.Text = "";
            if (marco != null) { marco.Stroke = BordeAlto; marco.Fill = Superficie; }
            if (icono != null) icono.Foreground = TextoTenue;
            if (texto != null) { texto.Text = T("Tocá para elegir un archivo"); texto.Foreground = TextoTenue; }
            return;
        }

        _archivoTamano = tamano;
        if (meta != null) meta.Text = nombre + " · " + (tamano < 0 ? "—" : FmtBytes(tamano));
        if (marco != null) { marco.Stroke = Verde; marco.Fill = VerdeSuave; }
        if (icono != null) icono.Foreground = VerdeTexto;
        if (texto != null) { texto.Text = nombre!; texto.Foreground = Texto; }
    }

    private void AutoCompletar(string nombre)
    {
        var m = RxNombre.Match(nombre ?? "");
        if (!m.Success) return;

        string guess = m.Groups[1].Value.ToLowerInvariant();
        foreach (var op in Opciones)
        {
            if (!op.EsGrupo && op.Valor.Length > 0 && op.Valor == guess)
            {
                SeleccionarProducto(op.Valor, op.Etiqueta);
                break;
            }
        }

        // Igual que el JS: la versión SOLO se auto-completa si el campo está
        // vacío (no pisa lo que el operario ya escribió).
        var ver = this.FindControl<TextBox>("TxtVersion");
        if (ver != null && string.IsNullOrEmpty(ver.Text)) ver.Text = m.Groups[2].Value;
    }

    // =========================================================================
    //  selector de producto (el <select> con <optgroup>)
    // =========================================================================

    private void ArmarPicker()
    {
        var host = this.FindControl<StackPanel>("PickerLista");
        if (host == null) return;
        host.Children.Clear();

        foreach (var op in Opciones)
        {
            if (op.EsGrupo)
            {
                host.Children.Add(new TextBlock
                {
                    Text = T(op.Etiqueta),
                    Foreground = TextoTenue,
                    FontSize = 11,
                    FontWeight = FontWeight.Medium,
                    LetterSpacing = 1.0,
                    Margin = new Thickness(4, 10, 4, 2)
                });
                continue;
            }

            var op2 = op;
            var b = new Button
            {
                Content = op.Valor.Length == 0 ? T(op.Etiqueta) : op.Etiqueta,
                Background = Superficie,
                Foreground = op.Valor.Length == 0 ? TextoTenue : Texto,
                BorderBrush = BordeAlto,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinHeight = 48,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            b.Click += (_, __) =>
            {
                SeleccionarProducto(op2.Valor, op2.Etiqueta);
                CerrarPicker();
            };
            host.Children.Add(b);
        }
    }

    private void SeleccionarProducto(string valor, string etiqueta)
    {
        _producto = valor ?? "";
        var btn = this.FindControl<Button>("BtnProducto");
        if (btn == null) return;
        btn.Content = _producto.Length == 0 ? T("— Elegir producto —") : etiqueta;
        btn.Foreground = _producto.Length == 0 ? TextoTenue : Texto;
    }

    private void OnProductoClick(object? sender, RoutedEventArgs e)
    {
        var ov = this.FindControl<Border>("PickerOverlay");
        if (ov != null) ov.IsVisible = true;
    }

    private void OnPickerCancelarClick(object? sender, RoutedEventArgs e) => CerrarPicker();
    private void OnPickerBackdropPressed(object? sender, PointerPressedEventArgs e) => CerrarPicker();
    private void OnPickerCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void CerrarPicker()
    {
        var ov = this.FindControl<Border>("PickerOverlay");
        if (ov != null) ov.IsVisible = false;
    }

    // =========================================================================
    //  upload (onUpload del JS)
    // =========================================================================

    private async void OnSubirClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null || _subiendo) return;

        string prod = (_producto ?? "").Trim();
        string ver  = (this.FindControl<TextBox>("TxtVersion")?.Text ?? "").Trim();
        // El changelog NO se trimea (igual que el original: `$('fwChangelog').value`).
        string chg  = this.FindControl<TextBox>("TxtChangelog")?.Text ?? "";

        SetResultado(null, null);

        // Orden de validaciones EXACTO al del original — el operario ve el
        // primer problema, no una lista.
        if (_archivo == null && _archivoRuta == null)
        { SetResultado("err", T("Falta el archivo de firmware.")); return; }
        if (!RxProd.IsMatch(prod))
        { SetResultado("err", T("Producto inválido — elegí uno de la lista.")); return; }
        if (!RxVer.IsMatch(ver))
        { SetResultado("err", T("Versión inválida — usá formato semver (ej 1.5.0).")); return; }
        // Tamaño conocido: se corta ANTES de leer el archivo. Si la metadata no
        // vino (el browser siempre la tiene, StorageProvider no siempre), los
        // mismos dos guards se aplican sobre los bytes leídos, más abajo.
        if (_archivoTamano >= 0 && _archivoTamano < MinBinBytes)
        { SetResultado("err", T("El archivo es demasiado chico (< 1 KB).")); return; }
        if (_archivoTamano >= 0 && _archivoTamano > MaxBinBytes)
        { SetResultado("err", T("El archivo supera el límite de 8 MB.")); return; }

        _subiendo = true;
        SetEnabled("BtnSubir", false);
        MostrarProgreso(true);
        SetProgreso(0);

        try
        {
            // Se lee hasta el cap + 1 byte: alcanza para saber que se pasó sin
            // cargar en RAM un archivo equivocado de cientos de MB.
            var (datos, excedido) = _archivoRuta != null
                ? await LeerBytesRutaAsync(_archivoRuta, MaxBinBytes).ConfigureAwait(true)
                : await LeerBytesAsync(_archivo!, MaxBinBytes).ConfigureAwait(true);
            if (excedido)
            { MostrarProgreso(false); SetResultado("err", T("El archivo supera el límite de 8 MB.")); return; }
            if (datos == null)
            { MostrarProgreso(false); SetResultado("err", T("No se pudo leer el archivo.")); return; }
            // Los MISMOS dos guards, ahora sobre lo que realmente se va a mandar.
            if (datos.Length < MinBinBytes)
            { MostrarProgreso(false); SetResultado("err", T("El archivo es demasiado chico (< 1 KB).")); return; }
            if (datos.Length > MaxBinBytes)
            { MostrarProgreso(false); SetResultado("err", T("El archivo supera el límite de 8 MB.")); return; }

            var ct = _cts?.Token ?? CancellationToken.None;
            var r = await _client.SubirAsync(prod, ver, chg, datos,
                        p => Dispatcher.UIThread.Post(() => SetProgreso(p)), ct).ConfigureAwait(true);

            MostrarProgreso(false);

            if (r.ErrorRed != null)
            {
                // "Error de red" a secas MENTÍA: por esta misma rama caía
                // cualquier excepción del cliente (p. ej. un header inválido)
                // con la LAN perfecta, y el operario se iba a revisar el cable.
                // Ahora: código dictable + mensaje amigable arriba, y el motivo
                // real de la excepción plegado abajo para soporte.
                string cabeza = string.IsNullOrEmpty(r.ErrorCodigo)
                    ? T("Error de red — revisá la conexión local.")
                    : r.ErrorCodigo + " · " + T(r.ErrorAmigable ?? "");
                SetResultado("err", cabeza,
                    string.IsNullOrWhiteSpace(r.ErrorTecnico) ? r.ErrorRed : r.ErrorTecnico);
                return;
            }
            if (r.Status == 200 && r.Datos != null && r.Datos.Ok)
            {
                string ok = string.Format(CultureInfo.InvariantCulture,
                    T("Subido {0} v{1} · {2}"),
                    r.Datos.Producto, r.Datos.Version, FmtBytes(r.Datos.TamanoBytes));
                SetResultado("ok", ok);
                // Reset del form — se PRESERVA el producto elegido para subir
                // otra versión seguida del mismo nodo sin volver a tocarlo.
                AplicarArchivo(null, 0);
                var vbox = this.FindControl<TextBox>("TxtVersion");
                if (vbox != null) vbox.Text = "";
                var cbox = this.FindControl<TextBox>("TxtChangelog");
                if (cbox != null) cbox.Text = "";
                // Mismo texto que la caja de resultado: el host lo puede
                // mostrar como toast si el panel está embebido y el operario
                // ya scrolleó lejos del formulario.
                Aviso?.Invoke(ok);
                await RefrescarAsync(ct);
                return;
            }

            string msg = (r.Datos != null && !string.IsNullOrEmpty(r.Datos.Error))
                ? r.Datos.Error!
                : "HTTP " + r.Status.ToString(CultureInfo.InvariantCulture);
            SetResultado("err", T("Falló") + ": " + msg);
        }
        finally
        {
            _subiendo = false;
            SetEnabled("BtnSubir", true);
        }
    }

    /// <summary>Igual que LeerBytesAsync pero desde una ruta local (la que
    /// devuelve el explorador propio de PilotX).</summary>
    private static async Task<(byte[]? Datos, bool Excedido)> LeerBytesRutaAsync(string ruta, long tope)
    {
        try
        {
            using var origen = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ms = new MemoryStream();
            byte[] buf = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = await origen.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > tope) return (null, true);
                ms.Write(buf, 0, n);
            }
            return (ms.ToArray(), false);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>Lee el archivo entero en memoria, cortando en tope+1 bytes.
    /// Devuelve (null, true) si se pasó del cap.</summary>
    private static async Task<(byte[]? Datos, bool Excedido)> LeerBytesAsync(IStorageFile f, long tope)
    {
        try
        {
            using var origen = await f.OpenReadAsync().ConfigureAwait(false);
            using var ms = new MemoryStream();
            byte[] buf = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = await origen.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > tope) return (null, true);
                ms.Write(buf, 0, n);
            }
            return (ms.ToArray(), false);
        }
        catch
        {
            return (null, false);
        }
    }

    // =========================================================================
    //  progreso / resultado
    // =========================================================================

    private void MostrarProgreso(bool visible)
    {
        var riel = this.FindControl<Border>("ProgresoRiel");
        if (riel != null) riel.IsVisible = visible;
        if (!visible) _ultimaEscala = -1;
    }

    private void SetProgreso(double pct)
    {
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        var barra = this.FindControl<Border>("ProgresoBarra");
        var riel = barra?.Parent as Border;
        if (barra == null || riel == null) return;
        double escala = pct / 100.0;
        if (Math.Abs(escala - _ultimaEscala) < 0.005) return;   // no bombear layout
        _ultimaEscala = escala;
        double ancho = riel.Bounds.Width;
        if (ancho <= 0) ancho = 320;   // fallback antes del primer layout
        barra.Width = ancho * escala;
    }

    /// <summary>Pinta el resultado del upload. kind: "ok" | "err" | null (limpia).
    /// <paramref name="detalleTecnico"/> = tipo + mensaje reales de la excepción:
    /// va PLEGADO abajo (convención del repo) y solo cuando hay algo que contar.</summary>
    private void SetResultado(string? kind, string? msg, string? detalleTecnico = null)
    {
        var box = this.FindControl<Border>("ResultBox");
        var txt = this.FindControl<TextBlock>("ResultText");
        var det = this.FindControl<Expander>("ResultDetalle");
        var detTxt = this.FindControl<TextBlock>("ResultDetalleText");
        if (box == null || txt == null) return;

        if (det != null) { det.IsVisible = !string.IsNullOrWhiteSpace(detalleTecnico); det.IsExpanded = false; }
        if (detTxt != null) detTxt.Text = detalleTecnico ?? "";

        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(msg))
        {
            box.IsVisible = false;
            txt.Text = "";
            if (det != null) det.IsVisible = false;
            return;
        }
        txt.Text = msg;
        box.IsVisible = true;
        if (kind == "ok")
        {
            box.Background = VerdeSuave;
            box.BorderBrush = VerdeTexto;
            txt.Foreground = Texto;
        }
        else
        {
            box.Background = RojoSuave;
            box.BorderBrush = Rojo;
            txt.Foreground = Rojo;
        }
    }

    // =========================================================================
    //  diálogo (AgpModal.confirm / AgpModal.alert)
    // =========================================================================

    private void AbrirModal(string titulo, string mensaje, string textoOk, bool destructivo, Action onOk)
    {
        SetTexto("ModalTitulo", titulo);
        SetTexto("ModalMensaje", mensaje);
        var btnOk = this.FindControl<Button>("BtnModalConfirmar");
        if (btnOk != null)
        {
            btnOk.Content = textoOk;
            btnOk.Background = destructivo ? Rojo : Verde;
            btnOk.Foreground = destructivo ? Superficie : Texto;
        }
        var btnCancel = this.FindControl<Button>("BtnModalCancelar");
        if (btnCancel != null) btnCancel.IsVisible = true;
        _modalOnOk = onOk;
        _alertaCierre = null;
        MostrarModal(true);
    }

    /// <summary>alert(): un solo botón, sin "Cancelar" (igual que modal.js).</summary>
    private void AbrirAlerta(string titulo, string mensaje, Action? alCerrar = null)
    {
        SetTexto("ModalTitulo", titulo);
        SetTexto("ModalMensaje", mensaje);
        var btnOk = this.FindControl<Button>("BtnModalConfirmar");
        if (btnOk != null)
        {
            btnOk.Content = T("Aceptar");
            btnOk.Background = Verde;
            btnOk.Foreground = Texto;
        }
        var btnCancel = this.FindControl<Button>("BtnModalCancelar");
        if (btnCancel != null) btnCancel.IsVisible = false;
        _modalOnOk = null;
        _alertaCierre = alCerrar;
        MostrarModal(true);
    }

    private void MostrarModal(bool visible)
    {
        var ov = this.FindControl<Border>("ModalOverlay");
        if (ov != null) ov.IsVisible = visible;
    }

    private void CerrarModal()
    {
        var cierre = _alertaCierre;
        _modalOnOk = null;
        _alertaCierre = null;
        MostrarModal(false);
        var btnCancel = this.FindControl<Button>("BtnModalCancelar");
        if (btnCancel != null) btnCancel.IsVisible = true;
        cierre?.Invoke();
    }

    private void OnModalConfirmarClick(object? sender, RoutedEventArgs e)
    {
        var accion = _modalOnOk;
        CerrarModal();
        accion?.Invoke();
    }

    private void OnModalCancelarClick(object? sender, RoutedEventArgs e) => CerrarModal();

    private void OnModalBackdropPressed(object? sender, PointerPressedEventArgs e) => CerrarModal();

    // La card absorbe el toque para que no llegue al backdrop y cierre el diálogo.
    private void OnModalCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    // =========================================================================
    //  layout responsive + helpers
    // =========================================================================

    /// <summary>Mismo corte que el @media (max-width:1100px) de la página: por
    /// debajo de 1100 px las dos tarjetas se apilan.</summary>
    private void AplicarAncho(double ancho)
    {
        bool una = ancho > 0 && ancho < 1100;
        if (una == _unaColumna) return;
        _unaColumna = una;

        var grid = this.FindControl<Grid>("CuerpoGrid");
        var izq = this.FindControl<Border>("CardCatalogo");
        var der = this.FindControl<Border>("CardSubir");
        if (grid == null || izq == null || der == null) return;

        if (una)
        {
            grid.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(izq, 0); Grid.SetRow(izq, 0);
            Grid.SetColumn(der, 0); Grid.SetRow(der, 1);
            izq.Margin = new Thickness(0, 0, 0, 0);
            der.Margin = new Thickness(0, 20, 0, 0);
        }
        else
        {
            grid.ColumnDefinitions = new ColumnDefinitions("*,*");
            Grid.SetColumn(izq, 0); Grid.SetRow(izq, 0);
            Grid.SetColumn(der, 1); Grid.SetRow(der, 0);
            izq.Margin = new Thickness(0, 0, 10, 0);
            der.Margin = new Thickness(10, 0, 0, 0);
        }
    }

    private void SetTexto(string nombre, string texto)
    {
        var tb = this.FindControl<TextBlock>(nombre);
        if (tb != null) tb.Text = texto;
    }

    private void SetEnabled(string nombre, bool habilitado)
    {
        var b = this.FindControl<Button>(nombre);
        if (b != null) b.IsEnabled = habilitado;
    }

    /// <summary>fmtBytes() del original, con separador decimal invariante (el
    /// toFixed de JS siempre usa punto).</summary>
    private static string FmtBytes(long n)
    {
        if (n < 1024) return n.ToString(CultureInfo.InvariantCulture) + " B";
        if (n < 1024 * 1024) return (n / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        return (n / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>fmtTs(): unix ms → fecha/hora local (toLocaleString del JS).</summary>
    private static string FmtTs(long unixMs)
    {
        if (unixMs == 0) return "—";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime
                   .ToString("G", CultureInfo.CurrentCulture);
        }
        catch { return "—"; }
    }

    // =========================================================================
    //  Flashear por USB (Task 11) — esptool.exe vía UsbFlashController.
    // =========================================================================

    // ---- puertos -------------------------------------------------------

    private async void OnUsbRefrescarPuertosClick(object? sender, RoutedEventArgs e)
        => await RefrescarPuertosUsbAsync();

    private async Task RefrescarPuertosUsbAsync()
    {
        if (_usbClient == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        var puertos = await _usbClient.PuertosAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        _usbPuertos = puertos;

        // Si el puerto elegido dejó de existir (se desenchufó el cable), se
        // limpia — mostrar un puerto fantasma seleccionado sería peor que
        // pedirlo de nuevo.
        if (!string.IsNullOrEmpty(_usbPuerto) && !puertos.Any(p => p.Port == _usbPuerto))
        {
            _usbPuerto = "";
            SetTextoBoton("BtnUsbPuerto", T("— Elegir puerto —"), false);
        }

        var btnDriver = this.FindControl<Button>("BtnUsbInstalarDriver");
        if (btnDriver != null) btnDriver.IsVisible = puertos.Count == 0;
    }

    private async void OnUsbInstalarDriverClick(object? sender, RoutedEventArgs e)
    {
        if (_usbClient == null) return;
        var btn = this.FindControl<Button>("BtnUsbInstalarDriver");
        if (btn != null) { btn.IsEnabled = false; btn.Content = T("Instalando… puede pedir permiso de administrador"); }
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            bool ok = await _usbClient.InstalarDriverAsync("ambos", ct).ConfigureAwait(true);
            if (!ok)
            {
                MostrarErrorUsb(_usbClient.UltimoErrorCodigo, _usbClient.UltimoErrorMensaje, null);
            }
            await RefrescarPuertosUsbAsync();
        }
        finally
        {
            if (btn != null) { btn.IsEnabled = true; btn.Content = T("Instalar driver USB"); }
        }
    }

    // ---- picker genérico (producto / versión / puerto) -----------------

    private void OnUsbProductoClick(object? sender, RoutedEventArgs e)
    {
        var items = new List<(string Valor, string Etiqueta)>();
        if (_catalogo?.Productos != null)
        {
            foreach (var p in _catalogo.Productos)
            {
                if (p.Versiones != null && p.Versiones.Exists(v => v.Local))
                    items.Add((p.Producto ?? "", (p.Producto ?? "").ToUpperInvariant()));
            }
        }
        AbrirUsbPicker("producto", T("Producto"), items);
    }

    private void OnUsbVersionClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_usbProducto)) return;
        var items = new List<(string Valor, string Etiqueta)>();
        var prod = _catalogo?.Productos?.Find(p => p.Producto == _usbProducto);
        if (prod?.Versiones != null)
        {
            foreach (var v in prod.Versiones)
            {
                if (!v.Local) continue;
                string etq = v.HasFactory ? (v.Version ?? "") : (v.Version ?? "") + "  ·  " + T("sin factory");
                items.Add((v.Version ?? "", etq));
            }
        }
        AbrirUsbPicker("version", T("Versión"), items);
    }

    private void OnUsbPuertoClick(object? sender, RoutedEventArgs e)
    {
        var items = new List<(string Valor, string Etiqueta)>();
        foreach (var p in _usbPuertos)
        {
            string etq = string.IsNullOrEmpty(p.Descripcion) || p.Descripcion == p.Port
                ? (p.Port ?? "")
                : p.Port + " · " + p.Descripcion;
            items.Add((p.Port ?? "", etq));
        }
        AbrirUsbPicker("puerto", T("Puerto COM"), items);
    }

    private void AbrirUsbPicker(string modo, string titulo, List<(string Valor, string Etiqueta)> items)
    {
        _usbPickerModo = modo;
        SetTexto("UsbPickerTitulo", titulo);

        var host = this.FindControl<StackPanel>("UsbPickerLista");
        if (host == null) return;
        host.Children.Clear();

        if (items.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = T("No hay opciones disponibles."),
                Foreground = TextoTenue,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 10, 4, 10)
            });
        }

        foreach (var it in items)
        {
            string valor = it.Valor, etiqueta = it.Etiqueta;
            var b = new Button
            {
                Content = etiqueta,
                Background = Superficie,
                Foreground = Texto,
                BorderBrush = BordeAlto,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinHeight = 48,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            b.Click += (_, __) =>
            {
                SeleccionarUsbItem(modo, valor, etiqueta);
                CerrarUsbPicker();
            };
            host.Children.Add(b);
        }

        var ov = this.FindControl<Border>("UsbPickerOverlay");
        if (ov != null) ov.IsVisible = true;
    }

    private void OnUsbPickerCancelarClick(object? sender, RoutedEventArgs e) => CerrarUsbPicker();
    private void OnUsbPickerBackdropPressed(object? sender, PointerPressedEventArgs e) => CerrarUsbPicker();
    private void OnUsbPickerCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void CerrarUsbPicker()
    {
        var ov = this.FindControl<Border>("UsbPickerOverlay");
        if (ov != null) ov.IsVisible = false;
    }

    private void SeleccionarUsbItem(string modo, string valor, string etiqueta)
    {
        switch (modo)
        {
            case "producto":
                _usbProducto = valor;
                _usbVersion = "";
                _usbVersionHasFactory = false;
                SetTextoBoton("BtnUsbProducto",
                    string.IsNullOrEmpty(valor) ? T("— Elegir producto —") : etiqueta, valor.Length > 0);
                SetTextoBoton("BtnUsbVersion", T("— Elegir versión —"), false);
                SetEnabled("BtnUsbVersion", valor.Length > 0);
                AplicarModoDisponible();
                break;

            case "version":
                _usbVersion = valor;
                _usbVersionHasFactory = BuscarHasFactory(_usbProducto, valor);
                // La etiqueta puede traer "  ·  sin factory": el botón elegido
                // muestra solo el número de versión, no el sufijo informativo.
                SetTextoBoton("BtnUsbVersion",
                    string.IsNullOrEmpty(valor) ? T("— Elegir versión —") : valor, valor.Length > 0);
                AplicarModoDisponible();
                break;

            case "puerto":
                _usbPuerto = valor;
                SetTextoBoton("BtnUsbPuerto",
                    string.IsNullOrEmpty(valor) ? T("— Elegir puerto —") : etiqueta, valor.Length > 0);
                break;
        }
    }

    private bool BuscarHasFactory(string producto, string version)
    {
        var prod = _catalogo?.Productos?.Find(p => p.Producto == producto);
        var ver = prod?.Versiones?.Find(v => v.Version == version);
        return ver?.HasFactory ?? false;
    }

    /// <summary>Si el catálogo se refrescó (subida/borrado) y la selección
    /// vigente ya no es válida (se borró la versión, dejó de estar local…), se
    /// limpia en vez de dejar un puerto/versión fantasma cargado.</summary>
    private void RevalidarSeleccionUsb()
    {
        if (string.IsNullOrEmpty(_usbProducto)) return;

        var prod = _catalogo?.Productos?.Find(p => p.Producto == _usbProducto);
        bool prodSigueValido = prod?.Versiones != null && prod.Versiones.Exists(v => v.Local);
        if (!prodSigueValido)
        {
            _usbProducto = "";
            _usbVersion = "";
            _usbVersionHasFactory = false;
            SetTextoBoton("BtnUsbProducto", T("— Elegir producto —"), false);
            SetTextoBoton("BtnUsbVersion", T("— Elegir versión —"), false);
            SetEnabled("BtnUsbVersion", false);
            AplicarModoDisponible();
            return;
        }

        if (!string.IsNullOrEmpty(_usbVersion))
        {
            var ver = prod!.Versiones!.Find(v => v.Version == _usbVersion && v.Local);
            if (ver == null)
            {
                _usbVersion = "";
                _usbVersionHasFactory = false;
                SetTextoBoton("BtnUsbVersion", T("— Elegir versión —"), false);
            }
            else
            {
                _usbVersionHasFactory = ver.HasFactory;
            }
            AplicarModoDisponible();
        }
    }

    private void SetTextoBoton(string nombre, string texto, bool elegido)
    {
        var b = this.FindControl<Button>(nombre);
        if (b == null) return;
        b.Content = texto;
        b.Foreground = elegido ? Texto : TextoTenue;
    }

    // ---- modo (Completo / Solo app) -------------------------------------

    private void OnUsbModoCompletoClick(object? sender, RoutedEventArgs e)
    {
        if (!_usbVersionHasFactory) return; // botón debería estar IsEnabled=false; doble resguardo
        _usbModoCompleto = true;
        PintarModoUsb();
    }

    private void OnUsbModoAppClick(object? sender, RoutedEventArgs e)
    {
        _usbModoCompleto = false;
        PintarModoUsb();
    }

    /// <summary>Apaga "Completo" cuando la versión elegida no tiene
    /// factory.bin en el cache (has_factory del catálogo — ver sub-paso
    /// backend en FirmwaresController.List()); si estaba elegido, cae solo a
    /// "Solo app" para no dejar seleccionado un modo deshabilitado.</summary>
    private void AplicarModoDisponible()
    {
        var btnCompleto = this.FindControl<Button>("BtnUsbModoCompleto");
        if (btnCompleto == null) return;

        btnCompleto.IsEnabled = _usbVersionHasFactory;
        if (!_usbVersionHasFactory && _usbModoCompleto)
            _usbModoCompleto = false;

        PintarModoUsb();
    }

    private void PintarModoUsb()
    {
        var btnCompleto = this.FindControl<Button>("BtnUsbModoCompleto");
        var btnApp = this.FindControl<Button>("BtnUsbModoApp");

        if (btnCompleto != null)
        {
            bool en = btnCompleto.IsEnabled;
            bool sel = en && _usbModoCompleto;
            btnCompleto.Background = !en ? Superficie2 : (sel ? VerdeSuave : Superficie);
            btnCompleto.BorderBrush = !en ? Borde : (sel ? VerdeTexto : BordeAlto);
            btnCompleto.Foreground = !en ? TextoTenue : (sel ? VerdeTexto : Texto);
        }
        if (btnApp != null)
        {
            bool sel = !_usbModoCompleto;
            btnApp.Background = sel ? VerdeSuave : Superficie;
            btnApp.BorderBrush = sel ? VerdeTexto : BordeAlto;
            btnApp.Foreground = sel ? VerdeTexto : Texto;
        }
    }

    // ---- flashear + poller -----------------------------------------------

    private async void OnUsbFlashearClick(object? sender, RoutedEventArgs e)
    {
        if (_usbClient == null || _usbFlasheando) return;

        // Mismo criterio del resto del panel: el operario ve el primer
        // problema, no una lista.
        if (string.IsNullOrEmpty(_usbProducto))
        { MostrarErrorUsb(null, T("Elegí un producto."), null); return; }
        if (string.IsNullOrEmpty(_usbVersion))
        { MostrarErrorUsb(null, T("Elegí una versión."), null); return; }
        if (string.IsNullOrEmpty(_usbPuerto))
        { MostrarErrorUsb(null, T("Elegí el puerto COM del nodo."), null); return; }

        string modo = _usbModoCompleto ? "completo" : "app";
        bool borrarAntes = this.FindControl<CheckBox>("ChkUsbBorrarAntes")?.IsChecked == true;

        MostrarResultadoUsb(null, null);
        SetUsbLog(null);
        _usbFlasheando = true;
        SetEnabled("BtnUsbFlashear", false);
        MostrarProgresoUsb(true);
        SetProgresoUsb(0);
        SetTexto("UsbFaseTexto", T("Iniciando…"));

        var req = new UsbFlashRequest
        {
            Producto = _usbProducto,
            Version = _usbVersion,
            Puerto = _usbPuerto,
            Modo = modo,
            BorrarAntes = borrarAntes
        };

        var ct = _cts?.Token ?? CancellationToken.None;
        bool ok = await _usbClient.FlashAsync(req, ct).ConfigureAwait(true);
        if (!ok)
        {
            _usbFlasheando = false;
            SetEnabled("BtnUsbFlashear", true);
            MostrarProgresoUsb(false);
            MostrarErrorUsb(_usbClient.UltimoErrorCodigo, _usbClient.UltimoErrorMensaje, null);
            return;
        }

        IniciarPollUsb();
    }

    private void IniciarPollUsb()
    {
        DetenerPollUsb();
        _usbPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _usbPollTimer.Tick += async (_, __) => await TickPollUsbAsync().ConfigureAwait(true);
        _usbPollTimer.Start();
    }

    private void DetenerPollUsb()
    {
        if (_usbPollTimer == null) return;
        _usbPollTimer.Stop();
        _usbPollTimer = null;
    }

    private async Task TickPollUsbAsync()
    {
        if (_usbClient == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        var estado = await _usbClient.EstadoAsync(ct).ConfigureAwait(true);
        // null = sin dato todavía (timeout del Hub) — se reintenta en el
        // próximo tick, NUNCA se interpreta como flasheo terminado (misma
        // trampa documentada en UsbFlashClient.EstadoAsync).
        if (estado == null) return;

        SetProgresoUsb(estado.Pct);
        SetTexto("UsbFaseTexto", FaseTexto(estado.Fase));
        if (!string.IsNullOrEmpty(estado.Log)) SetUsbLog(estado.Log);

        if (estado.Resultado == null) return; // sigue en curso

        DetenerPollUsb();
        _usbFlasheando = false;
        SetEnabled("BtnUsbFlashear", true);
        MostrarProgresoUsb(false);

        if (estado.Resultado == "ok")
        {
            SetTexto("UsbFaseTexto", T("Listo."));
            MostrarResultadoUsb("ok", T("Flasheo terminado."));
            Aviso?.Invoke(T("Flasheo por USB terminado."));
            await RefrescarPuertosUsbAsync().ConfigureAwait(true);
        }
        else
        {
            MostrarErrorUsb(estado.Codigo, null, estado.Log);
        }
    }

    private static string FaseTexto(string? fase) => fase switch
    {
        "conectando" => T("Conectando…"),
        "borrando" => T("Borrando chip…"),
        "escribiendo" => T("Escribiendo firmware…"),
        "verificando" => T("Verificando…"),
        "reset" => T("Reiniciando el nodo…"),
        "listo" => T("Listo."),
        "error" => T("Error."),
        _ => T("Preparando…"),
    };

    // ---- progreso / resultado / log --------------------------------------

    private void MostrarProgresoUsb(bool visible)
    {
        var riel = this.FindControl<Border>("UsbProgresoRiel");
        if (riel != null) riel.IsVisible = visible;
        if (!visible) _usbUltimaEscala = -1;
    }

    private void SetProgresoUsb(double pct)
    {
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        var barra = this.FindControl<Border>("UsbProgresoBarra");
        var riel = barra?.Parent as Border;
        if (barra == null || riel == null) return;
        double escala = pct / 100.0;
        if (Math.Abs(escala - _usbUltimaEscala) < 0.005) return;
        _usbUltimaEscala = escala;
        double ancho = riel.Bounds.Width;
        if (ancho <= 0) ancho = 320;
        barra.Width = ancho * escala;
    }

    /// <summary>Pinta el resultado del flasheo (ok/err). El log crudo se
    /// maneja aparte con SetUsbLog — vive siempre en su propio Expander,
    /// independiente de si el resultado fue ok o error.</summary>
    private void MostrarResultadoUsb(string? kind, string? msg)
    {
        var box = this.FindControl<Border>("UsbResultBox");
        var txt = this.FindControl<TextBlock>("UsbResultText");
        if (box == null || txt == null) return;

        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(msg))
        {
            box.IsVisible = false;
            txt.Text = "";
            return;
        }
        txt.Text = msg;
        box.IsVisible = true;
        if (kind == "ok") { box.Background = VerdeSuave; box.BorderBrush = VerdeTexto; txt.Foreground = Texto; }
        else { box.Background = RojoSuave; box.BorderBrush = Rojo; txt.Foreground = Rojo; }
    }

    private void SetUsbLog(string? texto)
    {
        var det = this.FindControl<Expander>("UsbLogExpander");
        var txt = this.FindControl<TextBlock>("UsbLogText");
        if (txt != null) txt.Text = texto ?? "";
        if (det != null) det.IsVisible = !string.IsNullOrWhiteSpace(texto);
    }

    /// <summary>Arma el mensaje de error del flasheo: `mensajeServidor` (ya
    /// amigable, viene del POST /api/usb/flash o de InstalarDriverAsync) si
    /// hay; si no, se resuelve por código con UsbFriendly (caso del polling,
    /// que solo trae `codigo` pelado). El placeholder "{port}" se interpola
    /// SIEMPRE acá aunque el server ya lo haga — por las dudas, nunca se
    /// muestra el literal "{port}" al operario.</summary>
    private void MostrarErrorUsb(string? codigo, string? mensajeServidor, string? logCrudo)
    {
        string mensaje = mensajeServidor ?? "";
        if (string.IsNullOrEmpty(mensaje) && !string.IsNullOrEmpty(codigo) && UsbFriendly.TryGetValue(codigo, out var plantilla))
            mensaje = plantilla;
        if (string.IsNullOrEmpty(mensaje))
            mensaje = T("No se pudo completar el flasheo.");
        mensaje = mensaje.Replace("{port}", _usbPuerto);

        string cabeza = string.IsNullOrEmpty(codigo) ? T(mensaje) : codigo + " · " + T(mensaje);
        MostrarResultadoUsb("err", cabeza);
        if (!string.IsNullOrWhiteSpace(logCrudo)) SetUsbLog(logCrudo);
    }
}
