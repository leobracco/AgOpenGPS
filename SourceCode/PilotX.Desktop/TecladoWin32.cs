// ============================================================================
// TecladoWin32.cs — envía teclas a la ventana que TIENE el foco.
//
// El teclado en pantalla vive en su propia ventana (ver TecladoWindow). Esa
// ventana está marcada como NO ACTIVABLE, así que al tocarla el foco se queda
// donde estaba — en el campo que el operario está editando. Las teclas se
// mandan con SendInput, que las entrega a la ventana enfocada: por eso el
// mismo teclado sirve para los campos de la UI nativa Y para los de las
// páginas web del Hub, sin que ninguna de las dos tenga que enterarse.
//
// Se usa KEYEVENTF_UNICODE en vez de códigos de tecla: manda el carácter tal
// cual (ñ, á, símbolos) sin depender de la distribución de teclado que tenga
// Windows configurada en la pantalla del tractor.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace PilotX.Desktop
{
    internal static class TecladoWin32
    {
        // --- SendInput -------------------------------------------------------
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)] public int type;
            // El offset del union es 8 en x64 (4 del type + padding de alineación).
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>Escribe un texto (uno o más caracteres) en la ventana enfocada.</summary>
        public static void EscribirTexto(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return;
            // Dos INPUT por carácter (down + up). Los pares suplementarios (emoji)
            // viajan como dos unidades UTF-16, que es justo lo que quiere la API.
            var inputs = new INPUT[texto.Length * 2];
            for (int i = 0; i < texto.Length; i++)
            {
                inputs[i * 2] = NuevoUnicode(texto[i], false);
                inputs[i * 2 + 1] = NuevoUnicode(texto[i], true);
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        /// <summary>Manda una tecla virtual (retroceso, enter, flechas…).</summary>
        public static void EscribirTeclaVirtual(ushort vk)
        {
            var inputs = new INPUT[2];
            inputs[0] = NuevoVk(vk, false);
            inputs[1] = NuevoVk(vk, true);
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        public const ushort VK_BACK = 0x08;
        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_TAB = 0x09;
        public const ushort VK_ESCAPE = 0x1B;

        private static INPUT NuevoUnicode(char c, bool arriba) => new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (arriba ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };

        private static INPUT NuevoVk(ushort vk, bool arriba) => new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = arriba ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };

        // --- Ventana que no roba el foco -------------------------------------
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;   // fuera del Alt+Tab

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>
        /// Marca la ventana como no activable. Sin esto, al tocar una tecla la
        /// ventana del teclado se lleva el foco, el campo que se estaba
        /// editando lo pierde y las teclas no van a ninguna parte.
        /// </summary>
        public static void HacerNoActivable(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            int estilo = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE, estilo | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        // --- Devolver el foco antes de escribir ------------------------------
        // WS_EX_NOACTIVATE evita que la ventana se active al tocarla, pero no
        // alcanza: igual se veía perder el cursor del campo y las teclas no
        // entraban. Así que además se guarda cuál era la ventana que estaba
        // adelante cuando se abrió el teclado, y se la trae de vuelta justo
        // antes de cada tecla — SendInput escribe en la que tiene el foco.
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        /// <summary>Trae al frente la ventana que estaba editándose.</summary>
        public static void DevolverFoco(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
            if (GetForegroundWindow() == hWnd) return;   // ya está
            try { SetForegroundWindow(hWnd); } catch { }
        }

        // --- Que el clic NO active la ventana ---------------------------------
        // WS_EX_NOACTIVATE y Focusable=false ayudan, pero la palabra final la
        // tiene Windows: cuando se toca una ventana manda WM_MOUSEACTIVATE
        // preguntando qué hacer. Respondiendo MA_NOACTIVATE la ventana recibe
        // el clic SIN activarse, y el campo que se está editando conserva el
        // foco. Es lo que hacen los teclados en pantalla del sistema.
        private const int GWLP_WNDPROC = -4;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;
        // En pantalla TÁCTIL el toque no manda WM_MOUSEACTIVATE sino
        // WM_POINTERACTIVATE (Windows no promueve el touch a mouse cuando la app
        // procesa punteros, como hace Avalonia). Sin manejarlo, tocar una tecla
        // ACTIVABA la ventana del teclado, el campo perdía el foco, su LostFocus
        // cerraba el teclado y la tecla no se escribía (bug en la tablet F7N).
        private const uint WM_POINTERACTIVATE = 0x024B;
        private const int PA_NOACTIVATE = 3;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Se guardan en estático a propósito: si el delegate lo recolecta el GC,
        // Windows llama a un puntero muerto y la pantalla se cae.
        private static IntPtr _wndProcAnterior;
        private static WndProcDelegate _wndProcPropio;

        public static void EvitarActivacionPorClic(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || _wndProcPropio != null) return;
            _wndProcPropio = (h, msg, w, l) =>
            {
                if (msg == WM_MOUSEACTIVATE) return (IntPtr)MA_NOACTIVATE;
                if (msg == WM_POINTERACTIVATE) return (IntPtr)PA_NOACTIVATE;
                return CallWindowProc(_wndProcAnterior, h, msg, w, l);
            };
            _wndProcAnterior = SetWindowLongPtr(hWnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProcPropio));
        }
    }
}
