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
using System.Collections.Generic;
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
                    // Anti-tormenta. Una falla que se repite en el hilo de
                    // render no llega de a una: un contexto GL perdido escribía
                    // ~70 KB/s hasta llenarle el disco a la pantalla de cabina.
                    //
                    // La firma es tipo + stack, NO el mensaje: dos fallas
                    // distintas pueden compartir texto, y colapsarlas escondería
                    // una de las dos.
                    //
                    // VENTANA de firmas, no "la última". La primera versión
                    // comparaba solo contra la anterior y NO SIRVIÓ: la pérdida
                    // de contexto tira DOS excepciones que se alternan
                    // (CompositionImportedGpuImage.Import y
                    // ServerCompositionDrawingSurface.UpdateWithKeyedMutex,
                    // medido 672 y 671 en un mismo episodio). Con A,B,A,B la
                    // firma nunca coincide con la de recién y se escribía todo
                    // igual: 109 KB en 5 s. Con un diccionario por ventana, cada
                    // firma distinta se escribe entera UNA vez por ventana y el
                    // resto solo suma al contador.
                    string firma = Firma(ex, err.Code, origen);
                    var ahora = DateTime.UtcNow;

                    if ((ahora - _ventanaDesde) > VentanaDedup)
                    {
                        VolcarResumenDeVentana();
                        _firmasVentana.Clear();
                        _ventanaDesde = ahora;
                    }

                    if (_firmasVentana.TryGetValue(firma, out long vistas))
                    {
                        _firmasVentana[firma] = vistas + 1;
                        return err;   // ya se escribió entera en esta ventana
                    }

                    _firmasVentana[firma] = 1;
                    _resumenPendiente = true;

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

        // ---- Anti-tormenta de errores repetidos ---------------------------
        //
        // 30 s: corto para que una falla que sigue viva deje rastro periódico
        // (no queda enterrada en un contador que nadie ve), y largo para que una
        // tormenta de miles por minuto colapse a un par de líneas.
        private static readonly TimeSpan VentanaDedup = TimeSpan.FromSeconds(30);
        private static readonly Dictionary<string, long> _firmasVentana = new();
        private static DateTime _ventanaDesde = DateTime.UtcNow;
        private static bool _resumenPendiente;

        /// <summary>
        /// Cierra la ventana: deja UNA línea con lo que se repitió y cuántas
        /// veces. Sin esto el contador se perdería y el log diría que la falla
        /// pasó una sola vez.
        /// </summary>
        private static void VolcarResumenDeVentana()
        {
            if (!_resumenPendiente) return;
            _resumenPendiente = false;
            try
            {
                var repetidas = new List<string>();
                foreach (var kv in _firmasVentana)
                {
                    if (kv.Value <= 1) continue;
                    // De la firma solo interesa el tipo de excepción; el stack
                    // completo ya quedó escrito arriba, repetirlo no suma.
                    string tipo = kv.Key;
                    int corte = tipo.IndexOf('#');
                    if (corte > 0) tipo = tipo.Substring(0, corte);
                    repetidas.Add($"{tipo} x{kv.Value}");
                }
                if (repetidas.Count == 0) return;

                RotarSiHaceFalta();
                File.AppendAllText(ArchivoLog,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [resumen ultimos "
                    + $"{(int)VentanaDedup.TotalSeconds}s] se repitieron: "
                    + string.Join(" · ", repetidas) + "\r\n\r\n",
                    Encoding.UTF8);
            }
            catch { /* el resumen es un lujo; nunca puede tumbar el handler */ }
        }

        /// <summary>
        /// Identidad de la falla: tipo + stack de toda la cadena, más el código
        /// y el origen. El MENSAJE queda afuera a propósito — suele traer datos
        /// variables (rutas, ids) que harían distinta cada repetición y el
        /// dedup no agarraría nunca.
        /// </summary>
        private static string Firma(Exception ex, string codigo, string origen)
        {
            var sb = new StringBuilder(codigo).Append('|').Append(origen);
            var e = ex;
            int nivel = 0;
            while (e != null && nivel < 8)
            {
                sb.Append('|').Append(e.GetType().FullName)
                  .Append('#').Append(e.StackTrace ?? "");
                e = e.InnerException;
                nivel++;
            }
            return sb.ToString();
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
