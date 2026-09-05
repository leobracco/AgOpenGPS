// ============================================================================
// AtomicJson.cs - Escritura/lectura segura de archivos de configuración JSON.
// Target: net48 (C# 7.3)
//
// Problema que resuelve:
//   `File.WriteAllText` trunca el archivo a 0 bytes y después escribe. Si la PC
//   se corta entre ambos pasos (apagado de golpe en cabina), el JSON queda vacío
//   o cortado. La próxima carga falla, cae a defaults y al volver a guardar pisa
//   lo que quedaba → se pierde la configuración del operario.
//
// Solución:
//   - Write(): escribe a `<archivo>.tmp`, fuerza el flush a disco físico
//     (Flush(true) = FlushFileBuffers) y reemplaza el destino con File.Replace,
//     que en NTFS es un rename atómico y deja la versión anterior como `.bak`.
//   - Read<T>(): si el archivo principal está vacío/corrupto, cae automáticamente
//     al `.bak` (última versión buena) antes de devolver null. Así un archivo
//     dañado no significa perder la config: se recupera del respaldo.
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AgroParallel.Common
{
    public static class AtomicJson
    {
        // Sufijo del respaldo de la última versión buena (lo deja File.Replace).
        public const string BakSuffix = ".bak";

        /// <summary>
        /// Escribe <paramref name="contents"/> en <paramref name="path"/> de forma
        /// atómica y durable: tmp + flush a disco + File.Replace. Conserva la
        /// versión anterior como `<path>.bak`. Lanza si no puede escribir el tmp.
        /// </summary>
        public static void Write(string path, string contents)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            string bak = path + BakSuffix;

            // 1. Escribir el tmp y forzar el flush a disco físico. Sin esto los
            //    datos pueden quedar en caché del OS/disco y perderse en un corte.
            var bytes = new UTF8Encoding(false).GetBytes(contents);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            // 2. Intercambio atómico. File.Replace exige que el destino exista y
            //    deja el viejo como .bak; si no existe, es la primera escritura.
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, bak);
                }
                catch (Exception)
                {
                    // Fallback raro (File.Replace falla en algunos FS/volúmenes o
                    // por antivirus): respaldar primero y recién después mover.
                    try { File.Copy(path, bak, true); } catch { }
                    try { File.Delete(path); } catch { }
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        /// <summary>
        /// Lee y deserializa <typeparamref name="T"/> desde <paramref name="path"/>.
        /// Si el archivo principal falta, está vacío o corrupto, intenta el `.bak`.
        /// Devuelve null sólo si ambos fallan (recién ahí el caller usa defaults).
        /// </summary>
        public static T Read<T>(string path, JsonSerializerOptions options) where T : class
        {
            string bak = path + BakSuffix;

            // REINTENTOS: la lectura puede caer JUSTO en la ventana de una
            // escritura concurrente. En el fallback de Write (cuando File.Replace
            // falla por antivirus/FS) hay un Delete seguido de un Move: entre los
            // dos, el archivo NO EXISTE. El lector desafortunado veía null y el
            // caller escribía defaults ENCIMA de la config buena — el operario
            // perdía los nodos configurados (caso de campo FlowX 2026-09-05, con
            // el bridge releyendo la config cada 2 s).
            for (int intento = 0; ; intento++)
            {
                var fromMain = TryDeserialize<T>(path, options);
                if (fromMain != null) return fromMain;

                // El principal no sirvió: recuperar de la última versión buena.
                var fromBak = TryDeserialize<T>(bak, options);
                if (fromBak != null)
                {
                    // Re-materializar el principal desde el respaldo para que la
                    // próxima lectura ya no dependa del .bak.
                    try { Write(path, File.ReadAllText(bak)); } catch { }
                    return fromBak;
                }

                // Sólo tiene sentido reintentar si HAY algo en disco: un archivo
                // que no existe no va a aparecer solo. 3 intentos × 40 ms cubren
                // de sobra un Delete+Move local.
                if (intento >= 2 || !Existe(path)) return null;
                try { System.Threading.Thread.Sleep(40); } catch { }
            }
        }

        /// <summary>
        /// ¿Hay algo en disco para este path (principal o respaldo)? Lo usan los
        /// Load() de las configs para NO escribir defaults encima de un archivo
        /// que existe pero no se pudo leer en ESTE instante: hacerlo destruye la
        /// configuración del cliente. Sin archivo, en cambio, crear el default es
        /// correcto (primer arranque).
        /// </summary>
        public static bool Existe(string path)
        {
            try { return File.Exists(path) || File.Exists(path + BakSuffix); }
            catch { return false; }
        }

        private static T TryDeserialize<T>(string path, JsonSerializerOptions options) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<T>(json, options);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
