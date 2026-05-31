// ============================================================================
// NodosController.cs
// Endpoints REST del módulo Nodos:
//   GET    /api/nodos                    → registry MQTT crudo
//   GET    /api/nodos/diagnostic         → diag MQTT
//   POST   /api/nodos/wildcard           → captura wildcard "#" on/off
//   POST   /api/nodos/reconnect          → reconectar al broker
//   GET    /api/nodos/unified            → vista combinada curado + live
//   POST   /api/nodos/aceptar            { uid, tipo, alias }
//   POST   /api/nodos/ignorar            { uid }
//   POST   /api/nodos/restaurar          { uid }
//   POST   /api/nodos/renombrar          { uid, alias }
//   DELETE /api/nodos/{uid}              → quita del curado
//
// Detalle por nodo (consumido por /pages/nodo-detalle.html):
//   GET    /api/nodos/{uid}/estado       → matriz wifi/mqtt/target/status + meta
//   GET    /api/nodos/{uid}/firmwares    → versiones .bin disponibles en cache LAN
//   POST   /api/nodos/{uid}/ota          { version }
//   GET    /api/nodos/{uid}/ota/progress → último estado del OTA enviado
//   POST   /api/nodos/{uid}/cmd          { cmd, extras? } — reiniciar, borrar_wifi…
// ============================================================================

