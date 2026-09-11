// ============================================================================
// TecladoController.cs — puente entre los campos de las páginas y el teclado
// que vive en su PROPIA ventana (PilotX.Desktop/TecladoWindow).
//
//   POST /api/teclado/abrir   → la página avisa "enfocaron un campo"
//   POST /api/teclado/cerrar  → "se fueron del campo"
//   GET  /api/teclado/estado  → lo consulta el shell para abrir/cerrar la ventana
//   POST /api/teclado/presente → el shell avisa que HAY teclado nativo escuchando
//
// Por qué este rodeo: las páginas del Hub corren en un WebView embebido, sin
// canal directo con el shell. El engine ya es el punto de encuentro de todo lo
// demás, así que el estado del teclado viaja por acá.
//
// Si nadie contesta que hay teclado nativo (Hub abierto desde el celular o
// desde un navegador común), la página se da cuenta por la respuesta y dibuja
// su teclado HTML de siempre.
// ============================================================================

using System;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class TecladoController : AgpControllerBase
    {
        // Estado compartido: lo escribe la página, lo lee el shell.
        private static readonly object _lock = new object();
        private static bool _abierto;
        private static bool _numerico;
        private static string _titulo = "";
        private static long _seq;
        // Última vez que el shell dijo "acá estoy". Si hace rato que no aparece
        // (se cerró PilotX, o el Hub se abrió desde un navegador), la página
        // vuelve sola a su teclado HTML en vez de quedarse sin ninguno.
        private static DateTime _shellVisto = DateTime.MinValue;
        private static readonly TimeSpan VENTANA_SHELL = TimeSpan.FromSeconds(5);

        private static bool HayShell => (DateTime.UtcNow - _shellVisto) < VENTANA_SHELL;

        public sealed class AbrirReq
        {
            public bool numerico { get; set; }
            public string titulo { get; set; }
        }

        [Route(HttpVerbs.Post, "/teclado/abrir")]
        public async Task Abrir()
        {
            var req = await ReadJsonBodyAsync<AbrirReq>();
            lock (_lock)
            {
                _abierto = true;
                _numerico = req != null && req.numerico;
                _titulo = req?.titulo ?? "";
                _seq++;
            }
            // La página necesita saber si alguien va a mostrar el teclado: si no
            // hay shell, dibuja el suyo.
            await WriteJsonAsync(new { ok = true, nativo = HayShell });
        }

        [Route(HttpVerbs.Post, "/teclado/cerrar")]
        public async Task Cerrar()
        {
            lock (_lock) { _abierto = false; _seq++; }
            await WriteJsonAsync(new { ok = true, nativo = HayShell });
        }

        [Route(HttpVerbs.Get, "/teclado/estado")]
        public Task Estado()
        {
            lock (_lock)
            {
                return WriteJsonAsync(new
                {
                    ok = true,
                    abierto = _abierto,
                    numerico = _numerico,
                    titulo = _titulo,
                    seq = _seq,
                    nativo = HayShell
                });
            }
        }

        /// <summary>El shell late acá para avisar que tiene el teclado nativo.</summary>
        [Route(HttpVerbs.Post, "/teclado/presente")]
        public Task Presente()
        {
            _shellVisto = DateTime.UtcNow;
            lock (_lock)
            {
                return WriteJsonAsync(new { ok = true, abierto = _abierto, numerico = _numerico, titulo = _titulo, seq = _seq });
            }
        }

        // --- Teclas pulsadas -------------------------------------------------
        // El teclado vive en otra ventana, así que mandar las teclas por
        // SendInput obliga a que el campo conserve el foco de Windows — y eso
        // no se sostiene: el WebView pierde el foco interno al tocar la otra
        // ventana y el operario ve cómo se le va el cursor.
        //
        // Por eso la tecla viaja como DATO: el teclado la publica acá y la
        // página la aplica sobre el campo que tenía, sin depender de quién
        // tenga el foco.
        private static readonly object _teclasLock = new object();
        private static long _teclaSeq;
        private static string _ultimaTecla = "";

        public sealed class TeclaReq { public string tecla { get; set; } }

        [Route(HttpVerbs.Post, "/teclado/tecla")]
        public async Task Tecla()
        {
            var req = await ReadJsonBodyAsync<TeclaReq>();
            lock (_teclasLock)
            {
                _ultimaTecla = req?.tecla ?? "";
                _teclaSeq++;
            }
            await WriteJsonAsync(new { ok = true, seq = _teclaSeq });
        }

        /// <summary>La página pregunta si hay tecla nueva desde la que ya aplicó.</summary>
        [Route(HttpVerbs.Get, "/teclado/teclas")]
        public Task Teclas([QueryField] long desde)
        {
            lock (_teclasLock)
            {
                bool hay = _teclaSeq > desde;
                return WriteJsonAsync(new { ok = true, seq = _teclaSeq, tecla = hay ? _ultimaTecla : null });
            }
        }
    }
}
