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

        // MERGE, no reemplazo: se aplican solo los campos presentes en el body.
        // El Hub manda únicamente los tres flags, y con el reemplazo entero eso
        // reseteaba a -1 todas las posiciones de los widgets — el operario
        // acomodaba el overlay en la pantalla, tocaba un toggle y lo perdía.
        [Route(HttpVerbs.Post, "/overlays")]
        public async Task Save()
        {
            string body;
            try { body = await ReadBodyAsync(); }
            catch { body = null; }

            var dto = OverlayPrefsService.Instance.Load();
            int aplicados;
            try { aplicados = AgpJsonMerge.Apply(dto, body); }
            catch { aplicados = 0; }

            if (aplicados == 0)
            {
                // Ningún campo reconocido: no se guarda nada. Guardar acá sería
                // pisar la config buena con los defaults del DTO.
                await WriteJsonAsync(new { ok = false, error = "invalid-body" });
                return;
            }

            OverlayPrefsService.Instance.Save(dto);
            await WriteJsonAsync(new { ok = true, aplicados });
        }
    }
}
