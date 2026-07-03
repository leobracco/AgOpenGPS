// ============================================================================
// OverlayPrefsController.cs
// Endpoints REST para las preferencias de overlays de PilotX (los widgets que
// el operario quiere ver sobre el mapa: QuantiX shapefileLegend, VistaX, FlowX).
// El Hub (página hub.html / pestañita "Widgets") los lee y los pisa.
// FormGPS los relee del archivo cada 250 ms y aplica sin reiniciar.
//
//   GET  /api/overlays   → OverlayPrefsDto  (snake_case)
//   POST /api/overlays   (body = OverlayPrefsDto)  → { ok }
//
// Nota: La serialización A DISCO ocurre en OverlayPrefsService.Save() con
// WriteIndented=true y sin policy (respeta [JsonPropertyName] del DTO).
// El wire HTTP usa AgpJson (snake_case vía policy + [JsonPropertyName]).
// Ambas rutas producen el mismo resultado porque OverlayPrefsDto tiene
// [JsonPropertyName] snake_case explícitos en todos sus campos.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class OverlayPrefsController : AgpControllerBase
    {
        [Route(HttpVerbs.Get, "/overlays")]
        public Task Get()
        {
            var dto = OverlayPrefsService.Instance.Load();
            return WriteJsonAsync(dto);
        }

        [Route(HttpVerbs.Post, "/overlays")]
        public async Task Save()
        {
            OverlayPrefsDto dto = null;
            try { dto = await ReadJsonBodyAsync<OverlayPrefsDto>(); }
            catch { dto = null; }
            if (dto == null) { await WriteJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            OverlayPrefsService.Instance.Save(dto);
            await WriteJsonAsync(new { ok = true });
        }
    }
}
