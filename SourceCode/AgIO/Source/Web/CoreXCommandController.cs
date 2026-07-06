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
    }
}
