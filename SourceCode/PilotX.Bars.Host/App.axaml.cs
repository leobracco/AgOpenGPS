using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;
using PilotX.Cockpit.Bars.Views;

namespace PilotX.Bars.Host;

public partial class App : Application
{
    // Thickness de cada barra (px logicos). Constantes compartidas para que
    // los insets de Left/Right (que dejan libre el alto de Top/Bottom) nunca
    // se desincronicen del thickness real de esas dos barras.
    private const double TopThickness = 46;
    private const double RightThickness = 58;
    private const double BottomThickness = 58;
    private const double LeftThickness = 92;        // angosta: solo la columna principal
    private const double LeftExpanded = 250;        // ancha: columna + submenú al lado

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No hay MainWindow: son 4 overlays independientes. Sin esto el
            // lifetime clasico cierra la app apenas se oculta/cierra alguna
            // ventana (el default vigila "la" ventana principal, que aca no
            // existe); el cierre real lo maneja WatchParent() con Shutdown().
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var http = new HttpClient();
            var cmd = new GuidanceCommandClient(http, Program.BaseUrl);
            var vmSup = new BarraSuperiorViewModel(cmd);
            var vmDer = new BarraDerechaViewModel(cmd);
            var vmAba = new BarraAbajoViewModel(cmd);
            var vmIzq = new MenuIzquierdaViewModel(cmd);

            var top    = new BarWindow(BarEdge.Top,    TopThickness,    new BarraSuperior  { DataContext = vmSup });
            var right  = new BarWindow(BarEdge.Right,  RightThickness,  new BarraDerecha   { DataContext = vmDer }, topInset: TopThickness, bottomInset: BottomThickness);
            var bottom = new BarWindow(BarEdge.Bottom, BottomThickness, new BarraAbajo     { DataContext = vmAba });
            var left   = new BarWindow(BarEdge.Left,   LeftThickness,   new MenuIzquierda  { DataContext = vmIzq }, topInset: TopThickness, bottomInset: BottomThickness);

            var poller = new CockpitStateClient(Program.BaseUrl);
            // Right/bottom arrancan OCULTAS (no Show() abajo): si no hay lote
            // iniciado no deben parpadear visibles durante el primer poll.
            // Este flag evita llamar Show()/Hide() en cada snapshot (250ms)
            // cuando el estado no cambio.
            bool rightBottomVisible = false;
            poller.SnapshotReceived += s => Dispatcher.UIThread.Post(() =>
            {
                vmSup.Apply(s);
                vmDer.Apply(s);
                vmAba.Apply(s);

                // right/bottom se muestran solo cuando hay lote activo.
                // Nota: en Avalonia 11 seteando Window.IsVisible = false una
                // ventana YA mostrada no se oculta de forma confiable en
                // Windows (el backing HWND se pinta invisible pero el hit-test
                // WS_EX_NOACTIVATE puede seguir robando el area). Se usa
                // Hide()/Show() (WindowBase) que si garantizan ShowWindow(SW_HIDE)/
                // (SW_SHOWNOACTIVATE) a nivel Win32.
                if (s.IsJobStarted != rightBottomVisible)
                {
                    rightBottomVisible = s.IsJobStarted;
                    if (rightBottomVisible) { right.Show(); bottom.Show(); }
                    else { right.Hide(); bottom.Hide(); }
                }
            });

            // La barra izquierda se ensancha cuando hay un submenú abierto (para
            // mostrar la lista al lado de la columna principal) y vuelve a angosta
            // al cerrarlo. Reemplaza el viejo resize:WxH del widget WebView2.
            vmIzq.PropertyChanged += (_, e) =>
            {
                // PropertyChanged llega en el UI thread (viene del click) -> ensancho
                // sincrónico, así la ventana ya está ancha cuando el submenú se hace
                // visible y sus botones se miden con el ancho correcto.
                if (e.PropertyName == nameof(MenuIzquierdaViewModel.OpenSubmenu))
                    left.SetThickness(vmIzq.OpenSubmenu != null ? LeftExpanded : LeftThickness);
            };

            top.Show(); left.Show();
            poller.Start();

            // Task 10 (fix spec): re-anclar las 4 barras si cambia la
            // resolución/pantalla (modo de video, monitor reconectado, etc.).
            // En un kiosco fijo esto rara vez dispara, pero el spec de diseño
            // lo exige explícitamente. Screens.Changed (Avalonia 11.2.3) avisa
            // ante cualquier cambio de la configuración de pantallas; alcanza
            // con suscribirse una vez (comparten el mismo IScreenImpl).
            top.Screens.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                top.Reposition();
                right.Reposition();
                bottom.Reposition();
                left.Reposition();
            });

            Program.WatchParent(() => Dispatcher.UIThread.Post(() =>
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
