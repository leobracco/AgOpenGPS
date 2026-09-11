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
        // Producto por defecto (PilotX Windows). Check/Download aceptan un
        // "product" explícito para reusar este mismo motor desde otras
        // plataformas (ej. Android, catálogo "PilotXAndroid") sin duplicar
        // la lógica de catálogo+descarga+SHA256. ApplyAsync() NO se
        // generaliza: es intrínsecamente Windows (spawnea el Updater.exe
        // externo) — cada plataforma implementa su propio Apply.
        private const string DefaultProduct = "PilotX";

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

        /// <summary>
        /// Corrige la versión actual detectada. Pensado para hosts donde
        /// DetectCurrentVersion() (AssemblyInformationalVersion) no refleja
        /// la versión real — ej. Android, donde la versión visible al
        /// usuario es PackageManager.GetPackageInfo(...).VersionName, no el
        /// atributo del assembly .NET. Llamar una sola vez al arrancar,
        /// antes de cualquier Check/Download. No-op si version es vacío.
        /// </summary>
        public static void SetCurrentVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            Update(s => s.CurrentVersion = version);
        }

        /// <summary>
        /// Marca la fase como Applying sin pasar por ApplyAsync() (que es
        /// intrínsecamente Windows — spawnea el Updater.exe externo). Otras
        /// plataformas con su propio mecanismo de instalación (ej. Android,
        /// que dispara el instalador del sistema via Intent) llaman esto
        /// para que el status refleje que ya se disparó la instalación.
        /// </summary>
        public static void MarkApplying()
        {
            Update(s => s.Phase = PilotXUpdatePhase.Applying);
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
            catch { return "0.0.0"; } // silencioso a propósito: fallback de versión si no se puede leer
        }

        // ── Paths ──────────────────────────────────────────────────────────
        // En el layout desktop actual el proceso que hace Apply es el ENGINE,
        // que corre desde <install>\Engine\ — pero el ZIP de update se extrae
        // sobre la RAÍZ de la instalación (donde viven Desktop\, el Updater y
        // Lanzar-PilotX.bat). Si el directorio del proceso se llama "Engine" y
        // el padre tiene pinta de instalación (existe Desktop\ o el .bat), el
        // install dir es el padre. Si no, el propio BaseDirectory — compat con
        // layouts viejos (todo plano) y con Android, que usa esta misma clase
        // (su dataDir nunca se llama "Engine").
        public static string InstallDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            try
            {
                if (string.Equals(Path.GetFileName(baseDir), "Engine", StringComparison.OrdinalIgnoreCase))
                {
                    string parent = Path.GetDirectoryName(baseDir);
                    if (!string.IsNullOrEmpty(parent) &&
                        (Directory.Exists(Path.Combine(parent, "Desktop")) ||
                         File.Exists(Path.Combine(parent, "Lanzar-PilotX.bat"))))
                        return parent;
                }
            }
            catch { } // silencioso a propósito: ante cualquier duda, layout plano
            return baseDir;
        }

        public static string StagingRoot()
            => Path.Combine(InstallDir(), "AgroParallel", "Updates");

        public static string StagingDir(string version)
            => Path.Combine(StagingRoot(), version);

        public static string StagingZip(string version)
            => Path.Combine(StagingDir(version), "payload.zip");

        // El ZIP de release pone AgroParallel.Updater.exe en la RAÍZ de la
        // instalación — con InstallDir() resolviendo la raíz (y no Engine\),
        // este path cierra solo.
        public static string UpdaterExe()
            => Path.Combine(InstallDir(),
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows)
                    ? "AgroParallel.Updater.exe" : "AgroParallel.Updater");

        // Qué relanza el Updater al terminar: la PANTALLA, no el motor headless
        // (el proceso que llama Apply es el Engine, pero relanzar solo el motor
        // dejaría la cabina sin UI). Prioridad:
        //   1. <install>\Lanzar-PilotX.bat — el launcher oficial: levanta
        //      Engine minimizado + Desktop + vigilante. El Updater lo arranca
        //      con UseShellExecute=true, que sí sabe correr un .bat.
        //   2. <install>\Desktop\PilotX.Desktop.exe (o PilotX.Desktop en
        //      Linux) — la pantalla sola, que lanza su Engine.
        //   3. El entry assembly actual — último recurso, layouts viejos.
        public static string EntryExe()
        {
            try
            {
                string install = InstallDir();
                string bat = Path.Combine(install, "Lanzar-PilotX.bat");
                if (File.Exists(bat)) return bat;

                string desktop = Path.Combine(install, "Desktop",
                    System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                        System.Runtime.InteropServices.OSPlatform.Windows)
                        ? "PilotX.Desktop.exe" : "PilotX.Desktop");
                if (File.Exists(desktop)) return desktop;
            }
            catch { } // silencioso a propósito: fallback de detección de exe propio
            try
            {
                var asm = Assembly.GetEntryAssembly();
                if (asm != null && !string.IsNullOrEmpty(asm.Location))
                    return asm.Location;
            }
            catch { } // silencioso a propósito: fallback de detección de exe propio
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
        // Pide el catálogo OTA filtrado por <product> y elige la mayor versión
        // semver. product/stagingRoot/payloadFileName tienen defaults que
        // reproducen el comportamiento Windows exacto de antes (sin cambios
        // para FormGpsPilotXUpdateService, que sigue llamando con 2 args).
        public static async Task<PilotXUpdateStatus> CheckAsync(HttpClient http, OrbitXConfig cfg,
            string product = DefaultProduct, string stagingRoot = null, string payloadFileName = "payload.zip")
        {
            Update(s => { s.Phase = PilotXUpdatePhase.Checking; s.LastError = null; });
            try
            {
                RequireAuth(cfg);

                string url = cfg.ServerUrl.TrimEnd('/') + "/api/ota/catalogo?producto=" + Uri.EscapeDataString(product);
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
                            if (!string.Equals(it.producto, product, StringComparison.OrdinalIgnoreCase)) continue;
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

                        bool newer = CompareSemver(latest.version, Snapshot().CurrentVersion) > 0;
                        // El payload staged solo cuenta si ademas es MAS NUEVO que
                        // lo instalado. Sin el "newer &&", tras aplicar un update el
                        // payload.zip queda en staging y Check marcaba ReadyToApply
                        // en falso (misma version = "listo para aplicar" eterno).
                        bool staged = newer && File.Exists(Path.Combine(stagingRoot ?? StagingRoot(), latest.version, payloadFileName));

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
        // Baja el payload de <product> desde OrbitX (device-auth) a staging y
        // verifica SHA256 contra el hash del catálogo. Defaults reproducen el
        // comportamiento Windows exacto (payload.zip bajo StagingRoot()).
        public static async Task<PilotXUpdateStatus> DownloadAsync(HttpClient http, OrbitXConfig cfg,
            string product = DefaultProduct, string stagingRoot = null, string payloadFileName = "payload.zip")
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
                string dirVer = Path.Combine(stagingRoot ?? StagingRoot(), version);
                Directory.CreateDirectory(dirVer);
                string zip = Path.Combine(dirVer, payloadFileName);
                string tmp = zip + ".part";
                if (File.Exists(tmp)) File.Delete(tmp);

                // ── Parche primero ─────────────────────────────────────────
                // Si en el catalogo hay un PilotXParche de esta misma version y
                // fue armado para la version instalada, se baja ese (~5 MB) en
                // vez del completo (~190 MB). El parche trae parche.json con su
                // version_base; si no coincide con lo instalado se descarta y
                // se sigue con el completo. AgroParallel.Updater vuelve a
                // validar el manifiesto antes de tocar nada (1.0.51+).
                if (string.Equals(product, DefaultProduct, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (await IntentarParcheAsync(http, cfg, version, zip).ConfigureAwait(false))
                        {
                            Update(s =>
                            {
                                s.Phase = PilotXUpdatePhase.ReadyToApply;
                                s.StagingReady = true;
                                s.ProgressPct = 100;
                            });
                            return Snapshot();
                        }
                    }
                    catch (Exception exParche)
                    {
                        // Cualquier problema con el parche NO frena la
                        // actualizacion: se cae al completo, que siempre sirve.
                        try { System.Diagnostics.Trace.WriteLine("[PilotXSelfUpdate] parche no aplicable: " + exParche.Message); } catch { }
                    }
                }

                string zipUrl = cfg.ServerUrl.TrimEnd('/') + "/api/ota/firmware/"
                              + Uri.EscapeDataString(product) + "/" + Uri.EscapeDataString(version);
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
                            try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { } // silencioso a propósito: best-effort leer snippet de error HTTP
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

        // ── Parche diferencial ─────────────────────────────────────────────
        private const string ParcheProduct = "PilotXParche";

        // "1.0.55+abc" / "1.0.55.0" -> "1.0.55"
        private static string VersionCorta(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            v = v.Trim();
            int i = v.IndexOfAny(new[] { '+', '-', ' ' });
            if (i > 0) v = v.Substring(0, i);
            var partes = v.Split('.');
            if (partes.Length < 3) return v;
            return partes[0] + "." + partes[1] + "." + partes[2];
        }

        /// <summary>
        /// Intenta bajar PilotXParche/<version> en lugar del completo. Devuelve
        /// true si quedo un payload.zip valido (parche para la version instalada);
        /// false si no hay parche, no coincide la base, o fallo cualquier paso.
        /// Nunca deja basura: el .part se borra siempre.
        /// </summary>
        private static async Task<bool> IntentarParcheAsync(HttpClient http, OrbitXConfig cfg, string version, string zipDestino)
        {
            string baseUrl = cfg.ServerUrl.TrimEnd('/');

            // 1) ¿Hay un parche de esta version en el catalogo?
            CatalogItem item = null;
            string catUrl = baseUrl + "/api/ota/catalogo?producto=" + Uri.EscapeDataString(ParcheProduct);
            using (var req = new HttpRequestMessage(HttpMethod.Get, catUrl))
            {
                req.Headers.Add("X-Device-ID", cfg.DeviceId);
                req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                req.Headers.Add("Cache-Control", "no-cache");
                using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return false;
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var list = JsonSerializer.Deserialize<List<CatalogItem>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CatalogItem>();
                    foreach (var it in list)
                        if (it != null && string.Equals(VersionCorta(it.version), VersionCorta(version), StringComparison.Ordinal))
                        { item = it; break; }
                }
            }
            if (item == null) return false;

            // 2) Bajarlo a un .part propio (no pisa el completo si ya existia).
            string tmp = zipDestino + ".parche.part";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                string zipUrl = baseUrl + "/api/ota/firmware/" + Uri.EscapeDataString(ParcheProduct) + "/" + Uri.EscapeDataString(item.version);
                using (var req = new HttpRequestMessage(HttpMethod.Get, zipUrl))
                {
                    req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                    req.Headers.Add("Cache-Control", "no-cache");
                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return false;
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
                                if (total > 0) { int pct = (int)(read * 100L / total); Update(s => { s.ProgressPct = pct; }); }
                            }
                        }
                    }
                }

                // 3) SHA del catalogo.
                if (!string.IsNullOrEmpty(item.hash_sha256))
                {
                    string got = FirmwareMirror.Sha256File(tmp);
                    if (!string.Equals(got, item.hash_sha256, StringComparison.OrdinalIgnoreCase)) return false;
                }

                // 4) ¿Es para la version que tengo instalada?
                string baseEsperada = null;
                using (var za = System.IO.Compression.ZipFile.OpenRead(tmp))
                {
                    var e = za.GetEntry("parche.json");
                    if (e == null) return false;
                    using (var sr = new StreamReader(e.Open()))
                    {
                        string man = sr.ReadToEnd();
                        var m = System.Text.RegularExpressions.Regex.Match(man, "\"version_base\"\\s*:\\s*\"([^\"]*)\"");
                        if (m.Success) baseEsperada = m.Groups[1].Value;
                    }
                }
                // Sirve si la instalada esta entre la base del parche (inclusive)
                // y la version nueva (exclusive): el parche trae todo lo que
                // cambio desde la base, asi que cubre cualquier version intermedia.
                // Misma regla que AgroParallel.Updater.ValidarParche.
                string instalada = VersionCorta(Snapshot().CurrentVersion);
                if (string.IsNullOrEmpty(baseEsperada)) return false;
                bool sirve = CompareSemver(instalada, VersionCorta(baseEsperada)) >= 0
                          && CompareSemver(instalada, VersionCorta(item.version)) < 0;
                if (!sirve) return false;

                // 5) Queda como payload.zip: Apply y el Updater no distinguen.
                if (File.Exists(zipDestino)) File.Delete(zipDestino);
                File.Move(tmp, zipDestino);
                Update(s => { s.SizeBytes = item.tamano_bytes; s.Sha256 = item.hash_sha256; });
                return true;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
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
                        try { handler(); } catch { } // silencioso a propósito: evento de cierre ordenado, no interrumpir el Updater
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
