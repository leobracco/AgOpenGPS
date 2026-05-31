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

            _webView = new WebView2 { Dock = DockStyle.Fill };
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

            // Hook (idempotente): solo una vez por instancia de control.
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

        private void OnAnchorChanged(object sender, EventArgs e) { UpdateAnchorBounds(); }

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
            if (AnchorControl != null)
            {
                try { ApplyAnchor(); } catch { }
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
                    var vistaxLive = new VistaXLiveService(_nodos, vistaxCfg, insumosCat);
                    var flowxCfg = new FlowXConfigService();
                    var flowxLive = new FlowXLiveService(_nodos, flowxCfg);
                    var stormxCfg = new StormXConfigService();
                    var stormxLive = new StormXLiveService(_nodos, stormxCfg);
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
                        System.Diagnostics.Debug.WriteLine("[HubWV2] FlowXBridge start: " + bex.Message);
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
                    target = target.TrimEnd('/') + "/" + InitialPage.TrimStart('/');
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
