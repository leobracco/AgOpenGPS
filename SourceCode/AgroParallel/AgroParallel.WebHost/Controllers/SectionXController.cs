// ============================================================================
// SectionXController.cs
// Endpoints REST del módulo SectionX:
//   GET  /api/sectionx/config   → SectionXConfigDto
//   POST /api/sectionx/config   (body = SectionXConfigDto) → { ok }
// ============================================================================

using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SectionXController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ISectionXConfigService _cfg;

        public SectionXController(ISectionXConfigService cfg)
        {
            _cfg = cfg;
        }

        [Route(HttpVerbs.Get, "/sectionx/config")]
        public object GetConfig()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            return _cfg.Load();
        }

        [Route(HttpVerbs.Post, "/sectionx/config")]
        public async Task<object> SaveConfig()
        {
            if (_cfg == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            var dto = JsonSerializer.Deserialize<SectionXConfigDto>(body, JsonOpts);
            if (dto == null) return new { ok = false, error = "invalid-body" };
            _cfg.Save(dto);
            return new { ok = true };
        }
    }
}
