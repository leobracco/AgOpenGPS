// ============================================================================
// CoreXEcuController.cs
// Endpoints REST del módulo CoreX-ECU (firmware Teensy 4.1, spec v1.11+):
//   GET    /api/corex-ecu/config                  → CoreXEcuConfigDto
//   POST   /api/corex-ecu/config                  (body = CoreXEcuConfigDto) → { ok }
//   GET    /api/corex-ecu/status                  → CoreXEcuStatusDto (proxy al Teensy)
//   GET    /api/corex-ecu/params                  → CoreXEcuParamsDto (proxy al Teensy)
//   POST   /api/corex-ecu/params                  (body = flat patch JSON) → CoreXEcuParamsUpdateResultDto
//   GET    /api/corex-ecu/wassrc                  → CoreXEcuWassrcDto                                     (v1.11+)
//   POST   /api/corex-ecu/wassrc                  (body { source }) → CoreXEcuWassrcUpdateResultDto       (v1.11+)
//   POST   /api/corex-ecu/zero                    → CoreXEcuZeroResultDto
//   POST   /api/corex-ecu/reboot                  → { ok }
//   POST   /api/corex-ecu/motor/test              (body { pwm, duration_ms }) → CoreXEcuMotorTestResultDto  (v1.09+)
//   POST   /api/corex-ecu/motor/stop              → CoreXEcuOkResultDto                                    (v1.09+)
//   POST   /api/corex-ecu/firmware/flash          (body { version }) → CoreXEcuFlashResultDto
//   POST   /api/corex-ecu/calibration/pwm-sweep   (body { step_duration_ms, settle_ms }) → start result    (v1.10+)
//   GET    /api/corex-ecu/calibration/pwm-sweep   → CoreXEcuSweepStatusDto                                 (v1.10+)
//   DELETE /api/corex-ecu/calibration/pwm-sweep   → CoreXEcuOkResultDto                                    (v1.10+)
//
// El controller es un proxy delgado: toda la lógica de HTTP + timeout +
// fallback "stub" cuando el Teensy no responde vive en CoreXEcuService.
//
// Nota CRÍTICA de serialización (camelCase outbound)
// ---------------------------------------------------
// El ResponseSerializer default de EmbedIO (Swan) emite PascalCase y la UI HTML
// del Hub asume camelCase. Por eso TODOS los endpoints serializan a mano con
// `System.Text.Json` + `JsonNamingPolicy.CamelCase` y mandan el JSON crudo con
// `HttpContext.SendStringAsync`. Si devolviéramos el DTO directamente el JS
// leería `j.Ok` en vez de `j.ok` y siempre caería al fallback `AGP-NET-201`.
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CoreXEcuController : WebApiController
    {
        // Inbound (body que manda la UI): PropertyNameCaseInsensitive=true para
        // que `step_duration_ms`/`settle_ms` matcheen los [JsonPropertyName] del DTO.
        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Outbound: serializamos con [JsonPropertyName] (snake_case en muchos
        // campos porque comparte DTO con el firmware), después post-procesamos
        // renombrando claves snake_case → camelCase para que coincidan con lo
        // que la UI espera (`s.imu.yawDeg`, `j.stepCount`, etc.).
        private static readonly JsonSerializerOptions WriteOptsRaw = new JsonSerializerOptions();

        private readonly ICoreXEcuService _svc;

        public CoreXEcuController(ICoreXEcuService svc)
        {
            _svc = svc;
        }

        // -------- helper outbound ----------------------------------------------

        private Task WriteJson(object payload)
        {
            // 1. Serializar con attributes (puede dar snake_case por [JsonPropertyName]).
            string raw = JsonSerializer.Serialize(payload, WriteOptsRaw);
            // 2. Re-escribir renombrando snake_case → camelCase en cada clave.
            string camel = RewriteKeysCamelCase(raw);
            return HttpContext.SendStringAsync(camel, "application/json", Encoding.UTF8);
        }

        /// <summary>Parsea el JSON y reescribe TODAS las claves a camelCase
        /// (snake_case → camelCase, PascalCase → camelCase). Garantiza que la
        /// UI HTML reciba siempre `errorCode`, `yawDeg`, `stepCount`, etc.,
        /// independientemente de los `[JsonPropertyName]` del DTO.</summary>
        private static string RewriteKeysCamelCase(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                    WriteCamel(w, doc.RootElement);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static void WriteCamel(Utf8JsonWriter w, JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var p in el.EnumerateObject())
                    {
                        w.WritePropertyName(ToCamelKey(p.Name));
                        WriteCamel(w, p.Value);
                    }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in el.EnumerateArray())
                        WriteCamel(w, item);
                    w.WriteEndArray();
                    break;
                default:
                    el.WriteTo(w);
                    break;
            }
        }

        private static string ToCamelKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.IndexOf('_') < 0)
            {
                // Ya camelCase o PascalCase: bajamos el primer char si es upper.
                if (char.IsUpper(name[0])) return char.ToLowerInvariant(name[0]) + name.Substring(1);
                return name;
            }
            var parts = name.Split('_');
            var sb = new StringBuilder();
            // primer segmento full lower
            sb.Append(parts[0].ToLowerInvariant());
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                sb.Append(char.ToUpperInvariant(parts[i][0]));
                if (parts[i].Length > 1) sb.Append(parts[i].Substring(1).ToLowerInvariant());
            }
            return sb.ToString();
        }

        private Task WriteServiceUnavailable()
        {
            return WriteJson(new { ok = false, errorCode = "AGP-SYS-009", error = "Servicio CoreX-ECU no disponible." });
        }

        // -------- /config ------------------------------------------------------

        [Route(HttpVerbs.Get, "/corex-ecu/config")]
        public Task GetConfig()
        {
            if (_svc == null) return WriteServiceUnavailable();
            return WriteJson(_svc.LoadConfig());
        }

        [Route(HttpVerbs.Post, "/corex-ecu/config")]
        public async Task SaveConfig()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);

            CoreXEcuConfigDto dto = null;
            try { dto = JsonSerializer.Deserialize<CoreXEcuConfigDto>(body, ReadOpts); }
            catch { /* dto se queda null y devolvemos invalid-body */ }

            if (dto == null)
            {
                await WriteJson(new { ok = false, errorCode = "AGP-NET-103", error = "Body JSON inválido." }).ConfigureAwait(false);
                return;
            }
            _svc.SaveConfig(dto);
            await WriteJson(new { ok = true }).ConfigureAwait(false);
        }

        // -------- /status ------------------------------------------------------

        [Route(HttpVerbs.Get, "/corex-ecu/status")]
        public async Task GetStatus()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var snap = await _svc.GetStatusAsync().ConfigureAwait(false);
            await WriteJson(snap).ConfigureAwait(false);
        }

        // -------- /params ------------------------------------------------------

        [Route(HttpVerbs.Get, "/corex-ecu/params")]
        public async Task GetParams()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.GetParamsAsync().ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex-ecu/params")]
        public async Task SetParams()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);

            // Parseo a Dictionary<string, object> preservando tipos (bool/number/string).
            // El firmware acepta cualquier subset de claves planas y devuelve
            // `updated.{autoZero,keya,imu}` indicando qué grupos se persistieron.
            var patch = new Dictionary<string, object>();
            try
            {
                using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in doc.RootElement.EnumerateObject())
                        {
                            // Ignoramos objetos/arrays: la spec del firmware
                            // dice "estructura plana — no anidada".
                            switch (p.Value.ValueKind)
                            {
                                case JsonValueKind.Number:
                                    if (p.Value.TryGetInt64(out var lv))
                                    {
                                        patch[p.Name] = lv;
                                    }
                                    else
                                    {
                                        patch[p.Name] = p.Value.GetDouble();
                                    }
                                    break;
                                case JsonValueKind.True:
                                    patch[p.Name] = 1;
                                    break;
                                case JsonValueKind.False:
                                    patch[p.Name] = 0;
                                    break;
                                case JsonValueKind.String:
                                    patch[p.Name] = p.Value.GetString();
                                    break;
                            }
                        }
                    }
                }
            }
            catch
            {
                await WriteJson(new { ok = false, errorCode = "AGP-NET-103", error = "Body JSON inválido." }).ConfigureAwait(false);
                return;
            }

            var dto = await _svc.UpdateParamsAsync(patch).ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        // -------- /wassrc (v1.11+) ---------------------------------------------

        [Route(HttpVerbs.Get, "/corex-ecu/wassrc")]
        public async Task GetWassrc()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.GetWassrcAsync().ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex-ecu/wassrc")]
        public async Task SetWassrc()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);

            CoreXEcuWassrcRequestDto req = null;
            try
            {
                req = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<CoreXEcuWassrcRequestDto>(body, ReadOpts);
            }
            catch { /* req queda null */ }

            if (req == null || string.IsNullOrWhiteSpace(req.Source))
            {
                await WriteJson(new { ok = false, errorCode = "AGP-NET-103", error = "Body inválido. Esperaba { source }." }).ConfigureAwait(false);
                return;
            }

            var dto = await _svc.SetWassrcAsync(req.Source).ConfigureAwait(false);

            // Si el firmware aceptó, persistir la preferencia en corexEcu.json
            // para que sobreviva al restart del PC y la UI la muestre al abrir.
            // RMW bajo lock — dos POST /wassrc concurrentes deben ser secuenciales
            // (sin esto, el segundo Load lee la copia pre-update del primero y
            // ambos se persisten — un cambio se pierde).
            if (dto != null && dto.Ok && !string.IsNullOrEmpty(dto.Source))
            {
                try
                {
                    _svc.UpdateConfig(cfg =>
                    {
                        if (cfg == null) return false;
                        if (string.Equals(cfg.WasSource, dto.Source, System.StringComparison.OrdinalIgnoreCase))
                            return false; // idempotente: no rewrite si ya coincide
                        cfg.WasSource = dto.Source;
                        return true;
                    });
                }
                catch { /* persistencia best-effort — el firmware ya tomó la fuente */ }
            }
            await WriteJson(dto).ConfigureAwait(false);
        }

        // -------- /zero --------------------------------------------------------

        [Route(HttpVerbs.Post, "/corex-ecu/zero")]
        public async Task ForceZero()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.ForceZeroAsync().ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        // -------- /reboot ------------------------------------------------------

        [Route(HttpVerbs.Post, "/corex-ecu/reboot")]
        public async Task Reboot()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            bool ok = await _svc.RebootAsync().ConfigureAwait(false);
            await WriteJson(new { ok }).ConfigureAwait(false);
        }

        // ====================== Motor manual (v1.09+) =======================

        [Route(HttpVerbs.Post, "/corex-ecu/motor/test")]
        public async Task MotorTest()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);

            CoreXEcuMotorTestRequestDto req = null;
            try
            {
                req = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<CoreXEcuMotorTestRequestDto>(body, ReadOpts);
            }
            catch { /* req se queda null */ }

            if (req == null)
            {
                await WriteJson(new { ok = false, errorCode = "AGP-NET-103", error = "Body inválido. Esperaba { pwm, duration_ms }." }).ConfigureAwait(false);
                return;
            }
            int dur = req.DurationMs > 0 ? req.DurationMs : 1000;
            var dto = await _svc.MotorTestAsync(req.Pwm, dur).ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex-ecu/motor/stop")]
        public async Task MotorStop()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.MotorStopAsync().ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        // ====================== Firmware OTA (Teensy) =======================

        [Route(HttpVerbs.Post, "/corex-ecu/firmware/flash")]
        public async Task FlashFirmware()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);

            CoreXEcuFlashRequestDto req = null;
            try
            {
                req = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<CoreXEcuFlashRequestDto>(body, ReadOpts);
            }
            catch { /* req queda null */ }

            if (req == null || string.IsNullOrWhiteSpace(req.Version))
            {
                await WriteJson(new { ok = false, errorCode = "AGP-NET-103", error = "Body inválido. Esperaba { version }." }).ConfigureAwait(false);
                return;
            }

            var dto = await _svc.FlashFirmwareAsync(req.Version).ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        // ====================== Calibración PWM sweep (v1.10+) ==============

        [Route(HttpVerbs.Post, "/corex-ecu/calibration/pwm-sweep")]
        public async Task StartSweep()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);

            CoreXEcuSweepStartRequestDto req;
            try
            {
                req = string.IsNullOrWhiteSpace(body)
                    ? new CoreXEcuSweepStartRequestDto()
                    : (JsonSerializer.Deserialize<CoreXEcuSweepStartRequestDto>(body, ReadOpts)
                       ?? new CoreXEcuSweepStartRequestDto());
            }
            catch
            {
                await WriteJson(new { ok = false, errorCode = "AGP-NET-103", error = "Body JSON inválido." }).ConfigureAwait(false);
                return;
            }

            var dto = await _svc.StartSweepAsync(req.StepDurationMs, req.SettleMs).ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/corex-ecu/calibration/pwm-sweep")]
        public async Task GetSweep()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.GetSweepAsync().ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Delete, "/corex-ecu/calibration/pwm-sweep")]
        public async Task CancelSweep()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.CancelSweepAsync().ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }
    }
}
