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
    public sealed class MapasController : WebApiController
    {
        private readonly IFieldMapsService _svc;

        public MapasController(IFieldMapsService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/mapas/sesiones")]
        public object GetSesiones()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            return _svc.ListSesiones();
        }

        [Route(HttpVerbs.Get, "/mapas/sesion/{ts}/heatmap")]
        public object GetHeatmap(string ts)
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            var fc = _svc.GetHeatmap(ts);
            if (fc == null) { HttpContext.Response.StatusCode = 404; return new { ok = false, error = "not-found" }; }
            return fc;
        }

        [Route(HttpVerbs.Get, "/mapas/sesion/{ts}/puntos")]
        public object GetPuntos(string ts)
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            var fc = _svc.GetPuntos(ts);
            if (fc == null) { HttpContext.Response.StatusCode = 404; return new { ok = false, error = "not-found" }; }
            return fc;
        }

        [Route(HttpVerbs.Get, "/mapas/boundary")]
        public object GetBoundary()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            var fc = _svc.GetBoundary();
            if (fc == null) { HttpContext.Response.StatusCode = 404; return new { ok = false, error = "no-boundary" }; }
            return fc;
        }

        [Route(HttpVerbs.Get, "/mapas/headland")]
        public object GetHeadland()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            var fc = _svc.GetHeadland();
            if (fc == null) { HttpContext.Response.StatusCode = 404; return new { ok = false, error = "no-headland" }; }
            return fc;
        }
    }
}
