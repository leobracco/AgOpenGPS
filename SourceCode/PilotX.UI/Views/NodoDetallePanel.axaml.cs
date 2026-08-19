// NodoDetallePanel.axaml.cs
//
// Reemplazo nativo de pages/nodo-detalle.html + js/nodo-detalle.js.
//
// ESTA PANTALLA DISPARA EL OTA: lo que se toca acá termina flasheando firmware
// en un nodo real, en el lote. Por eso el port es LITERAL, sin "mejoras":
//
//   · MISMOS endpoints y verbos (GET  /api/nodos/{uid}/estado,
//     GET /api/nodos/{uid}/firmwares, POST /api/nodos/{uid}/ota { version },
//     GET /api/nodos/{uid}/ota/progress, POST /api/nodos/{uid}/cmd { cmd },
//     POST /api/nodos/asignacion-implemento { uid, asignado }).
//   · MISMO payload snake_case. El body del OTA lleva SOLO `version`, igual que
//     el JS: `allow_downgrade` existe en OtaBody pero la página nunca lo manda,
//     así que el guard anti-downgrade semver del server queda ACTIVO.
//   · MISMOS textos, mismos estados (cargando / vacío / error), mismo intervalo
//     de refresco (estado 2 s, progreso del OTA 1 s mientras dura).
//   · MISMO orden de confirmaciones: el OTA y los comandos peligrosos
//     (reiniciar, borrar_wifi, clear_safe_mode) piden confirmación ANTES de
//     salir a la red.
//
// ── Guards de seguridad del OTA: dónde vive cada uno ────────────────────────
// Ninguno de estos se reimplementa acá (duplicarlos sería inventar una segunda
// verdad); la pantalla los RESPETA y muestra su resultado:
//   1. Anti-downgrade semver  → FirmwareOtaCoordinator.IsDowngrade. La pantalla
//      no manda `allow_downgrade`, o sea que el guard queda prendido, y cuando
//      el server responde ok:false con "downgrade_bloqueado: nodo en X, pedido
//      Y" eso se muestra tal cual en el toast.
//   2. SHA-256 esperado       → lo pone el coordinator en el payload MQTT
//      (`sha256`) leyéndolo del cache; el firmware aborta si no coincide. Acá
//      se MUESTRA el hash de cada versión (corto + completo) para que el
//      operario pueda cotejarlo, no se recalcula.
//   3. Watchdog de 5 min      → el coordinator degrada a
//      "error / timeout_sin_resultado_5min" el OTA que quedó colgado en
//      sent/iniciando; la pantalla lo ve por el polling y pinta la barra en
//      rojo con ese detalle.
//   4. Dedup ring del relay   → anti-duplicado del reporte al cloud, server-side.
//   5. Flujo agp/{producto}/{UID}/ota/progress (0..100 | "ok" | "fail:<motivo>")
//      → lo consume el coordinator; la UI lee su snapshot por HTTP.
//   6. Un solo OTA por nodo   → si ya hay uno en curso, el botón avisa y no
//      manda nada (mismo guard del JS).
//
// Doctrina del repo respetada: los nodos SOLO se dan de alta solos, por
// `announcement` MQTT. Esta pantalla no agrega nodos ni deja escribir un UID a
// mano (la página tampoco).
//
// API: Abrir(uid) apunta la pantalla a un nodo; Attach(client) engancha el wire
// y arranca el polling; Detach() lo apaga. Detach NO cancela un OTA en curso —
// el flasheo sigue del lado del nodo, que es lo correcto: cerrar una pantalla
// no puede dejar un ESP32 a medio flashear.
//
// Y JUSTAMENTE POR ESO el polling del OTA se REARMA al volver a entrar: Detach
// apaga el loop de progreso pero el flasheo sigue, así que al reabrir el MISMO
// nodo hay que volver a mirarlo. Sin ese rearme el panel quedaba con un OTA
// "en curso" que ya nadie estaba mirando y el botón Aplicar contestaba para
// siempre "Ya hay un OTA en curso para este nodo" (la página se recuperaba
// sola porque cerrar y volver a entrar la recargaba entera).
//
// OJO con el idioma: Traductor.Aplicar(this) se llama en el ctor y de nuevo en
// IdiomaCambio, y CADA vez se repinta todo lo dinámico desde el último snapshot
// (Aplicar restaura el texto cacheado del XAML y blanquearía las pills, la
// barra de OTA y la matriz si no se re-renderizaran).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class NodoDetallePanel : UserControl, IPanelEmbebible
{
    // ---- wire / estado -----------------------------------------------------
    private NodoDetalleClient? _client;
    private string _uid = "";
    private CancellationTokenSource? _cts;        // polling de estado (2 s)
    private CancellationTokenSource? _otaCts;     // polling del OTA (1 s)

    private NodoEstadoWire? _ultimoEstado;        // último estado bueno
    private NodoFirmwaresWire? _ultimoFirmware;   // último catálogo bueno
    private NodoOtaWire? _ultimoOta;              // lo último que se pintó en la barra
    private string? _otaActivoUid;                // hay un OTA corriendo? UID destino
    private bool _pinOcupado;                     // el pin está esperando respuesta
    private bool _fwUnaColumna;                   // layout del listado (@media 720px)
    private double _ultimaEscala = -1;            // ancho pintado de la barra

    // Refresco del estado: 2 s, igual que el setInterval del original.
    private static readonly TimeSpan RefrescoEstado = TimeSpan.FromSeconds(2);
    // Progreso del OTA: 1 s mientras dura.
    private static readonly TimeSpan RefrescoOta = TimeSpan.FromSeconds(1);
    // Después de un OTA terminado el original espera 1,5 s y recarga.
    private static readonly TimeSpan EsperaPostOta = TimeSpan.FromMilliseconds(1500);

    // ---- paleta clara (tokens PilotXPanel* del theme) ----------------------
    private static readonly IBrush Superficie  = new SolidColorBrush(Color.Parse("#FFFFFF")); // PilotXPanelSurface
    private static readonly IBrush Superficie2 = new SolidColorBrush(Color.Parse("#EDF1EC")); // PilotXPanelSurface2
    private static readonly IBrush Fondo       = new SolidColorBrush(Color.Parse("#F5F7F4")); // PilotXPanelBg
    private static readonly IBrush Borde       = new SolidColorBrush(Color.Parse("#E2E7E2")); // PilotXPanelBorder
    private static readonly IBrush BordeAlto   = new SolidColorBrush(Color.Parse("#C5CFC5")); // PilotXPanelBorderHigh
    private static readonly IBrush Texto       = new SolidColorBrush(Color.Parse("#101612")); // PilotXPanelText
    private static readonly IBrush TextoTenue  = new SolidColorBrush(Color.Parse("#535E54")); // PilotXPanelTextDim
    private static readonly IBrush Verde       = new SolidColorBrush(Color.Parse("#4ABA3E")); // PilotXPanelAccent
    private static readonly IBrush VerdeTexto  = new SolidColorBrush(Color.Parse("#2F7A26")); // PilotXPanelAccentText / Ok
    private static readonly IBrush VerdeSuave  = new SolidColorBrush(Color.Parse("#E8F4E5")); // PilotXPanelOkSoft
    private static readonly IBrush Rojo        = new SolidColorBrush(Color.Parse("#C0261F")); // PilotXPanelErr
    private static readonly IBrush RojoSuave   = new SolidColorBrush(Color.Parse("#FBE6E6")); // PilotXPanelErrSoft

    // Badges sólidos del último reset anormal. Van RELLENOS con texto blanco,
    // así que el color tiene que aguantar el contraste al sol: se usan los
    // tokens oscuros de la paleta clara (PilotXPanelErr #C0261F → 5,94:1 y
    // PilotXPanelWarn #8A6100 → 5,5:1 contra blanco). El ámbar claro que había
    // antes (#DC8C1E) daba 2,69:1 — ilegible — y encima no existía en ninguna
    // de las dos paletas.
    private static readonly IBrush BadgeCrit = new SolidColorBrush(Color.Parse("#C0261F"));  // PilotXPanelErr
    private static readonly IBrush BadgeWarn = new SolidColorBrush(Color.Parse("#8A6100"));  // PilotXPanelWarn

    private static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    // Grilla de la matriz: la MISMA para la cabecera y para las 6 filas.
    private const string ColsMatriz = "84,120,160,150,200,*";
    private const double AnchoMatriz = 1010;

    // boot_reason → severidad (mismo set que /pages/nodos.html y NodosPanel).
    private static readonly HashSet<string> BootCrit = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "task_wdt", "int_wdt", "panic", "brownout", "wdt" };
    private static readonly HashSet<string> BootWarn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "sdio", "unknown" };

    // ---- callbacks al host -------------------------------------------------

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>"← Volver a Nodos": el host vuelve a la lista de nodos.</summary>
    public Action? OnRequestVolver { get; set; }

    // ---- diálogo interno ---------------------------------------------------
    private Action? _modalOnOk;

    // ---- toast -------------------------------------------------------------
    private DispatcherTimer? _toastTimer;

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    public NodoDetallePanel()
    {
        InitializeComponent();

        ArmarMatriz();
        LimpiarPantalla();

        // El listado de versiones pasa a UNA columna por debajo de 720 px de
        // ancho útil: el mismo breakpoint del @media de la página.
        var lista = this.FindControl<StackPanel>("FirmwaresList");
        if (lista != null) lista.SizeChanged += (_, e) => AplicarAnchoLista(e.NewSize.Width);

        // La barra de progreso se dibuja por ancho: al cambiar el tamaño del
        // riel hay que repintarla o queda con el ancho viejo.
        var riel = this.FindControl<Border>("OtaRiel");
        if (riel != null) riel.SizeChanged += (_, __) => RepintarBarra();

        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
        // Post al dispatcher: el cambio de idioma puede venir de otro hilo y
        // acá se toca el árbol visual.
        PilotX.Cockpit.Bars.Traductor.IdiomaCambio += () => Dispatcher.UIThread.Post(OnIdiomaCambio);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Estado INICIAL de la página: título "Cargando…", metadatos en
    /// "—", sin pills, sin fila resaltada en la matriz y el listado de
    /// firmwares en "Cargando…". Se usa al construir y al cambiar de nodo (la
    /// página equivalía a recargarse entera con otro ?uid=).</summary>
    private void LimpiarPantalla()
    {
        SetTexto("HdrTitle", T("Cargando…"));
        SetTexto("HdrSub", "—");
        OcultarBootBadge();

        SetTexto("MetaUid", "—");
        SetTexto("MetaTipo", "—");
        SetTexto("MetaAlias", "—");
        SetTexto("MetaEstado", "—");
        SetTexto("MetaIp", "—");
        SetTexto("MetaFw", "—");
        SetTexto("MetaLastSeen", "—");

        var pills = this.FindControl<WrapPanel>("HdrPills");
        if (pills != null) pills.Children.Clear();
        var pin = this.FindControl<Button>("BtnPinImplemento");
        if (pin != null) pin.IsVisible = false;

        RenderMatrix(null);

        var cmds = this.FindControl<WrapPanel>("CmdRow");
        if (cmds != null) cmds.Children.Clear();

        var lista = this.FindControl<StackPanel>("FirmwaresList");
        if (lista != null)
        {
            lista.Children.Clear();
            lista.Children.Add(Sutil(T("Cargando…")));
        }
        SetTexto("FirmwaresMeta", "");

        RenderOtaState(null);
    }

    /// <summary>Aplicar() restaura en cada control el texto que encontró la
    /// primera vez, así que después de traducir hay que REPINTAR todo lo
    /// dinámico desde el último snapshot; si no, las pills, la matriz, el
    /// listado de firmwares y la barra del OTA quedarían en blanco.</summary>
    private void OnIdiomaCambio()
    {
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
        ArmarMatriz();
        if (_ultimoEstado != null)
        {
            RenderIdentidad(_ultimoEstado);
            RenderPills(_ultimoEstado);
            RenderMatrix(_ultimoEstado.Matriz);
            RenderCmds(_ultimoEstado.ComandosDisponibles);
        }
        if (_ultimoFirmware != null) RenderFirmwares(_ultimoFirmware);
        RenderOtaState(_ultimoOta);
    }

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    /// <summary>El pin del implemento y las pills de estado van a la barra de
    /// contexto del shell; la fila de cabecera vieja queda oculta.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<Button>("BtnPinImplemento"),
                                        this.FindControl<WrapPanel>("HdrPills"));

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>
    /// Apunta la pantalla a un nodo. Es el equivalente del `?uid=` de la URL:
    /// el UID SIEMPRE viene de una fila de Nodos (que sale del announcement
    /// MQTT) — acá no se escribe a mano ni se da de alta nada.
    /// </summary>
    public void Abrir(string uid)
    {
        string nuevo = (uid ?? "").Trim();
        bool cambio = !string.Equals(nuevo, _uid, StringComparison.Ordinal);
        _uid = nuevo;

        if (cambio)
        {
            // Nodo distinto: se tira el estado viejo para no mezclar datos de
            // dos nodos en la misma pantalla y se reinicia el polling (el
            // arranque vuelve a pedir estado Y catálogo de firmwares).
            PararEstado();
            PararOta();
            _otaActivoUid = null;
            _ultimoEstado = null;
            _ultimoFirmware = null;
            _ultimoOta = null;
            OcultarError();
            LimpiarPantalla();
        }

        if (string.IsNullOrEmpty(_uid))
        {
            // Mismo camino sin salida que el original cuando falta el ?uid=
            // (acá no hay URL: el UID lo trae el host al abrir la pantalla).
            PararEstado();
            SetTexto("HdrTitle", T("No se sabe qué nodo mostrar"));
            SetTexto("HdrSub", T("Volvé a Nodos y entrá tocando una fila."));
            OcultarBootBadge();
            return;
        }

        ArrancarEstado();
    }

    public void Attach(NodoDetalleClient client)
    {
        _client = client;
        if (!string.IsNullOrEmpty(_uid)) ArrancarEstado();
    }

    public void Detach()
    {
        // OJO: esto NO cancela el OTA. El flasheo lo maneja el nodo y el
        // coordinator del server; cerrar la pantalla solo apaga el polling.
        // `_otaActivoUid` queda a propósito: dice que hay un flasheo vivo, y al
        // volver a entrar ArrancarEstado() → RearmarOta() retoma el progreso.
        PararEstado();
        PararOta();
        CerrarModal();
        OcultarToast();
    }

    private void ArrancarEstado()
    {
        if (_client == null || string.IsNullOrEmpty(_uid)) return;
        // Antes del guard del polling de estado: se puede volver con el estado
        // ya corriendo (Attach + Abrir) y el OTA sin loop.
        RearmarOta();
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = LoopEstadoAsync(_cts.Token);
    }

    /// <summary>
    /// Vuelve a mirar un OTA que quedó en curso. Es el caso de "disparé el
    /// flasheo, cerré el panel y volví a entrar al mismo nodo": Detach apagó el
    /// polling pero el nodo sigue flasheando, así que hay que retomar el
    /// progreso. Sin esto la pantalla quedaba trabada (`_otaActivoUid` no se
    /// limpia nunca y el botón rechaza todo).
    /// </summary>
    private void RearmarOta()
    {
        if (_otaActivoUid == null) return;
        // El OTA es de ESTE nodo? Si quedó uno de otro UID, no es asunto de
        // esta pantalla (Abrir() con otro uid ya lo había limpiado).
        if (!string.Equals(_otaActivoUid, _uid, StringComparison.Ordinal)) return;
        if (_otaCts != null) return;                 // ya se está mirando
        _otaCts = new CancellationTokenSource();
        _ = LoopOtaAsync(_otaCts.Token);
    }

    private void PararEstado()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private void PararOta()
    {
        try { _otaCts?.Cancel(); } catch { }
        _otaCts = null;
    }

    /// <summary>cargarEstado() + cargarFirmwares() de arranque y después
    /// cargarEstado() cada 2 s. El `if (document.hidden) return;` del original
    /// se traduce a "el panel no está a la vista".</summary>
    private async Task LoopEstadoAsync(CancellationToken ct)
    {
        await CargarEstadoAsync(ct).ConfigureAwait(true);
        await CargarFirmwaresAsync(ct).ConfigureAwait(true);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(RefrescoEstado, ct).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            if (!IsEffectivelyVisible) continue;
            await CargarEstadoAsync(ct).ConfigureAwait(true);
        }
    }

    // =========================================================================
    //  lecturas
    // =========================================================================

    private async Task CargarEstadoAsync(CancellationToken ct)
    {
        if (_client == null || string.IsNullOrEmpty(_uid)) return;
        var r = await _client.GetEstadoAsync(_uid, ct).ConfigureAwait(true);
        if (r.Cancelado || ct.IsCancellationRequested) return;

        if (r.ErrorRed != null)
        {
            // `catch (e) { toast('Error al leer estado: ' + e.message) }`, con el
            // código AGP adelante para que se pueda dictar por teléfono.
            MostrarErrorAgp(r);
            Toast(T("Error al leer estado") + ": " + Cabeza(r), "err");
            return;
        }

        var d = r.Datos;
        if (d == null || !d.Ok)
        {
            Toast(T("No se pudo leer el estado del nodo"), "err");
            return;
        }

        OcultarError();
        _ultimoEstado = d;
        RenderIdentidad(d);
        RenderPills(d);
        RenderMatrix(d.Matriz);
        RenderCmds(d.ComandosDisponibles);
        if (d.Ota != null) RenderOtaState(d.Ota);
    }

    private async Task CargarFirmwaresAsync(CancellationToken ct)
    {
        if (_client == null || string.IsNullOrEmpty(_uid)) return;
        var r = await _client.GetFirmwaresAsync(_uid, ct).ConfigureAwait(true);
        if (r.Cancelado || ct.IsCancellationRequested) return;

        var lista = this.FindControl<StackPanel>("FirmwaresList");
        if (r.ErrorRed != null)
        {
            MostrarErrorAgp(r);
            if (lista != null)
            {
                lista.Children.Clear();
                lista.Children.Add(Sutil(T("Error") + ": " + Cabeza(r)));
            }
            return;
        }

        var d = r.Datos;
        if (d == null || !d.Ok)
        {
            if (lista != null)
            {
                lista.Children.Clear();
                lista.Children.Add(Sutil(T("No se pudo leer el catálogo local.")));
            }
            return;
        }

        _ultimoFirmware = d;
        RenderFirmwares(d);
    }

    /// <summary>pollProgress(): 1 s mientras hay un OTA en curso. Errores en
    /// silencio, igual que el original (el nodo se reinicia en medio del flasheo
    /// y el Hub puede no contestar un par de ticks).</summary>
    private async Task LoopOtaAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(RefrescoOta, ct).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            if (_client == null || string.IsNullOrEmpty(_uid) || _otaActivoUid == null) return;

            var r = await _client.GetOtaProgressAsync(_uid, ct).ConfigureAwait(true);
            if (r.Cancelado || ct.IsCancellationRequested) return;
            var ota = r.Datos != null && r.Datos.Ok ? r.Datos.Ota : null;
            if (ota == null) continue;

            RenderOtaState(ota);
            string st = (ota.Status ?? "").ToLowerInvariant();
            if (st != "ok" && st != "error") continue;

            // OTA terminó.
            PararOta();
            _otaActivoUid = null;
            if (st == "ok")
                Toast(T("OTA completado") + ": v" + (ota.Version ?? ""), "ok");
            else
                Toast(T("OTA falló") + ": " + (string.IsNullOrEmpty(ota.Detalle) ? T("sin detalle") : ota.Detalle!), "err");

            // Refrescar firmware actual + estado (setTimeout de 1,5 s).
            var refresco = _cts?.Token ?? CancellationToken.None;
            _ = RecargarPostOtaAsync(refresco);
            return;
        }
    }

    private async Task RecargarPostOtaAsync(CancellationToken ct)
    {
        try { await Task.Delay(EsperaPostOta, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        await CargarEstadoAsync(ct).ConfigureAwait(true);
        await CargarFirmwaresAsync(ct).ConfigureAwait(true);
    }

    // =========================================================================
    //  render: identidad + pills
    // =========================================================================

    private void RenderIdentidad(NodoEstadoWire e)
    {
        // Alias / UID son datos del operario: NO se traducen jamás.
        string titulo = !string.IsNullOrEmpty(e.Alias) ? e.Alias!
                      : !string.IsNullOrEmpty(e.Uid) ? e.Uid!
                      : T("Nodo");
        SetTexto("HdrTitle", titulo);
        RenderBootBadge(e.BootReason);

        string sub = string.IsNullOrEmpty(e.Tipo) ? "—" : e.Tipo!;
        if (!string.IsNullOrEmpty(e.Alias) && !string.IsNullOrEmpty(e.Uid)) sub += " · " + e.Uid;
        SetTexto("HdrSub", sub);

        RenderPinImplemento(e);

        SetTexto("MetaUid",      string.IsNullOrEmpty(e.Uid) ? "—" : e.Uid!);
        SetTexto("MetaTipo",     string.IsNullOrEmpty(e.Tipo) ? "—" : e.Tipo!);
        SetTexto("MetaAlias",    string.IsNullOrEmpty(e.Alias) ? "—" : e.Alias!);
        SetTexto("MetaEstado",   string.IsNullOrEmpty(e.EstadoCurado) ? "—" : T(e.EstadoCurado!));
        SetTexto("MetaIp",       string.IsNullOrEmpty(e.Ip) ? "—" : e.Ip!);
        SetTexto("MetaFw",       string.IsNullOrEmpty(e.Firmware) ? "—" : e.Firmware!);
        SetTexto("MetaLastSeen", RelTime(e.LastSeenUtc));
    }

    /// <summary>Badge del último reset: rojo si fue anormal (watchdog, panic,
    /// brownout), ámbar en los dudosos, nada en los normales.</summary>
    private void RenderBootBadge(string? reason)
    {
        var caja = this.FindControl<Border>("HdrBootBadge");
        var txt = this.FindControl<TextBlock>("HdrBootBadgeText");
        if (caja == null || txt == null) return;

        if (string.IsNullOrWhiteSpace(reason)) { caja.IsVisible = false; return; }
        string key = reason!.Trim().ToLowerInvariant();

        if (BootCrit.Contains(key))
        {
            caja.IsVisible = true;
            caja.Background = BadgeCrit;
            txt.Text = "⚠ " + key;
            ToolTip.SetTip(caja, T("Último reset") + ": " + key + " (" + T("anormal — revisar") + ")");
            return;
        }
        if (BootWarn.Contains(key))
        {
            caja.IsVisible = true;
            caja.Background = BadgeWarn;
            txt.Text = key;
            ToolTip.SetTip(caja, T("Último reset") + ": " + key);
            return;
        }
        caja.IsVisible = false;   // poweron / sw_reset / ext / deepsleep → silencio
    }

    private void OcultarBootBadge()
    {
        var caja = this.FindControl<Border>("HdrBootBadge");
        if (caja != null) caja.IsVisible = false;
    }

    private void RenderPills(NodoEstadoWire e)
    {
        var host = this.FindControl<WrapPanel>("HdrPills");
        if (host == null) return;
        host.Children.Clear();

        // online / offline
        host.Children.Add(Pill(e.Online ? T("online") : T("offline"), e.Online ? "ok" : "err"));

        // broker MQTT
        host.Children.Add(e.BrokerConnected
            ? Pill(T("broker MQTT"), "ok")
            : Pill(T("broker MQTT off"), "err"));

        // firmware
        if (!string.IsNullOrEmpty(e.Firmware))
            host.Children.Add(Pill(T("fw") + " " + e.Firmware, "muted"));

        // safe-mode: el nodo solo acepta ping + clear_safe_mode.
        if (e.SafeMode)
        {
            string cc = e.CrashCount.HasValue
                ? " · " + e.CrashCount.Value.ToString(CultureInfo.InvariantCulture) + " " + T("crashes")
                : "";
            var p = Pill(T("safe-mode") + cc, "err");
            ToolTip.SetTip(p, T("El nodo crasheó ≥3 veces seguidas. OTA y cmds peligrosos rechazados. Resolvé la causa y usá \"Resetear safe-mode\"."));
            host.Children.Add(p);
        }

        // sync desired/reported
        if (e.ConfigSync != null && !string.IsNullOrEmpty(e.ConfigSync.Status))
        {
            string cs = e.ConfigSync.Status!.ToLowerInvariant();
            string label, cls, title;
            switch (cs)
            {
                case "in_sync":
                    label = T("config OK"); cls = "ok";
                    title = T("Firmware reportó la última config que la PC le pidió.");
                    break;
                case "pending":
                    label = T("config pendiente"); cls = "muted";
                    title = T("La PC publicó una config nueva y el firmware todavía no respondió (< 10 s).");
                    break;
                case "drift":
                    label = T("config DRIFT"); cls = "err";
                    title = T("La PC publicó una config nueva pero el firmware nunca echó back. Verificá conectividad o que el firmware soporte /config/reported.");
                    break;
                case "no_report":
                    label = T("config no_report"); cls = "err";
                    title = T("PC publicó pero el firmware no responde en /config/reported. Puede ser firmware legacy (no soporta echo-back) o nodo desconectado.");
                    break;
                default:
                    label = T("config") + " " + cs; cls = "muted"; title = "";
                    break;
            }
            var p = Pill(label, cls);
            if (title.Length > 0) ToolTip.SetTip(p, title);
            host.Children.Add(p);
        }
    }

    /// <summary>.pill del original: punto + texto, redondeada. ok / err / muted.</summary>
    private static Border Pill(string texto, string clase)
    {
        IBrush fondo, borde, tinta, punto;
        switch (clase)
        {
            case "ok":
                fondo = VerdeSuave; borde = VerdeTexto; tinta = VerdeTexto; punto = VerdeTexto; break;
            case "err":
                fondo = RojoSuave; borde = Rojo; tinta = Rojo; punto = Rojo; break;
            default:
                fondo = Superficie2; borde = BordeAlto; tinta = TextoTenue; punto = TextoTenue; break;
        }

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        fila.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = punto,
            VerticalAlignment = VerticalAlignment.Center
        });
        fila.Children.Add(new TextBlock
        {
            Text = texto,
            Foreground = tinta,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center
        });

        return new Border
        {
            Background = fondo,
            BorderBrush = borde,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(12, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = fila
        };
    }

    /// <summary>El pin solo tiene sentido para nodos aceptados / offline (no
    /// pendientes ni ignorados), igual que en la página.</summary>
    private void RenderPinImplemento(NodoEstadoWire e)
    {
        var btn = this.FindControl<Button>("BtnPinImplemento");
        if (btn == null) return;

        string est = e.EstadoCurado ?? "";
        bool puedeAsignar = est == "aceptado" || est == "offline";
        if (!puedeAsignar) { btn.IsVisible = false; return; }

        btn.IsVisible = true;
        bool asignado = e.DelImplementoActivo;
        btn.Tag = asignado ? "1" : "0";
        // Sin emoji a propósito: la pantalla de cabina no siempre tiene la
        // fuente con los pictogramas y salen cuadraditos. Mismos textos que la
        // fila de Nodos, que es de donde se entra acá.
        btn.Content = asignado ? T("En implemento") : T("Asignar");
        btn.Background = asignado ? VerdeSuave : Superficie2;
        btn.BorderBrush = asignado ? Verde : BordeAlto;
        btn.Foreground = asignado ? Texto : TextoTenue;
        ToolTip.SetTip(btn, asignado
            ? T("Quitar del implemento activo (no disparará alarma offline)")
            : T("Asignar al implemento activo (alarma offline cuando caiga)"));
    }

    // =========================================================================
    //  render: matriz diagnóstica
    // =========================================================================

    /// <summary>Una fila de la matriz documental. Los glifos y los textos son
    /// LITERALES de la página: la matriz no se calcula acá, el server manda cuál
    /// es la fila activa (`matriz.row`).</summary>
    private sealed class FilaMatriz
    {
        public string Key = "";
        public string[] Glifos = Array.Empty<string>();   // "ok" | "no" | "na"
        public string[] Sufijos = Array.Empty<string>();  // texto al lado del glifo
        public string EstadoReal = "";
        public string Consejo = "";
    }

    private static readonly FilaMatriz[] Filas =
    {
        new FilaMatriz {
            Key = "broker-down",
            Glifos = new[] { "na", "no", "na", "na" },
            Sufijos = new[] { "", "broker off", "", "" },
            EstadoReal = "Broker MQTT caído en la PC",
            Consejo = "Verificar CoreX en la PC. Reconectar desde Nodos → Diagnóstico MQTT."
        },
        new FilaMatriz {
            Key = "wifi-off",
            Glifos = new[] { "no", "na", "na", "na" },
            Sufijos = new[] { "off", "", "", "" },
            EstadoReal = "Apagado / fuera de LAN",
            Consejo = "El nodo no responde. Anuncio retenido viejo. Chequear alimentación, antena, o si quedó en AP fallback (SSID propio visible desde celu)."
        },
        new FilaMatriz {
            Key = "wifi-ok-mqtt-down",
            Glifos = new[] { "ok", "no", "na", "na" },
            Sufijos = new[] { "", "", "", "" },
            EstadoReal = "WiFi sí, broker no",
            Consejo = "Verificar IP del broker en el nodo (portal web del nodo → broker). Si está bien, reiniciar nodo."
        },
        new FilaMatriz {
            Key = "rx-ok-target-no",
            Glifos = new[] { "ok", "ok", "no", "ok" },
            Sufijos = new[] { "", "", "", "" },
            EstadoReal = "Nodo vivo, bridge dormido",
            Consejo = "El nodo está sano. PilotX no está mandando target (motor desactivado, sección sin asignar, o bridge sin Start). Revisar la página del producto."
        },
        new FilaMatriz {
            Key = "rx-no-target-ok",
            Glifos = new[] { "ok", "ok", "ok", "no" },
            Sufijos = new[] { "", "", "", "" },
            EstadoReal = "Recibe, no responde",
            Consejo = "Status > 3 s atrás. Watchdog HW, colisión de ClientID, o nodo trabado. Reiniciar el nodo."
        },
        new FilaMatriz {
            Key = "ok-pleno",
            Glifos = new[] { "ok", "ok", "ok", "ok" },
            Sufijos = new[] { "", "", "", "" },
            EstadoReal = "OK pleno",
            Consejo = "El nodo está respondiendo y recibiendo órdenes. Telemetría se actualiza ~10 Hz."
        },
    };

    // key de la fila → (marco, textos que se ponen en negrita cuando está activa)
    private readonly Dictionary<string, (Border Marco, List<TextBlock> Textos)> _filasMatriz = new();

    private void ArmarMatriz()
    {
        var host = this.FindControl<StackPanel>("MatrizHost");
        if (host == null) return;
        host.Children.Clear();
        _filasMatriz.Clear();

        // Cabecera
        var cab = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(ColsMatriz),
            Background = Fondo,
            MinWidth = AnchoMatriz
        };
        string[] titulos = { "WiFi", "MQTT", "Target llega al nodo", "Status llega a PC", "Estado real", "Cómo se ve / qué hacer" };
        for (int i = 0; i < titulos.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = T(titulos[i]),
                Foreground = TextoTenue,
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(i == 0 ? 12 : 8, 10, 8, 10)
            };
            Grid.SetColumn(tb, i);
            cab.Children.Add(tb);
        }
        host.Children.Add(cab);
        host.Children.Add(new Border { Height = 1, Background = Borde, MinWidth = AnchoMatriz });

        // Filas
        foreach (var f in Filas)
        {
            var textos = new List<TextBlock>();
            var g = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(ColsMatriz),
                MinWidth = AnchoMatriz
            };

            for (int i = 0; i < 4; i++)
            {
                var celda = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(i == 0 ? 12 : 8, 10, 8, 10)
                };
                celda.Children.Add(new TextBlock
                {
                    Text = Glifo(f.Glifos[i]),
                    Foreground = GlifoColor(f.Glifos[i]),
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Width = 18,
                    TextAlignment = TextAlignment.Center
                });
                if (!string.IsNullOrEmpty(f.Sufijos[i]))
                {
                    var suf = new TextBlock
                    {
                        Text = T(f.Sufijos[i]),
                        Foreground = TextoTenue,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    textos.Add(suf);
                    celda.Children.Add(suf);
                }
                Grid.SetColumn(celda, i);
                g.Children.Add(celda);
            }

            // "Estado real" va en negrita SIEMPRE (era <strong> en la página).
            var estado = new TextBlock
            {
                Text = T(f.EstadoReal),
                Foreground = Texto,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 10, 8, 10)
            };
            Grid.SetColumn(estado, 4);
            g.Children.Add(estado);

            var consejo = new TextBlock
            {
                Text = T(f.Consejo),
                Foreground = TextoTenue,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 10, 12, 10)
            };
            textos.Add(consejo);
            Grid.SetColumn(consejo, 5);
            g.Children.Add(consejo);

            var marco = new Border
            {
                Child = g,
                Background = Superficie,
                BorderBrush = Borde,
                BorderThickness = new Thickness(0, 0, 0, 1),
                MinWidth = AnchoMatriz
            };
            host.Children.Add(marco);
            _filasMatriz[f.Key] = (marco, textos);
        }
    }

    private static string Glifo(string clase) => clase switch
    {
        "ok" => "✓",
        "no" => "✗",
        _ => "—"
    };

    private static IBrush GlifoColor(string clase) => clase switch
    {
        "ok" => VerdeTexto,
        "no" => Rojo,
        _ => TextoTenue
    };

    /// <summary>Resalta la fila donde está parado el nodo. La decide el SERVER
    /// (ResolveMatrixRow); acá no se recalcula nada.</summary>
    private void RenderMatrix(NodoMatrizWire? matriz)
    {
        foreach (var kv in _filasMatriz)
        {
            kv.Value.Marco.Background = Superficie;
            kv.Value.Marco.BorderBrush = Borde;
            kv.Value.Marco.BorderThickness = new Thickness(0, 0, 0, 1);
            foreach (var tb in kv.Value.Textos) tb.FontWeight = FontWeight.Normal;
        }
        string? row = matriz?.Row;
        if (string.IsNullOrEmpty(row)) return;
        if (!_filasMatriz.TryGetValue(row!, out var hit)) return;

        hit.Marco.Background = VerdeSuave;
        // El inset-shadow de 4 px del CSS: acá es el borde izquierdo en acento.
        hit.Marco.BorderBrush = Verde;
        hit.Marco.BorderThickness = new Thickness(4, 0, 0, 1);
        foreach (var tb in hit.Textos) tb.FontWeight = FontWeight.SemiBold;
    }

    // =========================================================================
    //  render: comandos disponibles
    // =========================================================================

    private static string LabelCmd(string cmd) => cmd switch
    {
        "reiniciar" => "Reiniciar nodo",
        "borrar_wifi" => "Borrar WiFi guardado",
        "estado" => "Pedir estado",
        "ping" => "Ping",
        "clear_safe_mode" => "Resetear safe-mode",
        _ => cmd
    };

    private static bool RequiereConfirm(string cmd)
        => cmd == "reiniciar" || cmd == "borrar_wifi" || cmd == "clear_safe_mode";

    private static string ConfirmMsg(string cmd)
    {
        if (cmd == "reiniciar")
            return T("Reiniciar el nodo ahora?") + "\n\n" +
                   T("Quedará offline ~5–10 s mientras vuelve a bootear.");
        if (cmd == "borrar_wifi")
            return T("Borrar credenciales WiFi del nodo?") + "\n\n" +
                   T("El nodo va a quedar en modo AP (SSID propio) hasta que lo reconfigures con el celular.");
        if (cmd == "clear_safe_mode")
            return T("Resetear safe-mode?") + "\n\n" +
                   T("El nodo borra su contador de crashes en NVS y vuelve a aceptar OTA/calibración. Si la causa de los crashes sigue, va a volver a entrar en safe-mode tras 3 reboots.");
        return T("Continuar?");
    }

    /// <summary>
    /// Los botones salen TAL CUAL de `comandos_disponibles`. El server incluye
    /// "ota" en esa lista y el botón queda etiquetado "ota" (labelCmd no lo
    /// traduce) — al tocarlo, POST /cmd responde "cmd-no-soportado" porque el
    /// OTA tiene endpoint propio. Es así en la página de hoy: se replica sin
    /// arreglar (arreglarlo cambiaría el comportamiento de una pantalla que
    /// flashea hardware; queda anotado en el reporte).
    /// </summary>
    private void RenderCmds(List<string>? comandos)
    {
        var host = this.FindControl<WrapPanel>("CmdRow");
        if (host == null) return;
        host.Children.Clear();
        if (comandos == null) return;

        foreach (var cmd in comandos)
        {
            if (string.IsNullOrEmpty(cmd)) continue;
            string c = cmd;
            bool primario = c == "clear_safe_mode";
            var b = new Button
            {
                Content = T(LabelCmd(c)),
                MinWidth = 140,
                MinHeight = 48,
                Padding = new Thickness(16, 0, 16, 0),
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(8),
                Background = primario ? Verde : Superficie,
                Foreground = c == "borrar_wifi" ? Rojo : Texto,
                BorderBrush = primario ? Verde : BordeAlto,
                BorderThickness = new Thickness(1),
                FontSize = 14,
                FontWeight = primario ? FontWeight.SemiBold : FontWeight.Normal,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            b.Click += (_, __) => DispararCmd(c);
            host.Children.Add(b);
        }
    }

    // =========================================================================
    //  render: firmwares disponibles
    // =========================================================================

    private void RenderFirmwares(NodoFirmwaresWire fw)
    {
        var host = this.FindControl<StackPanel>("FirmwaresList");
        if (host == null) return;
        host.Children.Clear();

        var lista = fw.Versiones ?? new List<NodoFirmwareVersionWire>();
        string actual = (fw.FirmwareActual ?? "").Trim();

        if (lista.Count == 0)
        {
            host.Children.Add(Sutil(T("No hay versiones en cache local para este producto. Subí o sincronizá un firmware desde OrbitX cloud y reintentá.")));
        }
        else
        {
            foreach (var v in lista) host.Children.Add(FilaFirmware(v, actual));
        }

        // "Servidor LAN de firmwares: <ip>:<puerto>" (8088 por defecto).
        string srv = (string.IsNullOrEmpty(fw.LanIp) ? "?" : fw.LanIp!) + ":" +
                     (fw.HttpPort > 0 ? fw.HttpPort : 8088).ToString(CultureInfo.InvariantCulture);
        SetTexto("FirmwaresMeta", T("Servidor LAN de firmwares") + ": " + srv);
    }

    private Control FilaFirmware(NodoFirmwareVersionWire v, string actual)
    {
        string version = v.Version ?? "";
        string changelog = v.Changelog ?? "";
        string sha = v.HashSha256 ?? "";
        bool esActual = v.EsActual ||
                        (actual.Length > 0 && string.Equals(actual, version, StringComparison.Ordinal));

        var pila = new StackPanel { Spacing = 0, Margin = new Thickness(0, 8, 0, 8) };

        var g = new Grid
        {
            ColumnDefinitions = _fwUnaColumna
                ? new ColumnDefinitions("*")
                : new ColumnDefinitions("2*,*,*,Auto")
        };
        if (_fwUnaColumna) g.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto");

        // --- versión + badge "actual" + changelog ---
        var celdaV = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        var filaV = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        filaV.Children.Add(new TextBlock
        {
            Text = "v" + version,
            Foreground = Texto,
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (esActual)
        {
            filaV.Children.Add(new Border
            {
                Background = VerdeSuave,
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 1, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = T("actual"),
                    Foreground = VerdeTexto,
                    FontSize = 11,
                    FontWeight = FontWeight.Medium
                }
            });
        }
        celdaV.Children.Add(filaV);
        if (changelog.Length > 0)
        {
            celdaV.Children.Add(new Border
            {
                Background = Superficie2,
                BorderBrush = Borde,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 4, 8, 0),
                MaxHeight = 80,
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new TextBlock
                    {
                        Text = changelog,
                        Foreground = TextoTenue,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            });
        }
        Colocar(g, celdaV, 0);

        // --- tamaño ---
        // `Math.round(bytes/1024) + ' KB'`. Math.round de JS es half-up, que NO
        // es Math.Round de .NET (banker's): va Floor(x + 0,5).
        string tam = "—";
        if (v.TamanoBytes.HasValue && v.TamanoBytes.Value != 0)
        {
            long kb = (long)Math.Floor(v.TamanoBytes.Value / 1024.0 + 0.5);
            tam = kb.ToString(CultureInfo.InvariantCulture) + " KB";
        }
        Colocar(g, new TextBlock
        {
            Text = tam,
            Foreground = TextoTenue,
            FontSize = 11,
            FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(_fwUnaColumna ? 0 : 8, _fwUnaColumna ? 4 : 0, 8, 0)
        }, 1);

        // --- SHA-256 recortado (el completo va abajo, en su propia línea) ---
        string shaCorto = sha.Length > 0
            ? sha.Substring(0, Math.Min(12, sha.Length)) + "…"
            : "—";
        var celdaSha = new TextBlock
        {
            Text = shaCorto,
            Foreground = TextoTenue,
            FontSize = 11,
            FontFamily = Mono,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(_fwUnaColumna ? 0 : 8, _fwUnaColumna ? 2 : 0, 8, 0)
        };
        if (sha.Length > 0) ToolTip.SetTip(celdaSha, sha);
        Colocar(g, celdaSha, 2);

        // --- acción ---
        var btn = new Button
        {
            Content = esActual ? T("Ya instalada") : T("Aplicar") + " v" + version,
            IsEnabled = !esActual,
            MinHeight = 48,
            MinWidth = 140,
            Padding = new Thickness(16, 0, 16, 0),
            Margin = new Thickness(_fwUnaColumna ? 0 : 8, _fwUnaColumna ? 8 : 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = esActual ? Superficie : Verde,
            Foreground = Texto,
            BorderBrush = esActual ? BordeAlto : Verde,
            BorderThickness = new Thickness(1),
            FontSize = 14,
            FontWeight = esActual ? FontWeight.Normal : FontWeight.SemiBold,
            HorizontalAlignment = _fwUnaColumna ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (!esActual)
        {
            string ver = version;
            btn.Click += (_, __) => DispararOta(ver);
        }
        Colocar(g, btn, 3);

        pila.Children.Add(g);

        // El SHA-256 COMPLETO, siempre visible: en cabina, con guante, no hay
        // hover — un hash que solo vive en el tooltip no se puede cotejar.
        if (sha.Length > 0)
        {
            pila.Children.Add(new TextBlock
            {
                Text = "sha256 " + sha,
                Foreground = TextoTenue,
                FontSize = 11,
                FontFamily = Mono,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        return new Border
        {
            BorderBrush = Borde,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = pila
        };
    }

    private void Colocar(Grid g, Control c, int indice)
    {
        if (_fwUnaColumna) { Grid.SetColumn(c, 0); Grid.SetRow(c, indice); }
        else { Grid.SetColumn(c, indice); Grid.SetRow(c, 0); }
        g.Children.Add(c);
    }

    /// <summary>Mismo corte que el @media (min-width:720px) de la página: por
    /// debajo de 720 px el renglón de cada versión se apila.</summary>
    private void AplicarAnchoLista(double ancho)
    {
        bool una = ancho > 0 && ancho < 720;
        if (una == _fwUnaColumna) return;
        _fwUnaColumna = una;
        if (_ultimoFirmware != null) RenderFirmwares(_ultimoFirmware);
    }

    // =========================================================================
    //  render: estado del OTA
    // =========================================================================

    private void RenderOtaState(NodoOtaWire? ota)
    {
        _ultimoOta = ota;

        var barra = this.FindControl<Border>("OtaBarra");
        string st = (ota?.Status ?? "").ToLowerInvariant();

        if (ota == null || string.IsNullOrEmpty(ota.Status) || st == "idle")
        {
            SetTexto("OtaStatus", T("sin actualización en curso"));
            SetTexto("OtaPct", "—");
            _ultimaEscala = -1;
            if (barra != null) { barra.Width = 0; barra.Background = Verde; }
            SetTexto("OtaDetalle", "");
            return;
        }

        double pct = ota.ProgressPct;
        if (double.IsNaN(pct)) pct = 0;
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;

        string label = ota.Status!;
        if (st == "sent") label = T("Comando enviado…");
        else if (st == "iniciando") label = T("Descargando firmware…");
        else if (st == "ok") label = T("Actualizado correctamente");
        else if (st == "error") label = T("Error");

        string version = ota.Version ?? "";
        SetTexto("OtaStatus", label + (version.Length > 0 ? " · v" + version : ""));
        SetTexto("OtaPct", FmtNum(pct) + "%");
        if (barra != null) barra.Background = st == "error" ? Rojo : Verde;
        PintarBarra(pct);
        SetTexto("OtaDetalle", ota.Detalle ?? "");
    }

    private void PintarBarra(double pct)
    {
        var barra = this.FindControl<Border>("OtaBarra");
        var riel = this.FindControl<Border>("OtaRiel");
        if (barra == null || riel == null) return;
        double escala = pct / 100.0;
        _ultimaEscala = escala;
        double ancho = riel.Bounds.Width;
        if (ancho <= 0) ancho = 320;   // fallback antes del primer layout
        barra.Width = ancho * escala;
    }

    private void RepintarBarra()
    {
        if (_ultimaEscala < 0) return;
        PintarBarra(_ultimaEscala * 100.0);
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    /// <summary>
    /// Dispara el OTA. Orden EXACTO del original:
    ///   1. hay uid y versión,
    ///   2. no hay otro OTA en curso para este nodo,
    ///   3. CONFIRMACIÓN del operario ("El nodo se va a reiniciar"),
    ///   4. recién ahí sale el POST con { version } — sin allow_downgrade, o sea
    ///      con el guard anti-downgrade del server activo.
    /// </summary>
    private void DispararOta(string version)
    {
        if (string.IsNullOrEmpty(_uid) || string.IsNullOrEmpty(version)) return;
        if (_otaActivoUid != null)
        {
            Toast(T("Ya hay un OTA en curso para este nodo"), "err");
            return;
        }
        AbrirConfirm(
            T("Actualizar firmware"),
            T("Aplicar firmware v") + version + T(" al nodo?") + "\n\n" + T("El nodo se va a reiniciar."),
            () => _ = EnviarOtaAsync(version));
    }

    private async Task EnviarOtaAsync(string version)
    {
        if (_client == null || string.IsNullOrEmpty(_uid)) return;
        // El disparo del OTA NO cuelga del token del polling: si el operario
        // cierra el panel justo después de confirmar, el comando ya salió.
        var r = await _client.EnviarOtaAsync(_uid, version, CancellationToken.None).ConfigureAwait(true);

        if (r.ErrorRed != null)
        {
            MostrarErrorAgp(r);
            Toast(T("Error") + ": " + Cabeza(r), "err");
            return;
        }
        var d = r.Datos;
        if (d == null || !d.Ok)
        {
            string motivo = d != null && !string.IsNullOrEmpty(d.Error) ? d.Error! : T("fallo");
            // Acá cae, entre otros, el guard anti-downgrade del server:
            // "downgrade_bloqueado: nodo en 1.14.0, pedido 1.9.0".
            Toast(T("No se pudo enviar OTA") + ": " + motivo, "err");
            return;
        }

        OcultarError();
        Toast(T("OTA enviado al nodo"), "ok");
        _otaActivoUid = _uid;
        RenderOtaState(new NodoOtaWire
        {
            Status = "sent",
            ProgressPct = 5,
            Version = version,
            Detalle = T("Comando enviado al nodo")
        });

        PararOta();
        RearmarOta();
    }

    private void DispararCmd(string cmd)
    {
        if (string.IsNullOrEmpty(_uid) || string.IsNullOrEmpty(cmd)) return;
        if (RequiereConfirm(cmd))
        {
            AbrirConfirm(T("Confirmar comando"), ConfirmMsg(cmd), () => _ = EnviarCmdAsync(cmd));
            return;
        }
        _ = EnviarCmdAsync(cmd);
    }

    private async Task EnviarCmdAsync(string cmd)
    {
        if (_client == null || string.IsNullOrEmpty(_uid)) return;
        var r = await _client.EnviarCmdAsync(_uid, cmd, CancellationToken.None).ConfigureAwait(true);

        if (r.ErrorRed != null)
        {
            MostrarErrorAgp(r);
            Toast(T("Error") + ": " + Cabeza(r), "err");
            return;
        }
        var d = r.Datos;
        if (d == null || !d.Ok)
        {
            string motivo = d != null && !string.IsNullOrEmpty(d.Error) ? d.Error! : T("fallo");
            Toast(T("Comando rechazado") + ": " + motivo, "err");
            return;
        }
        OcultarError();
        Toast(T("Comando enviado") + ": " + T(LabelCmd(cmd)), "ok");
    }

    private async void OnPinImplementoClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null || string.IsNullOrEmpty(_uid) || _pinOcupado) return;
        var btn = this.FindControl<Button>("BtnPinImplemento");
        bool ya = btn != null && (btn.Tag as string) == "1";
        bool nuevo = !ya;

        _pinOcupado = true;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            var r = await _client.AsignarImplementoAsync(_uid, nuevo, CancellationToken.None).ConfigureAwait(true);
            if (r.ErrorRed != null)
            {
                MostrarErrorAgp(r);
                Toast(T("Error") + ": " + Cabeza(r), "err");
                return;
            }
            var d = r.Datos;
            if (d != null && d.Ok)
            {
                Toast(nuevo ? T("Agregado al implemento activo") : T("Quitado del implemento activo"), "ok");
                // Refresh inmediato para que el pin refleje el cambio.
                await CargarEstadoAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                string motivo = d != null && !string.IsNullOrEmpty(d.Error) ? d.Error! : T("fallo");
                Toast(T("No se pudo cambiar la asignación") + ": " + motivo, "err");
            }
        }
        finally
        {
            _pinOcupado = false;
            if (btn != null) btn.IsEnabled = true;
        }
    }

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();
    private void OnVolverClick(object? sender, RoutedEventArgs e) => OnRequestVolver?.Invoke();

    // =========================================================================
    //  diálogo (AgpModal.confirm)
    // =========================================================================

    private void AbrirConfirm(string titulo, string mensaje, Action onOk)
    {
        SetTexto("ModalTitulo", titulo);
        SetTexto("ModalMensaje", mensaje);
        _modalOnOk = onOk;
        var ov = this.FindControl<Border>("ModalOverlay");
        if (ov != null) ov.IsVisible = true;
    }

    private void CerrarModal()
    {
        _modalOnOk = null;
        var ov = this.FindControl<Border>("ModalOverlay");
        if (ov != null) ov.IsVisible = false;
    }

    private void OnModalConfirmarClick(object? sender, RoutedEventArgs e)
    {
        var accion = _modalOnOk;
        CerrarModal();
        accion?.Invoke();
    }

    private void OnModalCancelarClick(object? sender, RoutedEventArgs e) => CerrarModal();

    // Tocar afuera = Cancelar (mismo comportamiento que el backdrop de AgpModal).
    private void OnModalBackdropPressed(object? sender, PointerPressedEventArgs e) => CerrarModal();

    // La card absorbe el toque para que no llegue al backdrop.
    private void OnModalCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    // =========================================================================
    //  toast + caja de error AGP
    // =========================================================================

    /// <summary>toast() del original: abajo a la derecha, 3,5 s. kind: "ok" |
    /// "err" | "" (neutro).</summary>
    private void Toast(string msg, string kind = "")
    {
        var caja = this.FindControl<Border>("ToastBox");
        var txt = this.FindControl<TextBlock>("ToastText");
        if (caja == null || txt == null) return;

        txt.Text = msg;
        if (kind == "ok") { caja.BorderBrush = VerdeTexto; txt.Foreground = Texto; }
        else if (kind == "err") { caja.BorderBrush = Rojo; txt.Foreground = Rojo; }
        else { caja.BorderBrush = BordeAlto; txt.Foreground = Texto; }
        caja.IsVisible = true;

        _toastTimer?.Stop();
        _toastTimer ??= new DispatcherTimer();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(3500);
        _toastTimer.Tick -= OnToastTick;
        _toastTimer.Tick += OnToastTick;
        _toastTimer.Start();
    }

    private void OnToastTick(object? sender, EventArgs e) => OcultarToast();

    private void OcultarToast()
    {
        _toastTimer?.Stop();
        var caja = this.FindControl<Border>("ToastBox");
        if (caja != null) caja.IsVisible = false;
    }

    /// <summary>Caja de error con el trío AGP: código dictable + mensaje amable
    /// arriba, detalle técnico plegado abajo. El original solo tenía el toast;
    /// el código y el detalle son la convención del repo para que el operario
    /// pueda dictarle algo útil a soporte.</summary>
    private void MostrarErrorAgp<TDatos>(NodoDetalleResultado<TDatos> r) where TDatos : class
    {
        var caja = this.FindControl<Border>("ErrorBox");
        if (caja == null) return;
        SetTexto("ErrCodigo", string.IsNullOrEmpty(r.ErrorCodigo) ? "AGP-NET-001" : r.ErrorCodigo!);
        SetTexto("ErrMensaje", string.IsNullOrEmpty(r.ErrorAmigable) ? (r.ErrorRed ?? "") : T(r.ErrorAmigable!));
        var det = this.FindControl<Expander>("ErrDetalleBox");
        string tecnico = string.IsNullOrWhiteSpace(r.ErrorTecnico) ? (r.ErrorRed ?? "") : r.ErrorTecnico!;
        SetTexto("ErrDetalle", tecnico);
        if (det != null) { det.IsVisible = !string.IsNullOrWhiteSpace(tecnico); det.IsExpanded = false; }
        caja.IsVisible = true;
    }

    private void OcultarError()
    {
        var caja = this.FindControl<Border>("ErrorBox");
        if (caja != null) caja.IsVisible = false;
    }

    /// <summary>Lo que va en el toast cuando hubo excepción: código dictable +
    /// mensaje amable (el detalle técnico queda en la caja de error).</summary>
    private static string Cabeza<TDatos>(NodoDetalleResultado<TDatos> r) where TDatos : class
        => string.IsNullOrEmpty(r.ErrorCodigo)
            ? (r.ErrorRed ?? "")
            : r.ErrorCodigo + " · " + T(r.ErrorAmigable ?? "");

    // =========================================================================
    //  helpers
    // =========================================================================

    private void SetTexto(string nombre, string texto)
    {
        var tb = this.FindControl<TextBlock>(nombre);
        if (tb != null) tb.Text = texto;
    }

    /// <summary>.subtitle del original: una línea gris chica.</summary>
    private static TextBlock Sutil(string texto) => new TextBlock
    {
        Text = texto,
        Foreground = TextoTenue,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>Número como lo imprime JS (sin ceros de más, punto decimal).</summary>
    private static string FmtNum(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>relTime() del original: ahora / hace N s / min / h / d.</summary>
    private static string RelTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return "—";
        double secs = Math.Floor((DateTime.UtcNow - t).TotalSeconds);
        if (secs < 5) return T("ahora");
        if (secs < 60) return T("hace") + " " + ((long)secs).ToString(CultureInfo.InvariantCulture) + " s";
        if (secs < 3600) return T("hace") + " " + ((long)Math.Floor(secs / 60)).ToString(CultureInfo.InvariantCulture) + " min";
        if (secs < 86400) return T("hace") + " " + ((long)Math.Floor(secs / 3600)).ToString(CultureInfo.InvariantCulture) + " h";
        return T("hace") + " " + ((long)Math.Floor(secs / 86400)).ToString(CultureInfo.InvariantCulture) + " d";
    }
}
