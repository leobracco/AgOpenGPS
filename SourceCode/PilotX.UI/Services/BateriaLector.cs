// BateriaLector.cs
//
// Estado de carga de la batería de la pantalla, leído local con
// GetSystemPowerStatus (kernel32) — la UI corre en la misma máquina, no
// hace falta pasar por el Engine. En Linux o sin batería devuelve
// tiene=false y el que consume oculta el dato (nunca inventa números).

using System;
using System.Runtime.InteropServices;

namespace PilotX.Desktop.Services;

public static class BateriaLector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0 = a batería, 1 = enchufada, 255 = ?
        public byte BatteryFlag;        // bit 8 = cargando, bit 128 = sin batería
        public byte BatteryLifePercent; // 0..100, 255 = desconocido
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static (bool tiene, int pct, bool cargando, bool enchufada) Leer()
    {
        if (!OperatingSystem.IsWindows()) return (false, 0, false, false);
        try
        {
            if (!GetSystemPowerStatus(out var s)) return (false, 0, false, false);
            if ((s.BatteryFlag & 128) != 0 || s.BatteryLifePercent > 100)
                return (false, 0, false, false);
            return (true, s.BatteryLifePercent,
                    (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1);
        }
        catch
        {
            return (false, 0, false, false);
        }
    }
}
