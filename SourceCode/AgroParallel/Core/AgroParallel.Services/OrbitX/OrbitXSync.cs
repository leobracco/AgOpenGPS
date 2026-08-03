// ============================================================================
// OrbitXSync.cs — Sincronización con OrbitX Cloud (servicio portable).
//
// Portado a netstandard2.0 (Tanda 1 del refactor "mover módulos AP"):
//  · System.Windows.Forms.Timer → System.Timers.Timer
//  · Sin dependencia del proyecto GPS upstream — consume estado vía IAogStateProvider
//    (FieldsDirectory y CurrentFieldDirectory ahora vienen en el snapshot).
//  · El shim legacy `OrbitXSync(FormGPS, ...)` fue eliminado: el call site
//    de FormGPS construye explícitamente un FormGpsStateProvider.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.OrbitX
{
    public class OrbitXSync : IDisposable
    {
        private readonly OrbitXConfig _cfg;
        private readonly IAogStateProvider _state;
        private readonly HttpClient _http;
        private System.Timers.Timer _timer;
        private System.Timers.Timer _firmwareTimer;
        private FirmwareLanServer _firmwareServer;
        private bool _firmwareSyncInFlight;
        private bool _syncInFlight;
        private bool _disposed;
        private readonly Queue<SyncItem> _queue = new Queue<SyncItem>();
        // Tope de reintentos antes de descartar un ítem que el server rechaza
        // siempre (4xx permanente) — sin esto bloqueaba la cola entera (head-of-line).
        private const int MaxIntentosPorItem = 5;

        public bool IsRunning { get; private set; }
        public int FilesSynced { get; private set; }
        public DateTime? LastSyncTime { get; private set; }
        public string LastError { get; private set; }
        public DateTime? LastFirmwareSync { get; private set; }
        public string LastFirmwareError { get; private set; }
        public string FirmwareServerPrefix => _firmwareServer?.BoundPrefix;

        private class SyncItem
        {
            public string RutaRel;
            public string Nombre;
            public string Subtipo;
            public string Producto;
            // Para archivos de texto (JSON/TXT/NDJSON/PRJ): Contenido tiene el
            // string UTF-8 y ContenidoBinario es null.
            // Para archivos binarios (SHP/SHX/DBF): Contenido es null y
            // ContenidoBinario tiene los bytes — se envían Base64 al cloud.
            public string Contenido;
            public byte[] ContenidoBinario;
            public bool EsBinario;
            public string HashMd5;
            public int TamanoBytes;
            public bool EsLote;
            public string LoteNombre;
            // Ruta local: hace falta para marcar el archivo como subido RECIÉN
            // cuando el server confirmó. Ver EnqueueIfChanged.
            public string LocalPath;
            // Reintentos consecutivos fallidos. Un archivo que el server rechaza
            // SIEMPRE (4xx permanente) bloqueaba la cola entera para siempre —
            // ver SyncTick: a los 5 intentos se descarta (sin anotar hash, así
            // que si el archivo cambia se re-encola solo).
            public int Intentos;
        }

        // Extensiones que requieren transporte binario (Base64). El resto se
        // sigue subiendo como string UTF-8 en el campo "contenido" — incluye
        // .prj (text WGS84) y .json/.txt/.ndjson/.xml/.kml.
        private static readonly HashSet<string> _binaryExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".shp", ".shx", ".dbf"
        };

        private static bool IsBinaryPath(string path)
        {
            return _binaryExts.Contains(Path.GetExtension(path));
        }

        public OrbitXSync(IAogStateProvider state, OrbitXConfig cfg)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _cfg = cfg ?? OrbitXConfig.Load();
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        public void Start()
        {
            // El mirror LAN de firmwares es INDEPENDIENTE del sync con OrbitX
            // cloud — el técnico de campo puede flashear sin internet siempre
            // que haya subido el .bin al cache local (POST /api/firmwares/upload).
            // Antes esto colgaba del early-return de _cfg.Enabled y dejaba el
            // server :8088 sin levantar.
            StartFirmwareMirror();

            if (IsRunning || !_cfg.Enabled)
            {
                Trace("Start abortado — IsRunning=" + IsRunning + " Enabled=" + _cfg.Enabled);
                return;
            }
            if (string.IsNullOrEmpty(_cfg.DeviceToken))
            {
                LastError = "Sin token configurado";
                Trace("Start abortado — DeviceToken vacío");
                return;
            }

            Trace("Start url=" + _cfg.ServerUrl + " device_id=" + _cfg.DeviceId
                + " interval=" + _cfg.SyncIntervalSec + "s estab=" + _cfg.EstabSlug);

            _timer = new System.Timers.Timer
            {
                Interval = _cfg.SyncIntervalSec * 1000d,
                AutoReset = true,
            };
            _timer.Elapsed += async (s, e) => await SyncTick();
            _timer.Start();

            IsRunning = true;

            // Heartbeat inicial.
            _ = SendHeartbeat();

            // Firmware mirror + LAN HTTP server (OTA para nodos ESP32).
            StartFirmwareMirror();
        }

        private void StartFirmwareMirror()
        {
            if (!_cfg.FirmwareMirrorEnabled) return;
            if (_firmwareServer != null) return; // ya arrancado (Start() llamado dos veces)

            _firmwareServer = new FirmwareLanServer(_cfg, msg => Trace(msg));
            _firmwareServer.Start();

            int minutes = _cfg.FirmwareSyncIntervalMin > 0 ? _cfg.FirmwareSyncIntervalMin : 10;
            _firmwareTimer = new System.Timers.Timer
            {
                Interval = minutes * 60 * 1000d,
                AutoReset = true,
            };
            _firmwareTimer.Elapsed += async (s, e) => await FirmwareSyncTick();
            _firmwareTimer.Start();

            // Primer sync diferido 5s (después de que arranque heartbeat).
            var first = new System.Timers.Timer { Interval = 5000, AutoReset = false };
            first.Elapsed += async (s, e) =>
            {
                first.Stop(); first.Dispose();
                await FirmwareSyncTick();
            };
            first.Start();
        }

        private async Task FirmwareSyncTick()
        {
            if (_disposed || !_cfg.FirmwareMirrorEnabled) return;
            if (_firmwareSyncInFlight) return;
            if (string.IsNullOrEmpty(_cfg.DeviceToken)) return;

            _firmwareSyncInFlight = true;
            try
            {
                var r = await FirmwareMirror.SyncAsync(_http, _cfg, msg => Trace(msg));
                LastFirmwareSync = DateTime.Now;
                LastFirmwareError = null;
                if (r.Descargados > 0 || r.Errores > 0)
                    Trace($"FW mirror: {r.CatalogCount} en catálogo, {r.Descargados} bajados, {r.Errores} errores");
            }
            catch (Exception ex)
            {
                LastFirmwareError = ex.Message;
                Trace($"FW mirror falló: {ex.Message}");
            }
            finally
            {
                _firmwareSyncInFlight = false;
            }
        }

        // Hook simple de log; escribe a Debug y a orbitx_sync.log al lado del exe
        // para que el usuario pueda diagnosticar por qué el tractor figura offline.
        private static readonly object _logLock = new object();
        private static void Trace(string msg)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg;
            try { System.Diagnostics.Trace.WriteLine("[OrbitX] " + line); } catch { } // silencioso a propósito: fallback del propio logger
            try
            {
                lock (_logLock)
                {
                    string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, "orbitx_sync.log");
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(path);
                    }
                    catch { } // silencioso a propósito: limpieza best-effort del archivo de log propio
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { } // silencioso a propósito: fallback de I/O del propio log de OrbitXSync
        }

        // Estado del último heartbeat — visible para diagnóstico desde la UI.
        public string LastHeartbeatStatus { get; private set; }
        public DateTime? LastHeartbeatTime { get; private set; }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_firmwareTimer != null) { _firmwareTimer.Stop(); _firmwareTimer.Dispose(); _firmwareTimer = null; }
            if (_firmwareServer != null) { _firmwareServer.Stop(); _firmwareServer = null; }
        }

        // =====================================================================
        // Sync tick — detecta cambios y sube
        // =====================================================================

        private async Task SyncTick()
        {
            if (_disposed || !_cfg.Enabled) return;
            // El timer ahora corre en thread-pool; evitamos solapamiento si un
            // tick previo todavía está esperando el server.
            if (_syncInFlight) return;
            _syncInFlight = true;
            try
            {
                // Heartbeat periódico — el panel marca el device offline si
                // pasaron > 2 min sin ver el ultimo_visto.
                await SendHeartbeat();

                // Encolar archivos de módulos.
                if (_cfg.SyncVistaX) EnqueueVistaXFiles();
                if (_cfg.SyncQuantiX) EnqueueQuantiXFiles();
                if (_cfg.SyncSectionX) EnqueueSectionXFiles();
                if (_cfg.SyncFlowX) EnqueueFlowXFiles();
                if (_cfg.SyncStormX) EnqueueStormXFiles();
                if (_cfg.SyncAOG) EnqueueAOGFiles();

                // Subir cola. Un archivo se da por sincronizado SOLO cuando el
                // server contesta OK: recién ahí anotamos el hash. Si falla
                // (sin WiFi en el lote), lo dejamos en la cola y cortamos el
                // ciclo — sin red, insistir con los demás solo suma timeouts.
                // El próximo tick reintenta, y si el proceso se reinició, el
                // hash sin anotar hace que se vuelva a encolar solo.
                //
                // Excepción: si el MISMO ítem ya falló MaxIntentosPorItem veces
                // seguidas (rechazo permanente del server, ej. 4xx por archivo
                // corrupto), cortar acá dejaría la cola entera bloqueada para
                // siempre detrás de él (head-of-line). Lo descartamos SIN anotar
                // el hash — si el archivo cambia más adelante, EnqueueIfChanged
                // lo vuelve a encolar solo — y seguimos con el resto.
                int subidos = 0;
                while (_queue.Count > 0)
                {
                    var item = _queue.Peek();
                    bool ok = await UploadFile(item);
                    if (!ok)
                    {
                        item.Intentos++;
                        if (item.Intentos >= MaxIntentosPorItem)
                        {
                            _queue.Dequeue();
                            Trace(string.Format("[AOG] DESCARTADO tras {0} intentos: {1} ({2} bytes): {3}",
                                item.Intentos, item.Nombre, item.TamanoBytes, LastError ?? "sin respuesta"));
                            continue;
                        }
                        Trace(string.Format("[AOG] PENDIENTE {0} ({1} bytes) — queda en cola ({2}) intento {3}/{4}: {5}",
                            item.Nombre, item.TamanoBytes, _queue.Count, item.Intentos, MaxIntentosPorItem, LastError ?? "sin respuesta"));
                        break;
                    }
                    _queue.Dequeue();
                    if (!string.IsNullOrEmpty(item.LocalPath))
                        _lastHashes[item.LocalPath] = item.HashMd5;
                    FilesSynced++;
                    subidos++;
                    Trace(string.Format("[AOG] OK {0} · {1} · {2} bytes{3}",
                        item.Nombre, item.Subtipo, item.TamanoBytes,
                        item.EsLote ? " · lote " + item.LoteNombre : ""));
                }
                if (subidos > 0)
                    Trace(string.Format("[AOG] {0} archivo(s) subidos · {1} en cola", subidos, _queue.Count));

                // Enviar posición del tractor (tracking).
                await SendTracking();

                // Descargar prescripciones pendientes.
                await CheckPrescriptions();

                LastSyncTime = DateTime.Now;
                LastError = null;

                _cfg.LastSync = LastSyncTime.Value.ToString("o", CultureInfo.InvariantCulture);
                _cfg.FilesSynced = FilesSynced;
                OrbitXConfig.SaveRuntimeFields(_cfg.LastSync, _cfg.FilesSynced, null);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                _syncInFlight = false;
            }
        }

        // =====================================================================
        // Encolar archivos por módulo
        // =====================================================================

        private void EnqueueVistaXFiles()
        {
            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;
            EnqueueIfChanged(Path.Combine(baseDir, "vistaX.json"), "vistax/vistaX.json", "vistax_config", "vistax");

            string implDir = Path.Combine(baseDir, "data", "implementos");
            if (Directory.Exists(implDir))
            {
                foreach (var f in Directory.GetFiles(implDir, "*.json"))
                    EnqueueIfChanged(f, "vistax/implementos/" + Path.GetFileName(f), "vistax_implemento", "vistax");
            }
        }

        private void EnqueueQuantiXFiles()
        {
            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;
            EnqueueIfChanged(Path.Combine(baseDir, "quantiX.json"), "quantix/quantiX.json", "quantix_config", "quantix");
            EnqueueIfChanged(Path.Combine(baseDir, "quantiX_motores.json"), "quantix/motores.json", "quantix_motores", "quantix");
        }

        private void EnqueueSectionXFiles()
        {
            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;
            EnqueueIfChanged(Path.Combine(baseDir, "sectionX.json"), "sectionx/sectionX.json", "sectionx_config", "sectionx");
        }

        // FlowX: pulverización con bomba central y dosis por sección. Por ahora
        // sólo encolamos el config (flowX.json) — el bridge no persiste calibraciones
        // ni telemetría a disco. Cuando se sumen logs/NDJSON de caudal vs target
        // se agregan acá con el mismo patrón.
        private void EnqueueFlowXFiles()
        {
            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;
            EnqueueIfChanged(Path.Combine(baseDir, "flowX.json"), "flowx/flowX.json", "flowx_config", "flowx");
        }

        // StormX: estación meteo móvil. Firmware aún sin MQTT publishing — el config
        // ya se persiste y conviene sincronizarlo igual para tener la URL/IP/topics
        // del nodo en la nube cuando el firmware esté.
        private void EnqueueStormXFiles()
        {
            string baseDir = AgroParallel.Common.AgpPaths.ConfigRoot;
            EnqueueIfChanged(Path.Combine(baseDir, "stormX.json"), "stormx/stormX.json", "stormx_config", "stormx");
        }

        private void EnqueueAOGFiles()
        {
            // Sincronizar el campo actual si hay uno abierto.
            try
            {
                var snap = _state.GetSnapshot();
                string fieldName = snap.CurrentFieldDirectory;
                if (string.IsNullOrEmpty(fieldName)) return;
                string fieldsRoot = snap.FieldsDirectory;
                if (string.IsNullOrEmpty(fieldsRoot)) return;
                string fieldDir = Path.Combine(fieldsRoot, fieldName);
                if (!Directory.Exists(fieldDir)) return;

                // "ablines.txt" es el nombre viejo; PilotX guarda las guías en
                // TrackLines.txt y así ninguna línea de guiado llegaba al cloud.
                // Contour.txt entra también: es trabajo del operario igual que
                // el resto (Windows no distingue mayúsculas en File.Exists).
                string[] aogFiles = new[] { "boundary.txt", "field.txt", "sections.txt",
                    "headland.txt", "flags.txt", "recpath.txt", "ablines.txt",
                    "tracklines.txt", "contour.txt" };

                foreach (var fn in aogFiles)
                {
                    string path = Path.Combine(fieldDir, fn);
                    if (File.Exists(path))
                    {
                        EnqueueIfChanged(path, "aog/fields/" + fieldName + "/" + fn,
                            SubtipoCloud(fn), "aog", true, fieldName);
                    }
                }

                // VistaX logs del campo: NDJSON (raw) + bundles SHP (puntos por
                // surco y heatmap) + PRJ (WGS84) — los binarios se transportan
                // Base64 (ver EnqueueIfChanged → IsBinaryPath).
                string vxDir = Path.Combine(fieldDir, "VistaX");
                if (Directory.Exists(vxDir))
                {
                    string[] vxPatterns = { "*.ndjson", "*.shp", "*.shx", "*.dbf", "*.prj" };
                    foreach (var pat in vxPatterns)
                    {
                        foreach (var f in Directory.GetFiles(vxDir, pat))
                        {
                            // Subtipo más fino para que el cloud pueda agrupar:
                            //   vistax_log (NDJSON) · vistax_shp (bundle puntos/heatmap)
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            string subt = ext == ".ndjson" ? "vistax_log" : "vistax_shp";
                            EnqueueIfChanged(f, "vistax/logs/" + fieldName + "/" + Path.GetFileName(f),
                                subt, "vistax", true, fieldName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AgpLog.Error("OrbitXSync", "encolar archivos de lote AOG", ex);
            }
        }

        // Nombre de archivo → `subtipo` que espera el cloud.
        //
        // OrbitX clasifica cada archivo del lote por este campo (routes/aog.js:
        // GET /lotes/:nombre y /lotes-mapa) y el parser arma los polígonos de
        // cobertura solo si encuentra `sections_coverage` MÁS el `field_origin`
        // (necesita el origen para pasar de metros a lat/lon). Acá se mandaba el
        // nombre del archivo pelado ("sections", "field"), que no coincide con
        // ninguno: el lote aparecía en el panel pero sin las pasadas, y todo
        // caía en el cajón "otros". Lo mismo con las guías (`track_lines`).
        private static string SubtipoCloud(string fileName)
        {
            switch (fileName.ToLowerInvariant())
            {
                case "field.txt":      return "field_origin";
                case "sections.txt":   return "sections_coverage";
                case "tracklines.txt": return "track_lines";
                case "ablines.txt":    return "ab_line";
                case "boundary.txt":   return "boundary";
                case "headland.txt":   return "headland";
                case "contour.txt":    return "contour";
                default:               return fileName.Replace(".txt", "");
            }
        }

        // Hash tracking para no subir archivos sin cambios.
        private readonly Dictionary<string, string> _lastHashes = new Dictionary<string, string>();

        private void EnqueueIfChanged(string localPath, string rutaRel, string subtipo, string producto,
            bool esLote = false, string loteNombre = "")
        {
            if (!File.Exists(localPath)) return;
            try
            {
                bool isBinary = IsBinaryPath(localPath);
                string contenidoTexto = null;
                byte[] contenidoBin = null;
                string hash;
                int sizeBytes;

                if (isBinary)
                {
                    contenidoBin = File.ReadAllBytes(localPath);
                    hash = ComputeMd5Bytes(contenidoBin);
                    sizeBytes = contenidoBin.Length;
                }
                else
                {
                    contenidoTexto = File.ReadAllText(localPath);
                    hash = ComputeMd5(contenidoTexto);
                    sizeBytes = Encoding.UTF8.GetByteCount(contenidoTexto);
                }

                string prev;
                if (_lastHashes.TryGetValue(localPath, out prev) && prev == hash)
                    return; // Ya CONFIRMADO por el server — el hash es sobre bytes/texto.

                // El hash NO se anota acá: se anota cuando el server confirma
                // (ver SyncTick). Anotarlo al encolar hacía que un archivo que
                // fallaba al subir —sin WiFi en el lote, típico— quedara marcado
                // como sincronizado y no se reintentara nunca más. El lindero y
                // la cabecera se escriben UNA vez: si ese intento caía, esa
                // versión no llegaba nunca al cloud.
                foreach (var enCola in _queue)
                {
                    if (enCola.LocalPath == localPath && enCola.HashMd5 == hash)
                        return; // ya está esperando su turno
                }

                _queue.Enqueue(new SyncItem
                {
                    LocalPath = localPath,
                    RutaRel = rutaRel,
                    Nombre = Path.GetFileName(localPath),
                    Subtipo = subtipo,
                    Producto = producto,
                    Contenido = contenidoTexto,
                    ContenidoBinario = contenidoBin,
                    EsBinario = isBinary,
                    HashMd5 = hash,
                    TamanoBytes = sizeBytes,
                    EsLote = esLote,
                    LoteNombre = loteNombre
                });
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "encolar archivo si cambió", ex);
            }
        }

        // =====================================================================
        // Upload
        // =====================================================================

        private async Task<bool> UploadFile(SyncItem item)
        {
            try
            {
                string url = _cfg.ServerUrl.TrimEnd('/') + "/api/aog/sync";

                var payload = new Dictionary<string, object>
                {
                    { "ruta_rel", item.RutaRel },
                    { "nombre", item.Nombre },
                    { "subtipo", item.Subtipo },
                    { "producto", item.Producto },
                    { "hash_md5", item.HashMd5 },
                    { "tamano", item.TamanoBytes },
                    { "ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    { "device_id", _cfg.DeviceId },
                    { "es_lote", item.EsLote },
                    { "lote_nombre", item.LoteNombre }
                };

                // Texto → "contenido" (string UTF-8). Binario → "contenido_base64".
                // El server lee uno u otro según presencia; ver routes/aog.js.
                if (item.EsBinario)
                    payload["contenido_base64"] = Convert.ToBase64String(item.ContenidoBinario);
                else
                    payload["contenido"] = item.Contenido ?? string.Empty;

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = content;
                request.Headers.Add("X-Device-ID", _cfg.DeviceId);
                request.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                    request.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);

                var response = await _http.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        // Intento de auto-registro con master token. Solo se gatilla cuando
        // SendHeartbeat recibe 401 "Dispositivo no registrado".
        private async Task TryAutoRegister(string url)
        {
            try
            {
                Trace("[HB] AUTO-REG intentando registrar device con master token...");
                var payload = new Dictionary<string, object>
                {
                    { "device_id", _cfg.DeviceId },
                    { "hostname", Environment.MachineName },
                    { "platform", "win32" },
                    { "version", "AgOpenGPS-AP" },
                    { "aog_path", AppDomain.CurrentDomain.BaseDirectory },
                    // ID de RustDesk (soporte remoto): si está instalado se lee
                    // una vez y viaja en el payload; el CRM lo muestra en la
                    // ficha del cliente. Sin RustDesk va null — nada que instalar.
                    { "rustdesk_id", LeerRustDeskId() }
                };
                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = content;
                req.Headers.Add("X-Device-ID", _cfg.DeviceId ?? "");
                req.Headers.Add("X-Auth-Token", _cfg.MasterToken);

                var resp = await _http.SendAsync(req);
                int code = (int)resp.StatusCode;
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(); } catch { } // silencioso a propósito: best-effort leer snippet de error HTTP
                if (resp.IsSuccessStatusCode)
                {
                    Trace("[HB] AUTO-REG OK " + code + " — device creado en CouchDB con master token. " +
                          "IMPORTANTE: regenerá el token desde el panel OrbitX → Dispositivos y pegalo en orbitX.json.");
                    LastHeartbeatStatus = "auto-registered (regenerate token!)";
                    LastError = null;
                    _cfg.DeviceToken = _cfg.MasterToken;
                    try { _cfg.Save(); } catch (Exception ex) { AgpLog.Warn("OrbitXSync", "guardar config tras auto-registro", ex); }
                }
                else
                {
                    Trace("[HB] AUTO-REG FAIL " + code + " body="
                        + (body.Length > 200 ? body.Substring(0, 200) + "…" : body)
                        + " — verificá que MasterToken en orbitX.json coincida con DEVICE_MASTER_TOKEN del server.");
                    LastError = "auto-reg " + code + ": " + body;
                }
            }
            catch (Exception ex)
            {
                Trace("[HB] AUTO-REG EX " + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ── RustDesk (soporte remoto) ────────────────────────────────────────
        // Lee el ID de RustDesk UNA vez por sesión ejecutando el cliente con
        // --get-id (2 s de timeout, best-effort). Si RustDesk no está
        // instalado o falla, devuelve null y el heartbeat lo reporta así:
        // el CRM muestra "sin RustDesk" en vez de romper nada.
        private static string _rustdeskId;
        private static bool _rustdeskLeido;

        private static string LeerRustDeskId()
        {
            if (_rustdeskLeido) return _rustdeskId;
            _rustdeskLeido = true;
            try
            {
                string exe = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "RustDesk", "rustdesk.exe");
                if (!System.IO.File.Exists(exe)) return null;

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--get-id",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return null;
                    string salida = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return null; }
                    salida = (salida ?? "").Trim();
                    // El ID es numérico (9-10 dígitos); cualquier otra cosa es
                    // un error del cliente y no sirve para conectarse.
                    if (salida.Length >= 6 && salida.Length <= 16 && long.TryParse(salida, out _))
                        _rustdeskId = salida;
                }
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "leyendo ID de RustDesk", ex);
            }
            return _rustdeskId;
        }

        private async Task SendHeartbeat()
        {
            string url = (_cfg.ServerUrl ?? "").TrimEnd('/') + "/api/devices/heartbeat";
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "device_id", _cfg.DeviceId },
                    { "hostname", Environment.MachineName },
                    { "platform", "win32" },
                    { "version", "AgOpenGPS-AP" },
                    { "aog_path", AppDomain.CurrentDomain.BaseDirectory },
                    // ID de RustDesk (soporte remoto): si está instalado se lee
                    // una vez y viaja en el payload; el CRM lo muestra en la
                    // ficha del cliente. Sin RustDesk va null — nada que instalar.
                    { "rustdesk_id", LeerRustDeskId() }
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = content;
                request.Headers.Add("X-Device-ID", _cfg.DeviceId ?? "");
                request.Headers.Add("X-Auth-Token", _cfg.DeviceToken ?? "");

                string tokenPreview = string.IsNullOrEmpty(_cfg.DeviceToken)
                    ? "(vacío)"
                    : (_cfg.DeviceToken.Length <= 8
                        ? _cfg.DeviceToken
                        : _cfg.DeviceToken.Substring(0, 4) + "…" + _cfg.DeviceToken.Substring(_cfg.DeviceToken.Length - 4));
                Trace("[HB] POST " + url + " device_id=" + _cfg.DeviceId + " token=" + tokenPreview);

                var response = await _http.SendAsync(request);
                int code = (int)response.StatusCode;
                string body = "";
                try { body = await response.Content.ReadAsStringAsync(); } catch { } // silencioso a propósito: best-effort leer snippet de error HTTP
                string snippet = body == null ? "" : (body.Length > 300 ? body.Substring(0, 300) + "…" : body);

                LastHeartbeatTime = DateTime.Now;
                LastHeartbeatStatus = code + " " + response.ReasonPhrase;

                if (response.IsSuccessStatusCode)
                {
                    Trace("[HB] OK " + code + " body=" + snippet);
                    if (body != null && body.Contains("estab_slug"))
                    {
                        int idx = body.IndexOf("\"estab_slug\":\"");
                        if (idx > 0)
                        {
                            idx += 14;
                            int end = body.IndexOf('"', idx);
                            if (end > idx)
                            {
                                _cfg.EstabSlug = body.Substring(idx, end - idx);
                                OrbitXConfig.SaveRuntimeFields(null, FilesSynced, _cfg.EstabSlug);
                            }
                        }
                    }
                }
                else
                {
                    Trace("[HB] FAIL " + code + " " + response.ReasonPhrase + " body=" + snippet);
                    LastError = "HB " + code + ": " + snippet;

                    bool noRegistrado = code == 401 && body != null
                        && body.IndexOf("no registrado", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool tieneMaster = !string.IsNullOrEmpty(_cfg.MasterToken);
                    bool yaEsMaster = !string.IsNullOrEmpty(_cfg.DeviceToken)
                        && _cfg.DeviceToken == _cfg.MasterToken;
                    if (noRegistrado && tieneMaster && !yaEsMaster)
                    {
                        await TryAutoRegister(url);
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                LastHeartbeatStatus = "timeout";
                LastError = "HB timeout: " + ex.Message;
                Trace("[HB] TIMEOUT url=" + url + " msg=" + ex.Message);
            }
            catch (HttpRequestException ex)
            {
                LastHeartbeatStatus = "http-error";
                LastError = "HB http: " + ex.Message;
                Trace("[HB] HTTP_ERR url=" + url + " msg=" + ex.Message
                    + (ex.InnerException != null ? " inner=" + ex.InnerException.Message : ""));
            }
            catch (Exception ex)
            {
                LastHeartbeatStatus = "exception";
                LastError = "HB ex: " + ex.Message;
                Trace("[HB] EX url=" + url + " type=" + ex.GetType().Name + " msg=" + ex.Message
                    + (ex.InnerException != null ? " inner=" + ex.InnerException.Message : ""));
            }
        }

        private async Task CheckPrescriptions()
        {
            // Antes este método tragaba silenciosamente cualquier error con
            // `catch { }`, así que cuando "no aparecían prescripciones" no había
            // forma de saber si era 401 (token), 404 (ruta), array vacío o
            // problema al escribir el archivo. Ahora cada paso loggea a
            // orbitx_sync.log para diagnóstico.
            string url = _cfg.ServerUrl.TrimEnd('/') + "/api/prescripciones/pendientes";
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Device-ID", _cfg.DeviceId);
                request.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                    request.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);

                var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    string errBody = "";
                    try { errBody = await response.Content.ReadAsStringAsync(); } catch { } // silencioso a propósito: best-effort leer snippet de error HTTP
                    Trace("[PRESC] LIST HTTP " + (int)response.StatusCode + " url=" + url
                        + (string.IsNullOrEmpty(errBody) ? "" : " body=" + Truncate(errBody, 200)));
                    return;
                }

                string body = await response.Content.ReadAsStringAsync();
                var items = JsonSerializer.Deserialize<JsonElement>(body);
                if (items.ValueKind != JsonValueKind.Array)
                {
                    Trace("[PRESC] LIST respuesta no-array: " + Truncate(body, 200));
                    return;
                }

                int total = 0, descargadas = 0, errores = 0;
                foreach (var item in items.EnumerateArray())
                {
                    total++;
                    string id = item.GetProperty("id").GetString();
                    string nombre = item.TryGetProperty("nombre", out var n) ? n.GetString() : "prescripcion";

                    string dlUrl = _cfg.ServerUrl.TrimEnd('/') + "/api/prescripciones/pendientes/" + id + "/contenido";
                    var dlReq = new HttpRequestMessage(HttpMethod.Get, dlUrl);
                    dlReq.Headers.Add("X-Device-ID", _cfg.DeviceId);
                    dlReq.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                    if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                        dlReq.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);

                    var dlResp = await _http.SendAsync(dlReq);
                    if (!dlResp.IsSuccessStatusCode)
                    {
                        errores++;
                        Trace("[PRESC] DL HTTP " + (int)dlResp.StatusCode + " id=" + id + " nombre=" + nombre);
                        continue;
                    }

                    string dlBody = await dlResp.Content.ReadAsStringAsync();
                    var dlData = JsonSerializer.Deserialize<JsonElement>(dlBody);

                    string contenido = dlData.TryGetProperty("contenido", out var c) ? c.GetString() : "";
                    if (string.IsNullOrEmpty(contenido))
                    {
                        errores++;
                        Trace("[PRESC] DL contenido vacío id=" + id + " nombre=" + nombre);
                        continue;
                    }

                    // El endpoint de pendientes entrega TODOS los
                    // aog_descarga_pendiente del device, no solo prescripciones:
                    // los LOTES creados en OrbitX (dibujar contorno + "enviar a
                    // PilotX") llegan por acá como Fields/<lote>/Field.txt,
                    // Boundary.txt y boundary.kml. Antes todo se guardaba como
                    // prescripción (.geojson en data/prescripciones) y encima se
                    // marcaba entregado: el lote del cloud se PERDÍA en una
                    // carpeta equivocada.
                    string rutaRel = item.TryGetProperty("ruta_rel", out var rr) ? (rr.GetString() ?? "") : "";
                    if (rutaRel.StartsWith("Fields/", StringComparison.OrdinalIgnoreCase) ||
                        rutaRel.StartsWith("Fields\\", StringComparison.OrdinalIgnoreCase))
                    {
                        if (GuardarArchivoDeLote(rutaRel, contenido)) descargadas++;
                        else errores++;
                        continue;
                    }

                    string dir = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, "data", "prescripciones");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    string filePath = Path.Combine(dir, nombre + ".geojson");
                    File.WriteAllText(filePath, contenido);
                    FilesSynced++;
                    descargadas++;
                    Trace("[PRESC] OK id=" + id + " → " + filePath + " (" + contenido.Length + " bytes)");
                }

                if (total == 0)
                    Trace("[PRESC] LIST OK · sin prescripciones pendientes (el server no tiene ninguna con entregado=false para este device)");
                else
                    Trace("[PRESC] LIST OK · " + total + " pendientes · " + descargadas + " descargadas · " + errores + " errores");
            }
            catch (Exception ex)
            {
                Trace("[PRESC] EX url=" + url + " type=" + ex.GetType().Name + " msg=" + ex.Message
                    + (ex.InnerException != null ? " inner=" + ex.InnerException.Message : ""));
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>
        /// Archivo de LOTE bajado del cloud (Fields/&lt;lote&gt;/Field.txt,
        /// Boundary.txt, boundary.kml — los genera "crear lote" en OrbitX).
        /// Se escribe directo en el directorio de lotes del tractor: el lote
        /// queda listo para abrir desde la pantalla Lote. Si ese lote está
        /// ABIERTO ahora, se saltea con aviso — el cierre del lote pisa el
        /// Boundary con lo que tiene en memoria y el archivo bajado se
        /// perdería en silencio.
        /// </summary>
        /// <summary>Importador de lote desde KML que inyecta el HOST (el motor
        /// implementa crear/actualizar el lote SIN abrirlo, con sus writers).
        /// (nombreLote, contenidoKml) → ok. Sin esto los lotes del cloud solo
        /// dejan el .kml crudo en el directorio del lote.</summary>
        public Func<string, string, bool> ImportarLoteDesdeKml;

        private bool GuardarArchivoDeLote(string rutaRel, string contenido)
        {
            try
            {
                var snap = _state.GetSnapshot();
                string fieldsDir = snap?.FieldsDirectory;
                if (string.IsNullOrEmpty(fieldsDir))
                {
                    Trace("[LOTE] sin fields_directory en el state — no sé dónde guardar " + rutaRel);
                    return false;
                }

                // Sanitizar: nada de ".." ni rutas absolutas dentro de ruta_rel.
                string rel = rutaRel.Replace('\\', '/');
                if (rel.Contains("..") || Path.IsPathRooted(rel))
                {
                    Trace("[LOTE] ruta_rel sospechosa, descartada: " + rutaRel);
                    return false;
                }
                // Sacar el prefijo "Fields/": el resto es <lote>/<archivo>.
                rel = rel.Substring("Fields/".Length);

                string[] partes = rel.Split('/');
                if (partes.Length < 2)
                {
                    Trace("[LOTE] ruta_rel sin lote/archivo: " + rutaRel);
                    return false;
                }
                string lote = partes[0];
                string archivo = partes[partes.Length - 1];

                if (!string.IsNullOrEmpty(snap.CurrentFieldDirectory) &&
                    string.Equals(snap.CurrentFieldDirectory, lote, StringComparison.OrdinalIgnoreCase))
                {
                    Trace("[LOTE] '" + lote + "' está ABIERTO en el tractor: no piso sus archivos. " +
                          "Cerralo y mandalo de nuevo desde OrbitX.");
                    return false;
                }

                // El boundary.kml es SOBERANO: el motor reconstruye Field.txt y
                // Boundary.txt con sus propios writers (los del server tienen
                // otro formato — lat/lon crudos y sin línea de fecha — y los
                // readers del motor no los digieren: el lote "no abría").
                if (archivo.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                {
                    var importar = ImportarLoteDesdeKml;
                    if (importar != null)
                    {
                        bool ok = importar(lote, contenido);
                        Trace(ok
                            ? "[LOTE] '" + lote + "' importado desde el KML del cloud (lindero listo)"
                            : "[LOTE] no se pudo importar '" + lote + "' desde el KML");
                        if (!ok) return false;
                    }
                    // El .kml crudo se guarda igual, como referencia/backup.
                    string destinoKml = Path.Combine(fieldsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destinoKml));
                    File.WriteAllText(destinoKml, contenido);
                    FilesSynced++;
                    return true;
                }

                // Field.txt / Boundary.txt del server: formato incompatible con
                // los readers del motor — NO se escriben (el import del KML los
                // genera bien). Se acepta el pendiente para que no re-encole.
                Trace("[LOTE] '" + archivo + "' del server ignorado (el KML manda; formato server-side no compatible)");
                return true;
            }
            catch (Exception ex)
            {
                Trace("[LOTE] EX " + rutaRel + ": " + ex.Message);
                return false;
            }
        }

        private async Task SendTracking()
        {
            try
            {
                var snap = _state.GetSnapshot();
                double lat = snap.Latitude;
                double lon = snap.Longitude;
                double speed = snap.AvgSpeed;
                double heading = snap.Heading;
                string field = snap.CurrentFieldDirectory ?? "";

                if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) return;

                string url = _cfg.ServerUrl.TrimEnd('/') + "/api/tracking/position";
                var payload = new Dictionary<string, object>
                {
                    { "lat", lat }, { "lon", lon },
                    { "heading", heading }, { "speed", speed },
                    { "field", field },
                    { "modules", new Dictionary<string, bool>
                        {
                            { "vistax", _cfg.SyncVistaX },
                            { "quantix", _cfg.SyncQuantiX },
                            { "sectionx", _cfg.SyncSectionX },
                            { "flowx", _cfg.SyncFlowX },
                            { "stormx", _cfg.SyncStormX }
                        }
                    }
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = content;
                request.Headers.Add("X-Device-ID", _cfg.DeviceId);
                request.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                    request.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);

                await _http.SendAsync(request);
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "enviar posición GPS al cloud", ex);
            }
        }

        private static string ComputeMd5(string input)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ComputeMd5Bytes(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _http?.Dispose();
        }
    }
}
