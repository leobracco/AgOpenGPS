// NodosPanel.axaml.cs
//
// Reemplazo nativo COMPLETO de pages/nodos.html (cierra el port #12, que habia
// quedado a medias: la lista era nativa pero el boton "Configurar" abria la
// pagina entera en WebView solo para poder aceptar/ignorar/renombrar un nodo o
// mirar el diagnostico MQTT — o sea, Chromium arrancaba igual).
//
// Nativo ahora:
//   · pantalla LISTA: pills online/offline + broker + implemento, banner de
//     nodos del implemento activo caidos, tabs curados con contador, tabla de
//     7 columnas y las acciones por fila (Aceptar, Ignorar, pin del implemento,
//     Configurar, Renombrar, Eliminar, Restaurar).
//   · pantalla DIAGNOSTICO: pill de conexion, grid del broker, caja de error
//     con codigo dictable, Reconectar / Refrescar / Wildcard / Re-escanear y el
//     log de los ultimos mensajes MQTT.
//
// Sigue en HTML (otras paginas, con su propio port):
//   · pages/nodo-detalle.html — se abre al tocar una fila (matriz wifi/mqtt,
//     firmwares/OTA, comandos del nodo).
//   · pages/setup.html — asistente de primera vez.
//   · las paginas de configuracion por producto: el boton "Configurar" de la
//     fila abre el PANEL NATIVO del producto (QuantiX/VistaX/FlowX/SectionX/
//     StormX), no la pagina.
// La pagina nodos.html NO se toca: el Hub remoto y el celular la siguen usando.
//
// El banner de alarma de este panel NO beepea: el beep (y su "Silenciar 10 min")
// es responsabilidad exclusiva de CabinaAlarmasOverlay, que corre SIEMPRE. Con
// los dos sonando el operario escuchaba doble con el panel abierto.
//
// API: Attach(NodosClient) prende el polling (unified 3 s); Detach() lo apaga.
// El diag se pollea a 2 s SOLO mientras su pantalla esta a la vista.
//
// OJO con el idioma: Traductor.Aplicar(this) se llama UNA vez, en el ctor. No
// volver a llamarlo en los render — se guarda el primer texto de cada control y
// despues lo reescribe, o sea que congelaria las pills y los contadores en el
// valor del primer tick. Lo que escribe el codigo se traduce con T().

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
using Avalonia.VisualTree;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class NodosPanel : UserControl, IPanelEmbebible
{
    // ---- wire / estado -----------------------------------------------------
    private NodosClient? _client;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _diagCts;
    private string _activeTab = "pendiente";
    private string _pantalla = "lista";
    // Cache del ULTIMO response COMPLETO (no solo la lista): al cambiar de tab
    // se re-renderiza con esto. Antes se armaba un response nuevo con la lista
    // sola y la pill parpadeaba "Broker desconectado" hasta el proximo tick.
    private NodosUnifiedResponse? _last;
    private NodoDiag? _diag;

    // ---- paleta clara PilotX (misma que GuiasPanel / CoreXEcuPanel) --------
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgFilaAlt  = new SolidColorBrush(Color.Parse("#F5F7F4"));
    private static readonly IBrush BgFilaSel  = new SolidColorBrush(Color.Parse("#DCEFD8"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#7A857B"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ok         = new SolidColorBrush(Color.Parse("#3D9A33"));
    private static readonly IBrush Warn       = new SolidColorBrush(Color.Parse("#B98A2E"));
    private static readonly IBrush Err        = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush Dim        = new SolidColorBrush(Color.Parse("#8A958B"));
    private static readonly IBrush BadgeCrit  = new SolidColorBrush(Color.Parse("#C92D2D"));
    private static readonly IBrush BadgeWarn  = new SolidColorBrush(Color.Parse("#DC8C1E"));

    private static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");
    private const string ColsTabla = "56,*,96,116,78,104,300";

    // boot_reason -> severidad. Criticas = cuelgue del firmware; poweron /
    // sw_reset / ext / deepsleep son benignas y no ensucian la pantalla.
    private static readonly HashSet<string> BootCrit = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "task_wdt", "int_wdt", "panic", "brownout", "wdt" };
    private static readonly HashSet<string> BootWarn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "sdio", "unknown" };

    // ---- callbacks al host -------------------------------------------------

    /// <summary>El operario cerro el panel (X).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Toco una fila: detalle del nodo (pages/nodo-detalle.html).</summary>
    public Action<string>? OnRequestDetalle { get; set; }

    /// <summary>"Configurar" de un nodo aceptado: el host abre el panel nativo
    /// del producto segun el tipo (quantix / vistax / sectionx / flowx / stormx).</summary>
    public Action<string>? OnRequestConfigurarProducto { get; set; }

    /// <summary>Asistente de primera vez (pages/setup.html).</summary>
    public Action? OnRequestAsistente { get; set; }

    /// <summary>Aviso corto para el operario (el host lo muestra como toast).
    /// Nunca modal bloqueante.</summary>
    public event Action<string>? Aviso;

    // ---- modal interno -----------------------------------------------------
    private Action<string>? _modalOnOk;

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    public NodosPanel()
    {
        InitializeComponent();
        // El diccionario se aplica UNA SOLA VEZ, al construir. Traductor.Aplicar
        // se guarda el texto que encuentra la primera vez en cada control y en
        // TODAS las pasadas siguientes se lo vuelve a escribir encima: llamarlo
        // al final de cada render congelaba las pills (online/offline, broker,
        // implemento), los contadores de los tabs y el grid del diagnostico en
        // el valor del primer tick, y hacia que el dialogo de "Eliminar nodo"
        // mostrara el titulo y el mensaje del dialogo ANTERIOR (el operario leia
        // "Aceptar nodo" mientras confirmaba un borrado). Todo texto que escribe
        // el codigo pasa por T() en el punto donde se escribe.
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
        PaintTabs();
        PaintPantalla();

        var txt = this.FindControl<TextBox>("ModalTexto");
        if (txt != null)
        {
            // El teclado nativo NO es automatico por foco: cada TextBox lo pide
            // con la misma senal HTTP que mandan las paginas del Hub.
            txt.GotFocus  += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, "Nombre del nodo"); };
            txt.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio. Las pills (online/offline, broker, implemento)
    /// y el botón "Asistente" quedan en la fila compacta de arriba.</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public void Attach(NodosClient client)
    {
        _client = client;
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
        // Si el panel se cerro estando en Diagnostico, al volver se reengancha
        // su polling (Detach lo apaga y la pantalla queda como estaba).
        if (_pantalla == "diag") ArrancarDiag();
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        PararDiag();
        CerrarModal();
        _ = _client?.TecladoAsync(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_client == null) return;
        NodosUnifiedResponse? resp;
        try { resp = await _client.GetUnifiedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Render(resp));
    }

    // El diag es caro y solo sirve mirandolo: se pollea unicamente mientras la
    // pantalla de Diagnostico esta a la vista.
    private void ArrancarDiag()
    {
        if (_diagCts != null) return;
        _diagCts = new CancellationTokenSource();
        _ = DiagLoopAsync(_diagCts.Token);
    }

    private void PararDiag()
    {
        try { _diagCts?.Cancel(); } catch { }
        _diagCts = null;
    }

    private async Task DiagLoopAsync(CancellationToken ct)
    {
        await DiagTickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await DiagTickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task DiagTickAsync(CancellationToken ct)
    {
        if (_client == null) return;
        NodosDiagResponse? resp;
        try { resp = await _client.GetDiagnosticAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => RenderDiag(resp));
    }

    // =========================================================================
    //  pantalla LISTA
    // =========================================================================

    private void Render(NodosUnifiedResponse? resp)
    {
        if (resp != null) _last = resp;
        var nodos = _last?.Nodos ?? new List<NodoUnified>();

        // ---- stats del header ----
        int online = 0;
        foreach (var n in nodos) if (n.Online) online++;
        SetTexto("StatOnline", online.ToString(CultureInfo.InvariantCulture) + " online");
        SetTexto("StatOffline", (nodos.Count - online).ToString(CultureInfo.InvariantCulture) + " offline");

        // ---- pill del broker ----
        bool conn = _last?.BrokerConnected ?? false;
        var brokerDot = this.FindControl<Ellipse>("BrokerDot");
        if (brokerDot != null) brokerDot.Fill = conn ? Ok : Err;
        var brokerTxt = this.FindControl<TextBlock>("BrokerText");
        if (brokerTxt != null)
        {
            brokerTxt.Text = T(conn ? "Broker conectado" : "Broker desconectado");
            brokerTxt.Foreground = conn ? Texto : Err;
        }

        // ---- pill del implemento activo ----
        string slug = _last?.ImplementoSlug ?? "";
        SetTexto("ImplementoText", string.IsNullOrWhiteSpace(slug)
            ? T("Implemento: ninguno") : T("Implemento:") + " " + slug);

        RenderBanner(nodos, slug);

        // ---- contadores por tab: se confia en el `estado` del server (el
        // server ya manda "offline" para los aceptados sin senal) ----
        int cP = 0, cA = 0, cO = 0, cI = 0;
        foreach (var n in nodos)
        {
            switch ((n.Estado ?? "").ToLowerInvariant())
            {
                case "pendiente": cP++; break;
                case "aceptado":  cA++; break;
                case "offline":   cO++; break;
                case "ignorado":  cI++; break;
            }
        }
        SetTabLabel("TabPendiente", "Pendientes", cP);
        SetTabLabel("TabAceptado",  "Aceptados",  cA);
        SetTabLabel("TabOffline",   "Off-line",   cO);
        SetTabLabel("TabIgnorado",  "Ignorados",  cI);

        // ---- filas del tab activo ----
        var filas = this.FindControl<StackPanel>("FilasList");
        if (filas == null) return;
        filas.Children.Clear();

        var visibles = new List<NodoUnified>();
        foreach (var n in nodos)
            if (string.Equals(n.Estado ?? "", _activeTab, StringComparison.OrdinalIgnoreCase))
                visibles.Add(n);

        if (visibles.Count == 0)
        {
            filas.Children.Add(new TextBlock
            {
                Text = T("No hay nodos en esta vista."),
                Padding = new Thickness(14, 20, 14, 20),
                Foreground = TextoDim,
                FontSize = 13
            });
            return;
        }

        bool alt = false;
        foreach (var n in visibles)
        {
            filas.Children.Add(BuildRow(n, alt));
            alt = !alt;
        }
    }

    private void RenderBanner(List<NodoUnified> nodos, string slug)
    {
        var caidos = new List<NodoUnified>();
        foreach (var n in nodos) if (n.DelImplementoActivo && !n.Online) caidos.Add(n);

        var banner = this.FindControl<Border>("AlertBanner");
        var lista = this.FindControl<StackPanel>("AlertLista");
        if (banner == null) return;
        banner.IsVisible = caidos.Count > 0;
        if (caidos.Count == 0 || lista == null) return;

        SetTexto("AlertTitulo", string.IsNullOrWhiteSpace(slug)
            ? T("Nodos del implemento activo caídos")
            : T("Nodos del implemento activo caídos") + " (" + slug + ")");

        lista.Children.Clear();
        foreach (var n in caidos)
        {
            // Alias/UID del operario: NO se traducen jamas.
            string label = !string.IsNullOrWhiteSpace(n.Alias) ? n.Alias!
                         : !string.IsNullOrWhiteSpace(n.Uid) ? n.Uid! : "?";
            if (!string.IsNullOrWhiteSpace(n.Tipo)) label += " · " + n.Tipo;
            label += string.IsNullOrWhiteSpace(n.LastSeenUtc)
                ? " — " + T("nunca visto")
                : " — " + T("última señal") + " " + RelTime(n.LastSeenUtc);
            lista.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Texto,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private Border BuildRow(NodoUnified n, bool alt)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(ColsTabla) };

        // --- Estado: punto verde / circulo gris ---
        var dot = new Ellipse
        {
            Width = 11, Height = 11,
            Fill = n.Online ? Ok : Brushes.Transparent,
            Stroke = n.Online ? Ok : Dim,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 12, 4, 12)
        };
        ToolTip.SetTip(dot, T(n.Online ? "En línea" : "Sin señal"));
        Grid.SetColumn(dot, 0);
        g.Children.Add(dot);

        // --- Alias / UID + badges ---
        var aliasBox = new StackPanel { Spacing = 2, Margin = new Thickness(6, 9, 4, 9), VerticalAlignment = VerticalAlignment.Center };
        var linea1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        bool hayAlias = !string.IsNullOrWhiteSpace(n.Alias);
        linea1.Children.Add(new TextBlock
        {
            Text = hayAlias ? n.Alias! : (n.Uid ?? "?"),
            Foreground = Texto,
            FontSize = 13,
            FontWeight = hayAlias ? FontWeight.SemiBold : FontWeight.Normal,
            FontFamily = hayAlias ? FontFamily.Default : Mono,
            VerticalAlignment = VerticalAlignment.Center
        });
        var badgeBoot = BootBadge(n.BootReason);
        if (badgeBoot != null) linea1.Children.Add(badgeBoot);
        if (n.SafeMode)
        {
            var safe = MakeBadge("SAFE", BadgeWarn);
            ToolTip.SetTip(safe, T("El nodo arrancó en modo seguro (se colgó varias veces)"));
            linea1.Children.Add(safe);
        }
        aliasBox.Children.Add(linea1);
        if (hayAlias)
        {
            aliasBox.Children.Add(new TextBlock
            {
                Text = n.Uid ?? "",
                Foreground = TextoDim,
                FontSize = 11,
                FontFamily = Mono
            });
        }
        Grid.SetColumn(aliasBox, 1);
        g.Children.Add(aliasBox);

        // --- Tipo (pill con color de producto) ---
        var tipoPill = new Border
        {
            Background = BgFila,
            BorderBrush = Borde,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(n.Tipo) ? "—" : n.Tipo!,
                Foreground = TipoColor(n.Tipo),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            }
        };
        Grid.SetColumn(tipoPill, 2);
        g.Children.Add(tipoPill);

        Grid.SetColumn(CeldaTexto(g, string.IsNullOrWhiteSpace(n.Ip) ? "—" : n.Ip!, true), 3);
        Grid.SetColumn(CeldaTexto(g, string.IsNullOrWhiteSpace(n.Firmware) ? "—" : n.Firmware!, true), 4);
        Grid.SetColumn(CeldaTexto(g, RelTime(n.LastSeenUtc), false), 5);

        // --- acciones segun el estado curado ---
        var acciones = new WrapPanel { Margin = new Thickness(4, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
        string uid = n.Uid ?? "";
        string est = (n.Estado ?? "").ToLowerInvariant();
        if (est == "pendiente")
        {
            acciones.Children.Add(BotonAccion("Aceptar", "Dar de alta el nodo con un nombre", () => PedirAceptar(n)));
            acciones.Children.Add(BotonAccion("Ignorar", "Dejar de mostrarlo en Pendientes", () => PedirIgnorar(n)));
        }
        else if (est == "aceptado" || est == "offline")
        {
            // Pin del implemento ACTIVO: solo los nodos marcados disparan alarma
            // cuando caen. Sin emoji a proposito — la pantalla de cabina no
            // siempre tiene la fuente con los pictogramas.
            bool pin = n.DelImplementoActivo;
            acciones.Children.Add(BotonAccion(
                pin ? "En implemento" : "Asignar",
                pin ? "Quitar del implemento activo" : "Asignar al implemento activo",
                () => ToggleImplemento(n), destacado: pin));
            if (est == "aceptado")
                acciones.Children.Add(BotonAccion("Configurar", "Abrir la configuración del producto",
                    () => Configurar(n)));
            acciones.Children.Add(BotonAccion("Renombrar", "Cambiar el nombre del nodo", () => PedirRenombrar(n)));
            acciones.Children.Add(BotonAccion("Eliminar", "Sacarlo de la configuración",
                () => PedirEliminar(n), destructivo: true));
        }
        else if (est == "ignorado")
        {
            acciones.Children.Add(BotonAccion("Restaurar", "Volver a mostrarlo en Pendientes", () => Restaurar(n)));
        }
        Grid.SetColumn(acciones, 6);
        g.Children.Add(acciones);

        var fila = new Border
        {
            Child = g,
            Background = alt ? BgFilaAlt : BgFila,
            BorderBrush = Borde,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(fila, T("Ver el detalle del nodo"));
        // Tocar la fila (fuera de los botones) abre el detalle del nodo.
        fila.Tapped += (_, e) =>
        {
            if (VieneDeBoton(e.Source)) return;
            if (!string.IsNullOrEmpty(uid)) OnRequestDetalle?.Invoke(uid);
        };
        return fila;
    }

    private static bool VieneDeBoton(object? origen)
    {
        var v = origen as Visual;
        while (v != null)
        {
            if (v is Button) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private TextBlock CeldaTexto(Grid g, string texto, bool mono)
    {
        var tb = new TextBlock
        {
            Text = texto,
            Padding = new Thickness(6, 9, 4, 9),
            Foreground = TextoMuted,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (mono) tb.FontFamily = Mono;
        ToolTip.SetTip(tb, texto);
        g.Children.Add(tb);
        return tb;
    }

    private Button BotonAccion(string texto, string tip, Action accion,
                               bool destructivo = false, bool destacado = false)
    {
        var b = new Button
        {
            Content = T(texto),
            MinHeight = 40,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 3, 6, 3),
            CornerRadius = new CornerRadius(8),
            Background = destacado ? BgFilaSel : BgFila,
            Foreground = destructivo ? Err : Texto,
            BorderBrush = destacado ? Verde : Borde,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(b, T(tip));
        b.Click += (_, __) => accion();
        return b;
    }

    private static Border? BootBadge(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        string key = reason!.Trim();
        if (BootCrit.Contains(key))
        {
            var b = MakeBadge("⚠ " + key.ToUpperInvariant(), BadgeCrit);
            ToolTip.SetTip(b, T("Último reinicio:") + " " + key + " (" + T("anormal — revisar") + ")");
            return b;
        }
        if (BootWarn.Contains(key))
        {
            var b = MakeBadge(key.ToUpperInvariant(), BadgeWarn);
            ToolTip.SetTip(b, T("Último reinicio:") + " " + key);
            return b;
        }
        return null;   // poweron / sw_reset / ext / deepsleep -> silencio
    }

    private static Border MakeBadge(string text, IBrush bg) => new Border
    {
        Background = bg,
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(7, 1, 7, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 9,
            FontWeight = FontWeight.Bold
        }
    };

    private static IBrush TipoColor(string? tipo)
    {
        string k = (tipo ?? "").ToLowerInvariant();
        if (k.Contains("quantix")) return new SolidColorBrush(Color.Parse("#4BA63F"));
        if (k.Contains("vistax"))  return new SolidColorBrush(Color.Parse("#3D87C6"));
        if (k.Contains("section")) return new SolidColorBrush(Color.Parse("#DC8C1E"));
        if (k.Contains("storm"))   return new SolidColorBrush(Color.Parse("#A06FBF"));
        if (k.Contains("flow"))    return new SolidColorBrush(Color.Parse("#2BB8B8"));
        return TextoMuted;
    }

    /// <summary>Tiempo relativo, igual al JS: ahora / hace N s / min / h / d.</summary>
    private static string RelTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return "—";
        double secs = (DateTime.UtcNow - t).TotalSeconds;
        if (secs < 5) return "ahora";
        if (secs < 60) return "hace " + ((int)secs).ToString(CultureInfo.InvariantCulture) + " s";
        if (secs < 3600) return "hace " + ((int)(secs / 60)).ToString(CultureInfo.InvariantCulture) + " min";
        if (secs < 86400) return "hace " + ((int)(secs / 3600)).ToString(CultureInfo.InvariantCulture) + " h";
        return "hace " + ((int)(secs / 86400)).ToString(CultureInfo.InvariantCulture) + " d";
    }

    private static string Hms(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return "";
        return t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // =========================================================================
    //  acciones de curado
    // =========================================================================

    private void PedirAceptar(NodoUnified n)
    {
        string uid = n.Uid ?? "";
        AbrirModalTexto("Aceptar nodo",
            "Ponele un nombre que se entienda (por ejemplo \"Motor izquierdo\"):",
            "Nodo " + uid,
            async alias => await AceptarAsync(n, alias));
    }

    private async Task AceptarAsync(NodoUnified n, string alias)
    {
        if (_client == null || string.IsNullOrWhiteSpace(n.Uid)) return;
        var r = await _client.AceptarAsync(n.Uid!, n.Tipo, alias);
        if (r?.Ok == true)
        {
            _activeTab = "aceptado";     // igual que el JS: salta al tab donde quedo
            PaintTabs();
            Aviso?.Invoke(r.AutoAsignado == true
                ? PilotX.Cockpit.Bars.Traductor.T("Nodo aceptado y asignado al implemento activo")
                : PilotX.Cockpit.Bars.Traductor.T("Nodo aceptado"));
            await RefrescarYaAsync();
        }
        else Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo aceptar el nodo") + " (AGP-NET-201)");
    }

    private void PedirIgnorar(NodoUnified n)
    {
        AbrirModalConfirm("Ignorar nodo",
            "¿Ignorar este nodo? Deja de aparecer en Pendientes.",
            "Ignorar", false,
            async () =>
            {
                if (_client == null || string.IsNullOrWhiteSpace(n.Uid)) return;
                if (await _client.IgnorarAsync(n.Uid!))
                {
                    Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Nodo ignorado"));
                    await RefrescarYaAsync();
                }
                else Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo ignorar el nodo") + " (AGP-NET-201)");
            });
    }

    private void PedirRenombrar(NodoUnified n)
    {
        AbrirModalTexto("Renombrar nodo", "Nuevo nombre:", n.Alias ?? "",
            async nuevo =>
            {
                if (_client == null || string.IsNullOrWhiteSpace(n.Uid)) return;
                if (await _client.RenombrarAsync(n.Uid!, nuevo))
                {
                    Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Nodo renombrado"));
                    await RefrescarYaAsync();
                }
                else Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo renombrar el nodo") + " (AGP-NET-201)");
            });
    }

    private void PedirEliminar(NodoUnified n)
    {
        AbrirModalConfirm("Eliminar nodo",
            "¿Sacar este nodo de la configuración?\n\nUID: " + (n.Uid ?? "") +
            "\n\nSe pierde el nombre. Si vuelve a anunciarse, aparece de nuevo en Pendientes.",
            "Eliminar", true,
            async () =>
            {
                if (_client == null || string.IsNullOrWhiteSpace(n.Uid)) return;
                if (await _client.EliminarAsync(n.Uid!))
                {
                    Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Nodo eliminado"));
                    await RefrescarYaAsync();
                }
                else Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo eliminar el nodo") + " (AGP-NET-201)");
            });
    }

    private async void Restaurar(NodoUnified n)
    {
        if (_client == null || string.IsNullOrWhiteSpace(n.Uid)) return;
        if (await _client.RestaurarAsync(n.Uid!))
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Nodo restaurado"));
            await RefrescarYaAsync();
        }
        else Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo restaurar el nodo") + " (AGP-NET-201)");
    }

    private async void ToggleImplemento(NodoUnified n)
    {
        if (_client == null || string.IsNullOrWhiteSpace(n.Uid)) return;
        bool nuevo = !n.DelImplementoActivo;
        var r = await _client.AsignarImplementoAsync(n.Uid!, nuevo);
        if (r?.Ok == true)
        {
            Aviso?.Invoke(nuevo
                ? PilotX.Cockpit.Bars.Traductor.T("Nodo asignado al implemento activo")
                : PilotX.Cockpit.Bars.Traductor.T("Nodo quitado del implemento activo"));
            await RefrescarYaAsync();
            return;
        }
        // El JS no distinguia este caso y el toque quedaba mudo.
        Aviso?.Invoke(r?.Error == "no-active-implemento"
            ? PilotX.Cockpit.Bars.Traductor.T("No hay implemento activo")
            : PilotX.Cockpit.Bars.Traductor.T("No se pudo cambiar el implemento del nodo") + " (AGP-NET-201)");
    }

    private void Configurar(NodoUnified n)
    {
        string t = (n.Tipo ?? "").ToLowerInvariant();
        string producto =
            t.Contains("quantix")  ? "quantix"  :
            t.Contains("vistax")   ? "vistax"   :
            t.Contains("sectionx") ? "sectionx" :
            t.Contains("flow")     ? "flowx"    :
            t.Contains("storm")    ? "stormx"   : "";
        if (producto.Length == 0)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Todavía no hay pantalla de configuración para este tipo de nodo"));
            return;
        }
        OnRequestConfigurarProducto?.Invoke(producto);
    }

    /// <summary>Refresco inmediato de la lista tras una accion (equivale al
    /// `await refresh()` del JS). Sin updates optimistas: manda el server.</summary>
    private async Task RefrescarYaAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        await TickAsync(ct);
    }

    // =========================================================================
    //  pantalla DIAGNOSTICO
    // =========================================================================

    private void RenderDiag(NodosDiagResponse? resp)
    {
        var dot = this.FindControl<Ellipse>("MqttDot");
        var txt = this.FindControl<TextBlock>("MqttText");
        var d = resp?.Diag;
        _diag = d;

        if (resp == null)
        {
            if (dot != null) dot.Fill = Err;
            if (txt != null) { txt.Text = T("sin respuesta"); txt.Foreground = Err; }
            return;
        }
        if (!resp.Ok || d == null)
        {
            if (dot != null) dot.Fill = Err;
            if (txt != null) { txt.Text = T("sin servicio"); txt.Foreground = Err; }
            return;
        }

        if (dot != null) dot.Fill = d.Connected ? Ok : Err;
        if (txt != null)
        {
            txt.Text = T(d.Connected ? "CONECTADO" : "DESCONECTADO");
            txt.Foreground = d.Connected ? Ok : Err;
        }

        SetTexto("DiagBroker", (string.IsNullOrWhiteSpace(d.BrokerAddress) ? "?" : d.BrokerAddress!)
                               + ":" + d.BrokerPort.ToString(CultureInfo.InvariantCulture));
        SetTexto("DiagAttempts", d.ConnectAttempts.HasValue
            ? d.ConnectAttempts.Value.ToString(CultureInfo.InvariantCulture) : "—");
        SetTexto("DiagLastOk", string.IsNullOrWhiteSpace(d.LastConnectedUtc)
            ? T("— nunca —") : FechaLocal(d.LastConnectedUtc));
        SetTexto("DiagCount", d.KnownNodesCount.ToString(CultureInfo.InvariantCulture));
        SetTexto("DiagSubs", (d.Subscriptions == null || d.Subscriptions.Count == 0)
            ? "—" : string.Join("   ", d.Subscriptions));
        SetTexto("DiagWild", d.WildcardCaptureOn ? "ON" : "off");

        string gaps = d.SeqGapCount.ToString(CultureInfo.InvariantCulture);
        if (d.RecentSeqGaps != null && d.RecentSeqGaps.Count > 0)
        {
            var g0 = d.RecentSeqGaps[0];
            gaps += " · " + T("último") + ": " + (g0.Uid ?? "?") + " (" +
                    g0.Missed.ToString(CultureInfo.InvariantCulture) + " msg, " + Hms(g0.TimestampUtc) + ")";
        }
        SetTexto("DiagGaps", gaps);
        SetTexto("DiagResets", d.SeqResetCount.ToString(CultureInfo.InvariantCulture));

        var btnWild = this.FindControl<Button>("BtnWildcard");
        if (btnWild != null)
            btnWild.Content = T(d.WildcardCaptureOn ? "Desactivar captura wildcard" : "Activar captura wildcard");

        // ---- caja de error: solo si hay error Y no esta conectado ----
        var box = this.FindControl<Border>("DiagErrorBox");
        bool hayError = !string.IsNullOrWhiteSpace(d.LastError) && !d.Connected;
        if (box != null) box.IsVisible = hayError;
        if (hayError)
        {
            string code = string.IsNullOrWhiteSpace(d.LastErrorCode) ? "AGP-SYS-009" : d.LastErrorCode!;
            SetTexto("DiagErrorCode", code);
            SetTexto("DiagErrorTs", Hms(d.LastErrorUtc));
            SetTexto("DiagErrorMsg", d.LastError ?? "");
            SetTexto("DiagErrorAyuda",
                T("Para soporte: dictá el código") + " " + code + " " + T("por teléfono o WhatsApp."));
            var tech = this.FindControl<Expander>("DiagErrorTechBox");
            if (tech != null) tech.IsVisible = !string.IsNullOrWhiteSpace(d.LastErrorTechnical);
            SetTexto("DiagErrorTech", d.LastErrorTechnical ?? "");
        }

        RenderMsgLog(d);
    }

    private void RenderMsgLog(NodoDiag d)
    {
        var host = this.FindControl<StackPanel>("MsgLogHost");
        if (host == null) return;
        host.Children.Clear();

        var msgs = d.RecentMessages;
        if (msgs == null || msgs.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = T("— sin mensajes aún —"),
                Foreground = TextoDim, FontSize = 11, FontFamily = Mono
            });
            return;
        }

        // Con la captura wildcard prendida el log puede venir enorme: se acota a
        // 50 filas y el payload a 500 caracteres (el resto, en el tooltip).
        int max = Math.Min(msgs.Count, 50);
        for (int i = 0; i < max; i++)
        {
            var m = msgs[i];
            string payload = m.Payload ?? "";
            bool cortado = payload.Length > 500;
            if (cortado) payload = payload.Substring(0, 500) + "…";

            var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            fila.Children.Add(new TextBlock
            {
                Text = Hms(m.TimestampUtc), Foreground = TextoDim,
                FontSize = 11, FontFamily = Mono, MinWidth = 62
            });
            fila.Children.Add(new TextBlock
            {
                Text = m.Topic ?? "", Foreground = Verde,
                FontSize = 11, FontFamily = Mono, FontWeight = FontWeight.SemiBold
            });
            var pay = new TextBlock
            {
                Text = payload, Foreground = TextoMuted,
                FontSize = 11, FontFamily = Mono,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 520
            };
            if (cortado) ToolTip.SetTip(pay, m.Payload ?? "");
            fila.Children.Add(pay);
            host.Children.Add(fila);
        }
    }

    private static string FechaLocal(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return iso!;
        return t.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private async void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        var b = sender as Button;
        if (_client == null) return;
        if (b != null) { b.IsEnabled = false; b.Content = T("Reconectando…"); }
        var r = await _client.ReconnectAsync();
        if (b != null) { b.IsEnabled = true; b.Content = T("Reconectar al broker"); }
        // El POST devuelve el diag nuevo: se aprovecha y no se pide de nuevo.
        if (r != null) RenderDiag(r);
        else await DiagTickAsync(_diagCts?.Token ?? CancellationToken.None);
    }

    private async void OnRefreshDiagClick(object? sender, RoutedEventArgs e)
        => await DiagTickAsync(_diagCts?.Token ?? CancellationToken.None);

    private async void OnWildcardClick(object? sender, RoutedEventArgs e)
    {
        var b = sender as Button;
        if (_client == null) return;
        bool nuevo = !(_diag?.WildcardCaptureOn ?? false);
        if (b != null) b.IsEnabled = false;
        await _client.SetWildcardAsync(nuevo);
        if (b != null) b.IsEnabled = true;
        await DiagTickAsync(_diagCts?.Token ?? CancellationToken.None);
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        // El JS tampoco tiene endpoint de rescan: vuelve a leer la lista.
        await RefrescarYaAsync();
        Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Lista de nodos actualizada"));
    }

    // =========================================================================
    //  modal interno (texto / confirmacion)
    // =========================================================================

    private void AbrirModalTexto(string titulo, string mensaje, string valorInicial, Func<string, Task> onOk)
    {
        SetTexto("ModalTitulo", T(titulo));
        SetTexto("ModalMensaje", T(mensaje));
        var txt = this.FindControl<TextBox>("ModalTexto");
        if (txt != null)
        {
            txt.IsVisible = true;
            txt.Text = valorInicial ?? "";
        }
        var btnOk = this.FindControl<Button>("BtnModalConfirmar");
        if (btnOk != null)
        {
            btnOk.Content = T("Aceptar");
            btnOk.Background = Verde;
        }
        _modalOnOk = valor =>
        {
            string v = (valor ?? "").Trim();
            if (v.Length == 0) return;    // vacio = no confirma (igual que el JS)
            _ = onOk(v);
        };
        MostrarModal(true);
        txt?.Focus();
    }

    private void AbrirModalConfirm(string titulo, string mensaje, string textoOk, bool destructivo, Func<Task> onOk)
    {
        SetTexto("ModalTitulo", T(titulo));
        SetTexto("ModalMensaje", T(mensaje));
        var txt = this.FindControl<TextBox>("ModalTexto");
        if (txt != null) txt.IsVisible = false;
        var btnOk = this.FindControl<Button>("BtnModalConfirmar");
        if (btnOk != null)
        {
            btnOk.Content = T(textoOk);
            btnOk.Background = destructivo ? Err : Verde;
        }
        _modalOnOk = ignorado => { _ = onOk(); };
        MostrarModal(true);
    }

    private void MostrarModal(bool visible)
    {
        var ov = this.FindControl<Border>("ModalOverlay");
        if (ov != null) ov.IsVisible = visible;
    }

    private void CerrarModal()
    {
        _modalOnOk = null;
        MostrarModal(false);
        _ = _client?.TecladoAsync(false);
    }

    private void OnModalConfirmarClick(object? sender, RoutedEventArgs e)
    {
        var accion = _modalOnOk;
        var txt = this.FindControl<TextBox>("ModalTexto");
        string valor = txt?.Text ?? "";
        CerrarModal();
        accion?.Invoke(valor);
    }

    private void OnModalCancelarClick(object? sender, RoutedEventArgs e) => CerrarModal();

    private void OnModalBackdropPressed(object? sender, PointerPressedEventArgs e) => CerrarModal();

    // La card absorbe el toque para que no llegue al backdrop y cierre el modal.
    private void OnModalCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    // =========================================================================
    //  tabs / pantallas / helpers
    // =========================================================================

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tab) return;
        _activeTab = tab;
        PaintTabs();
        Render(null);   // re-render con el ULTIMO response completo (pills incluidas)
    }

    private void SetTabLabel(string ctrl, string label, int count)
    {
        var btn = this.FindControl<Button>(ctrl);
        if (btn != null) btn.Content = T(label) + "  (" + count.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private void PaintTabs()
    {
        PaintTab("TabPendiente", "pendiente");
        PaintTab("TabAceptado", "aceptado");
        PaintTab("TabOffline", "offline");
        PaintTab("TabIgnorado", "ignorado");
    }

    private void PaintTab(string ctrl, string tab)
    {
        var btn = this.FindControl<Button>(ctrl);
        if (btn == null) return;
        bool activo = _activeTab == tab;
        btn.Background = activo ? BgFila : Brushes.Transparent;
        btn.Foreground = activo ? Texto : TextoMuted;
        btn.BorderBrush = activo ? Verde : Brushes.Transparent;
        btn.BorderThickness = new Thickness(0, 0, 0, 3);
        btn.FontWeight = activo ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void OnPantallaListaClick(object? sender, RoutedEventArgs e) => MostrarPantalla("lista");
    private void OnPantallaDiagClick(object? sender, RoutedEventArgs e) => MostrarPantalla("diag");

    private void MostrarPantalla(string cual)
    {
        _pantalla = cual;
        PaintPantalla();
        if (cual == "diag") ArrancarDiag();   // tick inmediato adentro del loop
        else PararDiag();
    }

    private void PaintPantalla()
    {
        var lista = this.FindControl<Grid>("PantallaLista");
        var diag = this.FindControl<ScrollViewer>("PantallaDiag");
        if (lista != null) lista.IsVisible = _pantalla == "lista";
        if (diag != null) diag.IsVisible = _pantalla == "diag";

        var bl = this.FindControl<Button>("BtnPantallaLista");
        var bd = this.FindControl<Button>("BtnPantallaDiag");
        if (bl != null)
        {
            bl.Background = _pantalla == "lista" ? BgFila : Brushes.Transparent;
            bl.Foreground = _pantalla == "lista" ? Texto : TextoMuted;
            bl.BorderBrush = _pantalla == "lista" ? Verde : Brushes.Transparent;
            bl.FontWeight = _pantalla == "lista" ? FontWeight.SemiBold : FontWeight.Normal;
        }
        if (bd != null)
        {
            bd.Background = _pantalla == "diag" ? BgFila : Brushes.Transparent;
            bd.Foreground = _pantalla == "diag" ? Texto : TextoMuted;
            bd.BorderBrush = _pantalla == "diag" ? Verde : Brushes.Transparent;
            bd.FontWeight = _pantalla == "diag" ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private void SetTexto(string ctrl, string texto)
    {
        var tb = this.FindControl<TextBlock>(ctrl);
        if (tb != null) tb.Text = texto;
    }

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    private void OnAsistenteClick(object? sender, RoutedEventArgs e) => OnRequestAsistente?.Invoke();
}
