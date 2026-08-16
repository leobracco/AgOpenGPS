// ============================================================================
// ConfigPanel.axaml.cs — shell nativo de la Configuración (porteo de
// pages/config.html: menú lateral en acordeón + pestañas + footer).
//
// QUÉ QUEDÓ NATIVO: el contenedor entero (navegación, footer con
// perfil/ancho/unidades, mensajes de estado, botón Guardar) y las pestañas ya
// portadas — hoy "Resumen", "Vehículo › Tipo", "Vehículo › Dimensiones",
// "Vehículo › Antena", "Implemento › Enganche", "Implemento › Distancias" e
// "Implemento › Offset".
// QUÉ SIGUE EN HTML: las pestañas que faltan y los módulos embebidos. El menú
// las abre por WebView (OnRequestHtml), así que el operario llega a TODO desde
// el mismo lugar de siempre. La página config.html no se toca ni se borra: la
// usa la PWA del celular. Es strangler fig, no big-bang.
//
// ---------------------------------------------------------------------------
// CÓMO SE AGREGA UNA PESTAÑA PORTADA (un solo lugar, tres líneas):
//   1. crear `XxxTab : ConfigTab` en Views/ConfigEditor/;
//   2. devolverla en CrearTab() con su clave (la misma que usa el HTML en
//      ?tab=, p. ej. "vdimensions");
//   3. poner Nativa = true en la fila de NAV.
// Mientras Nativa sea false, esa entrada del menú abre el HTML. Nada más hay
// que tocar: ni MainWindow ni el router.
// ---------------------------------------------------------------------------
//
// Contrato de las pestañas (réplica del de config.js):
//   · Rebuild()      arma el árbol (solo al entrar o por acción del operario);
//   · AlEntrarAsync()= enter();
//   · AlSalirAsync() = leave() → false CANCELA la navegación (guardado fallido);
//   · Live()         refresco liviano por tick. Las pestañas CON campos lo
//                    dejan vacío: repintar abajo del dedo tira el foco y cierra
//                    el teclado nativo.
//
// El snapshot se refresca cada 3 s (config, no telemetría). Sirve para que al
// volver de una pestaña abierta en HTML el panel no muestre datos viejos.
// ============================================================================

using System;
using System.Collections.Generic;
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
using PilotX.Desktop.Views.ConfigEditor;

namespace PilotX.Desktop.Views;

public partial class ConfigPanel : UserControl
{
    /// <summary>Una entrada del menú lateral. `Tab` es la MISMA clave que usa
    /// el HTML en ?tab= — así una pestaña sin portar se abre por WebView sin
    /// tabla de traducción de nombres.</summary>
    private sealed class CfgNav
    {
        public string Tab = "";
        public string Titulo = "";
        public string Grupo = "";      // "" = suelto arriba, fuera del acordeón
        public bool Nativa;
    }

    // Mismo orden y mismos grupos que el #menu de config.html. "Pines relay",
    // "Display" y "Botones" no están porque salieron del menú del HTML
    // (pedido 2026-08-03) y se llegan por ?tab= — igual que allá.
    private static readonly CfgNav[] NAV =
    {
        new CfgNav { Tab = "summary",     Titulo = "Resumen",      Grupo = "",           Nativa = true  },

        // "Tipo", no "Tipo y marca": la elección de marca murió el 2026-08-10
        // (el vehículo del mapa es siempre el triángulo verde). El HTML todavía
        // arrastra el rótulo viejo en su menú; acá el nombre dice lo que hay.
        new CfgNav { Tab = "vconfig",     Titulo = "Tipo",         Grupo = "Vehículo",   Nativa = true  },
        new CfgNav { Tab = "vdimensions", Titulo = "Dimensiones",  Grupo = "Vehículo",   Nativa = true  },
        new CfgNav { Tab = "vantenna",    Titulo = "Antena",       Grupo = "Vehículo",   Nativa = true  },

        new CfgNav { Tab = "tconfig",     Titulo = "Enganche",     Grupo = "Implemento", Nativa = true  },
        new CfgNav { Tab = "thitch",      Titulo = "Distancias",   Grupo = "Implemento", Nativa = true  },
        new CfgNav { Tab = "tooloffset",  Titulo = "Offset",       Grupo = "Implemento", Nativa = true  },
        new CfgNav { Tab = "toolpivot",   Titulo = "Pivote",       Grupo = "Implemento"                 },
        new CfgNav { Tab = "tsettings",   Titulo = "Timing",       Grupo = "Implemento"                 },

        new CfgNav { Tab = "tsections",   Titulo = "Secciones",    Grupo = "Secciones"                  },
        new CfgNav { Tab = "tswitches",   Titulo = "Switches",     Grupo = "Secciones"                  },
        new CfgNav { Tab = "amachine",    Titulo = "Máquina",      Grupo = "Secciones"                  },

        new CfgNav { Tab = "heading",     Titulo = "Rumbo",        Grupo = "GPS / IMU"                  },
        new CfgNav { Tab = "roll",        Titulo = "Rolido",       Grupo = "GPS / IMU"                  },

        new CfgNav { Tab = "uturn",       Titulo = "U-Turn",       Grupo = "Otros"                      },
        new CfgNav { Tab = "tram",        Titulo = "Tram",         Grupo = "Otros"                      },
    };

