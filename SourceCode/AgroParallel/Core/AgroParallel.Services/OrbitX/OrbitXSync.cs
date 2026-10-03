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
using AgroParallel.Services.OrbitX;

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

        // Lotes borrados por el operario. Ver ColaLotesBorrados: separa el
        // aviso pendiente al cloud del tombstone que evita que el sync
        // reponga lo que el operario acaba de borrar.
        private readonly ColaLotesBorrados _lotesBorrados = new ColaLotesBorrados(
            Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, "data", "lotes_borrados.json"));

        /// <summary>Cola de borrados pendientes de avisar al cloud. La UI encola
        /// acá al borrar un lote.</summary>
        public ColaLotesBorrados LotesBorrados => _lotesBorrados;

        // Espaciado de los avisos de borrado cuando el server NO tiene el
        // endpoint (404 del catch-all): reintentar cada 30 s contra una ruta que
        // no existe no la hace aparecer. 5 min → 1 h. Ver AvisarLotesBorrados.
        private readonly BackoffReintentos _avisoBorradoBackoff =
            new BackoffReintentos(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

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

            // Backoff del heartbeat: arranca en un tick de sync y se va
            // duplicando mientras el cloud siga caído, hasta 5 minutos. Ver
            // SendHeartbeat.
            int seg = _cfg.SyncIntervalSec > 0 ? _cfg.SyncIntervalSec : 30;
            _hbBackoff = new BackoffReintentos(TimeSpan.FromSeconds(seg), TimeSpan.FromMinutes(5));
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

        // Espaciado de reintentos del heartbeat cuando el cloud falla (502 del
        // proxy, SSL cortado, timeout). Ver SendHeartbeat.
        private readonly BackoffReintentos _hbBackoff;

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
                // Va con SyncAOG: es la config de PilotX, no de un modulo X-*.
                if (_cfg.SyncAOG) EnqueuePilotXConfig();

                await AvisarLotesBorrados();

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
                            Trace(string.Format("[PilotX] DESCARTADO tras {0} intentos: {1} ({2} bytes): {3}",
                                item.Intentos, item.Nombre, item.TamanoBytes, LastError ?? "sin respuesta"));
                            continue;
                        }
                        Trace(string.Format("[PilotX] PENDIENTE {0} ({1} bytes) — queda en cola ({2}) intento {3}/{4}: {5}",
                            item.Nombre, item.TamanoBytes, _queue.Count, item.Intentos, MaxIntentosPorItem, LastError ?? "sin respuesta"));
                        break;
                    }
                    _queue.Dequeue();
                    if (!string.IsNullOrEmpty(item.LocalPath))
                        _lastHashes[item.LocalPath] = item.HashMd5;
                    FilesSynced++;
                    subidos++;
                    Trace(string.Format("[PilotX] OK {0} · {1} · {2} bytes{3}",
                        item.Nombre, item.Subtipo, item.TamanoBytes,
                        item.EsLote ? " · lote " + item.LoteNombre : ""));
                }
                if (subidos > 0)
                    Trace(string.Format("[PilotX] {0} archivo(s) subidos · {1} en cola", subidos, _queue.Count));

                // Enviar posición del tractor (tracking).
                await SendTracking();

                // Subir lo nuevo del log de eventos.
                await SendLogEventos();

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
            EnqueueQuantiXPid(baseDir);
        }

        // Cuántos bytes de registro PID se encolan como MUCHO en un tick. El
        // CSV va como string adentro del JSON del sync y la cola vive en RAM:
        // sin tope, la primera sincronización de una pantalla con 100 corridas
        // guardadas se come la memoria y tapa los lotes del operario detrás.
        // Lo que no entra en este tick sale en el siguiente — el hash recién se
        // anota cuando el server confirma, así que nada se pierde.
        private const int PresupuestoPidPorTick = 4 * 1024 * 1024;

        // Un CSV más grande que esto no se sube: es una corrida de horas y el
        // POST no lo aguanta el enlace del tractor. Queda en disco para
        // levantarlo con un pendrive.
        private const int MaxCsvPidBytes = 8 * 1024 * 1024;

        // CSV de PID que ya se avisó que es demasiado grande. Sin esto el aviso
        // sale en CADA tick (cada 30 s, para siempre) y tapa el orbitx_sync.log
        // justo cuando se lo va a leer para otra cosa.
        private readonly HashSet<string> _pidDemasiadoGrande =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Sube el registro de PID de QuantiX: un CSV por motor a 5 Hz, SIN
        /// decimar, más el sesion.json con las ganancias de esa corrida.
        ///
        /// Por qué urge (Las Gringas, 2026-09-25): el CV de siembra se explica
        /// por huecos de consigna, pero NADA lo registraba donde se lo pueda
        /// mirar sin ir al tractor. La curva de rpm contra target es lo único
        /// que distingue "el PID no sigue" de "el motor no da más" de "se cortó
        /// el MQTT", y en el lote las tres se ven igual: semilla despareja.
        ///
        /// Sólo se suben las corridas CERRADAS — las que ya tienen sesion.json.
        /// La corrida en curso está siendo apendeada a 5 Hz: subirla cada 30 s
        /// sería mandar el archivo entero de nuevo cada vez, y encima
        /// incompleto. Cuando la corrida cierra, sube una sola vez.
        ///
        /// El sesion.json va DESPUÉS de sus CSV a propósito: que esté en el
        /// cloud significa que la corrida llegó completa.
        /// </summary>
        private void EnqueueQuantiXPid(string baseDir)
        {
            try
            {
                string raiz = Path.Combine(baseDir, "pid-quantix");
                if (!Directory.Exists(raiz)) return;

                var sesiones = new List<string>(Directory.GetDirectories(raiz));
                // Los nombres son yyyy-MM-dd_HHmm: ordenar por nombre es
                // ordenar por fecha. Las viejas primero — si hay backlog, sale
                // en orden cronológico y no salteado.
                sesiones.Sort(StringComparer.OrdinalIgnoreCase);

                int presupuesto = PresupuestoPidPorTick;

                foreach (string sesionDir in sesiones)
                {
                    string sidecar = Path.Combine(sesionDir, "sesion.json");
                    if (!File.Exists(sidecar)) continue;   // corrida en curso

                    string sesion = Path.GetFileName(sesionDir);
                    string prefijo = "quantix/pid/" + sesion + "/";
                    bool completa = true;

                    foreach (string csv in Directory.GetFiles(sesionDir, "*.csv"))
                    {
                        long largo;
                        try { largo = new FileInfo(csv).Length; }
                        catch { continue; }

                        if (largo > MaxCsvPidBytes)
                        {
                            if (_pidDemasiadoGrande.Add(csv))
                                Trace(string.Format(
                                    "[PID] {0}/{1} pesa {2} bytes — no se sube (tope {3}). Queda en disco.",
                                    sesion, Path.GetFileName(csv), largo, MaxCsvPidBytes));
                            continue;
                        }

                        // El presupuesto lo gasta SOLO lo que realmente se
                        // encoló: un CSV ya confirmado en un tick anterior sale
                        // por el early-return de EnqueueIfChanged y no cuenta.
                        int antes = _queue.Count;
                        EnqueueIfChanged(csv, prefijo + Path.GetFileName(csv), "quantix_pid", "quantix");
                        if (_queue.Count == antes) continue;

                        presupuesto -= (int)largo;
                        if (presupuesto <= 0) { completa = false; break; }
                    }

                    if (!completa) break;   // se sigue en el próximo tick

                    EnqueueIfChanged(sidecar, prefijo + "sesion.json", "quantix_pid", "quantix");
                }
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "encolar registro PID de QuantiX", ex);
            }
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

        /// <summary>
        /// Sube la CONFIGURACION DE LA MAQUINA: el perfil de vehiculo activo y
        /// los demas perfiles guardados (Vehicles/*.XML), mas el aog_settings
        /// que dice cual esta puesto.
        ///
        /// Por que urge: el perfil XML tiene TODA la geometria y calibracion —
        /// antena, implemento, secciones, PID de direccion y los look-ahead de
        /// encendido/apagado. Sin eso en el cloud no se puede diagnosticar un
        /// equipo sin ir hasta el tractor. El 2026-09-17 se quiso revisar un
        /// pintado tardio y el lookAheadOn no estaba en ningun lado.
        ///
        /// El server YA lo esperaba: routes/aog.js:84 busca
        /// subtipo "vehicle_config" y hay una pagina vehiculos.ejs en el panel.
        /// Estaban vacias porque nadie subia el archivo.
        ///
        /// LO QUE NO SE SUBE, a proposito:
        ///  · *.clave      sidecar de PerfilGuard: es la CLAVE que protege el
        ///                 perfil. Es una credencial, no configuracion.
        ///  · *.bak        respaldo local, ruido.
        ///  · orbitX.json  device_id + token del equipo.
        /// </summary>
        private void EnqueuePilotXConfig()
        {
            try
            {
                // 1) TODA la config del ConfigRoot (perfil, dirección, CoreX
                //    integrado, FlowX, nodos, secciones, etc.). Antes solo subía
                //    aog_settings.json; ahora se sube todo para poder ver la
                //    config completa del equipo desde el cloud sin ir al campo.
                //    Se excluye orbitX.json (device_id + token = credencial).
                string cfgRoot = AgroParallel.Common.AgpPaths.ConfigRoot;
                if (!string.IsNullOrEmpty(cfgRoot) && Directory.Exists(cfgRoot))
                {
                    foreach (var f in Directory.GetFiles(cfgRoot, "*.json"))
                    {
                        if (string.Equals(Path.GetFileName(f), "orbitX.json", StringComparison.OrdinalIgnoreCase))
                            continue;
                        EnqueueIfChanged(f, "pilotx/" + Path.GetFileName(f), "pilotx_config", "pilotx");
                    }

                    // Config del Engine de guiado (tool.json = geometría de la
                    // herramienta, look-ahead de secciones, etc.).
                    string geDir = Path.Combine(cfgRoot, "GuidanceEngineData");
                    if (Directory.Exists(geDir))
                        foreach (var f in Directory.GetFiles(geDir, "*.json"))
                            EnqueueIfChanged(f, "pilotx/GuidanceEngineData/" + Path.GetFileName(f),
                                             "pilotx_config", "pilotx");
                }

                // 2) Perfiles de vehículo (Vehicles/*.XML): geometría, offset y
                //    altura de antena, IMU, dirección. Vehicles/ es hermano de
                //    Fields/; se deriva del snapshot para no meterle la
                //    dependencia de AgOpenGPS.Core a este servicio. Si no hay
                //    campo abierto todavía no se puede ubicar, pero la config
                //    de arriba (los .json) igual se subió.
                var snap = _state.GetSnapshot();
                string fieldsRoot = snap?.FieldsDirectory;
                if (string.IsNullOrEmpty(fieldsRoot)) return;
                string dataRoot = Path.GetDirectoryName(
                    fieldsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(dataRoot)) return;

                string vehDir = Path.Combine(dataRoot, "Vehicles");
                if (!Directory.Exists(vehDir)) return;

                foreach (var f in Directory.GetFiles(vehDir))
                {
                    // Comparacion EXPLICITA de la extension y no un patron
                    // "*.XML": en Windows ese patron tambien engancha nombres
                    // como "Deutz.XMLviejo" por los nombres cortos 8.3.
                    if (!string.Equals(Path.GetExtension(f), ".XML", StringComparison.OrdinalIgnoreCase))
                        continue;
                    EnqueueIfChanged(f, "pilotx/vehicles/" + Path.GetFileName(f),
                                     "vehicle_config", "pilotx");
                }
            }
            catch (Exception ex)
            {
                // Que no se caiga el tick de sync por esto: los lotes del
                // operario importan mas que la config.
                LastError = ex.Message;
            }
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

                // Registro de alturas (planimetría): Elevation.txt NO va en la
                // lista de arriba porque no viaja entero — ver EnqueueElevacion.
                EnqueueElevacion(fieldDir, fieldName);

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
                AgpLog.Error("OrbitXSync", "encolar archivos de lote de PilotX", ex);
            }
        }

        // ── Elevation.txt (registro de alturas) ─────────────────────────────
        // Viaja en PARTES de 5000 filas (ElevacionPartes), cada una como
        // archivo propio del lote:
        //   ruta_rel = aog/fields/<lote>/Elevation/Elevation_0001.txt
        //   subtipo  = "elevation_points", producto "aog", es_lote = FALSE
        //   (con lote_nombre igual). es_lote=false a propósito: en OrbitX varias
        //   consultas traen TODOS los docs es_lote de un lote con su contenido
        //   (contexto del lote, /api/aog/lotes/:nombre, agrarIA con limit 20):
        //   decenas de partes de 300 KB las inflaban y podían dejar afuera el
        //   Boundary. La planimetría las junta por subtipo + lote_nombre.
        // Las partes completas no cambian nunca: se suben una vez y el server
        // no archiva copias (mismo hash). La última parte (la que crece) se
        // sube como mucho cada ElevacionIntervaloParcial, para no mandar —ni
        // archivar en el server— una copia nueva cada 30 s.
        // Un Elevation.txt con la cabecera sola (lotes migrados de AOG) no
        // viaja: no hay filas.
        private static readonly TimeSpan ElevacionIntervaloParcial = TimeSpan.FromMinutes(5);
        private readonly Dictionary<string, long> _elevacionLargoProcesado = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _elevacionUltimaParcial = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private void EnqueueElevacion(string fieldDir, string fieldName)
        {
            try
            {
                string path = Path.Combine(fieldDir, "Elevation.txt");
                var fi = new FileInfo(path);
                if (!fi.Exists) return;

                // Sin cambios de tamaño (y sin parcial demorada) no hay nada que
                // releer: el archivo es append-only.
                long largo;
                if (_elevacionLargoProcesado.TryGetValue(path, out largo) && largo == fi.Length) return;

                // FileShare.ReadWrite: el motor lo puede tener abierto escribiendo.
                string contenido;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    contenido = sr.ReadToEnd();
                }

                var partes = ElevacionPartes.Partir(contenido);
                DateTime ahora = DateTime.UtcNow;
                bool parcialDemorada = false;
                foreach (var p in partes)
                {
                    if (!p.Completa)
                    {
                        DateTime ultima;
                        if (_elevacionUltimaParcial.TryGetValue(path, out ultima) && ahora - ultima < ElevacionIntervaloParcial)
                        {
                            parcialDemorada = true;
                            continue;
                        }
                    }
                    string nombre = ElevacionPartes.NombreParte(p.Indice);
                    bool encolada = EnqueueTextoSiCambio(path + "#" + p.Indice,
                        "aog/fields/" + fieldName + "/Elevation/" + nombre, nombre,
                        p.Contenido, ElevacionPartes.Subtipo, "aog", false, fieldName);
                    if (!p.Completa && encolada) _elevacionUltimaParcial[path] = ahora;
                }
                // Con la parcial demorada hay que volver a mirar en el próximo
                // tick aunque el archivo no crezca (máquina parada al final).
                if (!parcialDemorada) _elevacionLargoProcesado[path] = fi.Length;
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "encolar Elevation.txt", ex);
            }
        }

        /// <summary>
        /// Como EnqueueIfChanged pero con el texto ya armado (una parte de un
        /// archivo). <paramref name="claveHash"/> identifica la parte para el
        /// "ya confirmado por el server". true = quedó encolada.
        /// </summary>
        private bool EnqueueTextoSiCambio(string claveHash, string rutaRel, string nombre, string texto,
            string subtipo, string producto, bool esLote, string loteNombre)
        {
            string hash = ComputeMd5(texto);
            string prev;
            if (_lastHashes.TryGetValue(claveHash, out prev) && prev == hash) return false;
            foreach (var enCola in _queue)
            {
                if (enCola.LocalPath != claveHash) continue;
                if (enCola.HashMd5 == hash) return false;
                // Una versión vieja de la MISMA parte sigue esperando (sin red
                // en el lote): se reemplaza en el lugar en vez de apilar copias.
                // Seguro: el encolado y la subida corren en el mismo SyncTick,
                // nunca a la vez.
                enCola.Contenido = texto;
                enCola.HashMd5 = hash;
                enCola.TamanoBytes = Encoding.UTF8.GetByteCount(texto);
                enCola.Intentos = 0;
                return true;
            }
            _queue.Enqueue(new SyncItem
            {
                LocalPath = claveHash,
                RutaRel = rutaRel,
                Nombre = nombre,
                Subtipo = subtipo,
                Producto = producto,
                Contenido = texto,
                EsBinario = false,
                HashMd5 = hash,
                TamanoBytes = Encoding.UTF8.GetByteCount(texto),
                EsLote = esLote,
                LoteNombre = loteNombre
            });
            return true;
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
                    // Versión real de PilotX (AssemblyInformationalVersion, con
                    // override por plataforma vía SetCurrentVersion) — antes iba
                    // el literal "AgOpenGPS-AP" y el CRM mostraba eso como versión.
                    { "version", PilotXSelfUpdate.Snapshot().CurrentVersion },
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
                    // Con el token nuevo el próximo heartbeat tiene que salir ya
                    // — no esperar el backoff que dejó el 401.
                    _hbBackoff.RegistrarExito();
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
        // Lee el ID de RustDesk ejecutando el cliente con --get-id (2 s de
        // timeout, best-effort) y lo cachea 10 minutos. Antes se leía UNA vez
        // por sesión: cuando el instalador LAN cambió el servidor de RustDesk
        // de la tablet de Clancy (2026-09-11) el ID cambió, y el heartbeat
        // siguió mandando el viejo hasta reiniciar PilotX, pisando lo que se
        // corregía a mano en OrbitX. Si RustDesk no está instalado o falla,
        // devuelve el último bueno (o null): el CRM muestra "sin RustDesk".
        private static string _rustdeskId;
        private static DateTime _rustdeskLeidoUtc = DateTime.MinValue;

        // ── Nodos para el heartbeat ──────────────────────────────────────────
        /// <summary>Lo cablea el host con el registro de nodos (NodoRegistryService.GetAll).
        /// Sin proveedor el heartbeat manda una lista vacia.</summary>
        public Func<IReadOnlyList<AgroParallel.Models.NodoStatus>> NodosProvider { get; set; }

        /// <summary>Mismo registro que NodosProvider, pero para el tracking:
        /// de acá sale la telemetría VIVA de cada motor (MotorsLive). Va
        /// separado a propósito — el censo del heartbeat corta en 64 nodos y
        /// aplana el estado; el tracking necesita el detalle por motor y de
        /// los QuantiX nada más. Lo cablea el host con NodoRegistryService.GetAll.</summary>
        public Func<IReadOnlyList<AgroParallel.Models.NodoStatus>> NodosLiveProvider { get; set; }

        /// <summary>Config de motores de QuantiX (QuantiXConfigService.GetMotores).
        /// Sin ella el tracking no puede traducir pps a sem/m ni saber qué
        /// surcos corta cada motor: los pulsos por segundo solos no le dicen
        /// nada a nadie.</summary>
        public Func<AgroParallel.Models.QxMotoresConfigDto> QuantiXConfigProvider { get; set; }

        private List<Dictionary<string, object>> ArmarNodos()
        {
            var lista = new List<Dictionary<string, object>>();
            try
            {
                var nodos = NodosProvider?.Invoke();
                if (nodos == null) return lista;
                foreach (var n in nodos)
                {
                    if (n == null || string.IsNullOrEmpty(n.Uid)) continue;
                    lista.Add(new Dictionary<string, object>
                    {
                        { "uid", n.Uid },
                        { "tipo", n.Type ?? "" },
                        { "fw", n.Firmware ?? "" },
                        { "ip", n.Ip ?? "" },
                        { "online", n.Online },
                        { "motors", n.Motors },
                        { "cables", n.Cables },
                        { "safe_mode", n.SafeMode },
                        { "crash_count", n.CrashCount },
                        // POR QUE se reinicio la ultima vez: "poweron" es normal;
                        // "brownout", "task_wdt", "panic" o "wdt" son las que
                        // explican un crash_count que sube solo. El firmware ya lo
                        // publica en su anuncio y NodoRegistryService lo captura,
                        // pero hasta ahora moria en la pantalla: desde el panel se
                        // veia el contador subir sin poder saber la causa. Caso
                        // real (Las Gringas, 2026-09-17): dos nodos con 38 y 37
                        // crashes y semillas desparejas, y para distinguir brownout
                        // de watchdog habia que ir hasta el tractor.
                        { "boot_reason", n.BootReason ?? "" },
                        { "last_seen", n.LastSeenUtc == default ? null : (object)n.LastSeenUtc.ToString("o") },
                    });
                    if (lista.Count >= 64) break;    // un heartbeat, no un censo
                }
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "armando nodos para el heartbeat", ex);
            }
            return lista;
        }

        private static string LeerRustDeskId()
        {
            if ((DateTime.UtcNow - _rustdeskLeidoUtc).TotalMinutes < 10) return _rustdeskId;
            _rustdeskLeidoUtc = DateTime.UtcNow;
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

        /// <summary>
        /// Heartbeat al cloud. Se traga TODOS sus errores a propósito: si el
        /// panel no puede marcar el tractor online, eso no puede frenar lo que
        /// de verdad importa (subir lotes, avisar borrados, bajar
        /// prescripciones), que sigue corriendo en el mismo tick.
        ///
        /// Cuando el cloud falla (502 del proxy, SSL cortado, timeout) se
        /// espacian los reintentos con _hbBackoff en vez de insistir cada 30 s:
        /// el server no se arregla porque le peguemos más seguido, y cada
        /// intento fallido se come hasta 30 s de timeout ADENTRO del tick,
        /// retrasando el resto del sync. Con un éxito, el backoff vuelve a cero.
        /// </summary>
        private async Task SendHeartbeat()
        {
            string url = (_cfg.ServerUrl ?? "").TrimEnd('/') + "/api/devices/heartbeat";
            if (!_hbBackoff.PuedeIntentar(DateTime.UtcNow))
                return; // en espera: ya se logueó al entrar al backoff, no se repite por tick

            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "device_id", _cfg.DeviceId },
                    { "hostname", Environment.MachineName },
                    { "platform", "win32" },
                    // Versión real de PilotX (AssemblyInformationalVersion, con
                    // override por plataforma vía SetCurrentVersion) — antes iba
                    // el literal "AgOpenGPS-AP" y el CRM mostraba eso como versión.
                    { "version", PilotXSelfUpdate.Snapshot().CurrentVersion },
                    { "aog_path", AppDomain.CurrentDomain.BaseDirectory },
                    // ID de RustDesk (soporte remoto): si está instalado se lee
                    // una vez y viaja en el payload; el CRM lo muestra en la
                    // ficha del cliente. Sin RustDesk va null — nada que instalar.
                    { "rustdesk_id", LeerRustDeskId() },
                    // Nodos ESP32 vistos por el broker (QuantiX, VistaX, FlowX…)
                    // con su firmware: OrbitX los muestra en Dispositivos y marca
                    // los que tienen una version mas nueva en el catalogo.
                    { "nodos", ArmarNodos() }
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
                    _hbBackoff.RegistrarExito();
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
                    AnotarFalloHeartbeat(code + " " + response.ReasonPhrase);

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
            // OJO con este catch: HttpClient tira TaskCanceledException (que es
            // OperationCanceledException) cuando se vence su propio Timeout, NO
            // sólo cuando alguien cancela. Acá no hay CancellationToken externo,
            // así que cualquier cancelación ES el timeout y taparla es correcto:
            // el heartbeat es best-effort y el timer sigue disparando ticks.
            // Si algún día este método recibe un ct, ESTE catch tiene que pasar
            // a `catch (OperationCanceledException) when (ct.IsCancellationRequested)`
            // y dejar que el timeout caiga en el catch de abajo — si no, un
            // timeout se confunde con un apagado ordenado (ya pasó en este repo:
            // un timeout mató un bucle de polling entero, en silencio).
            catch (OperationCanceledException ex)
            {
                LastHeartbeatStatus = "timeout";
                LastError = "HB timeout: " + ex.Message;
                AnotarFalloHeartbeat("timeout");
                Trace("[HB] TIMEOUT url=" + url + " msg=" + ex.Message);
            }
            catch (HttpRequestException ex)
            {
                // Acá caen el SSL cortado por el host remoto y el DNS/ruteo sin red.
                LastHeartbeatStatus = "http-error";
                LastError = "HB http: " + ex.Message;
                AnotarFalloHeartbeat("http-error");
                Trace("[HB] HTTP_ERR url=" + url + " msg=" + ex.Message
                    + (ex.InnerException != null ? " inner=" + ex.InnerException.Message : ""));
            }
            catch (Exception ex)
            {
                LastHeartbeatStatus = "exception";
                LastError = "HB ex: " + ex.Message;
                AnotarFalloHeartbeat(ex.GetType().Name);
                Trace("[HB] EX url=" + url + " type=" + ex.GetType().Name + " msg=" + ex.Message
                    + (ex.InnerException != null ? " inner=" + ex.InnerException.Message : ""));
            }
        }

        /// <summary>
        /// Anota un heartbeat fallido y espacia el próximo intento. Se loguea una
        /// sola línea por fallo (no una por tick), así el log sigue sirviendo
        /// para diagnosticar en campo sin taparse de repeticiones.
        /// </summary>
        private void AnotarFalloHeartbeat(string motivo)
        {
            var espera = _hbBackoff.RegistrarFallo(DateTime.UtcNow);
            Trace("[HB] backoff: " + motivo + " — fallo " + _hbBackoff.FallosConsecutivos
                + " seguido(s), próximo intento en " + (int)espera.TotalSeconds + "s");
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
        /// (nombreLote, contenidoKml) → nombre de la carpeta REALMENTE usada, o
        /// null/vacío si no se pudo importar. Sin esto los lotes del cloud solo
        /// dejan el .kml crudo en el directorio del lote. El nombre resuelto
        /// puede diferir del pedido (colisión con un lote del operario → entra
        /// como "&lt;nombre&gt; (OrbitX)"): el .kml de backup tiene que caer en
        /// esa carpeta, nunca en la del nombre pedido.</summary>
        public Func<string, string, string> ImportarLoteDesdeKml;

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

                // El operario borró este lote y todavía no se le pudo avisar al
                // cloud. Si lo escribiéramos, reaparecería solo en el próximo
                // ciclo: ResolutorLoteCloud devuelve Crear cuando la carpeta no
                // existe. Se ackea el pendiente para que el server no lo
                // reencole eternamente.
                if (_lotesBorrados.EstaBorrado(lote))
                {
                    Trace("[LOTE] '" + lote + "' fue borrado en la cabina — no se repone");
                    return true;
                }

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
                    // rel = "<loteCloud>/<archivo>" — el nombre SIN resolver. Si
                    // hubo colisión con un lote del operario, ImportarLoteDesdeKml
                    // ya mandó el lindero a "<loteCloud> (OrbitX)"; el .kml de
                    // backup tiene que ir a la MISMA carpeta resuelta, nunca a la
                    // del nombre pedido — si no, el backup cae en la carpeta del
                    // operario (huérfano hoy, pero el boundary.kml es SOBERANO:
                    // el día que se lea de nuevo, le mete el lindero del cloud).
                    var importar = ImportarLoteDesdeKml;
                    string carpetaResuelta = lote;
                    if (importar != null)
                    {
                        carpetaResuelta = importar(lote, contenido);
                        bool ok = !string.IsNullOrEmpty(carpetaResuelta);
                        Trace(ok
                            ? "[LOTE] '" + lote + "' importado desde el KML del cloud (lindero listo, carpeta '" + carpetaResuelta + "')"
                            : "[LOTE] no se pudo importar '" + lote + "' desde el KML");
                        if (!ok) return false;
                    }
                    // El .kml crudo se guarda igual, como referencia/backup — en
                    // la carpeta resuelta, no en la del nombre pedido.
                    string restoRel = rel.Substring(lote.Length);   // "/archivo.kml" (con lo que venga después)
                    string relResuelto = carpetaResuelta + restoRel;
                    string destinoKml = Path.Combine(fieldsDir, relResuelto.Replace('/', Path.DirectorySeparatorChar));
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
                    // Telemetría de guiado en vivo — para diagnosticar remotamente
                    // desde OrbitX (cross-track, ángulo de dirección, fix, si el
                    // piloto está enganchado) sin depender de fotos de la pantalla.
                    { "xte", snap.CrossTrackErrorM },
                    { "steer_angle", snap.SteerAngleDeg },
                    { "fix", snap.FixQuality },
                    { "autosteer", snap.IsAutoSteerOn },
                    { "reverse", snap.IsReverse },
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

                // Cada motor de QuantiX con lo que está haciendo AHORA. Sin
                // esto, la posición del tractor en el cloud no dice nada de la
                // siembra: se veía un recorrido y había que creerle al operario
                // que "iba despareja". Ver ArmarQx.
                var qx = ArmarQx(snap);
                if (qx.Count > 0) payload["qx"] = qx;

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

        // Un motor, no un nodo: el nodo es la caja, el motor es el surco.
        private const int MaxMotoresTracking = 64;

        /// <summary>
        /// Qué está dosificando cada motor de QuantiX en este instante, para
        /// que el punto de tracking sirva de diagnóstico y no sólo de mapita.
        ///
        /// Van pps objetivo y pps real (lo que PilotX pidió y lo que el sensor
        /// contó), el PWM y la carga, y esos mismos pps traducidos a la unidad
        /// que usa el operario — sem/m o kg/ha, NUNCA pps. Los pps quedan igual
        /// porque son el único dato crudo: si la calibración está mal cargada,
        /// la traducción miente y los pulsos no.
        ///
        /// La inversa es la MISMA que muestra el widget de cabina
        /// (WidgetQuantiXController): sem/m = pps × sem_vuelta / ppr / (vel ×
        /// surcos del motor). Que el cloud calcule distinto que la pantalla es
        /// peor que no tener el dato — se discutiría cuál de los dos miente.
        ///
        /// `seccion_on` es el PEDIDO de PilotX (SectionOnRequest cruzado con
        /// los cortes del motor), no el estado ya filtrado que manda el bridge:
        /// el antirrebote de apagado vive en el bridge y acá se quiere ver el
        /// pedido crudo, que es contra lo que se compara el pps real.
        /// </summary>
        private List<Dictionary<string, object>> ArmarQx(AgroParallel.Models.AogStateSnapshot snap)
        {
            var lista = new List<Dictionary<string, object>>();
            try
            {
                var cfg = QuantiXConfigProvider?.Invoke();
                if (cfg == null || cfg.Nodos == null || cfg.Nodos.Count == 0) return lista;

                // Live por UID. Sólo QuantiX: el resto de los nodos no tiene motores.
                var liveByUid = new Dictionary<string, AgroParallel.Models.NodoStatus>(
                    StringComparer.OrdinalIgnoreCase);
                var vivos = NodosLiveProvider?.Invoke();
                if (vivos != null)
                {
                    foreach (var n in vivos)
                    {
                        if (n == null || string.IsNullOrEmpty(n.Uid)) continue;
                        if (n.Type == null ||
                            n.Type.IndexOf("quantix", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        liveByUid[n.Uid] = n;
                    }
                }

                double velMs = (snap != null ? snap.AvgSpeed : 0) / 3.6;
                double anchoM = (snap != null && snap.ToolWidth > 0) ? snap.ToolWidth : 0;
                bool[] secciones = snap != null ? snap.SectionOnRequest : null;

                foreach (var nodo in cfg.Nodos)
                {
                    if (nodo == null || string.IsNullOrEmpty(nodo.Uid)) continue;
                    if (!nodo.Habilitado || nodo.Motores == null) continue;

                    AgroParallel.Models.NodoStatus live;
                    liveByUid.TryGetValue(nodo.Uid, out live);

                    for (int mi = 0; mi < nodo.Motores.Length; mi++)
                    {
                        var motor = nodo.Motores[mi];
                        if (motor == null || !motor.Habilitado) continue;

                        double ppsObj = 0, ppsReal = 0;
                        int pwm = 0, carga = 0;
                        if (live != null && live.MotorsLive != null)
                        {
                            foreach (var ml in live.MotorsLive)
                            {
                                if (ml == null || ml.Id != mi) continue;
                                ppsObj = ml.PpsTarget;
                                ppsReal = ml.PpsReal;
                                pwm = ml.Pwm;
                                carga = ml.LoadPct;
                                break;
                            }
                        }

                        bool esSem = string.Equals(motor.UnidadDosis, "sem_m",
                                                   StringComparison.OrdinalIgnoreCase);

                        bool seccionOn = false;
                        if (secciones != null && motor.Cortes != null)
                        {
                            foreach (int corte in motor.Cortes)
                            {
                                int idx = corte - 1;
                                if (idx >= 0 && idx < secciones.Length && secciones[idx])
                                {
                                    seccionOn = true;
                                    break;
                                }
                            }
                        }

                        lista.Add(new Dictionary<string, object>
                        {
                            { "uid", nodo.Uid },
                            { "id", mi },
                            { "pps_obj", Redondear(ppsObj, 2) },
                            { "pps_real", Redondear(ppsReal, 2) },
                            { "obj", Redondear(PpsADosis(ppsObj, motor, esSem, velMs, anchoM), 2) },
                            { "real", Redondear(PpsADosis(ppsReal, motor, esSem, velMs, anchoM), 2) },
                            { "unidad", esSem ? "sem_m" : "kg_ha" },
                            { "pwm", pwm },
                            { "carga_pct", carga },
                            { "seccion_on", seccionOn },
                            { "cortes", motor.Cortes },
                        });

                        if (lista.Count >= MaxMotoresTracking) return lista;
                    }
                }
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "armando motores de QuantiX para el tracking", ex);
            }
            return lista;
        }

        /// <summary>Inversa pps → dosis en la unidad del motor. 0 con el tractor
        /// quieto: dividir por una velocidad de casi cero da números enormes que
        /// después hay que explicar.</summary>
        internal static double PpsADosis(double pps, AgroParallel.Models.QxMotorConfigDto motor,
                                         bool esSem, double velMs, double anchoM)
        {
            if (pps <= 0 || velMs <= 0.1) return 0;
            if (esSem)
            {
                double ppr = motor.DientesEngranaje > 0 ? motor.DientesEngranaje : 24;
                double semPorPulso = motor.SemillasVuelta / ppr;
                int surcos = (motor.Cortes != null && motor.Cortes.Count > 0) ? motor.Cortes.Count : 1;
                return pps * semPorPulso / (velMs * surcos);
            }
            if (motor.MeterCal > 0 && anchoM > 0)
                return pps * motor.MeterCal * 10.0 / (anchoM * velMs);
            return 0;
        }

        private static double Redondear(double v, int decimales)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            return Math.Round(v, decimales, MidpointRounding.AwayFromZero);
        }

        // =====================================================================
        // Log de eventos → cloud
        // =====================================================================

        // Espaciado cuando el server no tiene el endpoint todavía (404 del
        // catch-all): mismo criterio que el aviso de lotes borrados.
        private readonly BackoffReintentos _logBackoff =
            new BackoffReintentos(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        // Tope por envío. El log es una ayuda para diagnosticar, no un stream:
        // si una pantalla estuvo dos días sin señal y juntó 40.000 líneas, sale
        // de a tandas y no en un POST de varios MB que el enlace del tractor no
        // banca.
        // Tiene que quedar POR DEBAJO del tope del server (LOG_MAX_LINEAS_ENVIO
        // en routes/aog.js): si mandáramos más de las que el server acepta, él
        // guarda las primeras, contesta ok, y nosotros avanzamos el offset — o
        // sea, las de más se pierden calladas.
        private const int MaxLineasPorEnvio = 400;
        private const int MaxBytesPorEnvio = 192 * 1024;

        /// <summary>Archivo de eventos de PilotX. Lo escribe AgLibrary
        /// (Log.EventWriter + FileSaveSystemEvents, que en el motor baja a disco
        /// cada 30 s desde CrashLog). Se deja settable para el host que mueva el
        /// directorio de trabajo.</summary>
        public string EventLogPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AgOpenGPS", "Logs", "AgOpenGPS_Events_Log.txt");

        private string OffsetLogPath
        {
            get
            {
                return Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot,
                                    "data", "orbitx_log_offset.json");
            }
        }

        /// <summary>
        /// Sube al cloud las líneas NUEVAS del log de eventos de PilotX.
        ///
        /// Por qué (Las Gringas, 2026-09-25): el CV de siembra se explicaba por
        /// microcortes de MQTT, y nada los registraba donde se los pueda mirar.
        /// Cuando el nodo se cae y vuelve, el broker embebido escribe [-] y [+]
        /// en este mismo log; con el log en el cloud, el hueco de semilla y la
        /// reconexión quedan en la misma línea de tiempo que el tracking. Sin
        /// esto hay que ir hasta el tractor con un pendrive, y para entonces el
        /// log ya rotó.
        ///
        /// El offset vive en disco y SOLO avanza cuando el server confirmó: un
        /// POST que falla en el lote (que es la mitad de los POST) no puede
        /// hacer que esas líneas se pierdan para siempre. Si el archivo se
        /// achicó —AgLibrary lo recorta al pasar 1 MB— el offset vuelve a cero y
        /// se sube todo de nuevo: repetir líneas es barato, perderlas no.
        ///
        /// Del corte se consume hasta el ÚLTIMO fin de línea: si el flush de
        /// AgLibrary cayó justo en la mitad de una línea, esa mitad espera al
        /// próximo tick en vez de viajar partida.
        /// </summary>
        private async Task SendLogEventos()
        {
            if (!_logBackoff.PuedeIntentar(DateTime.UtcNow)) return;

            string ruta = EventLogPath;
            if (string.IsNullOrEmpty(ruta) || !File.Exists(ruta)) return;

            long offset;
            string rutaAnotada;
            LeerOffsetLog(out rutaAnotada, out offset);
            // Log distinto (cambió el directorio de trabajo) o log recortado:
            // arrancar de cero.
            if (!string.Equals(rutaAnotada, ruta, StringComparison.OrdinalIgnoreCase)) offset = 0;

            string bloque;
            long nuevoOffset;
            try
            {
                using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (offset > fs.Length) offset = 0;      // el log se recortó
                    if (offset == fs.Length) return;         // nada nuevo
                    fs.Seek(offset, SeekOrigin.Begin);

                    int cuanto = (int)Math.Min(fs.Length - offset, MaxBytesPorEnvio);
                    var buf = new byte[cuanto];
                    int leidos = fs.Read(buf, 0, cuanto);
                    if (leidos <= 0) return;

                    bool hastaElFinal = (offset + leidos) >= fs.Length;
                    int corte = CorteHastaFinDeLinea(buf, leidos, hastaElFinal, MaxLineasPorEnvio);
                    if (corte <= 0) return;   // una sola línea gigante a medio escribir

                    bloque = Encoding.UTF8.GetString(buf, 0, corte);
                    nuevoOffset = offset + corte;
                }
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "leer el log de eventos", ex);
                return;
            }

            var lineas = PartirLineasLog(bloque, MaxLineasPorEnvio);
            if (lineas.Count == 0)
            {
                // Puro separador: avanzar igual, si no el offset se traba acá.
                GuardarOffsetLog(ruta, nuevoOffset);
                return;
            }

            try
            {
                string url = _cfg.ServerUrl.TrimEnd('/') + "/api/aog/log";
                var payload = new Dictionary<string, object>
                {
                    { "device_id", _cfg.DeviceId },
                    { "ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    { "archivo", Path.GetFileName(ruta) },
                    { "lineas", lineas },
                };

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                request.Headers.Add("X-Device-ID", _cfg.DeviceId);
                request.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                    request.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);

                var resp = await _http.SendAsync(request);
                if (resp.IsSuccessStatusCode)
                {
                    _logBackoff.RegistrarExito();
                    GuardarOffsetLog(ruta, nuevoOffset);
                    Trace(string.Format("[LOG] {0} línea(s) subidas · offset {1}", lineas.Count, nuevoOffset));
                    return;
                }

                if ((int)resp.StatusCode == 404)
                {
                    _logBackoff.RegistrarFallo(DateTime.UtcNow);
                    Trace("[LOG] el server no tiene /api/aog/log todavía — se reintenta más tarde");
                    return;
                }

                // 4xx que no es 404 (device sin estab, cuerpo rechazado): el
                // offset NO avanza, pero insistir cada 30 s contra un rechazo
                // permanente tampoco sirve.
                _logBackoff.RegistrarFallo(DateTime.UtcNow);
                Trace(string.Format("[LOG] el server contestó {0} — offset sin avanzar", (int)resp.StatusCode));
            }
            catch (Exception ex)
            {
                // Sin red en el lote: ni backoff ni offset. El próximo tick reintenta.
                AgpLog.Warn("OrbitXSync", "subir el log de eventos", ex);
            }
        }

        /// <summary>
        /// Hasta dónde del bloque leído se puede consumir, en bytes. De acá sale
        /// el offset nuevo, así que TIENE que coincidir con lo que después se
        /// manda: cortar por bytes acá y volver a cortar por líneas más adelante
        /// hacía que el offset avanzara sobre líneas que nunca salieron del
        /// tractor. Por eso el tope de líneas se aplica ACÁ y no después.
        ///
        /// Reglas: se corta en un fin de línea (CRLF cuenta como uno solo), a
        /// más tardar en la línea `maxLineas`. Si el bloque llega al final del
        /// archivo y no se llegó al tope, se consume entero — la última línea
        /// está completa aunque AgLibrary todavía no le haya puesto separador.
        /// Devuelve 0 cuando no hay ningún fin de línea y queda archivo por
        /// delante (una línea a medio escribir, más larga que el bloque): ahí no
        /// se consume nada y se espera al próximo tick.
        /// </summary>
        internal static int CorteHastaFinDeLinea(byte[] buf, int leidos, bool hastaElFinal, int maxLineas)
        {
            if (buf == null || leidos <= 0) return 0;
            if (leidos > buf.Length) leidos = buf.Length;
            if (maxLineas <= 0) return 0;

            int lineas = 0;
            int ultimoFin = 0;
            for (int i = 0; i < leidos; i++)
            {
                byte b = buf[i];
                if (b != (byte)'\n' && b != (byte)'\r') continue;
                // "\r\n" es UN fin de línea, no dos.
                if (b == (byte)'\r' && i + 1 < leidos && buf[i + 1] == (byte)'\n') i++;
                lineas++;
                ultimoFin = i + 1;
                if (lineas >= maxLineas) return ultimoFin;
            }

            if (hastaElFinal) return leidos;
            return ultimoFin;
        }

        /// <summary>
        /// Parte el bloque en líneas. AgLibrary separa los eventos con "\r"
        /// PELADO, no con CRLF (ver Log.EventWriter): partir sólo por '\n' deja
        /// el archivo entero convertido en una sola línea kilométrica, que es
        /// exactamente lo que hacía la página de eventos del panel del motor.
        /// </summary>
        internal static List<string> PartirLineasLog(string bloque, int max)
        {
            var lineas = new List<string>();
            if (string.IsNullOrEmpty(bloque)) return lineas;
            foreach (var l in bloque.Split('\r', '\n'))
            {
                string t = l.Trim();
                if (t.Length == 0) continue;
                lineas.Add(Truncate(t, 1000));
                if (lineas.Count >= max) break;
            }
            return lineas;
        }

        private void LeerOffsetLog(out string ruta, out long offset)
        {
            ruta = null;
            offset = 0;
            try
            {
                string p = OffsetLogPath;
                if (!File.Exists(p)) return;
                using (var doc = JsonDocument.Parse(File.ReadAllText(p)))
                {
                    JsonElement el;
                    if (doc.RootElement.TryGetProperty("path", out el) && el.ValueKind == JsonValueKind.String)
                        ruta = el.GetString();
                    if (doc.RootElement.TryGetProperty("offset", out el) && el.TryGetInt64(out long v) && v >= 0)
                        offset = v;
                }
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "leer el offset del log de eventos", ex);
                ruta = null;
                offset = 0;
            }
        }

        private void GuardarOffsetLog(string ruta, long offset)
        {
            try
            {
                string p = OffsetLogPath;
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var payload = new Dictionary<string, object>
                {
                    { "path", ruta },
                    { "offset", offset },
                    { "ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                };
                File.WriteAllText(p, JsonSerializer.Serialize(payload), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                AgpLog.Warn("OrbitXSync", "guardar el offset del log de eventos", ex);
            }
        }

        /// <summary>
        /// Le avisa al cloud de los lotes que el operario borró. Es best-effort:
        /// en el lote no hay WiFi, así que lo que no sale ahora sale en el
        /// próximo tick. El tombstone se levanta ÚNICAMENTE cuando el cloud
        /// confirma (2xx, 410, o un 404 cuyo cuerpo dice que el lote no está);
        /// abandonar el aviso NO lo toca — el lote sigue protegido contra la
        /// reposición aunque se deje de insistir.
        ///
        /// Los desenlaces los decide ClasificadorAvisoBorrado. Lo importante:
        ///  · fallo transitorio (sin red, 5xx, timeout) ⇒ NO se descarta nada,
        ///    se corta el barrido y se reintenta TODO en el próximo tick. La PC
        ///    del tractor pasa horas sin señal: la cola tiene que sobrevivir eso.
        ///  · rechazo permanente (4xx) ⇒ se abandona ese aviso en el acto, con
        ///    una sola línea de log. Antes se gastaban 5 POST por lote contra un
        ///    código que nunca iba a cambiar (20 líneas en orbitx_sync.log por
        ///    cada barrido de 4 lotes).
        ///  · el server no tiene el endpoint (404 del catch-all) ⇒ el problema es
        ///    el mismo para TODOS los lotes: se corta el barrido y se espacia el
        ///    reintento (5 min → 1 h), con una línea que nombra el problema real
        ///    (falta desplegar /api/aog/lote/borrado). Los avisos quedan en cola
        ///    para cuando el endpoint exista.
        /// </summary>
        private async Task AvisarLotesBorrados()
        {
            var pendientes = _lotesBorrados.Pendientes();
            if (pendientes.Count == 0) return;
            // Si el server no tiene el endpoint, no se vuelve a probar hasta que
            // venza el backoff (ver más abajo).
            if (!_avisoBorradoBackoff.PuedeIntentar(DateTime.UtcNow)) return;

            string url = _cfg.ServerUrl.TrimEnd('/') + "/api/aog/lote/borrado";

            for (int i = 0; i < pendientes.Count; i++)
            {
                string lote = pendientes[i];
                int codigo;
                string cuerpo;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Add("X-Device-ID", _cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                    if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                        req.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);
                    req.Content = new StringContent(
                        JsonSerializer.Serialize(new Dictionary<string, object> { ["lote"] = lote }),
                        Encoding.UTF8, "application/json");

                    var resp = await _http.SendAsync(req);
                    codigo = (int)resp.StatusCode;
                    cuerpo = "";
                    try { cuerpo = await resp.Content.ReadAsStringAsync(); } catch { } // silencioso a propósito: best-effort leer el cuerpo para clasificar
                }
                catch (Exception ex)
                {
                    // Sin señal: no tiene sentido intentar los pendientes que
                    // quedan en este mismo tick — cada POST son otros 30s de
                    // _http.Timeout, y "Borrar todos" puede dejar cientos de
                    // avisos en cola. Mismo criterio que la cola de subida más
                    // abajo en SyncTick ("sin red, insistir con los demás solo
                    // suma timeouts"): se corta acá, sin descartar nada, y se
                    // reintenta TODO en el próximo tick.
                    Trace("[LOTE] no se pudo avisar el borrado de '" + lote + "': " + ex.Message);
                    return;
                }

                var resultado = ClasificadorAvisoBorrado.Clasificar(codigo, cuerpo);
                if (resultado == ResultadoAvisoBorrado.Confirmado)
                {
                    _avisoBorradoBackoff.RegistrarExito(); // el endpoint responde: se vuelve al ritmo normal
                    _lotesBorrados.Confirmar(lote);
                    Trace("[LOTE] borrado avisado al cloud: '" + lote + "' (HTTP " + codigo + ")");
                    continue;
                }

                if (resultado == ResultadoAvisoBorrado.Reintentable)
                {
                    // Igual que el corte por excepción: el server está caído o
                    // ocupado, insistir con los demás en este tick sólo suma
                    // esperas. Nada se descarta.
                    Trace("[LOTE] el cloud no pudo tomar el aviso de '" + lote + "' (HTTP "
                        + codigo + ") — se reintenta en el próximo ciclo");
                    return;
                }

                if (resultado == ResultadoAvisoBorrado.RutaInexistente)
                {
                    // El server no tiene desplegado /api/aog/lote/borrado: el 404
                    // viene del catch-all, no del lote. Vale para TODOS los
                    // pendientes, así que no se prueba ninguno más en este ciclo.
                    //
                    // Los avisos NO se tiran: el día que el endpoint esté
                    // desplegado, el cloud se entera de los lotes que el operario
                    // ya había borrado. Lo que sí se hace es espaciar mucho el
                    // reintento (5 min → 1 h), que es lo que faltaba: antes se
                    // gastaba un POST por lote en CADA ciclo de 30 s contra un
                    // 404 que nunca iba a cambiar (con 4 lotes borrados, 20
                    // líneas en orbitx_sync.log en dos minutos).
                    var espera = _avisoBorradoBackoff.RegistrarFallo(DateTime.UtcNow);
                    Trace("[LOTE] el servidor no tiene el endpoint de borrado (HTTP " + codigo
                        + " en " + url + ") — quedan " + pendientes.Count
                        + " aviso(s) en cola, se reintenta en " + (int)espera.TotalMinutes
                        + " min; los lotes siguen protegidos en la cabina");
                    return;
                }

                // Rechazo permanente de ESTE aviso: se abandona y se sigue con
                // los demás. El tombstone queda.
                _avisoBorradoBackoff.RegistrarExito(); // el endpoint contestó: existe
                _lotesBorrados.AbandonarAviso(lote);
                Trace("[LOTE] el cloud rechazó el aviso de borrado de '" + lote + "' (HTTP " + codigo
                    + ") — no se insiste; el lote sigue protegido en la cabina");
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
