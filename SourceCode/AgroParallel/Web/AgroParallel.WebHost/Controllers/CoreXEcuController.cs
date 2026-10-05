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
// Serialización JSON
// ------------------
// Todos los endpoints usan AgpControllerBase.WriteJsonAsync (snake_case global
// vía AgpJson). Los DTOs con [JsonPropertyName] respetan el atributo; los campos
// sin atributo se convierten a snake_case por política. El JS del Hub lee
// siempre snake_case (ej. s.error_code, s.imu.yaw_deg, s.was.zero_done, etc.).
// ============================================================================

using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class CoreXEcuController : AgpControllerBase
    {
        private readonly ICoreXEcuService _svc;

        public CoreXEcuController(ICoreXEcuService svc)
        {
            _svc = svc;
        }

        private Task WriteServiceUnavailable()
        {
            return WriteJsonAsync(new { ok = false, error_code = "AGP-SYS-009", error = "Servicio CoreX-ECU no disponible." });
        }

        // -------- /config ------------------------------------------------------

        [Route(HttpVerbs.Get, "/corex-ecu/config")]
        public Task GetConfig()
        {
            if (_svc == null) return WriteServiceUnavailable();
            return WriteJsonAsync(_svc.LoadConfig());
        }

        [Route(HttpVerbs.Post, "/corex-ecu/config")]
        public async Task SaveConfig()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            CoreXEcuConfigDto dto = null;
            try { dto = await ReadJsonBodyAsync<CoreXEcuConfigDto>().ConfigureAwait(false); }
            catch { /* dto se queda null y devolvemos invalid-body */ }

            if (dto == null)
            {
                await WriteJsonAsync(new { ok = false, error_code = "AGP-NET-103", error = "Body JSON inválido." }).ConfigureAwait(false);
                return;
            }
            _svc.SaveConfig(dto);
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }

        // -------- /status ------------------------------------------------------

        [Route(HttpVerbs.Get, "/corex-ecu/status")]
        public async Task GetStatus()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var snap = await _svc.GetStatusAsync().ConfigureAwait(false);
            await WriteJsonAsync(snap).ConfigureAwait(false);
        }

        // -------- /params ------------------------------------------------------

        [Route(HttpVerbs.Get, "/corex-ecu/params")]
        public async Task GetParams()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.GetParamsAsync().ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex-ecu/params")]
        public async Task SetParams()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            string body = await ReadBodyAsync().ConfigureAwait(false);

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
                await WriteJsonAsync(new { ok = false, error_code = "AGP-NET-103", error = "Body JSON inválido." }).ConfigureAwait(false);
                return;
            }

            var dto = await _svc.UpdateParamsAsync(patch).ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        // -------- /wassrc (v1.11+) ---------------------------------------------

        [Route(HttpVerbs.Get, "/corex-ecu/wassrc")]
        public async Task GetWassrc()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.GetWassrcAsync().ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex-ecu/wassrc")]
        public async Task SetWassrc()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            CoreXEcuWassrcRequestDto req = null;
            try
            {
                req = await ReadJsonBodyAsync<CoreXEcuWassrcRequestDto>().ConfigureAwait(false);
            }
            catch { /* req queda null */ }

            if (req == null || string.IsNullOrWhiteSpace(req.Source))
            {
                await WriteJsonAsync(new { ok = false, error_code = "AGP-NET-103", error = "Body inválido. Esperaba { source }." }).ConfigureAwait(false);
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
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        // -------- /zero --------------------------------------------------------

        [Route(HttpVerbs.Post, "/corex-ecu/zero")]
        public async Task ForceZero()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.ForceZeroAsync().ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        // -------- /reboot ------------------------------------------------------

        [Route(HttpVerbs.Post, "/corex-ecu/reboot")]
        public async Task Reboot()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            bool ok = await _svc.RebootAsync().ConfigureAwait(false);
            await WriteJsonAsync(new { ok }).ConfigureAwait(false);
        }

        // ====================== Motor manual (v1.09+) =======================

        [Route(HttpVerbs.Post, "/corex-ecu/motor/test")]
        public async Task MotorTest()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            CoreXEcuMotorTestRequestDto req = null;
            try
            {
                req = await ReadJsonBodyAsync<CoreXEcuMotorTestRequestDto>().ConfigureAwait(false);
            }
            catch { /* req se queda null */ }

            if (req == null)
            {
                await WriteJsonAsync(new { ok = false, error_code = "AGP-NET-103", error = "Body inválido. Esperaba { pwm, duration_ms }." }).ConfigureAwait(false);
                return;
            }
            int dur = req.DurationMs > 0 ? req.DurationMs : 1000;
            var dto = await _svc.MotorTestAsync(req.Pwm, dur).ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/corex-ecu/motor/stop")]
        public async Task MotorStop()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.MotorStopAsync().ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        // ====================== Firmware OTA (Teensy) =======================

        [Route(HttpVerbs.Post, "/corex-ecu/firmware/flash")]
        public async Task FlashFirmware()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            CoreXEcuFlashRequestDto req = null;
            try
            {
                req = await ReadJsonBodyAsync<CoreXEcuFlashRequestDto>().ConfigureAwait(false);
            }
            catch { /* req queda null */ }

            if (req == null || string.IsNullOrWhiteSpace(req.Version))
            {
                await WriteJsonAsync(new { ok = false, error_code = "AGP-NET-103", error = "Body inválido. Esperaba { version }." }).ConfigureAwait(false);
                return;
            }

            var dto = await _svc.FlashFirmwareAsync(req.Version).ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        // ====================== Calibración PWM sweep (v1.10+) ==============

        [Route(HttpVerbs.Post, "/corex-ecu/calibration/pwm-sweep")]
        public async Task StartSweep()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            CoreXEcuSweepStartRequestDto req;
            try
            {
                req = await ReadJsonBodyAsync<CoreXEcuSweepStartRequestDto>().ConfigureAwait(false)
                      ?? new CoreXEcuSweepStartRequestDto();
            }
            catch
            {
                await WriteJsonAsync(new { ok = false, error_code = "AGP-NET-103", error = "Body JSON inválido." }).ConfigureAwait(false);
                return;
            }

            var dto = await _svc.StartSweepAsync(req.StepDurationMs, req.SettleMs).ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/corex-ecu/calibration/pwm-sweep")]
        public async Task GetSweep()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.GetSweepAsync().ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Delete, "/corex-ecu/calibration/pwm-sweep")]
        public async Task CancelSweep()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }
            var dto = await _svc.CancelSweepAsync().ConfigureAwait(false);
            await WriteJsonAsync(dto).ConfigureAwait(false);
        }
    }
}
