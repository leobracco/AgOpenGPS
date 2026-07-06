using System.Threading.Tasks;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    /// <summary>POST /api/corex/* — comandos del dashboard (se completa en Task 4).</summary>
    public sealed class CoreXCommandController : AgpControllerBase
    {
        private readonly FormLoop _form;

        public CoreXCommandController(FormLoop form)
        {
            _form = form;
        }

        // EmbedIO exige al menos una ruta por controller (si no, tira
        // ArgumentException al registrar). Ping placeholder hasta que la
        // Task 4 agregue los comandos reales.
        [Route(HttpVerbs.Get, "/corex/ping")]
        public Task Ping() => WriteJsonAsync(new { ok = true });
    }
}
