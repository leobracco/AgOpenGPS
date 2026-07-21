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

            var top    = new BarWindow(BarEdge.Top,    64, new BarraSuperior  { DataContext = vmSup });
            var right  = new BarWindow(BarEdge.Right,  74, new BarraDerecha   { DataContext = vmDer });
            var bottom = new BarWindow(BarEdge.Bottom, 74, new BarraAbajo     { DataContext = vmAba });
            var left   = new BarWindow(BarEdge.Left,   94, new MenuIzquierda  { DataContext = vmIzq });

            var poller = new CockpitStateClient(Program.BaseUrl);
            // Arrancan mostradas (Show() mas abajo); este flag evita llamar
            // Show()/Hide() en cada snapshot (250ms) cuando el estado no cambio.
            bool rightBottomVisible = true;
            poller.SnapshotReceived += s => Dispatcher.UIThread.Post(() =>
            {
                vmSup.Apply(s);
                vmDer.Apply(s);
                vmAba.Apply(s);

                // right/bottom se ocultan cuando no hay lote activo.
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

            top.Show(); left.Show(); right.Show(); bottom.Show();
            poller.Start();

            Program.WatchParent(() => Dispatcher.UIThread.Post(() =>
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
