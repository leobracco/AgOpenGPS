// ============================================================================
// FormAgroParallelHubWebView2.cs
// Host WinForms que arranca AgpWebHost + WebView2 y navega a la UI HTML.
// Reemplazo progresivo de FormAgroParallelHub (Fase B placeholder).
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgroParallel.FlowX;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.WebHost;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgroParallel.Shell
{
    public class FormAgroParallelHubWebView2 : Form
    {
        private readonly IAogStateProvider _state;
        private readonly ILotesService _lotes;
        private readonly IVehicleToolService _vehicleTool;
        private readonly IShapefileService _shapefile;
        private readonly ICoverageService _coverage;
        private readonly ISectionControlService _sectionsCore;
        private readonly IQuantiXRuntimeService _quantixRuntime;
        private readonly IGuidanceCalculator _guidance;
        private readonly IPilotXUpdateService _pilotxUpdate;
        private readonly string _wwwroot;
        private readonly int _port;
        private AgpWebHost _webHost;
        private bool _ownsHost; // true solo si el Hub levanto su propio host (fallback)
        private NodoRegistryService _nodos;
        private FlowXBridge _flowxBridge;
        private WebView2 _webView;

        /// <summary>
        /// Página inicial a la que navega el WebView2 una vez levantado el host.
        /// Si es null/vacío va al welcome (index.html). Si trae valor, se concatena
        /// al Url base (ej: "pages/camaras.html"). Lo usa PilotX para abrir el Hub
        /// directo en una sub-pantalla desde un botón externo.
        /// </summary>
        public string InitialPage { get; set; }

        /// <summary>
        /// Control de anclaje (en PilotX: <c>oglMain</c>, el GLControl del mapa).
        /// Si se setea ANTES de mostrar la ventana, el Hub se posiciona y
        /// dimensiona para cubrir exactamente la pantalla de ese control en
        /// vez de ir maximizado a pantalla completa. La ventana sigue al
        /// control si PilotX se mueve o redimensiona.
        /// Si queda en null, el comportamiento es borderless + maximized
        /// (modo histórico).
        /// </summary>
        public Control AnchorControl { get; set; }
        private Control _anchorHooked;
        private Form _anchorParentForm;

        /// <summary>
        /// Modo "widget flotante": ventana chica, draggable y por encima del mapa
        /// que NO lo cubre. Sustituye al widget Avalonia (spike no shippeado) para
        /// popups de datos como datos-lote.html / datos-gps.html, de modo que el
        /// operario siga viendo la pantalla principal. Si está en true,
        /// <see cref="ApplyAnchor"/> NO se usa (no se cubre el mapa); en su lugar la
        /// ventana toma <see cref="FloatingSize"/> y se ubica en una esquina del
        /// <see cref="AnchorControl"/>. La página HTML trae su propio botón de
        /// cierre (mensaje 'close-hub').
        /// </summary>
        public bool FloatingWidget { get; set; }

        /// <summary>Tamaño del widget flotante (solo aplica si FloatingWidget == true).</summary>
        public Size FloatingSize { get; set; } = new Size(720, 760);

        /// <summary>
        /// Dock del widget flotante contra un borde del AnchorControl:
        /// "top" | "right" | "bottom" (null = flotante libre en la esquina,
        /// comportamiento histórico). Con dock la ventana va SIN borde (parece
        /// una barra nativa), toma el largo completo de ese borde (el espesor
        /// sale de FloatingSize) y SIGUE al mapa si PilotX se mueve o
        /// redimensiona. Lo usan las barras HTML que reemplazan a las nativas.
        /// </summary>
        public string FloatingDock { get; set; }

        /// <summary>Margen del dock (reserva espacio p/ no pisar otras barras).</summary>
        public Padding DockMargin { get; set; }

        // ---------- Pre-warm del runtime de WebView2 ----------
        // CoreWebView2Environment.CreateAsync(...) es la operación más lenta del
        // cold-start del Hub (típicamente 500ms-1s la primera vez por arranque).
        // PilotX llama a Prewarm() durante FormGPS_Load para que cuando el operario
        // toque "⬢ AP" o "📷 Cámaras" la nav sea ~instantánea.
        // Idempotente: llamadas repetidas devuelven el mismo Task.
        private static Task<CoreWebView2Environment> s_envTask;
        private static readonly object s_envLock = new object();

        public static Task<CoreWebView2Environment> Prewarm()
        {
            lock (s_envLock)
            {
                if (s_envTask == null)
                {
                    string userData = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "AgroParallel", "WebView2Data");
                    try { Directory.CreateDirectory(userData); } catch { }
                    s_envTask = CoreWebView2Environment.CreateAsync(null, userData);
                }
                return s_envTask;
            }
        }

        public FormAgroParallelHubWebView2(IAogStateProvider state, string wwwroot, int port = 5180)
            : this(state, null, null, null, wwwroot, port) { }

        public FormAgroParallelHubWebView2(IAogStateProvider state, ILotesService lotes, string wwwroot, int port = 5180)
            : this(state, lotes, null, null, wwwroot, port) { }

        public FormAgroParallelHubWebView2(IAogStateProvider state, ILotesService lotes, IVehicleToolService vehicleTool, string wwwroot, int port = 5180)
            : this(state, lotes, vehicleTool, null, wwwroot, port) { }

        public FormAgroParallelHubWebView2(IAogStateProvider state, ILotesService lotes, IVehicleToolService vehicleTool, IShapefileService shapefile, string wwwroot, int port = 5180)
            : this(state, lotes, vehicleTool, shapefile, null, null, null, null, null, wwwroot, port) { }

        public FormAgroParallelHubWebView2(
            IAogStateProvider state,
            ILotesService lotes,
            IVehicleToolService vehicleTool,
            IShapefileService shapefile,
            ICoverageService coverage,
            ISectionControlService sectionsCore,
            IQuantiXRuntimeService quantixRuntime,
            IGuidanceCalculator guidance,
            string wwwroot,
            int port = 5180)
            : this(state, lotes, vehicleTool, shapefile, coverage, sectionsCore, quantixRuntime, guidance, null, wwwroot, port) { }

        public FormAgroParallelHubWebView2(
            IAogStateProvider state,
            ILotesService lotes,
            IVehicleToolService vehicleTool,
            IShapefileService shapefile,
            ICoverageService coverage,
            ISectionControlService sectionsCore,
            IQuantiXRuntimeService quantixRuntime,
            IGuidanceCalculator guidance,
            IPilotXUpdateService pilotxUpdate,
            string wwwroot,
            int port = 5180)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _lotes = lotes;
            _vehicleTool = vehicleTool;
            _shapefile = shapefile;
            _coverage = coverage;
            _sectionsCore = sectionsCore;
            _quantixRuntime = quantixRuntime;
            _guidance = guidance;
            _pilotxUpdate = pilotxUpdate;
            _wwwroot = wwwroot;
            _port = port;

            Text = "AgroParallel · Piloto";
            // Defaults para el modo "anchor null" (histórico):
            //   borderless + maximized → sustituye visualmente a FormGPS.
            // Si AnchorControl está seteado al hacer Show(), OnLoad reconfigura
            // a Normal con el rect del control (modo overlay sobre el mapa).
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            // Defensa: si por algún motivo el borde vuelve, no dejes la ventana
            // microscópica.
            Width = 1280;
            Height = 800;

            // Anti-parpadeo (2026-07-17): mientras WebView2 inicializa y navega,
            // el control pinta su DefaultBackgroundColor — blanco por defecto —
            // y cada barra/widget que se crea flashea blanco sobre el mapa
            // (muy notorio al aceptar términos y al abrir un lote, que es
            // cuando se instancian las barras dockeadas y los overlays).
            // Usamos el fondo del design system (#F5F7F4) en el form y en el
            // WebView para que la transición sea imperceptible.
            var agpBg = System.Drawing.Color.FromArgb(0xF5, 0xF7, 0xF4);
            BackColor = agpBg;
            _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = agpBg };
            Controls.Add(_webView);

            Load += OnLoad;
            FormClosing += OnClosing;
        }

        /// <summary>
        /// Reconfigura la ventana para superponerse al AnchorControl: borderless,
        /// Normal (no maximizado), bounds = rect-pantalla del control. Hookea
        /// resize/move del control y del form padre para seguirlo.
        /// </summary>
        private void ApplyAnchor()
        {
            var c = AnchorControl;
            if (c == null || c.IsDisposed) return;

            // Estado de la ventana → overlay sobre el mapa.
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;

            UpdateAnchorBounds();

            HookAnchor(c);
        }

        // Hook (idempotente) a los eventos del anchor + form padre: solo una
        // vez por instancia de control. Lo comparten ApplyAnchor (overlay
        // completo) y ApplyFloating con dock (barras HTML).
        private void HookAnchor(Control c)
        {
            if (_anchorHooked != c)
            {
                if (_anchorHooked != null)
                {
                    _anchorHooked.SizeChanged -= OnAnchorChanged;
                    _anchorHooked.LocationChanged -= OnAnchorChanged;
                    _anchorHooked.HandleDestroyed -= OnAnchorDestroyed;
                }
                _anchorHooked = c;
                c.SizeChanged += OnAnchorChanged;
                c.LocationChanged += OnAnchorChanged;
                c.HandleDestroyed += OnAnchorDestroyed;

                var pf = c.FindForm();
                if (_anchorParentForm != pf)
                {
                    if (_anchorParentForm != null)
                    {
                        _anchorParentForm.Move -= OnAnchorChanged;
                        _anchorParentForm.SizeChanged -= OnAnchorChanged;
                    }
                    _anchorParentForm = pf;
                    if (pf != null)
                    {
                        pf.Move += OnAnchorChanged;
                        pf.SizeChanged += OnAnchorChanged;
                    }
                }
            }
        }

        /// <summary>
        /// Configura la ventana como widget flotante chico: borde tool-window
        /// (título mínimo con X, draggable), Normal (no maximizado), tamaño
        /// FloatingSize y ubicada en la esquina superior derecha del AnchorControl
        /// (o del área de trabajo si no hay anchor). A diferencia de ApplyAnchor,
        /// NO cubre el mapa ni sigue al control: queda flotando para que el
        /// operario siga viendo la pantalla principal.
        /// </summary>
        private void ApplyFloating()
        {
            // Dockeado a un borde del mapa: sin borde (parece barra nativa),
            // sigue al anchor, y su geometría la calcula UpdateDockBounds.
            if (!string.IsNullOrEmpty(FloatingDock))
            {
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TryRoundCorners();
                UpdateDockBounds();
                var anchor = AnchorControl;
                if (anchor != null && !anchor.IsDisposed) HookAnchor(anchor);
                return;
            }

            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            TryRoundCorners();

            Size sz = FloatingSize;
            if (sz.Width < 200) sz.Width = 720;
            if (sz.Height < 200) sz.Height = 760;

            var c = AnchorControl;
            Rectangle area = (c != null && !c.IsDisposed && c.IsHandleCreated)
                ? c.RectangleToScreen(c.ClientRectangle)
                : Screen.PrimaryScreen.WorkingArea;

            sz = ScaleWidgetToArea(sz, area);

            int x = area.Right - sz.Width - 24;
            int y = area.Top + 24;
            if (x < area.Left + 8) x = area.Left + 8;
            if (y + sz.Height > area.Bottom - 8) y = area.Bottom - 8 - sz.Height;
            if (y < area.Top + 8) y = area.Top + 8;
            SetBounds(x, y, sz.Width, sz.Height);
        }

        /// <summary>
        /// Adecúa el tamaño pedido de un widget al área disponible del mapa.
        /// Los width/height de los call sites están pensados para monitor de
        /// escritorio: en la pantalla del tractor (10" 1080x720) los grandes
        /// quedaban casi pantalla completa aun con el clamp duro. Pasada
        /// general 2026-07-19:
        /// 1) los widgets chicos (&lt;50% del área en ambos ejes) se respetan
        ///    tal cual — son popups con layout exacto;
        /// 2) los grandes se escalan proporcional al área (referencia
        ///    1440x900, factor nunca &lt; 0.6 para no pulverizar los targets
        ///    táctiles) y se capean a 78% ancho / 85% alto del mapa para que
        ///    el operario siga viendo el guiado detrás.
        /// Las páginas del Hub son responsive: reacomodan solas.
        /// </summary>
        private static Size ScaleWidgetToArea(Size sz, Rectangle area)
        {
            bool esGrande = sz.Width > area.Width * 0.5 || sz.Height > area.Height * 0.5;
            if (esGrande)
            {
                double scale = Math.Min(1.0, Math.Min(area.Width / 1440.0, area.Height / 900.0));
                if (scale < 0.6) scale = 0.6;
                sz.Width = (int)Math.Round(sz.Width * scale);
                sz.Height = (int)Math.Round(sz.Height * scale);

                int maxW = (int)(area.Width * 0.78);
                int maxH = (int)(area.Height * 0.85);
                if (sz.Width > maxW) sz.Width = maxW;
                if (sz.Height > maxH) sz.Height = maxH;
            }
            else
            {
                // Aun chicos, jamás más grandes que el área menos margen.
                if (sz.Width > area.Width - 16) sz.Width = area.Width - 16;
                if (sz.Height > area.Height - 16) sz.Height = area.Height - 16;
            }

            if (sz.Width < 260) sz.Width = 260;
            if (sz.Height < 180) sz.Height = 180;
            return sz;
        }

        /// <summary>
        /// Redimensiona un widget flotante en caliente (ej: guia-rapida.html
        /// expandiendo un panel de acciones) manteniendo la misma esquina de
        /// anclaje que <see cref="ApplyFloating"/> — crece/achica hacia abajo,
        /// no hacia arriba, para no taparle el mapa al operario. Se dispara
        /// desde <see cref="OnWebMessageReceived"/> vía postMessage('resize:WxH').
        /// No-op si la ventana no es un widget flotante.
        /// </summary>
        public void ResizeFloatingWidget(int width, int height)
        {
            if (!FloatingWidget) return;
            if (!string.IsNullOrEmpty(FloatingDock))
            {
                // Barra dockeada: el resize solo cambia el espesor/tamaño base;
                // la posición la sigue calculando el dock.
                FloatingSize = new Size(width, height);
                UpdateDockBounds();
                return;
            }
            if (width < 200) width = 200;
            if (height < 80) height = 80;

            // OJO: acá NO se aplica ScaleWidgetToArea — los 'resize:WxH' vienen
            // del propio HTML con el tamaño EXACTO de su contenido (constantes
            // tipo EXPANDED/COLLAPSED de guia-rapida.js): escalarlos recortaría.
            var c = AnchorControl;
            if (c != null && !c.IsDisposed && c.IsHandleCreated)
            {
                Rectangle r = c.RectangleToScreen(c.ClientRectangle);
                if (width > r.Width - 16) width = r.Width - 16;
                if (height > r.Height - 16) height = r.Height - 16;
                int x = r.Right - width - 24;
                int y = r.Top + 24;
                SetBounds(x, y, width, height);
            }
            else
            {
                SetBounds(Left, Top, width, height);
            }
        }

        private void OnAnchorChanged(object sender, EventArgs e)
        {
            if (FloatingWidget)
            {
                if (!string.IsNullOrEmpty(FloatingDock)) UpdateDockBounds();
            }
            else
            {
                UpdateAnchorBounds();
            }
        }

        /// <summary>
        /// Geometría de barra dockeada: pegada al borde FloatingDock del
        /// AnchorControl, largo completo de ese borde (menos DockMargin) y
        /// espesor = FloatingSize. "top" queda centrada con su ancho propio.
        /// </summary>
        private void UpdateDockBounds()
        {
            Rectangle r;
            var c = AnchorControl;
            if (c != null && !c.IsDisposed && c.IsHandleCreated)
                r = c.RectangleToScreen(c.ClientRectangle);
            else
                r = Screen.PrimaryScreen.WorkingArea;
            if (r.Width < 100 || r.Height < 100) return; // oculto/minimizado

            var m = DockMargin;
            int x, y, w, h;
            switch ((FloatingDock ?? string.Empty).ToLowerInvariant())
            {
                case "top":
                    w = Math.Max(200, FloatingSize.Width);
                    h = Math.Max(40, FloatingSize.Height);
                    x = r.Left + (r.Width - w) / 2;
                    y = r.Top + 4 + m.Top;
                    break;
                case "right":
                    w = Math.Max(40, FloatingSize.Width);
                    h = r.Height - 8 - m.Top - m.Bottom;
                    x = r.Right - w - 4 - m.Right;
                    y = r.Top + 4 + m.Top;
                    break;
                case "left":
                    w = Math.Max(40, FloatingSize.Width);
                    h = r.Height - 8 - m.Top - m.Bottom;
                    x = r.Left + 4 + m.Left;
                    y = r.Top + 4 + m.Top;
                    break;
                case "bottom":
                    h = Math.Max(40, FloatingSize.Height);
                    w = r.Width - 8 - m.Left - m.Right;
                    x = r.Left + 4 + m.Left;
                    y = r.Bottom - h - 4 - m.Bottom;
                    break;
                default:
                    return;
            }
            SetBounds(x, y, w, h);
        }

        private void OnAnchorDestroyed(object sender, EventArgs e)
        {
            // Si el oglMain se destruye (cerrar PilotX), cerramos el Hub también.
            try { Close(); } catch { }
        }

        private void UpdateAnchorBounds()
        {
            var c = AnchorControl;
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return;
            try
            {
                Rectangle rect = c.RectangleToScreen(c.ClientRectangle);
                if (rect.Width < 100 || rect.Height < 100) return; // control oculto/minimizado
                SetBounds(rect.X, rect.Y, rect.Width, rect.Height);
            }
            catch { }
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            // Si PilotX nos pasó un control de anclaje (oglMain), nos posicionamos
            // sobre él en lugar de ir maximized. Esto debe pasar antes de
            // navegar el WebView2 para que el layout final ya esté aplicado y
            // el contenido no parpadee al re-encajar.
            if (FloatingWidget)
            {
                try { ApplyFloating(); } catch { }
            }
            else if (AnchorControl != null)
            {
                try { ApplyAnchor(); } catch { }
            }

            // Anti-parpadeo (2026-07-17): las barras dockeadas (espejo HTML)
            // aparecían como franja lisa #F5F7F4 durante los ~2-3 s que tarda
            // WebView2 en inicializar + renderizar la página (capturado con
            // ráfaga de screenshots al continuar lote). Arrancan invisibles
            // y se revelan recién con NavigationCompleted; failsafe 4 s por
            // si la navegación falla.
            bool dockedBar = FloatingWidget && !string.IsNullOrEmpty(FloatingDock);
            if (dockedBar)
            {
                Opacity = 0;
                var revealTimer = new System.Windows.Forms.Timer { Interval = 4000 };
                revealTimer.Tick += (s2, e2) =>
                {
                    revealTimer.Stop();
                    revealTimer.Dispose();
                    try { if (!IsDisposed && Opacity < 1) Opacity = 1; } catch { }
                };
                revealTimer.Start();
            }
            try
            {
                // Si FormGPS ya levanto el host via AgpWebHostBootstrap (camino normal),
                // reusamos. _ownsHost queda en false asi OnClosing NO lo apaga
                // (lo apaga FormGPS al cerrar PilotX).
                if (AgpWebHostBootstrap.IsRunning)
                {
                    _webHost = AgpWebHostBootstrap.Host;
                    _ownsHost = false;
                }
                else
                {
                    // Fallback (no deberia pasar en runtime PilotX): arrancamos uno
                    // local. Util si el Hub se instancia standalone sin FormGPS.
                    _ownsHost = true;
                    _nodos = new NodoRegistryService();
                    var (broker, brokerPort) = LoadBrokerConfig();
                    if (string.IsNullOrWhiteSpace(broker)) broker = "127.0.0.1";
                    if (brokerPort <= 0) brokerPort = 1883;
                    _nodos.Start(broker, brokerPort);

                    var vistaxCfg = new VistaXConfigService();
                    var insumosCat = new InsumoCatalogService();
                    var vistaxLive = new VistaXLiveService(_nodos, vistaxCfg, insumosCat, _state, _sectionsCore);
                    var flowxCfg = new FlowXConfigService();
                    var flowxLive = new FlowXLiveService(_nodos, flowxCfg);
                    var stormxCfg = new StormXConfigService();
                    var stormxLive = new StormXLiveService(_nodos, stormxCfg);
                    var linexCfg = new LineXConfigService();
                    var linexLive = new LineXLiveService(_nodos, linexCfg);
                    _webHost = new AgpWebHost(
                        _state,
                        new SistemaService(),
                        _nodos,
                        new OrbitXConfigService(),
                        new SectionXConfigService(),
                        new CamarasConfigService(),
                        new QuantiXConfigService(_nodos),
                        vistaxCfg,
                        vistaxLive,
                        new DebugLogService(),
                        _lotes,
                        _vehicleTool,
                        _shapefile,
                        _coverage,
                        _sectionsCore,
                        _quantixRuntime,
                        _guidance,
                        _pilotxUpdate,
                        flowxCfg,
                        flowxLive,
                        stormxCfg,
                        stormxLive,
                        linexCfg,
                        linexLive,
                        _wwwroot,
                        _port);
                    _webHost.Start();

                    // FlowXBridge fallback (mismo criterio que Bootstrap):
                    // publica targets a los nodos FlowX. Si flowX.json está vacío
                    // o disabled, StartAsync sale sin hacer nada.
                    try
                    {
                        _flowxBridge = new FlowXBridge(_state, FlowXConfig.Load());
                        _ = _flowxBridge.StartAsync();
                    }
                    catch (Exception bex)
                    {
                        System.Diagnostics.Trace.WriteLine("[HubWV2] FlowXBridge start: " + bex.Message);
                    }
                }

                // Reusamos el environment pre-cacheado si PilotX ya lo armó durante
                // FormGPS_Load (vía Prewarm()). Si no, lo armamos ahora — el
                // método Prewarm es idempotente.
                var env = await Prewarm().ConfigureAwait(true);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.AddHostObjectToScript("agp", new ShellBridge(this));

                // Canal estable de mensajes JS→host. El sidebar.js manda
                // 'close-hub' al pulsar el botón rojo del pie. postMessage
                // está disponible inmediatamente, a diferencia de hostObjects
                // que tiene timing race con el primer load del documento.
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Reveal de barra dockeada: ver comentario anti-parpadeo arriba.
                if (dockedBar)
                {
                    _webView.CoreWebView2.NavigationCompleted += (s2, e2) =>
                    {
                        try
                        {
                            if (!IsDisposed && Opacity < 1)
                                BeginInvoke(new Action(() => { try { Opacity = 1; } catch { } }));
                        }
                        catch { }
                    };
                }

                // F12 (DevTools), Ctrl+R / F5 (reload) — habilitados por los
                // settings nativos de WebView2. Si esto se desactiva (por
                // ejemplo en build release), F12 deja de abrir DevTools.
                var settings = _webView.CoreWebView2.Settings;
                settings.AreDevToolsEnabled = true;
                settings.AreBrowserAcceleratorKeysEnabled = true;

                // Pantalla principal = welcome AgroParallel (index.html). El
                // welcome auto-avanza a pages/piloto.html a los ~1.6s o al
                // primer toque del operario. La sidebar y el hub-shell viejos
                // se llegan por click desde el mapa (botón a definir).
                // Si PilotX nos pasó InitialPage, abrimos directo ahí (caso:
                // botón "Cámaras" en el toolbar de FormGPS).
                string target = _webHost.Url;
                if (!string.IsNullOrEmpty(InitialPage))
                {
                    // URL absoluta (ej: dashboard CoreX en 127.0.0.1:5181) se
                    // navega tal cual; lo relativo se cuelga del host del Hub.
                    target = InitialPage.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? InitialPage
                        : target.TrimEnd('/') + "/" + InitialPage.TrimStart('/');
                }
                _webView.CoreWebView2.Navigate(target);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fallo al inicializar WebView2: " + ex.Message,
                    "AgroParallel", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        // JS → host. El sidebar manda 'close-hub' al pulsar el botón "✕ Cerrar Hub".
        // Cualquier otro mensaje se ignora silenciosamente (no rompemos por payloads
        // desconocidos: futuros features pueden agregar mensajes sin tocar acá).
        private void OnWebMessageReceived(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch { /* puede ser JSON */ }

            if (string.Equals(msg, "close-hub", StringComparison.OrdinalIgnoreCase))
            {
                try { BeginInvoke(new Action(Close)); } catch { }
            }
            else if (string.Equals(msg, "open-wifi-settings", StringComparison.OrdinalIgnoreCase))
            {
                try { new ShellBridge(this).OpenWifiSettings(); } catch { }
            }
            else if (msg != null && msg.StartsWith("resize:", StringComparison.OrdinalIgnoreCase))
            {
                // 'resize:WxH' — paneles expandibles de widgets flotantes chicos
                // (ej: guia-rapida.html al desplegar el panel de un ícono agrupado).
                string spec = msg.Substring("resize:".Length);
                string[] parts = spec.Split(new[] { 'x', 'X' }, 2);
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int w)
                    && int.TryParse(parts[1], out int h))
                {
                    try { BeginInvoke(new Action(() => ResizeFloatingWidget(w, h))); } catch { }
                }
            }
        }

        // ---------- Esquinas redondeadas de la ventana (Windows 11) ----------
        // Los widgets HTML dibujan tarjetas con border-radius, pero la ventana
        // WinForms es rectangular y se ve el fondo cuadrado del WebView2 en las
        // esquinas. DWMWA_WINDOW_CORNER_PREFERENCE = ROUND le pide al compositor
        // que recorte la ventana con esquinas redondeadas (antialiasing incluido).
        // En Windows 10 el atributo no existe y la llamada falla silenciosa (no-op).
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void TryRoundCorners()
        {
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            try
            {
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* Win10 o DWM apagado: la ventana queda cuadrada, sin romper */ }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            // Desengancho los listeners del anchor para no dejar referencias a
            // controles destruidos.
            try
            {
                if (_anchorHooked != null)
                {
                    _anchorHooked.SizeChanged -= OnAnchorChanged;
                    _anchorHooked.LocationChanged -= OnAnchorChanged;
                    _anchorHooked.HandleDestroyed -= OnAnchorDestroyed;
                    _anchorHooked = null;
                }
                if (_anchorParentForm != null)
                {
                    _anchorParentForm.Move -= OnAnchorChanged;
                    _anchorParentForm.SizeChanged -= OnAnchorChanged;
                    _anchorParentForm = null;
                }
            }
            catch { }

            // Solo apagamos el host si lo creamos nosotros (fallback).
            // Si lo levanto FormGPS via Bootstrap, lo apaga FormGPS al cerrar PilotX.
            if (_ownsHost)
            {
                try { _flowxBridge?.Stop(); _flowxBridge?.Dispose(); } catch { }
                try { _webHost?.Stop(); } catch { }
                try { _nodos?.Stop(); } catch { }
                _flowxBridge = null;
            }
            try { _webView?.Dispose(); } catch { }
            _webHost = null;
            _nodos = null;
            _webView = null;
        }

        // Lee broker/port desde vistaX.json sin depender del proyecto GPS.
        // Si el archivo no existe o es inválido, devuelve (null, 1883) y
        // simplemente no se arranca el descubrimiento MQTT.
        private static (string addr, int port) LoadBrokerConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vistaX.json");
                if (!File.Exists(path)) return (null, 1883);

                string json = File.ReadAllText(path);
                using (var doc = JsonDocument.Parse(json))
                {
                    string addr = null;
                    int port = 1883;
                    if (doc.RootElement.TryGetProperty("BrokerAddress", out var ja))
                        addr = ja.GetString();
                    if (doc.RootElement.TryGetProperty("BrokerPort", out var jp) && jp.ValueKind == JsonValueKind.Number)
                        port = jp.GetInt32();
                    return (addr, port > 0 ? port : 1883);
                }
            }
            catch
            {
                return (null, 1883);
            }
        }
    }
}
