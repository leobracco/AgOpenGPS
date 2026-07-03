// ============================================================================
// MapasController.cs — endpoints REST de la página Mapas en el Hub.
//
//   GET /api/mapas/sesiones                  → MapasSesionesDto
//   GET /api/mapas/sesion/{ts}/heatmap       → GeoJSON (Polygon FeatureCollection)
//   GET /api/mapas/sesion/{ts}/puntos        → GeoJSON (Point FeatureCollection)
//   GET /api/mapas/boundary                  → GeoJSON Polygon del lote
//   GET /api/mapas/headland                  → GeoJSON LineString cabecera
//
// Todas las respuestas en WGS84 [lon,lat]. 404 si no hay lote / sesión no existe.
// ============================================================================

using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class MapasController : AgpControllerBase
    {
        private readonly IFieldMapsService _svc;

        public MapasController(IFieldMapsService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/mapas/sesiones")]
        public System.Threading.Tasks.Task GetSesiones()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.ListSesiones());
        }

        [Route(HttpVerbs.Get, "/mapas/sesion/{ts}/heatmap")]
        public System.Threading.Tasks.Task GetHeatmap(string ts)
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var fc = _svc.GetHeatmap(ts);
            if (fc == null) { HttpContext.Response.StatusCode = 404; return WriteJsonAsync(new { ok = false, error = "not-found" }); }
            return WriteJsonAsync(fc);
        }

        [Route(HttpVerbs.Get, "/mapas/sesion/{ts}/puntos")]
        public System.Threading.Tasks.Task GetPuntos(string ts)
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var fc = _svc.GetPuntos(ts);
            if (fc == null) { HttpContext.Response.StatusCode = 404; return WriteJsonAsync(new { ok = false, error = "not-found" }); }
            return WriteJsonAsync(fc);
        }

        [Route(HttpVerbs.Get, "/mapas/boundary")]
        public System.Threading.Tasks.Task GetBoundary()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var fc = _svc.GetBoundary();
            if (fc == null) { HttpContext.Response.StatusCode = 404; return WriteJsonAsync(new { ok = false, error = "no-boundary" }); }
            return WriteJsonAsync(fc);
        }

        [Route(HttpVerbs.Get, "/mapas/headland")]
        public System.Threading.Tasks.Task GetHeadland()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var fc = _svc.GetHeadland();
            if (fc == null) { HttpContext.Response.StatusCode = 404; return WriteJsonAsync(new { ok = false, error = "no-headland" }); }
            return WriteJsonAsync(fc);
        }
    }
}
