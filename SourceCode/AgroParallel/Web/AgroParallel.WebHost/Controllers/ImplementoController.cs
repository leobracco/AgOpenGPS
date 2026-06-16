// ============================================================================
// ImplementoController.cs — REST del implemento central.
//
// IMPORTANTE: EmbedIO usa Swan.Json por default y NO honra [JsonPropertyName],
// por lo que los DTOs salían con keys PascalCase y el JS no las leía. Acá
// serializamos el body de la respuesta manualmente con System.Text.Json y lo
// escribimos a HttpContext.Response para preservar snake_case (mismo workaround
// que QuantiXController / VistaXController).
//
// Endpoints:
//   GET    /api/implemento                → ImplementoDto del ACTIVO
//   PUT    /api/implemento                → guarda en el ACTIVO
//
//   GET    /api/implementos               → { activo, lista:[{slug,nombre,activo}] }
//   GET    /api/implementos/{slug}        → ImplementoDto de ese slug
//   PUT    /api/implementos/{slug}        → guarda ese slug
//   DELETE /api/implementos/{slug}        → borra
//   POST   /api/implementos/activo        body: {slug}
//   POST   /api/implementos/nuevo         body: {nombre} → crea blank + lo activa
//   POST   /api/implementos/copiar        body: {from,nombre} → duplica + lo activa
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SysJson = System.Text.Json.JsonSerializer;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ImplementoController : WebApiController
    {
        private static readonly System.Text.Json.JsonSerializerOptions JsonOpts =
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private readonly IImplementoService _svc;
        private readonly INodosCuratedService _curated;

        public ImplementoController(IImplementoService svc, INodosCuratedService curated = null)
        {
            _svc = svc;
            _curated = curated;
        }

        // Cuando el operario cambia de perfil (o crea/copia uno y queda activo),
        // si ese implemento tiene nodos VistaX/QuantiX/FlowX asignados encendemos
        // el overlay correspondiente para que no tenga que ir al Hub. Solo PRENDE
        // — si el operario apagó un overlay, respetamos esa decisión.
        private void AutoOpenOverlays()
        {
            if (_curated == null) return;
            OverlayAutoOpener.EnsureForActiveImplemento(_svc, _curated);
        }

        // ---- helpers de respuesta -------------------------------------

        private async Task WriteJson(object payload)
        {
            HttpContext.Response.ContentType = "application/json; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(SysJson.Serialize(payload, JsonOpts));
            await HttpContext.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
                .ConfigureAwait(false);
        }

        private async Task<string> ReadBody()
        {
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                return await sr.ReadToEndAsync().ConfigureAwait(false);
        }

        // ---- ACTIVO (compat: rutas pre-CRUD) ---------------------------

        [Route(HttpVerbs.Get, "/implemento")]
        public async Task GetActivo()
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            var dto = _svc.GetImplemento();
            await WriteJson(new
            {
                ok = true,
                slug = _svc.GetActiveSlug(),
                path = _svc.GetPath(),
                implemento = dto
            });
        }

        [Route(HttpVerbs.Put, "/implemento")]
        public async Task PutActivo()
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBody();
            ImplementoDto dto;
            try { dto = SysJson.Deserialize<ImplementoDto>(body, JsonOpts); }
            catch (Exception ex) { await WriteJson(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (dto == null) { await WriteJson(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.SaveImplemento(dto);
            await WriteJson(new { ok = ok, slug = _svc.GetActiveSlug() });
        }

        // ---- CRUD multi-implemento -------------------------------------

        [Route(HttpVerbs.Get, "/implementos")]
        public async Task ListImplementos()
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            // Forzamos bootstrap implícito via GetImplemento.
            _svc.GetImplemento();
            await WriteJson(new
            {
                ok = true,
                activo = _svc.GetActiveSlug(),
                lista = _svc.List()
            });
        }

        [Route(HttpVerbs.Get, "/implementos/{slug}")]
        public async Task GetBySlug(string slug)
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            var dto = _svc.Load(slug);
            if (dto == null) { await WriteJson(new { ok = false, error = "not-found" }); return; }
            await WriteJson(new { ok = true, slug = slug, implemento = dto });
        }

        [Route(HttpVerbs.Put, "/implementos/{slug}")]
        public async Task PutBySlug(string slug)
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBody();
            ImplementoDto dto;
            try { dto = SysJson.Deserialize<ImplementoDto>(body, JsonOpts); }
            catch (Exception ex) { await WriteJson(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (dto == null) { await WriteJson(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.Save(slug, dto);
            await WriteJson(new { ok = ok, slug = slug });
        }

        [Route(HttpVerbs.Delete, "/implementos/{slug}")]
        public async Task DeleteBySlug(string slug)
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            bool ok = _svc.Delete(slug);
            await WriteJson(new { ok = ok, activo = _svc.GetActiveSlug() });
        }

        [Route(HttpVerbs.Post, "/implementos/activo")]
        public async Task SetActivo()
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBody();
            string slug = "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                    if (doc.RootElement.TryGetProperty("slug", out var el))
                        slug = el.GetString() ?? "";
            }
            catch { await WriteJson(new { ok = false, error = "bad-json" }); return; }
            bool ok = _svc.SetActive(slug);
            if (ok) AutoOpenOverlays();
            await WriteJson(new { ok = ok, activo = _svc.GetActiveSlug() });
        }

        [Route(HttpVerbs.Post, "/implementos/nuevo")]
        public async Task Nuevo()
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBody();
            string nombre = "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                    if (doc.RootElement.TryGetProperty("nombre", out var el))
                        nombre = el.GetString() ?? "";
            }
            catch { /* nombre vacío permitido */ }

            if (string.IsNullOrWhiteSpace(nombre)) nombre = "Implemento nuevo";
            string slug = ImplementoService.MakeSlug(nombre);
            // Evitar colisión: si ya existe, sufijo numérico.
            int i = 2; string baseSlug = slug;
            while (_svc.Load(slug) != null) slug = baseSlug + "-" + (i++);

            var dto = new ImplementoDto { Nombre = nombre };
            dto.Trenes.Add(new TrenDto { Id = 1, Nombre = "Tren único", DistanciaM = 0 });
            dto.Secciones.Add(new SeccionDto { Id = 1, Nombre = "Sección 1" });
            bool ok = _svc.Save(slug, dto);
            if (ok) { _svc.SetActive(slug); AutoOpenOverlays(); }
            await WriteJson(new { ok = ok, slug = slug });
        }

        // ---- Catálogo de modelos de sembradoras ------------------------
        // GET  /api/catalogo/sembradoras → list agrupada por marca
        // POST /api/implemento/aplicar-plantilla {marca, modelo}
        //       → busca template y mergea sus campos en el implemento activo,
        //         preservando ancho/overlap/hitch/secciones existentes.

        [Route(HttpVerbs.Get, "/catalogo/sembradoras")]
        public async Task GetCatalogo()
        {
            await WriteJson(new
            {
                ok = true,
                marcas = SembradorasCatalog.GroupedByMarca()
            });
        }

        [Route(HttpVerbs.Post, "/implemento/aplicar-plantilla")]
        public async Task AplicarPlantilla()
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBody();
            string marca = "", modelo = "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("marca", out var em)) marca = em.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("modelo", out var emo)) modelo = emo.GetString() ?? "";
                }
            }
            catch { await WriteJson(new { ok = false, error = "bad-json" }); return; }

            var tpl = SembradorasCatalog.Find(marca, modelo);
            if (tpl == null) { await WriteJson(new { ok = false, error = "template-not-found" }); return; }

            var dto = _svc.GetImplemento();
            if (dto == null) dto = new ImplementoDto();

            // Mergea campos físicos + metadata. NO toca:
            //  · Nombre del implemento (es del usuario)
            //  · AnchoTotalM (lo decide el operario o sale de la geometría AOG)
            //  · OverlapM / HitchLengthM / lookaheads (config de PilotX)
            //  · Trenes / Surcos / Secciones (estructura ya armada)
            dto.Categoria = "sembradora";
            dto.Marca = tpl.Marca;
            dto.Modelo = tpl.Modelo;
            dto.TipoCultivo = tpl.TipoCultivo;
            dto.TipoSiembra = tpl.TipoSiembra;
            dto.TipoDosificador = tpl.TipoDosificador;
            dto.NumeroTorres = tpl.NumeroTorres;
            dto.TieneFertilizacion = tpl.TieneFertilizacion;
            dto.TipoEstructura = tpl.TipoEstructura;
            // Si el implemento está vacío (recién creado), también pre-llenamos
            // las dimensiones físicas — si ya tiene surcos definidos no las pisamos.
            bool implementoVacio = (dto.Surcos == null || dto.Surcos.Count == 0);
            if (dto.NumeroSurcos <= 0)
                dto.NumeroSurcos = tpl.NumeroSurcos;
            if (dto.DistanciaEntreSurcosM <= 0)
                dto.DistanciaEntreSurcosM = tpl.DistanciaEntreSurcosM;

            // Si el implemento está vacío y el template define una estructura
            // multi-tren (Tanzi 14500 = 2 trenes), creamos los trenes y
            // distribuimos los surcos consecutivos: primera mitad → tren 1
            // (trasero), segunda mitad → tren 2 (delantero, distancia_m > 0).
            // Esto coincide con la convención del visualizador y del mock Tanzi
            // (numeración 1..N de atrás-izq hacia adelante-der).
            if (implementoVacio && tpl.NumeroTrenes >= 2 && tpl.NumeroSurcos > 0)
            {
                dto.Trenes = new List<TrenDto>();
                for (int t = 1; t <= tpl.NumeroTrenes; t++)
                {
                    dto.Trenes.Add(new TrenDto
                    {
                        Id = t,
                        Nombre = tpl.NumeroTrenes == 2
                            ? (t == 1 ? "Trasero" : "Delantero")
                            : ("Tren " + t),
                        // Distancia típica entre trenes en air-drill = ~0.7 m.
                        // Tren 1 = referencia (0). El resto desplazados hacia atrás
                        // siguiendo la convención (distancia_m > 0 = más cerca del tractor).
                        DistanciaM = (t - 1) * 0.7
                    });
                }
                dto.Surcos = new List<SurcoDto>();
                int surcosPorTren = tpl.NumeroSurcos / tpl.NumeroTrenes;
                int sobran = tpl.NumeroSurcos - surcosPorTren * tpl.NumeroTrenes;
                int numero = 1;
                for (int t = 0; t < tpl.NumeroTrenes; t++)
                {
                    int cant = surcosPorTren + (t < sobran ? 1 : 0);
                    int trenId = t + 1;
                    for (int k = 0; k < cant; k++)
                    {
                        dto.Surcos.Add(new SurcoDto
                        {
                            Numero = numero++,
                            TrenId = trenId,
                            SeccionPilotX = 0
                        });
                    }
                }
                dto.NumeroSurcos = tpl.NumeroSurcos;
            }

            bool ok = _svc.SaveImplemento(dto);
            await WriteJson(new { ok = ok, slug = _svc.GetActiveSlug(), implemento = dto });
        }

        // ---- Catálogo de OTRA maquinaria (cosechadora/pulverizadora/fertilizadora) ----
        // GET  /api/catalogo/maquinas → tipos → marcas → modelos
        // POST /api/implemento/aplicar-maquina {categoria, marca, modelo}
        //       → setea ancho de labor + secciones + categoría/marca/modelo y
        //         limpia la estructura de sembradora (surcos/trenes/torres).

        [Route(HttpVerbs.Get, "/catalogo/maquinas")]
        public async Task GetCatalogoMaquinas()
        {
            await WriteJson(new
            {
                ok = true,
                tipos = MaquinasCatalog.GroupedByTipo()
            });
        }

        [Route(HttpVerbs.Post, "/implemento/aplicar-maquina")]
        public async Task AplicarMaquina()
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBody();
            string categoria = "", marca = "", modelo = "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("categoria", out var ec)) categoria = ec.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("marca", out var em)) marca = em.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("modelo", out var emo)) modelo = emo.GetString() ?? "";
                }
            }
            catch { await WriteJson(new { ok = false, error = "bad-json" }); return; }

            var tpl = MaquinasCatalog.Find(categoria, marca, modelo);
            if (tpl == null) { await WriteJson(new { ok = false, error = "template-not-found" }); return; }

            var dto = _svc.GetImplemento();
            if (dto == null) dto = new ImplementoDto();

            // Identidad + ancho de labor (dato principal del catálogo de máquinas).
            dto.Categoria = tpl.Categoria;
            dto.Marca = tpl.Marca;
            dto.Modelo = tpl.Modelo;
            dto.AnchoTotalM = tpl.AnchoLaborM;

            // Las máquinas no sembradoras no tienen surcos/torres ni dosificador
            // de siembra: limpiamos esa estructura para que la página no muestre
            // datos de sembradora que no aplican.
            dto.NumeroSurcos = 0;
            dto.Surcos = new List<SurcoDto>();
            dto.Trenes = new List<TrenDto> { new TrenDto { Id = 1, Nombre = "Tren único", DistanciaM = 0 } };
            dto.NumeroTorres = 0;
            dto.TipoCultivo = "";
            dto.TipoSiembra = "";
            dto.TipoDosificador = "";
            dto.TipoEstructura = "";
            dto.TieneFertilizacion = (tpl.Categoria == "fertilizadora");

            // Secciones: una por sección de corte del modelo (mínimo 1).
            int n = tpl.NumeroSecciones > 0 ? tpl.NumeroSecciones : 1;
            dto.Secciones = new List<SeccionDto>();
            for (int i = 1; i <= n; i++)
                dto.Secciones.Add(new SeccionDto { Id = i, Nombre = "Sección " + i });

            bool ok = _svc.SaveImplemento(dto);
            await WriteJson(new { ok = ok, slug = _svc.GetActiveSlug(), implemento = dto });
        }

        [Route(HttpVerbs.Post, "/implementos/copiar")]
        public async Task Copiar()
        {
            if (_svc == null) { await WriteJson(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBody();
            string from = "", nombre = "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("from", out var ef)) from = ef.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("nombre", out var en)) nombre = en.GetString() ?? "";
                }
            }
            catch { await WriteJson(new { ok = false, error = "bad-json" }); return; }
            if (string.IsNullOrWhiteSpace(from)) { await WriteJson(new { ok = false, error = "from-required" }); return; }
            if (string.IsNullOrWhiteSpace(nombre)) nombre = from + " (copia)";

            string slug = ImplementoService.MakeSlug(nombre);
            int i = 2; string baseSlug = slug;
            while (_svc.Load(slug) != null) slug = baseSlug + "-" + (i++);

            bool ok = _svc.Copy(from, slug, nombre);
            if (ok) { _svc.SetActive(slug); AutoOpenOverlays(); }
            await WriteJson(new { ok = ok, slug = slug });
        }
    }
}
