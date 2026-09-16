// ============================================================================
// ResolutorLoteCloud.cs — decide DÓNDE cae un lote que baja de OrbitX y si hay
// que escribirle el lindero.
//
// El trabajo del operario no se pisa nunca: si ya existe una carpeta con ese
// nombre y NO es espejo del cloud, el lote de OrbitX entra como
// "<nombre> (OrbitX)". Antes se hacía BoundaryFiles.Save() directo sobre la
// carpeta existente y el lindero recorrido en cabina se perdía sin aviso
// (reporte 2026-09-12).
//
// El marcador .orbitx es lo que hace posible distinguir el espejo del cloud del
// lote local. Sin él, "guardar aparte" crearía un lote nuevo en CADA ciclo de
// sync. El SHA del KML además evita reescribir cuando el lindero no cambió.
//
// Lógica pura de archivos a propósito: vive acá y no en el motor para que sea
// testeable — AgroParallel.Services.Tests no referencia PilotX.GuidanceEngine.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgroParallel.Services.OrbitX
{
    public enum AccionLoteCloud
    {
        /// <summary>No hay carpeta: crear el lote y escribir el marcador.</summary>
        Crear,
        /// <summary>Es el espejo del cloud y el lindero cambió: reescribir.</summary>
        ActualizarEspejo,
        /// <summary>Es el espejo y el KML es el mismo: no tocar el disco.</summary>
        SinCambios,
        /// <summary>Se agotaron los sufijos. El caller loguea y descarta.</summary>
        SinLugar,
    }

    public sealed class DestinoLoteCloud
    {
        public string Directorio { get; set; }
        public string NombreCarpeta { get; set; }
        public AccionLoteCloud Accion { get; set; }
    }

    public static class ResolutorLoteCloud
    {
        public const string NombreMarcador = ".orbitx";
        private const int MaxSufijos = 20;

        public static DestinoLoteCloud Resolver(string fieldsRoot, string nombreCloud, string shaKml)
        {
            string limpio = LimpiarNombre(nombreCloud);
            if (string.IsNullOrEmpty(limpio) || string.IsNullOrEmpty(fieldsRoot))
                return new DestinoLoteCloud { Accion = AccionLoteCloud.SinLugar };

            for (int intento = 0; intento <= MaxSufijos; intento++)
            {
                string candidato = NombreCandidato(limpio, intento);
                string dir = Path.Combine(fieldsRoot, candidato);

                if (!Directory.Exists(dir))
                {
                    return new DestinoLoteCloud
                    {
                        Directorio = dir,
                        NombreCarpeta = candidato,
                        Accion = AccionLoteCloud.Crear,
                    };
                }

                var marcador = LeerMarcador(dir);
                bool esEspejoDeEsteLote = marcador != null &&
                    string.Equals(marcador.LoteCloud, nombreCloud, StringComparison.OrdinalIgnoreCase);

                if (esEspejoDeEsteLote)
                {
                    // No basta con que el marcador diga "mismo SHA": si el
                    // Boundary.txt del espejo desapareció (se borró a mano, un
                    // sync anterior murió a mitad de camino, etc.) el marcador
                    // queda huérfano y "SinCambios" dejaría el lindero perdido
                    // PARA SIEMPRE — ni un re-push del cloud lo trae de vuelta,
                    // porque el SHA sigue siendo el mismo. Sin el archivo, se
                    // reescribe igual aunque el SHA no haya cambiado.
                    bool boundaryExiste = File.Exists(Path.Combine(dir, "Boundary.txt"));
                    return new DestinoLoteCloud
                    {
                        Directorio = dir,
                        NombreCarpeta = candidato,
                        Accion = (boundaryExiste && string.Equals(marcador.ShaKml, shaKml, StringComparison.Ordinal))
                            ? AccionLoteCloud.SinCambios
                            : AccionLoteCloud.ActualizarEspejo,
                    };
                }

                // Ocupado por el operario (o por el espejo de OTRO lote cloud):
                // no se toca, se prueba el sufijo siguiente.
            }

            return new DestinoLoteCloud { Accion = AccionLoteCloud.SinLugar };
        }

        // intento 0 = el nombre pelado; 1 = "(OrbitX)"; 2+ = "(OrbitX N)".
        private static string NombreCandidato(string limpio, int intento)
        {
            if (intento == 0) return limpio;
            if (intento == 1) return limpio + " (OrbitX)";
            return limpio + " (OrbitX " + intento.ToString(CultureInfo.InvariantCulture) + ")";
        }

        public static void EscribirMarcador(string directorio, string nombreCloud, string shaKml)
        {
            var doc = new MarcadorOrbitX
            {
                LoteCloud = nombreCloud,
                ShaKml = shaKml,
                Ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(Path.Combine(directorio, NombreMarcador),
                              JsonSerializer.Serialize(doc));
        }

        private static MarcadorOrbitX LeerMarcador(string directorio)
        {
            try
            {
                string ruta = Path.Combine(directorio, NombreMarcador);
                if (!File.Exists(ruta)) return null;
                return JsonSerializer.Deserialize<MarcadorOrbitX>(File.ReadAllText(ruta));
            }
            catch
            {
                // Marcador roto = no sabemos de quién es la carpeta. Se trata como
                // del operario: no pisar nunca es la regla.
                return null;
            }
        }

        public static string CalcularSha(string contenido)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(contenido ?? ""));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Saca los caracteres que no valen como nombre de carpeta.
        /// Mismo criterio que EngineLotesService.CleanName: se eliminan, no se
        /// reemplazan.</summary>
        public static string LimpiarNombre(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return "";
            var invalidos = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(nombre.Length);
            foreach (char c in nombre)
                if (Array.IndexOf(invalidos, c) < 0) sb.Append(c);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Lee el <c>lote_cloud</c> grabado en el marcador .orbitx de una carpeta,
        /// o null si la carpeta no tiene marcador (es del operario) o el marcador
        /// está roto. Expuesto para que otros callers (ej. el borrado de lotes,
        /// que necesita saber si la carpeta que se está por borrar es el espejo
        /// de un lote cloud) no dupliquen el parseo del JSON — la única fuente de
        /// verdad sobre el formato del marcador es este archivo.
        /// </summary>
        public static string LeerLoteCloud(string directorio)
            => LeerMarcador(directorio)?.LoteCloud;

        /// <summary>
        /// True si "candidato" queda DENTRO del árbol de "root" (ambos resueltos
        /// con GetFullPath), INCLUYENDO al propio root — es un chequeo genérico
        /// de pertenencia al árbol, no una guarda de borrado. LimpiarNombre saca
        /// caracteres inválidos de archivo, pero ".." no es uno de ellos — un
        /// Path.Combine(root, "../../algo") sigue resolviendo hacia AFUERA de
        /// root sin que LimpiarNombre lo note.
        ///
        /// OJO: para decidir si una carpeta se puede BORRAR no alcanza con esto
        /// — el propio root "queda dentro de root" y Directory.Delete(root, true)
        /// borraría TODOS los lotes (hallazgo 2026-09-16, repro real). Esa guarda,
        /// más estricta, es <see cref="EsCarpetaDeLoteBorrable"/>.
        /// </summary>
        public static bool QuedaDentroDeRoot(string root, string candidato)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidato)) return false;
            try
            {
                string fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullCand = Path.GetFullPath(candidato);
                return fullCand.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                    || fullCand.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Ruta ilegible (chars raros, etc.): del lado seguro, no está adentro.
                return false;
            }
        }

        /// <summary>
        /// Guarda real para BORRAR una carpeta de lote — más estricta que
        /// <see cref="QuedaDentroDeRoot"/>. Exige DOS cosas del resuelto
        /// (Path.GetFullPath) de root+nombreLimpio:
        ///   1) el padre resuelto es EXACTAMENTE el root (no el root mismo, ni
        ///      dos niveles adentro por un separador raro).
        ///   2) el nombre de hoja resuelto es IGUAL a nombreLimpio.
        ///
        /// Por qué hacen falta las dos, con repro real (hallazgo 2026-09-16):
        ///   · nombreLimpio = "." o "..." → Windows COLAPSA los puntos finales
        ///     al resolver la ruta y el resultado es el propio root → sin la
        ///     condición (1) se borraría Fields/ entero.
        ///   · nombreLimpio = "Campo.." o "Campo." → resuelve a "Fields/Campo"
        ///     (existe de verdad) pero la hoja resuelta ("Campo") NO coincide
        ///     con el nombre pedido ("Campo..") → sin la condición (2) se
        ///     borraría "Campo" salteando la guarda del lote abierto, que
        ///     compara contra el nombre CRUDO, no contra el resuelto.
        ///
        /// devuelve, en directorioResuelto, la ruta ya resuelta — el caller la
        /// necesita para comparar contra el lote abierto por el nombre
        /// REALMENTE afectado, no por el nombre crudo que mandó el cliente.
        /// </summary>
        public static bool EsCarpetaDeLoteBorrable(string root, string nombreLimpio, out string directorioResuelto)
        {
            directorioResuelto = null;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(nombreLimpio)) return false;
            try
            {
                string fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(Path.Combine(root, nombreLimpio))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!string.Equals(Path.GetDirectoryName(full), fullRoot, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.Equals(Path.GetFileName(full), nombreLimpio, StringComparison.OrdinalIgnoreCase))
                    return false;

                directorioResuelto = full;
                return true;
            }
            catch
            {
                // Ruta ilegible: del lado seguro, no se puede borrar.
                return false;
            }
        }

        /// <summary>
        /// Decide qué nombres hay que tombstonear (avisarle al sync que no
        /// vuelvan a bajar) cuando se borró la carpeta "nombreCarpetaBorrada"
        /// dentro de "fieldsRoot". Pensada para llamarse DESPUÉS de borrar la
        /// carpeta del disco — ya no está, así que no aparece como "hermana"
        /// en el barrido de abajo.
        ///
        /// Devuelve hasta dos nombres:
        ///   · el nombre de la propia carpeta borrada — SALVO que un hermano
        ///     VIVO tenga un marcador .orbitx cuyo lote_cloud sea ESE mismo
        ///     nombre (arreglo 2026-09-16): en ese caso el nombre cloud le
        ///     pertenece al espejo ajeno, no al lote recién borrado, y
        ///     tombstonearlo dejaría a ese espejo congelado para siempre
        ///     esperando un sync que el tombstone bloquea.
        ///   · el lote_cloud del propio marcador de la carpeta borrada
        ///     (loteCloudDelPropioMarcador), si tenía uno y era distinto de su
        ///     nombre de carpeta — o sea, si la carpeta borrada era ELLA MISMA
        ///     el espejo de otro nombre cloud.
        /// </summary>
        public static IReadOnlyList<string> NombresATombstonearTrasBorrar(
            string fieldsRoot, string nombreCarpetaBorrada, string loteCloudDelPropioMarcador)
        {
            var resultado = new List<string>();
            if (string.IsNullOrEmpty(nombreCarpetaBorrada)) return resultado;

            if (!ExisteHermanoQueReclamaEseNombreCloud(fieldsRoot, nombreCarpetaBorrada))
                resultado.Add(nombreCarpetaBorrada);

            if (!string.IsNullOrEmpty(loteCloudDelPropioMarcador) &&
                !string.Equals(loteCloudDelPropioMarcador, nombreCarpetaBorrada, StringComparison.OrdinalIgnoreCase))
            {
                resultado.Add(loteCloudDelPropioMarcador);
            }

            return resultado;
        }

        // Barre las carpetas que quedan en fieldsRoot buscando un marcador
        // .orbitx cuyo lote_cloud sea exactamente nombreCloud: si existe, ese
        // nombre "pertenece" a esa carpeta viva, no al lote que se acaba de
        // borrar.
        private static bool ExisteHermanoQueReclamaEseNombreCloud(string fieldsRoot, string nombreCloud)
        {
            if (string.IsNullOrEmpty(fieldsRoot) || !Directory.Exists(fieldsRoot)) return false;
            try
            {
                foreach (var dir in Directory.GetDirectories(fieldsRoot))
                {
                    string marcador = LeerLoteCloud(dir);
                    if (string.Equals(marcador, nombreCloud, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* del lado seguro: si no se puede enumerar, no se asume dueño */ }
            return false;
        }

        private sealed class MarcadorOrbitX
        {
            [System.Text.Json.Serialization.JsonPropertyName("lote_cloud")]
            public string LoteCloud { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sha_kml")]
            public string ShaKml { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("ts")]
            public string Ts { get; set; }
        }
    }
}
