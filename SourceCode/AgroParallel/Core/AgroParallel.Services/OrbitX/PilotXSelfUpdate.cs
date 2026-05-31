// ============================================================================
// PilotXSelfUpdate.cs
// Núcleo del auto-update de la app PC PilotX.
//
// Canal PÚBLICO (sin device-auth) — pedido explícito 2026-05-27:
//   GET  https://agroparallel.com/update/pilotx/latest.json
//        → { "version":"...", "url":"...", "sha256":"...",
//            "size_bytes":..., "changelog":"..." }
//   GET  <url-del-manifest>           (ZIP del payload)
//
// Ventajas vs el viejo OTA OrbitX (que sigue existiendo para los nodos ESP32):
//  · No requiere pairing/device-token: cualquier tractor encendido y con
//    internet puede actualizar — útil cuando el operario está con el cloud
//    desvinculado o el master_token roto.
//  · Deploy = subir 2 archivos a la web pública (latest.json + el zip).
//  · Cero cambios server-side para shippear una versión nueva.
//
// Stage: <baseDir>/AgroParallel/Updates/<version>/payload.zip
// Apply: lanza Updater.exe con args { --pid, --zip, --install, --exe } y sale.
//
// La detección "ya estoy actualizado" es por comparación semver (1.2.3 < 1.2.4).
// El número de versión vivo lo expone la AssemblyInformationalVersionAttribute
// del .exe (o cae a "0.0.0" si no está set).
// ============================================================================

using System;
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
        // Canal público — pedido explícito 2026-05-27 "que busque las
        // actualizaciones PilotX directamente en agroparallel.com/update".
        // Sin device-auth: solo hay que tener internet en el tractor.
        public const string PublicUpdateBaseUrl = "https://agroparallel.com/update/pilotx/";
        public const string PublicManifestUrl  = PublicUpdateBaseUrl + "latest.json";
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
            return Path.Combine(InstallDir(), "AgOpenGPS.exe");
        }

        // ── Manifest DTO (latest.json del canal público) ───────────────────
        // Shape esperado:
        //   { "version": "2026.5.27",
        //     "url":     "https://agroparallel.com/update/pilotx/2026.5.27.zip",
        //     "sha256":  "abc123…",
        //     "size_bytes": 12345678,
        //     "changelog": "Fixes prescripciones, lock URL OrbitX, …" }
        // `url` puede ser absoluta o relativa (en cuyo caso se resuelve contra
        // PublicUpdateBaseUrl). `sha256` y `changelog` son opcionales pero muy
        // recomendados — sin sha256 saltea verificación y confía en HTTPS.
        private sealed class PublicManifest
        {
            public string version { get; set; }
            public string url { get; set; }
            public string sha256 { get; set; }
            public long size_bytes { get; set; }
            public string changelog { get; set; }
        }

        // ── Check ──────────────────────────────────────────────────────────
        // El parámetro `cfg` se mantiene por compatibilidad con el adapter pero
        // YA NO se usa — el canal de update es público y vive en
        // PublicManifestUrl. Si en algún momento queremos volver al OTA OrbitX
        // device-auth, hay que rescatar el código del git log (commit anterior).
        public static async Task<PilotXUpdateStatus> CheckAsync(HttpClient http, OrbitXConfig cfg)
        {
            Update(s => { s.Phase = PilotXUpdatePhase.Checking; s.LastError = null; });
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, PublicManifestUrl))
                {
                    // Cache-bust: el operario que tocó "Buscar" quiere la versión
                    // nueva ya, no la cacheada por un CDN intermedio.
                    req.Headers.Add("Cache-Control", "no-cache");
                    req.Headers.Add("Pragma", "no-cache");
                    using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            throw new Exception("Manifest HTTP " + (int)resp.StatusCode
                                + " en " + PublicManifestUrl
                                + (string.IsNullOrEmpty(body) ? "" : " · " + Trunc(body, 160)));
                        }
                        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var m = JsonSerializer.Deserialize<PublicManifest>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (m == null || string.IsNullOrEmpty(m.version))
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

                        bool newer = CompareSemver(m.version, DetectCurrentVersion()) > 0;
                        bool staged = File.Exists(StagingZip(m.version));

                        Update(s =>
                        {
                            s.AvailableVersion = m.version;
                            s.Changelog = m.changelog ?? "";
                            s.SizeBytes = m.size_bytes;
                            s.Sha256 = m.sha256;
                            s.LastCheckUnixMs = nowMs;
                            s.StagingReady = staged;
                            // Guardamos la URL del ZIP en el campo Changelog NO,
                            // mejor en un slot dedicado: aprovecho LastError=null
                            // y persisto la URL en una static aparte.
                            if (staged) s.Phase = PilotXUpdatePhase.ReadyToApply;
                            else if (newer) s.Phase = PilotXUpdatePhase.UpdateAvailable;
                            else s.Phase = PilotXUpdatePhase.Idle;
                        });

                        // URL del ZIP — la guardamos aparte para que Download la use.
                        lock (_lock) { _lastManifestZipUrl = ResolveZipUrl(m.url, m.version); }
                    }
                }
            }
            catch (Exception ex)
            {
                Update(s => { s.Phase = PilotXUpdatePhase.Error; s.LastError = ex.Message; });
            }
            return Snapshot();
        }

        // URL del ZIP de la última check — resuelta contra PublicUpdateBaseUrl
        // si vino relativa. Cleared/seteada SOLO desde CheckAsync.
        private static string _lastManifestZipUrl;

        private static string ResolveZipUrl(string urlFromManifest, string version)
        {
            if (!string.IsNullOrEmpty(urlFromManifest))
            {
                if (urlFromManifest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || urlFromManifest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return urlFromManifest;
                // Relativa al folder del manifest.
                return PublicUpdateBaseUrl + urlFromManifest.TrimStart('/');
            }
            // Convención por defecto si el manifest no trae `url`.
            return PublicUpdateBaseUrl + Uri.EscapeDataString(version) + ".zip";
        }

        // ── Download ───────────────────────────────────────────────────────
        // El `cfg` queda ignorado — la URL del ZIP la determinó CheckAsync
        // desde el manifest público.
        public static async Task<PilotXUpdateStatus> DownloadAsync(HttpClient http, OrbitXConfig cfg)
        {
            string version, expectedHash, zipUrl;
            lock (_lock)
            {
                version = _status.AvailableVersion;
                expectedHash = _status.Sha256;
                zipUrl = _lastManifestZipUrl;
            }
            if (string.IsNullOrEmpty(version))
            {
                Update(s => { s.Phase = PilotXUpdatePhase.Error; s.LastError = "No hay versión disponible. Ejecutá Check primero."; });
                return Snapshot();
            }
            if (string.IsNullOrEmpty(zipUrl))
            {
                // Fallback por si Check no se llamó (improbable): convención.
                zipUrl = PublicUpdateBaseUrl + Uri.EscapeDataString(version) + ".zip";
            }

            Update(s => { s.Phase = PilotXUpdatePhase.Downloading; s.ProgressPct = 0; s.LastError = null; });

            try
            {
                Directory.CreateDirectory(StagingDir(version));
                string zip = StagingZip(version);
                string tmp = zip + ".part";
                if (File.Exists(tmp)) File.Delete(tmp);

                using (var req = new HttpRequestMessage(HttpMethod.Get, zipUrl))
                {
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

                // Verifica SHA256 si el manifest lo trajo; si no, confiamos en HTTPS.
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
