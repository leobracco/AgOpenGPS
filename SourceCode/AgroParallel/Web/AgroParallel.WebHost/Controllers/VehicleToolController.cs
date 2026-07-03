// ============================================================================
// VehicleToolController.cs
// REST endpoints para config de Vehículo + Herramienta de PilotX desde HTML.
//   GET  /api/vehicle           → VehicleConfigDto
//   PUT  /api/vehicle           ← VehicleConfigDto
//   GET  /api/tool              → ToolConfigDto
//   PUT  /api/tool              ← ToolConfigDto
//   GET  /api/vehicle-tool      → { vehicle, tool }  (bundle conveniente)
// ============================================================================

using System;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class VehicleToolController : AgpControllerBase
    {
        private readonly IVehicleToolService _svc;

        public VehicleToolController(IVehicleToolService svc) { _svc = svc; }

        [Route(HttpVerbs.Get, "/vehicle")]
        public Task GetVehicle()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, vehicle = _svc.GetVehicle() });
        }

        [Route(HttpVerbs.Get, "/tool")]
        public Task GetTool()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, tool = _svc.GetTool() });
        }

        [Route(HttpVerbs.Get, "/vehicle-tool")]
        public Task GetBundle()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var b = _svc.GetBundle();
            return WriteJsonAsync(new { ok = true, vehicle = b.Vehicle, tool = b.Tool });
        }

        [Route(HttpVerbs.Put, "/vehicle")]
        public async Task PutVehicle()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            VehicleConfigDto cfg;
            try { cfg = await ReadJsonBodyAsync<VehicleConfigDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (cfg == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.SaveVehicle(cfg);
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Put, "/tool")]
        public async Task PutTool()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            ToolConfigDto cfg;
            try { cfg = await ReadJsonBodyAsync<ToolConfigDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (cfg == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.SaveTool(cfg);
            await WriteJsonAsync(new { ok });
        }
    }
}
