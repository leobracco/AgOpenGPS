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
//   --gl=on|off                     -> render OpenGL del mapa. Default ON.
//                                      --gl=off usa Skia, que dibuja MUCHO
//                                      menos (sin zoom, cobertura, guías
//                                      contiguas ni sprites): ver App.UseGl
//                                      antes de mandárselo a alguien.

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
        // ANTES que nada: sin esto, cualquier excepción no atendida le tira al
        // operario el cartel de Windows ("dejó de funcionar / ver detalles"),
        // que en la cabina no le sirve a nadie y termina en cerrar y perder la
        // jornada. Ver CrashHandler.
        CrashHandler.Instalar();

        // BuildAvaloniaApp llama .LogToTrace(), que manda los diagnósticos de
        // Avalonia a System.Diagnostics.Trace. Sin un listener registrado, Trace
        // los tira: veníamos corriendo con el log del framework apagado sin
        // saberlo. Importa para el mapa negro — cuando el compositor no puede
        // renderizar un control, se lo traga y lo reporta por acá, no por
        // excepción (errores.log quedó en cero durante 5 congelamientos
        // seguidos). Va a stderr, que es donde ya escribe el diagnóstico del
        // mapa, así queda todo en la misma línea de tiempo.
        System.Diagnostics.Trace.Listeners.Add(
            new System.Diagnostics.TextWriterTraceListener(Console.Error));
        System.Diagnostics.Trace.AutoFlush = true;

        // Sink de audio para las alarmas de cabina: el poller portable
        // (PilotX.UI) entrega el WAV y el head lo toca con lo que haya —
        // winmm en Windows, aplay/paplay en Linux.
        PilotX.Desktop.Services.SoundAlarmPoller.WavSink =
            OperatingSystem.IsWindows() ? WinmmWavPlayer.Play : LinuxWavPlayer.Play;

        // Soporte remoto integrado: si el paquete trae RustDesk y no está
        // instalado, la PRIMERA vez dispara la única elevación que existe
        // (UAC en Windows, pkexec en Linux) y nunca más. Cero instalación
        // manual — la integración resuelve el OS adentro.
        RustDeskIntegracion.AsegurarEnSegundoPlano();

        App.ColdStart = Stopwatch.StartNew();
        // Inyectar el backend de WebView del head Desktop (WebView.Avalonia).
        // La UI compartida (PilotX.UI) solo conoce IWebViewHost.
        App.WebViewHost = new DesktopWebViewHost();
        ParseArgs(args);

        // Teclado en pantalla: vive en su PROPIA ventana, no dentro de la
        // pagina. Este poller es el que la abre cuando alguien toca un campo
        // (las paginas del Hub avisan por el engine) y el que le dice al
        // engine que hay teclado nativo; si PilotX no esta corriendo, las
        // paginas se dan cuenta y usan su teclado HTML.
        try { new TecladoPoller(App.TargetUrl).Start(); } catch { }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Backend WGL en vez de ANGLE (--diag-wgl). Lo setea ParseArgs,
    /// que corre ANTES de BuildAvaloniaApp en Main.</summary>
    public static bool UsarWgl;

    public static AppBuilder BuildAvaloniaApp()
    {
        var b = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseDesktopWebView()
            .WithInterFont()
            .LogToTrace();
        if (UsarWgl)
        {
            // Sin ANGLE: contexto desktop GL directo contra el driver, sin
            // puente D3D11 ni textura compartida con keyed mutex. Software
            // queda de fallback por si el driver no da WGL utilizable.
            b = b.With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.Software
                }
            });
            Console.Error.WriteLine("[Program] DIAG: RenderingMode=WGL (sin ANGLE)");
        }
        return b;
    }

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
            // El WebView se baja solo tras unos minutos sin usarse (son ~260 MB
            // de Chromium ocioso). Estos dos lo ajustan sin recompilar:
            //   --webview-siempre        -> no bajarlo nunca (comportamiento viejo)
            //   --webview-ocioso=<min>   -> cambiar el plazo; 0 = no bajarlo
            else if (a.Equals("--webview-siempre", StringComparison.OrdinalIgnoreCase))
                App.WebViewSiempre = true;
            else if (a.StartsWith("--webview-ocioso=", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(a.Substring("--webview-ocioso=".Length),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var min))
                {
                    if (min <= 0) App.WebViewSiempre = true;
                    else App.WebViewOcioso = TimeSpan.FromMinutes(min);
                }
            }
            // Interruptores de DIAGNÓSTICO del congelamiento al abrir lote.
            // Apagados por default; sirven para partir en dos lo que pasa en
            // ese instante y ver cuál de las dos mitades lo dispara.
            else if (a.Equals("--diag-sin-encuadre", StringComparison.OrdinalIgnoreCase))
            {
                App.DiagSinEncuadre = true;
            }
            else if (a.Equals("--diag-sin-geometria", StringComparison.OrdinalIgnoreCase))
            {
                App.DiagSinGeometria = true;
            }
            // Experimento: backend WGL en vez de ANGLE. Sin ANGLE no hay puente
            // D3D11 ni textura compartida con keyed mutex — si el congelamiento
            // desaparece con esto, la causa vive en esa capa. Los shaders ya
            // tienen preludio dual (300 es / 330 core por GlVersion.Type), así
            // que el contexto desktop GL compila sin tocar nada más.
            else if (a.Equals("--diag-wgl", StringComparison.OrdinalIgnoreCase))
            {
                UsarWgl = true;
            }
            else if (a.Equals("--diag-sin-lindero", StringComparison.OrdinalIgnoreCase))
            {
                App.DiagSinLindero = true;
            }
            else if (a.Equals("--diag-sin-guias", StringComparison.OrdinalIgnoreCase))
            {
                App.DiagSinGuias = true;
            }
            else if (a.Equals("--singleview", StringComparison.OrdinalIgnoreCase))
            {
                // Prueba en Desktop de la vista portable (Views.MainView) que usará
                // el head Android. Monta MainView en una Window en vez de MainWindow.
                App.UseSingleView = true;
            }
        }
        if (string.IsNullOrEmpty(page)) App.TargetUrl = baseUrl;
        else App.TargetUrl = baseUrl.TrimEnd('/') + "/" + page.TrimStart('/');
    }
}
