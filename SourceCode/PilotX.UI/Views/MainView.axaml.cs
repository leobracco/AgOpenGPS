// MainView.axaml.cs — pantalla LIVE portable (mapa + barras del cockpit).
//
// L5 del port Android: es la vista que el head single-view (PilotX.Android.App)
// monta como MainView, y también es reusable en Desktop. Hostea el mapa GL + las
// 4 barras del cockpit y cablea los pollers live contra el engine (:5180),
// reusando los MISMOS servicios cliente que MainWindow (que son portables).
//
// A diferencia de MainWindow, NO trae los overlays/diálogos/WebView (específicos
// de escritorio o dependientes de IWebViewHost) ni el HUD nativo: es el MVP de la
// pantalla de guiado. Las operaciones de ventana (min/max/cerrar) se resuelven
// contra la Window contenedora si existe (Desktop); en Android son no-op.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;
using PilotX.Cockpit.Bars.Views;
using PilotX.Desktop.Services;
using PilotX.Desktop.Views;

namespace PilotX.Desktop.Views
{
    public partial class MainView : UserControl
    {
        private MapPanel? _mapHost;
        private BarraSuperior? _barSuperior;
        private BarraDerecha? _barDerecha;
        private BarraAbajo? _barAbajo;
        private MenuIzquierda? _menuIzq;

        // Overlay del WebView (pantallas HTML del Hub en single-view Android).
        private Grid? _webOverlay;
        private Panel? _webSlot;
        private TextBlock? _webTitle;
        private IWebViewHandle? _webView;
        private SistemaClient? _sistemaClient; // lazy, solo para brillo_up/brillo_dn

        private BarraSuperiorViewModel? _vmSup;
        private BarraDerechaViewModel? _vmDer;
        private BarraAbajoViewModel? _vmAba;
        private MenuIzquierdaViewModel? _vmIzq;

        private HttpClient? _cockpitHttp;
        private GuidanceCommandClient? _cockpitCmd;
        private CockpitStateClient? _cockpitPoller;

        private HudPoller? _hudPoller;
        private CoveragePoller? _coveragePoller;
        private GuidanceGeometryPoller? _guidancePoller;
        private ToolGeometryPoller? _toolPoller;
        private TramGeometryPoller? _tramPoller;
        private PathsGeometryPoller? _pathsPoller;

        // Debug de guiado (mismo formato que la BarraSuperior de MainWindow).
        private double _lastTractorHeadingDeg = double.NaN;
        private double _lastGuideHeadingDeg = double.NaN;
        private double _lastPathsAway = double.NaN;
        private double _lastXteMeters = double.NaN;

        private const double MenuIzqCollapsed = 40;
        private const double MenuIzqNarrow = 140;
        private const double MenuIzqExpanded = 316;

        private readonly List<Action> _cleanup = new List<Action>();
        private bool _started;

