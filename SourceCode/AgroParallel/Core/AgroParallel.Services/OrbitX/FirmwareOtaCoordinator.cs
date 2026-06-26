// ============================================================================
// FirmwareOtaCoordinator.cs
//
// Coordinador único de OTA y comandos genéricos hacia nodos ESP32.
//
// Reusa la conexión MQTT del NodoRegistryService (no abre otro cliente):
//   · Se suscribe al wildcard `agp/+/+/ota/resultado` apenas arranca.
//   · Mantiene un Dictionary<UID, NodoOtaState> con el último estado conocido
//     del OTA por nodo (sent → iniciando → ok|error).
//   · Expone SendOtaAsync(producto, uid, version) → publica
//     agp/{producto}/{UID}/cmd con {cmd:"ota", url, version}.
//   · Expone SendCmdAsync(producto, uid, cmd, extras?) para reiniciar,
//     borrar_wifi, estado, ping, etc. (mismos comandos que ya entienden los
//     firmwares VistaX y QuantiX).
//
// La URL del firmware se arma con FirmwareOtaClient.BuildFirmwareUrl, que
// resuelve la IP LAN del PC y apunta al FirmwareLanServer (puerto de
// OrbitXConfig.FirmwareHttpPort, default 8088).
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using AgroParallel.Models;

namespace AgroParallel.OrbitX
{
    /// <summary>Snapshot del último estado conocido de un OTA por nodo.</summary>
    public sealed class NodoOtaState
    {
        public string Uid { get; set; }
        public string Producto { get; set; }
        /// <summary>idle | sent | iniciando | ok | error</summary>
        public string Status { get; set; }
        public string Version { get; set; }
        public string Detalle { get; set; }
        public DateTime LastChangeUtc { get; set; }
        /// <summary>Estimación de progreso 0..100 para mostrar barra en UI.</summary>
        public int ProgressPct { get; set; }
        /// <summary>URL servida al nodo (debug/UI).</summary>
        public string Url { get; set; }
    }

    public sealed class FirmwareOtaCoordinator : IDisposable
    {
        private readonly INodoRegistryService _registry;
        private readonly OrbitXConfig _cfg;
        private readonly Action<string> _log;
        private readonly ConcurrentDictionary<string, NodoOtaState> _state =
            new ConcurrentDictionary<string, NodoOtaState>(StringComparer.OrdinalIgnoreCase);
        // K9: StartAsync puede ser llamado concurrentemente (UI + arranque app).
        // Sin lock, dos llamados podían leer _started=false simultáneamente y
        // suscribirse dos veces al wildcard / colgar dos veces el handler.
        private readonly object _startLock = new object();
        private bool _started;

        // H2: watchdog para OTAs que se quedan colgados en "iniciando"/"sent"
        // sin recibir el resultado final. Si el firmware crashea mid-flash o
        // pierde MQTT durante el reboot, el estado quedaba para siempre en
        // "iniciando" — la UI no podía mostrar fallo y el cloud nunca recibía
        // resultado. Cada WATCHDOG_INTERVAL_MS escaneamos y degradamos los
        // que excedieron WATCHDOG_TIMEOUT_MS.
        private const int WATCHDOG_INTERVAL_MS = 30_000;   // chequeo cada 30s
        private const int WATCHDOG_TIMEOUT_MS  = 300_000;  // 5 min sin progreso
        private Timer _watchdog;

        // HttpClient compartido para postear resultados al cloud OrbitX.
        // Lazy: solo se crea si efectivamente termina un OTA y hay que reportar.
        // Una sola instancia por toda la app — HttpClient está pensado para vivir
        // mucho y reusarse (DNS pooling).
        private static HttpClient _sharedHttp;
        private static readonly object _httpLock = new object();
        private static HttpClient GetHttp()
        {
            if (_sharedHttp != null) return _sharedHttp;
            lock (_httpLock)
            {
                if (_sharedHttp == null)
                {
                    _sharedHttp = new HttpClient();
                    _sharedHttp.Timeout = TimeSpan.FromSeconds(15);
                }
                return _sharedHttp;
            }
        }

