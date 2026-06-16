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
            try { System.Diagnostics.Debug.WriteLine("[OrbitX] " + line); } catch { }
            try
            {
                lock (_logLock)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "orbitx_sync.log");
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(path);
                    }
                    catch { }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { }
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

                // Subir cola.
                while (_queue.Count > 0)
                {
                    var item = _queue.Dequeue();
                    bool ok = await UploadFile(item);
                    if (ok) FilesSynced++;
                }

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
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
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
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            EnqueueIfChanged(Path.Combine(baseDir, "quantiX.json"), "quantix/quantiX.json", "quantix_config", "quantix");
            EnqueueIfChanged(Path.Combine(baseDir, "quantiX_motores.json"), "quantix/motores.json", "quantix_motores", "quantix");
        }

        private void EnqueueSectionXFiles()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            EnqueueIfChanged(Path.Combine(baseDir, "sectionX.json"), "sectionx/sectionX.json", "sectionx_config", "sectionx");
        }

        // FlowX: pulverización con bomba central y dosis por sección. Por ahora
        // sólo encolamos el config (flowX.json) — el bridge no persiste calibraciones
        // ni telemetría a disco. Cuando se sumen logs/NDJSON de caudal vs target
        // se agregan acá con el mismo patrón.
        private void EnqueueFlowXFiles()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            EnqueueIfChanged(Path.Combine(baseDir, "flowX.json"), "flowx/flowX.json", "flowx_config", "flowx");
        }

        // StormX: estación meteo móvil. Firmware aún sin MQTT publishing — el config
        // ya se persiste y conviene sincronizarlo igual para tener la URL/IP/topics
        // del nodo en la nube cuando el firmware esté.
        private void EnqueueStormXFiles()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
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

                string[] aogFiles = new[] { "boundary.txt", "field.txt", "sections.txt",
                    "headland.txt", "flags.txt", "recpath.txt", "ablines.txt" };

                foreach (var fn in aogFiles)
                {
                    string path = Path.Combine(fieldDir, fn);
                    if (File.Exists(path))
                    {
                        string subtipo = fn.Replace(".txt", "");
                        EnqueueIfChanged(path, "aog/fields/" + fieldName + "/" + fn, subtipo, "aog",
                            true, fieldName);
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
            catch { }
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
                    return; // Sin cambios — el hash es sobre bytes/texto, no se cruzan.

                _lastHashes[localPath] = hash;

                _queue.Enqueue(new SyncItem
                {
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
            catch { }
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
                    { "aog_path", AppDomain.CurrentDomain.BaseDirectory }
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
                try { body = await resp.Content.ReadAsStringAsync(); } catch { }
                if (resp.IsSuccessStatusCode)
                {
                    Trace("[HB] AUTO-REG OK " + code + " — device creado en CouchDB con master token. " +
                          "IMPORTANTE: regenerá el token desde el panel OrbitX → Dispositivos y pegalo en orbitX.json.");
                    LastHeartbeatStatus = "auto-registered (regenerate token!)";
                    LastError = null;
                    _cfg.DeviceToken = _cfg.MasterToken;
                    try { _cfg.Save(); } catch { }
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
                    { "aog_path", AppDomain.CurrentDomain.BaseDirectory }
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
                try { body = await response.Content.ReadAsStringAsync(); } catch { }
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
                    try { errBody = await response.Content.ReadAsStringAsync(); } catch { }
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

                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "prescripciones");
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
            catch { }
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
