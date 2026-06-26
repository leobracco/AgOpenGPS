// ============================================================================
// LineXLiveService.cs — implementación.
//
// Estrategia:
//   - Suscribe agp/linex/+/status_live al MQTT compartido del NodoRegistry.
//   - Parsea payload del firmware:
//       { uid, sections:[{id, state(bool), angle, us}], rssi, uptime }
//     Si el firmware no incluye uid en el payload, lo infiere de topic[2]
//     (canónico 4-part: agp/linex/<uid>/status_live).
//   - Mantiene Dictionary<uid, Reading>.
//   - GetSnapshot() recompone la lista resolviendo nombre/board_type desde
//     config y marcando Online según timeout (3 s default).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class LineXLiveService : ILineXLiveService, IDisposable
    {
        private readonly INodoRegistryService _nodos;
        private readonly ILineXConfigService _cfgSvc;

        private LineXConfigDto _cfg;
        private readonly object _lock = new object();

        // uid → última lectura
        private sealed class Reading
        {
            public List<LxSurcoLiveDto> Surcos = new List<LxSurcoLiveDto>();
            public int Rssi;
            public long Uptime;
            public DateTime LastTs;
        }
        private readonly Dictionary<string, Reading> _readings =
            new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);

        // Timeout para considerar un nodo online.
        private const int TimeoutMs = 3000;

        public bool IsRunning { get; private set; }

        public LineXLiveService(INodoRegistryService nodos, ILineXConfigService cfgSvc)
        {
            _nodos = nodos;
            _cfgSvc = cfgSvc;
            Reload();
        }

        public void Reload()
        {
            lock (_lock)
            {
                _cfg = _cfgSvc?.Load() ?? new LineXConfigDto();
            }
        }

        public void Start()
        {
            if (IsRunning) return;
            if (_nodos == null) return;
            _nodos.MessageReceived += OnMqttMessage;
            _ = _nodos.SubscribeAsync("agp/linex/+/status_live");
            IsRunning = true;
            System.Diagnostics.Debug.WriteLine("[linex] live service started");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try { _nodos.MessageReceived -= OnMqttMessage; } catch { }
            IsRunning = false;
            lock (_lock) { _readings.Clear(); }
            System.Diagnostics.Debug.WriteLine("[linex] live service stopped");
        }

        public void Dispose() => Stop();

        // ------------------- MQTT in --------------------
        private void OnMqttMessage(object sender, MqttMessageReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Topic)) return;
            if (!e.Topic.StartsWith("agp/linex/", StringComparison.OrdinalIgnoreCase)) return;
            if (!e.Topic.EndsWith("/status_live", StringComparison.OrdinalIgnoreCase)) return;

            var parts = e.Topic.Split('/');
            if (parts.Length < 4) return;
            string uidFromTopic = parts[2];

            try
            {
                using (var doc = JsonDocument.Parse(e.Payload))
                {
                    var root = doc.RootElement;
                    string uid = root.TryGetProperty("uid", out var ju) && ju.ValueKind == JsonValueKind.String
                        ? ju.GetString()
                        : uidFromTopic;
                    if (string.IsNullOrEmpty(uid)) uid = uidFromTopic;
                    if (string.IsNullOrEmpty(uid)) return;

                    var surcos = new List<LxSurcoLiveDto>();
                    if (root.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in secs.EnumerateArray())
                        {
                            surcos.Add(new LxSurcoLiveDto
                            {
                                Id = (int)ReadDouble(s, "id"),
                                Abierto = ReadBool(s, "state", "abierto", "open"),
                                Angle = (int)ReadDouble(s, "angle"),
                                Us = (int)ReadDouble(s, "us")
                            });
                        }
                    }

                    int rssi = (int)ReadDouble(root, "rssi");
                    long uptime = (long)ReadDouble(root, "uptime");

                    DateTime now = DateTime.UtcNow;
                    lock (_lock)
                    {
                        if (!_readings.TryGetValue(uid, out var r))
                        {
                            r = new Reading();
                            _readings[uid] = r;
                        }
                        r.Surcos = surcos;
                        r.Rssi = rssi;
                        r.Uptime = uptime;
                        r.LastTs = now;
                    }
                }
            }
            catch { /* payload roto: ignorar */ }
        }

        private static double ReadDouble(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetDouble();
            }
            return 0;
        }

        private static bool ReadBool(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (root.TryGetProperty(k, out var v))
                {
                    if (v.ValueKind == JsonValueKind.True) return true;
                    if (v.ValueKind == JsonValueKind.False) return false;
                    if (v.ValueKind == JsonValueKind.Number) return v.GetDouble() != 0;
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var st = v.GetString();
                        return string.Equals(st, "open", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(st, "true", StringComparison.OrdinalIgnoreCase)
                            || st == "1";
                    }
                }
            }
            return false;
        }

        // ------------------- Snapshot --------------------
        public LineXLiveSnapshotDto GetSnapshot()
        {
            lock (_lock)
            {
                var snap = new LineXLiveSnapshotDto { MonitoreoActivo = IsRunning };
                DateTime now = DateTime.UtcNow;

                // uids: los de config (aunque no haya status_live aún) + los que reportan.
                var uids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_cfg?.Nodos != null)
                {
                    foreach (var n in _cfg.Nodos)
                        if (!string.IsNullOrEmpty(n.Uid)) uids.Add(n.Uid);
                }
                foreach (var k in _readings.Keys) uids.Add(k);

                foreach (var uid in uids)
                {
                    _readings.TryGetValue(uid, out var r);
                    string nombre = "";
                    string boardType = "";
                    if (_cfg?.Nodos != null)
                    {
                        var cfgNodo = _cfg.Nodos.FirstOrDefault(n =>
                            string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase));
                        if (cfgNodo != null)
                        {
                            nombre = cfgNodo.Nombre ?? "";
                            boardType = cfgNodo.BoardType ?? "";
                        }
                    }
                    bool online = r != null && (now - r.LastTs).TotalMilliseconds <= TimeoutMs;

                    snap.Nodos.Add(new LxNodoLiveDto
                    {
                        Uid = uid,
                        Nombre = nombre,
                        BoardType = boardType,
                        Online = online,
                        Rssi = r?.Rssi ?? 0,
                        Uptime = r?.Uptime ?? 0,
                        Surcos = r?.Surcos ?? new List<LxSurcoLiveDto>(),
                        LastSeenIso = (r != null && r.LastTs != default(DateTime))
                            ? r.LastTs.ToString("O") : ""
                    });
                }

                snap.Nodos = snap.Nodos.OrderBy(n => n.Uid).ToList();
                return snap;
            }
        }
    }
}
