// ============================================================================
// OrbitXSync.cs - Sincronización con OrbitX Cloud desde AgOpenGPS
// Sube archivos de campo, config de módulos, y datos de monitoreo.
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
using System.Windows.Forms;

namespace AgroParallel.OrbitX
{
    public class OrbitXSync : IDisposable
    {
        private readonly OrbitXConfig _cfg;
        private readonly AgOpenGPS.FormGPS _parent;
        private readonly HttpClient _http;
        private System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _firmwareTimer;
        private FirmwareLanServer _firmwareServer;
        private bool _firmwareSyncInFlight;
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
            public string Contenido;
            public bool EsLote;
            public string LoteNombre;
        }

        public OrbitXSync(AgOpenGPS.FormGPS parent, OrbitXConfig cfg)
        {
            _parent = parent;
            _cfg = cfg ?? OrbitXConfig.Load();
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        public void Start()
        {
            if (IsRunning || !_cfg.Enabled) return;
            if (string.IsNullOrEmpty(_cfg.DeviceToken))
            {
                LastError = "Sin token configurado";
                return;
            }

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _cfg.SyncIntervalSec * 1000;
            _timer.Tick += async (s, e) => await SyncTick();
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

            _firmwareServer = new FirmwareLanServer(_cfg, msg => Trace(msg));
            _firmwareServer.Start();

            int minutes = _cfg.FirmwareSyncIntervalMin > 0 ? _cfg.FirmwareSyncIntervalMin : 10;
            _firmwareTimer = new System.Windows.Forms.Timer();
            _firmwareTimer.Interval = minutes * 60 * 1000;
            _firmwareTimer.Tick += async (s, e) => await FirmwareSyncTick();
            _firmwareTimer.Start();

            // Primer sync diferido 5s (después de que arranque heartbeat).
            var first = new System.Windows.Forms.Timer { Interval = 5000 };
            first.Tick += async (s, e) =>
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

        // Hook simple de log; reemplazable por logging real cuando exista.
        private static void Trace(string msg)
        {
            try { System.Diagnostics.Debug.WriteLine("[OrbitX] " + msg); } catch { }
        }

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

            try
            {
                // Heartbeat periódico — el panel marca el device offline si
                // pasaron > 2 min sin ver el ultimo_visto. Antes se enviaba
                // sólo una vez al arranque, por eso siempre figuraba offline.
                await SendHeartbeat();

                // Encolar archivos de módulos.
                if (_cfg.SyncVistaX) EnqueueVistaXFiles();
                if (_cfg.SyncQuantiX) EnqueueQuantiXFiles();
                if (_cfg.SyncSectionX) EnqueueSectionXFiles();
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
                // Merge en disco: no pisar cambios externos (CamarasStreamingEnabled, etc.).
                OrbitXConfig.SaveRuntimeFields(_cfg.LastSync, _cfg.FilesSynced, null);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
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

        private void EnqueueAOGFiles()
        {
            // Sincronizar el campo actual si hay uno abierto.
            try
            {
                string fieldName = _parent.currentFieldDirectory;
                if (string.IsNullOrEmpty(fieldName)) return;
                string fieldDir = Path.Combine(AgOpenGPS.RegistrySettings.fieldsDirectory, fieldName);
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

                // VistaX logs del campo.
                string vxDir = Path.Combine(fieldDir, "VistaX");
                if (Directory.Exists(vxDir))
                {
                    foreach (var f in Directory.GetFiles(vxDir, "*.ndjson"))
                        EnqueueIfChanged(f, "vistax/logs/" + fieldName + "/" + Path.GetFileName(f),
                            "vistax_log", "vistax", true, fieldName);
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
                string content = File.ReadAllText(localPath);
                string hash = ComputeMd5(content);

                string prev;
                if (_lastHashes.TryGetValue(localPath, out prev) && prev == hash)
                    return; // Sin cambios.

                _lastHashes[localPath] = hash;

                _queue.Enqueue(new SyncItem
                {
                    RutaRel = rutaRel,
                    Nombre = Path.GetFileName(localPath),
                    Subtipo = subtipo,
                    Producto = producto,
                    Contenido = content,
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
                    { "contenido", item.Contenido },
                    { "hash_md5", ComputeMd5(item.Contenido) },
                    { "tamano", item.Contenido.Length },
                    { "ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    { "device_id", _cfg.DeviceId },
                    { "es_lote", item.EsLote },
                    { "lote_nombre", item.LoteNombre }
                };

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

        private async Task SendHeartbeat()
        {
            try
            {
                string url = _cfg.ServerUrl.TrimEnd('/') + "/api/devices/heartbeat";
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
                request.Headers.Add("X-Device-ID", _cfg.DeviceId);
                request.Headers.Add("X-Auth-Token", _cfg.DeviceToken);

                var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    // Extraer estab_slug si viene en la respuesta.
                    if (body.Contains("estab_slug"))
                    {
                        int idx = body.IndexOf("\"estab_slug\":\"");
                        if (idx > 0)
                        {
                            idx += 14;
                            int end = body.IndexOf('"', idx);
                            if (end > idx)
                            {
                                _cfg.EstabSlug = body.Substring(idx, end - idx);
                                // Merge: solo updatea EstabSlug, preserva resto.
                                OrbitXConfig.SaveRuntimeFields(null, FilesSynced, _cfg.EstabSlug);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private async Task CheckPrescriptions()
        {
            try
            {
                string url = _cfg.ServerUrl.TrimEnd('/') + "/api/prescripciones/pendientes";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Device-ID", _cfg.DeviceId);
                request.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                    request.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);

                var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return;

                string body = await response.Content.ReadAsStringAsync();
                var items = JsonSerializer.Deserialize<JsonElement>(body);
                if (items.ValueKind != JsonValueKind.Array) return;

                foreach (var item in items.EnumerateArray())
                {
                    string id = item.GetProperty("id").GetString();
                    string nombre = item.TryGetProperty("nombre", out var n) ? n.GetString() : "prescripcion";

                    // Descargar contenido.
                    string dlUrl = _cfg.ServerUrl.TrimEnd('/') + "/api/prescripciones/pendientes/" + id + "/contenido";
                    var dlReq = new HttpRequestMessage(HttpMethod.Get, dlUrl);
                    dlReq.Headers.Add("X-Device-ID", _cfg.DeviceId);
                    dlReq.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                    if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                        dlReq.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);

                    var dlResp = await _http.SendAsync(dlReq);
                    if (!dlResp.IsSuccessStatusCode) continue;

                    string dlBody = await dlResp.Content.ReadAsStringAsync();
                    var dlData = JsonSerializer.Deserialize<JsonElement>(dlBody);

                    string contenido = dlData.TryGetProperty("contenido", out var c) ? c.GetString() : "";
                    if (string.IsNullOrEmpty(contenido)) continue;

                    // Guardar en el directorio del campo actual o en data/.
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "prescripciones");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    string filePath = Path.Combine(dir, nombre + ".geojson");
                    File.WriteAllText(filePath, contenido);
                    FilesSynced++;
                }
            }
            catch { }
        }

        private async Task SendTracking()
        {
            try
            {
                double lat = 0, lon = 0, heading = 0, speed = 0;
                string field = "";

                if (_parent.AppModel != null)
                {
                    lat = _parent.AppModel.CurrentLatLon.Latitude;
                    lon = _parent.AppModel.CurrentLatLon.Longitude;
                }
                speed = _parent.avgSpeed;
                heading = _parent.pivotAxlePos.heading;
                field = _parent.currentFieldDirectory ?? "";

                // No enviar si no hay posición GPS.
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
                            { "sectionx", _cfg.SyncSectionX }
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _http?.Dispose();
        }
    }
}
