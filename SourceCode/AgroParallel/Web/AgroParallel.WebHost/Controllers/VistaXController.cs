// ============================================================================
// VistaXController.cs — REST del módulo VistaX.
//
//   GET  /api/vistax/config              → VistaXConfigDto
//   PUT  /api/vistax/config              body: VistaXConfigDto
//   GET  /api/vistax/implemento          → VistaXImplementoDto
//   PUT  /api/vistax/implemento          body: VistaXImplementoDto
//   GET  /api/vistax/live                → VistaXLiveSnapshotDto
//   POST /api/vistax/reload              fuerza recarga config + implemento
//   POST /api/vistax/sensor/mute         body: { uid, cable, muted } — silencia/reactiva un sensor
//   POST /api/vistax/sensor/config       body: { uid, cable, tipo, bajada, tren, nombre, is_active }
//                                          → edita/crea un único sensor del mapeo
//   POST /api/vistax/calibrar/start      body: VistaXCalibracionStartDto
//   GET  /api/vistax/calibrar/state      → VistaXCalibracionStateDto
//   POST /api/vistax/calibrar/apply      body: VistaXCalibracionApplyDto
//   POST /api/vistax/calibrar/cancel
//   GET  /api/vistax/sensor/tipos        → [{ id, modo, etiqueta }]   tipos válidos
//   POST /api/vistax/overlay             body: { activo: true }       toggle overlay PilotX
// ============================================================================

