// DebugPanel.axaml.cs
//
// Reemplazo nativo de pages/debug.html + js/debug.js: el log unificado de todos
// los módulos del sistema (quantix, vistax, orbitx, sectionx, cámaras, sistema,
// host, ui…). Es la pantalla que se mira cuando algo falla en el lote, así que
// el port es literal, sin "mejoras":
//
//   · MISMOS endpoints y verbos (GET /api/debug/snapshot?max=500,
//     PUT /api/debug/config con { modules, min_level },
//     POST /api/debug/module?name=&on=, POST /api/debug/clear,
//     POST /api/debug/record?on=).
//   · MISMOS textos: "Debug", "Log unificado de todos los módulos del
//     sistema", "En vivo", "Desconectado", "Filtrar...", "Debug/Info/Warn/
//     Error", "Pausar"/"Reanudar", "Limpiar", "Grabar"/"Grabando…",
//     "Exportar", "Cargando…", "Sin eventos…", "No se pudo conectar al
//     servicio Debug.".
//   · MISMO buffer de 1500 renglones con descarte por la cabeza, MISMO
//     auto-scroll que se apaga si el operario scrollea para arriba (umbral de
//     32 px) y MISMO nombre de archivo del export
//     ("debug-<ISO con : y . cambiados por ->.log").
//
// QUÉ NO SE PORTA: el WebSocket /ws/debug. En su lugar se pollea el endpoint
// REST hermano /api/debug/entries?since= (ver DebugClient.cs): el seq del
// servidor es monotónico y /clear no lo resetea, así que no se saltea ni se
// duplica ninguna línea del buffer del servidor (que es un ring de 5000 y
// descarta por la cabeza: en una tormenta de log se ve la cola, no el todo).
// Consecuencias visibles, las dos anotadas a propósito:
//   · la pill pasa a "Desconectado" cuando una pasada falla y vuelve sola a
//     "En vivo" cuando el servicio contesta (el original hacía lo mismo con
//     ws.onclose + setTimeout(connectWs, 1200));
//   · las líneas aparecen de a tandas de 500 ms en vez de una por una.
//
// BUGS DEL ORIGINAL QUE SE REPLICAN TAL CUAL (NO se arreglan acá):
//   1. renderAll() NO filtra: re-pinta state.buffer entero. O sea que escribir
//      en el buscador, cambiar el nivel o apagar un módulo NO esconde lo que ya
//      está en pantalla — el filtro solo decide qué ENTRA de ahí en adelante.
//      (El CSS tiene una clase .dbg-line.hide que nadie usa: la idea quedó a
//      medio hacer.) Se replica: Render() vuelca el buffer sin filtrar.
//   2. loadSnapshot() pisa state.cfg.minLevel con el min_level del servidor
//      pero NO toca state.minLevel ni el botón marcado: el filtro del visor
//      arranca SIEMPRE en "info" aunque el servidor diga otra cosa. Se replica.
//   3. toggleRecord() no mira el `ok` de la respuesta: si el servidor no pudo
//      abrir el archivo (ok:false, file vacío) el botón igual queda en
//      "Grabando…". Solo revierte si la request falla entera. Se replica.
//   4. Con la pausa activa las líneas que llegan se DESCARTAN (no se encolan):
//      al reanudar no aparecen. Se replica — el polling avanza el `since`
//      igual, que es lo que hacía el WS al ignorar el mensaje.
//
// El buscador NO abre el teclado nativo: el <input> del original lleva
// data-no-keyboard, que es el opt-out explícito de keyboard.js. Se respeta.
//
// API: Attach(DebugClient) carga el snapshot y arranca el polling; Detach()
// corta todo.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

/// <summary>Un renglón ya formateado del visor. Se arma una vez por entrada y
/// no cambia — por eso no necesita notificar cambios.</summary>
public sealed class DebugLinea
{
    public string Hora { get; set; } = "";
    public string Modulo { get; set; } = "";
    public string Mensaje { get; set; } = "";
    public IBrush ModuloColor { get; set; } = Brushes.Black;
    public IBrush MensajeColor { get; set; } = Brushes.Black;
}

public partial class DebugPanel : UserControl, IPanelEmbebible
{
    // ---- wire --------------------------------------------------------------
    private DebugClient? _client;
    private CancellationTokenSource? _cts;

    /// <summary>Cadencia del polling. Doctrina de paneles: 2 Hz.</summary>
    private static readonly TimeSpan Cadencia = TimeSpan.FromMilliseconds(500);

