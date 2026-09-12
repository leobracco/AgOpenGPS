// ============================================================================
// LibraXLiveService.cs — implementación.
//
// Estrategia:
//   - Suscribe agp/librax/+/status_live al MQTT compartido del NodoRegistry,
//     vía MqttLiveServiceBase<Reading>.
//   - Parsea el payload del firmware (contrato de fase 1, todos los campos
//     opcionales): ratio, paddle_hz, rpm, moist_mv, sensor_ok, noise, up.
//   - NO interpreta: el ratio se guarda crudo en permil, tal como lo publica
//     el nodo. Baseline de paletas, factor de calibración y qq/ha son fase 2;
//     meterlos acá ahora sería inventar números que nadie calibró.
//   - Un nodo listado en libraX.json aparece en el snapshot aunque nunca haya
//     publicado, marcado offline: si el nodo no aparece, la UI tiene que poder
//     mostrar "lo esperaba y no está" en vez de una lista vacía.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class LibraXLiveService : MqttLiveServiceBase<LibraXLiveService.Reading>,
        ILibraXLiveService, IDisposable
    {
        private readonly ILibraXConfigService _cfgSvc;
        private LibraXConfigDto _cfg;

        // uid → última lectura (el registry _readings lo hereda de la base)
        public sealed class Reading
        {
            public int RatioPermil, PaddleHz, Rpm, MoistMv, Noise, UptimeS;
            public bool SensorOk;
            public DateTime LastTs;
        }

        // ── Configuración de la base ─────────────────────────────────────────
        protected override string TopicPrefix => "agp/librax/";
        protected override int MinParts => 4;
        protected override string[] Subscriptions => new[] { "agp/librax/+/status_live" };

        protected override int TimeoutMs
        {
            get { lock (_lock) { return _cfg?.TimeoutMs > 0 ? _cfg.TimeoutMs : 3000; } }
        }

        // El firmware no manda "uid" dentro de status_live (el topic ya lo
        // lleva y a 5 Hz cada byte cuenta), pero lo aceptamos si aparece.
        protected override string ExtractUid(string[] parts, JsonElement root)
        {
            if (root.TryGetProperty("uid", out var ju) && ju.ValueKind == JsonValueKind.String)
            {
                var v = ju.GetString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return parts.Length > 2 ? parts[2] : null;
        }

        // ── Constructor ──────────────────────────────────────────────────────
        public LibraXLiveService(INodoRegistryService nodos, ILibraXConfigService cfgSvc)
            : base(nodos)
        {
            _cfgSvc = cfgSvc;
            Reload();
        }

        public void Reload()
        {
            lock (_lock)
            {
                _cfg = _cfgSvc?.Load() ?? new LibraXConfigDto();
            }
        }

        public void Dispose() => Stop();

        protected override void OnStart()
            => System.Diagnostics.Trace.WriteLine("[librax] live service started");

        protected override void OnStop()
            => System.Diagnostics.Trace.WriteLine("[librax] live service stopped");

        // ── OnPayload ────────────────────────────────────────────────────────
        // subtopic = "status_live". El uid ya viene extraído.
        protected override void OnPayload(string uid, string subtopic, string[] topicParts, JsonElement root)
        {
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                if (!_readings.TryGetValue(uid, out var r))
                {
                    r = new Reading();
                    _readings[uid] = r;
                }
                r.RatioPermil = (int)ReadDouble(root, "ratio");
                r.PaddleHz    = (int)ReadDouble(root, "paddle_hz");
                r.Rpm         = (int)ReadDouble(root, "rpm");
                r.MoistMv     = (int)ReadDouble(root, "moist_mv");
                r.Noise       = (int)ReadDouble(root, "noise");
                r.UptimeS     = (int)ReadDouble(root, "up");
                r.SensorOk    = ReadBool(root, "sensor_ok");
                r.LastTs      = now;
            }
        }

        // ── Snapshot ─────────────────────────────────────────────────────────
        public LibraXLiveSnapshotDto GetSnapshot()
        {
            lock (_lock)
            {
                int timeoutMs = _cfg?.TimeoutMs > 0 ? _cfg.TimeoutMs : 3000;
                DateTime now = DateTime.UtcNow;

                var snap = new LibraXLiveSnapshotDto { MonitoreoActivo = IsRunning };

                // Set de uids: los configurados + los efectivamente vistos.
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
                    if (_cfg?.Nodos != null)
                    {
                        var cn = _cfg.Nodos.FirstOrDefault(n =>
                            string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase));
                        if (cn != null) nombre = cn.Nombre ?? "";
                    }

                    bool online = r != null && (now - r.LastTs).TotalMilliseconds <= timeoutMs;

                    snap.Nodos.Add(new LbxNodoLiveDto
                    {
                        Uid = uid,
                        Nombre = nombre,
                        Online = online,
                        RatioPermil = r?.RatioPermil ?? 0,
                        RatioPct = (r?.RatioPermil ?? 0) / 10.0,
                        PaddleHz = r?.PaddleHz ?? 0,
                        Rpm = r?.Rpm ?? 0,
                        MoistMv = r?.MoistMv ?? 0,
                        SensorOk = r?.SensorOk ?? false,
                        Noise = r?.Noise ?? 0,
                        UptimeS = r?.UptimeS ?? 0,
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
