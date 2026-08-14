// ============================================================================
// EngineSistemaServiceLinux.cs — contracara Linux de EngineSistemaService:
// misma ISistemaService (brillo + acciones de energía) para que
// GET/POST api/sistema/* funcionen igual en las dos plataformas.
//
// Brillo, en orden de preferencia:
//   1. sysfs (/sys/class/backlight/*/brightness) — paneles integrados; para
//      ESCRIBIR hace falta permiso (regla udev o grupo video en la pantalla).
//   2. brightnessctl — mismo backend con el setuid resuelto por la distro.
//   3. ddcutil (getvcp/setvcp 10) — monitores externos por DDC/CI, el
//      equivalente exacto del camino dxva2 de Windows.
//
// Energía: systemctl poweroff/reboot/suspend + loginctl para cerrar sesión.
// ============================================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    public sealed class EngineSistemaServiceLinux : ISistemaService
    {
        public int GetBrightness()
        {
            try
            {
                int sysfs = TryGetSysfs();
                if (sysfs >= 0) return sysfs;
            }
            catch { }
            try
            {
                string s = Run("brightnessctl", "-m");
                // formato -m: device,class,current,percent%,max
                if (s != null)
                {
                    var partes = s.Trim().Split(',');
                    foreach (var p in partes)
                        if (p.EndsWith("%") && int.TryParse(p.TrimEnd('%'), out int pct))
                            return pct;
                }
            }
            catch { }
            try
            {
                // ddcutil getvcp 10 → "... current value = 55, max value = 100"
                string s = Run("ddcutil", "getvcp", "10", "--brief");
                if (s != null)
                {
                    // --brief: "VCP 10 C 55 100"
                    var tk = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length >= 5
                        && int.TryParse(tk[3], out int cur) && int.TryParse(tk[4], out int max) && max > 0)
                        return (int)Math.Round(100.0 * cur / max);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] Sistema.GetBrightness (linux): " + ex.Message);
            }
            return -1;
        }

        public bool SetBrightness(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            try { if (TrySetSysfs(percent)) return true; } catch { }
            try
            {
                if (Run("brightnessctl", "set", percent.ToString(CultureInfo.InvariantCulture) + "%") != null
                    && GetBrightness() >= 0) return true;
            }
            catch { }
            try
            {
                return Run("ddcutil", "setvcp", "10", percent.ToString(CultureInfo.InvariantCulture)) != null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] Sistema.SetBrightness (linux): " + ex.Message);
                return false;
            }
        }

        public void ExecutePowerAction(PowerAction action)
        {
            switch (action)
            {
                case PowerAction.Shutdown: Fire("systemctl", "poweroff"); break;
                case PowerAction.Restart: Fire("systemctl", "reboot"); break;
                case PowerAction.Suspend: Fire("systemctl", "suspend"); break;
                case PowerAction.LogOff:
                    Fire("loginctl", "terminate-user",
                        Environment.GetEnvironmentVariable("USER") ?? Environment.UserName);
                    break;
                // Sin UI que cerrar: apaga el proceso motor mismo.
                case PowerAction.ExitApp: Environment.Exit(0); break;
            }
        }

        // ===== sysfs =====
        private static string PrimerBacklight()
        {
            const string root = "/sys/class/backlight";
            if (!Directory.Exists(root)) return null;
            foreach (var d in Directory.GetDirectories(root)) return d;
            return null;
        }

        private static int TryGetSysfs()
        {
            string dir = PrimerBacklight();
            if (dir == null) return -1;
            int cur = int.Parse(File.ReadAllText(Path.Combine(dir, "brightness")).Trim());
            int max = int.Parse(File.ReadAllText(Path.Combine(dir, "max_brightness")).Trim());
            if (max <= 0) return -1;
            return (int)Math.Round(100.0 * cur / max);
        }

        private static bool TrySetSysfs(int percent)
        {
            string dir = PrimerBacklight();
            if (dir == null) return false;
            int max = int.Parse(File.ReadAllText(Path.Combine(dir, "max_brightness")).Trim());
            int target = (int)Math.Round(max * percent / 100.0);
            // Falla con UnauthorizedAccess si la pantalla no tiene la regla
            // udev — el caller cae a brightnessctl.
            File.WriteAllText(Path.Combine(dir, "brightness"),
                target.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        // ===== procesos =====
        private static string Run(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            string salida = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? salida : null;
        }

        private static void Fire(string exe, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] Sistema." + exe + ": " + ex.Message);
            }
        }
    }
}
