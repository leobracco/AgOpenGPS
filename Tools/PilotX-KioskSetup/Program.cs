// PilotX-KioskSetup.exe
// ================================================================================
// Configura una PC Windows en un tractor para arrancar directamente en AgOpenGPS
// (modo kiosko): AutoLogon a usuario "pilotx" + Shell de Windows = AgOpenGPS.exe.
//
// Uso:
//   PilotX-KioskSetup.exe                 → modo interactivo (pide confirmacion)
//   PilotX-KioskSetup.exe /yes            → aplica sin preguntar
//   PilotX-KioskSetup.exe /undo           → revierte (Shell=explorer, AutoLogon=0)
//   PilotX-KioskSetup.exe /status         → muestra estado actual
//   PilotX-KioskSetup.exe /aog "C:\path"  → usa este path para AgOpenGPS.exe
//
// Pensado para una PC en condiciones poco prolijas: detecta y limpia Shells
// rotos (apuntando a .bat que no existen), lo cual es comun en tractores que
// pasaron por varias instalaciones.
// ================================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Microsoft.Win32;

namespace AgroParallel.PilotX.KioskSetup
{
    internal static class Program
    {
        private const string KioskUser = "pilotx";
        private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        private const string PasswordlessKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

        private static readonly string[] AogCandidates = {
            @"C:\PilotX\AgOpenGPS.exe",
            @"C:\Program Files\AgroParallel\PilotX\AgOpenGPS.exe",
            @"C:\Program Files (x86)\AgroParallel\PilotX\AgOpenGPS.exe",
        };

        private static int Main(string[] args)
        {
            Console.Title = "PilotX Kiosk Setup";
            try
            {
                if (!IsAdmin())
                {
                    WriteErr("Este programa requiere ejecutarse como Administrador.");
                    Pause();
                    return 2;
                }

                bool yes = HasFlag(args, "/yes") || HasFlag(args, "-y");
                if (HasFlag(args, "/status"))    return ShowStatus();
                if (HasFlag(args, "/undo"))      return Undo(yes);

                string aogExplicit = GetArg(args, "/aog");
                return Apply(yes, aogExplicit);
            }
            catch (Exception ex)
            {
                WriteErr("Excepcion no manejada: " + ex);
                Pause();
                return 99;
            }
        }

        // ── APPLY ───────────────────────────────────────────────────────────────
        private static int Apply(bool yes, string aogExplicit)
        {
            Banner("Configurar PilotX en modo KIOSKO");

            string aog = ResolveAog(aogExplicit);
            if (aog == null)
            {
                WriteErr("No encontre AgOpenGPS.exe en ninguno de:");
                foreach (var c in AogCandidates) Console.WriteLine("  - " + c);
                Console.WriteLine();
                Console.WriteLine("Pasalo explicito:  PilotX-KioskSetup.exe /aog \"C:\\PilotX\"");
                Pause();
                return 3;
            }
            Console.WriteLine("AOG detectado: " + aog);

            string currentShell = ReadCurrentShell();
            Console.WriteLine("Shell actual:  " + (currentShell ?? "(no seteado)"));
            if (!string.IsNullOrEmpty(currentShell)
                && !currentShell.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                string shellExe = currentShell.Trim('"').Split(' ')[0];
                if (!File.Exists(shellExe))
                    WriteWarn("El Shell actual apunta a un archivo INEXISTENTE — sera reemplazado.");
            }

            Console.WriteLine();
            Console.WriteLine("Esto va a:");
            Console.WriteLine("  1. Crear usuario local '" + KioskUser + "' (sin password)");
            Console.WriteLine("  2. Activar AutoLogon a '" + KioskUser + "' al boot");
            Console.WriteLine("  3. Reemplazar el Shell de Windows por: " + aog);
            Console.WriteLine("     (oculta el escritorio: AOG arranca fullscreen)");
            Console.WriteLine();
            Console.WriteLine("Para revertir:  PilotX-KioskSetup.exe /undo");
            Console.WriteLine();

            if (!yes && !Confirm("Continuar?"))
            {
                Console.WriteLine("Cancelado.");
                return 1;
            }

            // 1) Crear usuario kiosko
            Step("Creando usuario '" + KioskUser + "'");
            if (UserExists(KioskUser))
            {
                Console.WriteLine("  ya existe, saltando creacion.");
            }
            else
            {
                Run("net", "user " + KioskUser + " \"\" /add /passwordreq:no /passwordchg:no /expires:never");
            }
            Run("net", "localgroup Usuarios " + KioskUser + " /add");
            Run("net", "localgroup Users " + KioskUser + " /add");
            Run("wmic", "useraccount where Name='" + KioskUser + "' set PasswordExpires=FALSE");

            // 2) AutoLogon
            Step("Configurando AutoLogon");
            using (var wl = Registry.LocalMachine.CreateSubKey(WinlogonKey))
            {
                wl.SetValue("AutoAdminLogon",   "1",                  RegistryValueKind.String);
                wl.SetValue("DefaultUserName",  KioskUser,            RegistryValueKind.String);
                wl.SetValue("DefaultPassword",  "",                   RegistryValueKind.String);
                wl.SetValue("DefaultDomainName", Environment.MachineName, RegistryValueKind.String);
                // Anti-bypass: si alguien aprieta Shift al boot, igual entra
                wl.SetValue("IgnoreShiftOverride", "1",               RegistryValueKind.String);
            }

            // En Win11 con cuentas locales, netplwiz oculta la opcion de AutoLogon
            // a menos que esto este en 0:
            using (var pwl = Registry.LocalMachine.CreateSubKey(PasswordlessKey))
            {
                pwl.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
            }

            // 3) Shell = AOG (HKLM global — TODOS los users veran AOG como shell)
            Step("Reemplazando Shell de Windows");
            using (var wl = Registry.LocalMachine.CreateSubKey(WinlogonKey))
            {
                wl.SetValue("Shell", aog, RegistryValueKind.String);
            }

            Banner("LISTO");
            ShowStatus();
            Console.WriteLine();
            Console.WriteLine("Backdoor para mantenimiento (si necesitas escritorio normal):");
            Console.WriteLine("  Ctrl+Alt+Supr → Administrador de tareas → Archivo → Ejecutar nueva tarea → explorer.exe");
            Console.WriteLine();

            if (yes || Confirm("Reiniciar ahora?"))
            {
                Run("shutdown", "/r /t 5 /c \"PilotX kiosko configurado\"");
            }
            return 0;
        }

