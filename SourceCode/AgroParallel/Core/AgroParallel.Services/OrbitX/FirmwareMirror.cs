// ============================================================================
// FirmwareMirror.cs - Espejo local de firmwares OTA desde OrbitX cloud
//
// Mantiene en disco los .bin descargados del cloud para que FirmwareLanServer
// los sirva por HTTP a los nodos ESP32 (QuantiX, VistaX, SectionX, etc.) que
// viven detrás del PC con PilotX en LAN.
//
// Layout:
//   <CacheDir>/<Producto>/<Version>/firmware.bin
//   <CacheDir>/<Producto>/<Version>/manifest.json
//   <CacheDir>/index.json    ← último catálogo del cloud
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AgroParallel.OrbitX
{
    public class FirmwareMirrorResult
    {
        public int CatalogCount;
        public int Descargados;
        public int Errores;
        public List<string> Mensajes = new List<string>();
    }

    public class FirmwareCatalogItem
    {
        public string producto { get; set; }
        public string version { get; set; }
        public string hash_sha256 { get; set; }
        public long tamano_bytes { get; set; }
        public string changelog { get; set; }
        public long ts { get; set; }
        public bool local { get; set; }  // marcado por listarLocal
    }

    public class FirmwareManifest
    {
        public string producto { get; set; }
        public string version { get; set; }
        public string hash_sha256 { get; set; }
        public long tamano_bytes { get; set; }
        public string changelog { get; set; }
        public long descargado_at { get; set; }
    }

    public static class FirmwareMirror
    {
        // ── Paths ──────────────────────────────────────────────────────────
        public static string ResolveCacheDir(OrbitXConfig cfg)
        {
            if (!string.IsNullOrEmpty(cfg.FirmwareCacheDir))
                return Path.GetFullPath(cfg.FirmwareCacheDir);
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "firmware-cache");
        }

        public static string DirVersion(string cacheDir, string producto, string version)
            => Path.Combine(cacheDir, producto, version);

        public static string PathBin(string cacheDir, string producto, string version)
            => Path.Combine(DirVersion(cacheDir, producto, version), "firmware.bin");

        public static string PathManifest(string cacheDir, string producto, string version)
            => Path.Combine(DirVersion(cacheDir, producto, version), "manifest.json");

        public static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        // ── SHA256 de archivo ──────────────────────────────────────────────
        public static string Sha256File(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(filePath))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ── Lista lo que hay en cache (con flag local=true) ────────────────
        public static List<FirmwareCatalogItem> ListLocal(string cacheDir)
        {
            var result = new List<FirmwareCatalogItem>();
            if (!Directory.Exists(cacheDir)) return result;

            // Si hay index.json reciente, lo usamos y marcamos cuáles están en disco.
            string idx = Path.Combine(cacheDir, "index.json");
            if (File.Exists(idx))
            {
                try
                {
                    var json = File.ReadAllText(idx);
                    var doc = JsonSerializer.Deserialize<IndexFile>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (doc?.catalog != null)
                    {
                        foreach (var fw in doc.catalog)
                        {
                            fw.local = File.Exists(PathBin(cacheDir, fw.producto, fw.version));
                            result.Add(fw);
                        }
                        return result;
                    }
                }
                catch { /* fallthrough → derivar del filesystem */ }
            }

            // Sin index — recorrer carpetas.
            foreach (var dirP in Directory.GetDirectories(cacheDir))
            {
                string producto = Path.GetFileName(dirP);
                foreach (var dirV in Directory.GetDirectories(dirP))
                {
                    string version = Path.GetFileName(dirV);
                    string manPath = PathManifest(cacheDir, producto, version);
                    if (!File.Exists(manPath)) continue;
                    try
                    {
                        var m = JsonSerializer.Deserialize<FirmwareManifest>(
                            File.ReadAllText(manPath),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (m == null) continue;
                        result.Add(new FirmwareCatalogItem
                        {
                            producto     = m.producto,
                            version      = m.version,
                            hash_sha256  = m.hash_sha256,
                            tamano_bytes = m.tamano_bytes,
                            changelog    = m.changelog,
                            ts           = m.descargado_at,
                            local        = File.Exists(PathBin(cacheDir, producto, version)),
                        });
                    }
                    catch { /* manifest roto, ignorar */ }
                }
            }
            return result;
        }

        // ── Sincroniza catálogo cloud → cache local ────────────────────────
        public static async Task<FirmwareMirrorResult> SyncAsync(
            HttpClient http, OrbitXConfig cfg, Action<string> log = null)
        {
            var res = new FirmwareMirrorResult();
            string cacheDir = ResolveCacheDir(cfg);
            EnsureDir(cacheDir);

            string url = cfg.ServerUrl.TrimEnd('/') + "/api/ota/catalogo";
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Add("X-Device-ID", cfg.DeviceId);
                req.Headers.Add("X-Auth-Token", cfg.DeviceToken);

                using (var resp = await http.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        throw new Exception($"Catálogo HTTP {(int)resp.StatusCode}: {Trunc(body, 200)}");
                    }
                    string json = await resp.Content.ReadAsStringAsync();
                    var catalog = JsonSerializer.Deserialize<List<FirmwareCatalogItem>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new List<FirmwareCatalogItem>();

                    res.CatalogCount = catalog.Count;

                    // Guardar index.json (para servir aún sin internet).
                    var idx = new IndexFile { ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), catalog = catalog };
                    File.WriteAllText(Path.Combine(cacheDir, "index.json"),
                        JsonSerializer.Serialize(idx, new JsonSerializerOptions { WriteIndented = true }));

                    foreach (var fw in catalog)
                    {
                        if (string.IsNullOrEmpty(fw.producto) || string.IsNullOrEmpty(fw.version)) continue;
                        string bin = PathBin(cacheDir, fw.producto, fw.version);
                        if (File.Exists(bin)) continue; // ya está

                        try
                        {
                            await DownloadAsync(http, cfg, fw, cacheDir, log);
                            res.Descargados++;
                        }
                        catch (Exception ex)
                        {
                            res.Errores++;
                            res.Mensajes.Add($"{fw.producto} {fw.version}: {ex.Message}");
                            log?.Invoke($"FW mirror error {fw.producto} {fw.version}: {ex.Message}");
                        }
                    }
                }
            }
            return res;
        }

        // ── Baja un .bin específico al cache ───────────────────────────────
        private static async Task DownloadAsync(
            HttpClient http, OrbitXConfig cfg, FirmwareCatalogItem fw,
            string cacheDir, Action<string> log)
        {
            EnsureDir(DirVersion(cacheDir, fw.producto, fw.version));
            string dst = PathBin(cacheDir, fw.producto, fw.version);
            string tmp = dst + ".part";

            string url = $"{cfg.ServerUrl.TrimEnd('/')}/api/ota/firmware/" +
                         $"{Uri.EscapeDataString(fw.producto)}/{Uri.EscapeDataString(fw.version)}";
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Add("X-Device-ID", cfg.DeviceId);
                req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var fs = File.Create(tmp))
                    {
                        await src.CopyToAsync(fs);
                    }
                }
            }

            // Verificar SHA256 si vino en el catálogo.
            string got = Sha256File(tmp);
            if (!string.IsNullOrEmpty(fw.hash_sha256) &&
                !string.Equals(got, fw.hash_sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new Exception(
                    $"SHA256 mismatch (esperado {fw.hash_sha256.Substring(0, 8)}.., recibido {got.Substring(0, 8)}..)");
            }

            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);

            // Manifest local.
            var manifest = new FirmwareManifest
            {
                producto       = fw.producto,
                version        = fw.version,
                hash_sha256    = got,
                tamano_bytes   = fw.tamano_bytes > 0 ? fw.tamano_bytes : new FileInfo(dst).Length,
                changelog      = fw.changelog ?? "",
                descargado_at  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            File.WriteAllText(PathManifest(cacheDir, fw.producto, fw.version),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            log?.Invoke($"FW descargado: {fw.producto} {fw.version} ({manifest.tamano_bytes / 1024.0:F1} KB)");
        }

        private static string Trunc(string s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));

        // ── Tipos auxiliares para serialización ────────────────────────────
        private class IndexFile
        {
            public long ts { get; set; }
            public List<FirmwareCatalogItem> catalog { get; set; }
        }
    }
}
