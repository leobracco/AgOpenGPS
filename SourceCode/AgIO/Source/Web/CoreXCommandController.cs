using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    /// <summary>
    /// POST /api/corex/{mqtt,ntrip}/toggle — comandos del dashboard.
    /// Los handlers WinForms tocan controles, así que marshalleamos al
    /// hilo UI con BeginInvoke (mismo patrón que los callbacks async viejos).
    /// </summary>
    public sealed class CoreXCommandController : AgpControllerBase
    {
        private readonly FormLoop _form;

        public CoreXCommandController(FormLoop form)
        {
            _form = form;
        }

        [Route(HttpVerbs.Post, "/corex/mqtt/toggle")]
        public Task ToggleMqtt()
        {
            _form.BeginInvoke((MethodInvoker)(() => _form.ToggleMqttBrokerFromWeb()));
            return WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/corex/ntrip/toggle")]
        public Task ToggleNtrip()
        {
            _form.BeginInvoke((MethodInvoker)(() => _form.ToggleNtripFromWeb()));
            return WriteJsonAsync(new { ok = true });
        }

        // ── Ciclo de vida (modo demonio: CoreX no tiene ventana propia) ──────
        // El timer interno de 800 ms deja salir la respuesta HTTP antes de
        // reiniciar/cerrar el proceso.

        [Route(HttpVerbs.Post, "/corex/reiniciar")]
        public Task Reiniciar()
        {
            _form.BeginInvoke((MethodInvoker)(() =>
            {
                AgLibrary.Logging.Log.EventWriter("Program Reset: reinicio pedido desde la web");
                _form.RestartFromWeb();
            }));
            return WriteJsonAsync(new { ok = true, restart = true });
        }

        [Route(HttpVerbs.Post, "/corex/apagar")]
        public Task Apagar()
        {
            _form.BeginInvoke((MethodInvoker)(() => _form.ShutdownFromWeb()));
            return WriteJsonAsync(new { ok = true });
        }
    }
}