        // Anti-duplicado: el firmware puede republicar el resultado (retained o
        // reanuncio tras reconexión). Trackeamos qué (uid, version, status_final)
        // ya se reportó al cloud y lo skipeamos hasta que arranque otro OTA.
        // El set se resetea para el uid cuando llega un "iniciando" o "sent".
        private readonly ConcurrentDictionary<string, string> _lastReportedKey =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public FirmwareOtaCoordinator(INodoRegistryService registry,
                                      OrbitXConfig cfg = null,
                                      Action<string> log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _cfg = cfg ?? OrbitXConfig.Load();
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Suscribe el wildcard de resultados y engancha el handler. Idempotente.
        /// </summary>
        public async Task StartAsync()
        {
            // K9: chequeo + set bajo lock para evitar doble subscribe/handler.
            lock (_startLock)
            {
                if (_started) return;
                _started = true;
            }
            try
            {
                await _registry.SubscribeAsync("agp/+/+/ota/resultado");
                _registry.MessageReceived += OnMqttMessage;
                // H2: arrancar el watchdog de OTAs colgados.
                _watchdog = new Timer(WatchdogTick, null,
                    WATCHDOG_INTERVAL_MS, WATCHDOG_INTERVAL_MS);
                _log("FirmwareOtaCoordinator: arriba, suscripto a agp/+/+/ota/resultado");
            }
            catch (Exception ex)
            {
                _log("FirmwareOtaCoordinator start error: " + ex.Message);
                // Revertir el started si falló — siguiente StartAsync reintentará.
                lock (_startLock) { _started = false; }
            }
        }

        public void Stop()
        {
            lock (_startLock)
            {
                if (!_started) return;
                _started = false;
            }
            try { _registry.MessageReceived -= OnMqttMessage; } catch { }
            try { _watchdog?.Dispose(); _watchdog = null; } catch { }
        }

        // ── Lecturas ─────────────────────────────────────────────────────────

        public NodoOtaState GetState(string uid)
            => string.IsNullOrEmpty(uid) ? null : (_state.TryGetValue(uid, out var s) ? s : null);

        public IReadOnlyCollection<NodoOtaState> GetAll()
            => _state.Values.ToList();

        // ── Acciones ─────────────────────────────────────────────────────────

        /// <summary>
        /// Dispara OTA en el nodo. Devuelve la URL que se entregó al nodo (útil
        /// para mostrarla en UI) o vacío si falló la publicación.
        /// </summary>
        public Task<(bool ok, string url, string topic, string error)> SendOtaAsync(
            string producto, string uid, string version)
            => SendOtaAsync(producto, uid, version, allowDowngrade: false);

        public async Task<(bool ok, string url, string topic, string error)> SendOtaAsync(
            string producto, string uid, string version, bool allowDowngrade)
        {
            if (string.IsNullOrWhiteSpace(producto)) return (false, "", "", "producto-vacio");
            if (string.IsNullOrWhiteSpace(uid))      return (false, "", "", "uid-vacio");
            if (string.IsNullOrWhiteSpace(version))  return (false, "", "", "version-vacia");

            string prodLo = producto.Trim().ToLowerInvariant();

            // H1: no-downgrade por defecto. Buscar el firmware reportado por el
            // nodo en el announcement y rechazar si la versión solicitada es
            // estrictamente menor (semver). Re-flashear la misma versión sigue
            // siendo válido (caso recovery: bin corrupto, EEPROM volada, etc.).
            // El operario puede forzar con allowDowngrade=true desde la UI.
            if (!allowDowngrade)
            {
                try
                {
                    var nodo = _registry.GetAll()?
                        .FirstOrDefault(n => string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase));
                    string current = nodo?.Firmware;
                    if (!string.IsNullOrEmpty(current) && IsDowngrade(current, version))
                    {
                        return (false, "", "",
                            "downgrade_bloqueado: nodo en " + current + ", pedido " + version);
                    }
                }
                catch (Exception ex)
                {
                    // Si el parse falla o el registry no responde, no bloqueamos —
                    // mejor permitir el OTA que dejar al usuario sin opción.
                    _log("OTA downgrade-check error: " + ex.Message);
                }
            }

            int port = _cfg != null && _cfg.FirmwareHttpPort > 0 ? _cfg.FirmwareHttpPort : 8088;

            // FlowX publica MQTT en `agp/flow/...` (topic prefix `flow`), por lo
            // que NodoRegistry guarda Tipo="Flow" → prodLo="flow". Pero los .bin
            // del producto se suben al cache con el nombre canónico ("flowx").
            // Resolvemos el nombre real del producto buscándolo en cache: probamos
            // prodLo primero y, si no aparece, su alias (sufijo "x" agregado o
            // removido). El resultado lo usamos para BuildFirmwareUrl y sha256 —
            // si lo dejábamos en "flow", el ESP32 pedía /firmware/flow/... y se
            // comía un 404 silencioso.
            string prodAlt = prodLo.EndsWith("x") ? prodLo.Substring(0, prodLo.Length - 1) : prodLo + "x";
            string prodCache = prodLo;
            string sha256 = null;
            try
            {
                string cacheDir = FirmwareMirror.ResolveCacheDir(_cfg);
                var local = FirmwareMirror.ListLocal(cacheDir);
                if (local != null)
                {
                    foreach (var f in local)
                    {
                        bool matchProd = string.Equals(f.producto, prodLo, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(f.producto, prodAlt, StringComparison.OrdinalIgnoreCase);
                        if (matchProd &&
                            string.Equals(f.version, version, StringComparison.OrdinalIgnoreCase))
                        {
                            prodCache = (f.producto ?? prodLo).ToLowerInvariant();
                            if (!string.IsNullOrEmpty(f.hash_sha256)) sha256 = f.hash_sha256;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { _log("OTA sha256 lookup error: " + ex.Message); }

            string url = FirmwareOtaClient.BuildFirmwareUrl(prodCache, version, port);
            // FlowX usa convención 5-part `agp/flow/<uid>/cmd/<verb>` (verbo en el
            // topic, no en el payload). El resto de los firmwares
            // (QuantiX/VistaX/StormX) usan 4-part `agp/{prod}/<uid>/cmd` con
            // `{cmd:"ota", ...}` en el payload. Sin este routing, el OTA a FlowX
            // se publica en 4-part y el firmware (suscripto a `cmd/+`) nunca lo ve.
            bool isFlow = (prodLo == "flow" || prodLo == "flowx");
            string topic = isFlow
                ? "agp/flow/" + uid + "/cmd/ota"
                : "agp/" + prodLo + "/" + uid + "/cmd";

            // En FlowX el verbo va en el topic — la clave `cmd` no hace falta
            // (la firmware ignora claves desconocidas, pero la omitimos por
            // higiene). En el resto, `cmd:"ota"` es el discriminador.
            var payload = new Dictionary<string, object>();
            if (!isFlow) payload["cmd"] = "ota";
            payload["url"]     = url;
            payload["version"] = version;
            if (!string.IsNullOrEmpty(sha256)) payload["sha256"] = sha256;

            // Optimistic update: marcamos "sent" antes de saber el resultado del broker.
            // Si la publicación falla, lo revertimos a "error".
            Upsert(uid, st =>
            {
                st.Producto = prodLo;
                st.Status = "sent";
                st.Version = version;
                st.Detalle = "Comando OTA enviado al nodo";
                st.Url = url;
                st.ProgressPct = 5;
                st.LastChangeUtc = DateTime.UtcNow;
            });

            // Tanda 2: publicar con envelope cmd_id+ttl=10s. TTL más largo para OTA
            // porque el firmware puede tardar 2-3s entre callback y publish del ack
            // (encola el OTA y vuelve al callback, pero el publish puede competir
            // con el announcement periódico). Si el firmware es legacy y no manda
            // ack, igual ejecuta el OTA — recibimos timeout pero el flash sigue.
            string err = null;
            global::AgroParallel.Services.Common.CmdAckResult ack = null;
            try { ack = await _registry.PublishCmdAsync(topic, uid, payload, ttlMs: 10000, source: "pilotx"); }
            catch (Exception ex) { err = ex.Message; }

            if (ack == null || (ack.Status == "publish_failed"))
            {
                Upsert(uid, st =>
                {
                    st.Status = "error";
                    st.Detalle = "publish-fallido" + (err == null ? "" : ": " + err);
                    st.ProgressPct = 0;
                    st.LastChangeUtc = DateTime.UtcNow;
                });
                return (false, url, topic, err ?? "publish-fallido");
            }

            // Ack rejected: el firmware aceptó el mensaje pero rechazó el cmd
            // (parametros_faltantes, safe_mode_active, expired, etc.) — UI debe
            // mostrar el detalle. No es lo mismo que "no llegó".
            if (string.Equals(ack.Status, "rejected", StringComparison.OrdinalIgnoreCase))
            {
                Upsert(uid, st =>
                {
                    st.Status = "error";
                    st.Detalle = "nodo-rechazo: " + (ack.Detail ?? "sin_detalle");
                    st.ProgressPct = 0;
                    st.LastChangeUtc = DateTime.UtcNow;
                });
                return (false, url, topic, ack.Detail ?? "rejected");
            }

            // Timeout: firmware legacy sin envelope, o no nos llegó el ack a tiempo.
            // El OTA puede haber empezado igual — dejamos optimistic update y el
            // /ota/resultado del flow normal terminará de actualizar el status.
            if (string.Equals(ack.Status, "timeout", StringComparison.OrdinalIgnoreCase))
            {
                // No bajamos a error — el OTA puede estar ejecutándose.
                Upsert(uid, st => { st.Detalle = "sin_ack_envelope_(fw_legacy?)"; st.LastChangeUtc = DateTime.UtcNow; });
            }
            return (true, url, topic, null);
        }

        /// <summary>
        /// Comando genérico al nodo (reiniciar, borrar_wifi, estado, ping, etc.).
        /// El firmware decide qué hace con cada cmd; el coordinator no valida.
        /// </summary>
        public async Task<(bool ok, string topic, string error)> SendCmdAsync(
            string producto, string uid, string cmd, IDictionary<string, object> extras = null)
        {
            if (string.IsNullOrWhiteSpace(producto)) return (false, "", "producto-vacio");
            if (string.IsNullOrWhiteSpace(uid))      return (false, "", "uid-vacio");
            if (string.IsNullOrWhiteSpace(cmd))      return (false, "", "cmd-vacio");

            string prodLo = producto.Trim().ToLowerInvariant();
            // FlowX usa 5-part `cmd/<verb>` (ver SendOtaAsync). El resto 4-part.
            bool isFlow = (prodLo == "flow" || prodLo == "flowx");
            string topic = isFlow
                ? "agp/flow/" + uid + "/cmd/" + cmd.Trim().ToLowerInvariant()
                : "agp/" + prodLo + "/" + uid + "/cmd";

            var payload = new Dictionary<string, object>();
            if (!isFlow) payload["cmd"] = cmd;
            if (extras != null)
                foreach (var kv in extras)
                    if (!payload.ContainsKey(kv.Key)) payload[kv.Key] = kv.Value;

            string json = JsonSerializer.Serialize(payload);
            try
            {
                bool ok = await _registry.PublishAsync(topic, json, retain: false);
                return (ok, topic, ok ? null : "publish-fallido");
            }
            catch (Exception ex)
            {
                return (false, topic, ex.Message);
            }
        }

        // ── Listener MQTT ────────────────────────────────────────────────────

        private void OnMqttMessage(object sender, MqttMessageReceivedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.Topic)) return;
            // Filtro estricto: agp/{producto}/{UID}/ota/resultado
            var parts = e.Topic.Split('/');
            if (parts.Length < 5) return;
            if (parts[0] != "agp" || parts[3] != "ota" || parts[4] != "resultado") return;

            string producto = parts[1];
            string uid = parts[2];
            if (string.IsNullOrEmpty(uid)) return;

            string status = null, version = null, detalle = null;
            try
            {
                using (var doc = JsonDocument.Parse(e.Payload ?? "{}"))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status",  out var s)) status  = s.GetString();
                    if (root.TryGetProperty("version", out var v)) version = v.GetString();
                    if (root.TryGetProperty("detalle", out var d)) detalle = d.GetString();
                }
            }
            catch { /* payload mal formado: dejamos status null y caemos al else */ }

            // Capturamos la versión previa ANTES de upsertear — para `version_anterior`
            // del log cloud. Si no había estado previo, lo dejamos null (el server
            // acepta el campo opcional).
            string versionAnterior = null;
            var prevSt = GetState(uid);
            if (prevSt != null && !string.IsNullOrEmpty(prevSt.Version) &&
                !string.Equals(prevSt.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                versionAnterior = prevSt.Version;
            }

            Upsert(uid, st =>
            {
                st.Producto = producto;
                st.Status = string.IsNullOrEmpty(status) ? "iniciando" : status;
                if (!string.IsNullOrEmpty(version)) st.Version = version;
                if (!string.IsNullOrEmpty(detalle)) st.Detalle = detalle;
                st.LastChangeUtc = DateTime.UtcNow;
                st.ProgressPct = MapPct(st.Status, st.ProgressPct);
            });

            // Cloud relay: solo en estados finales (ok|error). "iniciando"/"sent"
            // sirven para resetear el anti-duplicado — un OTA nuevo del mismo
            // nodo debe poder loguear de nuevo aunque la versión coincida.
            string statusLo = (status ?? "").ToLowerInvariant();
            if (statusLo == "iniciando" || statusLo == "sent")
            {
                _lastReportedKey.TryRemove(uid, out _);
                return;
            }
            if (statusLo != "ok" && statusLo != "error") return;

            // Anti-duplicado: el firmware puede republicar el mismo resultado
            // (retained / reanuncio). Clave = uid|version|status.
            string dedupKey = (version ?? "") + "|" + statusLo;
            string already;
            if (_lastReportedKey.TryGetValue(uid, out already) &&
                string.Equals(already, dedupKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _lastReportedKey[uid] = dedupKey;

            if (_cfg == null || !_cfg.Enabled) return;
            if (string.IsNullOrWhiteSpace(_cfg.ServerUrl)) return;

            // Fire-and-forget: no esperamos al cloud. Si OrbitX está offline o
            // tarda, el flow OTA local ya terminó — el log queda en memoria
            // (NodoOtaState) y se reintenta al próximo OTA. No relanzamos
            // excepciones para evitar romper el handler MQTT.
            string capturedProducto = producto;
            string capturedUid = uid;
            string capturedVersion = version;
            string capturedPrev = versionAnterior;
            string capturedStatus = statusLo;
            string capturedDetalle = detalle;
            _ = Task.Run(async () =>
            {
                try
                {
                    await FirmwareOtaCloudReporter.ReportAsync(
                        GetHttp(), _cfg,
                        capturedProducto, capturedUid, capturedVersion,
                        capturedPrev, capturedStatus, capturedDetalle, _log)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log("OTA cloud relay task error: " + ex.Message);
                }
            });
        }

        // El firmware reporta sólo "iniciando" y luego "ok" o "error". No tenemos
        // % real, sólo damos pasos visuales para que la UI no se vea congelada.
        private static int MapPct(string status, int prev)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case "sent":      return 5;
                case "iniciando": return Math.Max(prev, 25);
                case "ok":        return 100;
                case "error":     return 0;
                default:          return prev;
            }
        }

        // H1: parse semver "1.11.0" / "1.2.3-rc1" → System.Version (ignora suffix
        // alfanumérico). Devuelve true si `requested` < `current` estrictamente.
        // Tolera basura (devuelve false → no bloquea) — el caller ya logueó error.
        private static bool IsDowngrade(string current, string requested)
        {
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(requested)) return false;
            Version vc, vr;
            if (!TryParseLooseVersion(current, out vc)) return false;
            if (!TryParseLooseVersion(requested, out vr)) return false;
            return vr.CompareTo(vc) < 0;
        }

        private static bool TryParseLooseVersion(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            string trimmed = s.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
                trimmed = trimmed.Substring(1);
            // Cortar pre-release / build metadata: "1.2.3-rc1+abc" → "1.2.3"
            int dash = trimmed.IndexOfAny(new[] { '-', '+' });
            if (dash > 0) trimmed = trimmed.Substring(0, dash);
            return Version.TryParse(trimmed, out v);
        }

        // H2: barre el diccionario y degrada a "error timeout_sin_resultado" los
        // OTAs que llevan más de WATCHDOG_TIMEOUT_MS en sent/iniciando sin
        // resultado final. Catch global porque corre en thread del ThreadPool
        // y una excepción acá tumbaría el timer entero.
        private void WatchdogTick(object _state2)
        {
            try
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromMilliseconds(WATCHDOG_TIMEOUT_MS);
                foreach (var kv in _state.ToArray())
                {
                    var st = kv.Value;
                    if (st == null) continue;
                    string s; DateTime ts;
                    lock (st) { s = (st.Status ?? "").ToLowerInvariant(); ts = st.LastChangeUtc; }
                    if (s != "sent" && s != "iniciando") continue;
                    if (ts > cutoff) continue;

                    Upsert(kv.Key, x =>
                    {
                        x.Status = "error";
                        x.Detalle = "timeout_sin_resultado_5min";
                        x.ProgressPct = 0;
                        x.LastChangeUtc = DateTime.UtcNow;
                    });
                    _log("FirmwareOtaCoordinator watchdog: " + kv.Key + " timeout (stuck en " + s + ")");

                    // Relay al cloud para que quede log del fallo. Mismo patrón
                    // que OnMqttMessage: fire-and-forget bajo Task.Run.
                    if (_cfg != null && _cfg.Enabled && !string.IsNullOrWhiteSpace(_cfg.ServerUrl))
                    {
                        string capUid = kv.Key;
                        string capProd = st.Producto;
                        string capVer = st.Version;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await FirmwareOtaCloudReporter.ReportAsync(
                                    GetHttp(), _cfg,
                                    capProd, capUid, capVer,
                                    null, "error", "timeout_sin_resultado_5min", _log)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex) { _log("OTA watchdog cloud relay error: " + ex.Message); }
                        });
                    }
                }
            }
            catch (Exception ex) { _log("FirmwareOtaCoordinator watchdog tick error: " + ex.Message); }
        }

        private void Upsert(string uid, Action<NodoOtaState> mut)
        {
            var st = _state.GetOrAdd(uid, _ => new NodoOtaState
            {
                Uid = uid,
                Status = "idle",
                ProgressPct = 0,
                LastChangeUtc = DateTime.UtcNow
            });
            lock (st) mut(st);
        }

        public void Dispose() => Stop();
    }
}
