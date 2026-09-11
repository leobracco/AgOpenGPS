// ============================================================================
// IdiomaController.cs — idioma de la interfaz, compartido pantalla + Hub.
//
//   GET  /api/idioma  -> { "idioma": "es", "seq": 3 }
//   POST /api/idioma     body { "idioma": "pt" }  -> idem con el aplicado
//
// El `seq` es lo que hace que la pantalla nativa y las páginas abiertas se
// enteren de un cambio hecho en OTRA ventana: ya consultan el estado varias
// veces por segundo, ven que el contador subió y vuelven a traducirse. Sin
// eso, cambiar el idioma desde el menú SISTEMA dejaba en castellano cualquier
// pantalla que ya estuviera abierta.
//
// El diccionario NO pasa por acá: es wwwroot/idiomas.json, servido como
// archivo estático y cacheado por quien lo consume.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class IdiomaController : AgpControllerBase
    {
        // Estático: el controller se instancia por request y el state provider
        // necesita leer el mismo contador.
        private static long s_seq;

        /// <summary>Idioma actual + contador de cambios, para el HUD.</summary>
        public static (string Idioma, long Seq) Actual() =>
            (IdiomaService.Instance.Actual(), s_seq);

        private sealed class Body
        {
            public string idioma { get; set; }
        }

        [Route(HttpVerbs.Get, "/idioma")]
        public async Task GetIdioma()
        {
            await WriteJsonAsync(new { idioma = IdiomaService.Instance.Actual(), seq = s_seq });
        }

        [Route(HttpVerbs.Post, "/idioma")]
        public async Task PostIdioma()
        {
            var b = await ReadJsonBodyAsync<Body>();
            string aplicado = IdiomaService.Instance.Guardar(b?.idioma);
            s_seq++;
            AgpLog.Info("Idioma", "Interfaz en: " + aplicado);
            await WriteJsonAsync(new { idioma = aplicado, seq = s_seq });
        }
    }
}
