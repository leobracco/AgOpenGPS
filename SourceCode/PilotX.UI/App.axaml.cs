using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PilotX.Desktop.Services;

namespace PilotX.Desktop;

public partial class App : Application
{
    // --singleview: en Desktop monta la vista portable (Views.MainView) dentro de
    // una Window, igual que la montará el head Android (ISingleViewApplicationLifetime).
    // Sirve para probar la pantalla Android-ready sin una tablet. Default false
    // (Desktop usa MainWindow, con todos los overlays/diálogos).
    public static bool UseSingleView { get; set; } = false;

    // Backend de WebView inyectado por el head (Desktop = WebView.Avalonia,
    // Android = WebView nativo). La UI compartida NO depende de ningún paquete
    // WebView; si es null, las pantallas HTML no portadas no abren (el mapa y
    // lo nativo siguen andando). Ver Services/IWebViewHost.cs.
    public static IWebViewHost? WebViewHost { get; set; }

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

    // Render del mapa de guiado: GL por DEFAULT. Skia con --gl=off, y OJO con
    // lo que Skia NO hace (ver abajo) antes de mandarlo a nadie.
    //
    // El 2026-08-04 se probó pasar el default a Skia como red de seguridad
    // contra el mapa negro, y hubo que volver atrás el mismo día: en cabina
    // "no hace zoom, no pinta, no veo las guías contiguas". Mirando qué
    // reenvía MapPanel cuando _gl es null, todo esto queda en no-op:
    //
    //     ZoomIn/ZoomOut/rueda · OnCoverage (lo trabajado) · OnPaths (guías
    //     contiguas) · OnShape (prescripción) · sprites de vehículo, rueda,
    //     implemento y piso · OnFlags · lightbar · heading-up · grilla ·
    //     día/noche · creación de línea AB
    //
    // Skia solo recibe OnSnapshot, OnGuidance, OnTool y OnTram. No es "GL sin
    // cobertura": es un mapa al que le falta casi todo. Sirve para ver dónde
    // está el tractor y nada más.
    //
    // El bug que motivó todo esto, medido con 20 ciclos de abrir/cerrar lote
    // por API (~3,5 min, lo dispara siempre):
    //
    //   · 12 de 20 ciclos congelan la surface GL, con el mapa en negro en 23
    //     de 42 latidos. (La primera medición dio "5 de 20" y era un PISO: el
    //     watchdog se rendía a los 5 intentos y dejaba de contar.)
    //   · en el congelamiento Avalonia DEJA DE LLAMAR OnOpenGlRender
    //     (entradas=0 con el control enganchado al árbol y _tickSuave pidiendo
    //     frame a 30 Hz) — no es que salgamos temprano nosotros;
    //   · el watchdog de MapPanel rehace la surface y la nueva se muere igual,
    //     porque el contexto GL es del compositor de Avalonia y está
    //     compartido: un control nuevo NO trae contexto nuevo;
    //   · a los 5 intentos el watchdog se rinde y el mapa queda negro hasta
    //     reiniciar PilotX a mano.
    //
    // Todo eso SIN excepciones (errores.log no creció) y SIN TDR del driver.
    // O sea que no hay nada que atrapar ni reintentar desde acá.
    //
    // Así que la convivencia con el bug es al revés: GL sigue de default y el
    // watchdog de MapPanel ya no se rinde nunca (antes se plantaba a los 5
    // intentos y ahí el mapa quedaba negro PARA SIEMPRE). Recrear la surface
    // devuelve el render — medido, las surfaces nuevas dibujan a 24-39 fps —
    // así que reintentar indefinidamente convierte "negro para siempre" en
    // "negro unos 12 segundos". No es la cura, es el torniquete.
    //
    //     PilotX.Desktop.exe --gl=off     -> Skia, con todo lo que le falta
    public static bool UseGl { get; set; } = true;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (UseSingleView)
            {
                // Vista portable montada en una Window borderless-maximizada (mismo
                // control que usará Android). Prueba de la pantalla Android-ready.
                desktop.MainWindow = new Window
                {
                    Title = "PilotX",
                    SystemDecorations = SystemDecorations.None,
                    WindowState = WindowState.Maximized,
                    Content = new Views.MainView(),
                };
            }
            else
            {
                desktop.MainWindow = new MainWindow();

                // Cerrar PilotX apaga TODO el stack: el Engine (guiado, broker
                // MQTT :1883, API :5180) y el host de barras no deben quedar
                // huérfanos consumiendo puertos y RAM — el operario cierra UNA
                // ventana y la máquina queda limpia. Guardados críticos van por
                // AtomicJson, así que el kill es tan seguro como un corte de luz
                // (que la cabina ya tolera). Solo en modo pantalla completa: un
                // widget flotante no es dueño del stack.
                desktop.MainWindow.Closed += (_, _) => ApagarStackCompleto();
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Android / single-view: la vista portable es la raíz.
            singleView.MainView = new Views.MainView();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void ApagarStackCompleto()
    {
        foreach (var nombre in new[] { "PilotX.GuidanceEngine", "PilotX.Bars.Host" })
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(nombre))
                {
                    try { p.Kill(); } catch { /* ya estaba muriendo */ }
                }
            }
            catch { /* sin permisos/procesos: no bloquear el cierre */ }
        }
    }
}