        // ── UNDO ────────────────────────────────────────────────────────────────
        private static int Undo(bool yes)
        {
            Banner("Revertir modo KIOSKO");
            ShowStatus();
            Console.WriteLine();
            Console.WriteLine("Esto va a:");
            Console.WriteLine("  - Shell = explorer.exe");
            Console.WriteLine("  - AutoAdminLogon = 0");
            Console.WriteLine("  - (NO borra el usuario '" + KioskUser + "' — hacelo a mano si querés)");
            Console.WriteLine();

            if (!yes && !Confirm("Continuar?"))
                return 1;

            Step("Restaurando Shell = explorer.exe");
            using (var wl = Registry.LocalMachine.CreateSubKey(WinlogonKey))
            {
                wl.SetValue("Shell", "explorer.exe", RegistryValueKind.String);
                wl.SetValue("AutoAdminLogon", "0",   RegistryValueKind.String);
                try { wl.DeleteValue("DefaultPassword",  false); } catch { }
                try { wl.DeleteValue("IgnoreShiftOverride", false); } catch { }
            }

            Banner("LISTO");
            ShowStatus();
            if (yes || Confirm("Reiniciar ahora?"))
                Run("shutdown", "/r /t 5 /c \"PilotX kiosko revertido\"");
            return 0;
        }

        // ── STATUS ──────────────────────────────────────────────────────────────
        private static int ShowStatus()
        {
            Banner("Estado actual");
            using (var wl = Registry.LocalMachine.OpenSubKey(WinlogonKey))
            {
                Console.WriteLine("  Shell           = " + (wl?.GetValue("Shell") ?? "(default explorer.exe)"));
                Console.WriteLine("  AutoAdminLogon  = " + (wl?.GetValue("AutoAdminLogon") ?? "0"));
                Console.WriteLine("  DefaultUserName = " + (wl?.GetValue("DefaultUserName") ?? "(none)"));
            }
            Console.WriteLine("  Usuario '" + KioskUser + "' " + (UserExists(KioskUser) ? "EXISTE" : "no existe"));
            return 0;
        }

        // ── helpers ─────────────────────────────────────────────────────────────
        private static string ResolveAog(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                if (File.Exists(explicitPath)) return explicitPath;
                if (Directory.Exists(explicitPath))
                {
                    string c = Path.Combine(explicitPath, "AgOpenGPS.exe");
                    if (File.Exists(c)) return c;
                }
                return null;
            }

            // Detectar tambien donde corre este .exe (si fue copiado al lado de AOG)
            try
            {
                string here = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                string c = Path.Combine(here ?? "", "AgOpenGPS.exe");
                if (File.Exists(c)) return c;
            }
            catch { }

            foreach (var c in AogCandidates)
                if (File.Exists(c)) return c;
            return null;
        }

        private static string ReadCurrentShell()
        {
            using (var wl = Registry.LocalMachine.OpenSubKey(WinlogonKey))
                return wl?.GetValue("Shell") as string;
        }

        private static bool UserExists(string name)
        {
            try
            {
                var psi = new ProcessStartInfo("net", "user " + name)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
            {
                var p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static void Run(string cmd, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(cmd, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    string errp = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    string tag = p.ExitCode == 0 ? "OK" : ("exit=" + p.ExitCode);
                    Console.WriteLine("  [" + cmd + "] " + tag);
                    if (p.ExitCode != 0)
                    {
                        if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine("    out: " + outp.Trim());
                        if (!string.IsNullOrWhiteSpace(errp)) Console.WriteLine("    err: " + errp.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [" + cmd + "] EXCEPTION: " + ex.Message);
            }
        }

        private static bool HasFlag(string[] args, string flag) =>
            args != null && args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        private static string GetArg(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static bool Confirm(string prompt)
        {
            Console.Write(prompt + " (s/N): ");
            string ans = Console.ReadLine();
            return ans != null && ans.Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase);
        }

        private static void Banner(string txt)
        {
            Console.WriteLine();
            Console.WriteLine("=== " + txt + " ===");
        }

        private static void Step(string txt) => Console.WriteLine(">> " + txt);
        private static void WriteErr(string txt)  { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Red;    Console.WriteLine("ERROR: " + txt); Console.ForegroundColor = c; }
        private static void WriteWarn(string txt) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("WARN:  " + txt); Console.ForegroundColor = c; }

        private static void Pause()
        {
            Console.Write("Presione Enter para salir...");
            try { Console.ReadLine(); } catch { }
        }
    }
}
