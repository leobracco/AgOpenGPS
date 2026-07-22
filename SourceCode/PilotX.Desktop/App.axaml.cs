using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PilotX.Desktop;

public partial class App : Application
{
    // URL final a la que navega el WebView. Program.Main la arma parseando args.
    // Default: el AgpWebHost del shell legacy (WinForms) en 127.0.0.1:5180.
    public static string TargetUrl { get; set; } = "http://127.0.0.1:5180/";

    // Stopwatch del cold-start. Lo dispara Program.Main al inicio; MainWindow
    // lo detiene en NavigationCompleted del WebView e imprime los ms a stdout.
    public static Stopwatch ColdStart { get; set; }

    // Modo de ventana: "full" (maximizada borderless, default — Hub principal
    // de cabina) o "float" (ventana chica con chrome propio, encima de PilotX,
    // redimensionable). Los widgets que tienen que dejar ver la pantalla del
    // piloto detras usan float.
    public static string WindowMode { get; set; } = "full";

    // Titulo de ventana (visible solo en modo float). Util para distinguir
    // multiples widgets abiertos al mismo tiempo.
    public static string WindowTitle { get; set; } = "PilotX";

    // Tamano inicial en modo float (en pixeles). 0 = usar default.
    public static int WindowWidth { get; set; } = 0;
    public static int WindowHeight { get; set; } = 0;

    // Render del mapa de guiado. Cuando es true (DEFAULT desde Stage 7),
    // MapPanel hostea MapGlSurface (Avalonia OpenGlControlBase +
    // Silk.NET.OpenGL) con todas las capas (grid/coverage/guías/secciones/
    // tram/paths) y cámara zoom+pan. Ya no requiere --gl=on. El Skia legacy
    // (MapSkiaSurface) queda solo como escape hatch con --gl=off por si una
    // GPU no arranca GL; se retira cuando GL esté probado en cabina.
    public static bool UseGl { get; set; } = true;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
