// ============================================================================
// AgroParallel.Updater - applier de auto-update
//
// Uso:
//   AgroParallel.Updater.exe --pid <pid> --zip <path> --install <dir> --exe <path>
//
// Flujo:
//   1. Espera a que el proceso PID (PilotX) termine (max 60 s). Si no, lo mata.
//   1b.Cierra el RESTO de procesos que corren desde el install dir (típicamente
//      CoreX/AgIO). Comparten DLLs (AgLibrary.dll, etc.) con PilotX, así que si
//      siguen vivos mantienen esos archivos bloqueados y la extracción falla.
//   2. Hace backup del install dir actual a <install>\AgroParallel\Backups\<ts>\
//      (solo .exe + .dll + Branding\ + AgroParallel\wwwroot\, no Fields/).
//   3. Extrae el ZIP encima de <install> (salteando el propio Updater en uso).
//   4. Relanza CoreX (y demás apps cerradas) primero, PilotX al final.
//   5. Si algo falla, restaura desde backup y relanza igual el estado previo.
//
// Log: <install>\AgroParallel\Updates\updater.log
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace AgroParallel.Updater
{
    internal static class Program
    {
        private static string _logPath;

        private static int Main(string[] args)
        {
            int pid = 0;
            string zip = null, install = null, exe = null;

            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--pid":     int.TryParse(args[++i], out pid); break;
                    case "--zip":     zip = args[++i]; break;
                    case "--install": install = args[++i]; break;
                    case "--exe":     exe = args[++i]; break;
                }
            }

            if (string.IsNullOrEmpty(zip) || string.IsNullOrEmpty(install))
            {
                Console.Error.WriteLine("Faltan argumentos: --zip y --install son obligatorios.");
                return 2;
            }
            if (string.IsNullOrEmpty(exe)) exe = Path.Combine(install, "PilotX.exe");

            try { _logPath = Path.Combine(install, "AgroParallel", "Updates", "updater.log"); } catch { }
            Log("==== AgroParallel.Updater iniciando ====");
            Log("pid=" + pid + " zip=" + zip);
            Log("install=" + install);
            Log("exe=" + exe);

            // 1. Esperar a que PilotX salga.
            if (pid > 0)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    Log("Esperando exit de PID " + pid + " (max 60s)...");
                    if (!p.WaitForExit(60_000))
                    {
                        Log("Timeout — matando proceso.");
                        try { p.Kill(); } catch (Exception ex) { Log("Kill fallo: " + ex.Message); }
                        Thread.Sleep(2000);
                    }
                    else Log("Proceso salio limpio.");
                }
                catch (ArgumentException) { Log("Proceso PID " + pid + " ya no existe."); }
                catch (Exception ex) { Log("Error esperando proceso: " + ex.Message); }
            }

            // 1b. Cerrar el resto de apps que corren desde el install dir (CoreX,
            // etc.). Comparten DLLs con PilotX; si siguen vivas, AgLibrary.dll y
            // compañía quedan bloqueadas y la extracción falla. Las relanzamos al
            // final (CoreX antes que PilotX para que el broker esté arriba).
            List<string> closedExes = CloseInstallDirProcesses(install, pid);

            // Pausa extra para que liberen los file handles (DLLs).
            Thread.Sleep(1500);

            // 2. Backup (best-effort).
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupDir = Path.Combine(install, "AgroParallel", "Backups", ts);
            try
            {
                Directory.CreateDirectory(backupDir);
                BackupTopLevel(install, backupDir);
                Log("Backup en " + backupDir);
            }
            catch (Exception ex)
            {
                Log("Backup fallo (continuando igual): " + ex.Message);
            }

            // 3. Extracción.
            bool extractOk = false;
            try
            {
                Log("Extrayendo " + zip + " -> " + install);
                ExtractZipOverwrite(zip, install);
                extractOk = true;
                Log("Extraccion OK.");
            }
            catch (Exception ex)
            {
                Log("Extraccion FALLO: " + ex.Message);
            }

            // 3b. Si falló, restaurar.
            if (!extractOk)
            {
                try
                {
                    Log("Restaurando backup...");
                    RestoreTopLevel(backupDir, install);
                    Log("Restore OK. Relanzando version anterior.");
                }
                catch (Exception ex)
                {
                    Log("Restore FALLO: " + ex.Message);
                }
            }

            // 4. Relanzar. Primero las apps que cerramos en 1b (CoreX → el broker
            // MQTT tiene que estar arriba antes que PilotX), PilotX al final.
            foreach (var path in closedExes)
            {
                if (string.Equals(Path.GetFileName(path), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase))
                    continue; // PilotX va al final
                RelaunchExe(path, install);
                Thread.Sleep(1500); // dar tiempo a que el broker levante
            }

            if (File.Exists(exe)) RelaunchExe(exe, install);
            else Log("ERROR: exe no existe tras update: " + exe);

            Log("==== Updater terminado ====");
            return extractOk ? 0 : 1;
        }

        // Cierra todo proceso cuyo ejecutable vive dentro de install dir, salvo
        // el PID ya manejado (PilotX) y el propio Updater. Devuelve las rutas de
        // los exes cerrados para poder relanzarlos después. CloseMainWindow primero
        // (cierre ordenado, p.ej. AgIO baja el broker), Kill como último recurso.
        private static List<string> CloseInstallDirProcesses(string install, int skipPid)
        {
            var closed = new List<string>();
            string installFull;
            try { installFull = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
            catch { return closed; }

            int selfPid = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                if (p.Id == skipPid || p.Id == selfPid) continue;
                string path = null;
                try { path = p.MainModule != null ? p.MainModule.FileName : null; }
                catch { continue; } // acceso denegado / sin main module → no es nuestro
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith(installFull, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    Log("Cerrando proceso del install dir: " + Path.GetFileName(path) + " (PID " + p.Id + ")");
                    bool exited = false;
                    try { if (p.CloseMainWindow()) exited = p.WaitForExit(8000); } catch { }
                    if (!exited && !p.HasExited)
                    {
                        Log("No cerró ordenado — matando PID " + p.Id);
                        try { p.Kill(); p.WaitForExit(5000); } catch (Exception ex) { Log("Kill fallo: " + ex.Message); }
                    }
                    closed.Add(path);
                }
                catch (Exception ex) { Log("No pude cerrar PID " + p.Id + ": " + ex.Message); }
            }
            return closed;
        }

        private static void RelaunchExe(string exe, string install)
        {
            try
            {
                Log("Relanzando " + exe);
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = install
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("Relanzar fallo (" + Path.GetFileName(exe) + "): " + ex.Message);
            }
        }

        // Backup superficial: copia archivos del top-level del install dir
        // (.exe / .dll / .json / .ico / .config) + las carpetas claves del shell.
        // No copia Fields/ ni firmware-cache/ ni AgroParallel/WebView2Data/.
        private static void BackupTopLevel(string src, string dst)
        {
            foreach (var f in Directory.GetFiles(src))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".exe" || ext == ".dll" || ext == ".json" || ext == ".config" || ext == ".ico" || ext == ".pdb")
                {
                    try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch { }
                }
            }
            CopyTreeIfExists(Path.Combine(src, "Branding"),               Path.Combine(dst, "Branding"));
            CopyTreeIfExists(Path.Combine(src, "AgroParallel", "wwwroot"), Path.Combine(dst, "AgroParallel", "wwwroot"));
        }

        private static void RestoreTopLevel(string src, string dst)
        {
            foreach (var f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch { }
            }
            CopyTreeIfExists(Path.Combine(src, "Branding"),                Path.Combine(dst, "Branding"));
            CopyTreeIfExists(Path.Combine(src, "AgroParallel", "wwwroot"), Path.Combine(dst, "AgroParallel", "wwwroot"));
        }

        private static void CopyTreeIfExists(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar);
                string outP = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outP));
                try { File.Copy(f, outP, true); } catch { }
            }
        }

        // Extrae sobrescribiendo. .NET Framework 4.8 trae ZipArchive
        // pero ZipFile.ExtractToDirectory(overwrite) recién en 4.6.2+.
        private static void ExtractZipOverwrite(string zipPath, string targetDir)
        {
            // El Updater está corriendo desde el install dir, así que no puede
            // sobrescribir su propio .exe (Windows lo tiene bloqueado). Lo salteamos:
            // si el Updater cambia, viaja en el ZIP igual y se aplica en el próximo
            // update (cuando ya no esté en uso este).
            string selfExe = null;
            try { selfExe = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName); } catch { }

            using (var fs = File.OpenRead(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) // entrada de directorio
                    {
                        Directory.CreateDirectory(Path.Combine(targetDir, entry.FullName));
                        continue;
                    }
                    if (!string.IsNullOrEmpty(selfExe) &&
                        string.Equals(entry.Name, selfExe, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Salteando self (en uso): " + entry.FullName);
                        continue;
                    }
                    string dst = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                    // Zip-slip defense: si la entrada sale del target, abortar.
                    if (!dst.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Entrada ZIP fuera del target: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    // Reintentos cortos si el archivo está bloqueado por antivirus.
                    int retries = 5;
                    while (true)
                    {
                        try { entry.ExtractToFile(dst, true); break; }
                        catch (IOException) when (--retries > 0) { Thread.Sleep(400); }
                    }
                }
            }
        }

        private static void Log(string line)
        {
            try
            {
                string row = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine;
                Console.Out.Write(row);
                if (!string.IsNullOrEmpty(_logPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                    File.AppendAllText(_logPath, row);
                }
            }
            catch { }
        }
    }
}
