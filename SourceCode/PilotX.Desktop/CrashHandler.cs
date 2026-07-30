// ============================================================================
// CrashHandler.cs — que un error no le tire a la cabina el diálogo de Windows.
//
// Hasta ahora PilotX.Desktop no tenía NINGÚN manejador global: una excepción
// no atendida llegaba cruda al runtime y Windows mostraba su cartel de
// "dejó de funcionar / ver detalles / continuar / salir". En una pantalla
// táctil en el tractor eso es lo peor que puede pasar: el operario no entiende
// qué pasó, no puede copiar nada, y lo único que le queda es cerrar y perder
// lo que estaba haciendo.
//
// Lo que hace este handler:
//   1. Escribe el detalle COMPLETO (tipo, mensaje, stack, cadena de inner
//      exceptions) a un archivo. Eso es lo que sirve para diagnosticar.
//   2. Le muestra al operario un cartel simple con un CÓDIGO (AGP-SYS-001,
//      AGP-MQTT-002…) y una frase en criollo de qué hacer. El código es lo
//      que después nos dice por dónde arrancar sin tener que pedirle que
//      lea un stack trace por teléfono.
//   3. No cierra la app si el error no es fatal: muchas excepciones sueltas
//      de una tarea de fondo no justifican perder la jornada.
//
// El mapeo excepción -> código vive en AgpErrorMapper, compartido con el
// resto del sistema para que un mismo problema tenga siempre el mismo número.
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Services;

namespace PilotX.Desktop
{
    public static class CrashHandler
    {
        private static readonly object _candado = new object();
        private static string _archivo;

        /// <summary>Ruta del log de errores. Público para poder mostrarla en el cartel.</summary>
        public static string ArchivoLog
        {
            get
            {
                if (_archivo != null) return _archivo;
                // Al lado del ejecutable: en la cabina no hay perfil de usuario
                // prolijo y buscar en AppData por teléfono no es viable.
                var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
                try { Directory.CreateDirectory(dir); } catch { }
                _archivo = Path.Combine(dir, "errores.log");
                return _archivo;
            }
        }

        /// <summary>Lo llama Main antes de levantar Avalonia.</summary>
        public static void Instalar()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var err = Registrar(e.ExceptionObject as Exception, "dominio", fatal: e.IsTerminating);
                Mostrar(err, e.IsTerminating);
            };

            // Sin esto, una excepción dentro de un Task que nadie await-eó se
            // pierde en silencio: el síntoma es "algo dejó de actualizarse" sin
            // ninguna pista. Se marca como observada para que no escale.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Registrar(e.Exception, "tarea", fatal: false);
                e.SetObserved();
            };
        }

        /// <summary>
        /// Deja el error en el log y devuelve el código para mostrarle al
        /// operario. Nunca tira: un handler de errores que falla deja al
        /// programa peor que antes.
        /// </summary>
        public static AgpError Registrar(Exception? ex, string origen, bool fatal)
        {
            AgpError err;
            try { err = AgpErrorMapper.FromException(ex); }
            catch { err = new AgpError("AGP-SYS-009", "Algo salió mal.", ex?.Message ?? "(sin detalle)"); }

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(new string('=', 78));
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{err.Code}]  origen={origen}  fatal={fatal}");
                sb.AppendLine($"  {err.Friendly}");
                sb.AppendLine();

                // Toda la cadena de inner exceptions: la causa raíz casi nunca
                // es la excepción de arriba.
                var e = ex;
                int nivel = 0;
                while (e != null)
                {
                    sb.AppendLine($"  [{nivel}] {e.GetType().FullName}: {e.Message}");
                    if (!string.IsNullOrEmpty(e.StackTrace)) sb.AppendLine(e.StackTrace);
                    e = e.InnerException;
                    nivel++;
                }
                sb.AppendLine();

                lock (_candado)
                {
                    RotarSiHaceFalta();
                    File.AppendAllText(ArchivoLog, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { /* si no se puede loguear, al menos que siga el cartel */ }

            // Que quede también en la consola redirigida, que es lo que se
            // captura cuando se lanza desde una tarea o un script.
            try { Console.Error.WriteLine($"[{err.Code}] {err.Friendly} ({origen}, fatal={fatal})"); } catch { }

            return err;
        }

        /// <summary>
        /// Muestra el cartel al operario. Va al hilo de UI porque el error
        /// puede venir de cualquier thread, y si Avalonia todavía no arrancó
        /// (o ya se está cayendo) simplemente no se muestra: el log ya quedó,
        /// que es lo que importa.
        /// </summary>
        private static void Mostrar(AgpError err, bool fatal)
        {
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        Views.ErrorDialog
                            .Crear(err.Code, err.Friendly, err.Technical, ArchivoLog, fatal)
                            .Show();
                    }
                    catch { /* sin UI viva no hay cartel; el log ya esta */ }
                });
            }
            catch { }
        }

        /// <summary>
        /// Corta el log si se pasa de 2 MB. Sin esto, un error que se repite en
        /// loop llena el disco de la pantalla, que no sobra.
        /// </summary>
        private static void RotarSiHaceFalta()
        {
            try
            {
                var fi = new FileInfo(ArchivoLog);
                if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return;
                var viejo = ArchivoLog + ".1";
                if (File.Exists(viejo)) File.Delete(viejo);
                File.Move(ArchivoLog, viejo);
            }
            catch { }
        }
    }
}
