// ============================================================================
// ElevacionPartes.cs — parte Elevation.txt (registro de alturas del lote) en
// pedazos de N filas para subirlo a OrbitX.
//
// Por qué no se sube entero como el resto de los archivos del lote: el sync
// re-sube el archivo cada vez que cambia su hash y el server (routes/aog.js,
// POST /api/aog/sync) guarda una copia histórica COMPLETA en cada cambio. Un
// lote grande junta decenas de miles de puntos (~60 bytes cada uno): subirlo
// entero cada 30 s mientras crece sería tráfico de datos móviles tirado y
// cientos de copias de varios MB en CouchDB.
//
// Partido en partes de N filas, cada parte COMPLETA no cambia nunca más: se
// sube una vez y el server no archiva nada (mismo hash). Solo la última parte
// (la que está creciendo) cambia — y el que llama decide cada cuánto la sube.
//
// Cada parte es un Elevation.txt válido por sí mismo (lleva la cabecera
// original), así el server puede parsear cada una sin juntar nada. Unir las
// partes = las filas en orden de índice.
//
// Lógica pura: vive acá para ser testeable (ElevacionPartesTests).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgroParallel.Services.OrbitX
{
    public static class ElevacionPartes
    {
        /// <summary>5000 filas ≈ 300 KB de texto por parte: un POST razonable
        /// por celular, y 5 km de recorrido a un punto por metro.</summary>
        public const int FilasPorParteDefault = 5000;

        public const string Subtipo = "elevation_points";

        public sealed class Parte
        {
            /// <summary>1, 2, 3… en el orden del archivo.</summary>
            public int Indice;
            /// <summary>Cabecera original + filas, CRLF, termina en salto de línea.</summary>
            public string Contenido;
            public int Filas;
            /// <summary>true = tiene las N filas y ya no va a cambiar.</summary>
            public bool Completa;
        }

        public static string NombreParte(int indice)
            => "Elevation_" + indice.ToString("0000", CultureInfo.InvariantCulture) + ".txt";

        /// <summary>
        /// Parte el contenido de un Elevation.txt. Sin filas de datos (por
        /// ejemplo, un lote migrado de AOG que trae solo la cabecera) devuelve
        /// una lista vacía: no hay nada que subir. Una última línea sin salto de
        /// línea se ignora (el motor puede estar escribiéndola).
        /// </summary>
        public static List<Parte> Partir(string contenido, int filasPorParte = FilasPorParteDefault)
        {
            var partes = new List<Parte>();
            if (string.IsNullOrEmpty(contenido)) return partes;
            if (filasPorParte < 1) filasPorParte = 1;

            // Solo líneas TERMINADAS: lo que viene después del último '\n' se descarta.
            int fin = contenido.LastIndexOf('\n');
            if (fin < 0) return partes;
            string[] lineas = contenido.Substring(0, fin).Split('\n');

            var cabecera = new StringBuilder();
            var filas = new List<string>();
            foreach (string cruda in lineas)
            {
                string l = cruda.TrimEnd('\r');
                if (EsFilaDeDatos(l)) filas.Add(l);
                else if (filas.Count == 0) cabecera.Append(l).Append("\r\n");
                // Una línea basura entre filas de datos no viaja.
            }
            if (filas.Count == 0) return partes;

            string cab = cabecera.ToString();
            for (int i = 0, indice = 1; i < filas.Count; i += filasPorParte, indice++)
            {
                int n = Math.Min(filasPorParte, filas.Count - i);
                var sb = new StringBuilder(cab.Length + n * 64);
                sb.Append(cab);
                for (int k = 0; k < n; k++) sb.Append(filas[i + k]).Append("\r\n");
                partes.Add(new Parte
                {
                    Indice = indice,
                    Contenido = sb.ToString(),
                    Filas = n,
                    Completa = n == filasPorParte,
                });
            }
            return partes;
        }

        /// <summary>Misma regla que ElevationFiles.EsFilaDeDatos (AgOpenGPS.Core):
        /// ≥ 8 campos y el primero es un número.</summary>
        public static bool EsFilaDeDatos(string linea)
        {
            if (string.IsNullOrEmpty(linea)) return false;
            int comas = 0, primera = -1;
            for (int i = 0; i < linea.Length; i++)
            {
                if (linea[i] != ',') continue;
                if (primera < 0) primera = i;
                comas++;
            }
            if (comas < 7 || primera <= 0) return false;
            double _;
            return double.TryParse(linea.Substring(0, primera), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }
    }
}
