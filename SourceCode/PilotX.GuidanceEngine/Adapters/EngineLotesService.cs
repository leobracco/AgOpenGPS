// ============================================================================
// EngineLotesService.cs — adaptador ILotesService sobre GuidanceEngineHost.
// Gemelo headless de FormGpsLotesService (GPS/AgroParallel/Common): mismo
// listado/apertura/cierre de lote, pero contra el host net9.0 en vez de
// FormGPS. Puerto directo de PilotX.Android/GuidanceEngineServices.cs
// (GuidanceEngineLotesService) — esa clase ya era 100% portable (sin ninguna
// API de Android), verificada en runtime real en el emulador; acá solo cambia
// el namespace/ubicación para que PilotX.Desktop la use vía EngineWebHost.
//
// Abrir/cerrar lote reusa GuidanceEngineHost.Job.cs (OpenField/CloseField),
// ya hecho y verificado desde el bloque 14. Crear/borrar/importar quedan en
// false (mismo comportamiento que el stub que reemplazan, no regresión):
// necesitan portar FileCreateField y el resto de SaveOpen.Designer.cs, que no
// es parte de este pedido (item 3 del PEDIDO taller).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineLotesService : ILotesService
    {
        private readonly GuidanceEngineHost _host;

        public EngineLotesService(GuidanceEngineHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public IList<FieldInfo> ListFields()
        {
            var result = new List<FieldInfo>();
            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            string current = null;
            try { current = _host.currentFieldDirectory; } catch { }

            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (!File.Exists(Path.Combine(di.FullName, "Field.txt"))) continue;

                    var info = new FieldInfo
                    {
                        Name = di.Name,
                        LastModifiedUtc = di.LastWriteTimeUtc,
                        IsCurrent = !string.IsNullOrEmpty(current) &&
                                    string.Equals(current, di.Name, StringComparison.OrdinalIgnoreCase),
                    };
                    string boundary = Path.Combine(di.FullName, "Boundary.txt");
                    if (File.Exists(boundary))
                    {
                        try
                        {
                            var lines = File.ReadAllLines(boundary);
                            info.HasBoundary = lines.Length > 2;
                            info.AreaHa = ComputeBoundaryAreaHa(lines);
                        }
                        catch { }
                    }
                    result.Add(info);
                }
                catch { /* skip broken dir */ }
            }
            result.Sort((a, b) =>
            {
                if (a.IsCurrent != b.IsCurrent) return a.IsCurrent ? -1 : 1;
                return b.LastModifiedUtc.CompareTo(a.LastModifiedUtc);
            });
            return result;
        }

        public string GetCurrentFieldName()
            => _host.IsJobStarted ? _host.currentFieldDirectory : null;

        public string GetCurrentFieldDirectory()
        {
            if (!_host.IsJobStarted) return null;
            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(_host.currentFieldDirectory)) return null;
            return Path.Combine(root, _host.currentFieldDirectory);
        }

        public Task<bool> OpenFieldAsync(string name)
            => Task.FromResult(_host.OpenField(name));

        public Task<bool> CloseFieldAsync()
        {
            _host.CloseField();
            return Task.FromResult(true);
        }

        // Crear lote nuevo headless: crea el directorio + Field.txt con el
        // origen = posición GPS actual, y lo abre. Mismo flujo que
        // FormGpsLotesService.CreateFieldAsync / FormGPS.FileCreateField, pero
        // sin WinForms (usa FieldPlaneFiles.Save + GuidanceEngineHost.OpenField).
        public Task<bool> CreateFieldAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(false);
            string clean = CleanName(name);
            if (string.IsNullOrEmpty(clean)) return Task.FromResult(false);

            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root)) return Task.FromResult(false);
            string dir = Path.Combine(root, clean);
            if (Directory.Exists(dir)) return Task.FromResult(false);

            try
            {
                // Cerrar el lote actual (si hay) antes de crear el nuevo.
                if (_host.IsJobStarted) _host.CloseField();

                Directory.CreateDirectory(dir);

                // Field.txt con el origen del plano local = lat/lon actual del GPS.
                var origin = _host.AppModelField.CurrentLatLon;
                AgOpenGPS.IO.FieldPlaneFiles.Save(dir, DateTime.Now, origin);

                // Abrir el lote recién creado (define plano local, IsJobStarted=true).
                return Task.FromResult(_host.OpenField(clean));
            }
            catch
            {
                // Limpieza best-effort si quedó a medio crear.
                try { if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0) Directory.Delete(dir); } catch { }
                return Task.FromResult(false);
            }
        }

        // Saca caracteres inválidos de nombre de carpeta (igual criterio que
        // FormGpsLotesService: no permitir path chars raros).
        private static string CleanName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Borra la carpeta del lote. Se niega a borrar el lote ABIERTO: hay que
        /// cerrarlo antes, si no se estaría borrando el piso mientras se trabaja.
        /// </summary>
        public Task<bool> DeleteFieldAsync(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(false);
                string root = RegistrySettings.fieldsDirectory;
                string dir = Path.Combine(root, name);
                if (!Directory.Exists(dir)) return Task.FromResult(false);
                if (_host.IsJobStarted &&
                    string.Equals(_host.currentFieldDirectory, name, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(false);
                Directory.Delete(dir, true);
                return Task.FromResult(true);
            }
            catch { return Task.FromResult(false); }
        }

        /// <summary>
        /// Lote nuevo a partir de otro: copia el ORIGEN del plano local (para que
        /// las coordenadas guardadas sigan siendo válidas) y, según lo pedido, el
        /// lindero, lo aplicado, las banderas, las guías y la cabecera.
        /// Port 1:1 de FormGPS.Lotes_CreateFromExisting — es I/O de archivos puro,
        /// no había nada de WinForms adentro.
        /// </summary>
        public Task<bool> CreateFromExistingAsync(string templateName, string newName,
                                                  bool copyApplied, bool copyFlags,
                                                  bool copyGuidance, bool copyHeadland)
        {
            if (string.IsNullOrWhiteSpace(templateName) || string.IsNullOrWhiteSpace(newName))
                return Task.FromResult(false);
            string clean = CleanName(newName);
            if (string.IsNullOrEmpty(clean)) return Task.FromResult(false);

            string root = RegistrySettings.fieldsDirectory;
            string templateDir = Path.Combine(root, templateName);
            string templateField = Path.Combine(templateDir, "Field.txt");
            if (!File.Exists(templateField)) return Task.FromResult(false);

            string newDir = Path.Combine(root, clean);
            if (Directory.Exists(newDir)) return Task.FromResult(false);

            string offsets, convergence, startFix;
            try
            {
                using (var reader = new StreamReader(templateField))
                {
                    reader.ReadLine(); reader.ReadLine(); reader.ReadLine(); reader.ReadLine();
                    offsets = reader.ReadLine();
                    reader.ReadLine();
                    convergence = reader.ReadLine();
                    reader.ReadLine();
                    startFix = reader.ReadLine();
                }
                if (offsets == null || convergence == null || startFix == null)
                    return Task.FromResult(false);
            }
            catch { return Task.FromResult(false); }

            try
            {
                if (_host.IsJobStarted) _host.CloseField();
                Directory.CreateDirectory(newDir);

                using (var writer = new StreamWriter(Path.Combine(newDir, "Field.txt")))
                {
                    writer.WriteLine(DateTime.Now.ToString("yyyy-MMMM-dd hh:mm:ss tt", CultureInfo.InvariantCulture));
                    writer.WriteLine("$FieldDir");
                    writer.WriteLine("FromExisting");
                    writer.WriteLine("$Offsets");
                    writer.WriteLine(offsets);
                    writer.WriteLine("$Convergence");
                    writer.WriteLine(convergence);
                    writer.WriteLine("StartFix");
                    writer.WriteLine(startFix);
                }

                void CopyIfExists(string file)
                {
                    string src = Path.Combine(templateDir, file);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(newDir, file));
                }

                if (copyApplied)
                {
                    CopyIfExists("Contour.txt");
                    CopyIfExists("Sections.txt");
                }
                else
                {
                    File.WriteAllText(Path.Combine(newDir, "Sections.txt"), "");
                    File.WriteAllLines(Path.Combine(newDir, "Contour.txt"), new[] { "$Contour" });
                }

                CopyIfExists("BackPic.txt");
                CopyIfExists("BackPic.png");
                CopyIfExists("Boundary.txt");
                CopyIfExists("Elevation.txt");

                if (File.Exists(Path.Combine(templateDir, "Headlines.txt"))) CopyIfExists("Headlines.txt");
                else File.WriteAllLines(Path.Combine(newDir, "Headlines.txt"), new[] { "$Headlines" });

                if (copyFlags) CopyIfExists("Flags.txt");
                else File.WriteAllLines(Path.Combine(newDir, "Flags.txt"), new[] { "$Flags", "0" });

                if (copyGuidance)
                {
                    CopyIfExists("ABLines.txt");
                    CopyIfExists("RecPath.txt");
                    CopyIfExists("CurveLines.txt");
                    CopyIfExists("Tram.txt");
                    CopyIfExists("TrackLines.txt");
                }
                else
                {
                    File.WriteAllLines(Path.Combine(newDir, "RecPath.txt"), new[] { "$RecPath", "0" });
                }

                if (copyHeadland) CopyIfExists("Headland.txt");

                return Task.FromResult(_host.OpenField(clean));
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        // Import KML / ISO-XML: en el nativo abren un diálogo de archivo de
        // WinForms (FormFieldKML / FormFieldISOXML). Headless no hay diálogo, y
        // la API todavía no recibe la ruta del archivo, así que se devuelve
        // false a propósito en vez de fingir que importó.
        public Task<bool> ImportKmlAsync() => Task.FromResult(false);
        public Task<bool> ImportIsoXmlAsync() => Task.FromResult(false);

        // Shoelace sobre Boundary.txt — copiado de FormGpsLotesService (mismo
        // parseo tolerante a formatos viejos: hasta 2 líneas True/False antes
        // del count).
        private static double ComputeBoundaryAreaHa(string[] lines)
        {
            try
            {
                int i = 1; // saltear header "$Boundary"
                while (i < lines.Length && (lines[i] == "True" || lines[i] == "False")) i++;
                if (i >= lines.Length) return 0;
                int numPoints = int.Parse(lines[i], CultureInfo.InvariantCulture);
                if (numPoints < 6 || i + numPoints >= lines.Length) return 0;

                double area = 0;
                double prevE = 0, prevN = 0, firstE = 0, firstN = 0;
                for (int p = 0; p < numPoints; p++)
                {
                    string[] words = lines[i + 1 + p].Split(',');
                    double e = double.Parse(words[0], CultureInfo.InvariantCulture);
                    double n = double.Parse(words[1], CultureInfo.InvariantCulture);
                    if (p == 0) { firstE = e; firstN = n; }
                    else area += (prevE + e) * (prevN - n);
                    prevE = e; prevN = n;
                }
                area += (prevE + firstE) * (prevN - firstN);
                return Math.Round(Math.Abs(area / 2) * 0.0001, 1);
            }
            catch { return 0; }
        }
    }
}
