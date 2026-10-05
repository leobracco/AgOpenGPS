// ============================================================================
// ControlController.cs — protocolo de la autoridad de control.
//
//   GET  /api/control/estado     quién tiene el control (+ "soy_dueno" del que pregunta)
//   POST /api/control/latido     cabina: presencia; dueño remoto: mantiene el control
//                                (cada ~1,5 s; sin latido por 3,5 s se revoca)
//   POST /api/control/pedir      {nombre?} remoto pide; la cabina = recuperar
//                                200 concedido | 202 pendiente | 409 rechazado
//   POST /api/control/soltar     el remoto suelta (o retira su pedido)
//   POST /api/control/ceder      SOLO cabina: concede al pedido pendiente
//   POST /api/control/rechazar   SOLO cabina: rechaza el pedido pendiente
//   POST /api/control/recuperar  SOLO cabina: retoma el control
//
// Todas son LECTURA para la puerta: el protocolo no se puede bloquear a sí mismo.
// ============================================================================

using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AgroParallel.Services.Control;
using AgroParallel.WebHost.Autoridad;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class ControlController : AgpControllerBase
    {
        private readonly PuertaControl _puerta;

        public ControlController(PuertaControl puerta)
        {
            _puerta = puerta;
        }

        [Route(HttpVerbs.Get, "/control/estado")]
        public Task GetEstado() => WriteJsonAsync(Armar(_puerta.Autoridad.Estado(), ClienteHttp.De(HttpContext)));

        [Route(HttpVerbs.Post, "/control/latido")]
        public Task PostLatido()
        {
            var c = ClienteHttp.De(HttpContext);
            return WriteJsonAsync(Armar(_puerta.Autoridad.Latido(c), c));
        }

        [Route(HttpVerbs.Post, "/control/pedir")]
        public async Task PostPedir()
        {
            var c = ClienteHttp.De(HttpContext);
            // El nombre puede venir en el body si la página no manda el header.
            if (!c.EsCabina)
            {
                PedirBody body = null;
                try { body = await ReadJsonBodyAsync<PedirBody>().ConfigureAwait(false); } catch { }
                string nombre = Recortar(body?.Nombre);
                if (!string.IsNullOrEmpty(nombre)) c = ClienteControl.Remoto(c.Id, nombre, c.Ip);
            }
            var r = _puerta.Pedir(c);
            HttpContext.Response.StatusCode =
                r.Resultado == ResultadoPedido.Concedido ? 200 :
                r.Resultado == ResultadoPedido.Pendiente ? 202 : 409;
            await WriteJsonAsync(new
            {
                ok = r.Resultado == ResultadoPedido.Concedido,
                resultado = r.Resultado == ResultadoPedido.Concedido ? "concedido"
                          : r.Resultado == ResultadoPedido.Pendiente ? "pendiente" : "rechazado",
                mensaje = r.Mensaje,
                estado = Armar(_puerta.Autoridad.Estado(), c),
            }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/control/soltar")]
        public Task PostSoltar()
        {
            var c = ClienteHttp.De(HttpContext);
            bool ok = _puerta.Autoridad.Soltar(c);
            return WriteJsonAsync(new { ok, estado = Armar(_puerta.Autoridad.Estado(), c) });
        }

        [Route(HttpVerbs.Post, "/control/ceder")]
        public Task PostCeder() => SoloCabina(c =>
        {
            bool ok = _puerta.Ceder();
            return new
            {
                ok,
                mensaje = ok ? "Control cedido." : "No hay ningún pedido de control vigente.",
                estado = Armar(_puerta.Autoridad.Estado(), c)
            };
        });

        [Route(HttpVerbs.Post, "/control/rechazar")]
        public Task PostRechazar() => SoloCabina(c =>
        {
            bool ok = _puerta.Autoridad.RechazarPedido();
            return new
            {
                ok,
                mensaje = ok ? "Pedido rechazado." : "No había pedido.",
                estado = Armar(_puerta.Autoridad.Estado(), c)
            };
        });

        [Route(HttpVerbs.Post, "/control/recuperar")]
        public Task PostRecuperar() => SoloCabina(c =>
        {
            _puerta.Autoridad.Recuperar("la cabina retomó el control");
            return new
            {
                ok = true,
                mensaje = "La cabina tiene el control.",
                estado = Armar(_puerta.Autoridad.Estado(), c)
            };
        });

        private Task SoloCabina(System.Func<ClienteControl, object> accion)
        {
            var c = ClienteHttp.De(HttpContext);
            if (!c.EsCabina)
                return WriteErrorAsync(403, "solo-cabina",
                    "Esto sólo se puede hacer desde la pantalla de la cabina.");
            return WriteJsonAsync(accion(c));
        }

        private object Armar(EstadoControl e, ClienteControl quien)
        {
            var ult = _puerta.UltimaAccionRemota;
            long ahora = _puerta.AhoraMs;
            bool soyDueno = quien != null && _puerta.Autoridad.TieneControl(quien);
            return new
            {
                ok = true,
                modo = _puerta.Modo == ModoAutoridad.Exigir ? "exigir" : "solo_registro",
                dueno_es_cabina = e.DuenoEsCabina,
                dueno_nombre = e.DuenoNombre,
                dueno_ip = e.DuenoIp,
                vence_en_ms = e.VenceEnMs,
                hay_pedido = e.HayPedido,
                pedido_nombre = e.PedidoNombre,
                pedido_ip = e.PedidoIp,
                cabina_presente = e.CabinaPresente,
                soy_cabina = quien != null && quien.EsCabina,
                soy_dueno = soyDueno,
                latido_ms = AutoridadControl.LatidoEsperadoMs,
                ultima_accion_remota = ult == null ? null : new
                {
                    nombre = ult.Nombre,
                    ip = ult.Ip,
                    accion = ult.Accion,
                    tenia_control = ult.TeniaControl,
                    hace_ms = ahora - ult.CuandoMs,
                },
            };
        }

        private static string Recortar(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s)
            {
                if (sb.Length >= 40) break;
                if (char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '_' || ch == '.') sb.Append(ch);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private sealed class PedirBody
        {
            [JsonPropertyName("nombre")] public string Nombre { get; set; }
        }
    }
}
