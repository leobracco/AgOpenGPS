// ============================================================================
// StormXLiveService.cs — implementación.
//
// Estrategia:
//   - Suscribe agp/storm/+/status_live al MQTT compartido del NodoRegistry.
//   - Parsea payload del firmware (campos opcionales, todos numéricos):
//     wind_ms, gust_ms, wind_dir, temp_c, hum_pct, press_hpa, delta_t_c, rain_mm.
//   - Si el firmware no manda delta_t_c, se calcula con la fórmula psicrométrica
//     simplificada de Bureau of Meteorology AU:
//        delta_T ≈ Tw - Td   (depresión psicrométrica)
//     Aproximación Magnus para Tw (bulbo húmedo):
//        Tw = T·atan(0.151977·sqrt(RH+8.313659))
//             + atan(T+RH) - atan(RH-1.676331)
//             + 0.00391838·RH^1.5·atan(0.023101·RH) - 4.686035
//     Esto es estándar AgVal/AgWeather, ±0.3 °C vs Stull exacto.
//   - Aplica timeout (LogIntervalSec*2, mín 30 s) para considerar el nodo
//     online: las estaciones meteo publican mucho más espaciadas que un
//     caudalímetro.
//
// Esta es la primera vez que el firmware StormX va a tener un consumidor
// del lado del PC; el contrato de payload lo definimos acá (ver DTO).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class StormXLiveService : IStormXLiveService, IDisposable
    {
        private readonly INodoRegistryService _nodos;
        private readonly IStormXConfigService _cfgSvc;

        private StormXConfigDto _cfg;
        private readonly object _lock = new object();

        private sealed class Reading
        {
            public double WindMs, GustMs, WindDir, TempC, HumPct, PressHpa, DeltaTC, RainMm;
            public bool HasDeltaT;
            public DateTime LastTs;
        }
        private readonly Dictionary<string, Reading> _readings =
            new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);

        public bool IsRunning { get; private set; }

        public StormXLiveService(INodoRegistryService nodos, IStormXConfigService cfgSvc)
        {
            _nodos = nodos;
            _cfgSvc = cfgSvc;
            Reload();
        }

        public void Reload()
        {
            lock (_lock)
            {
                _cfg = _cfgSvc?.Load() ?? new StormXConfigDto();
            }
        }

        public void Start()
        {
            if (IsRunning) return;
            if (_nodos == null) return;
            _nodos.MessageReceived += OnMqttMessage;
            _ = _nodos.SubscribeAsync("agp/storm/+/status_live");
            IsRunning = true;
            System.Diagnostics.Debug.WriteLine("[stormx] live service started");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try { _nodos.MessageReceived -= OnMqttMessage; } catch { }
            IsRunning = false;
            lock (_lock) _readings.Clear();
            System.Diagnostics.Debug.WriteLine("[stormx] live service stopped");
        }

        public void Dispose() => Stop();

        // ------------------- MQTT in --------------------
        private void OnMqttMessage(object sender, MqttMessageReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Topic)) return;
            if (!e.Topic.StartsWith("agp/storm/", StringComparison.OrdinalIgnoreCase)) return;
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
                        ? ju.GetString() : uidFromTopic;
                    if (string.IsNullOrEmpty(uid)) uid = uidFromTopic;
                    if (string.IsNullOrEmpty(uid)) return;

                    DateTime now = DateTime.UtcNow;
                    lock (_lock)
                    {
                        if (!_readings.TryGetValue(uid, out var r))
                        {
                            r = new Reading();
                            _readings[uid] = r;
                        }
                        r.WindMs   = ReadDouble(root, "wind_ms",   "wind", "viento_ms");
                        r.GustMs   = ReadDouble(root, "gust_ms",   "gust", "rafaga_ms");
                        r.WindDir  = ReadDouble(root, "wind_dir",  "dir",  "viento_dir");
                        r.TempC    = ReadDouble(root, "temp_c",    "temp", "temperatura");
                        r.HumPct   = ReadDouble(root, "hum_pct",   "hum",  "humedad");
                        r.PressHpa = ReadDouble(root, "press_hpa", "press","presion");
                        r.RainMm   = ReadDouble(root, "rain_mm",   "rain", "lluvia_mm");

                        if (TryReadDouble(root, out double dt, "delta_t_c", "delta_t"))
                        {
                            r.DeltaTC = dt;
                            r.HasDeltaT = true;
                        }
                        else
                        {
                            r.DeltaTC = ComputeDeltaT(r.TempC, r.HumPct);
                            r.HasDeltaT = false;
                        }
                        r.LastTs = now;
                    }
                }
            }
            catch { /* payload roto */ }
        }

        // Delta-T = T - Tw (bulbo seco - bulbo húmedo). Aproximación Stull 2011.
        private static double ComputeDeltaT(double tempC, double humPct)
        {
            if (humPct <= 0 || humPct > 100) return 0;
            double T = tempC, RH = humPct;
            double Tw =
                T * Math.Atan(0.151977 * Math.Sqrt(RH + 8.313659))
                + Math.Atan(T + RH) - Math.Atan(RH - 1.676331)
                + 0.00391838 * Math.Pow(RH, 1.5) * Math.Atan(0.023101 * RH)
                - 4.686035;
            double dt = T - Tw;
            return dt < 0 ? 0 : dt;
        }

        private static double ReadDouble(JsonElement root, params string[] keys)
        {
            return TryReadDouble(root, out double v, keys) ? v : 0;
        }
        private static bool TryReadDouble(JsonElement root, out double value, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number)
                {
                    value = v.GetDouble();
                    return true;
                }
            }
            value = 0;
            return false;
        }

        // ------------------- Snapshot --------------------
        public StormXLiveSnapshotDto GetSnapshot()
        {
            lock (_lock)
            {
                var limits = _cfg?.Limits ?? new StormXLimitsDto();
                int timeoutMs = Math.Max(30, (_cfg?.LogIntervalSec ?? 30) * 2) * 1000;
                DateTime now = DateTime.UtcNow;

                var snap = new StormXLiveSnapshotDto
                {
                    MonitoreoActivo = IsRunning,
                    Limits = limits
                };

                // Set de uids: nodos en config + nodos vistos.
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

                    var dto = new StxNodoLiveDto
                    {
                        Uid = uid,
                        Nombre = nombre,
                        Online = online,
                        WindMs = r?.WindMs ?? 0,
                        GustMs = r?.GustMs ?? 0,
                        WindDir = r?.WindDir ?? 0,
                        TempC = r?.TempC ?? 0,
                        HumPct = r?.HumPct ?? 0,
                        PressHpa = r?.PressHpa ?? 0,
                        DeltaTC = r?.DeltaTC ?? 0,
                        RainMm = r?.RainMm ?? 0,
                        LastSeenIso = (r != null && r.LastTs != default(DateTime))
                            ? r.LastTs.ToString("O") : ""
                    };
                    dto.Verdict = ComputeVerdict(dto, limits, online);
                    snap.Nodos.Add(dto);
                }

                snap.Nodos = snap.Nodos.OrderBy(n => n.Uid).ToList();
                return snap;
            }
        }

        private static string ComputeVerdict(StxNodoLiveDto s, StormXLimitsDto lim, bool online)
        {
            if (!online) return "no-data";
            int fails = 0;
            if (s.WindMs > lim.WindMaxMs) fails++;
            if (s.WindMs > 0 && s.WindMs < lim.WindMinMs) fails++;
            if (s.HumPct > 0 && s.HumPct < lim.HumMinPct) fails++;
            if (s.TempC > lim.TempMaxC) fails++;
            if (s.DeltaTC > lim.DeltaTMaxC) fails++;
            if (fails == 0) return "ok";
            if (fails == 1) return "warn";
            return "bad";
        }
    }
}
