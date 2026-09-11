using System;
using System.Runtime.InteropServices;

namespace PilotX.Bars.Host;

/// <summary>Contracara X11 de Win32NoActivate: pone el input hint de ICCCM en
/// False (XWMHints) para que el window manager no le dé el foco de teclado a
/// la barra al clickearla — la pantalla principal lo conserva. Mismo criterio
/// que el teclado en pantalla de PilotX.Desktop.</summary>
internal static class X11NoActivate
{
    private const long InputHint = 1 << 0;

    // XWMHints en x64: flags(long) input(Bool=int) initial_state(int)
    // icon_pixmap/icon_window(XID) icon_x icon_y icon_mask window_group.
    [StructLayout(LayoutKind.Sequential)]
    private struct XWMHints
    {
        public IntPtr flags;
        public int input;
        public int initial_state;
        public IntPtr icon_pixmap;
        public IntPtr icon_window;
        public int icon_x;
        public int icon_y;
        public IntPtr icon_mask;
        public IntPtr window_group;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XSetWMHints(IntPtr display, IntPtr window, ref XWMHints hints);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);

    public static void Apply(IntPtr xid)
    {
        if (xid == IntPtr.Zero) return;
        IntPtr display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            var hints = new XWMHints { flags = new IntPtr(InputHint), input = 0 };
            XSetWMHints(display, xid, ref hints);
            XFlush(display);
        }
        catch { /* Wayland puro o sin libX11: la barra funciona igual */ }
        finally
        {
            try { if (display != IntPtr.Zero) XCloseDisplay(display); } catch { }
        }
    }
}
