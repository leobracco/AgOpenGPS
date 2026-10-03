// AgOpenGPS.IO/ElevationFiles.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.IO
{
    /// <summary>
    /// Stateless reader/writer helpers for Elevation.txt.
    /// </summary>
    public static class ElevationFiles
    {
        /// <summary>
        /// Create or overwrite Elevation.txt with header.
        /// </summary>
        public static void CreateHeader(string fieldDirectory, DateTime timestamp, Wgs84 startFix)
        {
            if (string.IsNullOrEmpty(fieldDirectory))
            {
                throw new ArgumentNullException(nameof(fieldDirectory));
            }

            if (!Directory.Exists(fieldDirectory))
            {
                Directory.CreateDirectory(fieldDirectory);
            }

            var path = Path.Combine(fieldDirectory, FileName);
            using (var writer = new StreamWriter(path, false))
            {
                writer.Write(BuildHeader(timestamp, startFix));
            }
        }

        public const string FileName = "Elevation.txt";

        /// <summary>Línea de columnas de AOG. La cabecera tiene 10 líneas fijas
        /// (fecha, $FieldDir, Elevation, $Offsets, 0,0, Convergence, 0,
        /// StartFix, lat,lon y ésta); las filas de datos vienen después.</summary>
        public const string ColumnHeader = "Latitude,Longitude,Elevation,Quality,Easting,Northing,Heading,Roll";

        /// <summary>
        /// Cabecera idéntica a la que escribe AOG (FileCreateElevation), con
        /// CRLF. StartFix: AOG ponía la posición del momento de crear el
        /// archivo; PilotX pasa el ORIGEN del lote (Field.txt), que es contra
        /// lo que están medidos Easting/Northing. Nadie la parsea para
        /// posicionar (las filas traen lat/lon propios), así que es compatible.
        /// </summary>
        public static string BuildHeader(DateTime timestamp, Wgs84 startFix)
        {
            var sb = new StringBuilder();
            sb.Append(timestamp.ToString("yyyy-MMMM-dd hh:mm:ss tt", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("$FieldDir\r\n");
            sb.Append("Elevation\r\n");
            sb.Append("$Offsets\r\n");
            sb.Append("0,0\r\n");
            sb.Append("Convergence\r\n");
            sb.Append("0\r\n");
            sb.Append("StartFix\r\n");
            sb.Append(startFix.Latitude.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(startFix.Longitude.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(ColumnHeader).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// Una fila de datos, SIN salto de línea. Mismas columnas y unidades que
        /// AOG (Elevation en m, Heading en radianes, Roll en grados) pero con
        /// formato fijo invariante ("F"): AOG usaba "N7"/"N2", que mete
        /// separador de miles ("1,234.56") y rompe el CSV con eastings ≥ 1 km.
        /// En PilotX "Elevation" es la altura del SUELO (antena corregida por
        /// rolido/cabeceo) y solo se graban fixes RTK fijo (Quality = 4).
        /// </summary>
        public static string FormatearFila(FilaElevacion f)
        {
            var c = CultureInfo.InvariantCulture;
            return f.Latitud.ToString("F7", c) + ","
                + f.Longitud.ToString("F7", c) + ","
                + Math.Round(f.AlturaSuelo, 3).ToString(c) + ","
                + f.Calidad.ToString(c) + ","
                + f.Easting.ToString("F2", c) + ","
                + f.Northing.ToString("F2", c) + ","
                + f.RumboRad.ToString("F3", c) + ","
                + Math.Round(f.RolidoGrados, 3).ToString(c);
        }

        /// <summary>
        /// Filas de datos de un Elevation.txt: líneas con ≥ 8 campos cuyo
        /// primero es un número. Ignora la cabecera (la de AOG o la nuestra).
        /// Archivo inexistente = 0.
        /// </summary>
        public static int ContarPuntos(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
            int n = 0;
            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (EsFilaDeDatos(line)) n++;
                }
            }
            return n;
        }

        public static bool EsFilaDeDatos(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            int comas = 0;
            int primera = -1;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ',')
                {
                    if (primera < 0) primera = i;
                    comas++;
                }
            }
            if (comas < 7 || primera <= 0) return false;
            return double.TryParse(line.Substring(0, primera), NumberStyles.Float,
                CultureInfo.InvariantCulture, out _);
        }

        /// <summary>
        /// Append elevation grid text to Elevation.txt.
        /// </summary>
        public static void Append(string fieldDirectory, string gridText)
        {
            if (string.IsNullOrEmpty(fieldDirectory))
            {
                throw new ArgumentNullException(nameof(fieldDirectory));
            }

            if (!Directory.Exists(fieldDirectory))
            {
                Directory.CreateDirectory(fieldDirectory);
            }

            var path = Path.Combine(fieldDirectory, FileName);
            using (var writer = new StreamWriter(path, true))
            {
                writer.Write(gridText);
            }
        }
        public sealed class ElevationData
        {
            public DateTime Created { get; set; }
            public Wgs84 StartFix { get; set; }
            public List<string> RawLines { get; set; } = new List<string>();
        }

        public static ElevationData Load(string fieldDirectory)
        {
            var path = Path.Combine(fieldDirectory, "Elevation.txt");
            if (!File.Exists(path)) return new ElevationData();

            var data = new ElevationData();
            using (var reader = new StreamReader(path))
            {
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    data.RawLines.Add(line);
                }
            }
            return data;
        }

    }
}

namespace AgOpenGPS.IO
{
    /// <summary>Una fila de Elevation.txt (columnas de AOG).</summary>
    public struct FilaElevacion
    {
        public double Latitud;
        public double Longitud;
        /// <summary>Altura del suelo (m).</summary>
        public double AlturaSuelo;
        public int Calidad;
        public double Easting;
        public double Northing;
        public double RumboRad;
        public double RolidoGrados;
    }
}
