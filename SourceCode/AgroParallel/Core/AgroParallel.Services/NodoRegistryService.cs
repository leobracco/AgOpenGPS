// ============================================================================
// NodoRegistryService.cs
// Implementación de INodoRegistryService — usa MQTTnet directamente para
// suscribirse a:
//   agp/+/+/announcement   (retained por-UID)
//   agp/+/+/status_live    (red de seguridad)
// Mantiene un Dictionary<UID, NodoStatus> y marca offline a >30s sin tráfico.
// Reemplaza la lógica embebida en FormNodos.cs (legacy).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.Common;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.Services
{
    public sealed class NodoRegistryService : INodoRegistryService, IDisposable
    {
        private readonly Dictionary<string, NodoStatus> _nodos =
            new Dictionary<string, NodoStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        private IMqttClient _client;
        private Timer _staleTimer;
        private bool _running;
        private readonly HashSet<string> _extraSubs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Diagnóstico: broker actual, ring buffer de mensajes recientes, wildcard "#"
        private string _brokerAddress;
        private int _brokerPort;
        private bool _wildcardCaptureOn;
        private const int RecentMessagesCapacity = 50;
        private readonly LinkedList<NodoMqttMessage> _recentMessages =
            new LinkedList<NodoMqttMessage>();
        private string _lastError;          // mensaje amigable (es)
        private string _lastErrorCode;      // "AGP-MQTT-001" etc.
        private string _lastErrorTechnical; // tipo+mensaje crudo (para soporte)
        private DateTime? _lastErrorUtc;
        private DateTime? _lastConnectedUtc;
        private int _connectAttempts;
        private Timer _reconnectTimer;
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);

        // Tanda 2 industrial doctrine #3: comandos con cmd_id + ttl + ack.
        // Rastreador de ack a comandos publicados con envelope (PublishCmdAsync).
        // Se llena vía HandleMessage cuando llegan mensajes a agp/+/+/ack.
        private readonly AgpCmdTracker _cmdTracker = new AgpCmdTracker();

        /// <summary>Tracker de ack para comandos con envelope. Lo usa FirmwareOtaCoordinator y bridges.</summary>
        public AgpCmdTracker CmdTracker { get { return _cmdTracker; } }

        // Tanda 2 industrial doctrine #7: schema + seq monotónico.
        // Detector de gaps en `seq` por (uid, schema). Informativo, no alarma.
        private readonly AgpSeqTracker _seqTracker = new AgpSeqTracker();

        /// <summary>Tracker de gaps en seq. Expone los últimos gaps para diagnóstico.</summary>
        public AgpSeqTracker SeqTracker { get { return _seqTracker; } }

        // Tanda 2 industrial doctrine #8: desired/reported config split.
        // PC publica a agp/{prod}/{uid}/config/desired (retained); firmware
        // echa back a agp/{prod}/{uid}/config/reported (retained, tras aplicar).
        // El tracker mantiene los hashes + timestamps de ambos por uid.
        private readonly AgpConfigSyncTracker _configSync = new AgpConfigSyncTracker();

        /// <summary>Tracker de sincronización desired/reported por uid.</summary>
        public AgpConfigSyncTracker ConfigSync { get { return _configSync; } }

        public event EventHandler Changed;
        public event EventHandler<MqttMessageReceivedEventArgs> MessageReceived;

        public void Start(string brokerAddress, int brokerPort)
        {
            if (_running) return;
            if (string.IsNullOrWhiteSpace(brokerAddress)) return;
            if (brokerPort <= 0) brokerPort = 1883;

            _brokerAddress = brokerAddress;
            _brokerPort = brokerPort;
            _running = true;
            _ = ConnectOnceAsync();
            _staleTimer = new Timer(_ => MarkStale(), null, 5000, 5000);
            // Auto-reconnect: cada 5s, si no estamos conectados, intentamos de nuevo.
            // Útil cuando CoreX se levanta DESPUÉS del Hub.
            _reconnectTimer = new Timer(_ => { _ = TryReconnectIfDown(); }, null, 5000, 5000);
        }

        private async Task TryReconnectIfDown()
        {
            if (!_running) return;
            var c = _client;
            if (c != null && c.IsConnected) return;
            await ConnectOnceAsync().ConfigureAwait(false);
        }

        public async Task<bool> ReconnectAsync()
        {
            await ConnectOnceAsync().ConfigureAwait(false);
            var c = _client;
            return c != null && c.IsConnected;
        }

        private async Task ConnectOnceAsync()
        {
            // Evita conexiones concurrentes (timer + manual + Start).
            if (!await _connectGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (string.IsNullOrEmpty(_brokerAddress)) return;
                if (_client != null && _client.IsConnected) return;

                _connectAttempts++;

                // Si había cliente viejo, descartarlo limpiamente.
                try
                {
                    if (_client != null)
                    {
                        if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false);
                        _client.Dispose();
                    }
                }
                catch { }
                _client = null;

                var factory = new MqttFactory();
                var client = factory.CreateMqttClient();

                client.ApplicationMessageReceivedAsync += e =>
                {
                    try
                    {
                        var topic = e.ApplicationMessage.Topic ?? "";
                        var payload = e.ApplicationMessage.PayloadSegment.Count > 0
                            ? Encoding.UTF8.GetString(
                                e.ApplicationMessage.PayloadSegment.Array,
                                e.ApplicationMessage.PayloadSegment.Offset,
                                e.ApplicationMessage.PayloadSegment.Count)
                            : "";
                        RecordRecent(topic, payload);
                        HandleMessage(topic, payload);
                        try { MessageReceived?.Invoke(this, new MqttMessageReceivedEventArgs(topic, payload)); }
                        catch { }
                    }
                    catch { }
                    return Task.CompletedTask;
                };

                var options = new MqttClientOptionsBuilder()
                    .WithClientId("AgpHub_NodoRegistry_" + Guid.NewGuid().ToString("N").Substring(0, 8))
                    .WithTcpServer(_brokerAddress, _brokerPort)
                    .WithCleanSession(true)
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                    .WithTimeout(TimeSpan.FromSeconds(5))
                    .Build();

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                {
                    await client.ConnectAsync(options, cts.Token).ConfigureAwait(false);
                }

                // Re-suscribir todo: defaults + extras + wildcard si estaba ON.
                await client.SubscribeAsync("agp/+/+/announcement").ConfigureAwait(false);
                await client.SubscribeAsync("agp/+/+/status_live").ConfigureAwait(false);
                // LWT (Last Will Testament) — los firmwares lo publican con
                // {"online":false,"reason":"..."} como will retained. El broker
                // dispara este mensaje al detectar caída TCP. Suscribirse acá
                // permite marcar el nodo offline en <15s (keepAlive del firmware)
                // en vez de los 15s del MarkStale + propagación, dando detección
                // casi instantánea de cuelgues/brownouts en el tractor.
                await client.SubscribeAsync("agp/+/+/lwt").ConfigureAwait(false);
                // Tanda 2: ack a comandos con envelope. El firmware publica
                // {cmd_id,status,detail,ts_ms} en agp/{prod}/{uid}/ack. El
                // tracker liga ese cmd_id con el Task del bridge que esperaba.
                await client.SubscribeAsync("agp/+/+/ack").ConfigureAwait(false);
                // Tanda 2 #8: desired/reported. Suscribimos a AMBOS para
                // poder repoblar el tracker al arrancar el Hub (los topics son
                // retained). En desired echamos lo que nosotros mismos publicamos;
                // reported es lo que el firmware confirma tras aplicar.
                await client.SubscribeAsync("agp/+/+/config/desired").ConfigureAwait(false);
                await client.SubscribeAsync("agp/+/+/config/reported").ConfigureAwait(false);

                List<string> extras;
                lock (_extraSubs) extras = new List<string>(_extraSubs);
                foreach (var s in extras)
                {
                    try { await client.SubscribeAsync(s).ConfigureAwait(false); } catch { }
                }
                if (_wildcardCaptureOn)
                {
                    try { await client.SubscribeAsync("#").ConfigureAwait(false); } catch { }
                }

                _client = client;
                _lastConnectedUtc = DateTime.UtcNow;
                _lastError = null;
                _lastErrorCode = null;
                _lastErrorTechnical = null;
                _lastErrorUtc = null;
            }
            catch (Exception ex)
            {
                // Mapeo amigable + código corto + detalle técnico aparte.
                // El operario ve algo legible; el bot/soporte recibe el código.
                var mapped = AgpErrorMapper.FromException(ex);
                _lastError = mapped.Friendly;
                _lastErrorCode = mapped.Code;
                _lastErrorTechnical = mapped.Technical;
                _lastErrorUtc = DateTime.UtcNow;
            }
            finally
            {
                try { _connectGate.Release(); } catch { }
            }
        }

        public async Task<bool> SubscribeAsync(string topicFilter)
        {
            if (string.IsNullOrEmpty(topicFilter)) return false;
            lock (_extraSubs)
            {
                if (!_extraSubs.Add(topicFilter))
                {
                    // ya estaba; aún así intentamos suscribir por si reconectamos
                }
            }
            var c = _client;
            if (c == null || !c.IsConnected) return false;
            try
            {
                await c.SubscribeAsync(topicFilter).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        public async Task<bool> PublishAsync(string topic, string payload, bool retain)
        {
            var c = _client;
            if (c == null || !c.IsConnected) return false;
            if (string.IsNullOrEmpty(topic)) return false;
            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload ?? "")
                    .WithRetainFlag(retain)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                await c.PublishAsync(msg, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Tanda 2: publica un comando con envelope (cmd_id + ttl + ack). Wrap-ea
        /// el payload con un campo `_meta` aditivo y registra el cmd_id en el
        /// tracker. El Task resuelve cuando llega el ack del firmware en
        /// `agp/{prod}/{uid}/ack` o cuando vence el ttl.
        ///
        /// Firmwares viejos (sin parser de envelope) ignoran `_meta` y procesan
        /// el resto del payload normal — el bridge recibirá un timeout, pero el
        /// comando habrá sido ejecutado igual. Por eso este método es additivo
        /// y se puede desplegar en PC antes de actualizar firmwares.
        /// </summary>
        /// <param name="topic">Ej: "agp/quantix/QX-AA11/cmd"</param>
        /// <param name="uid">UID del nodo (para tracker bookkeeping).</param>
        /// <param name="payload">Diccionario con los campos cmd/url/etc.</param>
        /// <param name="ttlMs">Validez del comando. Default 5000.</param>
        /// <param name="source">Origen lógico. Default "pilotx".</param>
        public async Task<CmdAckResult> PublishCmdAsync(
            string topic, string uid,
            System.Collections.Generic.IDictionary<string, object> payload,
            int ttlMs = 5000, string source = "pilotx")
        {
            if (string.IsNullOrEmpty(topic))
                return new CmdAckResult { Ok = false, Status = "error", Detail = "topic_vacio" };

            var env = AgpEnvelope.NewCmd(ttlMs, source);
            string json = AgpEnvelope.SerializeWithEnvelope(env, payload);

            // Registrar ANTES de publicar — si el ack es ultrarrápido, no
            // queremos perderlo por carrera.
            var ackTask = _cmdTracker.TrackAsync(uid, env.CmdId, ttlMs);

            bool published = false;
            try { published = await PublishAsync(topic, json, retain: false).ConfigureAwait(false); }
            catch { }

            if (!published)
            {
                // Liberar el slot del tracker con un ack sintético "publish_failed".
                try { _cmdTracker.OnAckReceived(env.CmdId, "publish_failed", "broker_no_acepto_publish"); }
                catch { }
                return new CmdAckResult { Ok = false, Status = "publish_failed", Detail = "broker_no_acepto_publish" };
            }

            return await ackTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Extractor manual de un campo string en JSON (sin instanciar JsonDocument).
        /// Usado en el hot path del listener para acks. Soporta valores con escapes
        /// simples; NO soporta valores con \" embebido — los detail/status no lo necesitan.
        /// </summary>
        private static string ExtractJsonStringField(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i + needle.Length);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        public NodoDiagInfo GetDiagnostic()
        {
            var info = new NodoDiagInfo
            {
                BrokerAddress = _brokerAddress ?? "",
                BrokerPort = _brokerPort,
                Connected = _client != null && _client.IsConnected,
                WildcardCaptureOn = _wildcardCaptureOn,
                Subscriptions = new List<string> { "agp/+/+/announcement", "agp/+/+/status_live", "agp/+/+/lwt", "agp/+/+/ack", "agp/+/+/config/desired", "agp/+/+/config/reported" },
                RecentMessages = new List<NodoMqttMessage>(RecentMessagesCapacity),
                KnownNodesCount = 0,
                LastError = _lastError,
                LastErrorCode = _lastErrorCode,
                LastErrorTechnical = _lastErrorTechnical,
                LastErrorUtc = _lastErrorUtc,
                LastConnectedUtc = _lastConnectedUtc,
                ConnectAttempts = _connectAttempts
            };

            lock (_extraSubs)
            {
                foreach (var s in _extraSubs) info.Subscriptions.Add(s);
            }
            if (_wildcardCaptureOn && !info.Subscriptions.Contains("#"))
                info.Subscriptions.Add("#");

            lock (_recentMessages)
            {
                // Más nuevos primero
                for (var node = _recentMessages.Last; node != null; node = node.Previous)
                    info.RecentMessages.Add(node.Value);
            }
            lock (_lock) info.KnownNodesCount = _nodos.Count;

            // Tanda 2 punto 7: snapshot del tracker de gaps.
            info.SeqGapCount = _seqTracker.TotalGapMessages;
            info.SeqResetCount = _seqTracker.TotalResets;
            var gaps = _seqTracker.GetRecentGaps();
            info.RecentSeqGaps = new List<NodoSeqGap>(gaps.Count);
            foreach (var g in gaps)
            {
                info.RecentSeqGaps.Add(new NodoSeqGap
                {
                    TimestampUtc = g.TimestampUtc,
                    Uid = g.Uid,
                    Schema = g.Schema,
                    LastSeq = g.LastSeq,
                    NewSeq = g.NewSeq,
                    Missed = g.Missed
                });
            }
            return info;
        }

        public async Task<bool> SetWildcardCaptureAsync(bool on)
        {
            _wildcardCaptureOn = on;
            var c = _client;
            if (c == null || !c.IsConnected) return false;
            try
            {
                if (on) await c.SubscribeAsync("#").ConfigureAwait(false);
                else await c.UnsubscribeAsync("#").ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        private void RecordRecent(string topic, string payload)
        {
            // Truncar payloads enormes para no comer RAM del ring buffer.
            string p = payload ?? "";
            if (p.Length > 512) p = p.Substring(0, 512) + "…";
            var msg = new NodoMqttMessage
            {
                TimestampUtc = DateTime.UtcNow,
                Topic = topic ?? "",
                Payload = p
            };
            lock (_recentMessages)
            {
                _recentMessages.AddLast(msg);
                while (_recentMessages.Count > RecentMessagesCapacity)
                    _recentMessages.RemoveFirst();
            }
        }

        public void Stop()
        {
            _running = false;
            try { _staleTimer?.Dispose(); } catch { }
            _staleTimer = null;
            try { _reconnectTimer?.Dispose(); } catch { }
            _reconnectTimer = null;
            try
            {
                if (_client != null && _client.IsConnected)
                    _ = _client.DisconnectAsync();
                _client?.Dispose();
            }
            catch { }
            _client = null;
        }

        public IReadOnlyList<NodoStatus> GetAll()
        {
            List<NodoStatus> copy;
            lock (_lock)
            {
                copy = new List<NodoStatus>(_nodos.Count);
                foreach (var n in _nodos.Values)
                {
                    List<MotorLive> motorsCopy = null;
                    if (n.MotorsLive != null && n.MotorsLive.Count > 0)
                    {
                        motorsCopy = new List<MotorLive>(n.MotorsLive.Count);
                        foreach (var m in n.MotorsLive)
                        {
                            motorsCopy.Add(new MotorLive
                            {
                                Id = m.Id,
                                PpsTarget = m.PpsTarget,
                                PpsReal = m.PpsReal,
                                Pwm = m.Pwm,
                                Rpm = m.Rpm,
                                Pulsos = m.Pulsos,
                                LastSeenUtc = m.LastSeenUtc
                            });
                        }
                    }
                    copy.Add(new NodoStatus
                    {
                        Uid = n.Uid,
                        Type = n.Type,
                        Ip = n.Ip,
                        Firmware = n.Firmware,
                        Motors = n.Motors,
                        Uptime = n.Uptime,
                        LastSeenUtc = n.LastSeenUtc,
                        Online = n.Online,
                        BootReason = n.BootReason,
                        SafeMode = n.SafeMode,
                        CrashCount = n.CrashCount,
                        MotorsLive = motorsCopy
                    });
                }
            }
            copy.Sort((a, b) =>
            {
                int c = b.Online.CompareTo(a.Online);
                if (c != 0) return c;
                c = string.Compare(a.Type ?? "", b.Type ?? "", StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Uid ?? "", b.Uid ?? "", StringComparison.OrdinalIgnoreCase);
            });
            return copy;
        }

        public void Dispose() => Stop();

        // ----- handler -----

        private void HandleMessage(string topic, string payload)
        {
            if (string.IsNullOrEmpty(topic)) return;

            var parts = topic.Split('/');
            if (parts.Length < 4) return;
            if (!string.Equals(parts[0], "agp", StringComparison.OrdinalIgnoreCase)) return;

            string type = parts[1];
            string uid = parts[2];
            string verb = parts[3];

            if (string.IsNullOrEmpty(uid)) return;

            // ── Desired/Reported config (Tanda 2 #8) ────────────────────────
            // Topic con 5 partes: agp/{prod}/{uid}/config/desired|reported
            // No actualiza Online/LastSeen para que el message retained no
            // mienta sobre la vitalidad del nodo (lo emite el broker, no el firmware).
            if (parts.Length >= 5 &&
                string.Equals(verb, "config", StringComparison.OrdinalIgnoreCase))
            {
                string sub = parts[4];
                if (string.Equals(sub, "desired", StringComparison.OrdinalIgnoreCase))
                {
                    try { _configSync.TrackDesired(uid, payload); } catch { }
                    return;
                }
                if (string.Equals(sub, "reported", StringComparison.OrdinalIgnoreCase))
                {
                    try { _configSync.TrackReported(uid, payload); } catch { }
                    return;
                }
            }

            // Capitalizar tipo: "quantix" → "QuantiX"-ish (deja la primera mayúscula)
            if (!string.IsNullOrEmpty(type))
                type = char.ToUpperInvariant(type[0]) + type.Substring(1);

            bool isAnnouncement =
                string.Equals(verb, "announcement", StringComparison.OrdinalIgnoreCase);
            bool isLwt =
                string.Equals(verb, "lwt", StringComparison.OrdinalIgnoreCase);
            bool isAck =
                string.Equals(verb, "ack", StringComparison.OrdinalIgnoreCase);

            // ── ACK handler (Tanda 2) ───────────────────────────────────────
            // Payload esperado: {"cmd_id":"...","status":"ok|rejected|error","detail":"...","ts_ms":...}
            // El parsing es manual (substring search) para no costar System.Text.Json
            // en el hot path del listener — los acks son frecuentes durante OTA/autotune.
            if (isAck)
            {
                if (!string.IsNullOrEmpty(payload))
                {
                    string cmdId = ExtractJsonStringField(payload, "cmd_id");
                    string status = ExtractJsonStringField(payload, "status");
                    string detail = ExtractJsonStringField(payload, "detail");
                    if (!string.IsNullOrEmpty(cmdId))
                    {
                        try { _cmdTracker.OnAckReceived(cmdId, status ?? "ok", detail ?? ""); }
                        catch { }
                    }
                }
                return;
            }

            // ── LWT handler ─────────────────────────────────────────────────
            // Payload esperado: {"online":true} (publicado por el firmware al
            // conectar, sobrescribe el retained) o {"online":false,"reason":...}
            // (publicado por el BROKER cuando detecta caída TCP del cliente).
            // No actualiza LastSeenUtc para no enmascarar la caída.
            if (isLwt)
            {
                bool isOnline = !string.IsNullOrEmpty(payload) &&
                                payload.IndexOf("\"online\":true", StringComparison.OrdinalIgnoreCase) >= 0;
                bool lwtChanged = false;
                lock (_lock)
                {
                    NodoStatus nl;
                    if (!_nodos.TryGetValue(uid, out nl))
                    {
                        nl = new NodoStatus { Uid = uid, Type = type };
                        _nodos[uid] = nl;
                        lwtChanged = true;
                    }
                    if (nl.Online != isOnline)
                    {
                        nl.Online = isOnline;
                        lwtChanged = true;
                    }
                    if (isOnline)
                    {
                        // Connect limpio del firmware → bumpear LastSeen para
                        // que MarkStale no lo marque offline acto seguido.
                        nl.LastSeenUtc = DateTime.UtcNow;
                    }
                }
                if (lwtChanged) RaiseChanged();
                return;
            }

            // Tanda 2 punto 7: gap detection (informativo, no alarma).
            // Aplica a CUALQUIER payload con `schema` + `seq` — announcement,
            // status_live, telemetría VistaX, motor_live de QuantiX, etc.
            // Firmwares legacy no traen estos campos → ExtractJsonNullableInt
            // devuelve null y Observe es no-op. Hacemos esto FUERA del lock
            // porque el tracker tiene su propio lock interno y no necesita el
            // estado de _nodos.
            if (!isAck && !isLwt && !string.IsNullOrEmpty(payload))
            {
                string schema = ExtractJson(payload, "schema");
                int? seqVal = ExtractJsonNullableInt(payload, "seq");
                if (!string.IsNullOrEmpty(schema) && seqVal.HasValue)
                {
                    try { _seqTracker.Observe(uid, schema, seqVal.Value); }
                    catch { /* tracker no debe romper el pipeline */ }
                }
            }

            bool changed = false;
            lock (_lock)
            {
                NodoStatus n;
                if (!_nodos.TryGetValue(uid, out n))
                {
                    n = new NodoStatus { Uid = uid, Type = type };
                    _nodos[uid] = n;
                    changed = true;
                }

                bool wasOffline = !n.Online;
                n.LastSeenUtc = DateTime.UtcNow;
                n.Online = true;
                if (wasOffline) changed = true;

                if (string.IsNullOrEmpty(n.Type) || n.Type == "Desconocido")
                {
                    n.Type = string.IsNullOrEmpty(type) ? "Desconocido" : type;
                    changed = true;
                }

                if (isAnnouncement && !string.IsNullOrEmpty(payload))
                {
                    string ip = ExtractJson(payload, "ip");
                    string fw = ExtractJson(payload, "fw");
                    int motors = ExtractJsonInt(payload, "motors");
                    long uptime = ExtractJsonLong(payload, "uptime");
                    string bootReason = ExtractJson(payload, "boot_reason");
                    // Tanda 2: safe_mode + crash_count reportados por firmwares con AgpSafeMode.
                    // Firmwares legacy no los traen → ExtractJsonBool/Int devuelven default y
                    // dejamos los valores actuales (no es señal válida de "salió de safe-mode").
                    bool? safeMode = ExtractJsonNullableBool(payload, "safe_mode");
                    int? crashCount = ExtractJsonNullableInt(payload, "crash_count");

                    if (!string.IsNullOrEmpty(ip) && n.Ip != ip) { n.Ip = ip; changed = true; }
                    if (!string.IsNullOrEmpty(fw) && n.Firmware != fw) { n.Firmware = fw; changed = true; }
                    if (motors > 0 && n.Motors != motors) { n.Motors = motors; changed = true; }
                    if (uptime > 0 && n.Uptime != uptime) { n.Uptime = uptime; changed = true; }
                    if (!string.IsNullOrEmpty(bootReason) && n.BootReason != bootReason)
                    {
                        n.BootReason = bootReason;
                        changed = true;
                    }
                    if (safeMode.HasValue && n.SafeMode != safeMode.Value)
                    {
                        n.SafeMode = safeMode.Value;
                        changed = true;
                    }
                    if (crashCount.HasValue && n.CrashCount != crashCount.Value)
                    {
                        n.CrashCount = crashCount.Value;
                        changed = true;
                    }
                }

                // QuantiX live motor telemetry sobre /status_live.
                // Payload: {"id":0,"pps_target":..,"pps_real":..,"pwm":..,"rpm":..,"pulsos":..}
                if (!isAnnouncement
                    && string.Equals(verb, "status_live", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(payload)
                    && type != null && type.IndexOf("quantix", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int motorId = ExtractJsonInt(payload, "id");
                    if (n.MotorsLive == null) n.MotorsLive = new List<MotorLive>();
                    MotorLive m = null;
                    for (int i = 0; i < n.MotorsLive.Count; i++)
                        if (n.MotorsLive[i].Id == motorId) { m = n.MotorsLive[i]; break; }
                    if (m == null)
                    {
                        m = new MotorLive { Id = motorId };
                        n.MotorsLive.Add(m);
                        changed = true;
                    }
                    m.PpsTarget = ExtractJsonDouble(payload, "pps_target");
                    m.PpsReal = ExtractJsonDouble(payload, "pps_real");
                    m.Pwm = ExtractJsonInt(payload, "pwm");
                    m.Rpm = ExtractJsonInt(payload, "rpm");
                    m.Pulsos = ExtractJsonLong(payload, "pulsos");
                    m.LastSeenUtc = DateTime.UtcNow;
                    changed = true;
                }
            }

            if (changed) RaiseChanged();
        }

        private void MarkStale()
        {
            // Red de seguridad por si el LWT del broker no se disparó
            // (ej: broker reiniciado entre el ÚLTIMO LWT del firmware y la
            // caída TCP). Antes 30s — bajamos a 15s ahora que los firmwares
            // anuncian cada 10s. A 8 km/h: 15s × 2.2 m/s = 33 m de "agujero
            // ciego" como peor caso si el LWT no llegó.
            const double StaleSeconds = 15;
            bool changed = false;
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                foreach (var n in _nodos.Values)
                {
                    bool online = (now - n.LastSeenUtc).TotalSeconds < StaleSeconds;
                    if (online != n.Online)
                    {
                        n.Online = online;
                        changed = true;
                    }
                }
            }
            if (changed) RaiseChanged();
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
        }

        // ----- mini parser JSON sin deps externas -----

        private static string ExtractJson(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            string needle = "\"" + key + "\"";
            int idx = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length) return null;

            if (json[i] == '"')
            {
                int start = i + 1;
                int end = json.IndexOf('"', start);
                if (end < 0) return null;
                return json.Substring(start, end - start);
            }
            else
            {
                int start = i;
                while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ' ' && json[i] != '\r' && json[i] != '\n')
                    i++;
                return json.Substring(start, i - start);
            }
        }

        private static int ExtractJsonInt(string json, string key)
        {
            int v;
            return int.TryParse(ExtractJson(json, key), out v) ? v : 0;
        }

        private static long ExtractJsonLong(string json, string key)
        {
            long v;
            return long.TryParse(ExtractJson(json, key), out v) ? v : 0L;
        }

        private static double ExtractJsonDouble(string json, string key)
        {
            double v;
            return double.TryParse(ExtractJson(json, key),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0d;
        }

        // Versiones "nullable" — devuelven null si la key NO está en el JSON,
        // así distinguimos "el firmware no lo reporta" (legacy, dejar valor previo)
        // de "el firmware lo reporta = 0/false" (señal válida que debe overrideear).
        private static int? ExtractJsonNullableInt(string json, string key)
        {
            var raw = ExtractJson(json, key);
            if (raw == null) return null;
            int v;
            return int.TryParse(raw, out v) ? (int?)v : null;
        }

        private static bool? ExtractJsonNullableBool(string json, string key)
        {
            var raw = ExtractJson(json, key);
            if (raw == null) return null;
            if (string.Equals(raw, "true",  StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw == "1") return true;
            if (raw == "0") return false;
            return null;
        }
    }
}
