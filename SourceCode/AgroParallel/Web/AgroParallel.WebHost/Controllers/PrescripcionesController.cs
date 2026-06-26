// ============================================================================
// PrescripcionesController.cs
// Endpoints REST de prescripciones variable-rate (Gap #5):
//   GET  /api/prescripciones/list             → lista de archivos disponibles
//   GET  /api/prescripciones/activa           → activa actual (parseada) o null
//   POST /api/prescripciones/activa           (body { id, propiedad_dosis })
//   POST /api/prescripciones/activa/clear     → desactiva (vuelve al shapefile)
//   GET  /api/prescripciones/dose?lat=&lon=   → dosis en un punto (debug/UI map)
//   GET  /api/prescripciones/preview/{id}     → GeoJSON raw del archivo (para
//                                               pintar overlay en el mapa)
// ============================================================================

using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class PrescripcionesController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IPrescripcionService _svc;

        public PrescripcionesController(IPrescripcionService svc) { _svc = svc; }

        [Route(HttpVerbs.Get, "/prescripciones/list")]
        public object List()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            return new { ok = true, items = _svc.ListAvailable() };
        }

        [Route(HttpVerbs.Get, "/prescripciones/activa")]
        public object GetActiva()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            var a = _svc.GetActive();
            if (a == null) return new { ok = true, activa = (object)null };
            // Devolvemos los datos parseados pero sin los rings completos
            // (pueden ser miles de puntos). Para preview-en-mapa la UI usa
            // /preview/{id} que devuelve el GeoJSON crudo.
            return new
            {
                ok = true,
                activa = new
                {
                    id = a.Id,
                    nombre = a.Nombre,
                    propiedad_dosis = a.PropiedadDosis,
                    feature_count = a.FeatureCount,
                    min_lon = a.MinLon,
                    min_lat = a.MinLat,
                    max_lon = a.MaxLon,
                    max_lat = a.MaxLat,
                    loaded_utc = a.LoadedUtc
                }
            };
        }

        [Route(HttpVerbs.Post, "/prescripciones/activa")]
        public async Task<object> SetActiva()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            string id = "", prop = "";
            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("id", out var jId))
                        id = jId.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("propiedad_dosis", out var jp))
                        prop = jp.GetString() ?? "";
                }
            }
            catch { return new { ok = false, error = "invalid-body" }; }

            bool ok = _svc.SetActive(id, prop);
            return new { ok, activa = _svc.GetActive() };
        }

        [Route(HttpVerbs.Post, "/prescripciones/activa/clear")]
        public object Clear()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            _svc.ClearActive();
            return new { ok = true };
        }

        [Route(HttpVerbs.Get, "/prescripciones/dose")]
        public object DoseAt()
        {
            if (_svc == null) return new { ok = false, error = "service-unavailable" };
            var qs = HttpContext.Request.QueryString;
            double lat = 0, lon = 0;
            double.TryParse(qs["lat"] ?? "0", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lat);
            double.TryParse(qs["lon"] ?? "0", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lon);
            double dose = _svc.GetDoseAt(lat, lon);
            return new { ok = true, lat, lon, dose };
        }

        [Route(HttpVerbs.Get, "/prescripciones/preview/{id}")]
        public async Task<object> Preview(string id)
        {
            // Devuelve el GeoJSON crudo para que el frontend lo pinte con
            // Leaflet/MapLibre. Si no existe el archivo, 404.
            string dir = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory, "data", "prescripciones");
            if (!Directory.Exists(dir)) return new { ok = false, error = "dir-missing" };

            foreach (var f in Directory.GetFiles(dir, "*.geojson"))
            {
                string name = Path.GetFileNameWithoutExtension(f) ?? "";
                // Comparación simple slug→filename (reaprovechamos la del service
                // duplicando lógica para no exponerla en la interface).
                string slug = Slug(name);
                if (slug == id)
                {
                    string content = await Task.Run(() => File.ReadAllText(f));
                    HttpContext.Response.ContentType = "application/geo+json";
                    return content;
                }
            }
            return new { ok = false, error = "not-found" };
        }

        private static string Slug(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            string s = sb.ToString().Trim('-');
            while (s.Contains("--")) s = s.Replace("--", "-");
            return s;
        }
    }
}
