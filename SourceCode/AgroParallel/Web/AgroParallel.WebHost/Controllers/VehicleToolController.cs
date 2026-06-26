// ============================================================================
// VehicleToolController.cs
// REST endpoints para config de Vehículo + Herramienta de PilotX desde HTML.
//   GET  /api/vehicle           → VehicleConfigDto
//   PUT  /api/vehicle           ← VehicleConfigDto
//   GET  /api/tool              → ToolConfigDto
//   PUT  /api/tool              ← ToolConfigDto
//   GET  /api/vehicle-tool      → { vehicle, tool }  (bundle conveniente)
//
// Deserialización con System.Text.Json: los DTOs traen [JsonPropertyName]
// y Swan los ignora (mismo bug que ya nos comió la config de QuantiX).
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SysJson = System.Text.Json.JsonSerializer;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class VehicleToolController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IVehicleToolService _svc;

        public VehicleToolController(IVehicleToolService svc) { _svc = svc; }

        [Route(HttpVerbs.Get, "/vehicle")]
        public object GetVehicle()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            return new { ok = true, vehicle = _svc.GetVehicle() };
        }

        [Route(HttpVerbs.Get, "/tool")]
        public object GetTool()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            return new { ok = true, tool = _svc.GetTool() };
        }

        [Route(HttpVerbs.Get, "/vehicle-tool")]
        public object GetBundle()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            var b = _svc.GetBundle();
            return new { ok = true, vehicle = b.Vehicle, tool = b.Tool };
        }

        [Route(HttpVerbs.Put, "/vehicle")]
        public async Task<object> PutVehicle()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            VehicleConfigDto cfg;
            try { cfg = SysJson.Deserialize<VehicleConfigDto>(body, JsonOpts); }
            catch (Exception ex) { return new { ok = false, error = "bad-json: " + ex.Message }; }
            if (cfg == null) return new { ok = false, error = "empty-body" };
            bool ok = _svc.SaveVehicle(cfg);
            return new { ok = ok };
        }

        [Route(HttpVerbs.Put, "/tool")]
        public async Task<object> PutTool()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            ToolConfigDto cfg;
            try { cfg = SysJson.Deserialize<ToolConfigDto>(body, JsonOpts); }
            catch (Exception ex) { return new { ok = false, error = "bad-json: " + ex.Message }; }
            if (cfg == null) return new { ok = false, error = "empty-body" };
            bool ok = _svc.SaveTool(cfg);
            return new { ok = ok };
        }
    }
}
