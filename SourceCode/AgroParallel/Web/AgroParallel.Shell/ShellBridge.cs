// ============================================================================
// ShellBridge.cs
// Objeto expuesto a JS via WebView2.AddHostObjectToScript("agp", new ShellBridge(...))
// Operaciones OS-level que no tienen sentido por HTTP (osk, fullscreen, etc.).
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgroParallel.Shell
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class ShellBridge
    {
        private readonly Form _host;

        public ShellBridge(Form host)
        {
            _host = host;
        }

        // No exponemos OpenSystemKeyboard(): la UI tiene su propio teclado virtual
        // HTML (keyboard.js). Nunca invocar osk.exe en cabina.

        public void ToggleFullscreen()
        {
            if (_host == null) return;
            _host.BeginInvoke(new Action(() =>
            {
                if (_host.FormBorderStyle == FormBorderStyle.None)
                {
                    _host.FormBorderStyle = FormBorderStyle.Sizable;
                    _host.WindowState = FormWindowState.Normal;
                }
                else
                {
                    _host.FormBorderStyle = FormBorderStyle.None;
                    _host.WindowState = FormWindowState.Maximized;
                }
            }));
        }

        public void Close()
        {
            if (_host == null) return;
            _host.BeginInvoke(new Action(() => _host.Close()));
        }

        // Abre el applet "Wi-Fi" nativo de Windows (Configuración → Red).
        // Caso de uso: el operario está en oficina y necesita conectarse a otra red
        // (la del tractor), pero no puede tocar Windows porque AOG está fullscreen
        // tapando el systray. Este shortcut le abre el panel encima del Hub.
        // ms-settings:network-wifi requiere Windows 10+. Si falla (Windows viejo o
        // política), fallback a ncpa.cpl (panel de control clásico).
        public void OpenWifiSettings()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:network-wifi",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ncpa.cpl",
                        UseShellExecute = true
                    });
                }
                catch { /* sin red disponible / política bloquea — nada que hacer */ }
            }
        }
    }
}
