// ============================================================================
// PilotXSelfUpdate.cs
// Núcleo del auto-update de la app PC PilotX.
//
// Canal OrbitX (device-auth) — pedido explícito 2026-06-08:
//   "subir el ZIP a OrbitX y que el tractor lo baje on-demand para actualizar".
//
//   GET  <ServerUrl>/api/ota/catalogo?producto=PilotX     (X-Device-ID/X-Auth-Token)
//        → [ { producto, version, hash_sha256, tamano_bytes, changelog, ts }, … ]
//   GET  <ServerUrl>/api/ota/firmware/PilotX/<version>     (mismo device-auth)
//        → ZIP del payload (el server lo guarda como <version>.bin, son los
//          mismos bytes; el Updater lo trata como ZIP por magic bytes PK).
//
// On-demand: el mirror periódico de firmwares (FirmwareMirror) NO baja PilotX
// (lo saltea explícito); el ZIP se descarga SOLO cuando el operario toca
// "Buscar"/"Descargar" en el panel de actualizaciones. Así no saturamos datos
// móviles bajando decenas de MB a cada tractor en cada tick.
//
// Requiere tractor VINCULADO a OrbitX (DeviceId + DeviceToken). Si no está
// vinculado, Check/Download devuelven error claro — no hay fallback público.
//
// Stage: <baseDir>/AgroParallel/Updates/<version>/payload.zip
// Apply: lanza Updater.exe con args { --pid, --zip, --install, --exe } y sale.
//
// La detección "ya estoy actualizado" es por comparación semver (1.2.3 < 1.2.4).
// El número de versión vivo lo expone la AssemblyInformationalVersionAttribute
// del .exe (o cae a "0.0.0" si no está set).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.OrbitX
{
    public static class PilotXSelfUpdate
    {
        private const string PRODUCT = "PilotX";

        /// <summary>
        /// Se dispara cuando ApplyAsync lanzó el Updater y el host debe cerrarse
        /// ordenadamente (guardar lote, etc.). El host (FormGPS) se suscribe una
        /// vez y hace Close(). Si nadie se suscribe, el Updater igual mata el
        /// proceso por timeout (60s) — pero eso saltea el guardado de lote.
        /// </summary>
        public static event Action ApplyRequested;

        private static readonly object _lock = new object();
        private static PilotXUpdateStatus _status = new PilotXUpdateStatus
        {
            Phase = PilotXUpdatePhase.Idle,
            CurrentVersion = DetectCurrentVersion(),
            ProgressPct = -1
        };

        public static PilotXUpdateStatus Snapshot()
        {
            lock (_lock) return Clone(_status);
        }

        private static void Update(Action<PilotXUpdateStatus> mut)
        {
            lock (_lock)
            {
                mut(_status);
            }
        }

        // ── Versión actual (AssemblyInformationalVersion) ──────────────────
        public static string DetectCurrentVersion()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (attr != null && !string.IsNullOrEmpty(attr.InformationalVersion))
                {
                    // Algunos toolchains agregan "+commitsha" — lo descartamos.
                    var v = attr.InformationalVersion;
                    int p = v.IndexOf('+');
                    if (p > 0) v = v.Substring(0, p);
                    return v;
                }
                var ver = asm.GetName().Version;
                return ver != null ? ver.ToString() : "0.0.0";
            }
            catch { return "0.0.0"; }
        }

        // ── Paths ──────────────────────────────────────────────────────────
        public static string InstallDir()
            => AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        public static string StagingRoot()
            => Path.Combine(InstallDir(), "AgroParallel", "Updates");

        public static string StagingDir(string version)
            => Path.Combine(StagingRoot(), version);

        public static string StagingZip(string version)
            => Path.Combine(StagingDir(version), "payload.zip");

        public static string UpdaterExe()
            => Path.Combine(InstallDir(), "AgroParallel.Updater.exe");

        public static string EntryExe()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly();
                if (asm != null && !string.IsNullOrEmpty(asm.Location))
                    return asm.Location;
            }
            catch { }
            return Path.Combine(InstallDir(), "PilotX.exe");
        }

        // ── Catálogo OTA OrbitX (item de /api/ota/catalogo) ────────────────
        private sealed class CatalogItem
        {
            public string producto { get; set; }
            public string version { get; set; }
            public string hash_sha256 { get; set; }
            public long tamano_bytes { get; set; }
            public string changelog { get; set; }
            public long ts { get; set; }
        }

        // Valida que la config tenga lo necesario para hablar con OrbitX.
        private static void RequireAuth(OrbitXConfig cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.ServerUrl)
                || string.IsNullOrEmpty(cfg.DeviceId) || string.IsNullOrEmpty(cfg.DeviceToken))
                throw new Exception("Tractor no vinculado a OrbitX — no se puede buscar/descargar la actualización.");
        }

        // ── Check ──────────────────────────────────────────────────────────
        // Pide el catálogo OTA filtrado por PilotX y elige la mayor versión semver.
        public static async Task<PilotXUpdateStatus> CheckAsync(HttpClient http, OrbitXConfig cfg)
        {
            Update(s => { s.Phase = PilotXUpdatePhase.Checking; s.LastError = null; });
            try
            {
                RequireAuth(cfg);

                string url = cfg.ServerUrl.TrimEnd('/') + "/api/ota/catalogo?producto=" + Uri.EscapeDataString(PRODUCT);
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                    // Cache-bust: el operario que tocó "Buscar" quiere la versión
                    // nueva ya, no la cacheada por un CDN intermedio.
                    req.Headers.Add("Cache-Control", "no-cache");
                    req.Headers.Add("Pragma", "no-cache");
                    using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            throw new Exception("Catálogo HTTP " + (int)resp.StatusCode
                                + " en " + url
                                + (string.IsNullOrEmpty(body) ? "" : " · " + Trunc(body, 160)));
                        }
                        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var list = JsonSerializer.Deserialize<List<CatalogItem>>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? new List<CatalogItem>();

                        // Mayor versión semver de PilotX (el server ya filtra por
                        // producto, pero revalidamos por las dudas).
                        CatalogItem latest = null;
                        foreach (var it in list)
                        {
                            if (it == null || string.IsNullOrEmpty(it.version)) continue;
                            if (!string.Equals(it.producto, PRODUCT, StringComparison.OrdinalIgnoreCase)) continue;
                            if (latest == null || CompareSemver(it.version, latest.version) > 0) latest = it;
                        }

                        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (latest == null)
                        {
                            Update(s =>
                            {
                                s.Phase = PilotXUpdatePhase.Idle;
                                s.AvailableVersion = null;
                                s.Changelog = null;
                                s.SizeBytes = 0;
                                s.Sha256 = null;
                                s.LastCheckUnixMs = nowMs;
                                s.StagingReady = false;
                            });
                            return Snapshot();
                        }

                        bool newer = CompareSemver(latest.version, DetectCurrentVersion()) > 0;
                        bool staged = File.Exists(StagingZip(latest.version));

                        Update(s =>
                        {
                            s.AvailableVersion = latest.version;
                            s.Changelog = latest.changelog ?? "";
                            s.SizeBytes = latest.tamano_bytes;
                            s.Sha256 = latest.hash_sha256;
                            s.LastCheckUnixMs = nowMs;
                            s.StagingReady = staged;
                            if (staged) s.Phase = PilotXUpdatePhase.ReadyToApply;
                            else if (newer) s.Phase = PilotXUpdatePhase.UpdateAvailable;
                            else s.Phase = PilotXUpdatePhase.Idle;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Update(s => { s.Phase = PilotXUpdatePhase.Error; s.LastError = ex.Message; });
            }
            return Snapshot();
        }

        // ── Download ───────────────────────────────────────────────────────
        // Baja el ZIP de PilotX desde OrbitX (device-auth) a staging y verifica
        // SHA256 contra el hash del catálogo.
        public static async Task<PilotXUpdateStatus> DownloadAsync(HttpClient http, OrbitXConfig cfg)
        {
            string version, expectedHash;
            lock (_lock)
            {
                version = _status.AvailableVersion;
                expectedHash = _status.Sha256;
            }
            if (string.IsNullOrEmpty(version))
            {
                Update(s => { s.Phase = PilotXUpdatePhase.Error; s.LastError = "No hay versión disponible. Ejecutá Check primero."; });
                return Snapshot();
            }

            try
            {
                RequireAuth(cfg);
            }
            catch (Exception ex)
            {
                Update(s => { s.Phase = PilotXUpdatePhase.Error; s.LastError = ex.Message; });
                return Snapshot();
            }

            Update(s => { s.Phase = PilotXUpdatePhase.Downloading; s.ProgressPct = 0; s.LastError = null; });

            try
            {
                Directory.CreateDirectory(StagingDir(version));
                string zip = StagingZip(version);
                string tmp = zip + ".part";
                if (File.Exists(tmp)) File.Delete(tmp);

                string zipUrl = cfg.ServerUrl.TrimEnd('/') + "/api/ota/firmware/"
                              + Uri.EscapeDataString(PRODUCT) + "/" + Uri.EscapeDataString(version);
                using (var req = new HttpRequestMessage(HttpMethod.Get, zipUrl))
                {
                    req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                    req.Headers.Add("Cache-Control", "no-cache");
                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string body = "";
                            try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                            throw new Exception("ZIP HTTP " + (int)resp.StatusCode
                                + " en " + zipUrl
                                + (string.IsNullOrEmpty(body) ? "" : " · " + Trunc(body, 160)));
                        }
                        long total = resp.Content.Headers.ContentLength ?? 0;
                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var fs = File.Create(tmp))
                        {
                            byte[] buf = new byte[64 * 1024];
                            long read = 0; int n;
                            while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                            {
                                await fs.WriteAsync(buf, 0, n).ConfigureAwait(false);
                                read += n;
                                if (total > 0)
                                {
                                    int pct = (int)(read * 100L / total);
                                    Update(s => { s.ProgressPct = pct; });
                                }
                            }
                        }
                    }
                }

                // Verifica SHA256 si el catálogo lo trajo; si no, confiamos en HTTPS.
                string got = FirmwareMirror.Sha256File(tmp);
                if (!string.IsNullOrEmpty(expectedHash) && !string.Equals(got, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    throw new Exception("SHA256 no coincide (esperado " + expectedHash.Substring(0, 8) + ".., recibido " + got.Substring(0, 8) + "..)");
                }

                if (File.Exists(zip)) File.Delete(zip);
                File.Move(tmp, zip);

                Update(s =>
                {
                    s.Phase = PilotXUpdatePhase.ReadyToApply;
                    s.StagingReady = true;
                    s.ProgressPct = 100;
                });
            }
            catch (Exception ex)
            {
                Update(s => { s.Phase = PilotXUpdatePhase.Error; s.LastError = ex.Message; });
            }
            return Snapshot();
        }

        // ── Apply ──────────────────────────────────────────────────────────
        /// <summary>
        /// Lanza el updater externo y devuelve. El host debe cerrar PilotX inmediatamente
        /// después; el updater detecta el cierre por PID y empieza el reemplazo.
        /// </summary>
        public static Task<PilotXUpdateStatus> ApplyAsync()
        {
            try
            {
                string version;
                lock (_lock) { version = _status.AvailableVersion; }
                if (string.IsNullOrEmpty(version))
                    throw new InvalidOperationException("No hay versión staged.");

                string zip = StagingZip(version);
                if (!File.Exists(zip))
                    throw new FileNotFoundException("ZIP no está en staging: " + zip);

                string updater = UpdaterExe();
                if (!File.Exists(updater))
                    throw new FileNotFoundException("AgroParallel.Updater.exe no encontrado en " + updater);

                string install = InstallDir();
                string exe = EntryExe();
                int pid = Process.GetCurrentProcess().Id;

                var psi = new ProcessStartInfo
                {
                    FileName = updater,
                    Arguments = "--pid " + pid +
                                " --zip \""    + zip     + "\"" +
                                " --install \""+ install + "\"" +
                                " --exe \""    + exe     + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = install
                };
                Process.Start(psi);
                Update(s => { s.Phase = PilotXUpdatePhase.Applying; });

                // Pedirle al host que se cierre ordenadamente. Esperamos un toque
                // para que el HTTP response de /apply llegue al WebView antes de
                // bajar la app; si no, el Updater igual lo mata por timeout.
                var handler = ApplyRequested;
                if (handler != null)
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(1500).ConfigureAwait(false);
                        try { handler(); } catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                Update(s => { s.Phase = PilotXUpdatePhase.Error; s.LastError = ex.Message; });
            }
            return Task.FromResult(Snapshot());
        }

        // ── Helpers ────────────────────────────────────────────────────────
        public static int CompareSemver(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
            int[] pa = ParseSemver(a);
            int[] pb = ParseSemver(b);
            for (int i = 0; i < 3; i++)
            {
                if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
            }
            return 0;
        }

        private static int[] ParseSemver(string v)
        {
            var r = new int[3];
            if (string.IsNullOrEmpty(v)) return r;
            // Strip pre-release/build (1.2.3-rc1+sha)
            int dash = v.IndexOfAny(new[] { '-', '+' });
            if (dash > 0) v = v.Substring(0, dash);
            var parts = v.Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int n;
                int.TryParse(parts[i], out n);
                r[i] = n;
            }
            return r;
        }

        private static string Trunc(string s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));

        private static PilotXUpdateStatus Clone(PilotXUpdateStatus s)
        {
            return new PilotXUpdateStatus
            {
                Phase = s.Phase,
                CurrentVersion = s.CurrentVersion,
                AvailableVersion = s.AvailableVersion,
                Changelog = s.Changelog,
                SizeBytes = s.SizeBytes,
                Sha256 = s.Sha256,
                ProgressPct = s.ProgressPct,
                LastError = s.LastError,
                LastCheckUnixMs = s.LastCheckUnixMs,
                StagingReady = s.StagingReady
            };
        }
    }
}
