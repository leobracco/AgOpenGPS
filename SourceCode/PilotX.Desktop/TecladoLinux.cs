// ============================================================================
// TecladoLinux.cs — contracara Linux/X11 de TecladoWin32: mismas cuatro
// operaciones (ventana al frente, devolver foco, escribir texto, tecla
// virtual) para que el teclado en pantalla escriba también en los campos
// NATIVOS, no solo en las páginas del Hub (que reciben la tecla como dato).
//
// La escritura va por xdotool (XTEST): `windowactivate --sync <id>` +
// `type`/`key` encadenados en UNA invocación — el equivalente exacto del
// combo DevolverFoco + SendInput de Windows. Si xdotool no está instalado se
// avisa UNA vez por stderr y el teclado sigue sirviendo para el Hub (camino
// de dato), igual que siempre.
//
// El "no robar el foco" se hace con el input hint de ICCCM (XWMHints.input =
// False) vía libX11 — es lo que respetan los window managers para paneles y
// teclados en pantalla. Si libX11 no está (Wayland puro), el fallback
// funcional sigue siendo windowactivate-antes-de-escribir.
// ============================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PilotX.Desktop
{
    internal static class TecladoLinux
    {
        private static bool _avisado;

        // --- xdotool ---------------------------------------------------------

        /// <summary>Id X11 de la ventana activa, o Zero si no se pudo saber.</summary>
        public static IntPtr GetActiveWindow()
        {
            string salida = Run("getactivewindow");
            if (salida != null && long.TryParse(salida.Trim(), out long id) && id != 0)
                return new IntPtr(id);
            return IntPtr.Zero;
        }

        /// <summary>Escribe texto en la ventana objetivo (la reactiva primero).</summary>
        public static void EscribirTexto(IntPtr objetivo, string texto)
        {
            if (string.IsNullOrEmpty(texto)) return;
            if (objetivo != IntPtr.Zero)
                Run("windowactivate", "--sync", objetivo.ToString(), "type", "--delay", "12", "--", texto);
            else
                Run("type", "--delay", "12", "--", texto);
        }

        /// <summary>Tecla especial por nombre de X keysym (BackSpace, Return, Tab…).</summary>
        public static void EscribirTecla(IntPtr objetivo, string keysym)
        {
            if (objetivo != IntPtr.Zero)
                Run("windowactivate", "--sync", objetivo.ToString(), "key", keysym);
            else
                Run("key", keysym);
        }

        public static void DevolverFoco(IntPtr objetivo)
        {
            if (objetivo != IntPtr.Zero)
                Run("windowactivate", objetivo.ToString());
        }

        private static string Run(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("xdotool")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                string salida = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return salida;
            }
            catch (Exception ex)
            {
                if (!_avisado)
                {
                    _avisado = true;
                    Console.Error.WriteLine(
                        "[TecladoLinux] xdotool no disponible (" + ex.Message +
                        ") — el teclado sigue andando para las páginas del Hub; " +
                        "para campos nativos instalar xdotool");
                }
                return null;
            }
        }

        // --- Ventana que no roba el foco (ICCCM input hint) ------------------

        private const long InputHint = 1 << 0;

        // XWMHints en x64: flags(long) input(Bool=int) initial_state(int)
        // icon_pixmap(XID=ulong) icon_window(Window=ulong) icon_x(int)
        // icon_y(int) icon_mask(XID=ulong) window_group(XID=ulong) = 56 bytes.
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

        /// <summary>Marca la ventana con input hint = False: el WM no le da el
        /// foco de teclado al clickearla y el campo editado no lo pierde.</summary>
        public static void HacerNoActivable(IntPtr xid)
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
            catch { /* sin X11 (Wayland puro): el fallback es windowactivate */ }
            finally
            {
                try { if (display != IntPtr.Zero) XCloseDisplay(display); } catch { }
            }
        }
    }

    /// <summary>Dispatcher por OS del teclado nativo: la ventana del teclado
    /// habla con esto y no con las APIs de cada plataforma.</summary>
    internal static class TecladoNativo
    {
        public static IntPtr VentanaAlFrente()
        {
            if (OperatingSystem.IsWindows()) return TecladoWin32.GetForegroundWindow();
            if (OperatingSystem.IsLinux()) return TecladoLinux.GetActiveWindow();
            return IntPtr.Zero;
        }

        public static void HacerNoActivable(IntPtr handle)
        {
            if (OperatingSystem.IsWindows())
            {
                try { TecladoWin32.HacerNoActivable(handle); } catch { }
                try { TecladoWin32.EvitarActivacionPorClic(handle); } catch { }
            }
            else if (OperatingSystem.IsLinux())
            {
                TecladoLinux.HacerNoActivable(handle);
            }
        }

        public static void DevolverFoco(IntPtr objetivo)
        {
            if (OperatingSystem.IsWindows()) TecladoWin32.DevolverFoco(objetivo);
            // Linux: el foco se devuelve dentro de la misma invocación de
            // xdotool (windowactivate --sync encadenado), no hace falta acá.
        }

        public static void Backspace(IntPtr objetivo)
        {
            if (OperatingSystem.IsWindows()) TecladoWin32.EscribirTeclaVirtual(TecladoWin32.VK_BACK);
            else if (OperatingSystem.IsLinux()) TecladoLinux.EscribirTecla(objetivo, "BackSpace");
        }

        public static void Enter(IntPtr objetivo)
        {
            if (OperatingSystem.IsWindows()) TecladoWin32.EscribirTeclaVirtual(TecladoWin32.VK_RETURN);
            else if (OperatingSystem.IsLinux()) TecladoLinux.EscribirTecla(objetivo, "Return");
        }

        public static void EscribirTexto(IntPtr objetivo, string texto)
        {
            if (OperatingSystem.IsWindows()) TecladoWin32.EscribirTexto(texto);
            else if (OperatingSystem.IsLinux()) TecladoLinux.EscribirTexto(objetivo, texto);
        }
    }
}
