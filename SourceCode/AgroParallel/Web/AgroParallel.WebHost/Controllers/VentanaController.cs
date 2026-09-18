// ============================================================================
// VentanaController.cs — CANAL página → host para la ventana que la contiene.
//
// Las páginas abiertas como ventana-diálogo de PilotX (Guías, Cabecera, Lote,
// Contorno…) no tienen forma de hablarle al shell: el WebView del diálogo
// solo expone NavigationCompleted, y las navegaciones que inicia la PÁGINA
// (el clásico centinela "pilotx-close") NO llegan — verificado con el lote
// (2026-07) y de nuevo con Guías (2026-08-06: el tilde verde dejaba una
// ventana en blanco abierta). Cada caso se venía tapando con una señal
// indirecta del HUD (cambió el lote / apareció una guía / cambió la guía
// activa), y cada uno fallaba en su caso borde.
//
// Esto lo resuelve de raíz y para todas las pantallas: la página POSTea acá
// lo que quiere de su ventana (cerrarse, o cambiar de tamaño) y el shell lo
// lee del estado que YA consulta 10 veces por segundo. Sin dependencias
// nuevas y sin canal nativo del WebView.
//
// El contador `seq` es lo que hace que el host detecte pedidos repetidos
// (cerrar dos veces la misma ventana, o dos resizes al mismo tamaño).
// ============================================================================

using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    /// <summary>Último pedido de la página sobre su propia ventana.</summary>
    public sealed class VentanaPedido
    {
        public long Seq { get; set; }
        public bool Cerrar { get; set; }
        public int Ancho { get; set; }
        public int Alto { get; set; }
    }

    public sealed class VentanaController : AgpControllerBase
    {
        // Estático a propósito: el controller se instancia por request y el
        // state provider necesita leer lo mismo. Un solo diálogo a la vez.
        private static readonly object s_lock = new object();
        private static long s_seq;
        private static bool s_cerrar;
        private static int s_ancho, s_alto;

        public static VentanaPedido Actual()
        {
            lock (s_lock)
                return new VentanaPedido { Seq = s_seq, Cerrar = s_cerrar, Ancho = s_ancho, Alto = s_alto };
        }

        private sealed class Body
        {
            public bool cerrar { get; set; }
            public int ancho { get; set; }
            public int alto { get; set; }
        }

        [Route(HttpVerbs.Post, "/ventana")]
        public async Task PostVentana()
        {
            var b = await ReadJsonBodyAsync<Body>();
            lock (s_lock)
            {
                s_cerrar = b != null && b.cerrar;
                s_ancho = b != null ? b.ancho : 0;
                s_alto = b != null ? b.alto : 0;
                s_seq++;
            }
            await WriteJsonAsync(new { ok = true });
        }
    }
}
