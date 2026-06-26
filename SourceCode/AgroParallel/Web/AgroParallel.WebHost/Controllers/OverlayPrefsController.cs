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
// Notas:
//  · El ResponseSerializer default de EmbedIO (Swan) ignora [JsonPropertyName]
//    y emite PascalCase, asi que serializamos a mano con System.Text.Json para
//    que el front lea snake_case como espera.
// ============================================================================

using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgroParallel.Services;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class OverlayPrefsController : WebApiController
    {
        private static readonly JsonSerializerOptions JsonInOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Sin policy: cada propiedad usa su [JsonPropertyName] explicito.
        private static readonly JsonSerializerOptions JsonOutOpts = new JsonSerializerOptions();

        private async Task SendJsonAsync(object obj)
        {
            string json = JsonSerializer.Serialize(obj, JsonOutOpts);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/overlays")]
        public async Task Get()
        {
            var dto = OverlayPrefsService.Instance.Load();
            await SendJsonAsync(dto);
        }

        [Route(HttpVerbs.Post, "/overlays")]
        public async Task Save()
        {
            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync();
            OverlayPrefsDto dto = null;
            try { dto = JsonSerializer.Deserialize<OverlayPrefsDto>(body, JsonInOpts); }
            catch { dto = null; }
            if (dto == null) { await SendJsonAsync(new { ok = false, error = "invalid-body" }); return; }
            OverlayPrefsService.Instance.Save(dto);
            await SendJsonAsync(new { ok = true });
        }
    }
}
