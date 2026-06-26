// ============================================================================
// FlowXLiveService.cs — implementación.
//
// Estrategia:
//   - Suscribe agp/flow/+/status_live al MQTT compartido del NodoRegistry.
//   - Parsea payload del firmware: { uid?, caudal_lmin|caudal, pwm,
//                                    pid_estado|pid_state, target?, error? }
//     Si el firmware no incluye uid en el payload, lo infiere de topic[2]
//     (canónico 4-part: agp/flow/<uid>/status_live).
//   - Mantiene Dictionary<uid, FxNodoLiveDto>.
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
    public sealed class FlowXLiveService : IFlowXLiveService, IDisposable
    {
        private readonly INodoRegistryService _nodos;
        private readonly IFlowXConfigService _cfgSvc;

        private FlowXConfigDto _cfg;
        private readonly object _lock = new object();

        // uid → última lectura
        private sealed class Reading
        {
            public double CaudalLmin;
            public double TargetLmin;
            public double ErrorLmin;
            public int Pwm;
            public long Pulsos;
            public string PidEstado;
            public DateTime LastTs;
        }
        private readonly Dictionary<string, Reading> _readings =
            new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);

        // Resultados cacheados de auto-tune / calibración (uno por uid,
        // sobreescritos cuando el firmware reporta el siguiente).
        private readonly Dictionary<string, FxTuneResultDto> _autotune =
            new Dictionary<string, FxTuneResultDto>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FxCalibrarResultDto> _calibrar =
            new Dictionary<string, FxCalibrarResultDto>(StringComparer.OrdinalIgnoreCase);

        // Caracterización: guardamos el JSON crudo tal cual lo emite el firmware
        // (payload de agp/flow/<uid>/caracterizar_result). La curva pwm/hz se
        // muestra en la UI sin necesidad de tipar un DTO específico — el
        // resultado es informativo (el operario decide si copia pwm_min al
        // campo de configuración).
        private readonly Dictionary<string, string> _char =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Timeout para considerar un nodo online.
        private const int TimeoutMs = 3000;

        public bool IsRunning { get; private set; }

        public FlowXLiveService(INodoRegistryService nodos, IFlowXConfigService cfgSvc)
        {
            _nodos = nodos;
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

        public void Start()
        {
            if (IsRunning) return;
            if (_nodos == null) return;
            _nodos.MessageReceived += OnMqttMessage;
            _ = _nodos.SubscribeAsync("agp/flow/+/status_live");
            _ = _nodos.SubscribeAsync("agp/flow/+/autotune_result");
            _ = _nodos.SubscribeAsync("agp/flow/+/calibrar_result");
            _ = _nodos.SubscribeAsync("agp/flow/+/caracterizar_result");
            IsRunning = true;
            System.Diagnostics.Debug.WriteLine("[flowx] live service started");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try { _nodos.MessageReceived -= OnMqttMessage; } catch { }
            IsRunning = false;
            lock (_lock) { _readings.Clear(); }
            System.Diagnostics.Debug.WriteLine("[flowx] live service stopped");
        }

        public void Dispose() => Stop();

        // ------------------- MQTT in --------------------
        private void OnMqttMessage(object sender, MqttMessageReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Topic)) return;

            if (!e.Topic.StartsWith("agp/flow/", StringComparison.OrdinalIgnoreCase)) return;

            var parts = e.Topic.Split('/');
            if (parts.Length < 4) return;
            string uidFromTopic = parts[2];

            // Canónicos: agp/flow/<uid>/{status_live|autotune_result|calibrar_result}
            if (e.Topic.EndsWith("/autotune_result", StringComparison.OrdinalIgnoreCase))
            { HandleAutotune(uidFromTopic, e.Payload); return; }
            if (e.Topic.EndsWith("/calibrar_result", StringComparison.OrdinalIgnoreCase))
            { HandleCalibrar(uidFromTopic, e.Payload); return; }
            if (e.Topic.EndsWith("/caracterizar_result", StringComparison.OrdinalIgnoreCase))
            { lock (_lock) { _char[uidFromTopic] = e.Payload ?? ""; } return; }
            if (!e.Topic.EndsWith("/status_live", StringComparison.OrdinalIgnoreCase)) return;

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

                    double caudal = ReadDouble(root, "caudal_lmin", "caudal", "flow");
                    double target = ReadDouble(root, "target_lmin", "target", "t");
                    double error  = ReadDouble(root, "error_lmin", "error", "err");
                    int pwm       = (int)ReadDouble(root, "pwm");
                    long pulsos   = (long)ReadDouble(root, "pulsos", "pulses");
                    string pidEstado =
                        ReadString(root, "pid_estado", "pid_state", "estado")
                        ?? "";

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

        private static string ReadString(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            return null;
        }

        // ------------------- Snapshot --------------------
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
                    {
                        if (!string.IsNullOrEmpty(n.Uid)) uids.Add(n.Uid);
                    }
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
                            // Dosis del primer producto (el firmware/bridge maneja
                            // hoy un solo producto por nodo en Sensor[0]).
                            if (cfgNodo.Productos != null && cfgNodo.Productos.Count > 0)
                                dosisLha = cfgNodo.Productos[0].DosisLha;
                        }
                    }
                    bool online = r != null && (now - r.LastTs).TotalMilliseconds <= TimeoutMs;

                    double caudalLmin = r?.CaudalLmin ?? 0;
                    double targetLmin = r?.TargetLmin ?? 0;
                    // L/ha: como target_lmin = dosis · vel · ancho / 600, la dosis
                    // por hectárea real aplicada = caudal_lmin / target_lmin · dosis
                    // (vel y ancho se cancelan). Sin target no se puede derivar.
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

        // ------------------- Autotune / Calibrar results --------------------
        // El firmware publica el resultado una sola vez al terminar la rutina.
        // Lo guardamos por uid y la UI lo poolea hasta consumirlo.

        private void HandleAutotune(string uidFromTopic, string payload)
        {
            try
            {
                using (var doc = JsonDocument.Parse(payload))
                {
                    var root = doc.RootElement;
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
            }
            catch { /* payload roto: ignorar */ }
        }

        private void HandleCalibrar(string uidFromTopic, string payload)
        {
            try
            {
                using (var doc = JsonDocument.Parse(payload))
                {
                    var root = doc.RootElement;
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
            }
            catch { /* payload roto: ignorar */ }
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
                        var s = v.GetString();
                        return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase)
                            || s == "1";
                    }
                }
            }
            return false;
        }

        public FxTuneResultDto GetAutoTuneResult(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (_lock)
            {
                return _autotune.TryGetValue(uid, out var r) ? r : null;
            }
        }

        public FxCalibrarResultDto GetCalibrarResult(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (_lock)
            {
                return _calibrar.TryGetValue(uid, out var r) ? r : null;
            }
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

        // Caracterización: devolvemos el JSON crudo (curva + pwm_min + hz_max).
        // La UI parsea — el payload puede ser ~600B con la curva incluida y no
        // vale la pena tiparlo en C# para algo que solo se muestra y se copia.
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
