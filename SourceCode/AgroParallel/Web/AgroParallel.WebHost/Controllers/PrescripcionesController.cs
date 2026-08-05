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
//
// ⚠️  Las propiedades GeoJSON estándar (type, features, geometry, coordinates,
//     properties) NO se tocan. /preview/{id} devuelve el contenido del archivo
//     tal cual (raw passthrough). Solo los envelopes/estados propios usan AgpJson.
// ============================================================================

using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class PrescripcionesController : AgpControllerBase
    {
        private readonly IPrescripcionService _svc;

        public PrescripcionesController(IPrescripcionService svc) { _svc = svc; }

        [Route(HttpVerbs.Get, "/prescripciones/list")]
        public Task List()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, items = _svc.ListAvailable() });
        }

        [Route(HttpVerbs.Get, "/prescripciones/activa")]
        public Task GetActiva()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var a = _svc.GetActive();
            if (a == null) return WriteJsonAsync(new { ok = true, activa = (object)null });
            // Devolvemos los datos parseados pero sin los rings completos
            // (pueden ser miles de puntos). Para preview-en-mapa la UI usa
            // /preview/{id} que devuelve el GeoJSON crudo.
            return WriteJsonAsync(new
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
            });
        }

        [Route(HttpVerbs.Post, "/prescripciones/activa")]
        public async Task SetActiva()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body;
            string id = "", prop = "";
            try
            {
                body = await ReadBodyAsync();
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("id", out var jId))
                        id = jId.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("propiedad_dosis", out var jp))
                        prop = jp.GetString() ?? "";
                }
            }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            bool ok = _svc.SetActive(id, prop);
            await WriteJsonAsync(new { ok, activa = _svc.GetActive() });
        }

        [Route(HttpVerbs.Post, "/prescripciones/activa/clear")]
        public Task Clear()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            _svc.ClearActive();
            return WriteJsonAsync(new { ok = true });
        }

        // Edición de campo: cambiar la dosis de UNA zona desde el mini-mapa de
        // QuantiX → Shape. Body snake_case: { id, zona, dosis }. La zona es el
        // índice de feature del geojson — el mismo orden con el que el piloto
        // dibuja los polígonos de /api/aog/shape, así el cliente puede mandar
        // directamente el índice del polígono que el operario tocó.
        [Route(HttpVerbs.Post, "/prescripciones/dosis-zona")]
        public async Task SetZoneDose()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string id = ""; int zona = -1; double dosis = double.NaN;
            try
            {
                var body = await ReadBodyAsync();
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("id", out var jId)) id = jId.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("zona", out var jz) && jz.TryGetInt32(out var z)) zona = z;
                    if (doc.RootElement.TryGetProperty("dosis", out var jd) && jd.ValueKind == JsonValueKind.Number)
                        dosis = jd.GetDouble();
                }
            }
            catch { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            bool ok = _svc.SetZoneDose(id, zona, dosis);
            await WriteJsonAsync(new { ok, id, zona, dosis });
        }

        [Route(HttpVerbs.Get, "/prescripciones/dose")]
        public Task DoseAt()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var qs = HttpContext.Request.QueryString;
            double lat = 0, lon = 0;
            double.TryParse(qs["lat"] ?? "0", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lat);
            double.TryParse(qs["lon"] ?? "0", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lon);
            double dose = _svc.GetDoseAt(lat, lon);
            return WriteJsonAsync(new { ok = true, lat, lon, dose });
        }

        [Route(HttpVerbs.Get, "/prescripciones/preview/{id}")]
        public async Task<object> Preview(string id)
        {
            // Devuelve el GeoJSON crudo para que el frontend lo pinte con
            // Leaflet/MapLibre. Si no existe el archivo, 404.
            // ⚠️ Raw passthrough: no serializar con AgpJson para no alterar
            // las propiedades GeoJSON estándar (type, features, geometry, etc.).
            string dir = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory, "data", "prescripciones");
            if (!Directory.Exists(dir)) return new { ok = false, error = "dir-missing" };

            foreach (var f in Directory.GetFiles(dir, "*.geojson"))
            {
                string name = Path.GetFileNameWithoutExtension(f) ?? "";
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