        public MainView()
        {
            InitializeComponent();

            _mapHost     = this.FindControl<MapPanel>("MapHost");
            _barSuperior = this.FindControl<BarraSuperior>("BarSuperior");
            _barDerecha  = this.FindControl<BarraDerecha>("BarDerecha");
            _barAbajo    = this.FindControl<BarraAbajo>("BarAbajo");
            _menuIzq     = this.FindControl<MenuIzquierda>("MenuIzq");

            _webOverlay  = this.FindControl<Grid>("WebOverlay");
            _webSlot     = this.FindControl<Panel>("WebSlot");
            _webTitle    = this.FindControl<TextBlock>("WebTitle");
            var webClose = this.FindControl<Button>("WebClose");
            if (webClose != null) webClose.Click += (_, _) => CloseWeb();

            // El wiring de red arranca cuando la vista entra al árbol visual (una
            // sola vez), para que Android/Desktop la instancien sin efectos de red
            // en el constructor.
            AttachedToVisualTree += (_, _) => StartLive();
            DetachedFromVisualTree += (_, _) =>
            {
                foreach (var c in _cleanup) { try { c(); } catch { } }
                _cleanup.Clear();
                _started = false;
            };
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private static string DeriveOrigin(string url)
        {
            try { var u = new Uri(url); return $"{u.Scheme}://{u.Host}:{u.Port}"; }
            catch { return "http://127.0.0.1:5180"; }
        }

        private void StartLive()
        {
            if (_started) return;
            _started = true;

            string origin = DeriveOrigin(App.TargetUrl);

            SetupCockpitBars(origin);

            // HUD (velocidad/heading/estado) → mapa + rumbo del tractor.
            _hudPoller = new HudPoller(baseUrl: origin, intervalMs: 250);
            _hudPoller.SnapshotReceived += OnHudSnapshot;
            _hudPoller.Start();
            _cleanup.Add(() => _hudPoller?.Dispose());

            if (App.UseGl)
            {
                var cov = new CoverageClient(origin);
                _coveragePoller = new CoveragePoller(cov, snap => _mapHost?.OnCoverage(snap), periodMs: 350);
                _coveragePoller.Start();
                _cleanup.Add(() => _coveragePoller?.Stop());

                var pc = new PathsGeometryClient(origin);
                _pathsPoller = new PathsGeometryPoller(pc, snap => _mapHost?.OnPaths(snap), periodMs: 1000);
                _pathsPoller.Start();
                _cleanup.Add(() => _pathsPoller?.Stop());
            }

            var gg = new GuidanceGeometryClient(origin);
            _guidancePoller = new GuidanceGeometryPoller(gg, snap =>
            {
                _mapHost?.OnGuidance(snap);
                _lastGuideHeadingDeg = ComputeGuideHeadingDeg(snap);
                _lastPathsAway = (snap.PathsAway == int.MinValue) ? double.NaN : snap.PathsAway;
                _lastXteMeters = snap.XteMeters;
                UpdateHeadingDebug();
            }, periodMs: 1000);
            _guidancePoller.Start();
            _cleanup.Add(() => _guidancePoller?.Stop());

            var tg = new ToolGeometryClient(origin);
            _toolPoller = new ToolGeometryPoller(tg, snap => _mapHost?.OnTool(snap), periodMs: 250);
            _toolPoller.Start();
            _cleanup.Add(() => _toolPoller?.Stop());

            var trc = new TramGeometryClient(origin);
            _tramPoller = new TramGeometryPoller(trc, snap => _mapHost?.OnTram(snap), periodMs: 1000);
            _tramPoller.Start();
            _cleanup.Add(() => _tramPoller?.Stop());
        }

        private void SetupCockpitBars(string origin)
        {
            _cockpitHttp = new HttpClient();
            _cockpitCmd = new GuidanceCommandClient(_cockpitHttp, origin);
            _cockpitCmd.LocalHandler = RouteCockpitCommand;

            _vmSup = new BarraSuperiorViewModel(_cockpitCmd);
            _vmDer = new BarraDerechaViewModel(_cockpitCmd);
            _vmAba = new BarraAbajoViewModel(_cockpitCmd);
            _vmIzq = new MenuIzquierdaViewModel(_cockpitCmd);

            if (_barSuperior != null) _barSuperior.DataContext = _vmSup;
            if (_barDerecha  != null) _barDerecha.DataContext  = _vmDer;
            if (_barAbajo    != null) _barAbajo.DataContext    = _vmAba;
            if (_menuIzq     != null) _menuIzq.DataContext      = _vmIzq;

            _vmIzq.PropertyChanged += (_, e) =>
            {
                if (_menuIzq == null) return;
                if (e.PropertyName == nameof(MenuIzquierdaViewModel.OpenSubmenu)
                    || e.PropertyName == nameof(MenuIzquierdaViewModel.IsCollapsed))
                {
                    _menuIzq.Width = _vmIzq!.IsCollapsed ? MenuIzqCollapsed
                        : (_vmIzq.OpenSubmenu != null ? MenuIzqExpanded : MenuIzqNarrow);
                }
            };

            _cockpitPoller = new CockpitStateClient(origin, intervalMs: 250);
            _cockpitPoller.SnapshotReceived += snap => Dispatcher.UIThread.Post(() =>
            {
                _vmSup?.Apply(snap);
                _vmDer?.Apply(snap);
                _vmAba?.Apply(snap);
            });
            _cockpitPoller.Start();
            _cleanup.Add(() => { try { _cockpitPoller?.Dispose(); } catch { } });
            _cleanup.Add(() => { try { _cockpitHttp?.Dispose(); } catch { } });
        }

        // Ruteo mínimo: solo acciones de ventana (contra la Window contenedora si
        // existe; en Android no-op). El resto (guiado, secciones, guías…) devuelve
        // false → sigue al backend por HTTP. Las pantallas de config/overlays que
        // maneja MainWindow no aplican en este MVP.
        private bool RouteCockpitCommand(string cmd)
        {
            var w = TopLevel.GetTopLevel(this) as Window;
            switch (cmd)
            {
                case "minimizar": if (w != null) w.WindowState = WindowState.Minimized; return true;
                case "maximizar":
                    if (w != null)
                        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    return true;
                case "apagar": w?.Close(); return true;

                // CoreX de sistema: dashboard en :5181 (cada pantalla su propio CoreX).
                case "corex":     OpenUrl("http://127.0.0.1:5181/", "CoreX"); return true;
                case "corex_ecu": OpenPage("pages/corex-ecu.html", "CoreX-ECU"); return true;
                case "direccion": OpenPage("pages/direccion.html", "Dirección"); return true;
                case "lote_menu":      OpenPage("pages/lote.html", "Lote"); return true;
                case "lote_continuar": OpenPage("pages/lote.html?do=continuar", "Lote"); return true;
                case "lote_nuevo":     OpenPage("pages/lote.html?do=nuevo", "Nuevo lote"); return true;
                case "lote_kml":       OpenPage("pages/lote.html?do=kml", "Lote desde KML"); return true;
                case "lote_datos":     OpenPage("pages/datos-lote.html", "Datos del lote"); return true;
                case "datos_gps":      OpenPage("pages/datos-gps.html", "Datos GPS"); return true;
                case "hub":            OpenPage("pages/hub.html", "Hub"); return true;
                case "webcam":         OpenPage("pages/camaras.html", "Cámaras"); return true;

                // ---- Controles de cámara/vista (menú Navegación) — 100%
                // cliente (MapGlSurface), no tocan el motor. ----
                case "v2d":     _mapHost?.SetHeadingUp(true);  _mapHost?.SetPitchDeg(0);   return true;
                case "v3d":     _mapHost?.SetHeadingUp(true);  _mapHost?.SetPitchDeg(-65); return true;
                case "norte2d": _mapHost?.SetHeadingUp(false); _mapHost?.SetPitchDeg(0);   return true;
                case "tilt_up": _mapHost?.TiltBy(+5); return true;
                case "tilt_dn": _mapHost?.TiltBy(-5); return true;
                case "grilla":  _mapHost?.ToggleGrid(); return true;
                case "dia_noche": _mapHost?.ToggleDayNight(); return true;

                // Brillo de PANTALLA (no del render) — mismo SistemaClient
                // que MainWindow.
                case "brillo_up": AdjustBrightness(+10); return true;
                case "brillo_dn": AdjustBrightness(-10); return true;
            }

            // Comandos que abren una página HTML del Hub (config/gráficos/lote-tools/…).
            string? page = cmd switch
            {
                "config_form"       => "pages/config.html",
                "todos_ajustes"     => "pages/ajustes-todos.html",
                "colores"           => "pages/colores.html",
                "colores_sec"       => "pages/colores-secciones.html",
                "mapeo_color"       => "pages/colores-secciones.html",
                "perfil_nuevo"      => "pages/perfiles.html",
                "perfil_cargar"     => "pages/perfiles.html",
                "perfil_gestion"    => "pages/perfiles.html",
                "directorios"       => "pages/config.html",
                "ayuda"             => "pages/ayuda.html",
                "grafico_direccion" => "pages/grafico-direccion.html",
                "grafico_rumbo"     => "pages/grafico-rumbo.html",
                "grafico_xte"       => "pages/grafico-xte.html",
                "chequeo_roll"      => "pages/grafico-correccion.html",
                "suavizar_ab"       => "pages/suavizar-ab.html",
                "corregir_pos"      => "pages/corregir-posicion.html",
                "visor_eventos"     => "pages/eventos.html",
                "bandera"           => "pages/banderas.html",
                "bandera_latlon"    => "pages/banderas.html",
                "lindero"           => "pages/contorno.html",
                "cabecera"          => "pages/cabecera.html",
                "cabecera_avanzada" => "pages/cabecera-lineas.html",
                "tram_crear"        => "pages/tramline.html",
                "importar_guias"    => "pages/tracks.html",
                "pick"              => "pages/tracks.html",
                "sim_coords"        => "pages/sim-coords.html",
                "asistente_direccion" => "pages/config.html",
                "herr_limites"      => "pages/contorno.html",
                _ => null
            };
            if (page != null) { OpenPage(page, "PilotX"); return true; }

            // Resto → backend de guiado por HTTP.
            return false;
        }

        // Brillo +/− del menú Navegación: lazy-init igual que MainWindow. Si
        // la PC/tablet no soporta brillo (GetBrightnessAsync -1), no hace nada.
        private async void AdjustBrightness(int delta)
        {
            if (_sistemaClient == null)
                _sistemaClient = new SistemaClient(DeriveOrigin(App.TargetUrl));
            int cur = await _sistemaClient.GetBrightnessAsync().ConfigureAwait(true);
            if (cur < 0) return;
            await _sistemaClient.SetBrightnessAsync(cur + delta).ConfigureAwait(true);
        }

        // Abre una página HTML del Hub (relativa al origin del engine) en el overlay.
        private void OpenPage(string relativePath, string title)
        {
            string url = PilotX.Desktop.App.TargetUrl?.TrimEnd('/') ?? "http://127.0.0.1:5180";
            int api = url.IndexOf("/pages/", StringComparison.OrdinalIgnoreCase);
            string origin = api >= 0 ? url.Substring(0, api) : url;
            OpenUrl(origin + "/" + relativePath.TrimStart('/'), title);
        }

        // Abre una URL absoluta en el overlay (ej. dashboard CoreX :5181).
        private void OpenUrl(string full, string title)
        {
            if (PilotX.Desktop.App.WebViewHost == null || _webSlot == null || _webOverlay == null)
                return;
            CloseWeb();  // suelta cualquier web view previo
            _webView = PilotX.Desktop.App.WebViewHost.Create(OnWebNavigated);
            _webSlot.Children.Add(_webView.Control);
            if (_webTitle != null) _webTitle.Text = title;
            _webView.Navigate(full);
            _webOverlay.IsVisible = true;
        }

        // La página pide cerrarse navegando a la URL centinela pilotx-close.
        private void OnWebNavigated(string url)
        {
            if ((url ?? string.Empty).IndexOf("pilotx-close", StringComparison.OrdinalIgnoreCase) >= 0)
                Dispatcher.UIThread.Post(CloseWeb);
        }

        private void CloseWeb()
        {
            if (_webOverlay != null) _webOverlay.IsVisible = false;
            try { _webView?.Release(); } catch { /* best-effort */ }
            if (_webSlot != null) _webSlot.Children.Clear();
            _webView = null;
        }

        private void OnHudSnapshot(HudSnapshot s)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _mapHost?.OnSnapshot(s);
                double deg = (s.Heading * 180.0 / Math.PI) % 360.0;
                if (deg < 0) deg += 360.0;
                _lastTractorHeadingDeg = deg;
                UpdateHeadingDebug();
            });
        }

        private static double ComputeGuideHeadingDeg(GuidanceGeometrySnapshot? snap)
        {
            var pts = snap?.Points;
            if (pts == null || pts.Count < 2) return double.NaN;
            var a = pts[0];
            var b = pts[pts.Count - 1];
            double dE = b.E - a.E, dN = b.N - a.N;
            if (Math.Abs(dE) < 1e-9 && Math.Abs(dN) < 1e-9) return double.NaN;
            double deg = Math.Atan2(dE, dN) * 180.0 / Math.PI;
            if (deg < 0) deg += 360.0;
            return deg;
        }

        private void UpdateHeadingDebug()
        {
            if (_vmSup == null) return;
            bool hasT = !double.IsNaN(_lastTractorHeadingDeg);
            bool hasG = !double.IsNaN(_lastGuideHeadingDeg);

            string s;
            if (!hasG)
            {
                s = hasT ? $"T {_lastTractorHeadingDeg:0}°  ·  sin guía" : "";
            }
            else
            {
                s = hasT
                    ? $"T {_lastTractorHeadingDeg:0}°  ·  G {_lastGuideHeadingDeg:0}°"
                    : $"G {_lastGuideHeadingDeg:0}°";

                if (hasT)
                {
                    double d = _lastGuideHeadingDeg - _lastTractorHeadingDeg;
                    while (d > 180) d -= 360;
                    while (d < -180) d += 360;
                    s += $"  ·  Δ {d:+0;-0;0}°";
                }
                if (!double.IsNaN(_lastPathsAway))
                {
                    int n = (int)_lastPathsAway;
                    string lr = n < 0 ? " izq" : n > 0 ? " der" : "";
                    s += $"  ·  ‖ {n}{lr}";
                }
                if (!double.IsNaN(_lastXteMeters))
                {
                    int cm = (int)Math.Round(Math.Abs(_lastXteMeters) * 100.0);
                    string lr = _lastXteMeters < 0 ? " izq" : _lastXteMeters > 0 ? " der" : "";
                    s += $"  ·  {cm}cm{lr}";
                }
            }

            _vmSup.DebugText = s;
        }
    }
}
