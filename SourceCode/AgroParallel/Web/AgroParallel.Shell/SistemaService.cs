// ============================================================================
// SistemaService.cs
// Implementación net48 de ISistemaService.
//   - Brillo: DDC/CI (dxva2.dll) → fallback WMI WmiSetBrightness.
//   - Power: shutdown.exe / rundll32 / Application.Exit.
// Port directo de la lógica que vivía en FormSistemaBrillo + FormSistemaApagar.
// ============================================================================

using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Shell
{
    public sealed class SistemaService : ISistemaService
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
            catch { return -1; }
        }

        public bool SetBrightness(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            try { if (TrySetDdcBrightness(percent)) return true; } catch { }
            try { return TrySetWmiBrightness(percent); } catch { return false; }
        }

        public void ExecutePowerAction(PowerAction action)
        {
            switch (action)
            {
                case PowerAction.Shutdown: Run("shutdown", "/s /t 0"); break;
                case PowerAction.Restart: Run("shutdown", "/r /t 0"); break;
                case PowerAction.LogOff: Run("shutdown", "/l"); break;
                case PowerAction.Suspend: Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"); break;
                case PowerAction.ExitApp: Application.Exit(); break;
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
