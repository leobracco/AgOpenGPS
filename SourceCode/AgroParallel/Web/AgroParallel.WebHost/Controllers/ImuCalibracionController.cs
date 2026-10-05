// ============================================================================
// ImuCalibracionController.cs
// Endpoints REST de calibración de roll del IMU interno de PilotX:
//   GET  /api/aog/imu              → { ok, snapshot: ImuCalibracionSnapshot }
//   POST /api/aog/imu/command      { "cmd": "zero_roll|remove_zero_offset|
//                                     roll_offset_up|roll_offset_down|
//                                     toggle_invert|reset_imu" }
//   POST /api/aog/imu/roll-filter  { "value": 0..100 }
//
// Mismo patrón que GuidanceController: proxy delgado, dispara sobre el hilo
// UI de PilotX vía IImuCalibracionService (implementación real en
// AgroParallel.Adapters, lado GPS — este proyecto no conoce CAHRS).
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ImuCalibracionController : AgpControllerBase
    {
        private readonly IImuCalibracionService _svc;

        public ImuCalibracionController(IImuCalibracionService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/aog/imu")]
        public Task GetImu()
        {
            var snap = _svc != null ? _svc.GetSnapshot() : null;
            return WriteJsonAsync(new { ok = true, snapshot = snap });
        }

        [Route(HttpVerbs.Post, "/aog/imu/command")]
        public async Task PostImuCommand()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            CommandBody body;
            try { body = await ReadJsonBodyAsync<CommandBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null || string.IsNullOrWhiteSpace(body.Cmd))
            {
                await WriteJsonAsync(new { ok = false, error = "empty-cmd" });
                return;
            }
            bool ok = _svc.ExecuteCommand(body.Cmd);
            await WriteJsonAsync(new { ok, cmd = body.Cmd });
        }

        [Route(HttpVerbs.Post, "/aog/imu/roll-filter")]
        public async Task PostRollFilter()
        {
            if (_svc == null)
            {
                await WriteJsonAsync(new { ok = false, error = "service-unavailable" });
                return;
            }
            FilterBody body;
            try { body = await ReadJsonBodyAsync<FilterBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null)
            {
                await WriteJsonAsync(new { ok = false, error = "bad-json" });
                return;
            }
            bool ok = _svc.SetRollFilter(body.Value);
            await WriteJsonAsync(new { ok, value = body.Value });
        }

        private sealed class CommandBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("cmd")]
            public string Cmd { get; set; }
        }

        private sealed class FilterBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("value")]
            public double Value { get; set; }
        }
    }
}
