// ============================================================================
// InsumoCatalogController.cs
// Endpoints REST del catálogo de insumos compartido:
//   GET  /api/insumos              → InsumoCatalogDto (todo el catálogo)
//   POST /api/insumos              (body = InsumoCatalogDto) → { ok }
//   GET  /api/insumos/activo       → InsumoDto | { ok:false, error:"none" }
//   POST /api/insumos/activo       (body = { id: "..." }) → { ok, activo }
// ============================================================================

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
    public sealed class InsumoCatalogController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // El ResponseSerializer default (Swan) ignora [JsonPropertyName] de
        // System.Text.Json y emite PascalCase. Los DTOs InsumoCatalogDto /
        // InsumoDto traen [JsonPropertyName("activo_id"|"items"|"nombre"|...)]
        // y la UI lee snake_case. Serializamos a mano con System.Text.Json
        // para que respete los atributos.
        private static readonly JsonSerializerOptions JsonOutOpts = new JsonSerializerOptions
        {
            // Sin policy: cada propiedad usa su [JsonPropertyName] explícito.
        };

        private readonly IInsumoCatalogService _svc;

        public InsumoCatalogController(IInsumoCatalogService svc)
        {
            _svc = svc;
        }

        private async Task SendJsonAsync(object obj)
        {
            string json = JsonSerializer.Serialize(obj, JsonOutOpts);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/insumos")]
        public async Task Get()
        {
            if (_svc == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            await SendJsonAsync(_svc.Load());
        }

        [Route(HttpVerbs.Post, "/insumos")]
        public async Task<object> Save()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            var dto = JsonSerializer.Deserialize<InsumoCatalogDto>(body, JsonOpts);
            if (dto == null) return new { ok = false, error = "invalid-body" };
            _svc.Save(dto);
            return new { ok = true };
        }

        [Route(HttpVerbs.Get, "/insumos/activo")]
        public async Task GetActivo()
        {
            if (_svc == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var activo = _svc.GetActivo();
            if (activo == null) { await SendJsonAsync(new { ok = false, error = "none" }); return; }
            await SendJsonAsync(activo);
        }

        // Body esperado: { "id": "soja-dm-46i17" }. id="" deselecciona.
        [Route(HttpVerbs.Post, "/insumos/activo")]
        public async Task SetActivo()
        {
            if (_svc == null) { await SendJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            string id = "";
            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("id", out var jId))
                        id = jId.GetString() ?? "";
                }
            }
            catch { await SendJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            bool ok = _svc.SetActivo(id);
            // activo se inlinea como JsonElement para que su [JsonPropertyName] aplique.
            var activoDto = _svc.GetActivo();
            string activoJson = activoDto == null ? "null"
                : JsonSerializer.Serialize(activoDto, JsonOutOpts);
            string body2 = "{\"ok\":" + (ok ? "true" : "false") + ",\"activo\":" + activoJson + "}";
            await HttpContext.SendStringAsync(body2, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
