// ============================================================================
// FlowXLiveService.cs — implementación.
//
// Estrategia:
//   - Suscribe agp/flow/+/status_live (y los topics de resultado) al MQTT
//     compartido del NodoRegistry, vía MqttLiveServiceBase<Reading>.
//   - Parsea payload del firmware: { uid?, caudal_lmin|caudal, pwm,
//                                    pid_estado|pid_state, target?, error? }
//     Si el firmware no incluye uid en el payload, lo infiere de topic[2]
//     (canónico 4-part: agp/flow/<uid>/status_live).
//   - Mantiene Dictionary<uid, Reading> (heredado de la base como _readings).
//   - GetSnapshot() recompone la lista resolviendo nombre desde config y
//     marcando Online según timeout (3 s default).
//
// Nota: el firmware FlowX actual tiene bugs documentados (topic 3-part en
// vez de 4-part, prefijo "nodes/" en vez de "agp/"). Hasta que se corrijan,
// este service no recibirá nada. La estructura está lista para cuando se
// arregle el firmware — el bridge ya publica el target correctamente.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class FlowXLiveService : MqttLiveServiceBase<FlowXLiveService.Reading>,
        IFlowXLiveService, IDisposable
    {
        private readonly IFlowXConfigService _cfgSvc;

        private FlowXConfigDto _cfg;

        // uid → última lectura (heredado _readings de la base)
        public sealed class Reading
        {
            public double CaudalLmin;
            public double TargetLmin;
            public double ErrorLmin;
            public int Pwm;
            public long Pulsos;
            public string PidEstado;
            public DateTime LastTs;
        }

        // Resultados cacheados de auto-tune / calibración (uno por uid,
        // sobreescritos cuando el firmware reporta el siguiente).
        private readonly Dictionary<string, FxTuneResultDto> _autotune =
            new Dictionary<string, FxTuneResultDto>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FxCalibrarResultDto> _calibrar =
            new Dictionary<string, FxCalibrarResultDto>(StringComparer.OrdinalIgnoreCase);

        // Caracterización: guardamos el JSON crudo tal cual lo emite el firmware
        // (payload de agp/flow/<uid>/caracterizar_result). La curva pwm/hz se
        // muestra en la UI sin necesidad de tipar un DTO específico.
        private readonly Dictionary<string, string> _char =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── Configuración de la base ─────────────────────────────────────────
        protected override string TopicPrefix => "agp/flow/";
        protected override int MinParts => 4;
        protected override string[] Subscriptions => new[]
        {
            "agp/flow/+/status_live",
            "agp/flow/+/autotune_result",
            "agp/flow/+/calibrar_result",
            "agp/flow/+/caracterizar_result",
        };

        // ── Constructor ──────────────────────────────────────────────────────
        public FlowXLiveService(INodoRegistryService nodos, IFlowXConfigService cfgSvc)
            : base(nodos)
        {
            _cfgSvc = cfgSvc;
            Reload();
        }

        public void Reload()
        {
            lock (_lock)
            {
                _cfg = _cfgSvc?.Load() ?? new FlowXConfigDto();
            }
        }

        public void Dispose() => Stop();

        // ── OnStart / OnStop ─────────────────────────────────────────────────
        protected override void OnStart()
        {
            System.Diagnostics.Trace.WriteLine("[flowx] live service started");
        }

        protected override void OnStop()
        {
            System.Diagnostics.Trace.WriteLine("[flowx] live service stopped");
        }

        // ── OnPayload ────────────────────────────────────────────────────────
        // subtopic viene de parts[3]: status_live | autotune_result |
        //                             calibrar_result | caracterizar_result.
        protected override void OnPayload(string uid, string subtopic, string[] topicParts, JsonElement root)
        {
            switch (subtopic.ToLowerInvariant())
            {
                case "autotune_result":
                    HandleAutotune(uid, root);
                    break;

                case "calibrar_result":
                    HandleCalibrar(uid, root);
                    break;

                case "caracterizar_result":
                    // root fue parseado por la base; necesitamos el JSON crudo.
                    // Lo re-serializamos para conservar el string original.
                    lock (_lock) { _char[uid] = root.GetRawText(); }
                    break;

                case "status_live":
                    HandleStatusLive(uid, root);
                    break;
            }
        }

        // ── Handlers de subtopics ────────────────────────────────────────────
        private void HandleStatusLive(string uidFromTopic, JsonElement root)
        {
            // El firmware puede incluir "uid" en el payload; si viene, se usa.
            string uid = root.TryGetProperty("uid", out var ju) && ju.ValueKind == JsonValueKind.String
                ? ju.GetString()
                : uidFromTopic;
            if (string.IsNullOrEmpty(uid)) uid = uidFromTopic;
            if (string.IsNullOrEmpty(uid)) return;

            double caudal   = ReadDouble(root, "caudal_lmin", "caudal", "flow");
            double target   = ReadDouble(root, "target_lmin", "target", "t");
            double error    = ReadDouble(root, "error_lmin", "error", "err");
            int pwm         = (int)ReadDouble(root, "pwm");
            long pulsos     = (long)ReadDouble(root, "pulsos", "pulses");
            string pidEstado = ReadString(root, "pid_estado", "pid_state", "estado") ?? "";

            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                if (!_readings.TryGetValue(uid, out var r))
                {
                    r = new Reading();
                    _readings[uid] = r;
                }
                r.CaudalLmin = caudal;
                if (target > 0) r.TargetLmin = target;
                if (error != 0) r.ErrorLmin = error;
                r.Pwm = pwm;
                r.Pulsos = pulsos;
                if (!string.IsNullOrEmpty(pidEstado)) r.PidEstado = pidEstado;
                r.LastTs = now;
            }
        }

        private void HandleAutotune(string uidFromTopic, JsonElement root)
        {
            var r = new FxTuneResultDto
            {
                Uid = ReadString(root, "uid") ?? uidFromTopic,
                ProductoId = (int)ReadDouble(root, "producto_id", "producto"),
                Ok = ReadBool(root, "ok", "success"),
                Kp = ReadDouble(root, "kp"),
                Ki = ReadDouble(root, "ki"),
                Kd = ReadDouble(root, "kd"),
                Ku = ReadDouble(root, "ku"),
                TuMs = ReadDouble(root, "tu_ms", "tu"),
                Error = ReadString(root, "error") ?? "",
                ReceivedUtc = DateTime.UtcNow.ToString("O")
            };
            string key = string.IsNullOrEmpty(r.Uid) ? uidFromTopic : r.Uid;
            lock (_lock) { _autotune[key] = r; }
        }

        private void HandleCalibrar(string uidFromTopic, JsonElement root)
        {
            var r = new FxCalibrarResultDto
            {
                Uid = ReadString(root, "uid") ?? uidFromTopic,
                ProductoId = (int)ReadDouble(root, "producto_id", "producto"),
                Ok = ReadBool(root, "ok", "success"),
                Pulsos = (long)ReadDouble(root, "pulsos", "pulses"),
                DurationMs = (long)ReadDouble(root, "duration_ms", "duration"),
                Error = ReadString(root, "error") ?? "",
                ReceivedUtc = DateTime.UtcNow.ToString("O")
            };
            string key = string.IsNullOrEmpty(r.Uid) ? uidFromTopic : r.Uid;
            lock (_lock) { _calibrar[key] = r; }
        }

        // ── Snapshot ─────────────────────────────────────────────────────────
        public FlowXLiveSnapshotDto GetSnapshot()
        {
            lock (_lock)
            {
                var snap = new FlowXLiveSnapshotDto { MonitoreoActivo = IsRunning };
                DateTime now = DateTime.UtcNow;

                // Construir un set de uids: los que están en config (aunque
                // todavía no haya status_live) + los que están reportando.
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
                    double dosisLha = 0;
                    if (_cfg?.Nodos != null)
                    {
                        var cfgNodo = _cfg.Nodos.FirstOrDefault(n =>
                            string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase));
                        if (cfgNodo != null)
                        {
                            nombre = cfgNodo.Nombre ?? "";
                            if (cfgNodo.Productos != null && cfgNodo.Productos.Count > 0)
                                dosisLha = cfgNodo.Productos[0].DosisLha;
                        }
                    }
                    bool online = r != null && IsOnline(r.LastTs, now);

                    double caudalLmin = r?.CaudalLmin ?? 0;
                    double targetLmin = r?.TargetLmin ?? 0;
                    double caudalLha = (targetLmin > 0.0001)
                        ? caudalLmin / targetLmin * dosisLha
                        : 0;

                    snap.Nodos.Add(new FxNodoLiveDto
                    {
                        Uid = uid,
                        Nombre = nombre,
                        Online = online,
                        CaudalLmin = caudalLmin,
                        TargetLmin = targetLmin,
                        CaudalLha = caudalLha,
                        TargetLha = dosisLha,
                        Pwm = r?.Pwm ?? 0,
                        Pulsos = r?.Pulsos ?? 0,
                        PidEstado = r?.PidEstado ?? "",
                        ErrorLmin = r?.ErrorLmin ?? 0,
                        LastSeenIso = (r != null && r.LastTs != default(DateTime))
                            ? r.LastTs.ToString("O") : ""
                    });
                }

                snap.Nodos = snap.Nodos.OrderBy(n => n.Uid).ToList();
                return snap;
            }
        }

        // ── Autotune / Calibrar / Caracterizar results ───────────────────────
        public FxTuneResultDto GetAutoTuneResult(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (_lock) { return _autotune.TryGetValue(uid, out var r) ? r : null; }
        }

        public FxCalibrarResultDto GetCalibrarResult(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (_lock) { return _calibrar.TryGetValue(uid, out var r) ? r : null; }
        }

        public void ClearAutoTuneResult(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (_lock) { _autotune.Remove(uid); }
        }

        public void ClearCalibrarResult(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (_lock) { _calibrar.Remove(uid); }
        }

        public string GetCaracterizarResultRaw(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (_lock) { return _char.TryGetValue(uid, out var r) ? r : null; }
        }

        public void ClearCaracterizarResult(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (_lock) { _char.Remove(uid); }
        }
    }
}
