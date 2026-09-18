// ============================================================================
// LineXLiveService.cs — implementación.
//
// Estrategia:
//   - Suscribe agp/linex/+/status_live al MQTT compartido del NodoRegistry,
//     vía MqttLiveServiceBase<Reading>.
//   - Parsea payload del firmware:
//       { uid, sections:[{id, state(bool), angle, us}], rssi, uptime }
//     Si el firmware no incluye uid en el payload, lo infiere de topic[2]
//     (canónico 4-part: agp/linex/<uid>/status_live).
//   - Mantiene Dictionary<uid, Reading> (heredado de la base como _readings).
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
    public sealed class LineXLiveService : MqttLiveServiceBase<LineXLiveService.Reading>,
        ILineXLiveService, IDisposable
    {
        private readonly ILineXConfigService _cfgSvc;

        private LineXConfigDto _cfg;

        // uid → última lectura (heredado _readings de la base)
        public sealed class Reading
        {
            public List<LxSurcoLiveDto> Surcos = new List<LxSurcoLiveDto>();
            public int Rssi;
            public long Uptime;
            public DateTime LastTs;
        }

        // ── Configuración de la base ─────────────────────────────────────────
        protected override string TopicPrefix => "agp/linex/";
        protected override int MinParts => 4;
        protected override string[] Subscriptions => new[]
        {
            "agp/linex/+/status_live",
        };

        // ── Constructor ──────────────────────────────────────────────────────
        public LineXLiveService(INodoRegistryService nodos, ILineXConfigService cfgSvc)
            : base(nodos)
        {
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

        public void Dispose() => Stop();

        // ── OnStart / OnStop ─────────────────────────────────────────────────
        protected override void OnStart()
        {
            System.Diagnostics.Trace.WriteLine("[linex] live service started");
        }

        protected override void OnStop()
        {
            System.Diagnostics.Trace.WriteLine("[linex] live service stopped");
        }

        // ── OnPayload ────────────────────────────────────────────────────────
        // subtopic viene de parts[3]: solo "status_live" por ahora.
        protected override void OnPayload(string uid, string subtopic, string[] topicParts, JsonElement root)
        {
            if (!string.Equals(subtopic, "status_live", StringComparison.OrdinalIgnoreCase))
                return;

            // El firmware puede incluir "uid" en el payload; si viene, se usa.
            string resolvedUid = root.TryGetProperty("uid", out var ju) && ju.ValueKind == JsonValueKind.String
                ? ju.GetString()
                : uid;
            if (string.IsNullOrEmpty(resolvedUid)) resolvedUid = uid;
            if (string.IsNullOrEmpty(resolvedUid)) return;

            var surcos = new List<LxSurcoLiveDto>();
            if (root.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in secs.EnumerateArray())
                {
                    surcos.Add(new LxSurcoLiveDto
                    {
                        Id = (int)ReadDouble(s, "id"),
                        Abierto = ReadBoolOpen(s, "state", "abierto", "open"),
                        Angle = (int)ReadDouble(s, "angle"),
                        Us = (int)ReadDouble(s, "us")
                    });
                }
            }

            int rssi     = (int)ReadDouble(root, "rssi");
            long uptime  = (long)ReadDouble(root, "uptime");

            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                if (!_readings.TryGetValue(resolvedUid, out var r))
                {
                    r = new Reading();
                    _readings[resolvedUid] = r;
                }
                r.Surcos = surcos;
                r.Rssi = rssi;
                r.Uptime = uptime;
                r.LastTs = now;
            }
        }

        // ReadBool especial: acepta además el string "open" (firmware LineX).
        // La base soporta "true"/"ok"/"1"; "open" es específico de este firmware.
        private static bool ReadBoolOpen(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!root.TryGetProperty(k, out var v)) continue;
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
            return false;
        }

        // ── Snapshot ─────────────────────────────────────────────────────────
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
                    bool online = r != null && IsOnline(r.LastTs, now);

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
