using System;
using System.Runtime.InteropServices;

namespace PilotX.Bars.Host;

/// <summary>Aplica WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW al HWND para que la
/// ventana reciba clicks sin robar el foco a FormGPS (fullscreen detras).</summary>
internal static class Win32NoActivate
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void Apply(IntPtr hWnd)
    {
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
