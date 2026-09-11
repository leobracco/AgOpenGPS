// OrbitXPanel.axaml.cs
//
// Reemplazo nativo de pages/orbitx.html + js/orbitx.js. Vinculación del tractor
// con OrbitX Cloud, estado del heartbeat y config de orbitX.json.
//
// El port es LITERAL, paso por paso del JS, sin "mejoras":
//   · MISMOS endpoints y verbos (/api/orbitx/{config,status,test,pair-info,
//     pair-reset}), MISMO payload snake_case, MISMO DTO entero de vuelta en el
//     POST (Object.assign sobre el que llegó).
//   · MISMAS cadencias: load() cada 10 s, pollPair() cada 4 s, las dos con su
//     primera pasada inmediata.
//   · MISMOS textos, MISMOS estados (pill idle/ok, card de vinculación
//     ámbar/verde/roja) y MISMO orden de secciones y de tabs.
//   · MISMO manejo de error del pairing: código AGP-* dictable + mensaje
//     amigable arriba, detalle técnico plegado abajo.
//   · server_url SOLO LECTURA: OrbitXConfigService.FixedServerUrl la fuerza en
//     Load() y en Save(). Se muestra, no se edita — pedido 2026-05-27.
//   · La autenticación con el cloud (X-Device-ID + X-Auth-Token) NO se toca:
//     vive entera en el Engine.
//
// BUGS DEL ORIGINAL QUE SE REPLICAN TAL CUAL (ver reporte, no son errores de
// este port):
//   1. load() corre cada 10 s y REPINTA el formulario de Config: lo que el
//      operario esté tipeando se pierde en la siguiente pasada. En el HTML es
//      `formEl.innerHTML = …`, acá es reasignar los TextBox. Igual de molesto,
//      igual de fiel.
//   2. pollPair() evalúa `if (!info || (info.error && !info.deviceId))` con
//      `deviceId` en camelCase mientras el wire manda `device_id`: esa
//      propiedad nunca existe y la guarda queda en "si vino error → card
//      offline". Se conserva la condición completa.
//   3. La tab OTA es un MOCK: cuatro filas escritas a mano en el HTML, sin
//      ningún fetch, con botones que no hacen nada. Se porta como está.
//   4. `lastShownCode` se actualiza pero no se usa para nada (el "re-mostrar
//      feedback al cambiar de código" quedó a medio hacer). Se conserva.
//
// API para el host: Attach(OrbitXPanelClient) arranca los dos polls;
// Detach() los corta, cierra el diálogo y baja el teclado nativo.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class OrbitXPanel : UserControl, IPanelEmbebible
{
    // ---- wire / estado -----------------------------------------------------
    private OrbitXPanelClient? _client;
    private CancellationTokenSource? _cts;

    /// <summary>El `formEl._dto` del JS: la config tal como llegó. El POST
    /// manda ESTE objeto con los campos del formulario pisados encima, así los
    /// que la UI no muestra (master_token, firmware_*, camaras_*) no se
    /// pierden.</summary>
    private OrbitXConfigWire? _cfg;

    /// <summary>Últimos snapshots pintados — para repintar en el cambio de
    /// idioma sin esperar al próximo poll.</summary>
    private OrbitXStatusWire? _ultimoStatus;
    private OrbitXPairInfoWire? _ultimoPair;
    private string? _ultimoPairError;      // excepción del fetch (o body.error)
    private bool _pairModoError;           // la card quedó en "offline crudo"

    /// <summary>Del JS: se actualiza al cambiar el código pero NO se usa (el
    /// feedback de regeneración quedó a medio hacer). Se conserva igual.</summary>
    private string? _lastShownCode;

    private bool _guardando;
    private bool _probando;
    private bool _idiomaEnganchado;        // suscripción viva a Traductor.IdiomaCambio
    private int _colsEstado = -1;          // columnas pintadas del grid de Estado
    private double _otaAncho = -1;         // ancho pintado de la barra mock

    // ---- diálogo interno ---------------------------------------------------
    // _modalOnOk    → confirm(): corre SOLO con el botón OK.
    // _alertaCierre → alert(): corre con CUALQUIER cierre (OK o tocar afuera),
    //                 igual que la promesa de AgpModal.alert.
    private Action? _modalOnOk;
    private Action? _alertaCierre;

    // ---- callbacks al host -------------------------------------------------

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>El botón "Abrir Prescripciones" de la última tab. En el HTML es
    /// un &lt;a href="quantix.html?tab=shape&amp;widget=1"&gt;; acá el host decide a
    /// qué pantalla nativa lleva (la Configuración tiene la entrada
    /// "prescripciones" = el editor QuantiX parado en Shape). Sin handler, el
    /// botón no hace nada.</summary>
    public Action? OnRequestPrescripciones { get; set; }

    // ---- textos EXACTOS del original --------------------------------------
    // Son la CLAVE del diccionario (castellano): todo lo que se pinta pasa por T().
    private const string TxtSinVerificar   = "Sin verificar";
    private const string TxtCloudConectado = "Cloud conectado";
    // "Cargando…" vive en el XAML (adentro de PairBody), igual que en el HTML.
    private const string TxtSinRespuesta   = "Sin respuesta del servicio.";
    private const string TxtSinConexion    = "Sin conexión local al servicio: ";

    /// <summary>Placeholder del código: SEIS rayas largas, igual que el JS.</summary>
    private const string TxtCodigoVacio    = "——————";

    private static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    // ---- pinceles: SIEMPRE desde los tokens del theme ----------------------
    // Se resuelven contra los recursos de la app (PilotXTheme.axaml, mergeado
    // en App.axaml). El valor de respaldo es el del propio token, y solo entra
    // en juego si alguien saca el recurso del theme.
    private static readonly Dictionary<string, string> Respaldo = new()
    {
        ["PilotXPanelCard"]        = "#FAFBFA",
        ["PilotXPanelSurface"]     = "#FFFFFF",
        ["PilotXPanelSurface2"]    = "#EDF1EC",
        ["PilotXPanelBg"]          = "#F5F7F4",
        ["PilotXPanelBorder"]      = "#E2E7E2",
        ["PilotXPanelBorderHigh"]  = "#C5CFC5",
        ["PilotXPanelText"]        = "#101612",
        ["PilotXPanelTextDim"]     = "#535E54",
        ["PilotXPanelAccent"]      = "#4ABA3E",
        ["PilotXPanelAccentText"]  = "#2F7A26",
        ["PilotXPanelOk"]          = "#2F7A26",
        ["PilotXPanelWarn"]        = "#8A6100",
        ["PilotXPanelErr"]         = "#C0261F",
        ["PilotXPanelIdle"]        = "#6E7A70",
        ["PilotXPanelOkSoft"]      = "#E8F4E5",
        ["PilotXPanelWarnSoft"]    = "#FBF1DC",
        ["PilotXPanelErrSoft"]     = "#FBE6E6",
    };

    private readonly Dictionary<string, IBrush> _pinceles = new();

    private IBrush P(string token)
    {
        if (_pinceles.TryGetValue(token, out var cache)) return cache;
        try
        {
            if (this.TryFindResource(token, out var v) && v is IBrush ib)
            {
                _pinceles[token] = ib;   // solo se cachea lo que SÍ resolvió
                return ib;
            }
        }
        catch { }
        // Todavía sin árbol (o alguien sacó el token del theme): se usa el valor
        // del propio token y NO se cachea, así el próximo pintado lo resuelve
        // contra el recurso de verdad.
        return new SolidColorBrush(Color.Parse(Respaldo.TryGetValue(token, out var hex) ? hex : "#535E54"));
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public OrbitXPanel()
    {
        InitializeComponent();

        // La card de vinculación arranca en "Cargando…" — está escrito en el
        // XAML (el HTML también lo trae inline en el pairBody) y el primer
        // render lo reemplaza entero.

        // La tab Prescripciones nombra la cadencia del heartbeat; el HTML la
        // deja en "30" hasta que llega la config.
        PintarPresc(30);

        // Teclado virtual PROPIO de PilotX (POST /api/teclado/abrir|cerrar).
        // NUNCA osk.exe. Device ID es readonly → no pide teclado.
        EngancharTeclado("CfgEstabSlug", "Establecimiento slug", numerico: false);
        EngancharTeclado("CfgDeviceToken", "Device Token", numerico: false);
        EngancharTeclado("CfgSyncInterval", "Sync interval (s)", numerico: true);

        // El grid de Estado imita el `auto-fit minmax(280px, 1fr)` del CSS.
        var grilla = this.FindControl<Grid>("PaneEstado");
        if (grilla != null) grilla.SizeChanged += (_, e) => AcomodarEstado(e.NewSize.Width);

        // La barra de progreso de la tab OTA es DIBUJO (width:62% en el HTML):
        // no hay ninguna transferencia detrás.
        var riel = this.FindControl<Border>("OtaProgresoRiel");
        if (riel != null) riel.SizeChanged += (_, e) => PintarProgresoMock(e.NewSize.Width);

        // El diccionario se aplica UNA SOLA VEZ, al construir: Aplicar cachea el
        // primer texto de cada control y se lo reescribe encima en cada pasada —
        // llamarlo en los render congelaría la pill de conexión y el hint del
        // formulario en su primer valor.
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    // ---- idioma en caliente -------------------------------------------------

    /// <summary>Se engancha en Attach y se SUELTA en Detach. IdiomaCambio es un
    /// evento ESTÁTICO: dejarlo enganchado de por vida hace que cada panel
    /// construido siga repintando desde el fondo, cerrado, para siempre. Con el
    /// panel cerrado tampoco hace falta — al reabrirlo, Attach vuelve a cargar
    /// config + estado y repinta todo.</summary>
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

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    /// <summary>La pill de conexión se va a la barra de contexto del shell.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<StackPanel>("HeaderPills"));

    /// <summary>Inyecta el cliente y arranca los dos polls, igual que el final
    /// del script: load() + setInterval(load, 10000) y pollPair() +
    /// setInterval(pollPair, 4000).</summary>
    public void Attach(OrbitXPanelClient client)
    {
        _client = client;
        EngancharIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = BucleCargaAsync(ct);
        _ = BuclePairAsync(ct);
    }

    /// <summary>Corta los polls, cierra el diálogo y baja el teclado nativo.
    /// Cerrado no se hace red ni queda un teclado flotando sobre el mapa.</summary>
    public void Detach()
    {
        SoltarIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _alertaCierre = null;
        CerrarModal();
        _ = _client?.TecladoAsync(false);
    }

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    /// <summary>Cambio de idioma con el panel abierto. Van las DOS cosas y en
    /// este orden: primero Aplicar sobre el árbol —si no, las tabs, "Guardar",
    /// "Probar conexión", "Server URL" y los toggles se quedan en el idioma
    /// viejo y la pantalla queda mitad y mitad— y después el repintado de lo
    /// que arma el código (card de pairing, estado, hint), que Aplicar habría
    /// devuelto a su primer valor cacheado.</summary>
    private void OnIdiomaCambio()
        => Dispatcher.UIThread.Post(() =>
        {
            PilotX.Cockpit.Bars.Traductor.Aplicar(this);
            if (_ultimoStatus != null) RenderStatus(_ultimoStatus);
            if (_cfg != null) PintarPresc(_cfg.SyncIntervalSec);
            RepintarPair();
        });

    // =========================================================================
    //  polls
    // =========================================================================

    /// <summary>load() cada 10 s (primera pasada inmediata).</summary>
    private async Task BucleCargaAsync(CancellationToken ct)
    {
        await CargarAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(10000), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await CargarAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>pollPair() cada 4 s (primera pasada inmediata).</summary>
    private async Task BuclePairAsync(CancellationToken ct)
    {
        await PollPairAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(4000), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await PollPairAsync(ct).ConfigureAwait(false);
        }
    }

    // =========================================================================
    //  load() — config + status
    // =========================================================================

    private async Task CargarAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var r = await _client.CargarAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || r.Cancelado) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (r.Error != null)
            {
                // catch del fetch: SOLO el hint. Ni el form ni el estado se tocan.
                SetTexto("FormHint", T("No se pudo cargar la config: ") + r.Error);
                return;
            }
            if (r.Config != null) RenderForm(r.Config);
            if (r.Status != null) RenderStatus(r.Status);
        });
    }

    /// <summary>renderStatus() del JS, campo por campo.</summary>
    private void RenderStatus(OrbitXStatusWire s)
    {
        _ultimoStatus = s;

        SetTexto("EstHeartbeat", s.Enabled ? "on" : "off");
        SetTexto("EstUltimoSync", FmtTs(s.LastSync));
        SetTexto("EstArchivos", s.FilesSynced.ToString(CultureInfo.InvariantCulture));
        SetTexto("EstEstab", string.IsNullOrEmpty(s.EstabSlug) ? "—" : s.EstabSlug!);
        SetTexto("EstDeviceId", string.IsNullOrEmpty(s.DeviceId) ? "—" : s.DeviceId!);
        SetTexto("EstConexion", s.CloudConnected ? "OK" : "—");
        SetTexto("EstUltimoError", string.IsNullOrEmpty(s.LastError) ? T("ninguno") : s.LastError!);

        PintarPillConexion(s.CloudConnected);
    }

    /// <summary>La pill del header: `pill ok` / `pill idle`.</summary>
    private void PintarPillConexion(bool conectado)
    {
        var caja = this.FindControl<Border>("ConnPill");
        var punto = this.FindControl<Ellipse>("ConnDot");
        var txt = this.FindControl<TextBlock>("ConnText");
        if (caja == null || punto == null || txt == null) return;

        if (conectado)
        {
            caja.Background = P("PilotXPanelOkSoft");
            caja.BorderBrush = P("PilotXPanelOk");
            punto.Fill = P("PilotXPanelOk");
            txt.Foreground = P("PilotXPanelOk");
            txt.Text = T(TxtCloudConectado);
        }
        else
        {
            caja.Background = P("PilotXPanelSurface2");
            caja.BorderBrush = P("PilotXPanelBorderHigh");
            punto.Fill = P("PilotXPanelIdle");
            txt.Foreground = P("PilotXPanelTextDim");
            txt.Text = T(TxtSinVerificar);
        }
    }

    /// <summary>renderForm() del JS.
    ///
    /// OJO: esto corre en CADA pasada de load(), o sea cada 10 s, y pisa lo que
    /// el operario esté tipeando — igual que el `innerHTML =` del original, que
    /// además le sacaba el foco. Es un bug del original que se replica a
    /// propósito (ver cabecera).</summary>
    private void RenderForm(OrbitXConfigWire cfg)
    {
        SetTexto("CfgServerUrl", string.IsNullOrEmpty(cfg.ServerUrl)
            ? "https://orbitx.agroparallel.com" : cfg.ServerUrl!);

        SetCampo("CfgEstabSlug", cfg.EstabSlug ?? "");
        SetCampo("CfgDeviceId", cfg.DeviceId ?? "");
        SetCampo("CfgDeviceToken", cfg.DeviceToken ?? "");
        SetCampo("CfgSyncInterval", cfg.SyncIntervalSec.ToString(CultureInfo.InvariantCulture));

        // `if (prescIntervalEl && syncIntervalSec)`: con 0 NO se toca el texto.
        if (cfg.SyncIntervalSec != 0) PintarPresc(cfg.SyncIntervalSec);

        SetTilde("ChkEnabled", cfg.Enabled);
        SetTilde("ChkSyncAog", cfg.SyncAOG);
        SetTilde("ChkSyncVistaX", cfg.SyncVistaX);
        SetTilde("ChkSyncQuantiX", cfg.SyncQuantiX);
        SetTilde("ChkSyncSectionX", cfg.SyncSectionX);

        _cfg = cfg;
    }

    /// <summary>collectDto() del JS: parte del DTO que llegó y le pisa encima
    /// SOLO los campos del formulario.</summary>
    private OrbitXConfigWire CollectDto()
    {
        var d = _cfg ?? new OrbitXConfigWire();
        // Object.assign({}, dto): copia, no se muta el original.
        var dto = new OrbitXConfigWire
        {
            Enabled = d.Enabled,
            ServerUrl = d.ServerUrl,
            DeviceToken = d.DeviceToken,
            MasterToken = d.MasterToken,
            DeviceId = d.DeviceId,
            EstabSlug = d.EstabSlug,
            SyncIntervalSec = d.SyncIntervalSec,
            SyncAOG = d.SyncAOG,
            SyncVistaX = d.SyncVistaX,
            SyncQuantiX = d.SyncQuantiX,
            SyncSectionX = d.SyncSectionX,
            FirmwareMirrorEnabled = d.FirmwareMirrorEnabled,
            FirmwareCacheDir = d.FirmwareCacheDir,
            FirmwareHttpPort = d.FirmwareHttpPort,
            FirmwareSyncIntervalMin = d.FirmwareSyncIntervalMin,
            CamarasStreamingEnabled = d.CamarasStreamingEnabled,
            CamarasRtspHost = d.CamarasRtspHost,
            CamarasRtspPort = d.CamarasRtspPort,
            CamarasFfmpegPath = d.CamarasFfmpegPath,
            LastSync = d.LastSync,
            FilesSynced = d.FilesSynced
        };

        // Los inputs de texto van SIN trim (el JS manda `el.value` crudo).
        dto.EstabSlug = LeerCampo("CfgEstabSlug");
        // Device ID es readonly pero el JS igual lo recolecta
        // (`input[data-name]` no filtra readonly): se manda tal cual.
        dto.DeviceId = LeerCampo("CfgDeviceId");
        dto.DeviceToken = LeerCampo("CfgDeviceToken");
        dto.SyncIntervalSec = ParseIntJs(LeerCampo("CfgSyncInterval"));

        dto.Enabled = LeerTilde("ChkEnabled");
        dto.SyncAOG = LeerTilde("ChkSyncAog");
        dto.SyncVistaX = LeerTilde("ChkSyncVistaX");
        dto.SyncQuantiX = LeerTilde("ChkSyncQuantiX");
        dto.SyncSectionX = LeerTilde("ChkSyncSectionX");

        return dto;
    }

    // =========================================================================
    //  Guardar / Probar conexión
    // =========================================================================

    private async void OnGuardarClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null || _guardando) return;
        _guardando = true;
        SetEnabled("BtnGuardar", false);
        SetTexto("FormHint", T("Guardando…"));

        var ct = _cts?.Token ?? CancellationToken.None;
        var r = await _client.GuardarAsync(CollectDto(), ct).ConfigureAwait(true);

        if (!r.Cancelado)
        {
            SetTexto("FormHint", r.Excepcion != null
                ? T("Error: ") + r.Excepcion
                : (r.Ok ? T("Guardado.") : T("Error al guardar.")));
        }
        _guardando = false;
        SetEnabled("BtnGuardar", true);
    }

    private async void OnProbarClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null || _probando) return;
        _probando = true;
        SetEnabled("BtnProbar", false);
        SetTexto("FormHint", T("Probando conexión…"));

        var ct = _cts?.Token ?? CancellationToken.None;
        var r = await _client.ProbarAsync(ct).ConfigureAwait(true);

        if (!r.Cancelado)
        {
            if (r.Excepcion != null)
            {
                SetTexto("FormHint", T("Error: ") + r.Excepcion);
            }
            else if (r.Ok)
            {
                SetTexto("FormHint", T("✓ Conexión OK"));
                // El JS fuerza la pill a "ok" acá, sin esperar al próximo load().
                PintarPillConexion(true);
            }
            else
            {
                SetTexto("FormHint", "✗ " + (string.IsNullOrEmpty(r.Error) ? T("sin respuesta") : r.Error!));
            }
        }
        _probando = false;
        SetEnabled("BtnProbar", true);
    }

    // =========================================================================
    //  pairing — pollPair()
    // =========================================================================

    private async Task PollPairAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var r = await _client.PairInfoAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || r.Cancelado) return;
        await Dispatcher.UIThread.InvokeAsync(() => AplicarPair(r));
    }

    private void AplicarPair(OrbitXPairResultado r)
    {
        var card = this.FindControl<Border>("PairCard");
        if (card == null) return;

        // catch del fetch: la card se muestra en rojo y el cuerpo dice el error.
        // El título y la pill NO se tocan (el JS tampoco los toca acá).
        if (r.Excepcion != null)
        {
            card.IsVisible = true;
            PintarMarcoPair("offline");
            _pairModoError = true;
            _ultimoPair = null;
            _ultimoPairError = T(TxtSinConexion) + r.Excepcion;
            PintarCuerpoError(_ultimoPairError);
            return;
        }

        var info = r.Info;
        // Guarda LITERAL del JS: `!info || (info.error && !info.deviceId)`.
        // `deviceId` (camelCase) nunca viene en el wire snake_case, así que la
        // segunda mitad siempre es verdadera — ver cabecera.
        if (info == null || (!string.IsNullOrEmpty(info.Error) && string.IsNullOrEmpty(info.DeviceIdCamel)))
        {
            card.IsVisible = true;
            PintarMarcoPair("offline");
            _pairModoError = true;
            _ultimoPair = null;
            // `info && info.error || 'Sin respuesta del servicio.'`
            _ultimoPairError = string.IsNullOrEmpty(info?.Error) ? T(TxtSinRespuesta) : info!.Error!;
            PintarCuerpoError(_ultimoPairError);
            return;
        }

        card.IsVisible = true;
        _pairModoError = false;
        _ultimoPairError = null;
        _ultimoPair = info;

        if (info.Paired)
        {
            RenderPairPaired(info);
            if (info.JustClaimed)
            {
                // Refrescamos form/estado para reflejar el token nuevo.
                var ct = _cts?.Token ?? CancellationToken.None;
                _ = CargarAsync(ct);
            }
        }
        else
        {
            RenderPairUnpaired(info);
        }
    }

    /// <summary>Repinta la card de pairing con lo último que se sabe (cambio de
    /// idioma).</summary>
    private void RepintarPair()
    {
        if (_pairModoError) { PintarCuerpoError(_ultimoPairError ?? T(TxtSinRespuesta)); return; }
        var info = _ultimoPair;
        if (info == null) return;
        if (info.Paired) RenderPairPaired(info);
        else RenderPairUnpaired(info);
    }

    // ---- sin vincular -------------------------------------------------------

    private void RenderPairUnpaired(OrbitXPairInfoWire info)
    {
        PintarMarcoPair(info.Status == "offline" ? "offline" : "unpaired");
        SetTexto("PairTitle", T("Vinculá este tractor a OrbitX Cloud"));

        if (info.Status == "offline")
            PintarPillPair("offline", T("sin conexión al cloud"));
        else if (info.Status == "expired")
            PintarPillPair("pending", T("código vencido, generando otro…"));
        else
            PintarPillPair("pending", T("esperando confirmación"));

        string code = string.IsNullOrEmpty(info.Code) ? TxtCodigoVacio : info.Code!;
        int expires = info.ExpiresInSec;
        int mm = expires / 60, ss = expires % 60;
        string clock = expires > 0
            ? T("vence en ") + mm.ToString(CultureInfo.InvariantCulture) + ":" +
              (ss < 10 ? "0" : "") + ss.ToString(CultureInfo.InvariantCulture)
            : "";
        string serverUrl = string.IsNullOrEmpty(info.ServerUrl) ? T("(no configurado)") : info.ServerUrl!;
        string serverHost = QuitarEsquema(serverUrl);

        var body = this.FindControl<StackPanel>("PairBody");
        if (body == null) return;
        body.Children.Clear();

        // ---- el código, grande ----
        body.Children.Add(new Border
        {
            Background = P("PilotXPanelSurface2"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 12, 0, 12),
            Child = new SelectableTextBlock
            {
                Text = code,
                Foreground = P("PilotXPanelText"),
                FontFamily = Mono,
                FontSize = 56,
                FontWeight = FontWeight.Bold,
                LetterSpacing = 12,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        });

        // ---- los 3 pasos ----
        var pasos = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Margin = new Thickness(0, 4, 0, 16)
        };
        // El host del server va en NEGRITA y "Vincular por código" en cursiva,
        // igual que el <strong>/<em> del HTML. El ⚡ que el HTML le ponía
        // adelante queda afuera: en Avalonia el emoji depende de la fuente
        // instalada y en la pantalla de cabina sale cuadradito.
        var p1 = Paso("1", T("Abrí OrbitX Cloud"), new InlineCollection
        {
            new Run(T("Desde tu PC o teléfono, logueate en ")),
            new Run(serverHost) { FontWeight = FontWeight.Bold },
            new Run(T(" con tu usuario de la organización.")),
        });
        var p2 = Paso("2", T("Dispositivos → Vincular por código"), new InlineCollection
        {
            new Run(T("En el panel cloud, andá a la sección \"Dispositivos\" y tocá ")),
            new Run(T("Vincular por código")) { FontStyle = FontStyle.Italic },
            new Run("."),
        });
        var p3 = Paso("3", T("Ingresá el código"), new InlineCollection
        {
            new Run(T("Tipeá los 6 caracteres de arriba, dale un nombre al tractor y confirmá. En unos segundos esta pantalla va a decir \"Vinculado\".")),
        });
        p1.Margin = new Thickness(0, 0, 6, 0);
        p2.Margin = new Thickness(6, 0, 6, 0);
        p3.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(p1, 0); Grid.SetColumn(p2, 1); Grid.SetColumn(p3, 2);
        pasos.Children.Add(p1); pasos.Children.Add(p2); pasos.Children.Add(p3);
        body.Children.Add(pasos);

        // ---- meta ----
        var meta = new WrapPanel();
        meta.Children.Add(MetaItem(T("Device ID local: "),
                                   string.IsNullOrEmpty(info.DeviceId) ? "—" : info.DeviceId!, mono: true));
        meta.Children.Add(MetaItem(T("Server: "), serverUrl, mono: false));
        if (clock.Length > 0)
            meta.Children.Add(new TextBlock
            {
                Text = clock,
                Foreground = P("PilotXPanelTextDim"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 16, 4),
                VerticalAlignment = VerticalAlignment.Center
            });
        body.Children.Add(meta);

        // ---- caja de error AGP-* / hint ----
        if (!string.IsNullOrEmpty(info.ErrorCode))
        {
            body.Children.Add(CajaError(info.ErrorCode!, info.Hint, info.HintTechnical));
        }
        else if (!string.IsNullOrEmpty(info.Hint))
        {
            body.Children.Add(new TextBlock
            {
                Text = info.Hint!,
                Foreground = P("PilotXPanelText"),
                FontSize = 14,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        // Del original: se guarda el código nuevo, pero no se usa para nada.
        if (!string.IsNullOrEmpty(info.Code) && info.Code != _lastShownCode)
            _lastShownCode = info.Code;
    }

    /// <summary>Un paso del instructivo (.pair-step): círculo numerado + título
    /// + cuerpo.</summary>
    private Control Paso(string numero, string titulo, InlineCollection cuerpo)
    {
        var cabeza = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        cabeza.Children.Add(new Border
        {
            Background = P("PilotXPanelAccent"),
            CornerRadius = new CornerRadius(999),
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Top,
            // Texto OSCURO sobre el verde de marca: es la regla del theme
            // (#4ABA3E es RELLENO; el blanco encima da 2.5:1 y al sol no se lee).
            Child = new TextBlock
            {
                Text = numero,
                Foreground = P("PilotXPanelText"),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        cabeza.Children.Add(new TextBlock
        {
            Text = titulo,
            Foreground = P("PilotXPanelText"),
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        var pila = new StackPanel { Spacing = 0 };
        pila.Children.Add(cabeza);
        pila.Children.Add(new TextBlock
        {
            Inlines = cuerpo,
            Foreground = P("PilotXPanelText"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        return new Border
        {
            Background = P("PilotXPanelBg"),
            BorderBrush = P("PilotXPanelBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = pila
        };
    }

    /// <summary>Un dato de la fila .pair-meta: etiqueta tenue + valor fuerte.</summary>
    private Control MetaItem(string etiqueta, string valor, bool mono)
    {
        var linea = new TextBlock
        {
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 16, 4)
        };
        linea.Inlines = new InlineCollection
        {
            new Run(etiqueta),
            new Run(valor)
            {
                Foreground = P("PilotXPanelText"),
                FontWeight = FontWeight.Bold,
                FontFamily = mono ? Mono : linea.FontFamily
            },
        };
        return linea;
    }

    /// <summary>Caja de error del pairing: badge AGP-* dictable + mensaje
    /// amigable arriba, detalle técnico PLEGADO abajo (convención del repo).</summary>
    private Control CajaError(string codigo, string? hint, string? tecnico)
    {
        var pila = new StackPanel { Spacing = 8 };

        var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fila.Children.Add(new Border
        {
            Background = P("PilotXPanelErr"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = codigo,
                Foreground = P("PilotXPanelSurface"),
                FontFamily = Mono,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        if (!string.IsNullOrEmpty(hint))
            fila.Children.Add(new TextBlock
            {
                Text = hint!,
                Foreground = P("PilotXPanelText"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
        pila.Children.Add(fila);

        pila.Children.Add(new TextBlock
        {
            Text = T("Para soporte: dictá el código ") + codigo + T(" al asistente por WhatsApp o teléfono."),
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrEmpty(tecnico))
        {
            pila.Children.Add(new Expander
            {
                Header = T("Detalle técnico (soporte)"),
                Content = new TextBlock
                {
                    Text = tecnico!,
                    Foreground = P("PilotXPanelText"),
                    FontFamily = Mono,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        return new Border
        {
            Background = P("PilotXPanelErrSoft"),
            BorderBrush = P("PilotXPanelErr"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 12, 0, 0),
            Child = pila
        };
    }

    // ---- vinculado ----------------------------------------------------------

    private void RenderPairPaired(OrbitXPairInfoWire info)
    {
        PintarMarcoPair("paired");
        SetTexto("PairTitle", T("Tractor vinculado"));
        PintarPillPair("ok", T("✓ activo"));

        var body = this.FindControl<StackPanel>("PairBody");
        if (body == null) return;
        body.Children.Clear();

        var meta = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        meta.Children.Add(MetaItem(T("Establecimiento: "),
            string.IsNullOrEmpty(info.EstabSlug) ? "—" : info.EstabSlug!, mono: false));
        meta.Children.Add(MetaItem(T("Device ID: "),
            string.IsNullOrEmpty(info.DeviceId) ? "—" : info.DeviceId!, mono: true));
        meta.Children.Add(MetaItem(T("Server: "),
            string.IsNullOrEmpty(info.ServerUrl) ? "—" : info.ServerUrl!, mono: false));
        body.Children.Add(meta);

        var btn = new Button
        {
            Content = T("Desvincular"),
            Background = P("PilotXPanelSurface"),
            Foreground = P("PilotXPanelText"),
            BorderBrush = P("PilotXPanelBorderHigh"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinHeight = 48,
            Padding = new Thickness(20, 0, 20, 0),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btn.Click += (_, __) => PedirDesvincular(btn);
        body.Children.Add(btn);
    }

    private void PedirDesvincular(Button btn)
    {
        AbrirModal(
            T("Desvincular OrbitX"),
            T("¿Desvincular este tractor de OrbitX? El token actual se borra y vas a tener que volver a vincularlo desde el panel cloud."),
            () => _ = DesvincularAsync(btn));
    }

    private async Task DesvincularAsync(Button btn)
    {
        if (_client == null) return;
        btn.IsEnabled = false;
        var ct = _cts?.Token ?? CancellationToken.None;

        var r = await _client.PairResetAsync(ct).ConfigureAwait(true);
        if (r.Cancelado) return;

        if (r.Excepcion != null)
        {
            // alert() del original: un solo botón, y el botón vuelve a habilitarse.
            AbrirAlerta("OrbitX", T("Error: ") + r.Excepcion);
            btn.IsEnabled = true;
            return;
        }

        _lastShownCode = null;
        await PollPairAsync(ct).ConfigureAwait(true);
    }

    // ---- pintura de la card de pairing --------------------------------------

    /// <summary>`.pair-card` + estado: unpaired (ámbar) / paired (verde) /
    /// offline (rojo). En el original el fondo tintado lo tiene solo el estado
    /// "sin vincular"; los otros dos quedan sobre la superficie de la card.</summary>
    private void PintarMarcoPair(string estado)
    {
        var card = this.FindControl<Border>("PairCard");
        if (card == null) return;
        switch (estado)
        {
            case "paired":
                card.BorderBrush = P("PilotXPanelAccent");
                card.Background = P("PilotXPanelSurface");
                break;
            case "offline":
                card.BorderBrush = P("PilotXPanelErr");
                card.Background = P("PilotXPanelSurface");
                break;
            default:  // unpaired
                card.BorderBrush = P("PilotXPanelWarn");
                card.Background = P("PilotXPanelWarnSoft");
                break;
        }
    }

    /// <summary>`.pair-state-pill` + estado: pending (ámbar) / ok (verde) /
    /// offline (rojo).</summary>
    private void PintarPillPair(string estado, string texto)
    {
        var caja = this.FindControl<Border>("PairStatePill");
        var txt = this.FindControl<TextBlock>("PairStateText");
        if (caja == null || txt == null) return;
        switch (estado)
        {
            case "ok":
                caja.Background = P("PilotXPanelOkSoft");
                caja.BorderBrush = P("PilotXPanelOk");
                txt.Foreground = P("PilotXPanelOk");
                break;
            case "offline":
                caja.Background = P("PilotXPanelErrSoft");
                caja.BorderBrush = P("PilotXPanelErr");
                txt.Foreground = P("PilotXPanelErr");
                break;
            default:  // pending
                caja.Background = P("PilotXPanelWarnSoft");
                caja.BorderBrush = P("PilotXPanelWarn");
                txt.Foreground = P("PilotXPanelWarn");
                break;
        }
        txt.Text = texto;
    }

    /// <summary>El cuerpo de la card cuando no hubo respuesta usable: una sola
    /// línea con clase .subtitle, igual que el HTML.</summary>
    private void PintarCuerpoError(string mensaje)
    {
        var body = this.FindControl<StackPanel>("PairBody");
        if (body == null) return;
        body.Children.Clear();
        body.Children.Add(Subtitulo(mensaje));
    }

    private TextBlock Subtitulo(string texto) => new TextBlock
    {
        Text = texto,
        Foreground = P("PilotXPanelTextDim"),
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap
    };

    // =========================================================================
    //  tabs
    // =========================================================================

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string clave) MostrarTab(clave);
    }

    private void MostrarTab(string clave)
    {
        PintarTab("TabEstado", clave == "estado");
        PintarTab("TabConfig", clave == "config");
        PintarTab("TabOta", clave == "ota");
        PintarTab("TabPresc", clave == "presc");

        SetVisible("PaneEstado", clave == "estado");
        SetVisible("PaneConfig", clave == "config");
        SetVisible("PaneOta", clave == "ota");
        SetVisible("PanePresc", clave == "presc");
    }

    private void PintarTab(string nombre, bool activa)
    {
        var b = this.FindControl<Button>(nombre);
        if (b == null) return;
        b.Foreground = activa ? P("PilotXPanelText") : P("PilotXPanelTextDim");
        b.BorderBrush = activa ? P("PilotXPanelAccent") : Brushes.Transparent;
        b.FontWeight = activa ? FontWeight.Medium : FontWeight.Normal;
    }

    // =========================================================================
    //  prescripciones
    // =========================================================================

    /// <summary>El párrafo de la tab Prescripciones, con la cadencia real del
    /// heartbeat (el HTML arranca en 30 y orbitx.js la pisa con la config).</summary>
    private void PintarPresc(int intervaloSeg)
    {
        var tb = this.FindControl<TextBlock>("PrescTexto");
        if (tb == null) return;
        tb.Inlines = new InlineCollection
        {
            new Run(T("Las prescripciones que llegan de OrbitX cloud se descargan solas (heartbeat cada ")),
            new Run(intervaloSeg.ToString(CultureInfo.InvariantCulture)),
            new Run(T("s) y se activan desde la pantalla ")),
            new Run(T("Prescripciones")) { FontWeight = FontWeight.Bold },
            new Run(T(", en el grupo ")),
            new Run(T("Campo")) { FontWeight = FontWeight.Bold },
            new Run(T(" del menú.")),
        };
    }

    private void OnAbrirPrescClick(object? sender, RoutedEventArgs e)
        => OnRequestPrescripciones?.Invoke();

    // =========================================================================
    //  diálogo (AgpModal.confirm / AgpModal.alert)
    // =========================================================================

    private void AbrirModal(string titulo, string mensaje, Action onOk)
    {
        SetTexto("ModalTitulo", titulo);
        SetTexto("ModalMensaje", mensaje);
        var btnOk = this.FindControl<Button>("BtnModalConfirmar");
        if (btnOk != null) btnOk.Content = T("Aceptar");
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
        if (btnOk != null) btnOk.Content = T("Aceptar");
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
    //  layout + helpers
    // =========================================================================

    /// <summary>Reparte las 4 cards de Estado como el
    /// `repeat(auto-fit, minmax(280px, 1fr))` del CSS, con el mismo gap de
    /// 20 px.</summary>
    private void AcomodarEstado(double ancho)
    {
        const double MinCard = 280, Gap = 20;
        int cols = ancho > 0 ? (int)Math.Floor((ancho + Gap) / (MinCard + Gap)) : 1;
        if (cols < 1) cols = 1;
        if (cols > 4) cols = 4;
        if (cols == _colsEstado) return;
        _colsEstado = cols;

        var grid = this.FindControl<Grid>("PaneEstado");
        if (grid == null) return;

        var cards = new[]
        {
            this.FindControl<Border>("CardHeartbeat"),
            this.FindControl<Border>("CardCola"),
            this.FindControl<Border>("CardEstab"),
            this.FindControl<Border>("CardConexion"),
        };

        var colDefs = new ColumnDefinitions();
        for (int i = 0; i < cols; i++) colDefs.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions = colDefs;

        int filas = (int)Math.Ceiling(4.0 / cols);
        var rowDefs = new RowDefinitions();
        for (int i = 0; i < filas; i++) rowDefs.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions = rowDefs;

        for (int i = 0; i < cards.Length; i++)
        {
            var c = cards[i];
            if (c == null) continue;
            int fila = i / cols, col = i % cols;
            Grid.SetRow(c, fila);
            Grid.SetColumn(c, col);
            c.Margin = new Thickness(col == 0 ? 0 : Gap / 2, fila == 0 ? 0 : Gap,
                                     col == cols - 1 ? 0 : Gap / 2, 0);
        }
    }

    /// <summary>La barra de la tab OTA está al 62% en el HTML: es dibujo, no
    /// hay ninguna transferencia detrás.</summary>
    private void PintarProgresoMock(double anchoRiel)
    {
        if (anchoRiel <= 0 || Math.Abs(anchoRiel - _otaAncho) < 0.5) return;
        _otaAncho = anchoRiel;
        var barra = this.FindControl<Border>("OtaProgresoBarra");
        if (barra != null) barra.Width = anchoRiel * 0.62;
    }

    private void EngancharTeclado(string campo, string titulo, bool numerico)
    {
        var tb = this.FindControl<TextBox>(campo);
        if (tb == null) return;
        tb.GotFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, T(titulo), numerico); };
        tb.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
    }

    private void SetTexto(string nombre, string texto)
    {
        var tb = this.FindControl<TextBlock>(nombre);
        if (tb != null) { tb.Text = texto; return; }
        var st = this.FindControl<SelectableTextBlock>(nombre);
        if (st != null) st.Text = texto;
    }

    private void SetCampo(string nombre, string texto)
    {
        var tb = this.FindControl<TextBox>(nombre);
        if (tb != null && tb.Text != texto) tb.Text = texto;
    }

    private string LeerCampo(string nombre) => this.FindControl<TextBox>(nombre)?.Text ?? "";

    private void SetTilde(string nombre, bool valor)
    {
        var c = this.FindControl<CheckBox>(nombre);
        if (c != null) c.IsChecked = valor;
    }

    private bool LeerTilde(string nombre) => this.FindControl<CheckBox>(nombre)?.IsChecked == true;

    private void SetEnabled(string nombre, bool habilitado)
    {
        var b = this.FindControl<Button>(nombre);
        if (b != null) b.IsEnabled = habilitado;
    }

    private void SetVisible(string nombre, bool visible)
    {
        var c = this.FindControl<Control>(nombre);
        if (c != null) c.IsVisible = visible;
    }

    /// <summary>fmtTs() del JS: ISO → hora local legible. Vacío = "—"; si no
    /// parsea, se devuelve el string crudo (igual que el original).</summary>
    private static string FmtTs(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "—";
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var d))
            return d.LocalDateTime.ToString("G", CultureInfo.CurrentCulture);
        return iso!;
    }

    /// <summary>`parseInt(v, 10) || 0` de JS: toma los dígitos del principio
    /// (con signo opcional) y devuelve 0 si no hay ninguno. NO es int.Parse:
    /// "30 seg" da 30, "abc" da 0.</summary>
    private static int ParseIntJs(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int i = 0;
        while (i < s!.Length && char.IsWhiteSpace(s[i])) i++;
        bool neg = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) { neg = s[i] == '-'; i++; }
        long v = 0; bool hubo = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            hubo = true;
            v = v * 10 + (s[i] - '0');
            if (v > int.MaxValue) { v = int.MaxValue; break; }
            i++;
        }
        if (!hubo) return 0;
        return (int)(neg ? -v : v);
    }

    /// <summary>`serverUrl.replace(/^https?:\/\//, '')` del JS.</summary>
    private static string QuitarEsquema(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("https://", StringComparison.Ordinal)) return url.Substring(8);
        if (url.StartsWith("http://", StringComparison.Ordinal)) return url.Substring(7);
        return url;
    }
}
