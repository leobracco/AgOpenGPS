// ============================================================================
// FirmwaresController.cs
//
// Endpoints REST para administrar el cache LOCAL de firmwares (los .bin que
// FirmwareLanServer sirve a los nodos ESP32 por LAN). Cubre el caso "no tengo
// internet pero tengo el .bin en un pendrive" — el técnico de campo puede
// subir el firmware al Hub sin pasar por el cloud.
//
// Endpoints:
//   GET    /api/firmwares                           → lista todo el cache
//   POST   /api/firmwares/upload                    → sube un .bin (binary body)
//          headers: X-AP-Producto, X-AP-Version, X-AP-Changelog (opt)
//          body: raw bytes del firmware.bin
//   DELETE /api/firmwares/{producto}/{version}      → borra del cache
//
// El catálogo sincronizado desde OrbitX cloud (index.json en cacheDir) y los
// uploads locales conviven en la misma carpeta — el manifest.json de cada
// versión es la fuente de verdad para "hash + size + changelog".
// ============================================================================

using AgroParallel.OrbitX;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class FirmwaresController : WebApiController
    {
        // Mismo regex que usa el router del cloud OrbitX y el LAN server para
        // armar paths — alfanumérico (case-insensitive) + guion para casos como
        // "corex-ecu". Lo guardamos siempre en lowercase para que matchee el
        // topic MQTT del producto (`agp/{producto}/...`).
        private static readonly Regex RxProducto = new Regex("^[a-zA-Z][a-zA-Z0-9-]{1,31}$");
        // Semver permisivo: dígitos, puntos, guiones, letras para pre-release.
        private static readonly Regex RxVersion = new Regex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,31}$");
        // Cap del .bin para que un upload errado no llene el disco. 8 MB sobra
        // para ESP32 (partition OTA típica = 1.3-1.9 MB).
        private const long MaxBinBytes = 8L * 1024 * 1024;

        [Route(HttpVerbs.Get, "/firmwares")]
        public object List()
        {
            OrbitXConfig cfg = SafeLoadOrbitX();
            string cacheDir = FirmwareMirror.ResolveCacheDir(cfg);
            var all = FirmwareMirror.ListLocal(cacheDir) ?? new List<FirmwareCatalogItem>();

            // Agrupado por producto + ordenado desc por versión para que la UI
            // muestre "última primero" sin trabajo extra.
            var byProducto = all
                .GroupBy(f => (f.producto ?? "").Trim().ToLowerInvariant(),
                         StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    producto = g.Key,
                    versiones = g
                        .OrderByDescending(f => f.version, StringComparer.OrdinalIgnoreCase)
                        .Select(f => new
                        {
                            version = f.version,
                            hash_sha256 = f.hash_sha256,
                            tamano_bytes = f.tamano_bytes,
                            changelog = f.changelog,
                            ts = f.ts,
                            local = f.local
                        })
                        .ToList()
                })
                .ToList();

            int port = cfg != null && cfg.FirmwareHttpPort > 0 ? cfg.FirmwareHttpPort : 8088;
            string lan = FirmwareOtaClient.ResolveLanIp();

            return new
            {
                ok = true,
                cache_dir = cacheDir,
                lan_ip = lan,
                http_port = port,
                productos = byProducto
            };
        }

        [Route(HttpVerbs.Post, "/firmwares/upload")]
        public async Task<object> Upload()
        {
            string producto = (HttpContext.Request.Headers["X-AP-Producto"] ?? "").Trim();
            string version = (HttpContext.Request.Headers["X-AP-Version"] ?? "").Trim();
            string changelog = HttpContext.Request.Headers["X-AP-Changelog"] ?? "";

            if (string.IsNullOrEmpty(producto) || !RxProducto.IsMatch(producto))
                return new { ok = false, error = "invalid-producto" };
            if (string.IsNullOrEmpty(version) || !RxVersion.IsMatch(version))
                return new { ok = false, error = "invalid-version" };

            string prodLo = producto.ToLowerInvariant();

            OrbitXConfig cfg = SafeLoadOrbitX();
            string cacheDir = FirmwareMirror.ResolveCacheDir(cfg);
            FirmwareMirror.EnsureDir(cacheDir);

            string dirVer = FirmwareMirror.DirVersion(cacheDir, prodLo, version);
            FirmwareMirror.EnsureDir(dirVer);

            string dst = FirmwareMirror.PathBin(cacheDir, prodLo, version);
            string tmp = dst + ".part";

            // Stream a disco. No leemos en memoria entera porque puede ser
            // 1-2 MB y EmbedIO no nos garantiza Content-Length confiable.
            // Cortamos si supera MaxBinBytes — protege el disco contra typos
            // (subir el .zip en vez del .bin, etc.).
            long total = 0;
            try
            {
                using (var input = HttpContext.Request.InputStream)
                using (var fs = File.Create(tmp))
                {
                    byte[] buf = new byte[16 * 1024];
                    int n;
                    while ((n = await input.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > MaxBinBytes)
                        {
                            fs.Dispose();
                            try { File.Delete(tmp); } catch { }
                            return new { ok = false, error = "file-too-large", max_bytes = MaxBinBytes };
                        }
                        await fs.WriteAsync(buf, 0, n).ConfigureAwait(false);
                    }
                }

                if (total < 1024)
                {
                    try { File.Delete(tmp); } catch { }
                    return new { ok = false, error = "file-too-small", bytes = total };
                }

                // Hash + manifest. Igual que FirmwareMirror.DownloadAsync.
                string sha = FirmwareMirror.Sha256File(tmp);
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);

                var manifest = new FirmwareManifest
                {
                    producto = prodLo,
                    version = version,
                    hash_sha256 = sha,
                    tamano_bytes = total,
                    changelog = changelog ?? "",
                    descargado_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                File.WriteAllText(
                    FirmwareMirror.PathManifest(cacheDir, prodLo, version),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

                return new
                {
                    ok = true,
                    producto = prodLo,
                    version,
                    hash_sha256 = sha,
                    tamano_bytes = total
                };
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return new { ok = false, error = "upload-failed", detail = ex.Message };
            }
        }

        [Route(HttpVerbs.Delete, "/firmwares/{producto}/{version}")]
        public object Delete(string producto, string version)
        {
            if (string.IsNullOrEmpty(producto) || !RxProducto.IsMatch(producto))
                return new { ok = false, error = "invalid-producto" };
            if (string.IsNullOrEmpty(version) || !RxVersion.IsMatch(version))
                return new { ok = false, error = "invalid-version" };

            string prodLo = producto.ToLowerInvariant();
            OrbitXConfig cfg = SafeLoadOrbitX();
            string cacheDir = FirmwareMirror.ResolveCacheDir(cfg);
            string dirVer = FirmwareMirror.DirVersion(cacheDir, prodLo, version);

            if (!Directory.Exists(dirVer))
                return new { ok = false, error = "not-found" };

            try
            {
                Directory.Delete(dirVer, recursive: true);
                // Si la carpeta del producto queda vacía, también la limpiamos —
                // sino la UI muestra el producto "sin versiones" indefinidamente.
                string dirProd = Path.Combine(cacheDir, prodLo);
                try
                {
                    if (Directory.Exists(dirProd) &&
                        !Directory.EnumerateFileSystemEntries(dirProd).Any())
                        Directory.Delete(dirProd, recursive: false);
                }
                catch { /* ignorar — el dir queda, no es crítico */ }

                return new { ok = true, producto = prodLo, version };
            }
            catch (Exception ex)
            {
                return new { ok = false, error = "delete-failed", detail = ex.Message };
            }
        }

        private OrbitXConfig SafeLoadOrbitX()
        {
            try { return OrbitXConfig.Load(); } catch { return new OrbitXConfig(); }
        }
    }
}
