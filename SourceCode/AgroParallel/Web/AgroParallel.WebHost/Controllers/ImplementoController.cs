// ============================================================================
// ImplementoController.cs — REST del implemento central.
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
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.Common;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ImplementoController : AgpControllerBase
    {
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

        // ---- ACTIVO (compat: rutas pre-CRUD) ---------------------------

        [Route(EmbedIO.HttpVerbs.Get, "/implemento")]
        public async Task GetActivo()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var dto = _svc.GetImplemento();
            await WriteJsonAsync(new
            {
                ok = true,
                slug = _svc.GetActiveSlug(),
                path = _svc.GetPath(),
                implemento = dto
            });
        }

        [Route(EmbedIO.HttpVerbs.Put, "/implemento")]
        public async Task PutActivo()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            ImplementoDto dto;
            try { dto = await ReadJsonBodyAsync<ImplementoDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            // Validar ANTES de persistir — el PUT es reemplazo completo del DTO.
            var val = ConfigValidation.ValidarImplemento(dto);
            if (!val.Ok)
            {
                await WriteErrorAsync(400, "AGP-CFG-001", "Config inválida", string.Join("; ", val.Errores));
                return;
            }
            // Defensa contra clientes viejos (ej. herramienta.js) que mandan
            // surcos desalineados con numero_surcos: regeneramos 1:1 igual que
            // hace la UI nueva, así el shape queda siempre consistente.
            if (dto.Surcos == null || dto.Surcos.Count != dto.NumeroSurcos)
                ImplementoSurcos.Regenerar(dto, dto.NumeroSurcos);
            // Ídem con la lista de Secciones: el write-back central→Tool deriva
            // NumSections de acá — desalineada, pisaba el guiado ("14 → 3").
            if (dto.Secciones == null || dto.Secciones.Count != dto.NumeroSurcos)
                ImplementoSurcos.SincronizarSecciones(dto);
            // Trenes: error de REPORTE nomás (ver ValidarTrenes) — no bloquea el guardado.
            var valTrenes = ConfigValidation.ValidarTrenes(dto);
            if (!valTrenes.Ok)
                AgpLog.Warn("Implemento", "trenes inconsistentes al guardar: " + string.Join("; ", valTrenes.Errores));
            bool ok = _svc.SaveImplemento(dto);
            await WriteJsonAsync(new { ok = ok, slug = _svc.GetActiveSlug() });
        }

        // ---- CRUD multi-implemento -------------------------------------

        [Route(EmbedIO.HttpVerbs.Get, "/implementos")]
        public async Task ListImplementos()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            // Forzamos bootstrap implícito via GetImplemento.
            _svc.GetImplemento();
            await WriteJsonAsync(new
            {
                ok = true,
                activo = _svc.GetActiveSlug(),
                lista = _svc.List()
            });
        }

        [Route(EmbedIO.HttpVerbs.Get, "/implementos/{slug}")]
        public async Task GetBySlug(string slug)
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var dto = _svc.Load(slug);
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "not-found" }); return; }
            await WriteJsonAsync(new { ok = true, slug = slug, implemento = dto });
        }

        [Route(EmbedIO.HttpVerbs.Put, "/implementos/{slug}")]
        public async Task PutBySlug(string slug)
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            ImplementoDto dto;
            try { dto = await ReadJsonBodyAsync<ImplementoDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            // Validar ANTES de persistir — el PUT es reemplazo completo del DTO.
            var val = ConfigValidation.ValidarImplemento(dto);
            if (!val.Ok)
            {
                await WriteErrorAsync(400, "AGP-CFG-001", "Config inválida", string.Join("; ", val.Errores));
                return;
            }
            // Defensa contra clientes viejos (ej. herramienta.js) que mandan
            // surcos desalineados con numero_surcos: regeneramos 1:1 igual que
            // hace la UI nueva, así el shape queda siempre consistente.
            if (dto.Surcos == null || dto.Surcos.Count != dto.NumeroSurcos)
                ImplementoSurcos.Regenerar(dto, dto.NumeroSurcos);
            // Ídem con la lista de Secciones: el write-back central→Tool deriva
            // NumSections de acá — desalineada, pisaba el guiado ("14 → 3").
            if (dto.Secciones == null || dto.Secciones.Count != dto.NumeroSurcos)
                ImplementoSurcos.SincronizarSecciones(dto);
            // Trenes: error de REPORTE nomás (ver ValidarTrenes) — no bloquea el guardado.
            var valTrenes = ConfigValidation.ValidarTrenes(dto);
            if (!valTrenes.Ok)
                AgpLog.Warn("Implemento", "trenes inconsistentes al guardar: " + string.Join("; ", valTrenes.Errores));
            bool ok = _svc.Save(slug, dto);
            await WriteJsonAsync(new { ok = ok, slug = slug });
        }

        [Route(EmbedIO.HttpVerbs.Delete, "/implementos/{slug}")]
        public async Task DeleteBySlug(string slug)
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            bool ok = _svc.Delete(slug);
            await WriteJsonAsync(new { ok = ok, activo = _svc.GetActiveSlug() });
        }

        [Route(EmbedIO.HttpVerbs.Post, "/implementos/activo")]
        public async Task SetActivo()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBodyAsync();
            string slug = "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                    if (doc.RootElement.TryGetProperty("slug", out var el))
                        slug = el.GetString() ?? "";
            }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            bool ok = _svc.SetActive(slug);
            if (ok) AutoOpenOverlays();
            await WriteJsonAsync(new { ok = ok, activo = _svc.GetActiveSlug() });
        }

        [Route(EmbedIO.HttpVerbs.Post, "/implementos/nuevo")]
        public async Task Nuevo()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBodyAsync();
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
            await WriteJsonAsync(new { ok = ok, slug = slug });
        }

        // ---- Catálogo de modelos de sembradoras ------------------------
        // GET  /api/catalogo/sembradoras → list agrupada por marca
        // POST /api/implemento/aplicar-plantilla {marca, modelo}
        //       → busca template y mergea sus campos en el implemento activo,
        //         preservando ancho/overlap/hitch/secciones existentes.

        [Route(EmbedIO.HttpVerbs.Get, "/catalogo/sembradoras")]
        public async Task GetCatalogo()
        {
            await WriteJsonAsync(new
            {
                ok = true,
                marcas = SembradorasCatalog.GroupedByMarca()
            });
        }

        [Route(EmbedIO.HttpVerbs.Post, "/implemento/aplicar-plantilla")]
        public async Task AplicarPlantilla()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBodyAsync();
            string marca = "", modelo = "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("marca", out var em)) marca = em.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("modelo", out var emo)) modelo = emo.GetString() ?? "";
                }
            }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }

            var tpl = SembradorasCatalog.Find(marca, modelo);
            if (tpl == null) { await WriteJsonAsync(new { ok = false, error = "template-not-found" }); return; }

            // RMW atómico vía Update — GetImplemento() devuelve la instancia
            // CACHEADA que QuantiXMotorBridge (5 Hz) y SectionXCutAdapter (10 Hz)
            // enumeran en paralelo; mutarla in situ (como antes) les puede volar
            // un InvalidOperationException en pleno tick. Update() opera sobre
            // una copia fresca leída de disco y sólo publica el resultado ya
            // terminado (ver ImplementoService.Update).
            _svc.GetImplemento(); // fuerza bootstrap/activo sin tocar el cache compartido
            string slug = _svc.GetActiveSlug();
            if (string.IsNullOrEmpty(slug)) { await WriteJsonAsync(new { ok = false, error = "no-active-implemento" }); return; }

            ImplementoDto dto = _svc.Update(slug, d =>
            {
                // Mergea campos físicos + metadata. NO toca:
                //  · Nombre del implemento (es del usuario)
                //  · AnchoTotalM (lo decide el operario o sale de la geometría de PilotX)
                //  · OverlapM / HitchLengthM / lookaheads (config de PilotX)
                //  · Trenes / Surcos / Secciones (estructura ya armada, salvo abajo)
                d.Categoria = "sembradora";
                d.Marca = tpl.Marca;
                d.Modelo = tpl.Modelo;
                d.TipoCultivo = tpl.TipoCultivo;
                d.TipoSiembra = tpl.TipoSiembra;
                d.TipoDosificador = tpl.TipoDosificador;
                d.NumeroTorres = tpl.NumeroTorres;
                d.TieneFertilizacion = tpl.TieneFertilizacion;
                d.TipoEstructura = tpl.TipoEstructura;
                // Si el implemento está vacío (recién creado), también pre-llenamos
                // las dimensiones físicas — si ya tiene surcos definidos no las pisamos.
                bool implementoVacio = (d.Surcos == null || d.Surcos.Count == 0);
                if (d.NumeroSurcos <= 0)
                    d.NumeroSurcos = tpl.NumeroSurcos;
                if (d.DistanciaEntreSurcosM <= 0)
                    d.DistanciaEntreSurcosM = tpl.DistanciaEntreSurcosM;

                // Si el implemento está vacío y el template define una estructura
                // multi-tren (Tanzi 14500 = 2 trenes), creamos los trenes y
                // distribuimos los surcos consecutivos: primera mitad → tren 1
                // (delantero), segunda mitad → tren 2 (trasero). Convención
                // validada en ConfigValidation.ValidarTrenes: "tren 1 = delantero,
                // distancia 0". Ningún tren arranca con distancia inventada — todos
                // en 0 hasta que el operario mida y cargue la distancia real; con
                // todo en 0 la guarda de TrenResolver ("sin distancias reales")
                // mantiene el fallback manual por nodo hasta ese momento.
                if (implementoVacio && tpl.NumeroTrenes >= 2 && tpl.NumeroSurcos > 0)
                {
                    var trenes = new List<TrenDto>();
                    for (int t = 1; t <= tpl.NumeroTrenes; t++)
                    {
                        trenes.Add(new TrenDto
                        {
                            Id = t,
                            Nombre = tpl.NumeroTrenes == 2
                                ? (t == 1 ? "Delantero" : "Trasero")
                                : ("Tren " + t),
                            DistanciaM = 0
                        });
                    }
                    var surcos = new List<SurcoDto>();
                    int surcosPorTren = tpl.NumeroSurcos / tpl.NumeroTrenes;
                    int sobran = tpl.NumeroSurcos - surcosPorTren * tpl.NumeroTrenes;
                    int numero = 1;
                    for (int t = 0; t < tpl.NumeroTrenes; t++)
                    {
                        int cant = surcosPorTren + (t < sobran ? 1 : 0);
                        int trenId = t + 1;
                        for (int k = 0; k < cant; k++)
                        {
                            surcos.Add(new SurcoDto
                            {
                                Numero = numero++,
                                TrenId = trenId,
                                SeccionPilotX = 0
                            });
                        }
                    }
                    d.Trenes = trenes;
                    d.Surcos = surcos;
                    d.NumeroSurcos = tpl.NumeroSurcos;
                }
            });

            bool ok = dto != null;
            await WriteJsonAsync(new { ok = ok, slug = slug, implemento = dto });
        }

        // ---- Catálogo de OTRA maquinaria (cosechadora/pulverizadora/fertilizadora) ----
        // GET  /api/catalogo/maquinas → tipos → marcas → modelos
        // POST /api/implemento/aplicar-maquina {categoria, marca, modelo}
        //       → setea ancho de labor + secciones + categoría/marca/modelo y
        //         limpia la estructura de sembradora (surcos/trenes/torres).

        [Route(EmbedIO.HttpVerbs.Get, "/catalogo/maquinas")]
        public async Task GetCatalogoMaquinas()
        {
            await WriteJsonAsync(new
            {
                ok = true,
                tipos = MaquinasCatalog.GroupedByTipo()
            });
        }

        [Route(EmbedIO.HttpVerbs.Post, "/implemento/aplicar-maquina")]
        public async Task AplicarMaquina()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBodyAsync();
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
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }

            var tpl = MaquinasCatalog.Find(categoria, marca, modelo);
            if (tpl == null) { await WriteJsonAsync(new { ok = false, error = "template-not-found" }); return; }

            // RMW atómico vía Update — mismo motivo que AplicarPlantilla: nunca
            // mutar in situ la instancia cacheada que leen los lazos de corte/dosis.
            _svc.GetImplemento(); // fuerza bootstrap/activo sin tocar el cache compartido
            string slug = _svc.GetActiveSlug();
            if (string.IsNullOrEmpty(slug)) { await WriteJsonAsync(new { ok = false, error = "no-active-implemento" }); return; }

            ImplementoDto dto = _svc.Update(slug, d =>
            {
                // Identidad + ancho de labor (dato principal del catálogo de máquinas).
                d.Categoria = tpl.Categoria;
                d.Marca = tpl.Marca;
                d.Modelo = tpl.Modelo;
                d.AnchoTotalM = tpl.AnchoLaborM;

                // Las máquinas no sembradoras no tienen surcos/torres ni dosificador
                // de siembra: limpiamos esa estructura para que la página no muestre
                // datos de sembradora que no aplican.
                d.NumeroSurcos = 0;
                d.Surcos = new List<SurcoDto>();
                d.Trenes = new List<TrenDto> { new TrenDto { Id = 1, Nombre = "Tren único", DistanciaM = 0 } };
                d.NumeroTorres = 0;
                d.TipoCultivo = "";
                d.TipoSiembra = "";
                d.TipoDosificador = "";
                d.TipoEstructura = "";
                d.TieneFertilizacion = (tpl.Categoria == "fertilizadora");

                // Secciones: una por sección de corte del modelo (mínimo 1).
                int n = tpl.NumeroSecciones > 0 ? tpl.NumeroSecciones : 1;
                d.Secciones = new List<SeccionDto>();
                for (int i = 1; i <= n; i++)
                    d.Secciones.Add(new SeccionDto { Id = i, Nombre = "Sección " + i });
            });

            bool ok = dto != null;
            await WriteJsonAsync(new { ok = ok, slug = slug, implemento = dto });
        }

        [Route(EmbedIO.HttpVerbs.Post, "/implementos/copiar")]
        public async Task Copiar()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            string body = await ReadBodyAsync();
            string from = "", nombre = "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("from", out var ef)) from = ef.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("nombre", out var en)) nombre = en.GetString() ?? "";
                }
            }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (string.IsNullOrWhiteSpace(from)) { await WriteJsonAsync(new { ok = false, error = "from-required" }); return; }
            if (string.IsNullOrWhiteSpace(nombre)) nombre = from + " (copia)";

            string slug = ImplementoService.MakeSlug(nombre);
            int i = 2; string baseSlug = slug;
            while (_svc.Load(slug) != null) slug = baseSlug + "-" + (i++);

            bool ok = _svc.Copy(from, slug, nombre);
            if (ok) { _svc.SetActive(slug); AutoOpenOverlays(); }
            await WriteJsonAsync(new { ok = ok, slug = slug });
        }
    }
}
