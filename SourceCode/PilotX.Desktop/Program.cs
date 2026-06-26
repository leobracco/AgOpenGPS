// Program.cs - entrypoint del shell PilotX.Desktop (Avalonia).
//
// Estrategia strangler fig: el shell WinForms (FormGPS + AgroParallel.Shell
// WebView2) sigue siendo productivo. Este shell crece en paralelo y se
// promueve cuando llegue a paridad. Por ahora apunta al mismo AgpWebHost
// en 127.0.0.1:5180 que ya levanta el shell legacy.
//
// Args soportados (mismos que el ex-spike):
//   --page=pages/camaras.html       -> http://127.0.0.1:5180/pages/camaras.html
//   --url=http://otro:port/algo     -> usa esa URL tal cual
//   --mode=float                    -> ventana chica con chrome PilotX, encima del Hub
//   --mode=full                     -> maximizada borderless (default, Hub principal)
//   --title="Camaras"               -> titulo de la ventana (solo modo float)
//   --width=800 --height=480        -> tamano inicial en modo float
//   --gl=on|off                     -> usa render OpenGL del mapa (Stage 1
//                                      de la migracion FormGPS -> Avalonia).
//                                      Default off mientras estabilizamos.

using System;
using System.Diagnostics;
using Avalonia;
using AvaloniaWebView;
using Avalonia.WebView.Desktop;

namespace PilotX.Desktop;

internal static class Program
{
    private const string DefaultBase = "http://127.0.0.1:5180/";

    [STAThread]
    public static void Main(string[] args)
    {
        // Stopwatch para medir cold-start del shell. Lo arrancamos lo antes
        // posible en Main para que el numero refleje wall-clock desde el
        // doble click hasta el primer render HTML. MainWindow lo lee en
        // NavigationCompleted y lo imprime a stdout.
        App.ColdStart = Stopwatch.StartNew();
        ParseArgs(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseDesktopWebView()
            .WithInterFont()
            .LogToTrace();

    private static void ParseArgs(string[] args)
    {
        string baseUrl = DefaultBase;
        string page = null;
        foreach (var raw in args ?? Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var a = raw.Trim();
            if (a.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
            { App.TargetUrl = a.Substring("--url=".Length); return; }
            if (a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                page = a.Substring("--page=".Length);
            else if (a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
                App.WindowMode = a.Substring("--mode=".Length).ToLowerInvariant();
            else if (a.StartsWith("--title=", StringComparison.OrdinalIgnoreCase))
                App.WindowTitle = a.Substring("--title=".Length).Trim('"');
            else if (a.StartsWith("--width=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(a.Substring("--width=".Length), out var w)) App.WindowWidth = w;
            }
            else if (a.StartsWith("--height=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(a.Substring("--height=".Length), out var h)) App.WindowHeight = h;
            }
            else if (a.StartsWith("--gl=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a.Substring("--gl=".Length).Trim().ToLowerInvariant();
                App.UseGl = v == "on" || v == "1" || v == "true" || v == "yes";
            }
        }
        if (string.IsNullOrEmpty(page)) App.TargetUrl = baseUrl;
        else App.TargetUrl = baseUrl.TrimEnd('/') + "/" + page.TrimStart('/');
    }
}