using System;
using System.Linq;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class VistaXController : AgpControllerBase
    {
        private readonly IVistaXConfigService _cfg;
        private readonly IVistaXLiveService _live;
        private readonly IVistaXCalibracionService _calib;
        // Fuente única de la geometría física (ancho/surcos/distancia/torres/trenes).
        // VistaX YA NO edita ni persiste geometría: el GET la muestra derivada del
        // central y el PUT la ignora. VistaX solo dueña de mapeo_sensores + límites.
        private readonly IImplementoService _impCentral;

        public VistaXController(IVistaXConfigService cfg,
                                IVistaXLiveService live,
                                IVistaXCalibracionService calib = null,
                                IImplementoService impCentral = null)
        {
            _cfg = cfg;
            _live = live;
            _calib = calib;
            _impCentral = impCentral;
        }

        [Route(HttpVerbs.Get, "/vistax/config")]
        public Task GetConfig()
        {
            if (_cfg == null) return WriteJsonAsync(Unavailable());
            return WriteJsonAsync(_cfg.GetConfig());
        }

        [Route(HttpVerbs.Put, "/vistax/config")]
        public async Task PutConfig()
        {
            if (_cfg == null) { await WriteJsonAsync(Unavailable()); return; }
            VistaXConfigDto dto;
            try { dto = await ReadJsonBodyAsync<VistaXConfigDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "invalid-json: " + ex.Message }); return; }
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            // Validar ANTES de persistir — el PUT es reemplazo completo del DTO.
            var val = ConfigValidation.ValidarVistaXConfig(dto);
            if (!val.Ok)
            {
                await WriteErrorAsync(400, "AGP-CFG-001", "Config inválida", string.Join("; ", val.Errores));
                return;
            }
            _cfg.SaveConfig(dto);
            _live?.Reload();
            await WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Get, "/vistax/implemento")]
        public Task GetImplemento()
        {
            if (_cfg == null) return WriteJsonAsync(Unavailable());
            var imp = _cfg.GetImplemento() ?? new VistaXImplementoDto();
            // La geometría física (ancho/surcos/distancia/torres/trenes) YA NO vive
            // en el archivo VistaX: se deriva del implemento central para que la UI
            // muestre siempre lo mismo que QuantiX/SectionX/guiado nativo. VistaX
            // solo es dueña de mapeo_sensores + límites/densidad (Setup.*).
            MergeCentralGeometry(imp);
            return WriteJsonAsync(new
            {
                path = _cfg.GetImplementoPath(),
                implemento = imp
            });
        }

        // Superpone la geometría del implemento central sobre el DTO VistaX que
        // se devuelve a la UI. No persiste: solo ajusta la instancia en memoria
        // para el serializado. Idempotente (siempre escribe los mismos valores).
        private void MergeCentralGeometry(VistaXImplementoDto imp)
        {
            if (imp == null) return;
            ImplementoDto c = null;
            try { c = _impCentral?.GetImplemento(); } catch { }
            if (c == null) return;
            if (imp.Setup == null) imp.Setup = new VistaXSetupDto();

            if (!string.IsNullOrEmpty(c.Nombre)) imp.Nombre = c.Nombre;
            if (c.AnchoTotalM > 0) imp.Setup.AnchoImplemento = c.AnchoTotalM;
            if (c.NumeroSurcos > 0) imp.Setup.TotalSurcos = c.NumeroSurcos;
            if (c.DistanciaEntreSurcosM > 0) imp.Setup.DistanciaEntreSurcos = c.DistanciaEntreSurcosM;
            if (c.NumeroTorres > 0) imp.Setup.Torres = c.NumeroTorres;
            if (c.Secciones != null && c.Secciones.Count > 0) imp.Setup.SeccionesAOG = c.Secciones.Count;

            if (c.Trenes != null && c.Trenes.Count > 0)
            {
                var trenes = new System.Collections.Generic.List<VistaXTrenConfigDto>();
                foreach (var t in c.Trenes)
                {
                    int surcosTren = c.Surcos != null
                        ? c.Surcos.Count(s => s.TrenId == t.Id)
                        : 0;
                    trenes.Add(new VistaXTrenConfigDto
                    {
                        Id = t.Id,
                        Nombre = string.IsNullOrEmpty(t.Nombre) ? ("Tren " + t.Id) : t.Nombre,
                        Surcos = surcosTren
                    });
                }
                imp.Trenes = trenes;
            }
        }

        [Route(HttpVerbs.Put, "/vistax/implemento")]
        public async Task PutImplemento()
        {
            if (_cfg == null) { await WriteJsonAsync(Unavailable()); return; }
            VistaXImplementoDto dto;
            try { dto = await ReadJsonBodyAsync<VistaXImplementoDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "invalid-json: " + ex.Message }); return; }
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }

            // VistaX ya NO edita geometría física: se ignora lo que venga en el body
            // para ancho/surcos/distancia/torres/trenes y se re-deriva del central.
            // Partimos del implemento persistido y solo pisamos lo que VistaX posee
            // (mapeo_sensores + límites/densidad del Setup). Así un PUT accidental
            // con geometría vieja no desincroniza al resto de las apps.
            var actual = _cfg.GetImplemento() ?? new VistaXImplementoDto();
            if (dto.Setup != null)
            {
                var s = actual.Setup ?? (actual.Setup = new VistaXSetupDto());
                // Campos VistaX-owned (límites, densidad, insumo, vista) — sí se guardan.
                s.DensidadObjetivo = dto.Setup.DensidadObjetivo;
                s.ToleranciaDesvio = dto.Setup.ToleranciaDesvio;
                s.FactorK = dto.Setup.FactorK;
                s.ObjetivosTren = dto.Setup.ObjetivosTren ?? s.ObjetivosTren;
                s.MaxDensidadSensor = dto.Setup.MaxDensidadSensor;
                s.InsumoActivoId = dto.Setup.InsumoActivoId;
                s.SurcosPorTorre = dto.Setup.SurcosPorTorre;
                s.VistaModoDefault = dto.Setup.VistaModoDefault;
                // NO se tocan: AnchoImplemento, TotalSurcos, DistanciaEntreSurcos,
                // SeccionesAOG, Torres → los manda el implemento central.
            }
            // mapeo_sensores es 100% VistaX: se reemplaza tal cual viene.
            if (dto.MapeoSensores != null) actual.MapeoSensores = dto.MapeoSensores;
            if (!string.IsNullOrEmpty(dto.Id)) actual.Id = dto.Id;

            // Validar el estado RESULTANTE del merge (no el DTO crudo): la
            // geometría central que este PUT ignora no debe generar rechazos.
            var val = ConfigValidation.ValidarVistaXImplemento(actual);
            if (!val.Ok)
            {
                await WriteErrorAsync(400, "AGP-CFG-001", "Config inválida", string.Join("; ", val.Errores));
                return;
            }

            _cfg.SaveImplemento(actual);
            _live?.Reload();
            await WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Get, "/vistax/live")]
        public Task GetLive()
        {
            if (_live == null) return WriteJsonAsync(Unavailable());
            return WriteJsonAsync(_live.GetSnapshot());
        }

        [Route(HttpVerbs.Post, "/vistax/reload")]
        public Task Reload()
        {
            _cfg?.GetConfig(); // ensure file touched
            _live?.Reload();
            return WriteJsonAsync(new { ok = true });
        }

        // Toggle de silenciado por sensor (uid + cable). Persiste en implemento.json
        // poniendo Muted en la entrada correspondiente de mapeo_sensores. La UI
        // usa esto desde el monitor o desde el widget del piloto.
        [Route(HttpVerbs.Post, "/vistax/sensor/mute")]
        public async Task MuteSensor()
        {
            if (_cfg == null) { await WriteJsonAsync(Unavailable()); return; }
            MuteRequest req;
            try { req = await ReadJsonBodyAsync<MuteRequest>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "invalid-json: " + ex.Message }); return; }
            if (req == null || string.IsNullOrEmpty(req.uid))
            {
                await WriteJsonAsync(new { ok = false, error = "uid-required" });
                return;
            }

            var imp = _cfg.GetImplemento() ?? new VistaXImplementoDto();
            if (imp.MapeoSensores == null)
            {
                await WriteJsonAsync(new { ok = false, error = "no-mapeo" });
                return;
            }
            int hits = 0;
            foreach (var s in imp.MapeoSensores)
            {
                if (string.Equals(s.Uid, req.uid, System.StringComparison.OrdinalIgnoreCase)
                    && s.Cable == req.cable)
                {
                    s.Muted = req.muted;
                    hits++;
                }
            }
            if (hits == 0) { await WriteJsonAsync(new { ok = false, error = "sensor-not-found" }); return; }
            _cfg.SaveImplemento(imp);
            _live?.Reload();
            await WriteJsonAsync(new { ok = true, hits });
        }

        private sealed class MuteRequest
        {
            public string uid { get; set; } = "";
            public int cable { get; set; }
            public bool muted { get; set; }
        }

        // ----------------------------------------------------------------
        // Edición single de un sensor del mapeo. Si (uid, cable) ya existe,
        // se sobreescriben los campos; si no, se inserta. Pensado para que
        // la UI guarde fila a fila sin tener que repostear todo el implemento.
        // ----------------------------------------------------------------
        [Route(HttpVerbs.Post, "/vistax/sensor/config")]
        public async Task SetSensorConfig()
        {
            if (_cfg == null) { await WriteJsonAsync(Unavailable()); return; }
            VistaXSensorConfigDto req;
            try { req = await ReadJsonBodyAsync<VistaXSensorConfigDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "invalid-json: " + ex.Message }); return; }
            if (req == null || string.IsNullOrEmpty(req.Uid))
            {
                await WriteJsonAsync(new { ok = false, error = "uid-required" });
                return;
            }

            // Normalizar tipo contra el catálogo.
            if (!VistaXSensorTypes.All.Contains(req.Tipo))
                req.Tipo = VistaXSensorTypes.Semilla;

            var imp = _cfg.GetImplemento() ?? new VistaXImplementoDto();
            if (imp.MapeoSensores == null)
                imp.MapeoSensores = new System.Collections.Generic.List<VistaXSensorConfigDto>();

            var existente = imp.MapeoSensores.Find(s =>
                string.Equals(s.Uid, req.Uid, System.StringComparison.OrdinalIgnoreCase)
                && s.Cable == req.Cable);

            if (existente == null)
            {
                imp.MapeoSensores.Add(req);
            }
            else
            {
                existente.Pin = req.Pin;
                existente.Bajada = req.Bajada;
                existente.SurcoDesde = req.SurcoDesde;
                existente.SurcoHasta = req.SurcoHasta;
                existente.Tipo = req.Tipo;
                existente.Nombre = req.Nombre;
                existente.Tren = req.Tren;
                existente.IsActive = req.IsActive;
                existente.SeccionAOG = req.SeccionAOG;
                existente.Objetivo = req.Objetivo;
                // Muted no se toca acá: tiene su propio endpoint /sensor/mute.
            }

            // Validar el implemento resultante (el sensor ya quedó mergeado).
            var val = ConfigValidation.ValidarVistaXImplemento(imp);
            if (!val.Ok)
            {
                await WriteErrorAsync(400, "AGP-CFG-001", "Config inválida", string.Join("; ", val.Errores));
                return;
            }

            _cfg.SaveImplemento(imp);
            _live?.Reload();
            await WriteJsonAsync(new { ok = true });
        }

        // ----------------------------------------------------------------
        // Catálogo de tipos válidos. Lo lee la UI para poblar el dropdown
        // sin hardcodearlo en JS — así si agregamos un tipo nuevo basta con
        // tocar VistaXSensorTypes.cs.
        // ----------------------------------------------------------------
        [Route(HttpVerbs.Get, "/vistax/sensor/tipos")]
        public Task GetSensorTipos()
        {
            return WriteJsonAsync(VistaXSensorTypes.All.Select(t => new
            {
                id = t,
                modo = VistaXSensorTypes.ModoFirmware(t),
                etiqueta = EtiquetaTipo(t)
            }).ToList());
        }

        private static string EtiquetaTipo(string t)
        {
            switch (t)
            {
                case VistaXSensorTypes.Semilla: return "Semilla";
                case VistaXSensorTypes.Fertilizante: return "Fertilizante";
                case VistaXSensorTypes.RotacionEje: return "Rotación de eje";
                case VistaXSensorTypes.Turbina: return "Turbina";
                case VistaXSensorTypes.BajadaHerramienta: return "Bajada de herramienta";
                case VistaXSensorTypes.TolvaVacia: return "Tolva vacía";
                case VistaXSensorTypes.TolvaLlena: return "Tolva llena";
                case VistaXSensorTypes.Presion: return "Presión";
                case VistaXSensorTypes.FinalCarrera: return "Final de carrera";
                default: return t;
            }
        }

        // ----------------------------------------------------------------
        // Calibración "Detectar densidad N segundos".
        // ----------------------------------------------------------------
        [Route(HttpVerbs.Post, "/vistax/calibrar/start")]
        public async Task CalibrarStart()
        {
            if (_calib == null) { await WriteJsonAsync(Unavailable()); return; }
            VistaXCalibracionStartDto req;
            try { req = await ReadJsonBodyAsync<VistaXCalibracionStartDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "invalid-json: " + ex.Message }); return; }
            bool ok = _calib.Start(req ?? new VistaXCalibracionStartDto());
            if (!ok) { await WriteJsonAsync(new { ok = false, error = "no-insumo-activo" }); return; }
            await WriteJsonAsync(new { ok = true, state = _calib.GetState() });
        }

        [Route(HttpVerbs.Get, "/vistax/calibrar/state")]
        public Task CalibrarState()
        {
            if (_calib == null) return WriteJsonAsync(Unavailable());
            return WriteJsonAsync(_calib.GetState());
        }

        [Route(HttpVerbs.Post, "/vistax/calibrar/apply")]
        public async Task CalibrarApply()
        {
            if (_calib == null) { await WriteJsonAsync(Unavailable()); return; }
            VistaXCalibracionApplyDto req;
            try { req = await ReadJsonBodyAsync<VistaXCalibracionApplyDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "invalid-json: " + ex.Message }); return; }
            bool ok = _calib.Apply(req ?? new VistaXCalibracionApplyDto());
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Post, "/vistax/calibrar/cancel")]
        public Task CalibrarCancel()
        {
            _calib?.Cancel();
            return WriteJsonAsync(new { ok = true });
        }

        // ----------------------------------------------------------------
        // Overlay live VistaX sobre PilotX (botón toggle desde pantalla
        // principal). La fuente de verdad real es VistaXConfig.Enabled
        // (vistaX.json) — lo lee FormGPS.InitVistaX() para decidir si crear
        // el panel nativo. Acá lo único que hacemos es escribir ese campo
        // remoto desde una página HTML (ej. botón en piloto.html).
        // ----------------------------------------------------------------
        [Route(HttpVerbs.Get, "/vistax/overlay")]
        public Task GetOverlay()
        {
            if (_cfg == null) return WriteJsonAsync(Unavailable());
            var cfg = _cfg.GetConfig();
            return WriteJsonAsync(new { activo = cfg != null && cfg.Enabled });
        }

        [Route(HttpVerbs.Post, "/vistax/overlay")]
        public async Task SetOverlay()
        {
            if (_cfg == null) { await WriteJsonAsync(Unavailable()); return; }
            OverlayRequest req;
            try { req = await ReadJsonBodyAsync<OverlayRequest>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "invalid-json: " + ex.Message }); return; }
            if (req == null) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }

            // Persistir en vistaX.json (fuente de verdad para el panel nativo
            // que crea FormGPS.InitVistaX). El cambio se ve la próxima vez que
            // se reinicia PilotX, o cuando el operario toca el botón VX.
            var cfg = _cfg.GetConfig() ?? new VistaXConfigDto();
            cfg.Enabled = req.activo;
            _cfg.SaveConfig(cfg);
            await WriteJsonAsync(new { ok = true, activo = cfg.Enabled });
        }

        private sealed class OverlayRequest { public bool activo { get; set; } }

        private object Unavailable() => new { ok = false, error = "service-unavailable" };
    }
}
