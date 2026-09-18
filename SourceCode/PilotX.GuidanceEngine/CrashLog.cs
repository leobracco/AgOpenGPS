// ============================================================================
// CrashLog.cs — que un error en el motor quede escrito y no se lo lleve el aire.
//
// El motor tenia DOS problemas, y el segundo tapaba al primero:
//
//   1. Ningun manejador global. Una excepcion no atendida mataba el proceso de
//      guiado sin dejar rastro; en la cabina eso es "PilotX dejo de moverse" y
//      nada mas.
//
//   2. Log.EventWriter escribe a un StringBuilder en memoria, y quien lo baja a
//      disco es FileSaveSystemEvents() — que SOLO lo llamaban CoreX y FormGPS.
//      El motor nunca lo llamaba, asi que todo lo que fue logueando (lote
//      abierto, cobertura guardada, Sections.txt que no se pudo leer) se
//      acumulaba sin escribirse nunca y moria con el proceso. Encima el buffer
//      crecia toda la jornada.
//
// Aca no hay cartel: el motor es headless. Lo que importa es que quede escrito
// con su codigo AGP-*, el mismo que ve el operario en la pantalla, para que al
// cruzar el log con lo que reporto se hable del mismo numero.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using AgLibrary.Logging;
using AgroParallel.Services;

namespace PilotX.GuidanceEngine
{
    public static class CrashLog
    {
        private static Timer _volcado;

        // Cada cuanto se baja el log a disco. Un corte de luz en la cabina se
        // lleva, como mucho, lo de la ultima media vuelta.
        private static readonly TimeSpan CadaCuanto = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Lo llama Main lo antes posible, DESPUES de RegistrySettings.Load()
        /// (que es quien define la ruta del archivo via Log.CheckLogSize).
        /// </summary>
        public static void Instalar()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Registrar(e.ExceptionObject as Exception, "dominio", e.IsTerminating);

            // Sin esto, una excepcion dentro de un Task que nadie await-eo se
            // pierde en silencio. En el motor eso se ve como "dejo de
            // actualizarse tal cosa" y no hay por donde empezar.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Registrar(e.Exception, "tarea", false);
                e.SetObserved();
            };

            // Al cerrar bien, que no quede nada en el buffer.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Volcar();

            _volcado = new Timer(_ => Volcar(), null, CadaCuanto, CadaCuanto);

            Log.EventWriter("GuidanceEngine: manejador de errores instalado");
            Volcar();
        }

        /// <summary>
        /// Deja el error en el log con su codigo AGP y lo baja a disco en el
        /// acto: si el proceso se esta muriendo, esperar al timer no sirve.
        /// </summary>
        public static void Registrar(Exception ex, string origen, bool fatal)
        {
            AgpError err;
            try { err = AgpErrorMapper.FromException(ex); }
            catch { err = new AgpError("AGP-SYS-009", "Algo salio mal.", ex?.Message ?? "(sin detalle)"); }

            try
            {
                Log.EventWriter($"[{err.Code}] {err.Friendly} (origen={origen}, fatal={fatal})");

                // Toda la cadena: la causa raiz casi nunca es la de arriba.
                var e = ex;
                int nivel = 0;
                while (e != null)
                {
                    Log.EventWriter($"  [{nivel}] {e.GetType().FullName}: {e.Message}");
                    if (!string.IsNullOrEmpty(e.StackTrace)) Log.EventWriter(e.StackTrace);
                    e = e.InnerException;
                    nivel++;
                }
            }
            catch { }

            // Consola aparte del log: es lo que se captura cuando el motor se
            // lanza redirigido desde un script o una tarea.
            try { Console.Error.WriteLine($"[{err.Code}] {err.Friendly} (origen={origen}, fatal={fatal})"); }
            catch { }

            Volcar();
        }

        /// <summary>Baja a disco lo que haya en el buffer. Nunca tira.</summary>
        public static void Volcar()
        {
            try { Log.FileSaveSystemEvents(); } catch { }
        }
    }
}
