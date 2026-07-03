// ============================================================================
// SetupController.cs
// Endpoints REST del asistente de primera vez del Hub PilotX:
//   GET  /api/setup/estado    → SetupStateDto + chequeos en vivo (broker, nodos)
//   POST /api/setup/paso      { paso, valor }
//   POST /api/setup/completar { valor:bool }
//   POST /api/setup/dismiss   { valor:bool }
// ============================================================================

using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using System.Threading.Tasks;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SetupController : AgpControllerBase
    {
        private readonly ISetupStateService _setup;
        private readonly INodoRegistryService _registry;
        private readonly INodosCuratedService _curated;
        private readonly IOrbitXConfigService _orbitxCfg;

        public SetupController(ISetupStateService setup,
                               INodoRegistryService registry,
                               INodosCuratedService curated,
                               IOrbitXConfigService orbitxCfg)
        {
            _setup = setup;
            _registry = registry;
            _curated = curated;
            _orbitxCfg = orbitxCfg;
        }

        [Route(HttpVerbs.Get, "/setup/estado")]
        public Task GetEstado()
        {
            var dto = _setup != null ? _setup.Load() : new SetupStateDto();

            // Chequeos live (no persistidos): el wizard los muestra en tiempo real.
            bool brokerConnected = false;
            int nodosDescubiertos = 0;
            int nodosAceptados = 0;
            int nodosPendientes = 0;
            try
            {
                if (_registry != null)
                {
                    var diag = _registry.GetDiagnostic();
                    brokerConnected = diag != null && diag.Connected;
                    var all = _registry.GetAll();
                    if (all != null) nodosDescubiertos = all.Count;
                }
            }
            catch { }
            try
            {
                if (_curated != null)
                {
                    var unified = _curated.GetUnified(_registry);
                    if (unified != null)
                    {
                        foreach (var n in unified)
                        {
                            if (n.Estado == "aceptado" || n.Estado == "offline") nodosAceptados++;
                            else if (n.Estado == "pendiente") nodosPendientes++;
                        }
                    }
                }
            }
            catch { }

            bool orbitxVinculado = false;
            try
            {
                if (_orbitxCfg != null)
                {
                    var cfg = _orbitxCfg.Load();
                    // Heurística: si hay device-id persistido, está vinculado.
                    orbitxVinculado = cfg != null && !string.IsNullOrEmpty(cfg.DeviceId);
                }
            }
            catch { }

            return WriteJsonAsync(new
            {
                ok = true,
                estado = dto,
                live = new
                {
                    broker_connected = brokerConnected,
                    nodos_descubiertos = nodosDescubiertos,
                    nodos_aceptados = nodosAceptados,
                    nodos_pendientes = nodosPendientes,
                    orbitx_vinculado = orbitxVinculado
                }
            });
        }

        public sealed class PasoBody { public string paso { get; set; } public bool valor { get; set; } }
        public sealed class FlagBody { public bool valor { get; set; } }

        [Route(HttpVerbs.Post, "/setup/paso")]
        public async Task MarcarPaso()
        {
            if (_setup == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var body = await ReadJsonBodyAsync<PasoBody>();
            if (body == null || string.IsNullOrEmpty(body.paso)) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            _setup.MarkPaso(body.paso, body.valor);
            await WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/setup/completar")]
        public async Task Completar()
        {
            if (_setup == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var body = await ReadJsonBodyAsync<FlagBody>();
            bool v = body != null ? body.valor : true;
            _setup.MarkCompleted(v);
            await WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/setup/dismiss")]
        public async Task Dismiss()
        {
            if (_setup == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            var body = await ReadJsonBodyAsync<FlagBody>();
            bool v = body != null ? body.valor : true;
            _setup.MarkDismissed(v);
            await WriteJsonAsync(new { ok = true });
        }
    }
}
