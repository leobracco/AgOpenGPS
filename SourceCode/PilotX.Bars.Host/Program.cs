using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;

namespace PilotX.Bars.Host;

internal static class Program
{
    public static int ParentPid = -1;
    public static string BaseUrl = "http://127.0.0.1:5180/";

    // Referencia retenida del proceso padre vigilado: aunque EnableRaisingEvents
    // ya mantiene vivo el wait handle, guardar la referencia explícita evita
    // dejarla librada al GC (más claro y a prueba de futuros refactors).
    private static Process? _parentProc;

    [STAThread]
    public static void Main(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--parent-pid=") && int.TryParse(a.Substring(13), out var p)) ParentPid = p;
            else if (a.StartsWith("--base-url=")) BaseUrl = a.Substring(11);
        }
        // Single-instance: si ya hay un Host, salir.
        bool created;
        using var mtx = new Mutex(true, "PilotX.Bars.Host.Singleton", out created);
        if (!created) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    // Cierra el Host cuando muere FormGPS (parent).
    public static void WatchParent(Action onParentExit)
    {
        if (ParentPid <= 0) return;
        try
        {
            var proc = Process.GetProcessById(ParentPid);
            _parentProc = proc; // retener referencia: ver comentario en el campo
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => onParentExit();
        }
        catch { onParentExit(); } // parent ya no existe
    }
}
