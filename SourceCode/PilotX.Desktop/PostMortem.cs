// ============================================================================
// PostMortem.cs — rescatar la lápida de la corrida anterior.
//
// POR QUÉ EXISTE (2026-09-19, investigando 15 caídas del vigilante).
//
// CrashHandler engancha AppDomain.UnhandledException y sirve para las
// excepciones managed normales. Pero NO alcanza para el tipo de caída que
// venimos sufriendo, y esto está COMPROBADO, no supuesto: el 19/09 a las
// 10:46:42 la pantalla murió con 0xC0000005 y en Build\Desktop\Logs no había
// NADA — ni siquiera la carpeta existía. Cero líneas, cero rastro.
//
// La razón es que un ACCESS_VIOLATION es una "corrupted state exception": el
// runtime NO se la entrega al código managed, la considera irrecuperable y
// baja el proceso desde su propio manejador de error fatal. Los handlers de
// AppDomain jamás corren. Lo único que quedaba era un evento 1026 en el Visor
// de eventos de Windows — que nadie mira, y que en la pantalla de un tractor
// a 200 km es directamente inalcanzable.
//
// LO QUE SÍ FUNCIONA. Antes de morir, el runtime escribe por stderr el
// cartel "Fatal error." con el tipo de excepción y el stack completo. Eso se
// verificó en banco provocando un ACCESS_VIOLATION de verdad (escritura a una
// dirección inválida): sale por stderr y el proceso termina con
// -1073741819, el mismo código que registró el vigilante. Como
// PilotX.Desktop es una app de ventana, ese stderr no va a ningún lado salvo
// que alguien lo redirija: de eso se encarga Lanzar-PilotX.bat, que manda la
// salida de cada corrida a Logs\salida.log y rota la anterior a
// Logs\salida-anterior.log. Esta clase levanta esa rotada en el arranque
// siguiente y, si terminó en "Fatal error.", lo deja escrito en errores.log.
//
// Como el vigilante relanza a los 3 segundos, el proceso nuevo recoge la
// lápida del anterior solo: cuando alguien abra errores.log va a encontrar la
// caída contada, con stack, sin depender de que nadie estuviera mirando.
//
// (Se probó también el crash report en JSON del runtime —
// DOTNET_EnableCrashReport— y en .NET 9.0.14 NO genera el archivo, así que no
// se usa. El minidump sí se genera y queda en Logs\crash\ para cuando haga
// falta abrirlo con WinDbg o dotnet-dump; esta clase solo lo nombra y se
// encarga de que no se acumulen.)
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PilotX.Desktop
{
    internal static class PostMortem
    {
        /// <summary>Carpeta de logs de la pantalla (la misma de errores.log).</summary>
        private static string DirLogs => Path.Combine(AppContext.BaseDirectory, "Logs");

        /// <summary>Dónde deja el runtime los minidumps. Tiene que coincidir con
        /// DOTNET_DbgMiniDumpName del .bat del vigilante.</summary>
        public static string DirVolcados => Path.Combine(DirLogs, "crash");

        /// <summary>Salida de la corrida ANTERIOR, rotada por el vigilante.</summary>
        private static string SalidaAnterior => Path.Combine(DirLogs, "salida-anterior.log");

        /// <summary>Cuántos minidumps se conservan. Cada uno pesa ~9 MB y la
        /// pantalla de cabina no tiene disco de sobra; con los 5 últimos
        /// alcanza para ver si una caída es nueva o la de siempre.</summary>
        private const int VolcadosAConservar = 5;

        /// <summary>Cuánto se lee del final de la salida anterior. El cartel de
        /// error fatal es lo ÚLTIMO que escribe el runtime, así que con la cola
        /// alcanza y no hay que cargar en memoria un log de varios MB.</summary>
        private const int ColaBytes = 256 * 1024;

        /// <summary>
        /// Lo llama Main al arrancar, apenas instalado el CrashHandler. Nunca
        /// tira: si esto fallara, la pantalla no arrancaría por culpa del
        /// mecanismo que existe justamente para cuando algo sale mal.
        /// </summary>
        public static void RevisarCorridaAnterior()
        {
            try
            {
                RevisarSalidaAnterior();
                Podar();

                // La salida ya contada se guarda por si el stack no alcanzó,
                // pero una corrida larga con el espía de render hablando puede
                // dejar un archivo enorme. Pasado el tope no vale lo que ocupa:
                // lo importante (el "Fatal error." con el stack) ya está
                // copiado en errores.log.
                try
                {
                    var leido = new FileInfo(SalidaAnterior + ".leido");
                    if (leido.Exists && leido.Length > 20L * 1024 * 1024) leido.Delete();
                }
                catch { }
            }
            catch { /* el post-mortem jamás puede impedir que la pantalla abra */ }
        }

        private static void RevisarSalidaAnterior()
        {
            if (!File.Exists(SalidaAnterior)) return;

            string cola;
            try { cola = LeerCola(SalidaAnterior, ColaBytes); }
            catch { return; }

            // El runtime escribe "Fatal error." y a continuación el tipo de
            // excepción y el stack. Si no está, la corrida anterior terminó
            // como corresponde (o la mató el operario) y no hay nada que
            // contar: no se ensucia el log con ruido.
            int i = cola.LastIndexOf("Fatal error.", StringComparison.Ordinal);
            if (i < 0)
            {
                Archivar();
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("La corrida ANTERIOR de la pantalla murió por una falla que el runtime");
            sb.AppendLine("no le entrega al código (ACCESS_VIOLATION o similar), por eso no pasó");
            sb.AppendLine("por el CrashHandler. Esto es lo último que alcanzó a escribir:");
            sb.AppendLine();
            sb.AppendLine(cola.Substring(i).TrimEnd());
            sb.AppendLine();
            sb.AppendLine("  salida completa: " + SalidaAnterior + ".leido");

            var dmp = UltimoVolcado();
            if (dmp != null)
            {
                sb.AppendLine("  minidump       : " + dmp.FullName
                    + "  (" + (dmp.Length / (1024 * 1024)) + " MB, "
                    + dmp.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + ")");
                sb.AppendLine("  para abrirlo   : dotnet-dump analyze \"" + dmp.FullName + "\"");
            }

            CrashHandler.RegistrarTexto(sb.ToString(), "post-mortem");
            Archivar();
        }

        /// <summary>
        /// Marca la salida anterior como ya contada. Se renombra en vez de
        /// borrarse: si el stack no alcanza, el log entero de esa corrida sigue
        /// estando. Si la pantalla se abre a mano (sin el vigilante) no hay
        /// rotación, y sin esto la misma caída se volvería a volcar en cada
        /// arranque.
        /// </summary>
        private static void Archivar()
        {
            try { File.Move(SalidaAnterior, SalidaAnterior + ".leido", overwrite: true); }
            catch { try { File.Delete(SalidaAnterior); } catch { } }
        }

        private static FileInfo? UltimoVolcado()
        {
            try
            {
                if (!Directory.Exists(DirVolcados)) return null;
                return new DirectoryInfo(DirVolcados)
                    .GetFiles("*.dmp")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Últimos <paramref name="bytes"/> del archivo, como texto.</summary>
        private static string LeerCola(string ruta, int bytes)
        {
            using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > bytes) fs.Seek(-bytes, SeekOrigin.End);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            return sr.ReadToEnd();
        }

        /// <summary>
        /// Deja solo los últimos minidumps. Sin esto, una racha de caídas como
        /// la del 05/09 (cuatro en 43 segundos) llena el disco de la pantalla
        /// con ~9 MB por vuelta y el remedio termina siendo peor que la falla.
        /// </summary>
        private static void Podar()
        {
            try
            {
                if (!Directory.Exists(DirVolcados)) return;
                var dumps = new DirectoryInfo(DirVolcados)
                    .GetFiles("*.dmp")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(VolcadosAConservar);
                foreach (var f in dumps)
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}
