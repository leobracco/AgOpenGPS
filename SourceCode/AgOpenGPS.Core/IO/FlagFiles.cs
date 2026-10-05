using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AgOpenGPS.IO
{
    public static class FlagsFiles
    {
        // ---- Tipo de bandera (punto de interes) -------------------------------
        //
        // Formato historico: lat,lon,easting,northing,heading,color,id,notas
        // (8 campos; los muy viejos, 6 sin heading ni notas). El tipo se agrega
        // como 9no campo OPCIONAL y con prefijo, "tipo=arbol", y SOLO si la
        // bandera tiene tipo:
        //   · una bandera comun escribe la linea byte a byte igual que antes;
        //   · un lector viejo (PilotX anterior, AOG) lee hasta words[7] y el
        //     campo extra le pasa de largo;
        //   · el prefijo evita confundir con el tipo una nota que tenga comas.
        private const string PrefijoTipo = "tipo=";

        private static string TipoAlFinal(string kind)
        {
            string k = LimpiarTipo(kind);
            return k.Length == 0 ? string.Empty : "," + PrefijoTipo + k;
        }

        /// <summary>Codigo de tipo apto para el CSV: minuscula, solo letras,
        /// digitos y guion bajo. Lo demas (comas, espacios) se descarta.</summary>
        private static string LimpiarTipo(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return string.Empty;
            var sb = new System.Text.StringBuilder(kind.Length);
            foreach (char c in kind.ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') sb.Append(c);
            return sb.ToString();
        }

        public static List<CFlag> DeduplicateFlags(IEnumerable<CFlag> flags)
        {
            var distinctFlags = new List<CFlag>();
            foreach (var f in flags)
            {
                bool duplicate = distinctFlags.Any(d =>
                    Math.Abs(d.latitude - f.latitude) < 1e-8 &&
                    Math.Abs(d.longitude - f.longitude) < 1e-8);

                if (!duplicate)
                {
                    distinctFlags.Add(f);
                }
            }
            return distinctFlags;
        }

        public static List<CFlag> Load(string fieldDirectory)
        {
            var result = new List<CFlag>();
            var path = Path.Combine(fieldDirectory, "Flags.txt");
            if (!File.Exists(path)) return result;

            using (var reader = new StreamReader(path))
            {
                reader.ReadLine(); // header
                var line = reader.ReadLine();
                int count;
                if (!int.TryParse(line, out count)) return result;

                for (int i = 0; i < count; i++)
                {
                    var words = (reader.ReadLine() ?? string.Empty).Split(',');
                    if (words.Length < 6) continue;

                    double lat = double.Parse(words[0], CultureInfo.InvariantCulture);
                    double lon = double.Parse(words[1], CultureInfo.InvariantCulture);
                    double easting = double.Parse(words[2], CultureInfo.InvariantCulture);
                    double northing = double.Parse(words[3], CultureInfo.InvariantCulture);
                    double heading = (words.Length >= 8)
                        ? double.Parse(words[4], CultureInfo.InvariantCulture)
                        : 0;
                    int color = int.Parse(words[words.Length >= 8 ? 5 : 4], CultureInfo.InvariantCulture);
                    int id = int.Parse(words[words.Length >= 8 ? 6 : 5], CultureInfo.InvariantCulture);
                    // Tipo de bandera (opcional, ver TipoAlFinal): si esta, es
                    // el ULTIMO campo y la nota es lo que queda entre medio
                    // (una nota con comas ya no se corta en la primera).
                    string kind = "";
                    int finNotas = words.Length;
                    if (words.Length >= 9 && words[words.Length - 1].StartsWith(PrefijoTipo, StringComparison.Ordinal))
                    {
                        kind = LimpiarTipo(words[words.Length - 1].Substring(PrefijoTipo.Length));
                        finNotas = words.Length - 1;
                    }
                    string notes = (words.Length >= 8
                        ? string.Join(",", words, 7, finNotas - 7)
                        : "").Trim();

                    // Use the same duplicate check as in Save
                    bool duplicate = result.Any(d =>
                        Math.Abs(d.latitude - lat) < 1e-8 &&
                        Math.Abs(d.longitude - lon) < 1e-8);

                    if (!duplicate)
                    {
                        result.Add(new CFlag(lat, lon, easting, northing, heading, color, id, notes) { kind = kind });
                    }
                }
            }

            return result;
        }

        public static void Save(string fieldDirectory, IReadOnlyList<CFlag> flags)
        {

            var filename = Path.Combine(fieldDirectory, "Flags.txt");

            // Prevent saving duplicates based on latitude and longitude
            var distinctFlags = new List<CFlag>();
            if (flags != null)
            {
                foreach (var f in flags ?? new List<CFlag>())
                {
                    bool duplicate = distinctFlags.Any(d =>
                        Math.Abs(d.latitude - f.latitude) < 1e-8 &&
                        Math.Abs(d.longitude - f.longitude) < 1e-8);

                    if (!duplicate)
                    {
                        distinctFlags.Add(f);
                    }
                }
            }
            using (var writer = new StreamWriter(filename, false))
            {
                writer.WriteLine("$Flags");
                writer.WriteLine(distinctFlags.Count.ToString(CultureInfo.InvariantCulture));

                for (int i = 0; i < distinctFlags.Count; i++)
                {
                    var f = distinctFlags[i];
                    writer.WriteLine(
                        f.latitude.ToString(CultureInfo.InvariantCulture) + "," +
                        f.longitude.ToString(CultureInfo.InvariantCulture) + "," +
                        f.easting.ToString(CultureInfo.InvariantCulture) + "," +
                        f.northing.ToString(CultureInfo.InvariantCulture) + "," +
                        f.heading.ToString(CultureInfo.InvariantCulture) + "," +
                        f.color.ToString(CultureInfo.InvariantCulture) + "," +
                        f.ID.ToString(CultureInfo.InvariantCulture) + "," +
                        (f.notes ?? string.Empty) +
                        TipoAlFinal(f.kind));
                }
            }
        }
    }
}