    // ---- estado (el `state` del JS, campo por campo) -----------------------
    private static readonly string[] OrdenNiveles = { "debug", "info", "warn", "error" };

    private Dictionary<string, bool>? _cfgModulos;   // state.cfg.modules (null = sin cfg)
    private string _cfgMinLevel = "debug";           // state.cfg.minLevel (va al PUT)
    private long _seq;
    private bool _pausado;
    private string _minLevel = "info";               // state.minLevel (filtro del visor)
    private string _busqueda = "";
    private bool _grabando;
    private bool _idiomaEnganchado;                  // suscripción viva a Traductor.IdiomaCambio
    private bool _pillViva = true;                   // último estado de la pill (arranca "En vivo", como el XAML)
    private string? _vacioClave;                     // cartel del visor SIN traducir (para repintarlo)
    private string? _archivoGrabacion;
    private readonly List<DebugEntryWire> _buffer = new();
    private const int MaxRender = 1500;
    private bool _autoScroll = true;

    private readonly ObservableCollection<DebugLinea> _lineas = new();

    // ---- textos EXACTOS de la página (claves del diccionario) --------------
    private const string TxtEnVivo       = "En vivo";
    private const string TxtDesconectado = "Desconectado";
    private const string TxtCargando     = "Cargando…";
    private const string TxtSinEventos   = "Sin eventos…";
    private const string TxtSinServicio  = "No se pudo conectar al servicio Debug.";
    private const string TxtPausar       = "Pausar";
    private const string TxtReanudar     = "Reanudar";
    private const string TxtGrabar       = "Grabar";
    private const string TxtGrabando     = "Grabando…";

