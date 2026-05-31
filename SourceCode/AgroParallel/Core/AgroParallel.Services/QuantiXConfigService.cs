// ============================================================================
// QuantiXConfigService.cs
//   * quantiX_motores.json en BaseDirectory (1:1 con legacy).
//   * Publica agp/quantix/{uid}/config y agp/quantix/{uid}/{verb} usando la
//     conexión MQTT del INodoRegistryService (no abre conexión propia).
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class QuantiXConfigService : IQuantiXConfigService
    {
        private const string MotoresFile = "quantiX_motores.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly INodoRegistryService _nodos;

        // Cache del último autotune_result por uid. Se llena desde
        // OnMqttMessage cuando llega agp/quantix/{uid}/autotune_result.
        private readonly ConcurrentDictionary<string, AutoTuneResult> _autoTuneByUid
            = new ConcurrentDictionary<string, AutoTuneResult>(StringComparer.OrdinalIgnoreCase);
        private int _autoTuneSubscribed;

        public QuantiXConfigService(INodoRegistryService nodos)
        {
            _nodos = nodos;
            if (_nodos != null)
            {
                _nodos.MessageReceived += OnMqttMessage;
                // Suscripción fire-and-forget al filtro de autotune_result.
                // El registry re-aplica las suscripciones extra al reconectar.
                _ = SubscribeAutoTuneAsync();
            }
        }

        private async Task SubscribeAutoTuneAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _autoTuneSubscribed, 1) != 0) return;
            try { await _nodos.SubscribeAsync("agp/+/+/autotune_result").ConfigureAwait(false); }
            catch { }
        }

        private void OnMqttMessage(object sender, MqttMessageReceivedEventArgs e)
        {
            try
            {
                if (e == null || string.IsNullOrEmpty(e.Topic)) return;
                if (e.Topic.IndexOf("autotune_result", StringComparison.OrdinalIgnoreCase) < 0) return;
                // Topic: agp/quantix/{uid}/autotune_result
                var parts = e.Topic.Split('/');
                if (parts.Length < 4) return;
                string uid = parts[2];
                if (string.IsNullOrEmpty(uid)) return;

                var p = e.Payload ?? "";
                var r = new AutoTuneResult
                {
                    Uid = uid,
                    MotorId = (int)ExtractNum(p, "\"id\":"),
                    Ok = p.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) >= 0,
                    Kp = ExtractNum(p, "\"kp\":"),
                    Ki = ExtractNum(p, "\"ki\":"),
                    Kd = ExtractNum(p, "\"kd\":"),
                    ReceivedUtc = DateTime.UtcNow
                };
                _autoTuneByUid[uid] = r;
            }
            catch { }
        }

        public AutoTuneResult GetAutoTuneResult(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            AutoTuneResult r;
            return _autoTuneByUid.TryGetValue(uid, out r) ? r : null;
        }

        private static double ExtractNum(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return 0;
            int idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            int start = idx + key.Length;
            int end = start;
            while (end < json.Length)
            {
                char c = json[end];
                if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\n' || c == '\r' || c == '\t') break;
                end++;
            }
            string s = json.Substring(start, end - start).Trim();
            double v;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static string Path(string name)
            => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);

        // ---------- Motores ----------

        public QxMotoresConfigDto GetMotores()
        {
            string p = Path(MotoresFile);
            if (!File.Exists(p))
            {
                var def = new QxMotoresConfigDto();
                SeedDefaultTrenes(def);
                return def;
            }
            try
            {
                var dto = JsonSerializer.Deserialize<QxMotoresConfigDto>(File.ReadAllText(p), ReadOpts);
                if (dto == null) dto = new QxMotoresConfigDto();
                if (dto.Nodos == null) dto.Nodos = new System.Collections.Generic.List<QxNodoConfigDto>();
                if (dto.Ignorados == null) dto.Ignorados = new System.Collections.Generic.List<string>();
                if (dto.Trenes == null) dto.Trenes = new System.Collections.Generic.List<QxTrenConfigDto>();
                if (dto.Trenes.Count == 0) SeedDefaultTrenes(dto);
                return dto;
            }
            catch { var fallback = new QxMotoresConfigDto(); SeedDefaultTrenes(fallback); return fallback; }
        }

        // Implementos de doble tren son lo más común — sembramos esa configuración
        // para que archivos viejos (sin sección "trenes") sigan funcionando sin
        // que el operario tenga que reconstruirla. Si quiere monotren, edita.
        private static void SeedDefaultTrenes(QxMotoresConfigDto dto)
        {
            dto.Trenes.Add(new QxTrenConfigDto { Id = 0, Nombre = "Delantero", DistanciaM = 0.0 });
            dto.Trenes.Add(new QxTrenConfigDto { Id = 1, Nombre = "Trasero", DistanciaM = 2.0 });
        }

        public void SaveMotores(QxMotoresConfigDto dto)
        {
            if (dto == null) return;
            try { File.WriteAllText(Path(MotoresFile), JsonSerializer.Serialize(dto, WriteOpts)); }
            catch { }
        }

        // ---------- MQTT publish ----------

        public async Task<bool> SendNodoConfigAsync(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;
            if (_nodos == null) return false;

            var motores = GetMotores();
            QxNodoConfigDto node = null;
            foreach (var n in motores.Nodos)
                if (string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase)) { node = n; break; }
            if (node == null || node.Motores == null) return false;

            // Payload espejo del firmware MQTT_Custom.cpp.
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.Append("{\"configs\":[");
            for (int mi = 0; mi < node.Motores.Length; mi++)
            {
                var m = node.Motores[mi];
                if (mi > 0) sb.Append(',');
                sb.Append("{\"idx\":").Append(mi);
                sb.Append(",\"config_pid\":{");
                sb.Append("\"kp\":").Append(m.Kp.ToString(ci));
                sb.Append(",\"ki\":").Append(m.Ki.ToString(ci));
                sb.Append(",\"kd\":").Append(m.Kd.ToString(ci));
                sb.Append("}");
                sb.Append(",\"calibracion\":{");
                sb.Append("\"pwm_min\":").Append(m.PwmMin);
                sb.Append(",\"pwm_max\":").Append(m.PwmMax);
                sb.Append("}");
                sb.Append(",\"meter_cal\":").Append(m.MeterCal.ToString(ci));
                sb.Append(",\"max_hz\":").Append(m.MaxHz.ToString(ci));
                sb.Append(",\"ff_gain\":").Append(m.FFGain.ToString(ci));
                sb.Append(",\"alpha\":").Append(m.Alpha.ToString(ci));
                sb.Append(",\"pid_time\":").Append(m.PIDTime);
                sb.Append(",\"slew_rate_per_sec\":").Append(m.SlewRatePerSec.ToString(ci));
                sb.Append(",\"dientes_engranaje\":").Append(m.DientesEngranaje);
                sb.Append('}');
            }
            sb.Append("]}");

            string payload = sb.ToString();

            // Tanda 2 #8 — desired/reported. Publicamos a AMBOS topics:
            //   · `/config` (legacy, retained) — los firmwares actuales lo
            //     siguen mirando hasta migrar al nuevo split.
            //   · `/config/desired` (retained) — el split nuevo. NodoRegistryService
            //     está suscrito y trackea esto contra lo que el firmware echa back
            //     en `/config/reported`. Si el firmware no soporta el split, este
            //     topic queda retained en el broker sin consumidor — sin daño.
            string topicLegacy = "agp/quantix/" + uid + "/config";
            string topicDesired = "agp/quantix/" + uid + "/config/desired";

            bool ok1 = await _nodos.PublishAsync(topicLegacy, payload, retain: true).ConfigureAwait(false);
            bool ok2 = await _nodos.PublishAsync(topicDesired, payload, retain: true).ConfigureAwait(false);
            return ok1 && ok2;
        }

        public async Task<bool> SendCmdAsync(string uid, string verb, string payload, bool retain)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(verb)) return false;
            if (_nodos == null) return false;
            string topic = "agp/quantix/" + uid + "/" + verb.Trim('/');
            return await _nodos.PublishAsync(topic, payload ?? "{}", retain).ConfigureAwait(false);
        }
    }
}