    private readonly CfgCtx _ctx = new CfgCtx();
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, ConfigTab> _tabs = new Dictionary<string, ConfigTab>(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _btns = new Dictionary<string, Button>(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _grupos = new Dictionary<string, Border>(StringComparer.Ordinal);
    private readonly Dictionary<string, StackPanel> _cuerpos = new Dictionary<string, StackPanel>(StringComparer.Ordinal);

    private string _tabActiva = "summary";
    private string _grupoAbierto = "";
    private bool _navegando;

    /// <summary>El operario cerró la configuración.</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Abrir una página del Hub en el WebView (pestaña sin portar o
    /// los módulos). Recibe la ruta relativa; el host cierra este panel.</summary>
    public Action<string>? OnRequestHtml { get; set; }

    /// <summary>Aviso corto → toast del host. Nunca modal.</summary>
    public event Action<string>? Aviso;

    public ConfigPanel()
    {
        InitializeComponent();
        _ctx.Aviso = m => Aviso?.Invoke(m);
        _ctx.Estado = SetEstado;
        _ctx.MarcarSucio = MarcarSucio;
        _ctx.RefrescarSnapshot = RefrescarSnapshotAsync;
        _ctx.AbrirHtml = r => OnRequestHtml?.Invoke(r);
        ArmarMenu();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>¿Esa pestaña ya está portada a nativo? Lo consulta el router
    /// de MainWindow para decidir entre el panel y el WebView.</summary>
    public static bool EsNativa(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return true;          // sin ?tab= aterriza en Resumen
        foreach (var n in NAV) if (n.Tab == tab) return n.Nativa;
        return false;
    }

    // =======================================================================
    //  Ciclo de vida
    // =======================================================================

    /// <summary>Abre la configuración. `tab` cubre los deep-links que en HTML
    /// eran config.html?tab=… — si esa pestaña todavía no está portada se cae
    /// a Resumen (el router debería haber mandado el WebView antes).</summary>
    public void Attach(ConfigVehiculoClient client, string? tab = null)
    {
        _ctx.Client = client;
        string destino = EsNativa(tab) && !string.IsNullOrEmpty(tab) ? tab! : "summary";

        if (_cts != null)
        {
            if (destino != _tabActiva) _ = MostrarTabAsync(destino);
            else PintarMenu();
            return;
        }

        _tabActiva = destino;
        _cts = new CancellationTokenSource();
        _ = ArrancarAsync(_cts.Token);
    }

    /// <summary>Cierra el panel. Igual que el `visibilitychange → hidden` del
    /// HTML: la pestaña activa GUARDA lo que tenga pendiente antes de irse.</summary>
    public void Detach()
    {
        try
        {
            if (_tabs.TryGetValue(_tabActiva, out var t)) _ = t.AlSalirAsync();
        }
        catch { }
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _ = _ctx.Client?.TecladoAsync(false);
    }

    private async Task ArrancarAsync(CancellationToken ct)
    {
        await CargarSnapshotAsync(ct).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PintarCabecera();
                _ = MostrarTabAsync(_tabActiva);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        await RunLoopAsync(ct).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 3 s: esto es configuración, no telemetría. El refresco existe
            // para no mostrar datos viejos al volver de una pestaña HTML.
            try { await Task.Delay(TimeSpan.FromMilliseconds(3000), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await CargarSnapshotAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PintarCabecera();
                if (_tabs.TryGetValue(_tabActiva, out var t)) t.Live();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task CargarSnapshotAsync(CancellationToken ct)
    {
        if (_ctx.Client == null) return;
        var snap = await _ctx.Client.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        // Un null (Hub caído) NO borra lo que ya se sabía: el panel sigue
        // mostrando los últimos valores CON el aviso de sin conexión (lo pinta
        // PintarCabecera con RefrescoCaido), en vez de vaciarse a "—" con cada
        // bache de red. Sin esa marca el panel mostraría números viejos con el
        // punto en verde, como si fueran los de ahora.
        _ctx.RefrescoCaido = snap == null && _ctx.Snap != null;
        if (snap != null || _ctx.Snap == null) _ctx.Snap = snap;
    }

    /// <summary>Re-lee el snapshot y repinta (lo llaman las pestañas después
    /// de guardar: hay guardados con efecto colateral en el motor).</summary>
    private async Task RefrescarSnapshotAsync(CancellationToken ct)
    {
        await CargarSnapshotAsync(ct).ConfigureAwait(false);
        try { await Dispatcher.UIThread.InvokeAsync(PintarCabecera); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // =======================================================================
    //  Cabecera + footer
    // =======================================================================

    private void PintarCabecera()
    {
        var dot   = this.FindControl<Ellipse>("EstadoDot");
        var pill  = this.FindControl<TextBlock>("PerfilPillText");
        var perf  = this.FindControl<TextBlock>("FtPerfil");
        var anc   = this.FindControl<TextBlock>("FtAncho");
        var uni   = this.FindControl<TextBlock>("FtUnidades");

        string perfil = _ctx.Snap?.PerfilActivo ?? "";
        if (string.IsNullOrWhiteSpace(perfil)) perfil = "—";

        IBrush color = _ctx.SinDatos ? CfgUi.Err
                     : (_ctx.ServicioCaido || _ctx.RefrescoCaido) ? CfgUi.Warn
                     : CfgUi.Ok;

        if (dot != null) dot.Fill = color;
        if (pill != null) pill.Text = _ctx.SinDatos
            ? PilotX.Cockpit.Bars.Traductor.T("Sin conexión")
            : perfil;

        string ancho = _ctx.Snap == null ? "—" : _ctx.FmtMedium(_ctx.Snap.Secciones?.ToolWidth);
        string unidades = _ctx.Snap == null
            ? "—"
            : PilotX.Cockpit.Bars.Traductor.T(_ctx.Snap.IsMetric ? "Métrico" : "Imperial");

        if (perf != null) perf.Text = PilotX.Cockpit.Bars.Traductor.T("Perfil") + ": " + perfil;
        if (anc  != null) anc.Text  = PilotX.Cockpit.Bars.Traductor.T("Ancho") + ": " + ancho;
        if (uni  != null) uni.Text  = PilotX.Cockpit.Bars.Traductor.T("Unidades") + ": " + unidades;

        // Los errores de carga se cuentan igual que en config.js. El mensaje se
        // BORRA solo cuando la conexión vuelve: si no, el rojo queda pegado
        // sobre valores que ya son buenos y el operario deja de creerle al
        // footer. Solo se limpia el mensaje que puso este chequeo — un
        // "Guardado ✔" de una pestaña no se pisa.
        if (_ctx.SinDatos)
        {
            SetEstado("Sin conexión con PilotX", "err");
            _estadoDeConexion = true;
        }
        else if (_ctx.ServicioCaido)
        {
            SetEstado("Servicio de configuración no disponible", "err");
            _estadoDeConexion = true;
        }
        else if (_ctx.RefrescoCaido)
        {
            SetEstado("Sin conexión con PilotX — se muestra el último dato leído", "err");
            _estadoDeConexion = true;
        }
        else if (_estadoDeConexion)
        {
            SetEstado("", "");
            _estadoDeConexion = false;
        }
    }

    /// <summary>El mensaje del footer lo puso el chequeo de conexión (y por eso
    /// se puede borrar cuando vuelve).</summary>
    private bool _estadoDeConexion;

    private void SetEstado(string mensaje, string clase)
    {
        // Cualquier mensaje que venga de una pestaña ("Guardado ✔") deja de ser
        // del chequeo de conexión: el tick siguiente no lo tiene que borrar.
        _estadoDeConexion = false;
        var lbl = this.FindControl<TextBlock>("EstadoText");
        if (lbl == null) return;
        lbl.Text = string.IsNullOrEmpty(mensaje) ? "" : PilotX.Cockpit.Bars.Traductor.T(mensaje);
        lbl.Foreground = clase == "ok" ? CfgUi.Ok : clase == "err" ? CfgUi.Err : CfgUi.TextoMuted;
    }

    private void MarcarSucio()
    {
        var b = this.FindControl<Button>("BtnGuardar");
        if (b == null) return;
        if (!_tabs.TryGetValue(_tabActiva, out var t) || !t.TieneGuardar) return;
        b.IsVisible = true;
        b.Content = PilotX.Cockpit.Bars.Traductor.T("Guardar");
    }

    // =======================================================================
    //  Menú lateral (acordeón, como el #menu del HTML)
    // =======================================================================

    private void ArmarMenu()
    {
        var host = this.FindControl<StackPanel>("MenuHost");
        if (host == null) return;
        host.Children.Clear();
        _btns.Clear();
        _grupos.Clear();
        _cuerpos.Clear();

        foreach (var nav in NAV)
        {
            var b = BotonMenu(nav);
            _btns[nav.Tab] = b;

            if (string.IsNullOrEmpty(nav.Grupo))
            {
                // "Resumen" vive suelto arriba, fuera del acordeón (igual que
                // en el HTML: abrirGrupoActivo lo ignora).
                host.Children.Add(b);
                continue;
            }

            if (!_cuerpos.TryGetValue(nav.Grupo, out var cuerpo))
            {
                host.Children.Add(TituloGrupo(nav.Grupo));
                cuerpo = new StackPanel { Spacing = 2, IsVisible = false };
                _cuerpos[nav.Grupo] = cuerpo;
                host.Children.Add(cuerpo);
            }
            cuerpo.Children.Add(b);
        }

        // Puerta al resto de la Configuración que sigue en HTML: módulos X-*,
        // Hub, Campo, Herramientas, Cloud, Mantenimiento y Ayuda. Sin esto, el
        // operario que entra al panel nativo perdería el acceso que hoy tiene.
        host.Children.Add(new Border
        {
            Height = 1, Background = CfgUi.BordeSuave, Margin = new Thickness(4, 10, 4, 8),
        });
        var mas = new Button
        {
            Content = PilotX.Cockpit.Bars.Traductor.T("Módulos y más…"),
            MinHeight = 44, Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent, Foreground = CfgUi.TextoMuted,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            FontSize = 13, Cursor = new Cursor(StandardCursorType.Hand),
        };
        mas.Click += (_, __) => OnRequestHtml?.Invoke("pages/config.html");
        host.Children.Add(mas);

        PintarMenu();
    }

    private Button BotonMenu(CfgNav nav)
    {
        var b = new Button
        {
            Content = PilotX.Cockpit.Bars.Traductor.T(nav.Titulo),
            MinHeight = 44,
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Foreground = CfgUi.Texto,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        string tab = nav.Tab;
        b.Click += (_, __) => _ = IrATabAsync(tab);
        return b;
    }

    private Border TituloGrupo(string grupo)
    {
        var txt = new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T(grupo).ToUpperInvariant(),
            Foreground = CfgUi.TextoMuted, FontSize = 11, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var borde = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = CfgUi.BordeSuave, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 10, 8, 10),
            MinHeight = 40,
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = txt,
        };
        string g = grupo;
        borde.Tapped += (_, __) => AlternarGrupo(g);
        _grupos[grupo] = borde;
        return borde;
    }

    private void AlternarGrupo(string grupo)
    {
        _grupoAbierto = _grupoAbierto == grupo ? "" : grupo;
        PintarMenu();
    }

    private void PintarMenu()
    {
        foreach (var kv in _btns)
        {
            bool activa = kv.Key == _tabActiva;
            kv.Value.Background = activa ? CfgUi.BgFilaSel : Brushes.Transparent;
            kv.Value.Foreground = activa ? CfgUi.Texto : CfgUi.TextoMuted;
            kv.Value.FontWeight = activa ? FontWeight.Bold : FontWeight.SemiBold;
        }
        foreach (var kv in _cuerpos)
            kv.Value.IsVisible = kv.Key == _grupoAbierto;
        foreach (var kv in _grupos)
            ((TextBlock)kv.Value.Child!).Foreground =
                kv.Key == _grupoAbierto ? CfgUi.Texto : CfgUi.TextoMuted;
    }

    // =======================================================================
    //  Navegación entre pestañas (irATab del HTML)
    // =======================================================================

    private async Task IrATabAsync(string tab)
    {
        if (_navegando) return;
        if (tab == _tabActiva) return;
        _navegando = true;
        try
        {
            // leave() de la pestaña actual: si el guardado falla NO se navega
            // (el operario se queda donde estaba, con el error a la vista).
            if (_tabs.TryGetValue(_tabActiva, out var vieja))
            {
                bool ok;
                try { ok = await vieja.AlSalirAsync().ConfigureAwait(true); }
                catch { ok = false; }
                if (!ok) return;
            }

            if (!EsNativa(tab))
            {
                // Todavía no portada: se abre la misma pestaña en el HTML, con
                // el deep-link que la página ya entiende.
                _ = _ctx.Client?.TecladoAsync(false);
                OnRequestHtml?.Invoke("pages/config.html?tab=" + tab);
                return;
            }

            await MostrarTabAsync(tab).ConfigureAwait(true);
        }
        finally { _navegando = false; }
    }

    private async Task MostrarTabAsync(string tab)
    {
        _ = _ctx.Client?.TecladoAsync(false);
        _tabActiva = tab;

        // Dejar abierto el grupo de la pestaña activa (abrirGrupoActivo del HTML).
        foreach (var n in NAV)
            if (n.Tab == tab && !string.IsNullOrEmpty(n.Grupo)) { _grupoAbierto = n.Grupo; break; }
        PintarMenu();

        if (!_tabs.TryGetValue(tab, out var vista))
        {
            vista = CrearTab(tab);
            _tabs[tab] = vista;
        }

        var host = this.FindControl<StackPanel>("TabHost");
        if (host != null)
        {
            host.Children.Clear();
            host.Children.Add(vista);
        }

        var btn = this.FindControl<Button>("BtnGuardar");
        if (btn != null) btn.IsVisible = vista.TieneGuardar;

        vista.Rebuild();

        // Traductor.Aplicar CACHEA el texto original de cada control la primera
        // vez que lo ve y en las pasadas siguientes vuelve a escribir ESE texto.
        // Por eso todo lo que se pinta a mano (subtítulo, estado, footer) va
        // DESPUÉS: si se pintara antes, el próximo cambio de pestaña restauraría
        // el valor de la primera vez (el subtítulo se quedaría clavado en
        // "Resumen" y el footer, con el ancho viejo).
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);

        var sub = this.FindControl<TextBlock>("SubtituloText");
        if (sub != null) sub.Text = PilotX.Cockpit.Bars.Traductor.T(TituloDe(tab));

        SetEstado("", "");
        try { await vista.AlEntrarAsync().ConfigureAwait(true); } catch { }
        PintarCabecera();
    }

    private static string TituloDe(string tab)
    {
        foreach (var n in NAV) if (n.Tab == tab) return n.Titulo;
        return "Configuración";
    }

    /// <summary>Fábrica de pestañas nativas. Cada porteo agrega su case acá
    /// (y pone Nativa = true en NAV).</summary>
    private ConfigTab CrearTab(string tab) => tab switch
    {
        "vconfig" => new VehiculoTab(_ctx),
        "vdimensions" => new DimensionesTab(_ctx),
        "vantenna" => new AntenaTab(_ctx),
        "tconfig" => new EngancheTab(_ctx),
        "thitch" => new DistanciasTab(_ctx),
        "tooloffset" => new OffsetTab(_ctx),
        _ => new ResumenTab(_ctx),
    };

    // =======================================================================
    //  Botones del shell
    // =======================================================================

    private void OnCerrarClick(object? s, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    private async void OnGuardarClick(object? s, RoutedEventArgs e)
    {
        if (!_tabs.TryGetValue(_tabActiva, out var t) || !t.TieneGuardar) return;
        // Sin cambios no hay POST: el "Guardado ✔" de abajo sería mentira (es
        // el quirk del botón flotante de config.html, que acá no se replica).
        if (!t.HayCambios) { SetEstado("Sin cambios", ""); return; }
        SetEstado("Guardando…", "");
        bool ok;
        try { ok = await t.AlSalirAsync().ConfigureAwait(true); }
        catch { ok = false; }
        if (!ok) return;                      // la pestaña ya puso su error
        // Resincroniza con lo persistido, igual que el botón flotante del HTML.
        try { await t.AlEntrarAsync().ConfigureAwait(true); } catch { }
        SetEstado("Guardado ✔", "ok");
        PintarCabecera();
    }
}
