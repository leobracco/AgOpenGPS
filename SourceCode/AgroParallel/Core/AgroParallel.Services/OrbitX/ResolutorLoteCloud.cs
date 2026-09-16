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
        /// con GetFullPath). LimpiarNombre saca caracteres inválidos de archivo,
        /// pero ".." no es uno de ellos — un Path.Combine(root, "../../algo")
        /// sigue resolviendo hacia AFUERA de root sin que LimpiarNombre lo note.
        /// Sin este chequeo aparte, un endpoint de borrado sin auth en la LAN
        /// podría borrar cualquier carpeta del disco, no solo lotes.
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