using AgroParallel.Models;
using AgroParallel.OrbitX;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class NodosController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly INodoRegistryService _registry;
        private readonly INodosCuratedService _curated;
        private readonly FirmwareOtaCoordinator _ota;
        private readonly IOrbitXConfigService _orbitxCfg;
        private readonly IImplementoService _implemento;

        public NodosController(INodoRegistryService registry,
                               INodosCuratedService curated,
                               FirmwareOtaCoordinator ota = null,
                               IOrbitXConfigService orbitxCfg = null,
                               IImplementoService implemento = null)
        {
            _registry = registry;
            _curated = curated;
            _ota = ota;
            _orbitxCfg = orbitxCfg;
            _implemento = implemento;
        }

        // ---- helper: marca DelImplementoActivo en la lista unificada según ImplementoDto.NodosUids ----
        private void MarcarImplementoActivo(IReadOnlyList<NodoUnifiedDto> nodos)
        {
            if (nodos == null || nodos.Count == 0 || _implemento == null) return;
            HashSet<string> uids = null;
            try
            {
                var dto = _implemento.GetImplemento();
                if (dto != null && dto.NodosUids != null)
                    uids = new HashSet<string>(dto.NodosUids, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            if (uids == null || uids.Count == 0) return;
            foreach (var n in nodos)
            {
                if (n != null && !string.IsNullOrEmpty(n.Uid) && uids.Contains(n.Uid))
                    n.DelImplementoActivo = true;
            }
        }

        [Route(HttpVerbs.Get, "/nodos")]
        public object GetAll()
        {
            if (_registry == null)
                return new { ok = false, nodos = new List<NodoStatus>(), brokerConnected = false, error = "service-unavailable" };
            var list = _registry.GetAll();
            bool brokerConnected = false;
            try { brokerConnected = _registry.GetDiagnostic().Connected; } catch { }
            return new { ok = true, count = list.Count, nodos = list, brokerConnected = brokerConnected };
        }

        [Route(HttpVerbs.Get, "/nodos/diagnostic")]
        public object Diagnostic()
        {
            if (_registry == null)
                return new { ok = false, error = "service-unavailable" };
            return new { ok = true, diag = _registry.GetDiagnostic() };
        }

        [Route(HttpVerbs.Post, "/nodos/wildcard")]
        public async Task<object> SetWildcard([QueryField] bool on)
        {
            if (_registry == null) return new { ok = false, error = "service-unavailable" };
            var ok = await _registry.SetWildcardCaptureAsync(on);
            return new { ok };
        }

        [Route(HttpVerbs.Post, "/nodos/reconnect")]
        public async Task<object> Reconnect()
        {
            if (_registry == null) return new { ok = false, error = "service-unavailable" };
            var ok = await _registry.ReconnectAsync();
            return new { ok, diag = _registry.GetDiagnostic() };
        }

        // ---------- vista curada ----------

        [Route(HttpVerbs.Get, "/nodos/unified")]
        public object GetUnified()
        {
            if (_curated == null) return new { ok = false, error = "service-unavailable", nodos = new List<NodoUnifiedDto>() };
            var list = _curated.GetUnified(_registry);
            MarcarImplementoActivo(list);
            bool brokerConnected = false;
            try { if (_registry != null) brokerConnected = _registry.GetDiagnostic().Connected; } catch { }
            string implementoSlug = "";
            try { if (_implemento != null) implementoSlug = _implemento.GetActiveSlug(); } catch { }
            return new { ok = true, count = list.Count, nodos = list, brokerConnected, implementoSlug };
        }

        // ---------- vínculo nodo ↔ implemento activo ----------

        public sealed class AsignacionBody { public string uid { get; set; } public bool asignado { get; set; } }

        /// <summary>
        /// Asocia / desasocia un nodo aceptado al implemento ACTIVO. Los nodos
        /// listados acá son los que disparan alarma cuando caen offline. Si un
        /// nodo está aceptado pero NO en esta lista (porque vive en otro implemento
        /// que ahora no está montado), su offline se ignora silenciosamente.
        /// </summary>
        [Route(HttpVerbs.Post, "/nodos/asignacion-implemento")]
        public async Task<object> AsignacionImplemento()
        {
            if (_implemento == null) return new { ok = false, error = "service-unavailable" };
            var body = await ReadBody<AsignacionBody>();
            if (body == null || string.IsNullOrEmpty(body.uid)) return new { ok = false, error = "invalid-body" };

            string slug = _implemento.GetActiveSlug();
            if (string.IsNullOrEmpty(slug)) return new { ok = false, error = "no-active-implemento" };

            string uid = body.uid.Trim();
            bool sinCambios = false;
            // RMW atómico — sin esto, dos AsignacionImplemento concurrentes
            // (UI desktop + móvil) pisan los cambios del otro: ambos leen la
            // lista vieja, ambos toggle, último Save gana.
            var result = _implemento.Update(slug, dto =>
            {
                if (dto.NodosUids == null) dto.NodosUids = new List<string>();
                bool present = dto.NodosUids.Any(u => string.Equals(u, uid, StringComparison.OrdinalIgnoreCase));
                if (body.asignado && !present) dto.NodosUids.Add(uid);
                else if (!body.asignado && present)
                    dto.NodosUids.RemoveAll(u => string.Equals(u, uid, StringComparison.OrdinalIgnoreCase));
                else sinCambios = true;
            });

            if (sinCambios) return new { ok = true, slug = slug, asignado = body.asignado, sin_cambios = true };
            // Si acabamos de asignar un nodo nuevo al implemento activo, encendemos
            // el overlay del producto correspondiente si estaba apagado — misma lógica
            // que cuando se cambia de implemento. Solo en alta (asignado=true).
            if (body.asignado && result != null)
                AgroParallel.Services.OverlayAutoOpener.EnsureForActiveImplemento(_implemento, _curated);
            return new { ok = result != null, slug = slug, asignado = body.asignado };
        }

        public sealed class AceptarBody { public string uid { get; set; } public string tipo { get; set; } public string alias { get; set; } }
        public sealed class UidBody { public string uid { get; set; } }
        public sealed class RenombrarBody { public string uid { get; set; } public string alias { get; set; } }

        [Route(HttpVerbs.Post, "/nodos/aceptar")]
        public async Task<object> Aceptar()
        {
            if (_curated == null) return new { ok = false, error = "service-unavailable" };
            var body = await ReadBody<AceptarBody>();
            if (body == null || string.IsNullOrEmpty(body.uid)) return new { ok = false, error = "invalid-body" };
            _curated.Aceptar(body.uid, body.tipo, body.alias);

            // Auto-asignación al implemento ACTIVO: si el operario acepta un nodo
            // estando en el implemento X, casi siempre quiere que ese nodo dispare
            // alarma si cae offline. Lo agregamos atomicamente. Si después resulta
            // ser de otro implemento, puede quitarlo con el pin "📌 En implemento".
            bool autoAsignado = false;
            string slugActivo = "";
            if (_implemento != null)
            {
                try
                {
                    slugActivo = _implemento.GetActiveSlug() ?? "";
                    if (!string.IsNullOrEmpty(slugActivo))
                    {
                        string uid = body.uid.Trim();
                        // RMW atómico — Aceptar puede correr concurrente con
                        // AsignacionImplemento o Eliminar; el lock del service
                        // serializa el toggle.
                        _implemento.Update(slugActivo, dto =>
                        {
                            if (dto.NodosUids == null) dto.NodosUids = new List<string>();
                            bool present = dto.NodosUids.Any(u => string.Equals(u, uid, StringComparison.OrdinalIgnoreCase));
                            if (!present)
                            {
                                dto.NodosUids.Add(uid);
                                autoAsignado = true;
                            }
                        });
                    }
                }
                catch { /* no bloqueamos la aceptación si el implemento falla */ }
            }
            return new { ok = true, auto_asignado = autoAsignado, implemento_slug = slugActivo };
        }

        [Route(HttpVerbs.Post, "/nodos/ignorar")]
        public async Task<object> Ignorar()
        {
            if (_curated == null) return new { ok = false, error = "service-unavailable" };
            var body = await ReadBody<UidBody>();
            if (body == null || string.IsNullOrEmpty(body.uid)) return new { ok = false, error = "invalid-body" };
            _curated.Ignorar(body.uid);
            return new { ok = true };
        }

        [Route(HttpVerbs.Post, "/nodos/restaurar")]
        public async Task<object> Restaurar()
        {
            if (_curated == null) return new { ok = false, error = "service-unavailable" };
            var body = await ReadBody<UidBody>();
            if (body == null || string.IsNullOrEmpty(body.uid)) return new { ok = false, error = "invalid-body" };
            _curated.Restaurar(body.uid);
            return new { ok = true };
        }

        [Route(HttpVerbs.Post, "/nodos/renombrar")]
        public async Task<object> Renombrar()
        {
            if (_curated == null) return new { ok = false, error = "service-unavailable" };
            var body = await ReadBody<RenombrarBody>();
            if (body == null || string.IsNullOrEmpty(body.uid) || string.IsNullOrEmpty(body.alias))
                return new { ok = false, error = "invalid-body" };
            _curated.Renombrar(body.uid, body.alias);
            return new { ok = true };
        }

        [Route(HttpVerbs.Delete, "/nodos/{uid}")]
        public object Eliminar(string uid)
        {
            if (_curated == null) return new { ok = false, error = "service-unavailable" };
            if (string.IsNullOrEmpty(uid)) return new { ok = false, error = "invalid-uid" };
            _curated.Eliminar(uid);

            // Limpia el UID del implemento ACTIVO si estaba asignado (evita huérfanos
            // en nodos_uids). Otros implementos pueden quedar con el UID — al accept
            // futuro reaparecerá pendiente y será reasignable.
            if (_implemento != null)
            {
                try
                {
                    string slugActivo = _implemento.GetActiveSlug();
                    if (!string.IsNullOrEmpty(slugActivo))
                    {
                        // RMW atómico — sin lock dos Eliminar concurrentes o
                        // Eliminar contra Aceptar pisan la lista. Update con
                        // Sanitize ya dedup case-insensitive.
                        _implemento.Update(slugActivo, dto =>
                        {
                            if (dto.NodosUids != null)
                                dto.NodosUids.RemoveAll(u => string.Equals(u, uid, StringComparison.OrdinalIgnoreCase));
                        });
                    }
                }
                catch { }
            }
            return new { ok = true };
        }

        // =====================================================================
        // Detalle por nodo
        // =====================================================================

        // Umbrales de la matriz de estado (segundos sin señal).
        // - "online" del registry usa 30s (LastSeenUtc).
        // - El "status_out" más fino que mostramos en la matriz usa 3s, que es
        //   el watchdog típico para "el nodo está activo ahora" sobre status_live.
        private const int OnlineThresholdSec = 30;
        private const int StatusFreshSec = 3;

        /// <summary>
        /// Estado runtime detallado de un nodo: ubicación en la matriz
        /// (wifi/mqtt/target/status), última señal, ip, fw, OTA en curso si
        /// hay, y comandos disponibles según el tipo.
        /// </summary>
        [Route(HttpVerbs.Get, "/nodos/{uid}/estado")]
        public object Estado(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return new { ok = false, error = "invalid-uid" };
            if (_registry == null || _curated == null)
                return new { ok = false, error = "service-unavailable" };

            // Combina datos del registry (telemetría runtime) con el curado (alias/estado)
            // a través del DTO unificado, así no duplicamos lógica de "estado pendiente/offline".
            var unified = _curated.GetUnified(_registry);
            MarcarImplementoActivo(unified);
            var u = unified.FirstOrDefault(n => string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase));
            if (u == null) return new { ok = false, error = "not-found" };

            // Last seen → segundos. Si no parsea ISO, dejamos null (UI lo trata como ∞).
            int? lastSeenSec = null;
            if (DateTime.TryParse(u.LastSeenUtc, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var lastSeen))
            {
                lastSeenSec = (int)Math.Max(0, (DateTime.UtcNow - lastSeen).TotalSeconds);
            }

            // Estado del broker (necesario para diferenciar "broker caído" de
            // "nodo caído"): si el broker no está conectado, todos los nodos
            // se ven offline aunque estén vivos en LAN.
            bool brokerConnected = false;
            try { brokerConnected = _registry.GetDiagnostic().Connected; } catch { }

            // Resolución de la matriz:
            //   wifi      ok | down | unknown  (down = sin señal hace mucho; AP fallback es indistinguible desde acá)
            //   mqtt      ok | down            (= u.Online && broker conectado)
            //   targetIn  ok | down | na       (na para nodos sensoriales sin target loop)
            //   statusOut ok | stale | down    (ok = LastSeen < StatusFreshSec)
            string mqtt = (u.Online && brokerConnected) ? "ok" : "down";
            string wifi = u.Online ? "ok" : (lastSeenSec.HasValue ? "down" : "unknown");

            string tipoLo = (u.Tipo ?? "").ToLowerInvariant();
            bool tieneTargetLoop = tipoLo.Contains("quantix")
                                || tipoLo.Contains("sectionx")
                                || tipoLo.Contains("flowx");
            string targetIn = !tieneTargetLoop
                ? "na"
                : (mqtt == "ok" ? "ok" : "down");

            string statusOut;
            if (!u.Online) statusOut = "down";
            else if (lastSeenSec.HasValue && lastSeenSec.Value <= StatusFreshSec) statusOut = "ok";
            else statusOut = "stale";

            // Mapeo de las 6 filas de la matriz documental → fila activa.
            string rowKey = ResolveMatrixRow(wifi, mqtt, targetIn, statusOut, brokerConnected);

            // OTA en curso (si hay).
            NodoOtaState ota = _ota?.GetState(uid);

            // Comandos disponibles según firmware:
            //   QuantiX/VistaX → reiniciar, borrar_wifi, estado, ota
            //   Otros          → ota (default conservador)
            bool soportaWifiReset = tipoLo.Contains("quantix") || tipoLo.Contains("vistax");
            var cmds = new List<string> { "estado", "ota" };
            cmds.Insert(0, "reiniciar");
            if (soportaWifiReset) cmds.Add("borrar_wifi");
            // Tanda 2: si el nodo está en safe-mode, exponemos clear_safe_mode como
            // comando disponible. La UI sólo lo muestra cuando u.SafeMode == true.
            if (u.SafeMode) cmds.Add("clear_safe_mode");

            // Tanda 2 #8 — desired/reported. Snapshot del tracker para este uid.
            // Si el nodo no tiene config syncing (no se le publicó desired desde
            // que arrancó el Hub), el tracker devuelve null → omitimos el campo.
            object configSync = null;
            try
            {
                var sync = _registry != null ? _registry.ConfigSync : null;
                var entry = sync != null ? sync.GetByUid(u.Uid) : null;
                if (entry != null)
                {
                    configSync = new
                    {
                        status = entry.Status.ToString().ToLowerInvariant(),
                        desired_hash = entry.DesiredHash,
                        desired_ts_utc = entry.DesiredTsUtc,
                        reported_hash = entry.ReportedHash,
                        reported_ts_utc = entry.ReportedTsUtc
                    };
                }
            }
            catch { }

            return new
            {
                ok = true,
                uid = u.Uid,
                tipo = u.Tipo,
                alias = u.Alias,
                estado_curado = u.Estado,
                ip = u.Ip,
                firmware = u.Firmware,
                online = u.Online,
                last_seen_utc = u.LastSeenUtc,
                last_seen_sec = lastSeenSec,
                broker_connected = brokerConnected,
                // Pertenencia al implemento activo + último reset (badge ⚠ si anormal).
                del_implemento_activo = u.DelImplementoActivo,
                boot_reason = u.BootReason,
                // Tanda 2: safe-mode + crash_count para mostrar pill + botón reset en UI.
                safe_mode = u.SafeMode,
                crash_count = u.CrashCount,
                matriz = new
                {
                    wifi,
                    mqtt,
                    target_in = targetIn,
                    status_out = statusOut,
                    row = rowKey
                },
                ota = ota == null ? null : new
                {
                    status = ota.Status,
                    version = ota.Version,
                    detalle = ota.Detalle,
                    progress_pct = ota.ProgressPct,
                    url = ota.Url,
                    last_change_utc = ota.LastChangeUtc.ToString("o")
                },
                comandos_disponibles = cmds,
                config_sync = configSync,
                umbrales = new
                {
                    online_sec = OnlineThresholdSec,
                    status_fresh_sec = StatusFreshSec
                }
            };
        }

        // Cada fila de la matriz documental tiene una clave única que la UI usa
        // para resaltar la línea correspondiente. Las claves coinciden con el
        // CSS de nodo-detalle.html.
        private static string ResolveMatrixRow(string wifi, string mqtt, string targetIn, string statusOut, bool brokerConnected)
        {
            if (!brokerConnected) return "broker-down";          // broker apagado
            if (wifi != "ok" && mqtt != "ok") return "wifi-off"; // apagado / fuera de LAN
            if (wifi == "ok" && mqtt != "ok") return "wifi-ok-mqtt-down";
            if (targetIn == "down" && statusOut == "ok") return "rx-ok-target-no";
            if (targetIn == "ok" && statusOut != "ok") return "rx-no-target-ok";
            return "ok-pleno";
        }

        /// <summary>
        /// Lista de firmwares disponibles en el cache local FilesystemServer
        /// para el producto correspondiente al nodo. Se ordena por versión
        /// descendente (semver lex).
        /// </summary>
        [Route(HttpVerbs.Get, "/nodos/{uid}/firmwares")]
        public object Firmwares(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return new { ok = false, error = "invalid-uid" };
            if (_registry == null || _curated == null)
                return new { ok = false, error = "service-unavailable" };

            var unified = _curated.GetUnified(_registry);
            var u = unified.FirstOrDefault(n => string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase));
            if (u == null) return new { ok = false, error = "not-found" };

            string prodLo = (u.Tipo ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(prodLo)) return new { ok = false, error = "tipo-vacio" };

            OrbitXConfig cfg = SafeLoadOrbitX();
            string cacheDir = FirmwareMirror.ResolveCacheDir(cfg);
            var all = FirmwareMirror.ListLocal(cacheDir) ?? new List<FirmwareCatalogItem>();

            var versiones = all
                .Where(f => string.Equals(f.producto, prodLo, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.version, StringComparer.OrdinalIgnoreCase)
                .Select(f => new
                {
                    version = f.version,
                    hash_sha256 = f.hash_sha256,
                    tamano_bytes = f.tamano_bytes,
                    changelog = f.changelog,
                    es_actual = string.Equals(f.version, u.Firmware, StringComparison.OrdinalIgnoreCase)
                })
                .ToList();

            int port = cfg != null && cfg.FirmwareHttpPort > 0 ? cfg.FirmwareHttpPort : 8088;
            string lan = FirmwareOtaClient.ResolveLanIp();

            return new
            {
                ok = true,
                uid = u.Uid,
                producto = prodLo,
                firmware_actual = u.Firmware,
                http_port = port,
                lan_ip = lan,
                versiones
            };
        }

        public sealed class OtaBody { public string version { get; set; } public bool allow_downgrade { get; set; } }

        /// <summary>Dispara OTA contra el nodo con la versión indicada.</summary>
        [Route(HttpVerbs.Post, "/nodos/{uid}/ota")]
        public async Task<object> OtaSend(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return new { ok = false, error = "invalid-uid" };
            if (_ota == null || _registry == null || _curated == null)
                return new { ok = false, error = "service-unavailable" };

            var body = await ReadBody<OtaBody>();
            if (body == null || string.IsNullOrEmpty(body.version))
                return new { ok = false, error = "invalid-body" };

            var unified = _curated.GetUnified(_registry);
            var u = unified.FirstOrDefault(n => string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase));
            if (u == null) return new { ok = false, error = "not-found" };

            string prodLo = (u.Tipo ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(prodLo)) return new { ok = false, error = "tipo-vacio" };

            var (ok, url, topic, err) = await _ota.SendOtaAsync(prodLo, u.Uid, body.version, body.allow_downgrade);
            return new { ok, url, topic, error = err };
        }

        /// <summary>
        /// Último estado conocido del OTA para ese UID. Útil para polling desde
        /// la UI mientras dura la actualización (no abrimos SSE, el firmware
        /// publica tres estados como mucho).
        /// </summary>
        [Route(HttpVerbs.Get, "/nodos/{uid}/ota/progress")]
        public object OtaProgress(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return new { ok = false, error = "invalid-uid" };
            if (_ota == null) return new { ok = false, error = "service-unavailable" };
            var st = _ota.GetState(uid);
            if (st == null) return new { ok = true, uid, ota = (object)null };
            return new
            {
                ok = true,
                uid,
                ota = new
                {
                    status = st.Status,
                    version = st.Version,
                    detalle = st.Detalle,
                    progress_pct = st.ProgressPct,
                    url = st.Url,
                    last_change_utc = st.LastChangeUtc.ToString("o")
                }
            };
        }

        public sealed class CmdBody { public string cmd { get; set; } public Dictionary<string, object> extras { get; set; } }

        /// <summary>
        /// Envía un comando genérico al nodo (reiniciar, borrar_wifi, estado…).
        /// El firmware acepta lo que entienda y descarta el resto.
        /// </summary>
        [Route(HttpVerbs.Post, "/nodos/{uid}/cmd")]
        public async Task<object> Cmd(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return new { ok = false, error = "invalid-uid" };
            if (_ota == null || _registry == null || _curated == null)
                return new { ok = false, error = "service-unavailable" };

            var body = await ReadBody<CmdBody>();
            if (body == null || string.IsNullOrEmpty(body.cmd))
                return new { ok = false, error = "invalid-body" };

            // Lista corta de comandos válidos. "ota" tiene endpoint propio porque
            // necesita armar URL + tracking — no permitirlo por acá evita
            // disparos accidentales sin el tracking del coordinator.
            var validos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "reiniciar", "borrar_wifi", "estado", "ping",
                // Tanda 2: reset del safe-mode persistido. El nodo cleará crash_count
                // en NVS y volverá a aceptar OTA/calibración. Si el bug que causó los
                // crashes sigue, va a volver a entrar en safe-mode tras 3 reboots.
                "clear_safe_mode"
            };
            if (!validos.Contains(body.cmd))
                return new { ok = false, error = "cmd-no-soportado", cmd = body.cmd };

            var unified = _curated.GetUnified(_registry);
            var u = unified.FirstOrDefault(n => string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase));
            if (u == null) return new { ok = false, error = "not-found" };

            string prodLo = (u.Tipo ?? "").Trim().ToLowerInvariant();
            var (ok, topic, err) = await _ota.SendCmdAsync(prodLo, u.Uid, body.cmd, body.extras);
            return new { ok, topic, cmd = body.cmd, error = err };
        }

        private OrbitXConfig SafeLoadOrbitX()
        {
            try { return OrbitXConfig.Load(); } catch { return new OrbitXConfig(); }
        }

        private async Task<T> ReadBody<T>() where T : class
        {
            try
            {
                string body;
                using (var sr = new StreamReader(HttpContext.Request.InputStream))
                    body = await sr.ReadToEndAsync();
                if (string.IsNullOrEmpty(body)) return null;
                return JsonSerializer.Deserialize<T>(body, JsonOpts);
            }
            catch { return null; }
        }
    }
}
