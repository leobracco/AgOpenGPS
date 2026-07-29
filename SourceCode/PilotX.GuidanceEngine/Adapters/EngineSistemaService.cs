// ============================================================================
// EngineSistemaService.cs — implementación net9 (headless) de ISistemaService.
// Sin esto, GET/POST api/sistema/brillo contra el motor (--webhost) siempre
// devolvía ok:false,value:-1 (el controller se registra igual, no da 404, así
// que el fallo se disfrazaba de "brillo no soportado por hardware" en vez de
// "servicio no cableado") — mismo hueco que tuvieron ConfigVehiculo/
// ImuCalibracion/IVehicleToolService antes.
//
// Port directo de AgroParallel.Shell/SistemaService.cs (net48): mismo DDC/CI
// vía dxva2.dll + fallback WMI WmiMonitorBrightness/WmiSetBrightness. Único
// cambio real es ExecutePowerAction: el original cerraba con
// System.Windows.Forms.Application.Exit() (no existe en un proceso consola
// headless); acá ExitApp hace Environment.Exit(0) — cierra el proceso motor,
// equivalente semántico para un head sin UI.
// ============================================================================

using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    // DDC/CI (dxva2.dll) + WMI (root\WMI): 100% Windows, igual que el resto del
    // motor en la práctica (CoreX/dxva2/System.IO.Ports ya son Windows-only sin
    // declararlo — acá se declara explícito porque System.Management SÍ trae
    // los atributos [SupportedOSPlatform] que el analizador chequea).
    [SupportedOSPlatform("windows")]
    public sealed class EngineSistemaService : ISistemaService
    {
        // ===== DDC/CI =====
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("dxva2.dll")]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);
        [DllImport("dxva2.dll")]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
        [DllImport("dxva2.dll")]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
        [DllImport("dxva2.dll")]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint pdwMinimumBrightness, ref uint pdwCurrentBrightness, ref uint pdwMaximumBrightness);
        [DllImport("dxva2.dll")]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;

        public int GetBrightness()
        {
            try
            {
                int ddc = TryGetDdcBrightness();
                if (ddc >= 0) return ddc;
            }
            catch { }
            try
            {
                return TryGetWmiBrightness();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] Sistema.GetBrightness: sin DDC/CI ni WMI (" + ex.Message + ")");
                return -1;
            }
        }

        public bool SetBrightness(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            try { if (TrySetDdcBrightness(percent)) return true; } catch { }
            try { return TrySetWmiBrightness(percent); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] Sistema.SetBrightness: sin DDC/CI ni WMI (" + ex.Message + ")");
                return false;
            }
        }

        public void ExecutePowerAction(PowerAction action)
        {
            switch (action)
            {
                case PowerAction.Shutdown: Run("shutdown", "/s /t 0"); break;
                case PowerAction.Restart: Run("shutdown", "/r /t 0"); break;
                case PowerAction.LogOff: Run("shutdown", "/l"); break;
                case PowerAction.Suspend: Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"); break;
                // Sin UI que cerrar: apaga el proceso motor mismo.
                case PowerAction.ExitApp: Environment.Exit(0); break;
            }
        }

        private static void Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, CreateNoWindow = true };
                Process.Start(psi);
            }
            catch { }
        }

        // ===== DDC helpers =====
        private static int TryGetDdcBrightness()
        {
            IntPtr h = MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_DEFAULTTOPRIMARY);
            uint num = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref num) || num == 0) return -1;
            var arr = new PHYSICAL_MONITOR[num];
            if (!GetPhysicalMonitorsFromHMONITOR(h, num, arr)) return -1;
            try
            {
                uint min = 0, cur = 0, max = 0;
                if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, ref min, ref cur, ref max)) return -1;
                if (max == 0) return -1;
                return (int)Math.Round(100.0 * (cur - min) / (max - min));
            }
            finally { DestroyPhysicalMonitors(num, arr); }
        }

        private static bool TrySetDdcBrightness(int percent)
        {
            IntPtr h = MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_DEFAULTTOPRIMARY);
            uint num = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref num) || num == 0) return false;
            var arr = new PHYSICAL_MONITOR[num];
            if (!GetPhysicalMonitorsFromHMONITOR(h, num, arr)) return false;
            try
            {
                uint min = 0, cur = 0, max = 0;
                if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, ref min, ref cur, ref max)) return false;
                uint target = (uint)(min + (max - min) * percent / 100);
                bool ok = true;
                for (int i = 0; i < num; i++)
                    ok &= SetMonitorBrightness(arr[i].hPhysicalMonitor, target);
                return ok;
            }
            finally { DestroyPhysicalMonitors(num, arr); }
        }

        private static int TryGetWmiBrightness()
        {
            using (var mos = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness"))
                foreach (ManagementObject mo in mos.Get())
                    return Convert.ToInt32(mo["CurrentBrightness"]);
            return -1;
        }

        private static bool TrySetWmiBrightness(int percent)
        {
            using (var mos = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
            {
                foreach (ManagementObject mo in mos.Get())
                {
                    mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                    return true;
                }
            }
            return false;
        }
    }
}
