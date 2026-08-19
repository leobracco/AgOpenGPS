// ============================================================================
// ConfigPanel.axaml.cs — shell nativo de la Configuración (porteo de
// pages/config.html: navegación por TABS en dos filas + pestañas + footer).
//
// Desde el 2026-08-18 la navegación ya NO es el menú lateral en acordeón:
// son dos filas de tabs horizontales arriba del contenido — fila 1 los grupos
// del NAV (Resumen … Mantenimiento), fila 2 las entradas del grupo activo,
// fusionada con la barra de contexto (las pills de módulo siguen a la derecha,
// siempre en el mismo lugar). Tocar un grupo abre su última entrada visitada
// (o la primera). El NAV, IrATabAsync y toda la semántica de guardado quedan
// tal cual: solo cambió el widget que los pinta.
//
// QUÉ QUEDÓ NATIVO: el contenedor entero (navegación, footer con
// perfil/ancho/unidades, mensajes de estado, botón Guardar) y las pestañas ya
// portadas — hoy "Resumen", "Vehículo › Tipo", "Vehículo › Dimensiones",
// "Vehículo › Antena", "Implemento › Enganche", "Implemento › Distancias",
// "Implemento › Offset", "Implemento › Pivote", "Implemento › Timing" y
// "Secciones › Secciones" (esta última toca ADEMÁS el implemento central por
// /api/implemento, para los trenes de siembra), "Secciones › Switches" y
// "Secciones › Máquina" (la única portada que NO guarda al salir: manda un PGN
// 238 al módulo con su propio botón "Enviar + Guardar" y descarta si se cierra
// sin enviar — ver la cabecera de MaquinaTab) y "GPS / IMU › Rumbo" (la única
// con guardado MIXTO: el tipo de antena y el paso mínimo postean AL TOQUE, como
// el original; el resto va por el botón Guardar — ver la cabecera de RumboTab) y
// "GPS / IMU › Rolido" (TRES semánticas de guardado a la vez y el único poll
// propio del panel: el tractor en vivo a 500 ms — ver la cabecera de RolidoTab)
// y "Otros › U-Turn" (geometría del giro de cabecera; su POST reconstruye las
// líneas de giro y descarta el U-turn ya dibujado — ver la cabecera de UturnTab)
// y "Otros › Tram" (ancho de trocha + las dos preferencias de trochas; la
// CONSTRUCCIÓN de las huellas sobre el lote sigue en pages/tramline(s).html —
// ver la cabecera de TramTab).
// y — desde el 2026-08-17 — los MÓDULOS, que viven en las MISMAS tabs de
// navegación con los grupos y el orden del original (Otros › Sonidos, Módulos,
// Campo, Herramientas, Cloud, Mantenimiento): ya no hay grilla intermedia "Módulos"
// (ModulosTab se eliminó — en el original ese paso no existía y obligaba a
// 3-4 toques para algo que estaba a 2). Tocar un módulo muestra su contenido
// EN EL ÁREA DE CONTENIDO de esta misma tarjeta: si tiene panel nativo se
// monta una INSTANCIA PROPIA acá adentro (patrón CamarasPanel de la ventana
// de cámaras: nunca se reparenta el overlay vivo de MainWindow — reparentar
// controles vivos es frágil en Avalonia), con Attach al entrar y Detach al
// salir (el Detach de QuantiX PARA MOTORES: no puede quedar sin llamar);
// si no tiene panel nativo, se muestra la página del Hub embebida (WebView).
// Los overlays sueltos de MainWindow (ShowQuantiXEditor y compañía) SIGUEN
// EXISTIENDO para los caminos directos (botón Configurar del monitor, Hub…);
// esto solo cambia cómo llega el operario DESDE la Configuración.
// QUÉ SIGUE EN HTML: desde la ola 3c, ninguna PESTAÑA DE CONFIG — las 16 son
// nativas. Al WebView (embebido acá adentro) salen los módulos sin panel
// nativo (LineX, Insumos, Mapas, Lab PID, Diagnóstico PWM,
// OrbitX, Conectar celular,
// Red WiFi, Debug y Ayuda) y las tres pestañas HUÉRFANAS de
// config.html —`relay`, `display`
// y `botones`—, que no están en el NAV porque tampoco están en el menú del
// HTML (las sacaron el 2026-08-03) y hoy NO las emite ningún botón ni ruta de
// la UI. Ojo antes de darlas por muertas: `relay` es el mapa de pines que viaja
// en el PGN al módulo de máquina, y `display` tiene el ÚNICO conmutador
// métrico/imperial del producto, del que dependen los límites y las unidades de
// 12 pestañas de acá. Ver docs/MIGRACION-OLA3C.md §3.
// El fallback de IrATabAsync se deja INTACTO igual: es la red para el próximo
// porteo y para cualquier deep-link viejo.
// La página config.html no se toca ni se borra: la usa la PWA del celular. Es
// strangler fig, no big-bang.
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
    /// <summary>Una entrada del menú lateral. Para las pestañas de config,
    /// `Tab` es la MISMA clave que usa el HTML en ?tab= — así una pestaña sin
    /// portar se abre por WebView sin tabla de traducción de nombres. Para los
    /// módulos, `Tab` es una clave interna ("mod_…", nunca viaja en ?tab=) y
    /// UNO de ModClave/ModRuta dice qué se muestra en el área de contenido:
    /// ModClave = panel nativo montado acá adentro (instancia propia);
    /// ModRuta  = página del Hub embebida en el WebView.</summary>
    private sealed class CfgNav
    {
        public string Tab = "";
        public string Titulo = "";
        public string Grupo = "";      // "" = suelto arriba, fuera del acordeón
        public bool Nativa;
        public string? ModClave;       // módulo con panel nativo embebido
        public string? ModRuta;        // módulo solo-HTML (página del Hub)
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
        new CfgNav { Tab = "toolpivot",   Titulo = "Pivote",       Grupo = "Implemento", Nativa = true  },
        new CfgNav { Tab = "tsettings",   Titulo = "Timing",       Grupo = "Implemento", Nativa = true  },

        new CfgNav { Tab = "tsections",   Titulo = "Secciones",    Grupo = "Secciones",  Nativa = true  },
        new CfgNav { Tab = "tswitches",   Titulo = "Switches",     Grupo = "Secciones",  Nativa = true  },
        new CfgNav { Tab = "amachine",    Titulo = "Máquina",      Grupo = "Secciones",  Nativa = true  },

        new CfgNav { Tab = "heading",     Titulo = "Rumbo",        Grupo = "GPS / IMU",  Nativa = true  },
        new CfgNav { Tab = "roll",        Titulo = "Rolido",       Grupo = "GPS / IMU",  Nativa = true  },

        new CfgNav { Tab = "uturn",       Titulo = "U-Turn",       Grupo = "Otros",      Nativa = true  },
        new CfgNav { Tab = "tram",        Titulo = "Tram",         Grupo = "Otros",      Nativa = true  },
        // Sonidos vive en "Otros" porque ahí lo tiene el menú del original
        // (config.html): el operario lo busca donde siempre estuvo.
        new CfgNav { Tab = "mod_sonidos", Titulo = "Sonidos",      Grupo = "Otros",      ModClave = "sonidos" },

        // ---- Módulos (mismo orden que el #menu del original; CoreX-ECU no
        //      estaba en el HTML y se intercala antes de Nodos) -------------
        new CfgNav { Tab = "mod_hub",      Titulo = "Hub",      Grupo = "Módulos", ModClave = "hub" },
        new CfgNav { Tab = "mod_quantix",  Titulo = "QuantiX",  Grupo = "Módulos", ModClave = "quantix" },
        new CfgNav { Tab = "mod_vistax",   Titulo = "VistaX",   Grupo = "Módulos", ModClave = "vistax" },
        new CfgNav { Tab = "mod_flowx",    Titulo = "FlowX",    Grupo = "Módulos", ModClave = "flowx" },
        new CfgNav { Tab = "mod_sectionx", Titulo = "SectionX", Grupo = "Módulos", ModClave = "sectionx" },
        // LineX no tiene panel nativo: se dice así, no se disfraza.
        new CfgNav { Tab = "mod_linex",    Titulo = "LineX",    Grupo = "Módulos", ModRuta = "pages/linex.html" },
        new CfgNav { Tab = "mod_stormx",   Titulo = "StormX",   Grupo = "Módulos", ModClave = "stormx" },
        new CfgNav { Tab = "mod_corex_ecu",Titulo = "CoreX-ECU",Grupo = "Módulos", ModClave = "corex_ecu" },
        new CfgNav { Tab = "mod_nodos",    Titulo = "Nodos",    Grupo = "Módulos", ModClave = "nodos" },
        new CfgNav { Tab = "mod_camaras",  Titulo = "Cámaras",  Grupo = "Módulos", ModClave = "camaras" },

        // ---- Campo --------------------------------------------------------
        new CfgNav { Tab = "mod_insumos", Titulo = "Insumos", Grupo = "Campo", ModClave = "insumos" },
        new CfgNav { Tab = "mod_mapas",   Titulo = "Mapas",   Grupo = "Campo", ModClave = "mapas" },
        // "Prescripciones" es la tab Shape del editor de QuantiX (el viejo
        // quantix.html?tab=shape), que ya es nativa: misma instancia que
        // "QuantiX" pero aterrizando en Shape.
        new CfgNav { Tab = "mod_prescripciones", Titulo = "Prescripciones", Grupo = "Campo", ModClave = "prescripciones" },

        // ---- Herramientas -------------------------------------------------
        new CfgNav { Tab = "mod_calculadora", Titulo = "Calculadora",     Grupo = "Herramientas", ModClave = "calculadora" },
        new CfgNav { Tab = "mod_pidlab",      Titulo = "Lab PID",         Grupo = "Herramientas", ModRuta = "pages/pid-lab.html" },
        new CfgNav { Tab = "mod_pwmdiag",     Titulo = "Diagnóstico PWM", Grupo = "Herramientas", ModRuta = "pages/pwm-diag.html" },

        // ---- Cloud --------------------------------------------------------
        new CfgNav { Tab = "mod_orbitx",     Titulo = "OrbitX",           Grupo = "Cloud", ModClave = "orbitx" },
        new CfgNav { Tab = "mod_firmwares",  Titulo = "Firmwares",        Grupo = "Cloud", ModClave = "firmwares" },
        new CfgNav { Tab = "mod_actualizar", Titulo = "Actualizar",       Grupo = "Cloud", ModClave = "actualizar" },
        new CfgNav { Tab = "mod_pwa",        Titulo = "Conectar celular", Grupo = "Cloud", ModRuta = "pages/pwa-qr.html" },

        // ---- Mantenimiento ------------------------------------------------
        new CfgNav { Tab = "mod_wifi",    Titulo = "Red WiFi", Grupo = "Mantenimiento", ModClave = "wifi" },
        new CfgNav { Tab = "mod_sistema", Titulo = "Sistema",  Grupo = "Mantenimiento", ModClave = "sistema" },
        new CfgNav { Tab = "mod_eventos", Titulo = "Eventos",  Grupo = "Mantenimiento", ModClave = "eventos" },
        new CfgNav { Tab = "mod_debug",   Titulo = "Debug",    Grupo = "Mantenimiento", ModClave = "debug" },
        new CfgNav { Tab = "mod_ayuda",   Titulo = "Ayuda",    Grupo = "Mantenimiento", ModRuta = "pages/ayuda.html" },
    };

    private readonly CfgCtx _ctx = new CfgCtx();
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, ConfigTab> _tabs = new Dictionary<string, ConfigTab>(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _btns = new Dictionary<string, Button>(StringComparer.Ordinal);

    /// <summary>Un grupo de la fila 1 de tabs. Para las entradas sueltas del
    /// NAV (Resumen) el grupo es la entrada misma: su tab abre directo y la
    /// fila 2 queda vacía (pero con el alto reservado).</summary>
    private sealed class GrupoCfg
    {
        public string Clave = "";
        public string Titulo = "";
        public readonly List<CfgNav> Entradas = new List<CfgNav>();
    }

    private readonly List<GrupoCfg> _gruposNav = new List<GrupoCfg>();
    private readonly Dictionary<string, Button> _btnsGrupo = new Dictionary<string, Button>(StringComparer.Ordinal);
    // Última entrada visitada de cada grupo (solo en memoria): re-tocar el
    // grupo vuelve ahí, no siempre a la primera. No se persiste a propósito.
    private readonly Dictionary<string, string> _ultimaDeGrupo = new Dictionary<string, string>(StringComparer.Ordinal);
    private string _grupoPintado = "";  // grupo cuyas entradas cuelgan en EntradasHost

    private string _tabActiva = "summary";
    private bool _navegando;

    // ── Módulo HTML embebido (ver el comentario del Grid en el .axaml) ──
    // El WebView vive ADENTRO de esta tarjeta, no a pantalla completa. Se crea
    // recién cuando el operario entra a un módulo (lazy: Chromium son ~260 MB)
    // y se vacía —no se destruye— al volver a una pestaña nativa o al cerrar,
    // así la próxima apertura no paga el arranque del motor de nuevo.
    private Services.IWebViewHandle? _web;
    private bool _htmlVisible;

    // ── Módulos con PANEL NATIVO embebido (Hub, QuantiX, VistaX, …) ──
    // Cada uno es una INSTANCIA PROPIA montada en ModuloNativoHost (patrón
    // CamarasPanel de la ventana de cámaras: nunca se reparenta el overlay
    // vivo de MainWindow). Se crean lazy la primera vez que el operario entra
    // y se cachean; el ciclo de vida es Attach al entrar / Detach al salir
    // (cambiar de entrada, ✕ del shell, Detach externo). El Detach de QuantiX
    // PARA MOTORES: por eso OcultarModuloNativo se llama en TODOS los caminos
    // de salida, no solo en la navegación feliz.
    private readonly Dictionary<string, Control> _modPaneles = new Dictionary<string, Control>(StringComparer.Ordinal);
    // Pills/acciones que cada panel embebido entregó (PillsDeContexto) para la
    // barra de contexto del shell. Se cachean junto con el panel: al volver a
    // entrar se re-cuelgan en ContextoPills sin rearmar nada.
    private readonly Dictionary<string, Control?> _modPills = new Dictionary<string, Control?>(StringComparer.Ordinal);
    private string _modActivo = "";     // clave del panel embebido visible ("" = ninguno)
    private string _modNavActual = "";  // Tab del NAV que lo mostró (quantix ≠ prescripciones)

    // Clientes HTTP de los módulos, lazy y cacheados (mismo criterio que los
    // lazy-init de MainWindow: si el operario nunca entra, cero costo).
    private NodosClient?         _nodosCli;
    private OverlaysClient?      _overlaysCli;
    private QuantiXEditorClient? _quantiXCli;
    private VistaXClient?        _vistaXCli;
    private FlowXClient?         _flowXCli;
    private SectionXClient?      _sectionXCli;
    private StormXClient?        _stormXCli;
    private CoreXEcuClient?      _ecuCli;
    private UpdateClient?        _updateCli;
    private SistemaClient?       _sistemaCli;
    private SonidosClient?       _sonidosCli;
    private CamarasClient?       _camarasCli;
    private CalculadoraSiembraClient? _calculadoraCli;
    private FirmwaresClient?     _firmwaresCli;
    private EventosClient?       _eventosCli;
    private InsumosClient?       _insumosCli;
    private MapasClient?         _mapasCli;
    private OrbitXPanelClient?   _orbitXCli;
    private RedWifiClient?       _wifiCli;
    private DebugClient?         _debugCli;
    private NodoDetalleClient?   _nodoDetalleCli;

    // WebView de la pestaña "Configurar" del CoreX-ECU embebido (el panel pide
    // un slot por OnConfigOpen; réplica local de AbrirEcuConfig de MainWindow).
    private Services.IWebViewHandle? _ecuWeb;

    // ── Módulo pedido DESDE AFUERA antes de que el panel termine de arrancar ──
    // AbrirModuloHtml puede llegar recién hecho el Attach (ShowConfigModulo en
    // MainWindow hace ShowConfig() + AbrirModuloHtml en la misma pasada). En ese
    // momento ArrancarAsync todavía no corrió su MostrarTabAsync inicial — si el
    // módulo se mostrara ya, ese MostrarTabAsync diferido lo taparía con la
    // pestaña nativa. Se guarda acá y lo consume ArrancarAsync en su lugar.
    private (string Ruta, string Subtitulo)? _moduloPendiente;
    private bool _arranqueListo;

    /// <summary>El operario cerró la configuración.</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Abrir una página del Hub en el WebView (pestaña sin portar o
    /// los módulos). Recibe la ruta relativa; el host cierra este panel.</summary>
    public Action<string>? OnRequestHtml { get; set; }

    /// <summary>Pedir a MainWindow un PANEL NATIVO por su clave ("hub",
    /// "quantix", …). Desde 2026-08-17 la Configuración ya NO lo usa (los
    /// módulos se montan embebidos acá adentro), pero la puerta queda cableada
    /// por si algún camino externo la necesita.</summary>
    public Action<string>? OnRequestPanelNativo { get; set; }

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
        _ctx.AbrirHtmlEmbebido = (r, t) => MostrarHtmlEmbebido(r, PilotX.Cockpit.Bars.Traductor.T(t));
        _ctx.AbrirPanelNativo = c => OnRequestPanelNativo?.Invoke(c);
        ArmarTabs();
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
        // Se vuelve a abrir: lo que se haya frenado por el cierre anterior
        // (p. ej. el poll del tractor en vivo de Rolido) puede volver a andar.
        _ctx.Cerrando = false;
        // Cliente del implemento CENTRAL (/api/implemento): OTRA configuración,
        // el mismo host. Hoy lo usa nada más que la carta de trenes de la
        // pestaña Secciones, pero vive acá para que no haya dos clientes del
        // mismo producto dando vueltas.
        if (_ctx.Implemento == null && client != null)
            _ctx.Implemento = new ImplementoClient(client.BaseUrl);
        string destino = EsNativa(tab) && !string.IsNullOrEmpty(tab) ? tab! : "summary";

        if (_cts != null)
        {
            if (destino != _tabActiva) _ = MostrarTabAsync(destino);
            else PintarTabs();
            return;
        }

        _tabActiva = destino;
        _arranqueListo = false;
        _cts = new CancellationTokenSource();
        _ = ArrancarAsync(_cts.Token);
    }

    /// <summary>
    /// Abre la Configuración PARADA en un módulo HTML embebido (misma tarjeta,
    /// mismo ✕, mismas tabs arriba). Es la puerta que usa MainWindow para
    /// los "Configurar" de los paneles (SectionX, Nodos, Insumos, Firmwares…):
    /// antes cada uno abría su propia ventana-diálogo suelta.
    /// Llamar SIEMPRE después de Attach (ShowConfig ya lo garantiza): si el
    /// arranque inicial todavía está en vuelo, el pedido queda pendiente y lo
    /// muestra ArrancarAsync — mostrarlo ya sería taparlo un instante después.
    /// La página aterriza con SU entrada del menú marcada activa (la que
    /// corresponde por ruta: nodo-detalle → Nodos, ?mod=camaras → Cámaras…):
    /// tocar esa entrada de nuevo vuelve a su vista principal, y cualquier
    /// otra entrada navega normal.
    /// </summary>
    public void AbrirModuloHtml(string ruta, string subtitulo)
    {
        var nav = NavDeRuta(ruta);
        _tabActiva = nav?.Tab ?? "";
        PintarTabs();
        string sub = PilotX.Cockpit.Bars.Traductor.T(subtitulo);
        if (!_arranqueListo)
        {
            _moduloPendiente = (ruta, sub);
            return;
        }
        MostrarHtmlEmbebido(ruta, sub);
    }

    /// <summary>Cierra el panel. Igual que el `visibilitychange → hidden` del
    /// HTML: la pestaña activa GUARDA lo que tenga pendiente antes de irse.</summary>
    public void Detach()
    {
        // ANTES del AlSalirAsync, que va sin await: si su guardado falla, la
        // pestaña se entera con el panel ya oculto y no tiene que rearmar nada
        // de fondo (ver CfgCtx.Cerrando).
        _ctx.Cerrando = true;
        // Un módulo pedido que no llegó a mostrarse muere con el cierre: si el
        // dispatcher del arranque corre después de este Detach, no tiene que
        // levantar un WebView sobre un panel ya oculto (airspace sobre el mapa).
        _moduloPendiente = null;
        // El módulo HTML sale de la vista por cualquier camino de cierre (✕,
        // otro panel que se abre encima, apagado): un WebView2 con página
        // cargada sigue pintando sobre el mapa aunque el panel esté oculto.
        OcultarHtmlEmbebido();
        // Y el módulo NATIVO embebido igual: su Detach para polls y — en el
        // caso de QuantiX — cualquier motor girando. Este es uno de los
        // caminos de salida que NO pasan por la navegación del menú.
        OcultarModuloNativo();
        try
        {
            if (_tabs.TryGetValue(_tabActiva, out var t)) _ = t.AlSalirAsync();
        }
        catch { }
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _ = _ctx.Client?.TecladoAsync(false);
    }

    /// <summary>Cierre de la APLICACIÓN con un módulo embebido activo. El caso
    /// que importa es QuantiX: su Detach normal dispara el STOP y lo suelta,
    /// y en el apagado el proceso muere antes de que ese STOP salga al cable —
    /// el motor queda girando con la pantalla apagada. Acá se ESPERA (hasta
    /// 1,5 s), espejo de DetenerEditorQuantiXAlApagar de MainWindow para el
    /// overlay suelto. Idempotente: si la Config ya se cerró, no hace nada.</summary>
    public void DetenerModulosAlApagar()
    {
        if (_modActivo != "quantix") return;
        _modActivo = "";
        _modNavActual = "";
        if (_modPaneles.TryGetValue("quantix", out var p))
            try { ((QuantiXEditorPanel)p).DetachAsync().Wait(1500); } catch { }
    }

    private async Task ArrancarAsync(CancellationToken ct)
    {
        await CargarSnapshotAsync(ct).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PintarCabecera();
                _arranqueListo = true;
                if (_moduloPendiente != null)
                {
                    // Se abrió con AbrirModuloHtml antes de terminar el
                    // arranque: se muestra ese módulo en lugar de la pestaña
                    // nativa. Traductor.Aplicar va ANTES del subtítulo por el
                    // mismo motivo que en MostrarTabAsync (cachea el primer
                    // texto que ve y lo restauraría en la próxima pasada).
                    var (ruta, sub) = _moduloPendiente.Value;
                    _moduloPendiente = null;
                    PilotX.Cockpit.Bars.Traductor.Aplicar(this);
                    PintarTabs();
                    MostrarHtmlEmbebido(ruta, sub);
                }
                else
                {
                    _ = MostrarTabAsync(_tabActiva);
                }
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
    //  Tabs de navegación (dos filas: grupos arriba, entradas en la barra de
    //  contexto — reemplazo del menú lateral en acordeón, 2026-08-18)
    // =======================================================================

    private void ArmarTabs()
    {
        var hostGrupos = this.FindControl<StackPanel>("GruposHost");
        if (hostGrupos == null) return;
        hostGrupos.Children.Clear();
        _btns.Clear();
        _btnsGrupo.Clear();
        _gruposNav.Clear();

        // Mismos grupos y mismo orden que el NAV. Las entradas sueltas
        // (Resumen) son un grupo de una sola entrada: su tab de fila 1 abre
        // directo y la fila 2 queda vacía, con el alto reservado.
        foreach (var nav in NAV)
        {
            string clave = string.IsNullOrEmpty(nav.Grupo) ? nav.Tab : nav.Grupo;
            var grupo = _gruposNav.Find(g => g.Clave == clave);
            if (grupo == null)
            {
                grupo = new GrupoCfg
                {
                    Clave = clave,
                    Titulo = string.IsNullOrEmpty(nav.Grupo) ? nav.Titulo : nav.Grupo,
                };
                _gruposNav.Add(grupo);
                var bg = BotonGrupo(grupo);
                _btnsGrupo[clave] = bg;
                hostGrupos.Children.Add(bg);
            }
            grupo.Entradas.Add(nav);
            _btns[nav.Tab] = BotonEntrada(nav);
        }

        // Ya no hay fila "Módulos" ni grilla intermedia: los módulos son
        // entradas directas de sus grupos (Módulos, Campo, Herramientas,
        // Cloud y Mantenimiento, más Sonidos en Otros), igual que en el menú
        // del original — un grupo y un toque, no una pantalla en el medio.
        PintarTabs();
    }

    /// <summary>Tab de la fila 1 (un grupo). Compacto para que los 11 entren
    /// enteros en 976 px de card; el activo se marca con fondo suave verde +
    /// subrayado de acento (peso tipográfico constante: cambiar a bold movería
    /// los anchos y la fila entera bailaría al navegar).</summary>
    private Button BotonGrupo(GrupoCfg grupo)
    {
        var b = new Button
        {
            Content = PilotX.Cockpit.Bars.Traductor.T(grupo.Titulo),
            MinHeight = 44,
            Padding = new Thickness(10, 4, 10, 4),
            Background = Brushes.Transparent,
            Foreground = CfgUi.TextoMuted,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        string clave = grupo.Clave;
        b.Click += (_, __) => AlTocarGrupo(clave);
        return b;
    }

    /// <summary>Pill de la fila 2 (una entrada del grupo activo). Mismo alto
    /// táctil que la fila 1, estilo más liviano.</summary>
    private Button BotonEntrada(CfgNav nav)
    {
        var b = new Button
        {
            Content = PilotX.Cockpit.Bars.Traductor.T(nav.Titulo),
            MinHeight = 44,
            Padding = new Thickness(12, 4, 12, 4),
            Background = Brushes.Transparent,
            Foreground = CfgUi.TextoMuted,
            BorderBrush = CfgUi.BordeSuave,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        string tab = nav.Tab;
        b.Click += (_, __) => _ = IrATabAsync(tab);
        return b;
    }

    /// <summary>Tocar un grupo abre su última entrada visitada (o la primera).
    /// La navegación real pasa por IrATabAsync: si el guardado de la pestaña
    /// actual falla, no se navega y las tabs no cambian de marca.</summary>
    private void AlTocarGrupo(string clave)
    {
        var grupo = _gruposNav.Find(g => g.Clave == clave);
        if (grupo == null || grupo.Entradas.Count == 0) return;
        string destino = _ultimaDeGrupo.TryGetValue(clave, out var ult) && BuscarNav(ult) != null
            ? ult
            : grupo.Entradas[0].Tab;
        _ = IrATabAsync(destino);
    }

    /// <summary>Grupo (de la fila 1) al que pertenece una tab. "" = ninguna
    /// fila marcada (p. ej. una página satélite sin entrada propia).</summary>
    private static string GrupoClaveDe(string tab)
    {
        var n = BuscarNav(tab);
        if (n == null) return "";
        return string.IsNullOrEmpty(n.Grupo) ? n.Tab : n.Grupo;
    }

    /// <summary>Repinta las dos filas de tabs según la entrada activa: marca
    /// el grupo en la fila 1, cuelga sus entradas en la fila 2 (vacía si el
    /// grupo es directo, tipo Resumen — el alto lo reserva la barra) y marca
    /// la entrada activa. También anota la última visitada del grupo.</summary>
    private void PintarTabs()
    {
        string grupoActivo = GrupoClaveDe(_tabActiva);
        if (grupoActivo.Length != 0) _ultimaDeGrupo[grupoActivo] = _tabActiva;

        foreach (var g in _gruposNav)
        {
            if (!_btnsGrupo.TryGetValue(g.Clave, out var b)) continue;
            bool activo = g.Clave == grupoActivo;
            b.Background = activo ? CfgUi.BgFilaSel : Brushes.Transparent;
            b.BorderBrush = activo ? CfgUi.Verde : Brushes.Transparent;
            b.Foreground = activo ? CfgUi.Texto : CfgUi.TextoMuted;
        }

        // Fila 2: solo se rearma cuando cambia el grupo (una página satélite
        // sin fila propia deja la última fila 2 a la vista, con nada marcado).
        string aPintar = grupoActivo.Length != 0 ? grupoActivo : _grupoPintado;
        if (aPintar != _grupoPintado)
        {
            var host = this.FindControl<StackPanel>("EntradasHost");
            if (host != null)
            {
                host.Children.Clear();
                var g = _gruposNav.Find(x => x.Clave == aPintar);
                if (g != null && g.Entradas.Count > 1)
                    foreach (var e in g.Entradas)
                        if (_btns.TryGetValue(e.Tab, out var be))
                            host.Children.Add(be);
            }
            _grupoPintado = aPintar;
        }

        foreach (var kv in _btns)
        {
            bool activa = kv.Key == _tabActiva;
            kv.Value.Background = activa ? CfgUi.BgFilaSel : Brushes.Transparent;
            kv.Value.BorderBrush = activa ? CfgUi.Verde : CfgUi.BordeSuave;
            kv.Value.Foreground = activa ? CfgUi.Texto : CfgUi.TextoMuted;
        }
    }

    /// <summary>Subtítulo de la barra de contexto. Con las tabs a la vista el
    /// nombre de la entrada activa ya está marcado en la fila 2: el subtítulo
    /// solo se muestra cuando dice algo DISTINTO (páginas satélite tipo
    /// "Nodos — Detalle del nodo").</summary>
    private void SetSubtitulo(string texto)
    {
        var st = this.FindControl<TextBlock>("SubtituloText");
        if (st == null) return;
        st.Text = texto;
        st.IsVisible = !string.Equals(
            texto, PilotX.Cockpit.Bars.Traductor.T(TituloDe(_tabActiva)), StringComparison.Ordinal);
    }

    // =======================================================================
    //  Navegación entre pestañas (irATab del HTML)
    // =======================================================================

    private async Task IrATabAsync(string tab)
    {
        if (_navegando) return;
        var nav = BuscarNav(tab);
        bool esModulo = nav != null && (nav.ModClave != null || nav.ModRuta != null);
        if (tab == _tabActiva)
        {
            // Ya estamos parados en esa fila.
            if (esModulo)
            {
                // Re-tocar la entrada de un módulo vuelve a su vista
                // PRINCIPAL si lo que se ve es otra cosa: p. ej. SectionX
                // mostrando su "Configurar" HTML, o Nodos mostrando el
                // detalle de un nodo. Si ya se ve lo principal, no-op (como
                // el iframe del original, que no recargaba).
                bool yaSeVe = nav!.ModClave != null
                    ? (_modActivo == ClavePanelDe(nav) && _modNavActual == nav.Tab && !_htmlVisible)
                    : _htmlVisible;
                if (yaSeVe) return;
                _navegando = true;
                try { MostrarModulo(nav); }
                finally { _navegando = false; }
                return;
            }
            // Pestaña de config mostrando un módulo HTML embebido (caso
            // legado): este toque significa "volver" a lo nativo.
            if (!_htmlVisible) return;
            _navegando = true;
            try { await MostrarTabAsync(tab).ConfigureAwait(true); }
            finally { _navegando = false; }
            return;
        }
        _navegando = true;
        try
        {
            // leave() de la pestaña actual: si el guardado falla NO se navega
            // (el operario se queda donde estaba, con el error a la vista).
            // Las entradas de módulo no están en _tabs, así que venir de un
            // módulo no dispara ningún leave — cada módulo guarda lo suyo.
            if (_tabs.TryGetValue(_tabActiva, out var vieja))
            {
                bool ok;
                try { ok = await vieja.AlSalirAsync().ConfigureAwait(true); }
                catch { ok = false; }
                if (!ok) return;
            }

            if (esModulo)
            {
                MostrarModulo(nav!);
                return;
            }

            if (!EsNativa(tab))
            {
                // Todavía no portada: se abre la misma pestaña en el HTML con el
                // deep-link que la página ya entiende, pero ADENTRO de esta
                // tarjeta — antes esto cerraba el panel y se iba a pantalla
                // completa, dejando al operario sin ✕ ni menú.
                _ = _ctx.Client?.TecladoAsync(false);
                string titulo = tab;
                foreach (var n in NAV) if (n.Tab == tab) { titulo = n.Titulo; break; }
                MostrarHtmlEmbebido("pages/config.html?tab=" + tab, titulo);
                _tabActiva = tab;
                PintarTabs();
                return;
            }

            await MostrarTabAsync(tab).ConfigureAwait(true);
        }
        finally { _navegando = false; }
    }

    /// <summary>
    /// Muestra un módulo HTML DENTRO de esta tarjeta (mismo tamaño, mismo ✕,
    /// mismas tabs arriba). Reemplaza al viejo camino que cerraba el panel
    /// y abría el WebView a pantalla completa sin salida.
    /// Si no hay backend de WebView (build sin Chromium) avisa y no hace nada.
    /// </summary>
    private void MostrarHtmlEmbebido(string ruta, string subtitulo)
    {
        var host = this.FindControl<Panel>("HtmlHost");
        var scroll = this.FindControl<ScrollViewer>("TabScroll");
        if (host == null || scroll == null) return;

        // El chequeo del navegador va PRIMERO: si no hay WebView el operario
        // se queda donde estaba — bajar el panel nativo antes de saber si se
        // puede mostrar algo lo dejaría mirando una pestaña vieja con la fila
        // del módulo todavía marcada (p. ej. SectionX › Configurar sin
        // Chromium).
        if (App.WebViewHost == null)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
                "Esta pantalla todavía necesita el navegador embebido y este equipo no lo tiene."));
            return;
        }

        // Si había un panel nativo embebido, se baja ANTES de tapar el área
        // (su Detach para polls y motores; dejarlo vivo abajo del WebView
        // sería un panel gastando red que nadie ve).
        OcultarModuloNativo();

        if (_web == null)
        {
            try
            {
                _web = App.WebViewHost.Create(_ => { });
                host.Children.Add(_web.Control);
            }
            catch (Exception ex)
            {
                _web = null;
                Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo abrir el módulo") + ": " + ex.Message);
                return;
            }
        }

        // ?widget=1: sin la barra lateral del Hub. Adentro de esta tarjeta esa
        // barra sería una segunda navegación compitiendo con el menú de acá.
        string full = OrigenHub() + (ruta ?? "").TrimStart('/');
        full += (full.IndexOf('?') >= 0 ? "&" : "?") + "widget=1";

        try { _web.Navigate(full); }
        catch (Exception ex) { Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo abrir el módulo") + ": " + ex.Message); return; }

        _htmlVisible = true;
        host.IsVisible = true;
        scroll.IsVisible = false;

        SetSubtitulo(subtitulo);
        var g = this.FindControl<Button>("BtnGuardar");
        if (g != null) g.IsVisible = false;   // cada módulo guarda lo suyo
    }

    /// <summary>Saca el módulo HTML de la vista y devuelve la tarjeta a las
    /// pestañas nativas. Vacía la página (no destruye el WebView) para no
    /// dejar a Chromium dibujando sobre la tarjeta.</summary>
    private void OcultarHtmlEmbebido()
    {
        if (!_htmlVisible) return;
        _htmlVisible = false;
        try { _web?.Blank(); } catch { }
        var host = this.FindControl<Panel>("HtmlHost");
        if (host != null) host.IsVisible = false;
        var scroll = this.FindControl<ScrollViewer>("TabScroll");
        if (scroll != null) scroll.IsVisible = true;
    }

    private async Task MostrarTabAsync(string tab)
    {
        // Cualquier navegación nativa gana sobre un módulo pedido desde afuera
        // que todavía no se mostró (el operario tocó una pestaña más rápido
        // que el arranque): sin esto ArrancarAsync lo mostraría igual encima.
        _moduloPendiente = null;
        _ = _ctx.Client?.TecladoAsync(false);
        // Veníamos de un módulo (HTML o panel nativo embebido): volver a las
        // pestañas nativas. El orden no importa acá — los dos son idempotentes.
        OcultarHtmlEmbebido();
        OcultarModuloNativo();
        _tabActiva = tab;

        // Marcar el grupo y la entrada activa en las dos filas de tabs (el
        // "abrirGrupoActivo" del HTML: PintarTabs deriva el grupo de la tab).
        PintarTabs();

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

        SetSubtitulo(PilotX.Cockpit.Bars.Traductor.T(TituloDe(tab)));

        SetEstado("", "");
        try { await vista.AlEntrarAsync().ConfigureAwait(true); } catch { }
        PintarCabecera();
    }

    private static string TituloDe(string tab)
    {
        foreach (var n in NAV) if (n.Tab == tab) return n.Titulo;
        return "Configuración";
    }

    private static CfgNav? BuscarNav(string tab)
    {
        foreach (var n in NAV) if (n.Tab == tab) return n;
        return null;
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
        "toolpivot" => new PivoteTab(_ctx),
        "tsettings" => new TimingTab(_ctx),
        "tsections" => new SeccionesTab(_ctx),
        "tswitches" => new SwitchesTab(_ctx),
        // Máquina NO guarda al salir (TieneGuardar=false y su AlSalirAsync
        // descarta): manda un PGN 238 al módulo y solo lo hace con su propio
        // botón "Enviar + Guardar". Ver la cabecera de MaquinaTab antes de
        // "emparejarla" con las hermanas.
        "amachine" => new MaquinaTab(_ctx),
        // Rumbo mezcla dos semánticas de guardado a propósito (es la del
        // original): el tipo de antena y el paso mínimo POSTEAN AL TOQUE, el
        // resto va por el botón Guardar. Ver la cabecera de RumboTab antes de
        // "emparejarla" con las hermanas.
        "heading" => new RumboTab(_ctx),
        // Rolido mezcla TRES semánticas de guardado (también del original): las
        // acciones del cero y el toggle "Invertir" postean AL TOQUE, y solo la
        // barra de Filtro va por el botón Guardar. Además es la única con poll
        // propio (500 ms, el tractor en vivo), que se para al salir. Ver la
        // cabecera de RolidoTab antes de "emparejarla" con las hermanas.
        "roll" => new RolidoTab(_ctx),
        // U-Turn guarda como las hermanas (botón Guardar / al salir), pero su
        // POST tiene efecto colateral fuerte en el motor: reconstruye las líneas
        // de giro y DESCARTA el U-turn ya dibujado. Por eso su HayCambios es
        // estricto — sin cambios no se postea. Ver la cabecera de UturnTab.
        "uturn" => new UturnTab(_ctx),
        // Tram guarda como las hermanas (botón Guardar / al salir). Es la
        // geometría de las trochas (ancho + dos preferencias), NO la
        // construcción de las huellas sobre el lote: eso sigue en
        // pages/tramline.html y pages/tramlines.html. Ver la cabecera de TramTab.
        "tram" => new TramTab(_ctx),
        _ => new ResumenTab(_ctx),
    };

    // =======================================================================
    //  Módulos (entradas del menú con ModClave / ModRuta)
    // =======================================================================

    /// <summary>Entra a un módulo del menú: marca su fila, abre su grupo y
    /// muestra el contenido EN EL ÁREA DE CONTENIDO de esta tarjeta — panel
    /// nativo embebido si existe, página del Hub embebida si no. Nunca abre
    /// otra ventana ni cierra la Configuración (réplica del iframe del
    /// original: las tabs quedan siempre arriba).</summary>
    private void MostrarModulo(CfgNav nav)
    {
        // Un módulo solo-HTML sin navegador embebido no puede mostrarse: se
        // avisa y el operario se queda donde estaba (misma honestidad que
        // tenía la grilla con sus fichas apagadas).
        if (nav.ModClave == null && App.WebViewHost == null)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
                "Esta pantalla todavía necesita el navegador embebido y este equipo no lo tiene."));
            return;
        }

        _moduloPendiente = null;
        _ = _ctx.Client?.TecladoAsync(false);
        _tabActiva = nav.Tab;
        PintarTabs();

        if (nav.ModClave != null) MostrarModuloNativo(nav);
        else MostrarHtmlEmbebido(nav.ModRuta!, PilotX.Cockpit.Bars.Traductor.T(nav.Titulo));
    }

    /// <summary>El panel de Avalonia que muestra la entrada. Prescripciones
    /// comparte instancia con QuantiX (es el mismo editor parado en Shape).</summary>
    private static string ClavePanelDe(CfgNav nav)
        => nav.ModClave == "prescripciones" ? "quantix" : (nav.ModClave ?? "");

    /// <summary>Monta el panel nativo del módulo ADENTRO del área de contenido
    /// (ModuloNativoHost). Instancia PROPIA cacheada — nunca se reparenta el
    /// overlay vivo de MainWindow (patrón CamarasPanel de la ventana de
    /// cámaras) — con Attach al entrar; el Detach lo hace OcultarModuloNativo
    /// en TODOS los caminos de salida.</summary>
    private void MostrarModuloNativo(CfgNav nav)
    {
        string panelKey = ClavePanelDe(nav);
        var host = this.FindControl<Panel>("ModuloNativoHost");
        var scroll = this.FindControl<ScrollViewer>("TabScroll");
        if (host == null || scroll == null || panelKey.Length == 0) return;

        OcultarHtmlEmbebido();

        // Cambio de panel: bajar el anterior (Detach) antes de subir el nuevo.
        // Mismo panel con otra entrada (QuantiX ↔ Prescripciones) NO se baja:
        // su Attach ya sabe cambiar de pestaña interna.
        bool mismoPanel = _modActivo == panelKey;
        bool mismaEntrada = mismoPanel && _modNavActual == nav.Tab && host.IsVisible;
        if (_modActivo.Length != 0 && !mismoPanel) OcultarModuloNativo();

        Control panel;
        try { panel = ObtenerPanelModulo(panelKey); }
        catch (Exception ex)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo abrir el módulo") + ": " + ex.Message);
            return;
        }

        if (panel.Parent == null) host.Children.Add(panel);
        foreach (var hijo in host.Children) hijo.IsVisible = ReferenceEquals(hijo, panel);

        if (!mismaEntrada)
        {
            try { AttachModulo(panelKey, nav); }
            catch (Exception ex)
            {
                Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo abrir el módulo") + ": " + ex.Message);
                return;
            }
        }
        _modActivo = panelKey;
        _modNavActual = nav.Tab;

        host.IsVisible = true;
        scroll.IsVisible = false;
        MostrarPillsContexto(panelKey);

        SetSubtitulo(PilotX.Cockpit.Bars.Traductor.T(nav.Titulo));
        var g = this.FindControl<Button>("BtnGuardar");
        if (g != null) g.IsVisible = false;   // cada módulo guarda lo suyo
    }

    /// <summary>Baja el panel nativo embebido: Detach (para polls y, en
    /// QuantiX, CUALQUIER MOTOR GIRANDO) y esconde el host. Idempotente — se
    /// llama en toda salida: otra pestaña, otro módulo, HTML encima, ✕ del
    /// shell y Detach externo del panel entero.</summary>
    private void OcultarModuloNativo()
    {
        // Las pills del módulo salen de la barra de contexto en TODA salida
        // (este método corre en todos los caminos de salida). La card NO
        // cambia de tamaño: es fija para todas las entradas.
        LimpiarPillsContexto();
        if (_modActivo.Length == 0)
        {
            var h0 = this.FindControl<Panel>("ModuloNativoHost");
            if (h0 != null) h0.IsVisible = false;
            return;
        }
        string clave = _modActivo;
        _modActivo = "";
        _modNavActual = "";
        // La pestaña Configurar del CoreX-ECU embebido tiene un WebView
        // propio: si quedó abierto, muere con el panel.
        CerrarEcuConfigEmbebida();
        try
        {
            if (_modPaneles.TryGetValue(clave, out var p))
            {
                switch (clave)
                {
                    case "hub":        ((HubPanel)p).Detach(); break;
                    case "quantix":    ((QuantiXEditorPanel)p).Detach(); break;  // manda el STOP a los motores
                    case "vistax":     ((VistaXEditorPanel)p).Detach(); break;
                    case "flowx":      ((FlowXEditorPanel)p).Detach(); break;
                    case "sectionx":   ((SectionXPanel)p).Detach(); break;
                    case "stormx":     ((StormXPanel)p).Detach(); break;
                    case "corex_ecu":  ((CoreXEcuPanel)p).Detach(); break;
                    case "nodos":      ((NodosPanel)p).Detach(); break;
                    case "camaras":    ((CamarasPanel)p).Detach(); break;
                    case "actualizar": ((ActualizarPanel)p).Detach(); break;
                    // Sistema no tiene Detach: Reset() desarma confirmaciones
                    // pendientes (es lo que hace MainWindow al esconderlo).
                    case "sistema":    ((SistemaPanel)p).Reset(); break;
                    case "sonidos":    ((SonidosPanel)p).Detach(); break;
                    case "calculadora":((CalculadoraSiembraPanel)p).Detach(); break;
                    case "firmwares":  ((FirmwaresPanel)p).Detach(); break;
                    case "eventos":    ((EventosPanel)p).Detach(); break;
                    case "insumos":    ((InsumosPanel)p).Detach(); break;
                    case "mapas":      ((MapasPanel)p).Detach(); break;
                    case "orbitx":     ((OrbitXPanel)p).Detach(); break;
                    case "wifi":       ((WifiPanel)p).Detach(); break;
                    case "debug":      ((DebugPanel)p).Detach(); break;
                    // El detalle del nodo apaga su POLLING y nada más: un OTA
                    // en curso lo siguen manejando el nodo y el coordinator.
                    case "nodo_detalle": ((NodoDetallePanel)p).Detach(); break;
                }
            }
        }
        catch { }
        var host = this.FindControl<Panel>("ModuloNativoHost");
        if (host != null) host.IsVisible = false;
        var scroll = this.FindControl<ScrollViewer>("TabScroll");
        if (scroll != null) scroll.IsVisible = true;
    }

    /// <summary>La ✕ propia de un panel embebido: vuelve a Resumen, adentro
    /// de la Configuración (el ✕ del shell sigue siendo la salida de todo).</summary>
    private void VolverDeModulo() => _ = IrATabAsync("summary");

    /// <summary>Crea (una sola vez) el panel nativo de un módulo y le cablea
    /// sus callbacks para vivir ADENTRO de la Configuración: toda navegación
    /// que en MainWindow abría otro overlay acá aterriza en la entrada de
    /// menú correspondiente, sin salir de la tarjeta.</summary>
    private Control ObtenerPanelModulo(string key)
    {
        if (_modPaneles.TryGetValue(key, out var ya)) return ya;
        Control p;
        switch (key)
        {
            case "hub":
            {
                // Los KPIs del Hub los alimenta el HudSnapshot: MainWindow se
                // lo reenvía a esta instancia vía OnSnapshot() (abajo). La
                // lista de nodos y los toggles de overlays van con red propia
                // (Attach).
                var h = new HubPanel();
                h.OnRequestQuantix  = () => _ = IrATabAsync("mod_quantix");
                h.OnRequestVistax   = () => _ = IrATabAsync("mod_vistax");
                h.OnRequestNodos    = () => _ = IrATabAsync("mod_nodos");
                h.OnRequestCorexEcu = () => _ = IrATabAsync("mod_corex_ecu");
                p = h;
                break;
            }
            case "quantix":
            {
                var q = new QuantiXEditorPanel();
                q.OnRequestCerrar = VolverDeModulo;
                q.Aviso += m => Aviso?.Invoke(m);
                p = q;
                break;
            }
            case "vistax":
            {
                var v = new VistaXEditorPanel();
                v.OnRequestCerrar = VolverDeModulo;
                // "‹ Monitor" queda sin cablear a propósito: el monitor live
                // es un overlay del mapa y abrirlo cerraría la Configuración.
                v.OnRequestAbrirInsumos       = () => _ = IrATabAsync("mod_insumos");
                v.OnRequestAbrirConfigCentral = () => _ = IrATabAsync("tsections");
                v.Aviso += m => Aviso?.Invoke(m);
                p = v;
                break;
            }
            case "flowx":
            {
                var f = new FlowXEditorPanel();
                f.OnRequestCerrar = VolverDeModulo;
                f.Aviso += m => Aviso?.Invoke(m);
                p = f;
                break;
            }
            case "sectionx":
            {
                var s = new SectionXPanel();
                // El mapeo surco→sección y el debug MQTT siguen en HTML: se
                // muestran acá adentro, con la fila SectionX todavía activa
                // (re-tocarla vuelve al panel live).
                s.OnRequestConfigurar = () => MostrarHtmlEmbebido(
                    "pages/sectionx.html",
                    PilotX.Cockpit.Bars.Traductor.T("SectionX — Configurar"));
                p = s;
                break;
            }
            case "stormx":
                p = new StormXPanel();
                break;
            case "corex_ecu":
            {
                var e = new CoreXEcuPanel();
                e.OnRequestCerrar = VolverDeModulo;
                e.OnConfigOpen  = slot => AbrirEcuConfigEmbebida(slot);
                e.OnConfigClose = CerrarEcuConfigEmbebida;
                p = e;
                break;
            }
            case "nodos":
            {
                var n = new NodosPanel();
                n.OnRequestCerrar = VolverDeModulo;
                // El detalle del nodo es NATIVO desde 2026-08-18: se monta acá
                // adentro con la fila "Nodos" todavía marcada, igual que hacía
                // la página embebida.
                n.OnRequestDetalle = uid => MostrarDetalleDeNodo(uid);
                n.OnRequestAsistente = () => MostrarHtmlEmbebido(
                    "pages/setup.html",
                    PilotX.Cockpit.Bars.Traductor.T("Nodos — Asistente de primera vez"));
                // "Configurar" de una fila va al módulo del producto — la
                // entrada de este mismo menú, no el overlay suelto.
                n.OnRequestConfigurarProducto = producto =>
                {
                    switch (producto)
                    {
                        case "quantix":  _ = IrATabAsync("mod_quantix");  break;
                        case "vistax":   _ = IrATabAsync("mod_vistax");   break;
                        case "sectionx": _ = IrATabAsync("mod_sectionx"); break;
                        case "flowx":    _ = IrATabAsync("mod_flowx");    break;
                        case "stormx":   _ = IrATabAsync("mod_stormx");   break;
                        default:
                            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
                                "Todavía no hay pantalla para ese tipo de nodo"));
                            break;
                    }
                };
                n.Aviso += m => Aviso?.Invoke(m);
                p = n;
                break;
            }
            case "camaras":
            {
                // Instancia propia, igual que la ventana de Cámaras arma la
                // suya: el overlay/ventana de MainWindow no se reparenta.
                var c = new CamarasPanel();
                c.OnRequestCerrar = VolverDeModulo;
                c.OnRequestConfigurar = () => MostrarHtmlEmbebido(
                    "pages/config.html?mod=camaras.html",
                    PilotX.Cockpit.Bars.Traductor.T("Cámaras — Configurar"));
                p = c;
                break;
            }
            case "actualizar":
                p = new ActualizarPanel();
                break;
            case "sistema":
                p = new SistemaPanel();
                break;
            case "sonidos":
            {
                var s = new SonidosPanel();
                s.OnRequestCerrar = VolverDeModulo;
                s.Aviso += m => Aviso?.Invoke(m);
                p = s;
                break;
            }
            case "calculadora":
            {
                var c = new CalculadoraSiembraPanel();
                c.OnRequestCerrar = VolverDeModulo;
                p = c;
                break;
            }
            case "firmwares":
            {
                var f = new FirmwaresPanel();
                f.OnRequestCerrar = VolverDeModulo;
                f.Aviso += m => Aviso?.Invoke(m);
                p = f;
                break;
            }
            case "eventos":
            {
                var ev = new EventosPanel();
                ev.OnRequestCerrar = VolverDeModulo;
                p = ev;
                break;
            }
            case "insumos":
            {
                var ins = new InsumosPanel();
                ins.OnRequestCerrar = VolverDeModulo;
                ins.Aviso += m => Aviso?.Invoke(m);
                p = ins;
                break;
            }
            case "mapas":
            {
                var mp = new MapasPanel();
                mp.OnRequestCerrar = VolverDeModulo;
                p = mp;
                break;
            }
            case "orbitx":
            {
                var ox = new OrbitXPanel();
                ox.OnRequestCerrar = VolverDeModulo;
                // "Abrir Prescripciones" (en el HTML, un <a> a
                // quantix.html?tab=shape) va a la entrada de este mismo menú:
                // el editor de QuantiX parado en Shape.
                ox.OnRequestPrescripciones = () => _ = IrATabAsync("mod_prescripciones");
                p = ox;
                break;
            }
            case "wifi":
            {
                var wf = new WifiPanel();
                wf.OnRequestCerrar = VolverDeModulo;
                p = wf;
                break;
            }
            case "debug":
            {
                var dbg = new DebugPanel();
                dbg.OnRequestCerrar = VolverDeModulo;
                dbg.Aviso += m => Aviso?.Invoke(m);
                p = dbg;
                break;
            }
            case "nodo_detalle":
            {
                // No es una entrada del menú: es el satélite de Nodos (se
                // entra tocando una fila). Su fila activa sigue siendo Nodos,
                // igual que cuando esto era nodo-detalle.html embebida.
                var nd = new NodoDetallePanel();
                nd.OnRequestCerrar = VolverDeModulo;
                nd.OnRequestVolver = () => _ = IrATabAsync("mod_nodos");
                p = nd;
                break;
            }
            default:
                throw new InvalidOperationException("módulo sin panel nativo: " + key);
        }
        // Estas instancias viven ADENTRO de la Configuración: pierden su marco
        // de tarjeta, su título grande y su ✕ (el shell ya pone todo eso), y
        // entregan sus pills/acciones para la barra de contexto del shell —
        // así quedan SIEMPRE en el mismo lugar, para todas las entradas.
        // Las instancias flotantes de MainWindow no pasan por acá y no cambian.
        if (p is IPanelEmbebible emb)
        {
            emb.ModoEmbebido();
            _modPills[key] = emb.PillsDeContexto();
        }
        _modPaneles[key] = p;
        return p;
    }

    /// <summary>
    /// MainWindow reenvía acá el HudSnapshot (4Hz, ya en el hilo de UI) cuando
    /// la Configuración está visible. Los paneles embebidos que viven de datos
    /// EMPUJADOS —la grilla de secciones de SectionX y los KPIs del Hub— son
    /// instancias PROPIAS (nunca el overlay de MainWindow), así que sin este
    /// reenvío quedaban en blanco/"--" para siempre: nadie los alimentaba.
    /// Los demás paneles embebidos (QuantiX, VistaX, FlowX, StormX, Nodos,
    /// CoreX-ECU…) tienen poller HTTP propio vía Attach y no lo necesitan.
    /// </summary>
    public void OnSnapshot(HudSnapshot s)
    {
        try
        {
            if (_modActivo == "sectionx" && _modPaneles.TryGetValue("sectionx", out var sx))
                ((SectionXPanel)sx).OnSnapshot(s);
            else if (_modActivo == "hub" && _modPaneles.TryGetValue("hub", out var h))
                ((HubPanel)h).OnSnapshot(s);
        }
        catch { /* un HUD perdido no puede tirar el shell de la Config */ }
    }

    /// <summary>Cuelga en la barra de contexto las pills que entregó el panel
    /// embebido activo (si no entregó nada, la barra muestra solo el nombre).</summary>
    private void MostrarPillsContexto(string panelKey)
    {
        var host = this.FindControl<StackPanel>("ContextoPills");
        if (host == null) return;
        host.Children.Clear();
        if (_modPills.TryGetValue(panelKey, out var pills) && pills != null)
            host.Children.Add(pills);
    }

    /// <summary>Vacía la barra de contexto (las pills quedan cacheadas en
    /// _modPills para la próxima entrada al módulo). Idempotente.</summary>
    private void LimpiarPillsContexto()
    {
        var host = this.FindControl<StackPanel>("ContextoPills");
        host?.Children.Clear();
    }

    /// <summary>Attach del panel embebido con su cliente HTTP (lazy, contra el
    /// mismo host que el resto de la Configuración). Se llama en CADA entrada:
    /// los Attach de estos paneles rearman sus polls y son re-entrantes.</summary>
    private void AttachModulo(string panelKey, CfgNav nav)
    {
        string baseUrl = _ctx.Client?.BaseUrl ?? "http://127.0.0.1:5180/";
        switch (panelKey)
        {
            case "hub":
                _nodosCli ??= new NodosClient(baseUrl);
                _overlaysCli ??= new OverlaysClient(baseUrl);
                ((HubPanel)_modPaneles[panelKey]).Attach(_nodosCli, _overlaysCli);
                break;
            case "quantix":
                _quantiXCli ??= new QuantiXEditorClient(baseUrl);
                // Prescripciones = el mismo editor parado en Shape.
                ((QuantiXEditorPanel)_modPaneles[panelKey]).Attach(
                    _quantiXCli, nav.ModClave == "prescripciones" ? "shape" : null);
                break;
            case "vistax":
                _vistaXCli ??= new VistaXClient(baseUrl);
                ((VistaXEditorPanel)_modPaneles[panelKey]).Attach(_vistaXCli);
                break;
            case "flowx":
                _flowXCli ??= new FlowXClient(baseUrl);
                ((FlowXEditorPanel)_modPaneles[panelKey]).Attach(_flowXCli);
                break;
            case "sectionx":
                _sectionXCli ??= new SectionXClient(baseUrl);
                ((SectionXPanel)_modPaneles[panelKey]).Attach(_sectionXCli);
                break;
            case "stormx":
                _stormXCli ??= new StormXClient(baseUrl);
                ((StormXPanel)_modPaneles[panelKey]).Attach(_stormXCli);
                break;
            case "corex_ecu":
                _ecuCli ??= new CoreXEcuClient(baseUrl);
                ((CoreXEcuPanel)_modPaneles[panelKey]).Attach(_ecuCli);
                break;
            case "nodos":
                _nodosCli ??= new NodosClient(baseUrl);
                ((NodosPanel)_modPaneles[panelKey]).Attach(_nodosCli);
                break;
            case "camaras":
                _camarasCli ??= new CamarasClient(baseUrl);
                ((CamarasPanel)_modPaneles[panelKey]).Attach(_camarasCli);
                break;
            case "actualizar":
                _updateCli ??= new UpdateClient(baseUrl);
                ((ActualizarPanel)_modPaneles[panelKey]).Attach(_updateCli);
                break;
            case "sistema":
                _sistemaCli ??= new SistemaClient(baseUrl);
                ((SistemaPanel)_modPaneles[panelKey]).Attach(_sistemaCli);
                break;
            case "sonidos":
                _sonidosCli ??= new SonidosClient(baseUrl);
                ((SonidosPanel)_modPaneles[panelKey]).Attach(_sonidosCli);
                break;
            case "calculadora":
                _calculadoraCli ??= new CalculadoraSiembraClient(baseUrl);
                ((CalculadoraSiembraPanel)_modPaneles[panelKey]).Attach(_calculadoraCli);
                break;
            case "firmwares":
                _firmwaresCli ??= new FirmwaresClient(baseUrl);
                ((FirmwaresPanel)_modPaneles[panelKey]).Attach(_firmwaresCli);
                break;
            case "eventos":
                _eventosCli ??= new EventosClient(baseUrl);
                ((EventosPanel)_modPaneles[panelKey]).Attach(_eventosCli);
                break;
            case "insumos":
                _insumosCli ??= new InsumosClient(baseUrl);
                ((InsumosPanel)_modPaneles[panelKey]).Attach(_insumosCli);
                break;
            case "mapas":
                _mapasCli ??= new MapasClient(baseUrl);
                ((MapasPanel)_modPaneles[panelKey]).Attach(_mapasCli);
                break;
            case "orbitx":
                _orbitXCli ??= new OrbitXPanelClient(baseUrl);
                ((OrbitXPanel)_modPaneles[panelKey]).Attach(_orbitXCli);
                break;
            case "wifi":
                _wifiCli ??= new RedWifiClient(baseUrl);
                ((WifiPanel)_modPaneles[panelKey]).Attach(_wifiCli);
                break;
            case "debug":
                _debugCli ??= new DebugClient(baseUrl);
                ((DebugPanel)_modPaneles[panelKey]).Attach(_debugCli);
                break;
            case "nodo_detalle":
                // El UID lo pone MostrarDetalleDeNodo con Abrir(): Abrir y
                // Attach son conmutativos, así que el orden no importa.
                _nodoDetalleCli ??= new NodoDetalleClient(baseUrl);
                ((NodoDetallePanel)_modPaneles[panelKey]).Attach(_nodoDetalleCli);
                break;
        }
    }

    /// <summary>El satélite de Nodos: el detalle de UN nodo, montado en el
    /// área de contenido con la fila "Nodos" todavía marcada (igual que cuando
    /// esto era nodo-detalle.html embebida). El UID SIEMPRE viene de una fila
    /// de la lista.</summary>
    private void MostrarDetalleDeNodo(string? uid)
    {
        var nav = new CfgNav
        {
            Tab = "mod_nodos",
            Titulo = "Nodos — Detalle del nodo",
            Grupo = "Módulos",
            ModClave = "nodo_detalle",
        };
        MostrarModuloNativo(nav);
        if (_modPaneles.TryGetValue("nodo_detalle", out var p))
            ((NodoDetallePanel)p).Abrir(uid ?? string.Empty);
    }

    /// <summary>¿A qué entrada del menú pertenece una página del Hub? Para que
    /// AbrirModuloHtml (la puerta externa de los "Configurar") aterrice con la
    /// fila correcta marcada. Cubre las rutas propias de los módulos solo-HTML,
    /// las páginas de los módulos con panel nativo y las satélites conocidas
    /// (nodo-detalle, setup, config.html?mod=…). Null = ninguna fila.</summary>
    private static CfgNav? NavDeRuta(string ruta)
    {
        if (string.IsNullOrEmpty(ruta)) return null;
        string pagina = ruta;
        // config.html?mod=X → la página que importa es X.
        int im = pagina.IndexOf("mod=", StringComparison.OrdinalIgnoreCase);
        if (im >= 0)
        {
            pagina = pagina.Substring(im + 4);
            int amp = pagina.IndexOf('&');
            if (amp >= 0) pagina = pagina.Substring(0, amp);
        }
        int q = pagina.IndexOf('?');
        if (q >= 0) pagina = pagina.Substring(0, q);
        int barra = pagina.LastIndexOf('/');
        if (barra >= 0) pagina = pagina.Substring(barra + 1);
        pagina = pagina.Trim();
        if (pagina.Length == 0) return null;

        foreach (var n in NAV)
            if (n.ModRuta != null && n.ModRuta.EndsWith("/" + pagina, StringComparison.OrdinalIgnoreCase))
                return n;

        string? tab = pagina.ToLowerInvariant() switch
        {
            "hub.html"        => "mod_hub",
            "quantix.html"    => "mod_quantix",
            "vistax.html"     => "mod_vistax",
            "flowx.html"      => "mod_flowx",
            "sectionx.html"   => "mod_sectionx",
            "stormx.html"     => "mod_stormx",
            "corex-ecu.html"  => "mod_corex_ecu",
            "nodos.html" or "nodo-detalle.html" or "setup.html" => "mod_nodos",
            "camaras.html"    => "mod_camaras",
            "sonidos.html"    => "mod_sonidos",
            "actualizar.html" => "mod_actualizar",
            "sistema.html"    => "mod_sistema",
            "calculadora-siembra.html" => "mod_calculadora",
            "firmwares.html"  => "mod_firmwares",
            "eventos.html"    => "mod_eventos",
            // Estas cinco dejaron de ser ModRuta el 2026-08-18 (pasaron a
            // panel nativo), así que ya no las encuentra el barrido de arriba:
            // se mapean acá para que un deep-link externo siga cayendo en su
            // fila.
            "insumos.html"    => "mod_insumos",
            "mapas.html"      => "mod_mapas",
            "orbitx.html"     => "mod_orbitx",
            "wifi.html"       => "mod_wifi",
            "debug.html"      => "mod_debug",
            _ => null,
        };
        return tab != null ? BuscarNav(tab) : null;
    }

    /// <summary>Origen del Hub local (sin /pages/…), con barra final.</summary>
    private static string OrigenHub()
    {
        string origen = App.TargetUrl ?? "http://127.0.0.1:5180/";
        int api = origen.IndexOf("/pages/", StringComparison.OrdinalIgnoreCase);
        if (api >= 0) origen = origen.Substring(0, api + 1);
        if (!origen.EndsWith("/")) origen += "/";
        return origen;
    }

    // ── Pestaña "Configurar" del CoreX-ECU embebido ────────────────────────
    // Réplica local de AbrirEcuConfig/CerrarEcuConfig de MainWindow: el panel
    // pide un slot (OnConfigOpen) y ahí se monta un WebView propio con
    // corex-ecu.html?widget=1; al salir de la pestaña (o del módulo) se
    // destruye — no se deja a Chromium pintando abajo de otra vista.

    private void AbrirEcuConfigEmbebida(Panel slot)
    {
        if (App.WebViewHost == null) return;
        try
        {
            if (_ecuWeb == null)
            {
                _ecuWeb = App.WebViewHost.Create(_ => { });
                slot.Children.Add(_ecuWeb.Control);
            }
            _ecuWeb.Navigate(OrigenHub() + "pages/corex-ecu.html?widget=1");
        }
        catch (Exception ex)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo abrir el módulo") + ": " + ex.Message);
        }
    }

    private void CerrarEcuConfigEmbebida()
    {
        if (_ecuWeb == null) return;
        try
        {
            var wv = _ecuWeb;
            _ecuWeb = null;
            if (wv.Control.Parent is Panel padre) padre.Children.Remove(wv.Control);
            wv.Destroy();
        }
        catch { _ecuWeb = null; }
    }

    // =======================================================================
    //  Botones del shell
    // =======================================================================

    private void OnCerrarClick(object? s, RoutedEventArgs e)
    {
        // Si el ✕ llega con un módulo HTML abierto, primero vaciarlo: un
        // WebView2 con página cargada sigue dibujando sobre el mapa aunque el
        // panel se oculte (airspace del control nativo). Y si lo abierto es un
        // panel nativo embebido, su Detach (motores incluidos) va acá también
        // — el Detach del shell lo repetiría, pero mejor no depender del host.
        OcultarHtmlEmbebido();
        OcultarModuloNativo();
        OnRequestCerrar?.Invoke();
    }

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
