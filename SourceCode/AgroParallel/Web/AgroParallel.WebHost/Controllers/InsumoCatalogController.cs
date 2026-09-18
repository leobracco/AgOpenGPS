// ============================================================================
// InsumoCatalogController.cs
// Endpoints REST del catálogo de insumos compartido:
//   GET  /api/insumos              → InsumoCatalogDto (todo el catálogo)
//   POST /api/insumos              (body = InsumoCatalogDto) → { ok }
//   GET  /api/insumos/activo       → InsumoDto | { ok:false, error:"none" }
//   POST /api/insumos/activo       (body = { id: "..." }) → { ok, activo }
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class InsumoCatalogController : AgpControllerBase
    {
        private readonly IInsumoCatalogService _svc;

        public InsumoCatalogController(IInsumoCatalogService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/insumos")]
        public Task Get()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.Load());
        }

        [Route(HttpVerbs.Post, "/insumos")]
        public async Task Save()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var dto = await ReadJsonBodyAsync<InsumoCatalogDto>();
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            _svc.Save(dto);
            await WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Get, "/insumos/activo")]
        public Task GetActivo()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var activo = _svc.GetActivo();
            if (activo == null) return WriteJsonAsync(new { ok = false, error = "none" });
            return WriteJsonAsync(activo);
        }

        // Body esperado: { "id": "soja-dm-46i17" }. id="" deselecciona.
        [Route(HttpVerbs.Post, "/insumos/activo")]
        public async Task SetActivo()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body;
            try
            {
                body = await ReadBodyAsync();
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    string id = "";
                    if (doc.RootElement.TryGetProperty("id", out var jId))
                        id = jId.GetString() ?? "";
                    bool ok = _svc.SetActivo(id);
                    var activoDto = _svc.GetActivo();
                    await WriteJsonAsync(new { ok, activo = activoDto });
                }
            }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); }
        }
    }
}
