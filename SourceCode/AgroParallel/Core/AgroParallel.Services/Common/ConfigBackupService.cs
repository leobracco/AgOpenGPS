// ============================================================================
// ConfigBackupService.cs - Respaldo de TODA la configuración + datos de PilotX.
// Target: netstandard2.0 (C# 7.3)
//
// Por qué existe:
//   AtomicJson ya evita que un corte de luz corrompa un archivo (deja `.bak`),
//   pero ambos (`.json` y `.json.bak`) viven en la carpeta de instalación. Si esa
//   carpeta se borra, se reinstala o se pisa en una actualización, igual se pierde
//   la configuración del operario. Este servicio copia los JSON de config (de la
//   carpeta de instalación) + TODO el árbol de datos de PilotX
//   (Documents\AgOpenGPS: Vehicles, Fields/lotes, etc. — menos Logs) a:
//
//       %ProgramData%\AgroParallel\Backups\<categoría>\<yyyyMMdd-HHmmss>\
//
//   que está FUERA del directorio de instalación → sobrevive updates y reinstalls.
//
// Categorías:
//   · Auto\        backups automáticos (1 por día) + "Forzar backup ahora".
//                  Se rotan: se borran los de más de AutoKeepDays días.
//   · Instalador\  "Backup de instalador": permanente, NUNCA se rota. Pensado para
//                  fijar el estado de configuración inicial que dejó el técnico.
//   · (raíz)       backups de versiones previas del servicio. Se listan/restauran
//                  como "previo"; la rotación no los toca.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AgroParallel.Services
{
    public sealed class ConfigImportResult
    {
        public bool Ok { get; set; }
        public int FilesRestored { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Resumen de un backup en disco (una carpeta bajo <c>%ProgramData%\AgroParallel\Backups</c>).
    /// </summary>
    public sealed class BackupInfo
    {
        public string Name { get; set; }   // identificador relativo: "Auto/<stamp>", "Instalador/<stamp>" o "<stamp>" (previo)
        public string Kind { get; set; }   // "instalador" | "auto" | "previo"
        public DateTime When { get; set; }
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>Un archivo dentro de un backup (ruta relativa + tamaño).</summary>
    public sealed class BackupFileInfo
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
    }

    public static class ConfigBackupService
    {
        // Subcarpetas por categoría.
        private const string AutoSubdir = "Auto";
        private const string InstallerSubdir = "Instalador";

        // Subcarpetas de datos a NO respaldar: Logs (regenerable y crece sin
        // control) y Fields (los lotes pesan GBs; el backup es sólo de config).
        private static readonly HashSet<string> ExcludedTopSegments =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Logs", "Fields" };

        // Antigüedad máxima de los backups automáticos antes de borrarlos.
        private const int AutoKeepDays = 14;

        private static readonly object _gate = new object();
        private static Timer _timer;
        private static string _lastSignature;
        private static string[] _fullDirsSnapshot;

        /// <summary>
        /// Carpetas de datos completas configuradas en <see cref="StartAutomatic"/>
        /// (p.ej. <c>Documents\AgOpenGPS</c>). El controller de export/import/restore
        /// del Hub las reusa sin tener que conocer rutas GPS.
        /// </summary>
        public static string[] ConfiguredExtraDirs
        {
            get { lock (_gate) { return _fullDirsSnapshot ?? new string[0]; } }
        }

        /// <summary>
        /// Raíz de backups, fuera del directorio de instalación:
        /// <c>%ProgramData%\AgroParallel\Backups</c>.
        /// </summary>
        public static string BackupRoot
        {
            get
            {
                string programData = Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData);
                if (string.IsNullOrWhiteSpace(programData))
                    programData = AgroParallel.Common.AgpPaths.ConfigRoot;
                return Path.Combine(programData, "AgroParallel", "Backups");
            }
        }

        /// <summary>
        /// Hace un respaldo ahora. <paramref name="fullDirs"/> = carpetas de datos a
        /// copiar enteras (menos <c>Logs</c> y <c>Fields</c>), p.ej. <c>Documents\AgOpenGPS</c>.
        ///
        /// <para><paramref name="installer"/>: guarda en <c>Instalador\</c> (permanente).</para>
        /// <para><paramref name="force"/>: backup manual — ignora el dedup y el límite
        /// de "uno por día". Sin force ni installer (backup automático): se saltea si ya
        /// hay uno de hoy o si nada cambió desde la última copia.</para>
        ///
        /// Devuelve la ruta de la carpeta creada, o null si no hizo nada.
        /// </summary>
        public static string RunBackup(IEnumerable<string> fullDirs = null, bool force = false, bool installer = false)
        {
            lock (_gate)
            {
                try
                {
                    var installSources = CollectInstallSources();
                    var fulls = (fullDirs ?? Enumerable.Empty<string>())
                        .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (installSources.Count == 0 && fulls.Count == 0) return null;

                    // Firma del conjunto completo (config + árboles de datos).
                    var all = new List<string>(installSources);
                    foreach (var root in fulls)
                        all.AddRange(EnumerateTreeFiles(root, ExcludedTopSegments));
                    string signature = ComputeSignature(all);

                    // Backup automático: a lo sumo uno por día y sólo si cambió algo.
                    if (!force && !installer)
                    {
                        if (HasAutoBackupToday()) return null;
                        if (signature == _lastSignature) return null;
                    }

                    string subdir = installer ? InstallerSubdir : AutoSubdir;
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string destDir = Path.Combine(BackupRoot, subdir, stamp);
                    // Dos backups en el mismo segundo → sufijo para no mezclarlos.
                    if (Directory.Exists(destDir))
                    {
                        int n = 2;
                        while (Directory.Exists(destDir + "-" + n)) n++;
                        destDir = destDir + "-" + n;
                    }
                    Directory.CreateDirectory(destDir);

                    // 1) Archivos de config del directorio de instalación → directo.
                    string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;
                    foreach (var src in installSources)
                    {
                        try
                        {
                            string rel = MakeRelativeToBase(baseDir, src);
                            if (rel == null) continue;
                            CopyInto(src, Path.Combine(destDir, rel));
                        }
                        catch { /* un archivo bloqueado no debe abortar el backup */ }
                    }

                    // 2) Árboles de datos completos → _external\<carpetaRaíz>\... (menos Logs y Fields).
                    foreach (var root in fulls)
                    {
                        string rootName = Path.GetFileName(root.TrimEnd('\\', '/'));
                        if (string.IsNullOrEmpty(rootName)) rootName = "ext";
                        string destRoot = Path.Combine(destDir, "_external", rootName);
                        CopyTreeToBackup(root, destRoot, ExcludedTopSegments);
                    }

                    // El backup de instalador no participa de la rotación ni fija la
                    // firma (no debe impedir que un automático posterior se genere).
                    if (!installer)
                    {
                        _lastSignature = signature;
                        RotateAuto(AutoKeepDays);
                    }

                    return destDir;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("[ConfigBackup] Error: " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// Arranca un respaldo automático inmediato + un chequeo periódico que crea, a
        /// lo sumo, un backup por día. Idempotente: si ya hay timer, no crea otro.
        /// <paramref name="fullDirs"/> (árboles de datos) se reusan en cada disparo.
        /// </summary>
        public static void StartAutomatic(IEnumerable<string> fullDirs = null)
        {
            lock (_gate)
            {
                _fullDirsSnapshot = fullDirs != null ? fullDirs.ToArray() : null;
                if (_timer != null) return;

                // Chequeo cada hora: si ya hay backup de hoy, RunBackup no hace nada.
                // Así sale 1/día con la PC prendida, y el del arranque cubre el resto.
                int ms = 60 * 60 * 1000;
                _timer = new Timer(_ =>
                {
                    try { RunBackup(_fullDirsSnapshot); } catch { }
                }, null, ms, ms);
            }

            // Backup inmediato (captura la config buena de la sesión anterior).
            RunBackup(fullDirs);
        }

        public static void Stop()
        {
            lock (_gate)
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }

        // --- Listar / Restaurar backups en disco ---------------------------

        /// <summary>
        /// Lista los backups disponibles (Instalador + Auto + previos en la raíz),
        /// del más nuevo al más viejo, con tipo, fecha, cantidad de archivos y tamaño.
        /// </summary>
        public static List<BackupInfo> ListBackups()
        {
            var list = new List<BackupInfo>();
            try
            {
                if (!Directory.Exists(BackupRoot)) return list;

                AddCategory(list, Path.Combine(BackupRoot, InstallerSubdir), InstallerSubdir, "instalador");
                AddCategory(list, Path.Combine(BackupRoot, AutoSubdir), AutoSubdir, "auto");

                // Backups "previos" sueltos en la raíz (de versiones anteriores).
                foreach (var dir in Directory.GetDirectories(BackupRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (string.Equals(name, AutoSubdir, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, InstallerSubdir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    AddOne(list, dir, name, "previo");
                }
            }
            catch { }

            return list.OrderByDescending(b => b.When).ToList();
        }

        private static void AddCategory(List<BackupInfo> list, string dir, string prefix, string kind)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var sub in Directory.GetDirectories(dir))
                    AddOne(list, sub, prefix + "/" + Path.GetFileName(sub), kind);
            }
            catch { }
        }

        private static void AddOne(List<BackupInfo> list, string dir, string relName, string kind)
        {
            try
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                long total = 0;
                foreach (var f in files)
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                list.Add(new BackupInfo
                {
                    Name = relName,
                    Kind = kind,
                    When = ParseStamp(Path.GetFileName(dir), Directory.GetCreationTime(dir)),
                    FileCount = files.Length,
                    TotalBytes = total
                });
            }
            catch { }
        }

        /// <summary>
        /// Lista los archivos contenidos en un backup (ruta relativa + tamaño),
        /// ordenados alfabéticamente. Devuelve lista vacía si el nombre es inválido
        /// o el backup no existe.
        /// </summary>
        public static List<BackupFileInfo> ListBackupFiles(string backupName)
        {
            var list = new List<BackupFileInfo>();

            string srcFull = ResolveBackupDir(backupName);
            if (srcFull == null) return list;

            try
            {
                foreach (var f in Directory.GetFiles(srcFull, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(srcFull.Length).TrimStart('\\', '/').Replace('\\', '/');
                    long bytes = 0;
                    try { bytes = new FileInfo(f).Length; } catch { }
                    list.Add(new BackupFileInfo { Path = rel, Bytes = bytes });
                }
            }
            catch { }

            return list.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Restaura la configuración + datos desde un backup en disco. Antes de pisar
        /// nada respalda el estado actual (red de seguridad). Escribe atómico (tmp +
        /// replace, deja <c>.bak</c>). Como el origen es nuestra propia carpeta de
        /// backups (confiable), restaura TODOS los archivos (incluye lotes .txt, etc.);
        /// la protección contra escapes de ruta se mantiene.
        /// </summary>
        public static ConfigImportResult RestoreFromBackup(string backupName, IEnumerable<string> fullDirs)
        {
            var res = new ConfigImportResult { Ok = false, FilesRestored = 0 };

            string srcFull = ResolveBackupDir(backupName);
            if (srcFull == null) { res.Error = "no-existe"; return res; }

            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;

            // Mapa <nombreCarpeta> → ruta absoluta de árboles de datos (Documents\AgOpenGPS).
            var extraByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fullDirs != null)
            {
                foreach (var d in fullDirs)
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    string nm = Path.GetFileName(d.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(nm)) extraByName[nm] = d;
                }
            }

            // Red de seguridad: respaldar el estado actual antes de restaurar.
            try { RunBackup(fullDirs, force: true); } catch { }

            try
            {
                foreach (var src in Directory.GetFiles(srcFull, "*", SearchOption.AllDirectories))
                {
                    string rel = src.Substring(srcFull.Length).TrimStart('\\', '/');

                    string dest;
                    if (rel.StartsWith("_external\\", StringComparison.OrdinalIgnoreCase) ||
                        rel.StartsWith("_external/", StringComparison.OrdinalIgnoreCase))
                    {
                        // _external\<carpeta>\<subRel> → mapear <carpeta> a su ruta real.
                        string rest = rel.Substring("_external".Length).TrimStart('\\', '/');
                        int slash = rest.IndexOfAny(new[] { '\\', '/' });
                        if (slash <= 0) continue;
                        string folder = rest.Substring(0, slash);
                        string subRel = rest.Substring(slash + 1);
                        if (!extraByName.TryGetValue(folder, out var targetRoot)) continue;
                        dest = Path.GetFullPath(Path.Combine(targetRoot, subRel));
                        string troot = Path.GetFullPath(targetRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                        if (!dest.StartsWith(troot, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else
                    {
                        dest = Path.GetFullPath(Path.Combine(baseDir, rel));
                        string broot = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                        if (!dest.StartsWith(broot, StringComparison.OrdinalIgnoreCase)) continue;
                    }

                    try
                    {
                        byte[] data = File.ReadAllBytes(src);
                        AtomicWriteBytes(dest, data);
                        res.FilesRestored++;
                    }
                    catch { /* un archivo no debe abortar el resto */ }
                }

                res.Ok = true;
                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                return res;
            }
        }

        // Resuelve "Auto/<stamp>", "Instalador/<stamp>" o "<stamp>" a una carpeta
        // absoluta dentro de BackupRoot. Devuelve null si es inválida o no existe.
        private static string ResolveBackupDir(string backupName)
        {
            if (string.IsNullOrWhiteSpace(backupName)) return null;
            if (backupName.Contains("..")) return null;

            string rel = backupName.Replace('/', Path.DirectorySeparatorChar)
                                   .Replace('\\', Path.DirectorySeparatorChar)
                                   .Trim(Path.DirectorySeparatorChar);
            string srcFull = Path.GetFullPath(Path.Combine(BackupRoot, rel));
            string rootFull = Path.GetFullPath(BackupRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!srcFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
            if (!Directory.Exists(srcFull)) return null;
            return srcFull;
        }

        // ¿Ya existe un backup automático con fecha de hoy?
        private static bool HasAutoBackupToday()
        {
            try
            {
                string autoDir = Path.Combine(BackupRoot, AutoSubdir);
                if (!Directory.Exists(autoDir)) return false;
                string today = DateTime.Now.ToString("yyyyMMdd");
                foreach (var d in Directory.GetDirectories(autoDir))
                {
                    string n = Path.GetFileName(d);
                    if (n.Length >= 8 && n.Substring(0, 8) == today) return true;
                }
            }
            catch { }
            return false;
        }

        // Carpeta "yyyyMMdd-HHmmss" (con sufijo opcional "-N") → DateTime.
        private static DateTime ParseStamp(string name, DateTime fallback)
        {
            try
            {
                if (!string.IsNullOrEmpty(name) && name.Length >= 15)
                {
                    string core = name.Substring(0, 15);
                    if (DateTime.TryParseExact(core, "yyyyMMdd-HHmmss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        return dt;
                }
            }
            catch { }
            return fallback;
        }

        // Borra los backups automáticos de más de <paramref name="days"/> días.
        private static void RotateAuto(int days)
        {
            try
            {
                string autoDir = Path.Combine(BackupRoot, AutoSubdir);
                if (!Directory.Exists(autoDir)) return;
                DateTime cutoff = DateTime.Now.AddDays(-days);
                foreach (var d in Directory.GetDirectories(autoDir))
                {
                    try
                    {
                        DateTime when = ParseStamp(Path.GetFileName(d), Directory.GetCreationTime(d));
                        if (when < cutoff) Directory.Delete(d, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // --- internos -------------------------------------------------------

        // Archivos de config que cuelgan del directorio de instalación: los `.json`
        // (+ `.bak`) de la raíz y los subárboles `data\` y `AgOpenGPS\`.
        private static List<string> CollectInstallSources()
        {
            var result = new List<string>();
            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;

            AddFiles(result, baseDir, "*.json", SearchOption.TopDirectoryOnly);
            AddFiles(result, baseDir, "*.json.bak", SearchOption.TopDirectoryOnly);

            foreach (var sub in new[] { "data", "AgOpenGPS" })
            {
                string subDir = Path.Combine(baseDir, sub);
                if (Directory.Exists(subDir))
                {
                    AddFiles(result, subDir, "*.json", SearchOption.AllDirectories);
                    AddFiles(result, subDir, "*.json.bak", SearchOption.AllDirectories);
                }
            }

            return result;
        }

        // Todos los archivos de un árbol, salteando los primeros segmentos excluidos (Logs, Fields).
        private static IEnumerable<string> EnumerateTreeFiles(string root, ICollection<string> excludeTops)
        {
            var acc = new List<string>();
            try
            {
                string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetFullPath(f).Substring(rootFull.Length);
                    string first = rel.Split('\\', '/')[0];
                    if (excludeTops.Contains(first)) continue;
                    acc.Add(f);
                }
            }
            catch { }
            return acc;
        }

        // Copia un árbol completo a destRoot, salteando los primeros segmentos excluidos.
        private static void CopyTreeToBackup(string srcRoot, string destRoot, ICollection<string> excludeTops)
        {
            try
            {
                string rootFull = Path.GetFullPath(srcRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (var f in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        string rel = Path.GetFullPath(f).Substring(rootFull.Length);
                        string first = rel.Split('\\', '/')[0];
                        if (excludeTops.Contains(first)) continue;
                        CopyInto(f, Path.Combine(destRoot, rel));
                    }
                    catch { /* un archivo bloqueado no aborta el resto */ }
                }
            }
            catch { }
        }

        private static void CopyInto(string src, string dest)
        {
            string destSub = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destSub) && !Directory.Exists(destSub))
                Directory.CreateDirectory(destSub);
            File.Copy(src, dest, true);
        }

        private static void AddFiles(List<string> acc, string dir, string pattern, SearchOption opt)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, pattern, opt))
                {
                    if (!acc.Contains(f)) acc.Add(f);
                }
            }
            catch { }
        }

        private static string ComputeSignature(List<string> files)
        {
            var sb = new StringBuilder();
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var fi = new FileInfo(f);
                    sb.Append(f).Append('|').Append(fi.Length).Append('|')
                      .Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
                catch { sb.Append(f).Append("|?\n"); }
            }
            return sb.ToString();
        }

        // --- Export / Import (USB) -----------------------------------------
        //
        // Layout del ZIP:
        //   manifest.json              → { base, external: { "<carpeta>": "<ruta abs>" } }
        //   base/<rel>                 → archivos que cuelgan del dir de instalación
        //   external/<carpeta>/<rel>   → archivos de las carpetas de datos
        //
        // El export a pendrive es una copia liviana de configuración: incluye los
        // `.json`/`.bak` de instalación y los `.xml` de los árboles de datos
        // (vehículos/implementos). No incluye los lotes (.txt) para que el .zip no
        // pese de más; para respaldo completo de lotes está el backup local en disco.
        // Sólo se restauran extensiones de config (.json/.bak/.xml) para que un ZIP
        // manipulado no pueda escribir archivos arbitrarios.

        private static readonly string[] AllowedExt = { ".json", ".bak", ".xml" };

        private static bool IsAllowed(string fileName)
        {
            string lower = (fileName ?? "").ToLowerInvariant();
            return AllowedExt.Any(e => lower.EndsWith(e));
        }

        /// <summary>
        /// Arma un ZIP en memoria con la configuración (para bajar a un pendrive desde
        /// el Hub). <paramref name="fullDirs"/> = árboles de datos de PilotX.
        /// </summary>
        public static byte[] CreateExportZip(IEnumerable<string> fullDirs)
        {
            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;
            var extras = (fullDirs ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Mapa carpeta→ruta absoluta para que el import sepa dónde restaurar.
            var externalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in extras)
            {
                string name = Path.GetFileName(d.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = "ext";
                if (!externalMap.ContainsKey(name)) externalMap[name] = d;
            }

            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    var manifest = new ExportManifest
                    {
                        ts = DateTime.Now.ToString("o"),
                        baseDir = baseDir,
                        external = externalMap
                    };
                    var mopts = new JsonSerializerOptions { WriteIndented = true };
                    AddZipText(zip, "manifest.json",
                        JsonSerializer.Serialize(manifest, mopts));

                    // base/*
                    foreach (var f in CollectInstallSources())
                    {
                        if (!IsAllowed(f)) continue;
                        string rel = MakeRelativeToBase(baseDir, f);
                        if (rel == null) continue;
                        AddZipFile(zip, "base/" + rel.Replace('\\', '/'), f);
                    }

                    // external/<carpeta>/*  (sólo XML de config; sin lotes)
                    foreach (var kv in externalMap)
                    {
                        foreach (var f in EnumerateXmlFiles(kv.Value))
                        {
                            string rel = MakeRelativeToBase(kv.Value, f);
                            if (rel == null) continue;
                            AddZipFile(zip, "external/" + kv.Key + "/" + rel.Replace('\\', '/'), f);
                        }
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Restaura una configuración desde un ZIP creado por <see cref="CreateExportZip"/>.
        /// Escribe de forma atómica (tmp + replace) y deja `.bak` del archivo previo.
        /// </summary>
        public static ConfigImportResult ImportFromZip(byte[] zipBytes, IEnumerable<string> fullDirs)
        {
            var res = new ConfigImportResult { Ok = false, FilesRestored = 0 };
            if (zipBytes == null || zipBytes.Length == 0)
            { res.Error = "empty"; return res; }

            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;

            // Antes de pisar nada, respaldamos el estado actual (red de seguridad).
            try { RunBackup(fullDirs, force: true); } catch { }

            try
            {
                using (var ms = new MemoryStream(zipBytes))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    // Leer manifest para mapear carpetas externas → rutas absolutas.
                    var externalTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var manEntry = zip.GetEntry("manifest.json");
                    if (manEntry != null)
                    {
                        try
                        {
                            using (var sr = new StreamReader(manEntry.Open()))
                            {
                                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                var man = JsonSerializer.Deserialize<ExportManifest>(sr.ReadToEnd(), opts);
                                if (man != null && man.external != null)
                                    foreach (var kv in man.external) externalTargets[kv.Key] = kv.Value;
                            }
                        }
                        catch { }
                    }

                    // Fallback: si el manifest no resuelve una carpeta externa,
                    // intentamos casarla por nombre con los fullDirs provistos.
                    var extraByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (fullDirs != null)
                        foreach (var d in fullDirs)
                        {
                            if (string.IsNullOrWhiteSpace(d)) continue;
                            string name = Path.GetFileName(d.TrimEnd('\\', '/'));
                            if (!string.IsNullOrEmpty(name)) extraByName[name] = d;
                        }

                    foreach (var entry in zip.Entries)
                    {
                        string name = entry.FullName.Replace('\\', '/');
                        if (name == "manifest.json") continue;
                        if (name.EndsWith("/")) continue;            // carpeta
                        if (name.Contains("..")) continue;           // traversal
                        if (!IsAllowed(name)) continue;

                        string targetRoot = null;
                        string rel = null;
                        if (name.StartsWith("base/", StringComparison.OrdinalIgnoreCase))
                        {
                            targetRoot = baseDir;
                            rel = name.Substring("base/".Length);
                        }
                        else if (name.StartsWith("external/", StringComparison.OrdinalIgnoreCase))
                        {
                            string rest = name.Substring("external/".Length);
                            int slash = rest.IndexOf('/');
                            if (slash <= 0) continue;
                            string folder = rest.Substring(0, slash);
                            rel = rest.Substring(slash + 1);
                            if (externalTargets.TryGetValue(folder, out var t)) targetRoot = t;
                            else if (extraByName.TryGetValue(folder, out var t2)) targetRoot = t2;
                            else continue; // no sabemos dónde va → no adivinar
                        }
                        else continue;

                        if (string.IsNullOrEmpty(rel)) continue;
                        string dest = Path.GetFullPath(Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar)));

                        // Reasegurar que el destino no se escape de su raíz.
                        string rootFull = Path.GetFullPath(targetRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                        if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            byte[] data;
                            using (var es = entry.Open())
                            using (var buf = new MemoryStream())
                            { es.CopyTo(buf); data = buf.ToArray(); }
                            AtomicWriteBytes(dest, data);
                            res.FilesRestored++;
                        }
                        catch { /* un archivo no debe abortar el resto */ }
                    }
                }

                res.Ok = true;
                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                return res;
            }
        }

        private sealed class ExportManifest
        {
            public string ts { get; set; }
            public string baseDir { get; set; }
            public Dictionary<string, string> external { get; set; }
        }

        private static IEnumerable<string> EnumerateXmlFiles(string dir)
        {
            var acc = new List<string>();
            AddFiles(acc, dir, "*.XML", SearchOption.AllDirectories);
            AddFiles(acc, dir, "*.XML.bak", SearchOption.AllDirectories);
            return acc;
        }

        private static string MakeRelativeToBase(string root, string fullPath)
        {
            try
            {
                string normRoot = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                string normFull = Path.GetFullPath(fullPath);
                if (normFull.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                    return normFull.Substring(normRoot.Length);
                return null;
            }
            catch { return null; }
        }

        private static void AddZipFile(ZipArchive zip, string entryName, string srcPath)
        {
            try
            {
                var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var src = File.OpenRead(srcPath))
                using (var dst = e.Open())
                    src.CopyTo(dst);
            }
            catch { }
        }

        private static void AddZipText(ZipArchive zip, string entryName, string text)
        {
            var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                w.Write(text);
        }

        // Escritura atómica de bytes (mismo esquema que AtomicJson.Write pero para
        // contenido binario/XML): tmp + flush + File.Replace, conserva `.bak`.
        private static void AtomicWriteBytes(string path, byte[] data)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            string bak = path + ".bak";

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }

            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, bak); }
                catch
                {
                    try { File.Copy(path, bak, true); } catch { }
                    try { File.Delete(path); } catch { }
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }
}
