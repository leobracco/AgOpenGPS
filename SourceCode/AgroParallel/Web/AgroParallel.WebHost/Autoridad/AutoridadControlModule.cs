// ============================================================================
// AutoridadControlModule.cs — filtro de la autoridad de control sobre /api.
//
// Módulo passthrough (IsFinalHandler=false) registrado ANTES del WebApi:
//   · todo request loopback marca "la cabina está viva";
//   · cada escritura se clasifica por ruta (ClasificadorComandos). Si es
//     ACCIONAMIENTO y el que llama no tiene el control, en modo Exigir se
//     corta acá con 423 + motivo en castellano; en SoloRegistro se anota y
//     sigue.
//   · POST /api/aog/guidance/command depende del comando del cuerpo: lo
//     decide GuidanceController (acá no se puede leer el body sin consumirlo).
// ============================================================================

using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Control;
using EmbedIO;

namespace AgroParallel.WebHost.Autoridad
{
    internal sealed class AutoridadControlModule : WebModuleBase
    {
        private readonly PuertaControl _puerta;

        public AutoridadControlModule(PuertaControl puerta) : base("/api")
        {
            _puerta = puerta;
        }

        public override bool IsFinalHandler => false;

        protected override async Task OnRequestAsync(IHttpContext context)
        {
            if (_puerta == null) return;
            var cliente = ClienteHttp.De(context);
            if (cliente.EsCabina) _puerta.Autoridad.MarcarPresenciaCabina();

            string metodo = context.Request.HttpMethod;
            string ruta = context.RequestedPath;
            var nivel = ClasificadorComandos.ClasificarEndpoint(metodo, ruta);
            if (nivel == NivelComando.Lectura || nivel == NivelComando.PorComando) return;

            bool direccion = ClasificadorComandos.EsEndpointDeDireccion(metodo, ruta);
            var d = _puerta.Autorizar(cliente, nivel, metodo + " " + ruta, direccion);
            if (d.Permitido) return;

            context.Response.StatusCode = d.StatusHttp;
            string json = AgpJson.Serialize(new { ok = false, error = "sin-control", mensaje = d.Motivo });
            await context.SendStringAsync(json, "application/json", Encoding.UTF8).ConfigureAwait(false);
            context.SetHandled();
        }
    }
}
