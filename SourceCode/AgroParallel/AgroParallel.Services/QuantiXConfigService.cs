// ============================================================================
// QuantiXConfigService.cs
//   * quantiX_motores.json y quantiX.json en BaseDirectory (1:1 con legacy).
//   * Publica agp/quantix/{uid}/config y agp/quantix/{uid}/{verb} usando la
//     conexión MQTT del INodoRegistryService (no abre conexión propia).
// ============================================================================

using System;
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
        private const string UdpFile = "quantiX.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly INodoRegistryService _nodos;
        public QuantiXConfigService(INodoRegistryService nodos) { _nodos = nodos; }

        private static string Path(string name)
            => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);

        // ---------- Motores ----------

        public QxMotoresConfigDto GetMotores()
        {
            string p = Path(MotoresFile);
            if (!File.Exists(p)) return new QxMotoresConfigDto();
            try
            {
                var dto = JsonSerializer.Deserialize<QxMotoresConfigDto>(File.ReadAllText(p), ReadOpts);
                if (dto == null) return new QxMotoresConfigDto();
                if (dto.Nodos == null) dto.Nodos = new System.Collections.Generic.List<QxNodoConfigDto>();
                if (dto.Ignorados == null) dto.Ignorados = new System.Collections.Generic.List<string>();
                return dto;
            }
            catch { return new QxMotoresConfigDto(); }
        }

        public void SaveMotores(QxMotoresConfigDto dto)
        {
            if (dto == null) return;
            try { File.WriteAllText(Path(MotoresFile), JsonSerializer.Serialize(dto, WriteOpts)); }
            catch { }
        }

        // ---------- UDP ----------

        public QxUdpConfigDto GetUdp()
        {
            string p = Path(UdpFile);
            if (!File.Exists(p))
            {
                var def = new QxUdpConfigDto();
                SaveUdp(def);
                return def;
            }
            try
            {
                return JsonSerializer.Deserialize<QxUdpConfigDto>(File.ReadAllText(p), ReadOpts)
                    ?? new QxUdpConfigDto();
            }
            catch { return new QxUdpConfigDto(); }
        }

        public void SaveUdp(QxUdpConfigDto dto)
        {
            if (dto == null) return;
            try { File.WriteAllText(Path(UdpFile), JsonSerializer.Serialize(dto, WriteOpts)); }
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

            string topic = "agp/quantix/" + uid + "/config";
            return await _nodos.PublishAsync(topic, sb.ToString(), retain: true).ConfigureAwait(false);
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
