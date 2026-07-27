// ============================================================================
// VehicleToolController.cs
// REST endpoints para config de Vehículo + Herramienta de PilotX desde HTML.
//   GET  /api/vehicle           → VehicleConfigDto
//   PUT  /api/vehicle           ← VehicleConfigDto
//   GET  /api/tool              → ToolConfigDto
//   PUT  /api/tool              ← ToolConfigDto
//   GET  /api/vehicle-tool      → { vehicle, tool }  (bundle conveniente)
//   GET  /api/imu               → ImuConfigDto
//   PUT  /api/imu               ← ImuConfigDto
//   GET  /api/imu/live          → ImuLiveDto (roll actual, cero, presencia IMU)
//   POST /api/imu/roll-zero     → poner roll actual como cero
//   POST /api/imu/roll-adjust   ← { delta } ajuste fino del cero (±°)
//   POST /api/imu/roll-remove   → rollZero = 0
//   POST /api/imu/reset         → reset IMU (centinelas)
// ============================================================================

using System;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class VehicleToolController : AgpControllerBase
    {
        private readonly IVehicleToolService _svc;

        public VehicleToolController(IVehicleToolService svc) { _svc = svc; }

        [Route(HttpVerbs.Get, "/vehicle")]
        public Task GetVehicle()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, vehicle = _svc.GetVehicle() });
        }

        [Route(HttpVerbs.Get, "/tool")]
        public Task GetTool()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, tool = _svc.GetTool() });
        }

        [Route(HttpVerbs.Get, "/vehicle-tool")]
        public Task GetBundle()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var b = _svc.GetBundle();
            return WriteJsonAsync(new { ok = true, vehicle = b.Vehicle, tool = b.Tool });
        }

        [Route(HttpVerbs.Put, "/vehicle")]
        public async Task PutVehicle()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            VehicleConfigDto cfg;
            try { cfg = await ReadJsonBodyAsync<VehicleConfigDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (cfg == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.SaveVehicle(cfg);
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Put, "/tool")]
        public async Task PutTool()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            ToolConfigDto cfg;
            try { cfg = await ReadJsonBodyAsync<ToolConfigDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (cfg == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.SaveTool(cfg);
            await WriteJsonAsync(new { ok });
        }

        // ---- Mis vehículos (sprites custom Agro Parallel del mapa) ----
        //   GET /api/vehicle/sprites → { ok, activo, sprites: [{archivo, nombre, url}] }
        //   PUT /api/vehicle/sprite  { archivo }   ("" = volver a marca embebida)
        // Los PNG viven en AgroParallel\wwwroot\img\vehiculos\ (los sirve el
        // static server en /img/vehiculos/) y FormGPS los usa como textura.

        [Route(HttpVerbs.Get, "/vehicle/sprites")]
        public Task GetVehicleSprites()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            var sprites = new System.Collections.Generic.List<object>();
            try
            {
                // Del wwwroot REAL que resolvió el host, no de BaseDirectory:
                // con el motor corriendo desde <install>\Engine\ esa ruta no
                // existe y el catálogo salía vacío sin ningún error visible.
                string raiz = AgroParallel.Common.AgpPaths.WwwRoot;
                if (string.IsNullOrEmpty(raiz))
                {
                    raiz = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "AgroParallel", "wwwroot");
                }
                string dir = System.IO.Path.Combine(raiz, "img", "vehiculos");
                if (System.IO.Directory.Exists(dir))
                {
                    var ti = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
                    foreach (var f in System.IO.Directory.GetFiles(dir, "*.png"))
                    {
                        string archivo = System.IO.Path.GetFileName(f);
                        // Variantes auxiliares (<nombre>.frente/.cola = piezas del
                        // articulado, <nombre>.mapa = versión sin ruedas para el
                        // mapa) no son vehículos del catálogo.
                        string sinExt = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (sinExt.EndsWith(".frente", StringComparison.OrdinalIgnoreCase)
                            || sinExt.EndsWith(".cola", StringComparison.OrdinalIgnoreCase)
                            || sinExt.EndsWith(".mapa", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Convención de nombre: tipo_marca_modelo.png
                        // (underscore separa niveles; guión = espacio dentro de
                        // un nivel; modelo opcional). Ej: rigido_pauny.png,
                        // articulado_pauny_580.png
                        string[] partes = System.IO.Path.GetFileNameWithoutExtension(f).Split('_');
                        string Limpio(int i) => i < partes.Length
                            ? ti.ToTitleCase(partes[i].Replace('-', ' ')) : "";
                        string tipo = Limpio(0);
                        string marca = Limpio(1);
                        string modelo = partes.Length > 2
                            ? ti.ToTitleCase(string.Join(" ", partes, 2, partes.Length - 2).Replace('-', ' '))
                            : "";
                        string nombre = (marca + " " + modelo).Trim();
                        if (nombre == "") nombre = tipo;
                        sprites.Add(new { archivo, nombre, tipo, marca, modelo, url = "/img/vehiculos/" + archivo });
                    }
                }
            }
            catch { /* sin carpeta: lista vacía */ }
            return WriteJsonAsync(new { ok = true, activo = _svc.GetVehiculoCustom(), sprites });
        }

        [Route(HttpVerbs.Put, "/vehicle/sprite")]
        public async Task PutVehicleSprite()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            SpriteBody body;
            try { body = await ReadJsonBodyAsync<SpriteBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            if (body == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.SetVehiculoCustom(body.Archivo ?? "");
            await WriteJsonAsync(new { ok });
        }

        private sealed class SpriteBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("archivo")]
            public string Archivo { get; set; }
        }

        // --- IMU ----------------------------------------------------------------

        private sealed class RollAdjustBody
        {
            public double delta { get; set; }
        }

        [Route(HttpVerbs.Get, "/imu")]
        public Task GetImu()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, imu = _svc.GetImu() });
        }

        [Route(HttpVerbs.Put, "/imu")]
        public async Task PutImu()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            ImuConfigDto cfg;
            try { cfg = await ReadJsonBodyAsync<ImuConfigDto>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (cfg == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.SaveImu(cfg);
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Get, "/imu/live")]
        public Task GetImuLive()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = true, live = _svc.GetImuLive() });
        }

        [Route(HttpVerbs.Post, "/imu/roll-zero")]
        public Task PostRollZero()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            bool ok = _svc.ZeroRoll();
            return WriteJsonAsync(new { ok, error = ok ? null : "no-imu-roll" });
        }

        [Route(HttpVerbs.Post, "/imu/roll-adjust")]
        public async Task PostRollAdjust()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            RollAdjustBody body;
            try { body = await ReadJsonBodyAsync<RollAdjustBody>(); }
            catch (Exception ex) { await WriteJsonAsync(new { ok = false, error = "bad-json: " + ex.Message }); return; }
            if (body == null) { await WriteJsonAsync(new { ok = false, error = "empty-body" }); return; }
            bool ok = _svc.AdjustRollZero(body.delta);
            await WriteJsonAsync(new { ok, error = ok ? null : "no-imu-roll" });
        }

        [Route(HttpVerbs.Post, "/imu/roll-remove")]
        public Task PostRollRemove()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = _svc.RemoveRollZero() });
        }

        [Route(HttpVerbs.Post, "/imu/reset")]
        public Task PostImuReset()
        {
            if (_svc == null) return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(new { ok = _svc.ResetImu() });
        }
    }
}