    // ---- paleta clara (tokens PilotXPanel* del theme) ----------------------
    private static readonly IBrush Superficie  = new SolidColorBrush(Color.Parse("#FFFFFF"));  // PilotXPanelSurface
    private static readonly IBrush Superficie2 = new SolidColorBrush(Color.Parse("#EDF1EC"));  // PilotXPanelSurface2
    private static readonly IBrush BordeAlto   = new SolidColorBrush(Color.Parse("#C5CFC5"));  // PilotXPanelBorderHigh
    private static readonly IBrush Texto       = new SolidColorBrush(Color.Parse("#101612"));  // PilotXPanelText
    private static readonly IBrush TextoTenue  = new SolidColorBrush(Color.Parse("#535E54"));  // PilotXPanelTextDim
    private static readonly IBrush Verde       = new SolidColorBrush(Color.Parse("#4ABA3E"));  // PilotXPanelAccent
    private static readonly IBrush VerdeTexto  = new SolidColorBrush(Color.Parse("#2F7A26"));  // PilotXPanelAccentText / Ok
    private static readonly IBrush VerdeSuave  = new SolidColorBrush(Color.Parse("#E8F4E5"));  // PilotXPanelOkSoft
    private static readonly IBrush Ambar       = new SolidColorBrush(Color.Parse("#8A6100"));  // PilotXPanelWarn
    private static readonly IBrush Rojo        = new SolidColorBrush(Color.Parse("#C0261F"));  // PilotXPanelErr
    private static readonly IBrush RojoSuave   = new SolidColorBrush(Color.Parse("#FBE6E6"));  // PilotXPanelErrSoft
    private static readonly IBrush Gris        = new SolidColorBrush(Color.Parse("#6E7A70"));  // PilotXPanelIdle

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Aviso corto para el operario (el host lo muestra como toast).</summary>
    public event Action<string>? Aviso;

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    public DebugPanel()
    {
        InitializeComponent();

        var lista = this.FindControl<ItemsControl>("BufferLista");
        if (lista != null) lista.ItemsSource = _lineas;

        PintarNiveles();
        PintarBotonGrabar();
        // La pill arranca en "En vivo" (lo que trae el XAML), igual que el
        // <span class="pill live"> del original antes de conectar.
        MostrarVacio(TxtCargando);

        // El diccionario se aplica UNA SOLA VEZ, al construir: Aplicar guarda el
        // primer texto de cada control y se lo reescribe encima en cada pasada —
        // llamarlo en los render congelaría la pill y el botón de grabar en su
        // primer valor (lección de NodosPanel/FirmwaresPanel). Todo lo que
        // escribe el código pasa por T().
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // =========================================================================
    //  idioma en caliente
    // =========================================================================

    /// <summary>Se engancha en Attach y se SUELTA en Detach: IdiomaCambio es un
    /// evento estático y una suscripción de por vida deja al panel repintando
    /// desde el fondo, cerrado, para siempre. Con el visor cerrado tampoco hace
    /// falta: al reabrirlo, Attach recarga el snapshot y repinta todo.</summary>
    private void EngancharIdioma()
    {
        if (_idiomaEnganchado) return;
        PilotX.Cockpit.Bars.Traductor.IdiomaCambio += OnIdiomaCambio;
        _idiomaEnganchado = true;
    }

    private void SoltarIdioma()
    {
        if (!_idiomaEnganchado) return;
        PilotX.Cockpit.Bars.Traductor.IdiomaCambio -= OnIdiomaCambio;
        _idiomaEnganchado = false;
    }

    /// <summary>Aplicar() le devuelve a cada control el texto que cacheó la
    /// PRIMERA vez, así que después de traducir hay que repintar todo lo que
    /// escribe el código: la pill, el botón de grabar, el de pausar y el cartel
    /// del visor vacío. El Post corre después del Aplicar del host. Los
    /// renglones del log y los chips de módulo NO se traducen (son datos).</summary>
    private void OnIdiomaCambio() => Dispatcher.UIThread.Post(() =>
    {
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
        PintarPill(_pillViva);
        PintarBotonGrabar();
        PintarBotonPausar();
        MostrarVacio(_vacioClave);
    });

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    /// <summary>La pill de estado va a la barra de contexto del shell; la fila
    /// de cabecera vieja queda oculta.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<StackPanel>("HeaderPills"));

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public void Attach(DebugClient client)
    {
        _client = client;
        EngancharIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();
        _ = ArrancarAsync(_cts.Token);
    }

    public void Detach()
    {
        SoltarIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        PintarPill(false);
    }

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    /// <summary>loadSnapshot().then(connectWs).catch(…) del original: si el
    /// snapshot falla NO se arranca el push (acá, el polling) — el visor queda
    /// con el cartel de error hasta que se reabra la pantalla.</summary>
    private async Task ArrancarAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var snap = await _client.GetSnapshotAsync(500, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        if (snap == null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PintarPill(false);
                MostrarVacio(TxtSinServicio);
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Snake_case: recording_file, min_level (wire de AgpJson).
            var cfg = snap.Config;
            _cfgModulos = cfg?.Modules ?? new Dictionary<string, bool>();
            _cfgMinLevel = string.IsNullOrEmpty(cfg?.MinLevel) ? "debug" : cfg!.MinLevel!;
            _seq = snap.Seq;
            _grabando = snap.Recording;
            _archivoGrabacion = snap.RecordingFile;

            // Sembrar buffer (el ÚNICO lugar donde el original filtra de verdad).
            _buffer.Clear();
            foreach (var e in snap.Entries ?? new List<DebugEntryWire>())
                if (PasaFiltro(e)) _buffer.Add(e);

            Render();
            RenderModulos();
            PintarBotonGrabar();
            PintarPill(true);
        });

        await PollearAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sustituto del push del WS: cada 500 ms pide lo nuevo por
    /// /api/debug/entries?since=. Falla = pill "Desconectado" y se sigue
    /// intentando, igual que el ws.onclose + setTimeout del original.</summary>
    private async Task PollearAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Cadencia, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            if (_client == null) return;

            var r = await _client.GetEntriesAsync(_seq, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (r == null) { PintarPill(false); return; }
                PintarPill(true);
                var entradas = r.Entries ?? new List<DebugEntryWire>();
                if (_pausado)
                {
                    // Pausado: se DESCARTAN (no se encolan), como el
                    // `if (state.paused) return;` del ws.onmessage. El `since`
                    // avanza igual para no recibirlas de nuevo al reanudar.
                    foreach (var e in entradas) if (e.Seq > _seq) _seq = e.Seq;
                    return;
                }
                foreach (var e in entradas) Agregar(e);
            });
        }
    }

    // =========================================================================
    //  buffer / render
    // =========================================================================

    /// <summary>appendEntry() del original.</summary>
    private void Agregar(DebugEntryWire e)
    {
        if (e.Seq > _seq) _seq = e.Seq;
        if (!PasaFiltro(e)) return;

        _buffer.Add(e);
        if (_buffer.Count > MaxRender)
        {
            _buffer.RemoveAt(0);
            if (_lineas.Count > 0) _lineas.RemoveAt(0);
        }
        _lineas.Add(Linea(e));
        MostrarVacio(null);

        if (_autoScroll && !_pausado) ScrollAlFinal();
    }

    /// <summary>renderAll() del original — OJO: NO filtra (bug conocido, ver la
    /// cabecera). Vuelca el buffer tal cual y scrollea al final.</summary>
    private void Render()
    {
        if (_buffer.Count == 0)
        {
            _lineas.Clear();
            MostrarVacio(TxtSinEventos);
            return;
        }
        _lineas.Clear();
        foreach (var e in _buffer) _lineas.Add(Linea(e));
        MostrarVacio(null);
        ScrollAlFinal();
    }

    /// <summary>passesFilter() del original, incluida la comparación estricta
    /// `mods[e.module] === false` (un módulo que no figura en el diccionario
    /// PASA, no se filtra).</summary>
    private bool PasaFiltro(DebugEntryWire e)
    {
        if (_cfgModulos == null) return true;
        string modulo = e.Module ?? "";
        if (e.Module != null && _cfgModulos.TryGetValue(modulo, out bool ena) && !ena) return false;
        if (Rango(e.Level) < Rango(_minLevel)) return false;
        if (_busqueda.Length > 0)
        {
            string q = _busqueda.ToLowerInvariant();
            string msg = (e.Message ?? "").ToLowerInvariant();
            string mod = modulo.ToLowerInvariant();
            if (!msg.Contains(q, StringComparison.Ordinal) &&
                !mod.Contains(q, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>levelRank(): índice en debug/info/warn/error. -1 para un nivel
    /// desconocido (el indexOf del JS devuelve lo mismo, y con eso una entrada
    /// de nivel raro queda SIEMPRE filtrada).</summary>
    private static int Rango(string? nivel)
    {
        string l = (string.IsNullOrEmpty(nivel) ? "info" : nivel!).ToLowerInvariant();
        return Array.IndexOf(OrdenNiveles, l);
    }

    private DebugLinea Linea(DebugEntryWire e)
    {
        string nivel = (string.IsNullOrEmpty(e.Level) ? "info" : e.Level!).ToLowerInvariant();
        IBrush colorMod;
        IBrush colorMsg = Texto;
        switch (nivel)
        {
            case "warn":  colorMod = Ambar; colorMsg = Ambar; break;
            case "error": colorMod = Rojo;  colorMsg = Rojo;  break;
            // El original pinta el módulo de las líneas "debug" en azul
            // (#7DA2C9). En la paleta clara de PilotX no hay token azul, así que
            // va gris (Idle) — el resto de la línea no cambia.
            case "debug": colorMod = Gris; break;
            default:      colorMod = VerdeTexto; break;
        }
        return new DebugLinea
        {
            Hora = FmtTs(e.Ts),
            Modulo = string.IsNullOrEmpty(e.Module) ? "host" : e.Module!,
            Mensaje = e.Message ?? "",
            ModuloColor = colorMod,
            MensajeColor = colorMsg,
        };
    }

    /// <summary>fmtTs(): ISO-8601 UTC → hora LOCAL hh:mm:ss.mmm (el `new Date()`
    /// del JS convierte a local). Si no parsea, devuelve el ISO crudo.</summary>
    private static string FmtTs(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out var d))
            return d.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return iso!;
    }

    /// <summary>Cartel del visor vacío. Recibe la CLAVE en castellano (no el
    /// texto ya traducido): así se puede volver a pintar en el idioma nuevo
    /// cuando el operario cambia de idioma con la pantalla abierta.</summary>
    private void MostrarVacio(string? clave)
    {
        _vacioClave = string.IsNullOrEmpty(clave) ? null : clave;
        var tb = this.FindControl<TextBlock>("BufferVacio");
        if (tb == null) return;
        if (string.IsNullOrEmpty(clave)) { tb.IsVisible = false; return; }
        tb.Text = T(clave!);
        tb.IsVisible = true;
    }

    private void ScrollAlFinal()
    {
        var sv = this.FindControl<ScrollViewer>("BufferScroll");
        if (sv == null) return;
        // Después del layout: la lista recién cambió de alto.
        Dispatcher.UIThread.Post(() =>
        {
            double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            sv.Offset = new Vector(sv.Offset.X, max);
        }, DispatcherPriority.Background);
    }

    /// <summary>Si el operario scrolleó para arriba se apaga el auto-scroll
    /// (mismo umbral de 32 px del original).</summary>
    private void OnBufferScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = sender as ScrollViewer;
        if (sv == null) return;
        _autoScroll = (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height) < 32;
    }

    // =========================================================================
    //  chips de módulo
    // =========================================================================

    private void RenderModulos()
    {
        var host = this.FindControl<WrapPanel>("ChipsHost");
        if (host == null) return;
        host.Children.Clear();
        if (_cfgModulos == null) return;

        // Object.keys(known).sort() → orden ordinal, igual que el JS.
        var nombres = new List<string>(_cfgModulos.Keys);
        nombres.Sort(StringComparer.Ordinal);

        foreach (var nombre in nombres)
        {
            bool on = _cfgModulos.TryGetValue(nombre, out bool v) && v;
            string clave = nombre;
            var chip = new Button
            {
                // Nombre de módulo = dato del sistema: NO se traduce.
                Content = nombre,
                Background = on ? VerdeSuave : Superficie2,
                Foreground = on ? VerdeTexto : TextoTenue,
                BorderBrush = on ? VerdeTexto : BordeAlto,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                MinHeight = 38,
                Padding = new Thickness(14, 0, 14, 0),
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 8),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            chip.Click += (_, __) => _ = ToggleModuloAsync(clave);
            host.Children.Add(chip);
        }
    }

    private async Task ToggleModuloAsync(string nombre)
    {
        if (_cfgModulos == null) return;
        bool actual = _cfgModulos.TryGetValue(nombre, out bool v) && v;
        _cfgModulos[nombre] = !actual;
        RenderModulos();
        if (_client != null)
            await _client.SetModuloAsync(nombre, !actual, _cts?.Token ?? CancellationToken.None)
                         .ConfigureAwait(true);
        // El original llama renderAll() acá "para re-filtrar el buffer en RAM",
        // pero renderAll NO filtra: en la práctica solo re-pinta lo mismo. Se
        // replica igual (ver bug 1 en la cabecera).
        Render();
    }

    // =========================================================================
    //  controles de la toolbar
    // =========================================================================

    private void OnBuscarChanged(object? sender, TextChangedEventArgs e)
    {
        _busqueda = this.FindControl<TextBox>("TxtBuscar")?.Text ?? "";
        Render();
    }

    private void OnNivelClick(object? sender, RoutedEventArgs e)
    {
        var b = sender as Button;
        string nivel = b?.Tag as string ?? "";
        if (nivel.Length == 0) return;
        _minLevel = nivel;
        PintarNiveles();
        if (_cfgModulos != null)
        {
            _cfgMinLevel = _minLevel;
            _ = _client?.PutConfigAsync(_cfgModulos, _cfgMinLevel, _cts?.Token ?? CancellationToken.None);
        }
        Render();
    }

    private void PintarNiveles()
    {
        PintarNivel("BtnNivelDebug", "debug");
        PintarNivel("BtnNivelInfo", "info");
        PintarNivel("BtnNivelWarn", "warn");
        PintarNivel("BtnNivelError", "error");
    }

    private void PintarNivel(string control, string nivel)
    {
        var b = this.FindControl<Button>(control);
        if (b == null) return;
        bool on = _minLevel == nivel;
        b.Background = on ? Verde : Superficie;
        b.Foreground = on ? Texto : TextoTenue;
        b.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void OnPausarClick(object? sender, RoutedEventArgs e)
    {
        _pausado = !_pausado;
        PintarBotonPausar();
        if (!_pausado) ScrollAlFinal();
    }

    private void PintarBotonPausar()
    {
        var b = this.FindControl<Button>("BtnPausar");
        if (b == null) return;
        b.Content = _pausado ? T(TxtReanudar) : T(TxtPausar);
        // .primary del original: el botón queda con el acento mientras la
        // pantalla está en pausa.
        b.Background = _pausado ? Verde : Superficie;
        b.Foreground = Texto;
    }

    private async void OnLimpiarClick(object? sender, RoutedEventArgs e)
    {
        // Igual que el original: primero se vacía en pantalla, después se le
        // avisa al servidor (y si el POST falla, no se deshace nada).
        _buffer.Clear();
        Render();
        if (_client != null)
            await _client.ClearAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(true);
    }

    private async void OnGrabarClick(object? sender, RoutedEventArgs e)
    {
        _grabando = !_grabando;
        PintarBotonGrabar();
        if (_client == null) return;
        var r = await _client.RecordAsync(_grabando, _cts?.Token ?? CancellationToken.None)
                             .ConfigureAwait(true);
        if (r == null)
        {
            // catch del original: se revierte el botón.
            _grabando = !_grabando;
            PintarBotonGrabar();
            return;
        }
        // Se toma `file` sin mirar `ok` — igual que el JS (ver bug 3).
        _archivoGrabacion = r.File;
        PintarBotonGrabar();
    }

    private void PintarBotonGrabar()
    {
        var b = this.FindControl<Button>("BtnGrabar");
        if (b == null) return;
        if (_grabando)
        {
            // "● Grabando…" — el punto rojo del ::before del original.
            b.Content = "●  " + T(TxtGrabando);
            b.Foreground = Rojo;
            b.Background = RojoSuave;
            b.BorderBrush = Rojo;
            b.FontWeight = FontWeight.Bold;
            ToolTip.SetTip(b, _archivoGrabacion ?? "");
        }
        else
        {
            b.Content = T(TxtGrabar);
            b.Foreground = Texto;
            b.Background = Superficie;
            b.BorderBrush = BordeAlto;
            b.FontWeight = FontWeight.Normal;
            ToolTip.SetTip(b, "");
        }
    }

    private void PintarPill(bool vivo)
    {
        _pillViva = vivo;
        var box = this.FindControl<Border>("EstadoPillBox");
        var txt = this.FindControl<TextBlock>("EstadoPill");
        var dot = this.FindControl<Ellipse>("EstadoDot");
        if (box == null || txt == null) return;

        if (vivo)
        {
            txt.Text = T(TxtEnVivo);
            txt.Foreground = VerdeTexto;
            box.Background = VerdeSuave;
            box.BorderBrush = VerdeTexto;
            if (dot != null) dot.Fill = VerdeTexto;
        }
        else
        {
            txt.Text = T(TxtDesconectado);
            txt.Foreground = TextoTenue;
            box.Background = Superficie2;
            box.BorderBrush = BordeAlto;
            if (dot != null) dot.Fill = TextoTenue;
        }
    }

    // =========================================================================
    //  exportar (exportBuffer del original)
    // =========================================================================

    private async void OnExportarClick(object? sender, RoutedEventArgs e)
    {
        // MISMO formato de línea y MISMO nombre de archivo que el original
        // ("debug-" + toISOString con ':' y '.' cambiados por '-' + ".log").
        var sb = new StringBuilder();
        for (int i = 0; i < _buffer.Count; i++)
        {
            var x = _buffer[i];
            if (i > 0) sb.Append('\n');
            sb.Append(x.Ts ?? "")
              .Append(" [").Append(string.IsNullOrEmpty(x.Module) ? "host" : x.Module).Append("] ")
              .Append((string.IsNullOrEmpty(x.Level) ? "info" : x.Level!).ToUpperInvariant())
              .Append(' ')
              .Append(x.Message ?? "");
        }

        string nombre = "debug-" +
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                    .Replace(':', '-').Replace('.', '-') + ".log";

        // Explorador PROPIO de PilotX en modo GUARDAR (nombre con teclado
        // nativo, confirmación inline si pisa). Fallback al SaveFilePicker del
        // sistema en no-Windows.
        if (ExploradorArchivos.CardDisponible)
        {
            string? ruta = await ExploradorArchivos.GuardarAsync(
                T("Exportar log"),
                System.IO.Path.GetFileNameWithoutExtension(nombre),
                "log");
            if (ruta == null) return;
            try
            {
                System.IO.File.WriteAllBytes(ruta, new UTF8Encoding(false).GetBytes(sb.ToString()));
            }
            catch (Exception ex)
            {
                var err2 = AgroParallel.Services.AgpErrorMapper.FromException(ex);
                Aviso?.Invoke(err2.Code + " · " + T(err2.Friendly));
            }
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        IStorageFile? destino;
        try
        {
            destino = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Exportar log"),
                SuggestedFileName = nombre,
                DefaultExtension = "log",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(T("Registro")) { Patterns = new[] { "*.log" } }
                }
            });
        }
        catch { return; }
        if (destino == null) return;

        try
        {
            await using var stream = await destino.OpenWriteAsync().ConfigureAwait(true);
            var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var err = AgroParallel.Services.AgpErrorMapper.FromException(ex);
            Aviso?.Invoke(err.Code + " · " + T(err.Friendly));
        }
    }
}
