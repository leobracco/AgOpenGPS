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

        public event EventHandler Changed;

        public void Start(string brokerAddress, int brokerPort)
        {
            if (_running) return;
            if (string.IsNullOrWhiteSpace(brokerAddress)) return;
            if (brokerPort <= 0) brokerPort = 1883;

            _running = true;
            _ = StartAsync(brokerAddress, brokerPort);
            _staleTimer = new Timer(_ => MarkStale(), null, 5000, 5000);
        }

        private async Task StartAsync(string brokerAddress, int brokerPort)
        {
            try
            {
                var factory = new MqttFactory();
                _client = factory.CreateMqttClient();

                _client.ApplicationMessageReceivedAsync += e =>
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
                        HandleMessage(topic, payload);
                    }
                    catch { }
                    return Task.CompletedTask;
                };

                var options = new MqttClientOptionsBuilder()
                    .WithClientId("AgpHub_NodoRegistry_" + Guid.NewGuid().ToString("N").Substring(0, 8))
                    .WithTcpServer(brokerAddress, brokerPort)
                    .WithCleanSession(true)
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                    .Build();

                await _client.ConnectAsync(options, CancellationToken.None).ConfigureAwait(false);

                await _client.SubscribeAsync("agp/+/+/announcement").ConfigureAwait(false);
                await _client.SubscribeAsync("agp/+/+/status_live").ConfigureAwait(false);
            }
            catch
            {
                // si falla la conexión, lo silenciamos — Stop limpia y reintento manual desde UI
            }
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

        public void Stop()
        {
            _running = false;
            try { _staleTimer?.Dispose(); } catch { }
            _staleTimer = null;
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

            // Capitalizar tipo: "quantix" → "QuantiX"-ish (deja la primera mayúscula)
            if (!string.IsNullOrEmpty(type))
                type = char.ToUpperInvariant(type[0]) + type.Substring(1);

            bool isAnnouncement =
                string.Equals(verb, "announcement", StringComparison.OrdinalIgnoreCase);

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

                    if (!string.IsNullOrEmpty(ip) && n.Ip != ip) { n.Ip = ip; changed = true; }
                    if (!string.IsNullOrEmpty(fw) && n.Firmware != fw) { n.Firmware = fw; changed = true; }
                    if (motors > 0 && n.Motors != motors) { n.Motors = motors; changed = true; }
                    if (uptime > 0 && n.Uptime != uptime) { n.Uptime = uptime; changed = true; }
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
            bool changed = false;
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                foreach (var n in _nodos.Values)
                {
                    bool online = (now - n.LastSeenUtc).TotalSeconds < 30;
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
    }
}
