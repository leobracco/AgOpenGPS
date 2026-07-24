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

        private const double MenuIzqNarrow = 100;
        private const double MenuIzqExpanded = 272;

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
                if (e.PropertyName == nameof(MenuIzquierdaViewModel.OpenSubmenu) && _menuIzq != null)
                    _menuIzq.Width = _vmIzq!.OpenSubmenu != null ? MenuIzqExpanded : MenuIzqNarrow;
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
            }
            return false;
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
