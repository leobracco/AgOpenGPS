// ============================================================================
// AgroParallel.Updater - applier de auto-update
//
// Uso:
//   AgroParallel.Updater.exe --pid <pid> --zip <path> --install <dir> --exe <path>
//
// Flujo:
//   1. Espera a que el proceso PID termine (max 60 s). Si no termina, mata.
//   2. Hace backup del install dir actual a <install>\AgroParallel\Backups\<ts>\
//      (solo .exe + .dll + Branding\ + AgroParallel\wwwroot\, no Fields/).
//   3. Extrae el ZIP encima de <install>.
//   4. Relanza <exe>.
//   5. Si algo falla, restaura desde backup.
//
// Log: <install>\AgroParallel\Updates\updater.log
// ============================================================================

using System;
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
            if (string.IsNullOrEmpty(exe)) exe = Path.Combine(install, "AgOpenGPS.exe");

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

            // 4. Relanzar PilotX.
            try
            {
                if (File.Exists(exe))
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
                else Log("ERROR: exe no existe tras update: " + exe);
            }
            catch (Exception ex)
            {
                Log("Relanzar fallo: " + ex.Message);
            }

            Log("==== Updater terminado ====");
            return extractOk ? 0 : 1;
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
